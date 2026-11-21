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

// BatchPageViewModel: Analyzovať – the folder read, the series sent from Novinky, the memory of series
public sealed partial class BatchPageViewModel
{
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Simulation { get => _simulation; private set => SetProperty(ref _simulation, value); }

    public string RunState { get => _runState; private set => SetProperty(ref _runState, value); }

    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    /// <summary>The folder is being read: groups appear as files are read, a progress bar shows how far.</summary>
    public bool IsAnalyzing => _analysisCancellation is not null;

    /// <summary>The speech of audio tracks is being recognised (owner, 1. 11. 2026: a gauge 0–100 % that moves).</summary>
    public bool IsListening => _speechCancellation is not null;

    /// <summary>The gauge under the summary: while the folder is read or the speech recognised.</summary>
    public bool ShowsProgress => IsAnalyzing || IsListening || IsAutoStep;

    /// <summary>What the stop button at the top stops.</summary>
    public string StopText => IsListening ? Loc.T("Zastaviť rozpoznávanie reči") : IsAutoStep && !IsAnalyzing ? Loc.T("Zastaviť") : Loc.T("Zastaviť analýzu");

    /// <summary>The summary says why nothing can be processed (no MKV, only results…): shown in the warning colour.</summary>
    public bool SummaryIsWarning { get => _summaryIsWarning; private set => SetProperty(ref _summaryIsWarning, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                RefreshCommands();
            }
        }
    }

    /// <summary>A run is in progress (updates wait for it).</summary>
    public bool IsRunning => _running;

    public string PauseText => _control?.IsPaused == true ? Loc.T("Pokračovať") : Loc.T("Pozastaviť");

    public async Task AnalyzeAsync()
    {
        // never while an analysis or a run is going on (a request from the Explorer, drag and drop)
        if (Folder is null || Busy)
        {
            return;
        }

        _stoppedAfterAnalysis = false;

        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Summary = Loc.T("MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.");
            return;
        }

        if (Folder is not { } folder)
        {
            return;
        }

        Busy = true;
        _results = [];
        Finish = null;

        // the subtitles of the previous analysis are no longer read: this one reads its own when it is done
        await StopReadingSubtitlesAsync();

        // nothing of a previous folder stays ready to run while (or if) this one is read
        _analysis = null;
        _items = [];
        _library = null;
        _fingerprintCancellation?.Cancel();
        _fingerprintCancellation = null;
        _fingerprints = new Dictionary<string, string>(Paths.Comparer);
        SlotRows.Clear();
        FileRows.Clear();

        // the groups grow while the folder is read (a group is final once the whole folder is read)
        var recursive = Recursive;
        var skipped = _services.Libraries?.ResultFolders;
        var libraryAtStart = _services.Library?.Root;
        using var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;
        var live = new Dictionary<string, GroupRow>(StringComparer.Ordinal);
        var readSubtitles = false;
        Groups.Clear();
        SelectedGroup = null;
        SummaryIsWarning = false;
        Summary = Loc.T("Hľadám video súbory…");
        Progress = 0;
        OnPropertyChanged(nameof(IsAnalyzing));
        RefreshCommands();
        try
        {
            var inspector = _services.Inspector(installation);
            var only = _only;
            // owner, 9. 11. 2026: the input folders of every disk at once
            var also = AllInputs ? (IReadOnlyList<string>)[.. _services.InputDirectories.Where(i => !Paths.Same(i, folder) && Directory.Exists(i))] : null;
            _analysis = await BatchAnalyzer.AnalyzeAsync(folder, recursive, inspector,
                new Progress<(int Done, int Total)>(p =>
                {
                    // reports still on their way after the end are dropped (they would overwrite the result)
                    if (ReferenceEquals(_analysisCancellation, cancellation))
                    {
                        Summary = Loc.F("Analyzujem {0} / {1}…", p.Done, p.Total);
                        Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                    }
                }),
                excludeDirectories: skipped,
                cancellationToken: cancellation.Token,
                found: new Progress<MediaFile>(file =>
                {
                    if (!ReferenceEquals(_analysisCancellation, cancellation))
                    {
                        return;
                    }

                    var signature = StructureSignature.Of(file);
                    if (!live.TryGetValue(signature, out var row))
                    {
                        live[signature] = row = new GroupRow(new BatchGroup(signature, live.Count)) { State = Loc.T("číta sa…") };
                        Groups.Add(row);
                    }

                    row.Group.Files.Add(file);
                    row.Title = GroupTitle(row.Group);
                    row.Refresh();
                }),
                only: only,
                alsoFolders: also);

            // nothing to process: why (no video, only in subfolders, only results…) – still stoppable
            string? why = null;
            if (_analysis.FileCount == 0 && _analysis.Failed.Count == 0)
            {
                Summary = Loc.T("Zisťujem, prečo sa nič nenašlo…");
                why = await Task.Run(() => string.Join(" ", _analysis.Folders.Select(f =>
                    (_analysis.Folders.Count > 1 ? f + ": " : "") + BatchAnalyzer.WhyEmpty(f, recursive, skipped, cancellation.Token))), cancellation.Token);
            }

            _analysisCancellation = null;
            OnPropertyChanged(nameof(IsAnalyzing));
            _library = _services.Library is { } layout ? LibraryJob.Guess(layout, _analysis, splitTitles: _only is null) : null;
            if (_library is not null && _services.Libraries is { } libraries)
            {
                // owner, 2. 11. 2026: each file into the output folder on its own disk
                _library.Libraries = libraries;
            }

            SortGroups();
            ApplySentSeries();
            var hint = ApplySeriesMemory();
            ApplyStudioEdits();
            ApplyEditedBatch();

            // owner, 29. 10. 2026: a folder sorted into MOVIES and TVSHOWS – each group holds one title
            if (_library is { Mixed: true } sorted)
            {
                _analysis = _analysis.SplitBy(sorted.UnitOf);
            }
            IReadOnlyList<string> inLibrary = _services.Libraries?.FindLeftovers() ?? Array.Empty<string>();
            _leftovers = [.. _analysis.LeftoverTemporaryFiles, .. inLibrary];
            _interrupted = _services.Libraries?.FindInterrupted() ?? Array.Empty<string>();
            _ranSinceAnalysis = false;
            _needsAnalysis = false;
            TrySave(inspector);
            Groups.Clear();
            foreach (var row in Arranged(folder, _analysis.Groups))
            {
                Groups.Add(row);
            }

            Summary = Loc.F("{0} súborov v {1} skupinách", _analysis.FileCount, _analysis.Groups.Count) +
                      (_analysis.Failed.Count > 0 ? Loc.F(", {0} sa nedalo prečítať ({1})", _analysis.Failed.Count, string.Join(", ", _analysis.Failed.Select(f => Path.GetFileName(f.Path)))) : "") +
                      (_leftovers.Count > 0 ? Loc.F(", {0} zvyšok po prerušenom spracovaní", _leftovers.Count) : "") +
                      (_interrupted.Count > 0
                          ? Loc.F(", POZOR: {0} nedokončený presun v knižnici (možno jediná kópia, nemažte): ", _interrupted.Count) +
                            string.Join("; ", _interrupted.Select(f => f + (FileProcessor.OriginalPlaceOf(AppSettings.JournalDirectory, f) is { } home ? Loc.T(" → patrí do ") + home : "")))
                          : "") + "." +
                      (_library is null ? Loc.T(" Na spracovanie nastavte výstupný priečinok (knižnicu).") : "") + hint;
            OnLibraryChanged();
            SelectedGroup = Groups.FirstOrDefault();

            // the reason first; the counts and the warnings about leftovers and interrupted moves stay
            if (why is not null)
            {
                Summary = why + " · " + Summary;
            }

            if (_editing is { } edited)
            {
                var own = edited.Files.Where(f => f.FromBatch).ToList();
                var missing = own.Count(f => !File.Exists(f.Path));
                Summary = Loc.F("Upravujete {0} MEGA spracovania ({1}{2}) – po úprave dajte Uložiť do {0}.", edited.Label,
                              Texts.Count(own.Count, Loc.T("súbor"), Loc.T("súbory"), Loc.T("súborov")), missing > 0 ? Loc.F(", {0} chýba", missing) : "") + " · " + Summary;
            }
            else if (only is not null)
            {
                Summary = Loc.F("Súbory poslané z Noviniek ({0})", only.Count) + " · " + Summary + Loc.T(" Celý priečinok spracujete po jeho výbere tlačidlom Priečinok….");
            }

            // owner, 29. 10. 2026: what is already in the library is known at the analysis (in the background)
            BackgroundWork.Run(CheckLibraryAsync());

            // owner, 30. 10. 2026: the language of every text subtitle track is read from its text (after the analysis,
            // the page free meanwhile; Spustiť waits for it, Zastaviť stops it)
            readSubtitles = true;

            // the output folder was changed while the folder was read: its MOVIES / TVSHOWS were not left out
            if (_services.Library?.Root != libraryAtStart)
            {
                _needsAnalysis = true;
                Summary += Loc.T(" Výstupný priečinok sa počas analýzy zmenil – analyzujte priečinok znova.");
                RefreshCommands();
            }

            SummaryIsWarning = _analysis.FileCount == 0 || _analysis.Failed.Count > 0 || _needsAnalysis;
        }
        catch (OperationCanceledException)
        {
            _analysis = null;
            _items = [];
            Groups.Clear();
            Summary = Loc.T("Analýza bola zastavená – dajte Analyzovať znova.");
            SummaryIsWarning = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _analysis = null;
            _items = [];
            _library = null;
            Groups.Clear();
            Summary = ex.Message;
            SummaryIsWarning = true;
        }
        finally
        {
            _analysisCancellation = null;
            Progress = 0;
            OnPropertyChanged(nameof(IsAnalyzing));
            Busy = false;
        }

        await RunAutoStepsAsync(readSubtitles, only: null);
    }

    // the steps after the analysis: the subtitles read, the names of films looked up, the speech heard – for every group,
    // or only for the groups added to the batch (Do Hromadného)
    private async Task RunAutoStepsAsync(bool readSubtitles, IReadOnlyList<BatchGroup>? only)
    {
        var onlyFiles = only?.SelectMany(g => g.Files).Select(f => f.Path).ToHashSet(Paths.Comparer);

        // owner, 7. 11. 2026: while the steps after the analysis run, a blinking line says so – nothing to do yet
        try
        {
            if (readSubtitles)
            {
                AutoStep = Loc.T("Čítame jazyk titulkov – počkajte, ešte nič nemeňte…");
                await ReadSubtitlesAsync(again: false, only);
            }

            // owner, 7. 11. 2026: the names of films are looked up on TMDB (and HISTRA) by themselves – no button; Zastaviť
            // stops the steps that follow too
            if (_analysis is not null && !_stoppedAfterAnalysis && FindMoviesCommand.CanExecute(null))
            {
                AutoStep = Loc.T("Hľadáme názvy filmov na TMDB – počkajte, ešte nič nemeňte…");
                await FindMoviesAsync(automatic: true, onlyFiles);
            }

            // owner, 7. 11. 2026: the audio tracks without a language are heard at once (the hand choice stays)
            if (_analysis is { Groups.Count: > 0 } && !_stoppedAfterAnalysis && DetectAudioAfterAnalysis && !Busy)
            {
                AutoStep = Loc.T("Analyzujeme jazykovo nenaznačené stopy – počkajte, ešte nič nemeňte…");
                await DetectAudioAsync(SpeechScope.All, automatic: true, only);
            }
        }
        finally
        {
            AutoStep = "";
        }
    }

    /// <summary>
    /// The step running by itself after Analyzovať (subtitles read, names looked up, speech heard) – shown blinking at the
    /// top so the user waits; empty when none runs.
    /// </summary>
    public string AutoStep
    {
        get => _autoStep;
        private set
        {
            if (SetProperty(ref _autoStep, value))
            {
                OnPropertyChanged(nameof(IsAutoStep));
                OnPropertyChanged(nameof(ShowsProgress));
                OnPropertyChanged(nameof(StopText));
            }
        }
    }

    private string _autoStep = "";

    public bool IsAutoStep => _autoStep.Length > 0;

    /// <summary>The files sent from Novinky – Seriály (only they are analysed); null for a whole folder.</summary>
    public IReadOnlyList<string>? ChosenFiles => _only;

    private IReadOnlyList<string>? _only;

    // the files of further series sent from Novinky together with the first (path → "Title (Year)")
    private Dictionary<string, string>? _sentSeries;

    /// <summary>
    /// Episodes of several series sent from Novinky – Seriály at once (owner, 29. 10. 2026): one batch, the first series
    /// in the field Seriál, each further series' files go to their own series (shown beside the field).
    /// </summary>
    public async Task AnalyzeSeriesAsync(IReadOnlyList<(string Title, IReadOnlyList<string> Files)> series)
    {
        if (Busy || series.Count == 0)
        {
            return;
        }

        var further = new Dictionary<string, string>(Paths.Comparer);
        foreach (var (title, files) in series.Skip(1))
        {
            foreach (var file in files.Where(f => !series[0].Files.Contains(f, Paths.Comparer)))
            {
                further[Path.GetFullPath(file)] = title;
            }
        }

        await AnalyzeFilesAsync([.. series.SelectMany(s => s.Files).Distinct(Paths.Comparer)], series[0].Title, further);
    }

    /// <summary>The further series of the batch beside the field Seriál ("Ďalšie seriály: Sľub (2025)"), or empty.</summary>
    // owner, 7. 11. 2026: only the number beside the field; the names in its tooltip
    public string OtherSeries => _library is { Kind: MediaKind.Series, SeriesOfFile.Count: > 0 } library
        ? library.SeriesOfFile.Values.Select(t => t.FolderName).Distinct(StringComparer.Ordinal).Count() is var count && count == 1
            ? Loc.T("+ 1 ďalší seriál") : count < 5 ? Loc.F("+ {0} ďalšie seriály", count) : Loc.F("+ {0} ďalších seriálov", count)
        : "";

    /// <summary>The further series of the batch by name (the tooltip of <see cref="OtherSeries"/>).</summary>
    public string OtherSeriesList => _library is { Kind: MediaKind.Series, SeriesOfFile.Count: > 0 } library
        ? Loc.T("Ďalšie seriály v tejto dávke – každá epizóda ide do priečinka svojho seriálu (stĺpec Cieľ v knižnici):") + Environment.NewLine +
          string.Join(Environment.NewLine, library.SeriesOfFile.Values.Select(t => t.FolderName).Distinct(StringComparer.Ordinal))
        : "";

    // the further series sent from Novinky, again after each analysis of the same files – keyed by the paths exactly as
    // the analysis has them (the planner looks them up so)
    private void ApplySentSeries()
    {
        if (_library is null || _analysis is null || _only is null || _sentSeries is null)
        {
            return;
        }

        foreach (var file in _analysis.Groups.SelectMany(g => g.Files))
        {
            if (_sentSeries.TryGetValue(Path.GetFullPath(file.Path), out var title) && (Remembered(title) ?? TitleGuess.Typed(MediaKind.Series, title)) is { } series)
            {
                _library.SeriesOfFile[file.Path] = series;
            }
        }
    }

    /// <summary>
    /// Episodes of one series sent from Novinky – Seriály: only these files are analysed (their common folder is the
    /// analysed one), the kind is series and the series' title is typed in (original title and year – confirmed on
    /// TMDB and HISTRA as any other title). <paramref name="further"/>: files of other series sent at the same time.
    /// </summary>
    /// <summary>Films and episodes chosen (new in the input folder): analysed as a folder of them; false while it is busy.</summary>
    public async Task<bool> AnalyzeChosenAsync(IReadOnlyList<string> files)
    {
        if (Busy || IsRunning || files.Count == 0)
        {
            return false;
        }

        Folder = CommonFolder(files);
        _only = files;
        _sentSeries = null;
        await AnalyzeAsync();
        return true;
    }

    /// <summary>
    /// Do Hromadného (owner, 21. 11. 2026): the new files of the input folders join the batch shown – only they are read
    /// (the tracks of the files already in the batch are not read again and nothing decided or suggested for them is lost);
    /// their groups come first, in the series as in the films, marked as new, and only for them the subtitles are read, the
    /// titles looked up and the speech heard. A batch that is not split into titles (one folder of one title, episodes sent
    /// from Novinky), a MEGA batch being corrected, files outside the batch's folders or no batch at all: the files are
    /// analysed on their own, as before. Null while the page is busy; false when the adding was stopped (nothing changed).
    /// </summary>
    public async Task<bool?> AddChosenAsync(IReadOnlyList<string> files)
    {
        if (Busy || IsRunning || IsAutoStep || IsReadingSubtitles)
        {
            return null;
        }

        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (files.Count == 0 || installation is null || _analysis is not { } analysis || _library is not { Mixed: true } library || _editing is not null ||
            _needsAnalysis || !files.All(f => analysis.Folders.Any(r => Paths.IsUnder(Path.GetFullPath(f), r))))
        {
            return await AnalyzeChosenAsync(files) ? true : null;
        }

        var known = analysis.Groups.SelectMany(g => g.Files).Select(f => Path.GetFullPath(f.Path)).ToHashSet(Paths.Comparer);
        var fresh = files.Select(Path.GetFullPath).Distinct(Paths.Comparer).Where(f => !known.Contains(f)).ToList();
        if (fresh.Count == 0)
        {
            RunState = Loc.T("Nové súbory už v Hromadnom spracovaní sú.");
            return true;
        }

        _stoppedAfterAnalysis = false;
        Busy = true;
        using var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;
        var summaryBefore = Summary;
        var warningBefore = SummaryIsWarning;
        Summary = Loc.F("Pridávam nové súbory {0} / {1}…", 0, fresh.Count);
        Progress = 0;
        OnPropertyChanged(nameof(IsAnalyzing));
        OnPropertyChanged(nameof(ShowsProgress));
        OnPropertyChanged(nameof(StopText));
        RefreshCommands();
        List<BatchGroup> added;
        try
        {
            var inspector = _services.Inspector(installation);
            var part = await BatchAnalyzer.AnalyzeAsync(analysis.Folder, recursive: true, inspector,
                progress: new Progress<(int Done, int Total)>(p =>
                {
                    if (ReferenceEquals(_analysisCancellation, cancellation))
                    {
                        Summary = Loc.F("Pridávam nové súbory {0} / {1}…", p.Done, p.Total);
                        Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                    }
                }),
                cancellationToken: cancellation.Token, only: fresh, alsoFolders: [.. analysis.Folders.Skip(1)]);

            // the new files guessed alone, each title on its own (as the files of the batch are), then joined: the groups of
            // the batch and everything decided for them stay as they are
            var guess = LibraryJob.Guess(library.Layout, part, alwaysSplit: true);
            if (guess.Mixed)
            {
                part = part.SplitBy(guess.UnitOf);
                library.Add(guess);
            }

            _analysis = analysis.Joined(part);
            added = [.. _analysis.Groups.Take(part.Groups.Count)];
            var hint = "";
            if (library.ApplyMemory(_services.SeriesMemory, [.. added.SelectMany(g => g.Files).Select(f => f.Path)]) is { Count: > 0 } given)
            {
                hint = Loc.F(" Napovedkyňa: {0} – titul, ktorý už poznáte, názov bez hľadania (zmeniť ho môžete).", string.Join(", ", given.Select(t => t.FolderName).Distinct(StringComparer.Ordinal)));
            }

            if (_only is not null)
            {
                // Analyzovať again reads the added files too
                _only = [.. _only, .. added.SelectMany(g => g.Files).Select(f => f.Path)];
            }

            TrySave(inspector);
            _analysisCancellation = null;
            OnPropertyChanged(nameof(IsAnalyzing));

            // the new groups first, marked; the place of each group is remembered for the next analysis
            for (var i = 0; i < added.Count; i++)
            {
                Groups.Insert(i, new GroupRow(added[i]) { Title = GroupTitle(added[i]), IsNew = true });
            }

            _groupRanks.Clear();
            for (var i = 0; i < Groups.Count; i++)
            {
                foreach (var file in Groups[i].Group.Files)
                {
                    _knownFiles.Add(file.Path);
                    _groupRanks[file.Path] = i;
                }
            }

            Summary = Loc.F("Pridané nové súbory: {0} v {1} – sú navrchu, farebne označené; čo ste už nastavili, ostalo.", part.FileCount,
                          Texts.Count(added.Count, Loc.T("skupine"), Loc.T("skupinách"), Loc.T("skupinách"))) +
                      (part.Failed.Count > 0 ? Loc.F(" {0} sa nedalo prečítať ({1}).", part.Failed.Count, string.Join(", ", part.Failed.Select(f => Path.GetFileName(f.Path)))) : "") +
                      " " + Loc.F("Spolu {0} súborov v {1} skupinách.", _analysis.FileCount, _analysis.Groups.Count) + hint;
            SummaryIsWarning = part.Failed.Count > 0;
            OnLibraryChanged();
            ShowGroup();
            SelectedGroup ??= Groups.FirstOrDefault();
            BackgroundWork.Run(CheckLibraryAsync());
        }
        catch (OperationCanceledException)
        {
            Summary = Loc.T("Pridávanie nových súborov bolo zastavené – v dávke sa nič nezmenilo.") + " · " + summaryBefore;
            SummaryIsWarning = warningBefore;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = Loc.T("Nové súbory sa nepodarilo pridať: ") + ex.Message + " · " + summaryBefore;
            SummaryIsWarning = true;
            return false;
        }
        finally
        {
            _analysisCancellation = null;
            Progress = 0;
            OnPropertyChanged(nameof(IsAnalyzing));
            OnPropertyChanged(nameof(ShowsProgress));
            OnPropertyChanged(nameof(StopText));
            Busy = false;
        }

        await RunAutoStepsAsync(readSubtitles: true, added);
        return true;
    }

    public async Task AnalyzeFilesAsync(IReadOnlyList<string> files, string seriesTitle, IReadOnlyDictionary<string, string>? further = null)
    {
        if (Busy || files.Count == 0)
        {
            return;
        }

        Folder = CommonFolder(files);
        _only = files;
        _sentSeries = further is { Count: > 0 } ? new Dictionary<string, string>(further, Paths.Comparer) : null;
        await AnalyzeAsync();
        if (_library is not null && _analysis is not null)
        {
            _library.Kind = MediaKind.Series;
            OnLibraryChanged();
            if (Remembered(seriesTitle) is { } remembered)
            {
                // napovedkyňa: the series processed before, with its title from TMDB and HISTRA – no new search
                _library.Series = remembered;
                _library.SeriesUnverified = false;
                OnPropertyChanged(nameof(SeriesTitle));
                OnLibraryChanged();
            }
            else
            {
                SeriesTitle = seriesTitle;
            }

            OnPropertyChanged(nameof(IsSeries));
            OnPropertyChanged(nameof(IsMovies));
        }
    }

    // napovedkyňa (owner, 30. 10. 2026): series processed before get their remembered titles at once
    private string ApplySeriesMemory()
    {
        if (_library is null || _analysis is null)
        {
            return "";
        }

        var given = _library.ApplyMemory(_services.SeriesMemory, _analysis.Groups.SelectMany(g => g.Files).Select(f => f.Path).ToList());
        return given.Count == 0 ? ""
            : Loc.F(" Napovedkyňa: {0} – titul, ktorý už poznáte, názov bez hľadania (zmeniť ho môžete).", string.Join(", ", given.Select(t => t.FolderName).Distinct(StringComparer.Ordinal)));
    }

    // the remembered series a title typed in Novinky names (original title and year), or null
    private TitleInfo? Remembered(string typed) =>
        TitleGuess.FromName(typed, MediaKind.Series) is { } name ? _services.SeriesMemory.Find([name]) : null;

    // napovedkyňa: the series of the files that go into the library (a run, a MEGA batch), under the names their files carry
    private void RememberSeries(IEnumerable<BatchItem> items)
    {
        if (_library is not { } library)
        {
            return;
        }

        var list = items.ToList();
        foreach (var series in list.Where(i => library.KindFor(i.File.Path) == MediaKind.Series && library.SeriesFor(i.File.Path) is not null)
                     .GroupBy(i => library.SeriesFor(i.File.Path)!))
        {
            _services.SeriesMemory.Remember(series.Key, series.SelectMany(i => library.SeriesNamesOf(i.File.Path)).Select(n => n.Name), DateTime.UtcNow);
        }

        // owner, 7. 11. 2026: films too, under the name their file carries – a TMDB ID typed once is not asked again
        foreach (var film in list.Where(i => library.KindFor(i.File.Path) == MediaKind.Movie && library.Movies.ContainsKey(i.File.Path)))
        {
            _services.SeriesMemory.Remember(library.Movies[film.File.Path], [library.NameGuesses.GetValueOrDefault(film.File.Path)?.Name], DateTime.UtcNow);
        }
    }

    // the deepest folder holding all the files
    private static string CommonFolder(IReadOnlyList<string> files)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(files[0]))!;
        while (files.Any(f => !Path.GetFullPath(f).StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) &&
               Path.GetDirectoryName(folder) is { } parent)
        {
            folder = parent;
        }

        return folder;
    }
}
