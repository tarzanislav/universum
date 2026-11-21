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

// BatchPageViewModel: the selection, Spustiť, the check of the library, pause, stop, Upratať, the report
public sealed partial class BatchPageViewModel
{
    private IReadOnlyList<GroupRow> _selectedSeriesGroups = [];

    /// <summary>
    /// The series groups marked in the list (several with Ctrl or Shift; owner, 7. 11. 2026): episodes in folders not named
    /// after their series get the series – its name, TMDB or TMDB ID – all at once.
    /// </summary>
    public void SelectSeriesGroups(IEnumerable<GroupRow> groups)
    {
        _selectedSeriesGroups = groups.Where(SeriesGroups.Contains).ToList();
        OnPropertyChanged(nameof(SeriesTargetNote));
    }

    // the files the row Seriál: names in a sorted folder: of all the series groups marked (when the group shown is one of
    // them), otherwise of the group shown
    private IReadOnlyList<string> SeriesFiles =>
        _selectedSeriesGroups.Count > 1 && SelectedGroup is { } shown && _selectedSeriesGroups.Contains(LeadOf(shown))
            ? _selectedSeriesGroups.SelectMany(g => g.Variants).SelectMany(v => v.Group.Files).Select(f => f.Path).ToList()
            : SelectedFiles;

    private int SeriesGroupsNamed => _selectedSeriesGroups.Count > 1 && SelectedGroup is { } shown && _selectedSeriesGroups.Contains(LeadOf(shown)) ? _selectedSeriesGroups.Count : 1;

    /// <summary>"pre 5 označených skupín" when the row Seriál: names several groups at once.</summary>
    public string SeriesTargetNote => _library is { Mixed: true } && SeriesGroupsNamed > 1
        ? Loc.F("→ pre {0} označených skupín", SeriesGroupsNamed)
        : "";

    private IReadOnlyList<FileRow> _selectedFiles = [];

