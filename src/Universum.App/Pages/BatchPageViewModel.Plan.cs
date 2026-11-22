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

// BatchPageViewModel: the plan of the groups, the slots of the tracks, the group shown
public sealed partial class BatchPageViewModel
{
    /// <summary>Recomputes the simulation after any decision (fast: no file is read).</summary>
    public void Replan()
    {
        if (_analysis is null)
        {
            return;
        }

        BatchPlanner.Suggest(_analysis, _services.Profile);
        // owner, 29. 10. 2026: files of open MEGA batches are shown "v dávke N" and left to that batch
        _items = BatchPlanner.Plan(_analysis, Template, _services.Profile, _library,
            _services.Mega.Reserved(_editing).ToDictionary(r => r.Key, r => r.Value.Label, Core.IO.Paths.Comparer));
        if (_fingerprints.Count > 0)
        {
            _items = BatchPlanner.MarkAlreadyInLibrary(_items, _services.LibraryIndex, _fingerprints);
        }

        if (_fingerprintCancellation is null && !IsAnalyzing && UncheckedOriginals().Count > 0)
        {
            BackgroundWork.Run(CheckLibraryAsync());
        }
        foreach (var row in Groups)
        {
            var items = _items.Where(i => i.Group == row.Group).ToList();
            var review = items.Count(i => i.Status == BatchStatus.NeedsReview);
            row.State = review == 0 ? Loc.T("pripravené") : Loc.F("na kontrolu: {0}", review);
            row.Review = review;
            row.Title = GroupTitle(row.Group);
        }

        foreach (var (lead, title) in _titleOf)
        {
            if (title.HasVariants)
            {
                lead.Title = TitleOfFiles([.. title.Files]);
            }

            lead.RefreshTitle();
        }

        foreach (var slot in SlotRows)
        {
            slot.Refresh();
        }

        if (_changedAtTrack)
        {
            // the names and what is left out at once, the order after the pause (the track stays under the mouse)
            ArrangeSlots(move: false);
            _arrangeLater.Schedule(() => ArrangeSlots());
        }
        else
        {
            _arrangeLater.Cancel();
            ArrangeSlots();
        }

        var byItem = _items.ToDictionary(i => i.File.Path);
        foreach (var file in FileRows)
        {
            file.Update(byItem[file.Item.File.Path]);
        }

        var remux = _items.Count(i => i.Status == BatchStatus.Remux);
        var header = _items.Count(i => i.Status == BatchStatus.HeaderEdit);
        var place = _items.Count(i => i.Status == BatchStatus.Place);
        Simulation = (_library is null ? Loc.T("Bez výstupného priečinka – len simulácia pravidiel · ") : Loc.F("Do knižnice {0} súborov ({1} nový súbor, {2} len hlavička, {3} bez zmeny obsahu) · ", remux + header + place, remux, header, place)) +
                     Loc.F("už v knižnici {0} · na kontrolu {1}", _items.Count(i => i.Status == BatchStatus.Unchanged), _items.Count(i => i.Status == BatchStatus.NeedsReview));
        OnFilmChanged();
        OnPropertyChanged(nameof(DeleteOriginalsText));
        OnPropertyChanged(nameof(SeriesTitle));
        OnPropertyChanged(nameof(OtherSeries));
        OnPropertyChanged(nameof(OtherSeriesList));
        OnPropertyChanged(nameof(SeriesSuggestion));
        OnPropertyChanged(nameof(SeriesNeedsConfirmation));
        ConfirmSeriesCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(KindNote));
        OnPropertyChanged(nameof(KindNeedsConfirmation));
        OnPropertyChanged(nameof(HasSpecialsQuestions));
        OnPropertyChanged(nameof(IsMixed));
        OnPropertyChanged(nameof(CanChooseKind));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(IsMovies));
        OnPropertyChanged(nameof(IsSeries));
        OnPropertyChanged(nameof(CanFindTitles));
        OnPropertyChanged(nameof(KeepAllSubtitles));
        OnPropertyChanged(nameof(CanKeepAllSubtitles));
        OnPropertyChanged(nameof(ChaptersNote));
        RefreshCommands();
    }

    /// <summary>Subtitles out of the language list are shown (the check box „ponechať všetky titulky“ is offered).</summary>
    public bool CanKeepAllSubtitles => SlotRows.Any(s => s.CanKeep);

    /// <summary>
    /// „Ponechať všetky titulky“ (owner, 21. 11. 2026): every subtitle track out of the language list of the title (or the
    /// group) shown is kept; off = they are left out as before.
    /// </summary>
    public bool KeepAllSubtitles
    {
        get => SlotRows.Where(s => s.CanKeep).ToList() is { Count: > 0 } keepable && keepable.All(s => s.Kept);
        set
        {
            foreach (var slot in SlotRows.Where(s => s.CanKeep).ToList())
            {
                foreach (var (group, key) in slot.Places)
                {
                    if (value)
                    {
                        group.Decisions.Kept.Add(key);
                    }
                    else
                    {
                        group.Decisions.Kept.Remove(key);
                    }
                }
            }

            ReplanAtTrack();
        }
    }

    /// <summary>
    /// The titles of the group's files without year and ID (decided, suggested, or guessed from the names), the first
    /// with the number of the others: "Zlatý kapor", "Zlatý kapor + 1 ďalší", "+ 2 ďalšie", "+ 5 ďalších".
    /// </summary>
    public string GroupTitle(BatchGroup group) => TitleOfFiles(group.Files);

    // the title of files (a group, or all variants of a title – owner, 21. 11. 2026), with the disks they lie on
    private string TitleOfFiles(IReadOnlyList<MediaFile> files)
    {
        string? first = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var title = (_library?.TitleFor(file.Path) ?? _library?.SuggestedTitleFor(file.Path))?.CleanName ?? NameOf(file.Path);
            if (title.Length > 0 && seen.Add(Words.Normalize(title)))
            {
                first ??= title;
            }
        }

        var others = seen.Count - 1;
        var caption = first is null ? ""
            : others switch
            {
                0 => first,
                1 => Loc.F("{0} + 1 ďalší", first),
                < 5 => Loc.F("{0} + {1} ďalšie", first, others),
                _ => Loc.F("{0} + {1} ďalších", first, others),
            };

        // several disks analysed: the disk of the group too (owner, 9. 11. 2026); a title on two disks names both
        if (caption.Length > 0 && _analysis is { Folders.Count: > 1 } && files.Count > 0)
        {
            caption += " · " + string.Join(" + ", files.Select(f => Disk(f.Path)).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        return caption;
    }

    // the title read from a file's name, remembered (the analysis names its groups after every file it reads)
    private string NameOf(string path)
    {
        if (!_namesOfFiles.TryGetValue(path, out var name))
        {
            _namesOfFiles[path] = name = TitleGuess.ForFile(path).Title?.Name ?? Path.GetFileNameWithoutExtension(path);
        }

        return name;
    }

    /// <summary>
    /// Moves a track of the group one place up (-1) or down (+1) among the kept tracks of its type (owner, 29. 10. 2026);
    /// the order is the group's decision for all its files.
    /// </summary>
    public void MoveSlot(SlotRow row, int by)
    {
        var same = SlotRows.Where(s => s.Key.Type == row.Key.Type && !s.IsRemovedByLanguage).ToList();
        var at = same.IndexOf(row);
        if (at >= 0 && at + by >= 0 && at + by < same.Count)
        {
            MoveSlotTo(row, same[at + by]);
        }
    }

    /// <summary>Puts a dragged track at the place of another track of the same type.</summary>
    public void MoveSlotTo(SlotRow row, SlotRow target)
    {
        // the pictures keep their order (owner, 22. 11. 2026: they are only shown, and one can be left out)
        if (row == target || row.IsVideo || row.Key.Type != target.Key.Type || (!ShowsVariants && row.Group != target.Group) || row.IsRemovedByLanguage || target.IsRemovedByLanguage)
        {
            return;
        }

        var order = SlotRows.Where(s => !s.IsRemovedByLanguage).ToList();
        var down = order.IndexOf(row) < order.IndexOf(target);
        order.Remove(row);
        order.Insert(order.IndexOf(target) + (down ? 1 : 0), row);

        // owner, 21. 11. 2026: the order of the title's tracks – each variant gets it for the tracks it carries
        foreach (var group in order.SelectMany(s => s.Places.Select(p => p.Group)).Distinct())
        {
            group.Decisions.Order.Clear();
            group.Decisions.Order.AddRange(order.Select(s => s.Places.FirstOrDefault(p => p.Group == group)).Where(p => p.Group is not null).Select(p => p.Key));
        }

        Replan();
    }

    /// <summary>The group's tracks in the order of the result (of its first file), the left-out subtitles at the end.</summary>
    // a change at a track (its language, Forced, its name): planned again, the order arranged after the pause
    private void ReplanAtTrack()
    {
        _changedAtTrack = true;
        try
        {
            Replan();
        }
        finally
        {
            _changedAtTrack = false;
        }
    }

    // what the rules do with the tracks of one group (of its first file): the result's order, the tracks left out by their
    // language, the names the rules give
    private (List<SlotKey> Order, HashSet<SlotKey> Removed, Dictionary<SlotKey, string?> Names)? ResultOf(BatchGroup group)
    {
        if (_items.FirstOrDefault(i => i.Group == group && Paths.Same(i.File.Path, group.Representative.Path)) is not { } item)
        {
            return null;
        }

        var keyOf = SlotKey.WithVideos(item.File).ToDictionary(x => x.Track.Id, x => x.Key);
        // a sound of the file written anew (Zvuk v štúdiu) stands in its original's slot, a picture written anew too
        foreach (var anew in item.File.Tracks.Where(t => t.Encode is { } encode && Paths.Same(encode.Path, item.File.Path)))
        {
            if (keyOf.TryGetValue(anew.Encode!.Source.Id, out var original))
            {
                keyOf[anew.Id] = original;
            }
        }

        foreach (var anew in item.File.Tracks.Where(t => t.VideoEncode is { } encode && Paths.Same(encode.Path, item.File.Path)))
        {
            if (keyOf.TryGetValue(anew.VideoEncode!.Source.Id, out var original))
            {
                keyOf[anew.Id] = original;
            }
        }

        var removed = item.Target.RemovedByLanguage.Where(t => keyOf.ContainsKey(t.Id)).Select(t => keyOf[t.Id]).ToHashSet();
        var order = item.Target.Tracks.Where(t => keyOf.ContainsKey(t.TrackId)).Select(t => keyOf[t.TrackId]).ToList();
        // the names the rules give (no name written – by the group or in the studio): what a written name is compared with
        var rules = UniversumRules.Apply(item.File, _services.Profile, item.Decisions with { Names = new Dictionary<int, string>() });
        var names = rules.Tracks.Where(t => keyOf.ContainsKey(t.TrackId)).GroupBy(t => keyOf[t.TrackId]).ToDictionary(g => g.Key, g => (string?)g.Last().Name);
        return (order, removed, names);
    }

    private void ArrangeSlots(bool move = true)
    {
        if (SlotRows.Count == 0 || SlotRows[0].Group is not { } main)
        {
            return;
        }

        // each group the rows show (the main variant first; with „Všetky“ also the tracks only other variants carry)
        var results = SlotRows.Select(s => s.Group).Distinct().Select(g => (Group: g, Result: ResultOf(g)))
            .Where(x => x.Result is not null).ToDictionary(x => x.Group, x => x.Result!.Value);
        if (!results.TryGetValue(main, out var first))
        {
            return;
        }

        foreach (var slot in SlotRows)
        {
            if (results.TryGetValue(slot.Group, out var result))
            {
                slot.SetRemovedByLanguage(result.Removed.Contains(slot.Key));
                slot.SetResultName(result.Names.GetValueOrDefault(slot.Key));
            }
        }

        if (!move)
        {
            return;
        }

        int At(SlotRow s) => results.TryGetValue(s.Group, out var r) && r.Order.IndexOf(s.Key) is var at and >= 0 ? at : int.MaxValue;
        // the pictures first, in the order of the file (also one left out)
        var wanted = SlotRows.Where(s => s.Group == main).OrderBy(s => s.IsVideo ? -1 : first.Order.IndexOf(s.Key) is var at and >= 0 ? at : int.MaxValue)
            .ThenBy(s => s.Key.Type).ThenBy(s => s.Key.Ordinal).ToList();
        // the tracks only other variants carry: after the main variant's tracks of their type
        foreach (var extra in SlotRows.Where(s => s.Group != main).OrderBy(s => s.Group.Label, StringComparer.Ordinal).ThenBy(At).ThenBy(s => s.Key.Ordinal))
        {
            var last = wanted.FindLastIndex(w => w.Key.Type == extra.Key.Type);
            wanted.Insert(last < 0 ? wanted.Count : last + 1, extra);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var from = SlotRows.IndexOf(wanted[i]);
            if (from != i)
            {
                SlotRows.Move(from, i);
            }
        }
    }

    /// <summary>
    /// The tracks and the files of the selected title (owner, 21. 11. 2026): with „Všetky“ the tracks of all its variants
    /// together – each track once, with how many files carry it – and the files of all of them; a variant chosen above the
    /// files shows its own group as before.
    /// </summary>
    private void ShowGroup()
    {
        SlotRows.Clear();
        FileRows.Clear();
        VariantChips.Clear();
        ShowsVariants = false;

        // a group still being read has no decisions yet: it is shown once the whole folder is read
        if (SelectedGroup?.Group is not { } group || IsAnalyzing)
        {
            NotifyTitleShown();
            return;
        }

        var lead = LeadOf(SelectedGroup);
        var title = _titleOf.GetValueOrDefault(lead);
        ShowsVariants = _wholeTitle && title is { HasVariants: true };
        if (ShowsVariants)
        {
            var total = title!.FileCount;
            foreach (var slot in BatchTitles.Slots(title))
            {
                var places = slot.Places.Select(p => p.Group).ToList();
                var missing = title.Variants.Where(v => !places.Contains(v)).ToList();
                var note = missing.Count == 0 ? ""
                    : places.Count <= missing.Count ? Loc.F("len {0}", string.Join(", ", places.Select(g => g.Label)))
                    : Loc.F("chýba v {0}", string.Join(", ", missing.Select(g => g.Label)));
                SlotRows.Add(new SlotRow(slot.Group, slot.Key, slot.Track, ReplanAtTrack, MoveSlot, _services.Profile, slot.Peers,
                    $"{slot.FileCount}/{total}", note, missing.Count > 0));
            }
        }
        else
        {
            foreach (var (key, track) in SlotKey.WithVideos(group.Representative))
            {
                SlotRows.Add(new SlotRow(group, key, track, ReplanAtTrack, MoveSlot, _services.Profile));
            }
        }

        ArrangeSlots();

        IReadOnlyList<BatchGroup> shown = ShowsVariants ? title!.Variants : [group];
        foreach (var item in _items.Where(i => shown.Contains(i.Group)))
        {
            FileRows.Add(new FileRow(item, _ => Replan(), this));
        }

        if (title is { HasVariants: true })
        {
            AddVariantChips(lead, title);
        }

        NotifyTitleShown();
        RefreshCommands();
    }

    private void OnLibraryChanged()
    {
        OnPropertyChanged(nameof(IsMovies));
        OnPropertyChanged(nameof(IsSeries));
        OnPropertyChanged(nameof(CanFindTitles));
        Replan();
        BackgroundWork.Run(LoadSeriesSeasonsAsync());
    }
}
