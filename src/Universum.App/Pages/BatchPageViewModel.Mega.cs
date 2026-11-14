using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Localization;
using Universum.App.Templates;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Speech;
using Universum.Core.Templates;
using Universum.Core.Text;
using Universum.Platform.Windows;

namespace Universum.App.Pages;

// BatchPageViewModel: MEGA – a batch sent, opened and corrected
public sealed partial class BatchPageViewModel
{
    private void SendToMega()
    {
        if (_analysis is null || _library is null)
        {
            return;
        }

        var ready = _items.Where(i => i.NeedsWork).ToList();
        var waiting = _items.Count(i => i.Status == BatchStatus.NeedsReview && !i.Reasons.Any(r => r.Kind == ReviewReasonKind.InMegaBatch));
        var profile = Template.Apply(_services.Profile);
        var template = Template.Slots.Count == 0 ? null : Template.ToJson();
        var target = _editing ?? SelectedMegaTarget?.Batch;
        var left = waiting > 0 ? "\n\n" + Loc.F("{0} na kontrolu – tie ostanú tu.", Texts.Count(waiting, Loc.T("súbor čaká"), Loc.T("súbory čakajú"), Loc.T("súborov čaká"))) : "";
        try
        {
            if (target is not null)
            {
                // owner, 29. 10. 2026: a batch completed with new files or corrected – the same library only
                if (!Paths.Same(Path.TrimEndingDirectorySeparator(target.LibraryRoot), Path.TrimEndingDirectorySeparator(_library.Layout.Root)))
                {
                    RunState = Loc.F("{0} zapisuje do inej knižnice ({1}) – pošlite súbory do novej dávky.", target.Label, target.LibraryRoot);
                    return;
                }

                if (target.State is not (Core.Mega.MegaBatchState.Waiting or Core.Mega.MegaBatchState.Missed))
                {
                    RunState = Loc.F("{0} už nečaká – pošlite súbory do novej dávky.", target.Label);
                    return;
                }

                // the batch deletes (or keeps) the originals as it was sent – the page has to say the same
                if (target.Originals != Originals)
                {
                    RunState = Loc.F("{0} má originály: {1}; tu je zvolené: {2}. " +
                               "Zvoľte rovnako, alebo pošlite súbory do novej dávky.", target.Label, OriginalsText(target.Originals), OriginalsText(Originals));
                    return;
                }

                if (!Confirm(Loc.F("Uložiť do {0} {1}?", target.Label, Texts.Count(ready.Count, Loc.T("pripravený súbor"), Loc.T("pripravené súbory"), Loc.T("pripravených súborov"))) + "\n\n" +
                             Loc.T("Súbory, ktoré dávka už má, dostanú nové rozhodnutia; ostatné sa pridajú.") +
                             "\n\n" + Loc.F("Originály: {0}.", OriginalsText(target.Originals)) + left))
                {
                    return;
                }

                var (added, changed) = _services.Mega.Put(target, ready, profile, template);
                StopEditing();
                RunState = $"{target.Label}: {Texts.Count(added, Loc.T("pridaný súbor"), Loc.T("pridané súbory"), Loc.T("pridaných súborov"))}, " +
                           $"{Texts.Count(changed, Loc.T("upravený"), Loc.T("upravené"), Loc.T("upravených"))}.";
            }
            else
            {
                var titles = ready.Select(i => i.Title?.Name).OfType<string>().Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
                var name = $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(_analysis.Folder))}: " +
                           string.Join(", ", titles.Take(3)) + (titles.Count > 3 ? Loc.F(" + {0} ďalšie", titles.Count - 3) : "");
                if (!Confirm(Loc.F("Odoslať do MEGA spracovania {0}?", Texts.Count(ready.Count, Loc.T("pripravený súbor"), Loc.T("pripravené súbory"), Loc.T("pripravených súborov"))) + "\n\n" +
                             Loc.T("Zapíšu sa neskôr – keď dávku spustíte alebo v nastavenom čase. Tu sa ukážu ako „v dávke“.") +
                             "\n\n" + Loc.F("Originály: {0} (dávka si to zapamätá).", OriginalsText(Originals)) + left))
                {
                    return;
                }

                // the batch takes over what happens with the originals (owner, 30. 10. 2026)
                if (_services.Mega.Add(name, _library.Layout.Root, profile, Originals, ready, template) is not { } batch)
                {
                    RunState = Loc.T("Všetky pripravené súbory už sú v niektorej dávke MEGA spracovania.");
                    return;
                }

                RunState = Loc.F("Odoslané do MEGA spracovania ako {0} ({1}).", batch.Label, Texts.Count(batch.Files.Count, Loc.T("súbor"), Loc.T("súbory"), Loc.T("súborov")));
            }

