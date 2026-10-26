using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Templates;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Templates;
using Universum.Core.Text;
using Universum.Platform.Windows;

namespace Universum.App.Pages;

/// <summary>
/// Batch processing of a folder (specification, sections 7 and 13): analysis, structure groups, decisions per group,
/// tolerant template, titles and episodes for the library, simulation, run with pause and stop, report.
/// </summary>
public sealed class BatchPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private BatchAnalysis? _analysis;
    private LibraryJob? _library;
    private IReadOnlyList<string> _leftovers = [];
    private IReadOnlyList<string> _interrupted = [];
    private bool _ranSinceAnalysis;
    private CancellationTokenSource? _tmdbCancellation;
    private IReadOnlyList<BatchItem> _items = [];
    private IReadOnlyList<ProcessingResult> _results = [];
    private GroupRow? _selectedGroup;
    private BatchTemplate _template = BatchTemplate.RulesOnly;
    private string _summary = "Vyberte priečinok so súbormi MKV a dajte Analyzovať.";
    private string _simulation = "";
    private string _runState = "";
    private double _progress;
    private bool _busy;
    private bool _running;
    private BatchControl? _control;
    private CancellationTokenSource? _cancellation;

    public BatchPageViewModel(AppServices services)
    {
        _services = services;
        ChooseFolderCommand = new RelayCommand(ChooseFolder, () => !Busy);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !Busy && Directory.Exists(Folder));
        ConfirmSuggestionsCommand = new RelayCommand(ConfirmSuggestions, () => !Busy && SlotRows.Any(s => s.HasSuggestion || s.ForcedSuggested));
        DetectSubtitlesCommand = new AsyncRelayCommand(DetectSubtitlesAsync, () => !Busy && SlotRows.Any(s => s.IsSubtitles && s.NeedsLanguage && s.Track.IsTextSubtitles));
        EditTemplateCommand = new RelayCommand(EditTemplate, () => !Busy);
        TemplateFromGroupCommand = new RelayCommand(TemplateFromGroup, () => !Busy && SelectedGroup is not null);
        LoadTemplateCommand = new RelayCommand(LoadTemplate, () => !Busy);
        SaveTemplateCommand = new RelayCommand(SaveTemplate, () => !Busy && Template.Slots.Count > 0);
        RulesOnlyCommand = new RelayCommand(() => Template = BatchTemplate.RulesOnly, () => !Busy);
        RunCommand = new AsyncRelayCommand(RunAsync, () => !Busy && !_ranSinceAnalysis && _items.Any(i => i.NeedsWork) && _services.Settings.OriginalHandling is not null);
        PauseCommand = new RelayCommand(TogglePause, () => _running);
        StopCommand = new RelayCommand(Stop, () => _running || _tmdbCancellation is not null);
        CleanupCommand = new RelayCommand(Cleanup, () => !Busy && _leftovers.Count > 0);
        SaveReportCommand = new RelayCommand(SaveReport, () => !Busy && _items.Count > 0);
        ChooseOutputDirectoryCommand = new RelayCommand(ChooseOutputDirectory, () => !Busy);
        ConfirmTitlesCommand = new RelayCommand(ConfirmTitles, () => !Busy && _library is not null && _items.Any(i => i.Reasons.Any(r => r.Kind is ReviewReasonKind.MissingTitle or ReviewReasonKind.MissingEpisode)));
        Tmdb = new TmdbPicker(services, () => MediaKind.Series, () => SeriesTitle.Length > 0 ? SeriesTitle : _library?.SeriesSuggestion?.FolderName ?? "", ChooseTmdbSeries);
        FindMoviesCommand = new AsyncRelayCommand(FindMoviesAsync, () => !Busy && _library is { Kind: MediaKind.Movie } && _services.Tmdb is not null);
        services.TmdbChanged += () =>
        {
            Tmdb.Refresh();
            FindMoviesCommand.NotifyCanExecuteChanged();
        };
        services.LibraryChanged += () =>
        {
            OnPropertyChanged(nameof(OutputDescription));
            if (_analysis is not null && _services.Library is { } layout)
            {
                _library = LibraryJob.Guess(layout, _analysis);
                OnLibraryChanged();
            }
        };
    }

    public RelayCommand ChooseOutputDirectoryCommand { get; }

    /// <summary>Accepts titles and episode numbers guessed from the names.</summary>
    public RelayCommand ConfirmTitlesCommand { get; }

    /// <summary>Looking up the series title on TMDB.</summary>
    public TmdbPicker Tmdb { get; }

    /// <summary>Refines the suggested film titles on TMDB (exact name and year only).</summary>
    public AsyncRelayCommand FindMoviesCommand { get; }

    public RelayCommand ChooseFolderCommand { get; }

    public AsyncRelayCommand AnalyzeCommand { get; }

    public RelayCommand ConfirmSuggestionsCommand { get; }

    public AsyncRelayCommand DetectSubtitlesCommand { get; }

    public RelayCommand EditTemplateCommand { get; }

    public RelayCommand TemplateFromGroupCommand { get; }

    public RelayCommand LoadTemplateCommand { get; }

    public RelayCommand SaveTemplateCommand { get; }

    public RelayCommand RulesOnlyCommand { get; }

    public AsyncRelayCommand RunCommand { get; }

    public RelayCommand PauseCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand CleanupCommand { get; }

    public RelayCommand SaveReportCommand { get; }

    public ObservableCollection<GroupRow> Groups { get; } = [];

    public ObservableCollection<SlotRow> SlotRows { get; } = [];

    public ObservableCollection<FileRow> FileRows { get; } = [];

    public IReadOnlyList<BatchItem> Items => _items;

    public IReadOnlyList<ProcessingResult> Results => _results;

    public BatchAnalysis? Analysis => _analysis;

    public string? Folder
    {
        get => _services.Settings.BatchFolder;
        set
        {
            _services.Settings.BatchFolder = value;
            _services.Settings.Save();
            OnPropertyChanged();
            RefreshCommands();
        }
    }

    public bool Recursive
    {
        get => _services.Settings.BatchRecursive;
        set
        {
            _services.Settings.BatchRecursive = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    public GroupRow? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                ShowGroup();
            }
        }
    }

    public BatchTemplate Template
    {
        get => _template;
        set
        {
            if (SetProperty(ref _template, value))
            {
                OnPropertyChanged(nameof(TemplateDescription));
                OnPropertyChanged(nameof(RemoveUnlisted));
                Replan();
            }
        }
    }

    public string TemplateDescription => Template.Slots.Count == 0
        ? "bez šablóny – len pravidlá UNIVERSUM"
        : $"{Template.Name}: " + string.Join(", ", Template.Slots.Select(s =>
            $"{Texts.Type(s.Type)} {s.Language}{(s.Role == TrackRole.Main ? "" : " " + Texts.Role(s.Role))}{(s.Required ? " (povinná)" : "")}"));

    public bool RemoveUnlisted
    {
        get => Template.Unlisted == UnlistedTracks.Remove;
        set => Template = Template with { Unlisted = value ? UnlistedTracks.Remove : UnlistedTracks.Keep };
    }

    public bool KeepOriginal
    {
        get => _services.Settings.OriginalHandling == OriginalHandling.Keep;
        set => SetHandling(value, OriginalHandling.Keep);
    }

    public bool DeleteOriginal
    {
        get => _services.Settings.OriginalHandling == OriginalHandling.Delete;
        set => SetHandling(value, OriginalHandling.Delete);
    }

    public string OutputDescription => _services.Library?.Root ?? "nie je nastavený – zvoľte ho tlačidlom Priečinok…";

    /// <summary>Naming decisions for the analysed folder; null without an output folder.</summary>
    public LibraryJob? Library => _library;

    public bool IsMovies
    {
        get => _library?.Kind != MediaKind.Series;
        set => SetKind(value, MediaKind.Movie);
    }

    public bool IsSeries
    {
        get => _library?.Kind == MediaKind.Series;
        set => SetKind(value, MediaKind.Series);
    }

    /// <summary>Title of the series as in the library ("Nine Puzzles (2025) [tmdbid-227191]"); for films each file has its own.</summary>
    public string SeriesTitle
    {
        get => _library?.Series?.FolderName ?? "";
        set
        {
            if (_library is null)
            {
                return;
            }

            _library.Series = TitleGuess.Typed(MediaKind.Series, value);
            Replan();
        }
    }

    public string SeriesSuggestion => _library is { Series: null, SeriesSuggestion: { } suggestion } ? "návrh: " + suggestion.FolderName : "";

    /// <summary>Sets the naming of one file: a film title ("Creep (2004)") or an episode ("S01E04"); also used by the self-test.</summary>
    public void SetNaming(string path, string text)
    {
        if (_library is null)
        {
            return;
        }

        if (_library.Kind == MediaKind.Series)
        {
            if (EpisodeNumber.TryParse(text) is { } number)
            {
                _library.Episodes[path] = number;
            }
            else
            {
                _library.Episodes.Remove(path);
            }
        }
        else if (TitleGuess.Typed(MediaKind.Movie, text) is { } title)
        {
            _library.Movies[path] = title;
        }
        else
        {
            _library.Movies.Remove(path);
        }

        Replan();
    }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Simulation { get => _simulation; private set => SetProperty(ref _simulation, value); }

    public string RunState { get => _runState; private set => SetProperty(ref _runState, value); }

    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

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

    public string PauseText => _control?.IsPaused == true ? "Pokračovať" : "Pozastaviť";

    public async Task AnalyzeAsync()
    {
        if (Folder is null)
        {
            return;
        }

        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Summary = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            return;
        }

        Busy = true;
        _results = [];
        try
        {
            var inspector = _services.Inspector(installation);
            _analysis = await BatchAnalyzer.AnalyzeAsync(Folder, Recursive, inspector,
                new Progress<(int Done, int Total)>(p => Summary = $"Analyzujem {p.Done} / {p.Total}…"),
                excludeDirectory: _services.Library?.Root);
            _library = _services.Library is { } layout ? LibraryJob.Guess(layout, _analysis) : null;
            IReadOnlyList<string> inLibrary = _services.Library?.FindLeftovers() ?? Array.Empty<string>();
            _leftovers = [.. _analysis.LeftoverTemporaryFiles, .. inLibrary];
            _interrupted = _services.Library?.FindInterrupted() ?? Array.Empty<string>();
            _ranSinceAnalysis = false;
            TrySave(inspector);
            Groups.Clear();
            foreach (var group in _analysis.Groups)
            {
                Groups.Add(new GroupRow(group));
            }

            Summary = $"{_analysis.FileCount} súborov v {_analysis.Groups.Count} skupinách" +
                      (_analysis.Failed.Count > 0 ? $", {_analysis.Failed.Count} sa nedalo prečítať ({string.Join(", ", _analysis.Failed.Select(f => Path.GetFileName(f.Path)))})" : "") +
                      (_leftovers.Count > 0 ? $", {_leftovers.Count} zvyšok po prerušenom spracovaní" : "") +
                      (_interrupted.Count > 0
                          ? $", POZOR: {_interrupted.Count} nedokončený presun v knižnici (možno jediná kópia, nemažte): " +
                            string.Join("; ", _interrupted.Select(f => f + (FileProcessor.OriginalPlaceOf(AppSettings.JournalDirectory, f) is { } home ? " → patrí do " + home : "")))
                          : "") + "." +
                      (_library is null ? " Na spracovanie nastavte výstupný priečinok (knižnicu)." : "");
            OnLibraryChanged();
            SelectedGroup = Groups.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Recomputes the simulation after any decision (fast: no file is read).</summary>
    public void Replan()
    {
        if (_analysis is null)
        {
            return;
        }

        BatchPlanner.Suggest(_analysis, _services.Profile);
        _items = BatchPlanner.Plan(_analysis, Template, _services.Profile, _library);
        foreach (var row in Groups)
        {
            var items = _items.Where(i => i.Group == row.Group).ToList();
            var review = items.Count(i => i.Status == BatchStatus.NeedsReview);
            row.State = review == 0 ? "pripravené" : $"na kontrolu: {review}";
        }

        foreach (var slot in SlotRows)
        {
            slot.Refresh();
        }

        var byItem = _items.ToDictionary(i => i.File.Path);
        foreach (var file in FileRows)
        {
            file.Update(byItem[file.Item.File.Path]);
        }

        var remux = _items.Count(i => i.Status == BatchStatus.Remux);
        var header = _items.Count(i => i.Status == BatchStatus.HeaderEdit);
        var place = _items.Count(i => i.Status == BatchStatus.Place);
        Simulation = (_library is null ? "Bez výstupného priečinka – len simulácia pravidiel · " : $"Do knižnice {remux + header + place} súborov ({remux} nový súbor, {header} len hlavička, {place} bez zmeny obsahu) · ") +
                     $"už v knižnici {_items.Count(i => i.Status == BatchStatus.Unchanged)} · na kontrolu {_items.Count(i => i.Status == BatchStatus.NeedsReview)}";
        OnPropertyChanged(nameof(SeriesTitle));
        OnPropertyChanged(nameof(SeriesSuggestion));
        RefreshCommands();
    }

    private void ShowGroup()
    {
        SlotRows.Clear();
        FileRows.Clear();
        if (SelectedGroup?.Group is not { } group)
        {
            return;
        }

        foreach (var (key, track) in SlotKey.Of(group.Representative))
        {
            SlotRows.Add(new SlotRow(group, key, track, Replan));
        }

        foreach (var item in _items.Where(i => i.Group == group))
        {
            FileRows.Add(new FileRow(item, _ => Replan(), this));
        }

        RefreshCommands();
    }

    private void OnLibraryChanged()
    {
        OnPropertyChanged(nameof(IsMovies));
        OnPropertyChanged(nameof(IsSeries));
        Replan();
    }

    private void SetKind(bool selected, MediaKind kind)
    {
        if (!selected || _library is null || _library.Kind == kind)
        {
            return;
        }

        _library.Kind = kind;
        OnLibraryChanged();
    }

    private void ChooseTmdbSeries(TmdbTitle title)
    {
        if (_library is null)
        {
            return;
        }

        var (text, note) = TmdbPicker.TitleText(title);
        _library.Kind = MediaKind.Series;
        _library.Series = TitleGuess.Typed(MediaKind.Series, text);
        RunState = note;
        OnLibraryChanged();
    }

    /// <summary>
    /// Refines the suggested film titles on TMDB, one request at a time (at most 4 per second, see RequestPacer); the
    /// same name and year is asked only once (cache). Stop cancels; titles found so far stay.
    /// </summary>
    private async Task FindMoviesAsync()
    {
        if (_library is null || _analysis is null || _services.Tmdb is not { } tmdb)
        {
            return;
        }

        var pending = _analysis.Groups.SelectMany(g => g.Files)
            .Where(f => !_library.Movies.ContainsKey(f.Path) && _library.MovieSuggestions.ContainsKey(f.Path))
            .Select(f => (f.Path, Guess: _library.MovieSuggestions[f.Path]))
            .ToList();
        var questions = pending.Select(p => (p.Guess.Name.ToLowerInvariant(), p.Guess.Year)).Distinct().Count();
        Busy = true;
        _tmdbCancellation = new CancellationTokenSource();
        StopCommand.NotifyCanExecuteChanged();
        void OnWaiting(TimeSpan delay) => RunState = $"TMDB žiada spomaliť – čakám {Math.Ceiling(delay.TotalSeconds):0} s a pokračujem…";
        tmdb.Waiting += OnWaiting;
        int asked = 0, found = 0;
        try
        {
            foreach (var (path, guess) in pending)
            {
                asked++;
                RunState = $"TMDB {asked} / {pending.Count} ({questions} rôznych názvov, najviac 4 otázky za sekundu)…";
                var results = await tmdb.SearchAsync(MediaKind.Movie, guess.Name, guess.Year, _tmdbCancellation.Token);
                if (TmdbClient.ExactMatch(results, guess.Name, guess.Year) is { } match && match.ToTitleInfo() is { } info)
                {
                    _library.MovieSuggestions[path] = info;
                    found++;
                }
            }

            RunState = $"TMDB spresnilo {found} z {pending.Count} navrhnutých názvov – skontrolujte ich a dajte Prijať návrhy názvov.";
        }
        catch (OperationCanceledException)
        {
            RunState = $"Zastavené – TMDB spresnilo {found} z {asked} doteraz prejdených názvov.";
        }
        catch (TmdbException ex)
        {
            RunState = ex.Message + $" (spresnené doteraz: {found})";
        }
        finally
        {
            tmdb.Waiting -= OnWaiting;
            TmdbPicker.TrySaveCache(tmdb);
            _tmdbCancellation.Dispose();
            _tmdbCancellation = null;
            Busy = false;
            Replan();
        }
    }

    private void ConfirmTitles()
    {
        _library?.ConfirmSuggestions();
        Replan();
    }

    private void ConfirmSuggestions()
    {
        foreach (var slot in SlotRows)
        {
            slot.ConfirmSuggestion();
        }

        Replan();
    }

    /// <summary>Recognises the language of undetermined text subtitles of the selected group (reads the representative file).</summary>
    private async Task DetectSubtitlesAsync()
    {
        if (SelectedGroup?.Group is not { } group || _services.CurrentMkvToolNix is not { } installation)
        {
            return;
        }

        Busy = true;
        try
        {
            foreach (var slot in SlotRows.Where(s => s.IsSubtitles && s.NeedsLanguage && s.Track.IsTextSubtitles).ToList())
            {
                RunState = $"Čítam titulky {slot.Slot}…";
                var text = await SubtitleText.ExtractAsync(installation, _services.Runner, group.Representative, slot.Track);
                if (text is not null && TextLanguageDetector.Detect(text) is { } code)
                {
                    group.Suggestions[slot.Key] = new LanguageEvidence(LanguageTag.FromCode(code), Confidence.Medium, EvidenceSource.SubtitleText);
                }
            }

            RunState = "";
            Replan();
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RunAsync()
    {
        if (_services.CurrentMkvToolNix is not { } installation || _services.Settings.OriginalHandling is not { } handling || _analysis is null || _library is null)
        {
            return;
        }

        Busy = true;
        _running = true;
        _control = new BatchControl();
        _cancellation = new CancellationTokenSource();
        RefreshCommands();
        try
        {
            var processor = _services.Processor(installation);
            _library.Layout.EnsureCreated();
            _results = await BatchRunner.RunAsync(
                _items, processor, new OutputOptions(handling),
                new Progress<BatchProgress>(p =>
                {
                    Progress = p.Total == 0 ? 0 : (p.Finished + (p.Result is null ? p.FileFraction : 0)) / p.Total;
                    RunState = $"{p.Finished} / {p.Total} · {Path.GetFileName(p.Item.File.Path)}";
                }),
                _control, _cancellation.Token);
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
            RunState = $"Hotovo: {done} súborov v knižnici" + (present > 0 ? $", {present} tam už bolo" : "") +
                       (failed > 0 ? $", {failed} s chybou (pozri správu)" : "") +
                       (_control.StopAfterCurrent ? " – zastavené na požiadanie." : ".") + " Pre ďalší beh dajte Analyzovať.";
            _ranSinceAnalysis = true;
        }
        finally
        {
            _running = false;
            Busy = false;
            OnPropertyChanged(nameof(PauseText));
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
        _tmdbCancellation?.Cancel();
        if (_control is not null)
        {
            _control.StopAfterCurrent = true;
            _control.Resume();
            RunState = "Zastavím po dokončení rozpracovaných súborov…";
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

        _ = AnalyzeAsync();
    }

    private void SaveReport()
    {
        var dialog = new SaveFileDialog { Filter = "Stránka HTML (*.html)|*.html|Tabuľka CSV (*.csv)|*.csv", FileName = "UNIVERSUM – správa.html" };
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
        var dialog = new OpenFolderDialog { Title = "Výstupný priečinok (knižnica s MOVIES a TVSHOWS)" };
        if (dialog.ShowDialog() == true)
        {
            _services.SetOutputDirectory(dialog.FolderName);
        }
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Priečinok so súbormi MKV" };
        if (dialog.ShowDialog() == true)
        {
            Folder = dialog.FolderName;
        }
    }

    private void EditTemplate()
    {
        var model = new TemplateEditorViewModel(Template.Slots.Count == 0 ? Template with { Name = "Moja šablóna" } : Template);
        if (new TemplateEditorWindow(model) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog() == true)
        {
            Template = model.ToTemplate();
        }
    }

    private void TemplateFromGroup()
    {
        var item = _items.FirstOrDefault(i => i.Group == SelectedGroup?.Group);
        if (item is not null)
        {
            Template = BatchTemplate.FromTarget($"Podľa skupiny {item.Group.Label}", item.Target);
        }
    }

    private void LoadTemplate()
    {
        Directory.CreateDirectory(AppSettings.TemplateDirectory);
        var dialog = new OpenFileDialog { Filter = "Šablóny UNIVERSUM (*.json)|*.json", InitialDirectory = AppSettings.TemplateDirectory };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                Template = BatchTemplate.FromJson(File.ReadAllText(dialog.FileName));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                RunState = "Šablónu sa nepodarilo načítať: " + ex.Message;
            }
        }
    }

    private void SaveTemplate()
    {
        Directory.CreateDirectory(AppSettings.TemplateDirectory);
        var dialog = new SaveFileDialog
        {
            Filter = "Šablóny UNIVERSUM (*.json)|*.json",
            InitialDirectory = AppSettings.TemplateDirectory,
            FileName = string.Concat(Template.Name.Split(Path.GetInvalidFileNameChars())) + ".json",
        };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, Template.ToJson());
        }
    }

    private void SetHandling(bool selected, OriginalHandling handling)
    {
        if (!selected)
        {
            return;
        }

        _services.Settings.OriginalHandling = handling;
        _services.Settings.Save();
        OnPropertyChanged(nameof(KeepOriginal));
        OnPropertyChanged(nameof(DeleteOriginal));
        RefreshCommands();
    }

    private static void TrySave(CachingInspector inspector)
    {
        try
        {
            inspector.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the cache is only an optimisation
        }
    }

    private void RefreshCommands()
    {
        foreach (var command in new IRelayCommand[]
                 {
                     ChooseFolderCommand, AnalyzeCommand, ConfirmSuggestionsCommand, DetectSubtitlesCommand, EditTemplateCommand,
                     TemplateFromGroupCommand, LoadTemplateCommand, SaveTemplateCommand, RulesOnlyCommand, RunCommand, PauseCommand,
                     StopCommand, CleanupCommand, SaveReportCommand, ChooseOutputDirectoryCommand, ConfirmTitlesCommand, FindMoviesCommand,
                 })
        {
            command.NotifyCanExecuteChanged();
        }
    }
}