    /// <summary>The files selected in the list (several with Ctrl or Shift).</summary>
    public void SelectFiles(IEnumerable<FileRow> files)
    {
        _selectedFiles = files.ToList();
        DetectAudioSelectedCommand.NotifyCanExecuteChanged();
        UndoMovedCommand.NotifyCanExecuteChanged();
        AddChaptersSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoMovedText));
        OnFilmChanged();
    }

    // the row Film: follows the group and the files selected in the list
    private void OnFilmChanged()
    {
        OnPropertyChanged(nameof(FilmTitle));
        OnPropertyChanged(nameof(FilmSuggestion));
        OnPropertyChanged(nameof(CanNameFilm));
        OnPropertyChanged(nameof(FilmHint));
        FilmTmdb?.Refresh();
    }

    private async Task RunAsync()
    {
        if (_services.CurrentMkvToolNix is not { } installation || _analysis is null || _library is null)
        {
            return;
        }

        // owner, 30. 10. 2026: deleted automatically = each original to the Recycle Bin right after its verified result;
        // the choice is kept between starts, so each run with it is confirmed
        var originals = Originals;
        if (originals == OriginalsMode.DeleteAutomatically &&
            !Confirm(Loc.T("Spustiť so ZMAZANÍM ORIGINÁLOV?") + "\n\n" + Loc.F("Každý originál pôjde do Koša Windows hneď po zapísaní a overení nového súboru v knižnici " +
                     "({0}). Ak ich chcete ponechať, zvoľte Originál: zachovať.", Texts.Count(_items.Count(i => i.NeedsWork), Loc.T("súbor"), Loc.T("súbory"), Loc.T("súborov")))))
        {
            return;
        }

        // owner, 2. 11. 2026: Superrýchle – the originals of header-only files become the results (moved, not copied)
        if (originals == OriginalsMode.MoveIntoLibrary &&
            !Confirm(Loc.T("Spustiť SUPERRÝCHLO – originály sa presunú do knižnice?") + "\n\n" +
                     Loc.F("Súbor, ktorému sa mení len hlavička a leží na disku svojho výstupného priečinka, sa presunie do knižnice a upraví tam za pár sekúnd – " +
                           "originál sa stane výsledkom (nejde do Koša; Vrátiť originály ho dá späť, ako bol). Ostatné súbory sa zapíšu nanovo a originál pôjde do Koša ({0}).",
                         Texts.Count(_items.Count(i => i.NeedsWork), Loc.T("súbor"), Loc.T("súbory"), Loc.T("súborov"))) + "\n\n" +
                     Loc.T("Nepresunie sa súbor s ďalším menom (hard link), otvorený iným programom ani na inom disku. Súbory, ktoré ešte zdieľate (torrent), nechajte radšej na Zachovať.")))
        {
            return;
        }

        var output = originals.Output();
        RememberSeries(_items.Where(i => i.NeedsWork));

        Busy = true;
        _running = true;
        OnPropertyChanged(nameof(IsIdle));
        Finish = null;
        var started = System.Diagnostics.Stopwatch.StartNew();
        RefreshCommands();
        try
        {
            var processor = _services.Processor(installation, deleteOriginals: originals.RemovesWhileRunning());
            // every output folder the files go into (each file into the one on its own disk)
            foreach (var root in _items.Where(i => i.NeedsWork && i.Placement is not null).Select(i => LibraryLayout.RootOf(i.Placement!.Directory) ?? _library.Layout.Root)
                         .Append(_library.Layout.Root).Distinct(Paths.Comparer))
            {
                new LibraryLayout(root).EnsureCreated();
            }

            // owner, 29. 10. 2026: the run is a job of the job queue – seen in Fronta úloh and Výstup úloh
            var job = new Core.Jobs.BatchJob(
                Loc.F("Hromadné spracovanie: {0} → knižnica", Path.GetFileName(Path.TrimEndingDirectorySeparator(_analysis.Folder))),
                _items, processor, output,
                new Progress<BatchProgress>(p =>
                {
                    Progress = p.Total == 0 ? 0 : (p.Finished + (p.Result is null ? p.FileFraction : 0)) / p.Total;
                    RunState = $"{p.Finished} / {p.Total} · {Path.GetFileName(p.Item.File.Path)}";
                }));
            _job = job;
            _control = job.Control;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            job.Changed += j =>
            {
                if (j.IsFinished)
                {
                    finished.TrySetResult();
                }
            };
            _services.Jobs.Add(job, start: true);
            if (job.Status == Core.Jobs.JobStatus.Waiting && _services.Jobs.Running is { } other)
            {
                RunState = Loc.F("Čaká vo fronte úloh – najprv sa dokončí úloha {0} ({1}).", other.Id, other.Description);
            }

            await finished.Task;
            _results = job.Results;
            _written.AddRange(OriginalCleanup.Collect(_results));
            var recycled = _results.Count(r => r.Messages.Contains(FileProcessor.OriginalRecycledMessage));
            var stayed = _results.Count(r => r.Messages.Any(m => m.StartsWith(FileProcessor.OriginalKeptPrefix, StringComparison.Ordinal)));
            var movedIn = _results.Count(r => r.Messages.Contains(FileProcessor.MovedIntoLibraryMessage));
            var copiedWhole = _results.Count(r => r.Messages.Any(m => m.StartsWith(FileProcessor.NotMovedPrefix, StringComparison.Ordinal)));
            var cache = _services.Inspector(installation);
            foreach (var result in _results.Where(r => r.Status == ProcessingStatus.Done))
            {
                cache.Invalidate(result.SourcePath);
                if (result.OutputPath is not null)
                {
                    cache.Invalidate(result.OutputPath);
                }
            }

            TrySave(cache);
            var done = _results.Count(r => r.Status == ProcessingStatus.Done);
            var failed = _results.Count(r => r.Status == ProcessingStatus.Failed);
            var present = _results.Count(r => r.Status == ProcessingStatus.AlreadyInLibrary);

            // cancelled in the job queue (Zrušiť), or the run itself failed (its message is in Výstup úloh)
            var cancelled = job.Status == Core.Jobs.JobStatus.Cancelled;
            var broken = job.Status == Core.Jobs.JobStatus.Failed && _results.Count == 0;
            var stopped = cancelled || _control.StopAfterCurrent;
            RunState = broken ? Loc.F("Spracovanie sa nepodarilo spustiť – pozri Výstup úloh (úloha {0}).", job.Id)
                : Loc.F("Hotovo: {0} súborov v knižnici", done) + (present > 0 ? Loc.F(", {0} tam už bolo", present) : "") +
                  (failed > 0 ? Loc.F(", {0} s chybou (pozri správu)", failed) : "") +
                  (cancelled ? Loc.T(" – zrušené vo fronte úloh.") : stopped ? Loc.T(" – zastavené na požiadanie.") : ".") + Loc.T(" Pre ďalší beh dajte Analyzovať.");
            _ranSinceAnalysis = true;

            // a group processed is no longer new
            foreach (var row in Groups.Where(g => g.IsNew && g.Group.Files.Any(f => _results.Any(r => Paths.Same(r.SourcePath, f.Path)))))
            {
                row.IsNew = false;
            }

            var took = started.Elapsed;
            var time = took.TotalMinutes >= 1 ? $"{(int)took.TotalHours}:{took.Minutes:00}:{took.Seconds:00}" : $"{Math.Max(1, (int)took.TotalSeconds)} s";
            Finish = new BatchFinish(
                failed > 0 || broken ? BatchFinishKind.Errors : stopped ? BatchFinishKind.Stopped : BatchFinishKind.Done,
                (broken ? Loc.T("CHYBA") : failed > 0 ? Loc.T("HOTOVO S CHYBAMI") : cancelled ? Loc.T("ZRUŠENÉ") : stopped ? Loc.T("ZASTAVENÉ") : Loc.T("HOTOVO")) + Loc.F(" – spracovanie skončilo o {0:H:mm}", DateTime.Now),
                Loc.F("{0} v knižnici", Texts.Count(done, Loc.T("súbor"), Loc.T("súbory"), Loc.T("súborov"))) + (present > 0 ? Loc.F(" · {0} tam už bolo", present) : "") +
                (failed > 0 ? Loc.F(" · {0} s chybou – podrobnosti v Uložiť správu…", failed) : "") +
                (recycled > 0 ? Loc.F(" · {0} v Koši", Texts.Count(recycled, Loc.T("originál"), Loc.T("originály"), Loc.T("originálov"))) : "") +
                (stayed > 0 ? Loc.F(" · {0} originálov ostalo (Kôš nie je k dispozícii – pozri Výstup úloh)", stayed) : "") +
                (movedIn > 0 ? Loc.F(" · {0} presunutých do knižnice (superrýchle; Vrátiť originály ich dá späť)", movedIn) : "") +
                (copiedWhole > 0 ? Loc.F(" · {0} superrýchle nešlo – skopírované celé (dôvod vo Výstupe úloh)", copiedWhole) : "") +
                (originals == OriginalsMode.DeleteManually && PendingOriginals().Count > 0 ? Loc.T(" · originály zmažete tlačidlom Zmazať originály") : "") +
                Loc.F(" · trvanie {0} · úloha {1} vo Fronte úloh", time, job.Id));
        }
        finally
        {
            _running = false;
            Busy = false;
            OnPropertyChanged(nameof(PauseText));
            OnPropertyChanged(nameof(DeleteOriginalsText));
            OnPropertyChanged(nameof(UndoMovedText));
            OnPropertyChanged(nameof(IsIdle));
            RefreshCommands();
        }
    }

    private CancellationTokenSource? _fingerprintCancellation;
    private Dictionary<string, string> _fingerprints = new(Paths.Comparer);

    // the originals to be written whose place in the library holds files and whose fingerprint is not known yet: they
    // are read in the background whenever a file gets its place (after the analysis, a title, an episode …)
    private List<string> UncheckedOriginals() => _items
        .Where(i => i.NeedsWork && i.Placement is { } placement && !_fingerprints.ContainsKey(Path.GetFullPath(i.File.Path)) &&
                    Directory.Exists(placement.Directory))
        .Select(i => i.File.Path).Distinct(Paths.Comparer).ToList();

    // a result made from the same original into the same target is "už v knižnici" (the run would skip it anyway)
    private async Task CheckLibraryAsync()
    {
        // one check at a time; a new analysis cancels it (not disposed: it holds no timer)
        if (_fingerprintCancellation is not null)
        {
            return;
        }

        var analysis = _analysis;
        var paths = UncheckedOriginals();
        if (analysis is null || paths.Count == 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _fingerprintCancellation = cancellation;
        var found = new Dictionary<string, string>(Paths.Comparer);
        try
        {
            await Task.Run(async () =>
            {
                foreach (var path in paths)
                {
                    try
                    {
                        found[Path.GetFullPath(path)] = await LibraryIndex.FingerprintAsync(path, cancellation.Token);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // not readable now: not marked (the run decides), not read again and again
                        found[Path.GetFullPath(path)] = "";
                    }
                }
            }, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_fingerprintCancellation, cancellation))
            {
                _fingerprintCancellation = null;
            }
        }

        if (!ReferenceEquals(_analysis, analysis) || cancellation.IsCancellationRequested)
        {
            return;
        }

        foreach (var (path, fingerprint) in found)
        {
            _fingerprints[path] = fingerprint;
        }

        if (!Busy)
        {
            Replan();
            RefreshCommands();
        }
    }

    private void TogglePause()
    {
        if (_control is null)
        {
            return;
        }

        if (_control.IsPaused)
        {
            _control.Resume();
        }
        else
        {
            _control.Pause();
        }

        OnPropertyChanged(nameof(PauseText));
    }

    private void Stop()
    {
        _stoppedAfterAnalysis = true;
        // an analysis only stops reading (a finished run's control is not touched)
        if (_analysisCancellation is { } analysis)
        {
            analysis.Cancel();
            return;
        }

        _tmdbCancellation?.Cancel();
        _speechCancellation?.Cancel();
        _subtitleReading?.Cancel();
        if (_control is not null)
        {
            _control.StopAfterCurrent = true;
            _control.Resume();
            RunState = Loc.T("Zastavím po dokončení rozpracovaných súborov…");

            // a run still waiting in the job queue is taken out of it
            if (_job is { Status: Core.Jobs.JobStatus.Waiting } waiting)
            {
                _services.Jobs.Cancel(waiting);
            }
        }
    }

    private void Cleanup()
    {
        if (_analysis is null)
        {
            return;
        }

        var bin = new WindowsRecycleBin();
        foreach (var file in _leftovers.Where(File.Exists))
        {
            try
            {
                if (bin.CanRecycle(file))
                {
                    bin.MoveToRecycleBin(file);
                }
            }
            catch (IOException ex)
            {
                RunState = ex.Message;
            }
        }

        BackgroundWork.Run(AnalyzeAsync());
    }

    private void SaveReport()
    {
        var dialog = new SaveFileDialog { Filter = Loc.T("Stránka HTML (*.html)|*.html|Tabuľka CSV (*.csv)|*.csv"), FileName = "UNIVERSUM – správa.html" };
        if (dialog.ShowDialog() == true)
        {
            var content = dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? "﻿" + BatchReport.Csv(_items, _results)
                : BatchReport.Html("UNIVERSUM – hromadné spracovanie " + Folder, _items, _results);
            File.WriteAllText(dialog.FileName, content);
        }
    }

    private void ChooseOutputDirectory()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Výstupný priečinok (knižnica s MOVIES a TVSHOWS)") };
        if (dialog.ShowDialog() == true)
        {
            _services.SetOutputDirectory(dialog.FolderName);
        }
    }
}