            RememberSeries(ready);
            _services.NotifyMegaChanged();
            Replan();
            RefreshCommands();
            MegaRequested?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RunState = Loc.T("Dávku sa nepodarilo uložiť: ") + ex.Message;
        }
    }

    internal static string OriginalsText(OriginalsMode mode) => mode switch
    {
        OriginalsMode.DeleteAutomatically => Loc.T("zmažú sa automaticky (do Koša hneď po overení výsledku)"),
        OriginalsMode.DeleteManually => Loc.T("zmažete ich ručne po dokončení dávky (do Koša)"),
        OriginalsMode.MoveIntoLibrary => Loc.T("superrýchle – súbor s inou len hlavičkou sa presunie do knižnice, ostatné originály do Koša"),
        _ => Loc.T("zachovajú sa"),
    };

    /// <summary>Where Odoslať do MEGA puts the prepared files: a new batch, or a waiting one to be completed.</summary>
    public System.Collections.ObjectModel.ObservableCollection<MegaTarget> MegaTargets { get; } = [];

    private MegaTarget? _megaTarget;

    public MegaTarget? SelectedMegaTarget { get => _megaTarget; set => SetProperty(ref _megaTarget, value); }

    /// <summary>A MEGA batch is open here to be corrected: the button saves it back.</summary>
    public bool IsEditingMega => _editing is not null;

    public string SendToMegaText => _editing is { } batch ? Loc.F("Uložiť do {0}", batch.Label) : Loc.T("Odoslať do MEGA");

    // the batch of MEGA spracovanie opened here to be corrected (it does not start meanwhile)
    private Core.Mega.MegaBatch? _editing;

    /// <summary>
    /// Opens a waiting MEGA batch to be corrected (owner, 29. 10. 2026): its files are analysed again with every decision
    /// stored with them – kinds, titles, episodes, languages, forced, kept and ordered tracks, Aj tak, the template.
    /// </summary>
    public async Task<bool> OpenMegaBatchAsync(Core.Mega.MegaBatch batch)
    {
        if (Busy || IsRunning || batch.State is not (Core.Mega.MegaBatchState.Waiting or Core.Mega.MegaBatchState.Missed) || batch.IsBeingEdited)
        {
            return false;
        }

        // only the files of Hromadné spracovanie: those of Single spracovanie and results of Zlučovanie stay in the batch as they are
        var files = batch.Files.Where(f => f.State == Core.Mega.MegaFileState.Waiting && f.FromBatch && File.Exists(f.Path)).Select(f => f.Path).ToList();
        if (files.Count == 0)
        {
            return false;
        }

        Folder = CommonFolder(files);
        _only = files;
        _sentSeries = null;
        _editing = batch;
        batch.IsBeingEdited = true;
        _services.NotifyMegaChanged();
        _template = batch.TemplateJson is { } json ? TryTemplate(json) : BatchTemplate.RulesOnly;
        OnPropertyChanged(nameof(Template));
        OnPropertyChanged(nameof(TemplateDescription));
        OnPropertyChanged(nameof(RemoveUnlisted));
        OnEditingChanged();
        await AnalyzeAsync();
        return true;
    }

    private static BatchTemplate TryTemplate(string json)
    {
        try
        {
            return BatchTemplate.FromJson(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return BatchTemplate.RulesOnly;
        }
    }

    // the decisions stored with the files of the batch being corrected, on the groups and the library of this analysis
    private void ApplyEditedBatch()
    {
        if (_editing is null || _library is null || _analysis is null)
        {
            return;
        }

        var stored = _editing.Files.Where(f => f.FromBatch).GroupBy(f => Path.GetFullPath(f.Path), Paths.Comparer).ToDictionary(g => g.Key, g => g.First(), Paths.Comparer);
        var kinds = new List<MediaKind>();
        foreach (var group in _analysis.Groups)
        {
            foreach (var media in group.Files)
            {
                if (!stored.TryGetValue(Path.GetFullPath(media.Path), out var file))
                {
                    continue;
                }

                var path = media.Path;
                // decisions by track ID only for the file's own tracks the studio does not decide (the IDs of external
                // files are given by each analysis; the studio's edits come back by path)
                var studied = file.Studio is { } studioOfFile
                    ? studioOfFile.Languages.Keys.Concat(studioOfFile.Forced.Keys).Concat(studioOfFile.SoundLanguages.Keys).ToHashSet()
                    : new HashSet<int>();
                var slots = SlotKey.Of(media).Where(x => x.Track.ExternalFile is null && !studied.Contains(x.Track.Id)).ToDictionary(x => x.Track.Id, x => x.Key);
                kinds.Add(file.Title.Kind);
                _library.KindOfFile[path] = file.Title.Kind;
                _library.ConfirmedKinds.Add(path);
                if (file.Title.Kind == MediaKind.Movie)
                {
                    _library.Movies[path] = file.Title;
                }
                else
                {
                    _library.SeriesOfFile[path] = file.Title;
                    _library.Series ??= file.Title;
                    if (file.Episode is { } episode)
                    {
                        _library.Episodes[path] = episode;
                    }
                }

                foreach (var (id, language) in file.Languages.Where(l => slots.ContainsKey(l.Key)))
                {
                    group.Decisions.SetFileLanguage(path, slots[id], language);
                }

                foreach (var (id, forced) in file.Forced.Where(f => slots.ContainsKey(f.Key)))
                {
                    group.Decisions.Forced[slots[id]] = forced;
                }

                // the group's names: of the file's own tracks by ID, of external files by their path (their IDs are the
                // analysis'); the ones the studio wrote come back with the studio's edits
                foreach (var (id, name) in file.Names)
                {
                    if (file.Studio?.Names.ContainsKey(id) == true || file.Studio?.SoundNames.ContainsKey(id) == true ||
                        file.Externals.Any(e => e.Id == id && e.Track?.Encode is not null) ||
                        (file.Externals.FirstOrDefault(e => e.Id == id) is { } external && file.Studio?.FileNames.Keys.Any(p => Paths.Same(p, external.Path)) == true))
                    {
                        continue;
                    }

                    var slot = SlotKey.Of(media).FirstOrDefault(x => file.Externals.FirstOrDefault(e => e.Id == id) is { } added
                        ? x.Track.ExternalFile is { } path && Paths.Same(path, added.Path)
                        : x.Track.ExternalFile is null && x.Track.Id == id);
                    if (slot.Track is not null)
                    {
                        group.Decisions.Names[slot.Key] = name;
                    }
                }

                group.Decisions.Kept.UnionWith(file.Kept.Where(slots.ContainsKey).Select(id => slots[id]));
                group.Decisions.ToSrt.UnionWith(file.ToSrt.Where(slots.ContainsKey).Select(id => slots[id]));
                if (file.Order.Count > 0 && group.Decisions.Order.Count == 0)
                {
                    // a sound written anew in the order stands for its original
                    var originals = file.Externals.Where(e => e.Track?.Encode is { } encode && Paths.Same(encode.Path, path)).ToDictionary(e => e.Id, e => e.Track!.Encode!.Source.Id);
                    group.Decisions.Order.AddRange(file.Order.Select(id => originals.GetValueOrDefault(id, id)).Where(slots.ContainsKey).Select(id => slots[id]));
                }

                if (file.ProcessAnyway)
                {
                    group.Decisions.ProcessAnyway.Add(path);
                }

                if (file.Addition is { Length: > 0 } addition)
                {
                    group.Decisions.Additions[path] = addition;
                    _additions[path] = addition;
                }

                if (file.Revision)
                {
                    group.Decisions.Revisions.Add(path);
                    _revisions.Add(path);
                }

                if (file.Studio is { } studio)
                {
                    group.Decisions.Studio[path] = studio;
                    _studioEdits[path] = studio;
                }
            }
        }

        if (kinds.Count > 0)
        {
            _library.Kind = kinds.GroupBy(k => k).OrderByDescending(g => g.Count()).First().Key;
        }
    }

    // the batch being corrected is let go (saved, another folder chosen, the queue cleared)
    private void StopEditing()
    {
        if (_editing is null)
        {
            return;
        }

        _editing.IsBeingEdited = false;
        _editing = null;
        OnEditingChanged();
        _services.NotifyMegaChanged();
    }

    private void OnEditingChanged()
    {
        OnPropertyChanged(nameof(IsEditingMega));
        OnPropertyChanged(nameof(SendToMegaText));
        RefreshMegaTargets();
    }

    private void RefreshMegaTargets()
    {
        var chosen = _megaTarget?.Batch;
        MegaTargets.Clear();
        MegaTargets.Add(new MegaTarget(Loc.T("do novej dávky"), null));
        foreach (var batch in _services.Mega.Batches.Where(b => b.State is Core.Mega.MegaBatchState.Waiting or Core.Mega.MegaBatchState.Missed && !b.IsBeingEdited))
        {
            MegaTargets.Add(new MegaTarget(Loc.F("doplniť {0}: {1}", batch.Label, batch.Name), batch));
        }

        SelectedMegaTarget = MegaTargets.FirstOrDefault(t => t.Batch == chosen) ?? MegaTargets[0];
    }
}
