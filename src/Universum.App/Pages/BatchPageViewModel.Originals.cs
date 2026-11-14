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

// BatchPageViewModel: what happens with the originals – moved, deleted, put back
public sealed partial class BatchPageViewModel
{
    /// <summary>What happens with the originals (owner, 30. 10. 2026 – only in Hromadné spracovanie; MEGA batches take it over).</summary>
    public OriginalsMode Originals => _services.Settings.BatchOriginals;

    public bool KeepOriginal
    {
        get => Originals == OriginalsMode.Keep;
        set => SetOriginals(value, OriginalsMode.Keep);
    }

    /// <summary>Each original goes to the Recycle Bin right after its result is written and verified.</summary>
    public bool DeleteAutomatically
    {
        get => Originals == OriginalsMode.DeleteAutomatically;
        set => SetOriginals(value, OriginalsMode.DeleteAutomatically);
    }

    /// <summary>The originals stay during the run; Zmazať originály sends them to the Recycle Bin after it.</summary>
    public bool DeleteManually
    {
        get => Originals == OriginalsMode.DeleteManually;
        set => SetOriginals(value, OriginalsMode.DeleteManually);
    }

    /// <summary>
    /// Superrýchle (owner, 2. 11. 2026): a file whose header only changes is moved into the library and edited there; any
    /// other one is written anew and its original goes to the Recycle Bin.
    /// </summary>
    public bool MoveIntoLibrary
    {
        get => Originals == OriginalsMode.MoveIntoLibrary;
        set => SetOriginals(value, OriginalsMode.MoveIntoLibrary);
    }

    /// <summary>Vrátiť originály: the originals moved into the library by Superrýchle put back as they were (selected files, else all).</summary>
    public AsyncRelayCommand UndoMovedCommand { get; }

    public string UndoMovedText => MovedOriginals().Count is var count and > 0 ? Loc.F("Vrátiť originály ({0})", count) : Loc.T("Vrátiť originály");

    // the results of the last run whose original was moved into the library (of the selected files when some are moved)
    private List<ProcessingResult> MovedOriginals()
    {
        var moved = _results.Where(r => r.UndoRecordPath is not null && r.Messages.Contains(FileProcessor.MovedIntoLibraryMessage)).ToList();
        var selected = moved.Where(r => _selectedFiles.Any(f => Paths.Same(f.Item.File.Path, r.SourcePath))).ToList();
        return selected.Count > 0 ? selected : moved;
    }

    private async Task UndoMovedAsync()
    {
        var moved = MovedOriginals();
        if (moved.Count == 0 || _services.CurrentMkvToolNix is not { } installation ||
            !Confirm(Loc.F("Vrátiť {0} na pôvodné miesto?", Texts.Count(moved.Count, Loc.T("originál"), Loc.T("originály"), Loc.T("originálov"))) + "\n\n" +
                     Loc.T("Súbory presunuté do knižnice (Superrýchle) sa vrátia tam, odkiaľ prišli, s pôvodnou hlavičkou a časom zmeny; z knižnice zmiznú.")))
        {
            return;
        }

        Busy = true;
        RefreshCommands();
        try
        {
            var processor = _services.Processor(installation);
            var cache = _services.Inspector(installation);
            var problems = new List<string>();
            var back = new List<ProcessingResult>();
            foreach (var result in moved)
            {
                var failed = await processor.UndoAsync(result.UndoRecordPath!);
                cache.Invalidate(result.SourcePath);
                if (result.OutputPath is not null)
                {
                    cache.Invalidate(result.OutputPath);
                }

                if (failed.Count == 0)
                {
                    back.Add(result);
                }
                else
                {
                    problems.Add($"{Path.GetFileName(result.SourcePath)}: {string.Join("; ", failed)}");
                }
            }

            _results = _results.Except(back).ToList();
            RunState = Loc.F("Vrátené na pôvodné miesto: {0}.", back.Count) + (problems.Count > 0 ? Loc.T(" Nepodarilo sa: ") + string.Join(" | ", problems) : "") +
                       Loc.T(" Pre ďalší beh dajte Analyzovať.");
        }
        finally
        {
            Busy = false;
            RefreshCommands();
            OnPropertyChanged(nameof(UndoMovedText));
        }
    }

