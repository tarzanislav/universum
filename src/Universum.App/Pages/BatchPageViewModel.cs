using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
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
    private CancellationTokenSource? _analysisCancellation;
    private CancellationTokenSource? _speechCancellation;
    private readonly Dictionary<string, string> _namesOfFiles = new(Universum.Core.IO.Paths.Comparer);
    private readonly Dictionary<string, IReadOnlyList<TmdbTitle>> _candidates = new(Universum.Core.IO.Paths.Comparer);
    private bool _summaryIsWarning;
    private IReadOnlyList<BatchItem> _items = [];
    private bool _needsAnalysis;
    private string? _typedTmdb;
    private IReadOnlyList<ProcessingResult> _results = [];
    private GroupRow? _selectedGroup;
    private FileRow? _selectedFile;
    private readonly Dictionary<string, string> _unverifiedNotes = new(Universum.Core.IO.Paths.Comparer);
    private string _seriesNote = "";
    private BatchTemplate _template = BatchTemplate.RulesOnly;
    private string _summary = "Vyberte priečinok s video súbormi (MKV, MP4, AVI, TS, M2TS, MOV, MPG, WebM…) a dajte Analyzovať – čo nie je MKV, uloží sa ako nový MKV.";
    private string _simulation = "";
    private string _runState = "";
    private double _progress;
    private bool _busy;
    private bool _running;
    private BatchControl? _control;
    private Core.Jobs.BatchJob? _job;

    public BatchPageViewModel(AppServices services)
    {
        _services = services;
        ChooseFolderCommand = new RelayCommand(ChooseFolder, () => !Busy);
        ClearCommand = new RelayCommand(Clear, () => !Busy && !IsRunning);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !Busy && Directory.Exists(Folder));
        ConfirmSuggestionsCommand = new RelayCommand(ConfirmSuggestions,
            () => !Busy && (SlotRows.Any(s => s.HasSuggestion || s.ForcedSuggested) || FileRows.Any(f => f.AudioSlots.Any(s => s.HasOwnSuggestion))));
        DetectSubtitlesCommand = new AsyncRelayCommand(DetectSubtitlesAsync, () => !Busy && SlotRows.Any(s => s.IsSubtitles && s.NeedsLanguage));
        DetectAudioCommand = new AsyncRelayCommand(() => DetectAudioAsync(SpeechScope.Group), () => !Busy && SelectedGroup is not null);
        DetectAudioSelectedCommand = new AsyncRelayCommand(() => DetectAudioAsync(SpeechScope.Selected), () => !Busy && _selectedFiles.Count > 0);
        DetectAudioAllCommand = new AsyncRelayCommand(() => DetectAudioAsync(SpeechScope.All), () => !Busy && _analysis is { Groups.Count: > 0 });
        EditTemplateCommand = new RelayCommand(EditTemplate, () => !Busy);
        TemplateFromGroupCommand = new RelayCommand(TemplateFromGroup, () => !Busy && SelectedGroup is not null);
        LoadTemplateCommand = new RelayCommand(LoadTemplate, () => !Busy);
        SaveTemplateCommand = new RelayCommand(SaveTemplate, () => !Busy && Template.Slots.Count > 0);
        RulesOnlyCommand = new RelayCommand(() => Template = BatchTemplate.RulesOnly, () => !Busy);
        RunCommand = new AsyncRelayCommand(RunAsync, () => !Busy && !_ranSinceAnalysis && !_needsAnalysis && _items.Any(i => i.NeedsWork) && _services.Settings.OriginalHandling is not null);
        PauseCommand = new RelayCommand(TogglePause, () => _running);
        StopCommand = new RelayCommand(Stop, () => _running || _tmdbCancellation is not null || _analysisCancellation is not null || _speechCancellation is not null);
        CleanupCommand = new RelayCommand(Cleanup, () => !Busy && _leftovers.Count > 0);
        SaveReportCommand = new RelayCommand(SaveReport, () => !Busy && _items.Count > 0);
        // a batch of MEGA spracovanie added, finished or removed: its files are free again or taken
        services.MegaChanged += () =>
        {
            if (!Busy && _analysis is not null)
            {
                Replan();
                RefreshCommands();
            }
        };
        SendToMegaCommand = new RelayCommand(SendToMega, () => !Busy && !_ranSinceAnalysis && !_needsAnalysis && _library is not null && _items.Any(i => i.NeedsWork) &&
                                                             _services.Settings.OriginalHandling is not null);
        ShowInfoCommand = new RelayCommand(() => InfoRequested?.Invoke(_selectedFile!.Item.File.Path), () => _selectedFile is not null);
        ChooseOutputDirectoryCommand = new RelayCommand(ChooseOutputDirectory, () => !Busy);
        ConfirmTitlesCommand = new RelayCommand(ConfirmTitles, () => !Busy && _library is not null &&
            (!_library.KindConfirmed || _items.Any(i => i.Reasons.Any(r => r.Kind is ReviewReasonKind.MissingTitle or ReviewReasonKind.MissingEpisode))));
        ConfirmKindCommand = new RelayCommand(ConfirmKind, () => !Busy && _library is { KindConfirmed: false });
        ConfirmSeriesCommand = new RelayCommand(() =>
        {
            if (_library is { Mixed: true } library)
            {
                foreach (var path in SelectedFiles.Where(library.SeriesSuggestionOfFile.ContainsKey))
                {
                    library.SeriesOfFile.TryAdd(path, library.SeriesSuggestionOfFile[path]);
                }
            }
            else
            {
                _library?.ConfirmSeries();
            }

            OnLibraryChanged();
        }, () => !Busy && SeriesNeedsConfirmation);
        AcceptGroupCommand = new RelayCommand(AcceptGroup, () => !Busy && !IsRunning && _library is not null && SelectedGroup is not null);
        AnswerSpecialsCommand = new RelayCommand(AnswerSpecials, () => !Busy && HasSpecialsQuestions);
        Tmdb = new TmdbPicker(services, () => MediaKind.Series,
            () => _typedTmdb ?? (SeriesTitle.Length > 0 ? SeriesTitle : SuggestedSeries?.FolderName ?? ""), ChooseTmdbSeries);
        FindMoviesCommand = new AsyncRelayCommand(FindMoviesAsync, () => !Busy && _library is { Kind: MediaKind.Movie } or { Mixed: true } && _services.Titles is not null);
        services.TitlesChanged += () =>
        {
            Tmdb.Refresh();
            FindMoviesCommand.NotifyCanExecuteChanged();
        };
        services.LibraryChanged += () =>
        {
            OnPropertyChanged(nameof(OutputDescription));
            if (_analysis is not null && _services.Library is { } layout)
            {
                // files of the analysis that now lie among the results (MOVIES, TVSHOWS) must be left out: analysed again first
                if (_analysis.Groups.SelectMany(g => g.Files).Any(f => layout.HoldsResult(f.Path)))
                {
                    _needsAnalysis = true;
                    Summary = "V MOVIES alebo TVSHOWS nového výstupného priečinka sú analyzované súbory – analyzujte priečinok znova (tie sa vynechajú).";
                    RunCommand.NotifyCanExecuteChanged();
                    return;
                }

                _library = LibraryJob.Guess(layout, _analysis);
                OnLibraryChanged();
            }
        };
    }

    public RelayCommand ChooseOutputDirectoryCommand { get; }

    /// <summary>Accepts the kind, titles and episode numbers guessed from the names.</summary>
    public RelayCommand ConfirmTitlesCommand { get; }

    /// <summary>Accepts the proposed kind (films or a series) as it is.</summary>
    public RelayCommand ConfirmKindCommand { get; }

    /// <summary>The answer to the questions Season 00 or a regular season: these files are specials (Season 00).</summary>
    public RelayCommand AnswerSpecialsCommand { get; }

    /// <summary>Some files ask Season 00 or a regular season (shown with the button).</summary>
    public bool HasSpecialsQuestions =>
        _library is { Kind: MediaKind.Series } library && library.OpenSeasonQuestions.Any(p => library.EpisodeSuggestions.TryGetValue(p, out var n) && n.Season == 0);

    /// <summary>"návrh – potvrďte" while the kind is only guessed from the names (specification 13.5).</summary>
    public string KindNote => _library is { Mixed: true } library
        ? SelectedFirst is not { } first ? ""
            : library.Unsorted.Contains(first) ? "⚠ súbor nie je v MOVIES ani v TVSHOWS – presuňte ho a dajte Analyzovať"
            : library.KindFor(first) == MediaKind.Series ? "(podľa priečinka TVSHOWS)" : "(podľa priečinka MOVIES)"
        : _library is { KindConfirmed: false } ? "(návrh podľa mien súborov – potvrďte)" : "";

    public bool KindNeedsConfirmation => _library is { KindConfirmed: false, Mixed: false };

    /// <summary>Looking up the series title on TMDB.</summary>
    public TmdbPicker Tmdb { get; }

    /// <summary>Refines the suggested film titles on TMDB (exact name and year only).</summary>
    public AsyncRelayCommand FindMoviesCommand { get; }

    public RelayCommand ChooseFolderCommand { get; }

    /// <summary>Vymazať frontu (owner, 29. 10. 2026): forgets the analysed files not processed yet – nothing on the disk changes.</summary>
    public RelayCommand ClearCommand { get; }

    /// <summary>Asks before the analysed files are forgotten (the self-test answers itself).</summary>
    public Func<string, bool> Confirm { get; set; } = question =>
        System.Windows.MessageBox.Show(question, "UNIVERSUM", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    private void Clear()
    {
        if (Busy || IsRunning || !Confirm("Vymazať frontu Hromadného spracovania?\n\nZabudnú sa analyzované súbory a rozhodnutia o nich (aj súbory poslané z Noviniek). Na disku sa nič nezmení."))
        {
            return;
        }

        _analysis = null;
        _items = [];
        _library = null;
        _only = null;
        _sentSeries = null;
        _results = [];
        Finish = null;
        SelectedGroup = null;
        SlotRows.Clear();
        FileRows.Clear();
        Groups.Clear();
        Simulation = "";
        SummaryIsWarning = false;
        Summary = "Fronta je prázdna – vyberte priečinok a dajte Analyzovať, alebo pošlite epizódy z Noviniek.";
        OnPropertyChanged(nameof(SeriesTitle));
        OnPropertyChanged(nameof(OtherSeries));
        OnPropertyChanged(nameof(SeriesSuggestion));
        OnPropertyChanged(nameof(IsSeries));
        OnPropertyChanged(nameof(IsMovies));
        OnPropertyChanged(nameof(ChosenFiles));
        RefreshCommands();
    }

    public AsyncRelayCommand AnalyzeCommand { get; }

    public RelayCommand ConfirmSuggestionsCommand { get; }

    public AsyncRelayCommand DetectSubtitlesCommand { get; }

    /// <summary>
    /// Recognises the spoken language of the selected group's audio tracks without a language – its films one by one, a
    /// series through one episode (a suggestion to confirm).
    /// </summary>
    public AsyncRelayCommand DetectAudioCommand { get; }

    /// <summary>The same for the files selected in the list, each on its own.</summary>
    public AsyncRelayCommand DetectAudioSelectedCommand { get; }

    /// <summary>The same for every group of the folder.</summary>
    public AsyncRelayCommand DetectAudioAllCommand { get; }

    public RelayCommand EditTemplateCommand { get; }

    public RelayCommand TemplateFromGroupCommand { get; }

    public RelayCommand LoadTemplateCommand { get; }

    public RelayCommand SaveTemplateCommand { get; }

    public RelayCommand RulesOnlyCommand { get; }

    public AsyncRelayCommand RunCommand { get; }

    /// <summary>
    /// Odoslať do MEGA (owner, 29. 10. 2026): the prepared files become a batch of MEGA spracovanie, written later; files
    /// waiting for a check stay here.
    /// </summary>
    public RelayCommand SendToMegaCommand { get; }

    /// <summary>Opens MEGA spracovanie after a batch was sent (the main window switches the page).</summary>
    public event Action? MegaRequested;

    public RelayCommand PauseCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand CleanupCommand { get; }

    public RelayCommand SaveReportCommand { get; }

    public ObservableCollection<GroupRow> Groups { get; } = [];

    public ObservableCollection<SlotRow> SlotRows { get; } = [];

    public ObservableCollection<FileRow> FileRows { get; } = [];

    /// <summary>The file selected in the group's file list.</summary>
    public FileRow? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                ShowInfoCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Opens the selected file on the Information page.</summary>
    public RelayCommand ShowInfoCommand { get; }

    /// <summary>The Information page should show this file.</summary>
    public event Action<string>? InfoRequested;

    /// <summary>The analysed files, by group (for the Information page).</summary>
    public IEnumerable<KnownFile> KnownFiles =>
        _items.Select(i => new KnownFile($"Hromadné spracovanie – skupina {i.Group.Label}", System.IO.Path.GetFileName(i.File.Path), i.File.Path));

    public IReadOnlyList<BatchItem> Items => _items;

    public IReadOnlyList<ProcessingResult> Results => _results;

    /// <summary>The end of the last run, shown boldly until the next analysis (owner, 29. 10. 2026); null while none.</summary>
    public BatchFinish? Finish
    {
        get => _finish;
        private set
        {
            if (SetProperty(ref _finish, value))
            {
                OnPropertyChanged(nameof(HasFinish));
            }
        }
    }

    public bool HasFinish => _finish is not null;

    private BatchFinish? _finish;

    public BatchAnalysis? Analysis => _analysis;

    public string? Folder
    {
        get => _services.Settings.BatchFolder;
        set
        {
            // a folder chosen again means the whole folder, not the files sent from Novinky
            _only = null;
            _sentSeries = null;
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
                if (IsMixed)
                {
                    foreach (var name in new[] { nameof(IsMovies), nameof(IsSeries), nameof(SeriesTitle), nameof(SeriesSuggestion), nameof(SeriesNeedsConfirmation), nameof(KindNote), nameof(CanFindTitles) })
                    {
                        OnPropertyChanged(name);
                    }

                    ConfirmSeriesCommand?.NotifyCanExecuteChanged();
                }

                AcceptGroupCommand?.NotifyCanExecuteChanged();
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
        get => _library is { Mixed: true } library ? SelectedFirst is not { } first || library.KindFor(first) != MediaKind.Series : _library?.Kind != MediaKind.Series;
        set => SetKind(value, MediaKind.Movie);
    }

    public bool IsSeries
    {
        get => _library is { Mixed: true } library ? SelectedFirst is { } first && library.KindFor(first) == MediaKind.Series : _library?.Kind == MediaKind.Series;
        set => SetKind(value, MediaKind.Series);
    }

    /// <summary>A folder sorted into MOVIES and TVSHOWS (owner, 29. 10. 2026): the kind comes from the folder, per group.</summary>
    public bool IsMixed => _library?.Mixed == true;

    /// <summary>The kind can be chosen (not in a sorted folder – there the user sorts the files into MOVIES or TVSHOWS).</summary>
    public bool CanChooseKind => !IsMixed;

    /// <summary>"Spresniť názvy": films, and in a sorted folder the series of TVSHOWS too.</summary>
    public bool CanFindTitles => IsMovies || IsMixed;

    /// <summary>"Obsah:" – of the selected group in a sorted folder.</summary>
    public string KindLabel => IsMixed ? "Obsah skupiny:" : "Obsah:";

    // the files of the selected group
    private IReadOnlyList<string> SelectedFiles => SelectedGroup?.Group.Files.Select(f => f.Path).ToList() ?? [];

    private string? SelectedFirst => SelectedGroup?.Group.Files.FirstOrDefault()?.Path;

    // the series suggested for the selected group (sorted folder) or for the folder
    private TitleInfo? SuggestedSeries => _library is { Mixed: true } library
        ? SelectedFirst is { } first ? library.SeriesSuggestionOfFile.GetValueOrDefault(first) : null
        : _library?.SeriesSuggestion;

    /// <summary>
    /// Title of the series as in the library ("Nine Puzzles (2025) [tmdbid-227191]"); for films each file has its own. In
    /// a sorted folder the series of the selected group.
    /// </summary>
    public string SeriesTitle
    {
        get => _library is { Mixed: true } library
            ? SelectedFirst is { } first ? library.SeriesOfFile.GetValueOrDefault(first)?.FolderName ?? "" : ""
            : _library?.Series?.FolderName ?? "";
        set
        {
            if (_library is null)
            {
                return;
            }

            if (_library.Mixed)
            {
                // a series' address or ID: its title from TMDB; a film's address does not belong among the series
                if (TitleGuess.ParseTmdb(value) is { } address)
                {
                    if (address.Kind == MediaKind.Movie)
                    {
                        RunState = "Toto je adresa filmu – v priečinku TVSHOWS má byť seriál; film presuňte do MOVIES.";
                        OnPropertyChanged();
                        return;
                    }

                    UseTmdbReference(address, value);
                    return;
                }

                _typedTmdb = null;
                var typed = TitleGuess.Typed(MediaKind.Series, value);
                foreach (var path in SelectedFiles)
                {
                    if (typed is null)
                    {
                        _library.SeriesOfFile.Remove(path);
                    }
                    else
                    {
                        _library.SeriesOfFile[path] = typed;
                    }
                }

                Replan();
                return;
            }

            // a TMDB address tells the kind itself: themoviedb.org/tv/… is a series, themoviedb.org/movie/… a film
            if (TitleGuess.ParseTmdb(value) is { Kind: not null } reference)
            {
                UseTmdbReference(reference, value);
                return;
            }

            _typedTmdb = null;
            _library.Series = TitleGuess.Typed(MediaKind.Series, value);
            Replan();
        }
    }

    public string SeriesSuggestion => _library is { Mixed: true } mixed
        ? SeriesTitle.Length == 0 && SuggestedSeries is { } groupSuggestion
            ? "návrh: " + groupSuggestion.FolderName + (groupSuggestion.Problem is { } problem ? $" ⚠ {problem} – nájdite ho cez TMDB alebo Spresniť názvy" : "")
            : ""
        : _library is { Series: null, SeriesSuggestion: { } suggestion } library
            ? "návrh: " + suggestion.FolderName + (library.SeriesUnverified ? " ⚠ " + _seriesNote : "")
            : "";

    /// <summary>The suggested series waits for its own confirmation (HISTRA did not confirm it; in a sorted folder any suggestion).</summary>
    public bool SeriesNeedsConfirmation => _library is { Mixed: true }
        ? SeriesTitle.Length == 0 && SuggestedSeries is { Problem: null }
        : _library is { Series: null, SeriesUnverified: true, SeriesSuggestion: not null };

    /// <summary>
    /// "Prijať všetko v skupine" (owner, 29. 10. 2026): the group's kind, title, episode numbers and track languages as
    /// suggested – except the questions Season 00 or a regular season and the doubtful language suggestions.
    /// </summary>
    public RelayCommand AcceptGroupCommand { get; }

    private void AcceptGroup()
    {
        if (_library is null || SelectedGroup is null)
        {
            return;
        }

        _library.AcceptAll(SelectedFiles);
        foreach (var path in SelectedFiles)
        {
            _unverifiedNotes.Remove(path);
        }

        // the languages of the group (Potvrdiť návrhy) – it plans again
        ConfirmSuggestions();
        OnPropertyChanged(nameof(SeriesTitle));
        OnPropertyChanged(nameof(SeriesSuggestion));
        RunState = $"Skupina {SelectedGroup.Label} ({SelectedGroup.Title}): návrhy prijaté." +
                   (_library.SeasonQuestions.Any(SelectedFiles.Contains) ? " Otázky Season 00 / bežná sezóna ostali na vás." : "");
    }

    /// <summary>Confirms the suggested series HISTRA did not confirm.</summary>
    public RelayCommand ConfirmSeriesCommand { get; }

    /// <summary>A film named by its TMDB ID or address typed in the field TMDB ID at the file (also used by the self-test).</summary>
    public void UseTmdbId(string path, string text)
    {
        if (_library is null || _library.KindFor(path) != MediaKind.Movie)
        {
            return;
        }

        if (TitleGuess.ParseTmdb(text) is { } reference)
        {
            _ = NameFilmFromTmdbAsync(path, reference);
        }
        else
        {
            RunState = "TMDB ID je číslo filmu (napr. 12345) alebo adresa z themoviedb.org – minisériál vložte ako adresu …/tv/….";
        }
    }

    /// <summary>Sets the naming of one file: a film title ("Creep (2004)") or an episode ("S01E04"); also used by the self-test.</summary>
    public void SetNaming(string path, string text)
    {
        if (_library is null)
        {
            return;
        }

        // a film's TMDB address or ID: its title from TMDB (the address says film or series)
        if (_library.KindFor(path) == MediaKind.Movie && TitleGuess.ParseTmdb(text) is { } reference)
        {
            _ = NameFilmFromTmdbAsync(path, reference);
            return;
        }

        if (_library.KindFor(path) == MediaKind.Series)
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

    /// <summary>The folder is being read: groups appear as files are read, a progress bar shows how far.</summary>
    public bool IsAnalyzing => _analysisCancellation is not null;

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

    public string PauseText => _control?.IsPaused == true ? "Pokračovať" : "Pozastaviť";

    public async Task AnalyzeAsync()
    {
        // never while an analysis or a run is going on (a request from the Explorer, drag and drop)
        if (Folder is null || Busy)
        {
            return;
        }

        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Summary = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            return;
        }

        if (Folder is not { } folder)
        {
            return;
        }

        Busy = true;
        _results = [];
        Finish = null;

        // nothing of a previous folder stays ready to run while (or if) this one is read
        _analysis = null;
        _items = [];
        _library = null;
        SlotRows.Clear();
        FileRows.Clear();

        // the groups grow while the folder is read (a group is final once the whole folder is read)
        var recursive = Recursive;
        var skipped = _services.Library?.ResultFolders;
        var libraryAtStart = _services.Library?.Root;
        using var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;
        var live = new Dictionary<string, GroupRow>(StringComparer.Ordinal);
        Groups.Clear();
        SelectedGroup = null;
        SummaryIsWarning = false;
        Summary = "Hľadám video súbory…";
        Progress = 0;
        OnPropertyChanged(nameof(IsAnalyzing));
        RefreshCommands();
        try
        {
            var inspector = _services.Inspector(installation);
            var only = _only;
            _analysis = await BatchAnalyzer.AnalyzeAsync(folder, recursive, inspector,
                new Progress<(int Done, int Total)>(p =>
                {
                    // reports still on their way after the end are dropped (they would overwrite the result)
                    if (ReferenceEquals(_analysisCancellation, cancellation))
                    {
                        Summary = $"Analyzujem {p.Done} / {p.Total}…";
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
                        live[signature] = row = new GroupRow(new BatchGroup(signature, live.Count)) { State = "číta sa…" };
                        Groups.Add(row);
                    }

                    row.Group.Files.Add(file);
                    row.Title = GroupTitle(row.Group);
                    row.Refresh();
                }),
                only: only);

            // nothing to process: why (no video, only in subfolders, only results…) – still stoppable
            string? why = null;
            if (_analysis.FileCount == 0 && _analysis.Failed.Count == 0)
            {
                Summary = "Zisťujem, prečo sa nič nenašlo…";
                why = await Task.Run(() => BatchAnalyzer.WhyEmpty(folder, recursive, skipped, cancellation.Token), cancellation.Token);
            }

            _analysisCancellation = null;
            OnPropertyChanged(nameof(IsAnalyzing));
            _library = _services.Library is { } layout ? LibraryJob.Guess(layout, _analysis, splitTitles: _only is null) : null;
            ApplySentSeries();

            // owner, 29. 10. 2026: a folder sorted into MOVIES and TVSHOWS – each group holds one title
            if (_library is { Mixed: true } sorted)
            {
                _analysis = _analysis.SplitBy(sorted.UnitOf);
            }
            IReadOnlyList<string> inLibrary = _services.Library?.FindLeftovers() ?? Array.Empty<string>();
            _leftovers = [.. _analysis.LeftoverTemporaryFiles, .. inLibrary];
            _interrupted = _services.Library?.FindInterrupted() ?? Array.Empty<string>();
            _ranSinceAnalysis = false;
            _needsAnalysis = false;
            TrySave(inspector);
            Groups.Clear();
            foreach (var group in _analysis.Groups)
            {
                Groups.Add(new GroupRow(group) { Title = GroupTitle(group) });
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

            // the reason first; the counts and the warnings about leftovers and interrupted moves stay
            if (why is not null)
            {
                Summary = why + " · " + Summary;
            }

            if (only is not null)
            {
                Summary = $"Súbory poslané z Noviniek ({only.Count}) · " + Summary + " Celý priečinok spracujete po jeho výbere tlačidlom Priečinok….";
            }

            // the output folder was changed while the folder was read: its MOVIES / TVSHOWS were not left out
            if (_services.Library?.Root != libraryAtStart)
            {
                _needsAnalysis = true;
                Summary += " Výstupný priečinok sa počas analýzy zmenil – analyzujte priečinok znova.";
                RefreshCommands();
            }

            SummaryIsWarning = _analysis.FileCount == 0 || _analysis.Failed.Count > 0 || _needsAnalysis;
        }
        catch (OperationCanceledException)
        {
            _analysis = null;
            _items = [];
            Groups.Clear();
            Summary = "Analýza bola zastavená – dajte Analyzovať znova.";
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
    }

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
    public string OtherSeries => _library is { Kind: MediaKind.Series, SeriesOfFile.Count: > 0 } library
        ? (library.SeriesOfFile.Values.Select(t => t.FolderName).Distinct(StringComparer.Ordinal).Count() is var count && count == 1 ? "+ ďalší seriál: " : $"+ ďalšie seriály ({count}): ") +
          string.Join(", ", library.SeriesOfFile.Values.Select(t => t.FolderName).Distinct(StringComparer.Ordinal))
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
            if (_sentSeries.TryGetValue(Path.GetFullPath(file.Path), out var title) && TitleGuess.Typed(MediaKind.Series, title) is { } series)
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
            SeriesTitle = seriesTitle;
            OnPropertyChanged(nameof(IsSeries));
            OnPropertyChanged(nameof(IsMovies));
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
            _services.Mega.Reserved().ToDictionary(r => r.Key, r => r.Value.Label, Core.IO.Paths.Comparer));
        foreach (var row in Groups)
        {
            var items = _items.Where(i => i.Group == row.Group).ToList();
            var review = items.Count(i => i.Status == BatchStatus.NeedsReview);
            row.State = review == 0 ? "pripravené" : $"na kontrolu: {review}";
            row.Title = GroupTitle(row.Group);
        }

        foreach (var slot in SlotRows)
        {
            slot.Refresh();
        }

        ArrangeSlots();
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
        OnPropertyChanged(nameof(OtherSeries));
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
        RefreshCommands();
    }

    /// <summary>
    /// The titles of the group's files without year and ID (decided, suggested, or guessed from the names), the first
    /// with the number of the others: "Zlatý kapor", "Zlatý kapor + 1 ďalší", "+ 2 ďalšie", "+ 5 ďalších".
    /// </summary>
    public string GroupTitle(BatchGroup group)
    {
        string? first = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in group.Files)
        {
            var title = (_library?.TitleFor(file.Path) ?? _library?.SuggestedTitleFor(file.Path))?.CleanName ?? NameOf(file.Path);
            if (title.Length > 0 && seen.Add(Words.Normalize(title)))
            {
                first ??= title;
            }
        }

        var others = seen.Count - 1;
        return first is null ? ""
            : others switch
            {
                0 => first,
                1 => $"{first} + 1 ďalší",
                < 5 => $"{first} + {others} ďalšie",
                _ => $"{first} + {others} ďalších",
            };
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
        if (row == target || row.Key.Type != target.Key.Type || row.Group != target.Group || row.IsRemovedByLanguage || target.IsRemovedByLanguage)
        {
            return;
        }

        var order = SlotRows.Where(s => !s.IsRemovedByLanguage).ToList();
        var down = order.IndexOf(row) < order.IndexOf(target);
        order.Remove(row);
        order.Insert(order.IndexOf(target) + (down ? 1 : 0), row);
        row.Group.Decisions.Order.Clear();
        row.Group.Decisions.Order.AddRange(order.Select(s => s.Key));
        Replan();
    }

    /// <summary>The group's tracks in the order of the result (of its first file), the left-out subtitles at the end.</summary>
    private void ArrangeSlots()
    {
        if (SlotRows.Count == 0 || SlotRows[0].Group is not { } group ||
            _items.FirstOrDefault(i => i.Group == group && i.File == group.Representative) is not { } item)
        {
            return;
        }

        var keyOf = SlotKey.Of(item.File).ToDictionary(x => x.Track.Id, x => x.Key);
        var removed = item.Target.RemovedByLanguage.Where(t => keyOf.ContainsKey(t.Id)).Select(t => keyOf[t.Id]).ToHashSet();
        var order = item.Target.Tracks.Where(t => keyOf.ContainsKey(t.TrackId)).Select(t => keyOf[t.TrackId]).ToList();
        foreach (var slot in SlotRows)
        {
            slot.SetRemovedByLanguage(removed.Contains(slot.Key));
        }

        var wanted = SlotRows.OrderBy(s => order.IndexOf(s.Key) is var at and >= 0 ? at : int.MaxValue)
            .ThenBy(s => s.Key.Type).ThenBy(s => s.Key.Ordinal).ToList();
        for (var i = 0; i < wanted.Count; i++)
        {
            var from = SlotRows.IndexOf(wanted[i]);
            if (from != i)
            {
                SlotRows.Move(from, i);
            }
        }
    }

    private void ShowGroup()
    {
        SlotRows.Clear();
        FileRows.Clear();

        // a group still being read has no decisions yet: it is shown once the whole folder is read
        if (SelectedGroup?.Group is not { } group || IsAnalyzing)
        {
            return;
        }

        foreach (var (key, track) in SlotKey.Of(group.Representative))
        {
            SlotRows.Add(new SlotRow(group, key, track, Replan, MoveSlot));
        }

        ArrangeSlots();

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
        OnPropertyChanged(nameof(CanFindTitles));
        Replan();
        _ = LoadSeriesSeasonsAsync();
    }

    /// <summary>
    /// A series named by its folder with a TMDB ID ("… (2025) [tmdbid-227191]"): its seasons are read from TMDB so every
    /// episode is checked (specification 13.5). Without TMDB nothing is checked.
    /// </summary>
    private async Task LoadSeriesSeasonsAsync()
    {
        if (_library is not { Kind: MediaKind.Series, Series: { TmdbId: { } id } series } library || _services.Tmdb is not { } tmdb ||
            library.SeriesOnTmdb is { } known && known.Title.FolderName == series.FolderName)
        {
            return;
        }

        try
        {
            if (await tmdb.GetAsync(MediaKind.Series, id) is { Seasons.Count: > 0 } details && ReferenceEquals(_library, library) && library.Series == series)
            {
                library.SeriesOnTmdb = (series, details.Seasons);
                TmdbPicker.TrySaveCache(tmdb);
                Replan();
            }
        }
        catch (TmdbException ex)
        {
            RunState = "TMDB: epizódy sa nedajú overiť – " + ex.Message;
        }
    }

    private void AnswerSpecials()
    {
        if (_library is not null)
        {
            RunState = $"Do Season 00: {_library.AnswerSpecials()} súborov.";
            Replan();
        }
    }

    private void ConfirmKind()
    {
        if (_library is not null)
        {
            _library.Kind = _library.Kind;
            OnLibraryChanged();
        }
    }

    private void SetKind(bool selected, MediaKind kind)
    {
        // in a sorted folder the folder decides (the user moves the file)
        if (!selected || _library is null || _library.Mixed || _library.Kind == kind)
        {
            return;
        }

        _library.Kind = kind;
        OnLibraryChanged();
    }

    private void UseTmdbReference(TmdbReference reference, string text)
    {
        if (_library is null)
        {
            return;
        }

        if (!_library.Mixed && reference.Kind is { } kind && (kind != _library.Kind || !_library.KindConfirmed))
        {
            _library.Kind = kind;
            OnLibraryChanged();
        }

        if (_services.Tmdb is null)
        {
            RunState = "Adresa TMDB: na načítanie názvu treba kľúč TMDB v Nastaveniach – alebo napíšte Názov (rok)" +
                       (reference.Kind == MediaKind.Movie ? " pri súboroch." : $" [tmdbid-{reference.Id}].");
            return;
        }

        _typedTmdb = text;
        _ = SearchTypedAsync();
    }

    private async Task SearchTypedAsync()
    {
        try
        {
            await Tmdb.SearchAsync();
        }
        finally
        {
            _typedTmdb = null;
        }
    }

    private async Task NameFilmFromTmdbAsync(string path, TmdbReference reference)
    {
        if ((_services.Titles?.Tmdb ?? _services.Tmdb) is not { } tmdb)
        {
            RunState = "Adresa TMDB: na načítanie názvu treba kľúč TMDB v Nastaveniach – alebo napíšte Názov (rok).";
            return;
        }

        try
        {
            // a /tv/ address (a TV film or miniseries) is a film without an ID after confirmation; a bare ID is a film's
            var kind = reference.Kind ?? MediaKind.Movie;
            if (await tmdb.GetAsync(kind, reference.Id) is { } film && _library is not null)
            {
                TmdbPicker.TrySaveCache(tmdb);
                await ApplyFilmAsync(path, film);
            }
            else
            {
                RunState = $"TMDB nepozná {(reference.Kind == MediaKind.Series ? "seriál" : "film")} s ID {reference.Id} – skontrolujte číslo alebo vložte adresu z themoviedb.org.";
            }
        }
        catch (TitleServiceException ex)
        {
            RunState = ex.Message;
        }
    }

    // a series of TMDB as a film of the library: its name and year, no ID (Jellyfin would take a series' ID for a film's)
    private static TitleInfo? AsFilm(TmdbTitle show) =>
        show.ToTitleInfo() is { } info ? new TitleInfo(MediaKind.Movie, info.Name, info.Year) : null;

    // owner, 29. 10. 2026: the user is told it is a miniseries, the name has no season and episode, it cannot be a series
    private static string MiniseriesNote(TmdbTitle show, TitleInfo asFilm) =>
        $"POZOR: {show.Display} je na TMDB minisériál alebo TV seriál (TMDB {show.Id}), nie film. V mene súboru nie je číslo série a epizódy, " +
        $"preto sa nedá spracovať ako seriál – uloží sa medzi filmy ako „{asFilm.FolderName}“ bez TMDB ID. Ak súhlasíte, potvrďte ho pri súbore; " +
        "inak zvoľte druh seriál a doplňte čísla epizód.";

    /// <summary>A film chosen by the user for a file (its TMDB address, or one of the titles found for it).</summary>
    public async Task ChooseCandidateAsync(string path, TmdbTitle film)
    {
        try
        {
            await ApplyFilmAsync(path, film);
        }
        catch (TitleServiceException ex)
        {
            RunState = ex.Message;
        }
    }

    /// <summary>The titles TMDB (or HISTRA) found for the file at the last "Spresniť názvy", best first; empty when none.</summary>
    public IReadOnlyList<TmdbTitle> CandidatesFor(string path) => _candidates.GetValueOrDefault(path) ?? [];

    // the film becomes the file's title – checked in HISTRA as everywhere: a title it does not confirm waits at the file
    private async Task ApplyFilmAsync(string path, TmdbTitle film)
    {
        if (_library is null)
        {
            return;
        }

        // a TV film or miniseries (a series on TMDB) chosen for a film: among the films without an ID, after confirmation
        if (film.Kind == MediaKind.Series)
        {
            if (AsFilm(film) is { } asFilm)
            {
                _library.ChosenTitles.Add(path);
                _library.Movies.Remove(path);
                _library.MovieSuggestions[path] = asFilm;
                _library.UnverifiedTitles.Add(path);
                _unverifiedNotes[path] = MiniseriesNote(film, asFilm);
                RunState = _unverifiedNotes[path];
            }

            Replan();
            return;
        }

        var (title, note) = TmdbPicker.TitleText(film);
        if (TitleGuess.Typed(MediaKind.Movie, title) is { } info)
        {
            var check = _services.Titles is { UsesHistra: true } titles ? await titles.CheckAsync(film) : null;
            _library.ChosenTitles.Add(path);
            if (check is not null && check.Check(film).NeedsConfirmation())
            {
                _library.Movies.Remove(path);
                _library.MovieSuggestions[path] = info;
                _library.UnverifiedTitles.Add(path);
                _unverifiedNotes[path] = TmdbPicker.Describe(check, film);
                note = (note.Length > 0 ? note + " " : "") + "⚠ " + _unverifiedNotes[path];
            }
            else
            {
                _library.Movies[path] = info;
                _library.UnverifiedTitles.Remove(path);
                _unverifiedNotes.Remove(path);
            }
        }

        RunState = note.Length > 0 ? note : $"{Path.GetFileName(path)}: {film.Display}";
        Replan();
    }

    private void ChooseTmdbSeries(TmdbTitle title)
    {
        if (_library is null)
        {
            return;
        }

        // a sorted folder: the series of the selected group
        if (_library.Mixed)
        {
            var (seriesText, seriesNote) = TmdbPicker.TitleText(title);
            if (title.Kind == MediaKind.Series && TitleGuess.Typed(MediaKind.Series, seriesText) is { } groupSeries)
            {
                foreach (var path in SelectedFiles)
                {
                    _library.SeriesOfFile[path] = groupSeries;
                }

                RunState = seriesNote.Length > 0 ? seriesNote : $"Skupina {SelectedGroup?.Label}: {title.Display}";
            }
            else
            {
                RunState = "Toto je film – v priečinku TVSHOWS má byť seriál; film presuňte do MOVIES.";
            }

            OnPropertyChanged(nameof(SeriesTitle));
            OnPropertyChanged(nameof(SeriesSuggestion));
            Replan();
            return;
        }

        var (text, note) = TmdbPicker.TitleText(title);
        if (title.Kind == MediaKind.Movie)
        {
            // a film's address in the series field: the folder holds this film (all files are its versions); a title HISTRA
            // did not confirm is only suggested and waits at each file for confirmation
            _library.Kind = MediaKind.Movie;
            if (TitleGuess.Typed(MediaKind.Movie, text) is { } film && _analysis is not null)
            {
                foreach (var file in _analysis.Groups.SelectMany(g => g.Files))
                {
                    _library.ChosenTitles.Add(file.Path);
                    if (Tmdb.NeedsConfirmation)
                    {
                        _library.Movies.Remove(file.Path);
                        _library.MovieSuggestions[file.Path] = film;
                        _library.UnverifiedTitles.Add(file.Path);
                        _unverifiedNotes[file.Path] = Tmdb.Warning;
                    }
                    else
                    {
                        _library.Movies[file.Path] = film;
                    }
                }
            }

            RunState = (note.Length > 0 ? note + " " : "") + (Tmdb.NeedsConfirmation ? "⚠ " + Tmdb.Warning + " " : "") +
                       "Adresa TMDB je film (/movie/): druh je filmy a všetky súbory dostali tento názov (verzie jedného filmu) – pri súboroch ho môžete zmeniť.";
            OnLibraryChanged();
            return;
        }

        _library.Kind = MediaKind.Series;
        var chosen = TitleGuess.Typed(MediaKind.Series, text);
        if (Tmdb.NeedsConfirmation && chosen is not null)
        {
            // HISTRA did not confirm TMDB's series: it is only suggested and waits for "Potvrdiť seriál"
            _library.Series = null;
            _library.SeriesSuggestion = chosen;
            _library.SeriesUnverified = true;
            _seriesNote = Tmdb.Warning;
            _library.SeriesOnTmdb = title.Seasons.Count > 0 ? (chosen, title.Seasons) : null;
            RunState = (note.Length > 0 ? note + " " : "") + "⚠ " + Tmdb.Warning + " Ak je správny, dajte Potvrdiť seriál.";
            OnLibraryChanged();
            return;
        }

        _library.SeriesUnverified = false;
        _library.Series = chosen;
        // seasons and episode counts: every episode is checked against them (specification 13.5)
        _library.SeriesOnTmdb = _library.Series is { } series && title.Seasons.Count > 0 ? (series, title.Seasons) : null;
        RunState = note;
        OnLibraryChanged();
    }

    /// <summary>
    /// Refines the suggested film titles on TMDB – without a TMDB key, or when TMDB finds nothing, in the HISTRA catalog –
    /// one request at a time (TMDB at most 4 per second, HISTRA 2, see RequestPacer); the same name and year is asked
    /// only once (cache). Stop cancels; titles found so far stay.
    /// </summary>
    private async Task FindMoviesAsync()
    {
        if (_library is null || _analysis is null || _services.Titles is not { } titles)
        {
            return;
        }

        var service = titles.Tmdb is null ? "HISTRA" : titles.UsesHistra ? "TMDB + HISTRA" : "TMDB";

        // asked by the title read from the name, never by an earlier suggestion of the service (owner's review 28. 10.:
        // a second run would otherwise confirm a title the name never matched)
        var pending = _analysis.Groups.SelectMany(g => g.Files)
            .Where(f => _library.KindFor(f.Path) == MediaKind.Movie && !_library.Unsorted.Contains(f.Path) &&
                        !_library.Movies.ContainsKey(f.Path) && _library.MovieSuggestions.ContainsKey(f.Path) && !_library.ChosenTitles.Contains(f.Path))
            .Select(f => (f.Path, Guess: _library.NameGuesses.GetValueOrDefault(f.Path) ?? _library.MovieSuggestions[f.Path]))
            .ToList();
        var questions = pending.Select(p => (p.Guess.Name.ToLowerInvariant(), p.Guess.Year)).Distinct().Count();
        Busy = true;
        _tmdbCancellation = new CancellationTokenSource();
        StopCommand.NotifyCanExecuteChanged();
        void OnWaiting(TimeSpan delay) => RunState = $"{service} žiada spomaliť – čakám {Math.Ceiling(delay.TotalSeconds):0} s a pokračujem…";
        titles.Waiting += OnWaiting;
        int asked = 0, found = 0, unverified = 0, ambiguous = 0, nowhere = 0;
        var problem = "";
        try
        {
            foreach (var (path, guess) in pending)
            {
                asked++;
                RunState = $"{service} {asked} / {pending.Count} ({questions} rôznych názvov)…";
                // the name of a file may be wrong (owner, 28. 10.): searched also without the year or shortened, matched
                // also by a similar name; whatever is not the name and year as read waits for confirmation at the file
                var results = await titles.SearchTolerantAsync(MediaKind.Movie, guess.Name, guess.Year, _tmdbCancellation.Token);
                _candidates[path] = results.Titles.Take(10).ToList();

                // owner, 29. 10. 2026: no film of that name – a TV film or miniseries is looked for too (TMDB keeps them as
                // series); it goes among the films without an ID, after the user confirms it at the file
                if (results.Titles.Count == 0 && titles.Tmdb is not null &&
                    await titles.SearchAsync(MediaKind.Series, guess.Name, guess.Year, _tmdbCancellation.Token) is { Titles.Count: > 0 } tv &&
                    (tv.Best(guess.Name, guess.Year) ?? tv.OnlyOne(guess.Year)) is { } show && AsFilm(show) is { } asFilm)
                {
                    _candidates[path] = tv.Titles.Take(10).ToList();
                    _library.MovieSuggestions[path] = asFilm;
                    _library.UnverifiedTitles.Add(path);
                    _unverifiedNotes[path] = MiniseriesNote(show, asFilm);
                    found++;
                    unverified++;
                    continue;
                }
                var year = results.Loosened is null ? guess.Year : null;
                var match = results.Best(guess.Name, year);
                var doubts = new List<string>();
                if (results.Loosened is { } how)
                {
                    doubts.Add(how);
                }

                if (match is null && results.Similar(guess.Name) is { } similar)
                {
                    match = similar.Title;
                    doubts.Add($"názov v mene súboru sa líši (zhoda {similar.Similarity * 100:0} %)");
                }

                if (match is null && results.OnlyOne(year) is { } only)
                {
                    match = only;
                    doubts.Add("jediný nájdený titul, jeho názov sa s menom súboru nezhoduje");
                }

                if (match is { Year: { } foundYear } && guess.Year is { } fileYear && Math.Abs(foundYear - fileYear) > 1)
                {
                    doubts.Add($"rok v mene súboru {fileYear}, na {results.Source} {foundYear}");
                }

                if (match?.ToTitleInfo() is { } info)
                {
                    _library.MovieSuggestions[path] = info;
                    found++;

                    // a loosened match, or HISTRA found nothing, another title, or could not be asked: the file waits for
                    // its own confirmation (or another title chosen from the list at it)
                    if (doubts.Count > 0 || results.Check(match).NeedsConfirmation())
                    {
                        if (results.Check(match) == TitleCheck.Unavailable && problem.Length == 0)
                        {
                            problem = results.Note;
                        }

                        _library.UnverifiedTitles.Add(path);
                        _unverifiedNotes[path] = (doubts.Count > 0
                                                     ? $"{results.Source} navrhuje {match.Display}: {string.Join("; ", doubts)} – skontrolujte a potvrďte, alebo vyberte iný titul. "
                                                     : "") + TmdbPicker.Describe(results, match);
                        unverified++;
                    }
                    else
                    {
                        _library.UnverifiedTitles.Remove(path);
                        _unverifiedNotes.Remove(path);
                    }
                }
                else if (results.Titles.Count > 0)
                {
                    // several titles and none named as the file: the suggestion from the name waits for a choice at the file
                    _library.UnverifiedTitles.Add(path);
                    var listed = string.Join(", ", results.Titles.Take(3).Select(t => t.Display)) + (results.Titles.Count > 3 ? ", …" : "");
                    _unverifiedNotes[path] = (results.Titles.Count == 1
                                                 ? $"{results.Source} našlo len {listed} – iný rok, než má súbor"
                                                 : $"{results.Source} našlo viac titulov ({listed}) a žiadny sa nevolá ako súbor") +
                                             " – vyberte správny zo zoznamu pri súbore, napíšte Názov (rok), vložte TMDB ID (číslo alebo adresu z themoviedb.org) alebo potvrďte návrh z mena súboru.";
                    ambiguous++;
                }
                else if (results.FoundNowhere)
                {
                    // found nowhere: the suggestion from the file's name stays and waits at the file for confirmation
                    _library.UnverifiedTitles.Add(path);
                    _unverifiedNotes[path] = (results.TmdbProblem is { } refused ? refused + " " : "") +
                                             (titles.Tmdb is null ? "Katalóg HISTRA titul nenašiel" : "TMDB ani HISTRA titul nenašli") +
                                             " – návrh je z mena súboru. Ak poznáte TMDB ID, vložte ho do poľa s názvom pri súbore (číslo alebo adresu z themoviedb.org).";
                    nowhere++;
                }
            }

            // a sorted folder (owner, 29. 10. 2026): each series of TVSHOWS by its folder's name, as a series on TMDB
            var seriesFound = 0;
            var seriesAsked = 0;
            foreach (var unit in _library.Units?.Where(u => u is { Kind: MediaKind.Series, Sorted: true, Name: not null } &&
                                                            !u.Files.All(_library.SeriesOfFile.ContainsKey)) ?? [])
            {
                seriesAsked++;
                RunState = $"{service}: seriál {unit.Name!.Name}…";
                var results = await titles.SearchTolerantAsync(MediaKind.Series, unit.Name.Name, unit.Name.Year, _tmdbCancellation.Token);
                if ((results.Best(unit.Name.Name, results.Loosened is null ? unit.Name.Year : null) ?? results.OnlyOne(unit.Name.Year)) is { } show &&
                    TitleGuess.Typed(MediaKind.Series, TmdbPicker.TitleText(show).Text) is { } info)
                {
                    foreach (var file in unit.Files)
                    {
                        _library.SeriesSuggestionOfFile[file] = info;
                    }

                    seriesFound++;
                }
            }

            RunState = (seriesAsked > 0 ? $"Seriály: nájdené {seriesFound} z {seriesAsked} – návrh potvrďte pri skupine (Potvrdiť seriál alebo Prijať všetko v skupine). " : "") +
                       $"Spresnené názvy ({service}): {found} z {pending.Count} navrhnutých" +
                       (unverified > 0 ? $", {unverified} čaká na potvrdenie (⚠ pri súbore – názov sa líšil od mena súboru alebo HISTRA titul nepotvrdila)" : "") +
                       (ambiguous > 0 ? $", {ambiguous} majú viac možných titulov (⚠ pri súbore – vyberte správny)" : "") +
                       (nowhere > 0 ? $", {nowhere} sa nenašli nikde (ostáva návrh z mena súboru – potvrďte ho pri súbore)" : "") +
                       " – skontrolujte ich a dajte Prijať návrhy názvov." + (problem.Length > 0 ? " " + problem : "");
        }
        catch (OperationCanceledException)
        {
            RunState = $"Zastavené – spresnené {found} z {asked} doteraz prejdených názvov.";
        }
        catch (TitleServiceException ex)
        {
            RunState = ex.Message + $" (spresnené doteraz: {found})";
        }
        finally
        {
            titles.Waiting -= OnWaiting;
            TmdbPicker.TrySaveCache(titles);
            _tmdbCancellation.Dispose();
            _tmdbCancellation = null;
            Busy = false;
            Replan();
        }
    }

    /// <summary>Confirms the suggested title of one film HISTRA did not confirm (the ⚠ button at the file).</summary>
    public void ConfirmTitle(string path)
    {
        if (_library is null)
        {
            return;
        }

        _library.ConfirmTitle(path);
        _unverifiedNotes.Remove(path);
        Replan();
    }

    /// <summary>Why HISTRA did not confirm a file's suggested title; null when it did (or was not asked).</summary>
    public string? UnverifiedNote(string path) =>
        _library?.UnverifiedTitles.Contains(path) == true ? _unverifiedNotes.GetValueOrDefault(path) ?? "HISTRA titul nepotvrdila." : null;

    private void ConfirmTitles()
    {
        _library?.ConfirmSuggestions();
        Replan();
    }

    private void ConfirmSuggestions()
    {
        var own = SlotRows.Where(s => s.NeedsOwnConfirmation).Select(s => s.Slot).ToList();
        own.AddRange(FileRows.SelectMany(f => f.AudioSlots)
            .Where(s => s.Group.FileSuggestion(s.Path, s.Key) is { NeedsOwnConfirmation: true } && s.ChosenLanguage is null)
            .Select(s => $"{Path.GetFileName(s.Path)} {s.Slot}"));
        foreach (var slot in SlotRows)
        {
            slot.ConfirmSuggestion();
        }

        // each file's own suggestions (heard in that file), except the doubtful ones
        foreach (var slot in FileRows.SelectMany(f => f.AudioSlots))
        {
            slot.ConfirmSuggestion();
        }

        Replan();
        if (own.Count > 0)
        {
            RunState = $"Návrhy s upozornením ostali na vašu voľbu ({string.Join(", ", own)}) – jazyk vyberte pri stope.";
        }
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
            // tracks with the language in their name ("CZ titulky") or chosen for the group are not read
            var undetermined = SlotRows.Where(s => s.IsSubtitles && s.NeedsLanguage && s.ChosenLanguage is null).ToList();
            var named = SlotRows.Count(s => s.IsSubtitles && s.IsNamedInTrack);
            int recognised = 0, unreadable = 0;
            var images = undetermined.Count(s => !s.Track.IsTextSubtitles);
            foreach (var slot in undetermined.Where(s => s.Track.IsTextSubtitles).ToList())
            {
                RunState = $"Čítam titulky {slot.Slot}…";
                var text = await SubtitleText.ExtractAsync(installation, _services.Runner, group.Representative, slot.Track);
                if (text is not null && TextLanguageDetector.Detect(text) is { } code)
                {
                    group.Suggestions[slot.Key] = new LanguageEvidence(LanguageTag.FromCode(code), Confidence.Medium, EvidenceSource.SubtitleText);
                    recognised++;
                }
                else
                {
                    unreadable++;
                }
            }

            Replan();
            RunState = Texts.Detection("Titulky", recognised, unreadable, named,
                images > 0 ? $"{images} obrazové (PGS, VobSub) sa čítať nedajú – jazyk vyberte pri stope" : null);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Which audio tracks "Rozpoznať jazyk audiostôp" listens to.</summary>
    public enum SpeechScope
    {
        /// <summary>The files selected in the list, each on its own.</summary>
        Selected,

        /// <summary>The selected group: its films one by one, a series through one episode.</summary>
        Group,

        /// <summary>Every group of the folder, the same way.</summary>
        All,
    }

    // the speech of the audio tracks without a language: pieces from several places of each film vote; every result is
    // a suggestion with what to check (owner, 28. 10.: the films of a group each on their own, or only chosen films)
    private async Task DetectAudioAsync(SpeechScope scope)
    {
        if (_analysis is null)
        {
            return;
        }

        var series = _library?.Kind == MediaKind.Series;
        var (targets, known) = scope switch
        {
            SpeechScope.Selected => SpeechTargets.ForFiles(_selectedFiles.Select(f => (f.Item.Group, f.Item.File))),
            SpeechScope.Group when SelectedGroup?.Group is { } group => SpeechTargets.ForGroups([group], series),
            SpeechScope.All => SpeechTargets.ForGroups(_analysis.Groups, series),
            _ => (Array.Empty<SpeechTarget>(), 0),
        };

        Busy = true;
        var cancellation = _speechCancellation = new CancellationTokenSource();
        StopCommand.NotifyCanExecuteChanged();
        int recognised = 0, failed = 0, done = 0;
        var notes = new List<string>();
        try
        {
            if (targets.Count > 0)
            {
                RunState = "Overujem nástroje rozpoznávania reči…";
                var tools = await _services.SpeechAsync(cancellation.Token);
                foreach (var target in targets)
                {
                    var name = $"{Path.GetFileName(target.File.Path)} – {Texts.Slot(target.Key)}";
                    var where = $"Počúvam {done + 1}/{targets.Count}: {name}";
                    RunState = where + "…";
                    Progress = (double)done / targets.Count;
                    var pieces = new Progress<int>(n => RunState = $"{where} (úsekov: {n})…");
                    var result = await SpokenLanguage.DetectAsync(tools, _services.Runner, target.File.Path, target.Key.Ordinal, target.File.Duration, pieces, cancellation.Token);
                    if (result.Language is { } code)
                    {
                        SpeechTargets.Keep(target, new LanguageEvidence(LanguageTag.FromCode(code), Confidence.Medium, EvidenceSource.Speech)
                        {
                            Detail = result.Doubt,
                            NeedsOwnConfirmation = result.NeedsOwnConfirmation,
                        });
                        recognised++;
                    }
                    else
                    {
                        failed++;
                        notes.Add($"{name}: {result.Doubt}");
                    }

                    done++;
                }
            }

            Replan();
            RunState = (targets.Count == 0 && known == 0
                           ? scope == SpeechScope.Selected ? "Vyberte v zozname súbory, ktoré sa majú vypočuť." : "Audio: žiadne stopy bez jazyka."
                           : Texts.Detection("Audio", recognised, failed, 0,
                               known > 0 ? $"{known} už jazyk má (z názvu stopy, vašej voľby alebo skoršieho rozpoznania)" : null)) +
                       (notes.Count > 0 ? " " + string.Join("; ", notes.Take(3)) + (notes.Count > 3 ? $" (a ďalšie: {notes.Count - 3})" : "") + "." : "");
        }
        catch (OperationCanceledException)
        {
            Replan();
            RunState = $"Rozpoznávanie reči zastavené – rozpoznané doteraz: {recognised} z {targets.Count}.";
        }
        catch (SpeechException ex)
        {
            Replan();
            RunState = ex.Message + (recognised > 0 ? $" (rozpoznané doteraz: {recognised})" : "");
        }
        finally
        {
            Progress = 0;
            _speechCancellation = null;
            cancellation.Dispose();
            Busy = false;
            StopCommand.NotifyCanExecuteChanged();
        }
    }

    private IReadOnlyList<FileRow> _selectedFiles = [];

    /// <summary>The files selected in the list (several with Ctrl or Shift).</summary>
    public void SelectFiles(IEnumerable<FileRow> files)
    {
        _selectedFiles = files.ToList();
        DetectAudioSelectedCommand.NotifyCanExecuteChanged();
    }

    private async Task RunAsync()
    {
        if (_services.CurrentMkvToolNix is not { } installation || _services.Settings.OriginalHandling is not { } handling || _analysis is null || _library is null)
        {
            return;
        }

        Busy = true;
        _running = true;
        Finish = null;
        var started = System.Diagnostics.Stopwatch.StartNew();
        RefreshCommands();
        try
        {
            var processor = _services.Processor(installation);
            _library.Layout.EnsureCreated();

            // owner, 29. 10. 2026: the run is a job of the job queue – seen in Fronta úloh and Výstup úloh
            var job = new Core.Jobs.BatchJob(
                $"Hromadné spracovanie: {Path.GetFileName(Path.TrimEndingDirectorySeparator(_analysis.Folder))} → knižnica",
                _items, processor, new OutputOptions(handling),
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
                RunState = $"Čaká vo fronte úloh – najprv sa dokončí úloha {other.Id} ({other.Description}).";
            }

            await finished.Task;
            _results = job.Results;
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
            RunState = broken ? $"Spracovanie sa nepodarilo spustiť – pozri Výstup úloh (úloha {job.Id})."
                : $"Hotovo: {done} súborov v knižnici" + (present > 0 ? $", {present} tam už bolo" : "") +
                  (failed > 0 ? $", {failed} s chybou (pozri správu)" : "") +
                  (cancelled ? " – zrušené vo fronte úloh." : stopped ? " – zastavené na požiadanie." : ".") + " Pre ďalší beh dajte Analyzovať.";
            _ranSinceAnalysis = true;
            var took = started.Elapsed;
            var time = took.TotalMinutes >= 1 ? $"{(int)took.TotalHours}:{took.Minutes:00}:{took.Seconds:00}" : $"{Math.Max(1, (int)took.TotalSeconds)} s";
            Finish = new BatchFinish(
                failed > 0 || broken ? BatchFinishKind.Errors : stopped ? BatchFinishKind.Stopped : BatchFinishKind.Done,
                (broken ? "CHYBA" : failed > 0 ? "HOTOVO S CHYBAMI" : cancelled ? "ZRUŠENÉ" : stopped ? "ZASTAVENÉ" : "HOTOVO") + $" – spracovanie skončilo o {DateTime.Now:H:mm}",
                $"{Texts.Count(done, "súbor", "súbory", "súborov")} v knižnici" + (present > 0 ? $" · {present} tam už bolo" : "") +
                (failed > 0 ? $" · {failed} s chybou – podrobnosti v Uložiť správu…" : "") + $" · trvanie {time} · úloha {job.Id} vo Fronte úloh");
        }
        finally
        {
            _running = false;
            Busy = false;
            OnPropertyChanged(nameof(PauseText));
            RefreshCommands();
        }
    }

    private void SendToMega()
    {
        if (_analysis is null || _library is null || _services.Settings.OriginalHandling is not { } handling)
        {
            return;
        }

        var ready = _items.Where(i => i.NeedsWork).ToList();
        var waiting = _items.Count(i => i.Status == BatchStatus.NeedsReview && !i.Reasons.Any(r => r.Kind == ReviewReasonKind.InMegaBatch));
        var titles = ready.Select(i => i.Title?.Name).OfType<string>().Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var name = $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(_analysis.Folder))}: " +
                   string.Join(", ", titles.Take(3)) + (titles.Count > 3 ? $" + {titles.Count - 3} ďalšie" : "");
        if (!Confirm($"Odoslať do MEGA spracovania {Texts.Count(ready.Count, "pripravený súbor", "pripravené súbory", "pripravených súborov")}?\n\n" +
                     "Zapíšu sa neskôr – keď dávku spustíte alebo v nastavenom čase. Tu sa ukážu ako „v dávke“." +
                     (waiting > 0 ? $"\n\n{Texts.Count(waiting, "súbor čaká", "súbory čakajú", "súborov čaká")} na kontrolu – tie ostanú tu." : "")))
        {
            return;
        }

        try
        {
            if (_services.Mega.Add(name, _library.Layout.Root, Template.Apply(_services.Profile), handling, ready) is { } batch)
            {
                RunState = $"Odoslané do MEGA spracovania ako {batch.Label} ({Texts.Count(batch.Files.Count, "súbor", "súbory", "súborov")}).";
                _services.NotifyMegaChanged();
                Replan();
                RefreshCommands();
                MegaRequested?.Invoke();
            }
            else
            {
                RunState = "Všetky pripravené súbory už sú v niektorej dávke MEGA spracovania.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunState = "Dávku sa nepodarilo uložiť: " + ex.Message;
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
        // an analysis only stops reading (a finished run's control is not touched)
        if (_analysisCancellation is { } analysis)
        {
            analysis.Cancel();
            return;
        }

        _tmdbCancellation?.Cancel();
        _speechCancellation?.Cancel();
        if (_control is not null)
        {
            _control.StopAfterCurrent = true;
            _control.Resume();
            RunState = "Zastavím po dokončení rozpracovaných súborov…";

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
        if ((Folder is { } last && Directory.Exists(last) ? last : _services.Settings.InputDirectory) is { } start && Directory.Exists(start))
        {
            dialog.InitialDirectory = start;
        }

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

    /// <summary>
    /// From the Explorer ("Pridať do Hromadného spracovania" on a file): the file's folder is analysed and the template is
    /// made from the file's group, so files like it are processed like it.
    /// </summary>
    public async Task TemplateFromFileAsync(string path)
    {
        if (Busy)
        {
            return;
        }

        Folder = Path.GetDirectoryName(path);
        await AnalyzeAsync();
        if (Groups.FirstOrDefault(g => g.Group.Files.Any(f => Universum.Core.IO.Paths.Same(f.Path, path))) is { } group)
        {
            SelectedGroup = group;
            TemplateFromGroup();
            RunState = $"Šablóna je zo súboru {Path.GetFileName(path)} (skupina {group.Label}) – súbory ako on sa spracujú rovnako.";
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
                     ChooseFolderCommand, ClearCommand, AnalyzeCommand, ConfirmSuggestionsCommand, DetectSubtitlesCommand, DetectAudioCommand, DetectAudioSelectedCommand,
                     DetectAudioAllCommand, EditTemplateCommand,
                     TemplateFromGroupCommand, LoadTemplateCommand, SaveTemplateCommand, RulesOnlyCommand, RunCommand, PauseCommand,
                     StopCommand, CleanupCommand, SaveReportCommand, ChooseOutputDirectoryCommand, ConfirmTitlesCommand, FindMoviesCommand, ConfirmKindCommand,
                     AnswerSpecialsCommand, ConfirmSeriesCommand, AcceptGroupCommand, SendToMegaCommand,
                 })
        {
            command.NotifyCanExecuteChanged();
        }
    }
}