    /// <summary>Zmazať originály – enabled after a run (never during it).</summary>
    public AsyncRelayCommand DeleteOriginalsCommand { get; }

    public bool CanDeleteOriginals => Originals != OriginalsMode.Keep;

    /// <summary>The choice of the originals can change only between runs.</summary>
    public bool IsIdle => !_running;

    public string DeleteOriginalsText => PendingOriginals().Count is var count and > 0 ? Loc.F("Zmazať originály ({0})", count) : Loc.T("Zmazať originály");

    // the originals of results in the library: written by the runs of this page, and found already in the library at
    // the analysis – each one only while it is still in its place
    private readonly List<WrittenOriginal> _written = [];

    private List<WrittenOriginal> PendingOriginals()
    {
        // the newest entry of an original first (a later run of the same original made the result that counts)
        var pending = Enumerable.Reverse(_written).Where(o => File.Exists(o.Source)).ToList();
        foreach (var item in _items.Where(i => i.AlreadyInLibraryAt is not null))
        {
            if (_fingerprints.TryGetValue(Path.GetFullPath(item.File.Path), out var fingerprint) &&
                OriginalCleanup.FromFingerprint(item.File.Path, fingerprint, item.AlreadyInLibraryAt!) is { } found && File.Exists(found.Source))
            {
                pending.Add(found);
            }
        }

        return pending.DistinctBy(o => Path.GetFullPath(o.Source), Paths.Comparer).ToList();
    }

    private async Task DeleteOriginalsAsync()
    {
        var pending = PendingOriginals();
        var folders = pending.Select(o => Path.GetDirectoryName(o.Source) ?? "").Distinct(Paths.Comparer).ToList();
        if (pending.Count == 0 ||
            !Confirm(Loc.F("Zmazať {0}?", Texts.Count(pending.Count, Loc.T("originál"), Loc.T("originály"), Loc.T("originálov"))) + "\n\n" +
                     Loc.F("Z priečinkov: {0}{1}", string.Join(", ", folders.Take(3)), folders.Count > 3 ? Loc.F(" + {0} ďalšie", folders.Count - 3) : "") + "\n\n" +
                     Loc.T("Idú do Koša Windows (dajú sa z neho obnoviť). Zmaže sa len originál, ktorého výsledok je v knižnici a ktorý sa od spracovania nezmenil; " +
                     "súbory v knižnici (MOVIES, TVSHOWS) sa nikdy nemažú.")))
        {
            return;
        }

        Busy = true;
        RefreshCommands();
        try
        {
            var library = _services.Libraries;
            var index = _services.LibraryIndex;
            var (removed, kept) = await Task.Run(() => OriginalCleanup.RemoveAsync(pending, index, new WindowsRecycleBin(), path => library?.HoldsResult(path) == true));
            _written.RemoveAll(o => removed.Contains(o.Source, Paths.Comparer));
            RunState = Loc.F("Do Koša: {0}", Texts.Count(removed.Count, Loc.T("originál"), Loc.T("originály"), Loc.T("originálov"))) +
                       (kept.Count > 0 ? Loc.F(" · ostalo {0}: ", kept.Count) + string.Join("; ", kept.Take(5)) + (kept.Count > 5 ? " …" : "") : "") +
                       (removed.Count > 0 ? Loc.T(" · Pre ďalšiu prácu s priečinkom dajte Analyzovať.") : "");
            LastCleanup = (removed, kept);
            if (removed.Count > 0)
            {
                _needsAnalysis = true;
            }
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(DeleteOriginalsText));
            RefreshCommands();
        }
    }

    /// <summary>The originals removed and kept by the last Zmazať originály (the self-test reads it).</summary>
    public (IReadOnlyList<string> Removed, IReadOnlyList<string> Kept) LastCleanup { get; private set; } = ([], []);

    private void SetOriginals(bool selected, OriginalsMode mode)
    {
        if (!selected || _running)
        {
            return;
        }

        _services.Settings.BatchOriginals = mode;
        _services.Settings.Save();
        foreach (var name in new[] { nameof(Originals), nameof(KeepOriginal), nameof(DeleteAutomatically), nameof(DeleteManually), nameof(MoveIntoLibrary), nameof(CanDeleteOriginals), nameof(DeleteOriginalsText), nameof(HiddenPanelsNote) })
        {
            OnPropertyChanged(name);
        }

        RefreshCommands();
    }
}
