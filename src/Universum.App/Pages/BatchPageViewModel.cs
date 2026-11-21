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

/// <summary>
/// Batch processing of a folder (specification, sections 7 and 13): analysis, structure groups, decisions per group,
/// tolerant template, titles and episodes for the library, simulation, run with pause and stop, report.
/// </summary>
public sealed partial class BatchPageViewModel : ObservableObject
{
    private readonly AppServices _services;

    // owner, 1. 11. 2026: the tracks are arranged two seconds after the last change of a language or of Forced
    private readonly ArrangeLater _arrangeLater = new();
    private bool _changedAtTrack;
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
    private string _summary = Loc.T("Vyberte priečinok s video súbormi (MKV, MP4, AVI, TS, M2TS, MOV, MPG, WebM…) a dajte Analyzovať – čo nie je MKV, uloží sa ako nový MKV.");
    private string _simulation = "";
    private string _runState = "";
    private double _progress;
    private bool _busy;
    private bool _running;
    private bool _stoppedAfterAnalysis;
    private BatchControl? _control;
    private Core.Jobs.BatchJob? _job;

    public BatchPageViewModel(AppServices services)
    {
        _services = services;
        Groups.CollectionChanged += (_, _) => SortGroups();
        ChooseFolderCommand = new RelayCommand(ChooseFolder, () => !Busy);
        ClearCommand = new RelayCommand(Clear, () => !Busy && !IsRunning);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !Busy && Directory.Exists(Folder));
        ConfirmSuggestionsCommand = new RelayCommand(ConfirmSuggestions,
            () => !Busy && (SlotRows.Any(s => s.HasSuggestion) || FileRows.Any(f => f.AudioSlots.Any(s => s.HasOwnSuggestion))));
        DetectSubtitlesCommand = new AsyncRelayCommand(() => ReadSubtitlesAsync(again: true),
            () => !Busy && _subtitleReading is null && SelectedGroup?.Group.Representative.Tracks.Any(t => t.Type == TrackType.Subtitles && t.IsTextSubtitles) == true);
        DetectAudioCommand = new AsyncRelayCommand(() => DetectAudioAsync(SpeechScope.Group), () => !Busy && SelectedGroup is not null);
        DetectAudioSelectedCommand = new AsyncRelayCommand(() => DetectAudioAsync(SpeechScope.Selected), () => !Busy && _selectedFiles.Count > 0);
        UndoMovedCommand = new AsyncRelayCommand(UndoMovedAsync, () => !Busy && !_running && MovedOriginals().Count > 0);
        DetectAudioAllCommand = new AsyncRelayCommand(() => DetectAudioAsync(SpeechScope.All, automatic: false), () => !Busy && _analysis is { Groups.Count: > 0 });
        EditTemplateCommand = new RelayCommand(EditTemplate, () => !Busy);
        TemplateFromGroupCommand = new RelayCommand(TemplateFromGroup, () => !Busy && SelectedGroup is not null);
        LoadTemplateCommand = new RelayCommand(LoadTemplate, () => !Busy);
        SaveTemplateCommand = new RelayCommand(SaveTemplate, () => !Busy && Template.Slots.Count > 0);
        RulesOnlyCommand = new RelayCommand(() => Template = BatchTemplate.RulesOnly, () => !Busy);
        RunCommand = new AsyncRelayCommand(RunAsync, () => !Busy && _subtitleReading is null && !_ranSinceAnalysis && !_needsAnalysis && _items.Any(i => i.NeedsWork));
        DeleteOriginalsCommand = new AsyncRelayCommand(DeleteOriginalsAsync, () => !Busy && !_running && Originals != OriginalsMode.Keep && PendingOriginals().Count > 0);
        PauseCommand = new RelayCommand(TogglePause, () => _running);
        StopCommand = new RelayCommand(Stop, () => _running || _tmdbCancellation is not null || _analysisCancellation is not null || _speechCancellation is not null ||
                                                   _subtitleReading is not null);
        CleanupCommand = new RelayCommand(Cleanup, () => !Busy && _leftovers.Count > 0);
        SaveReportCommand = new RelayCommand(SaveReport, () => !Busy && _items.Count > 0);
        // a batch of MEGA spracovanie added, finished or removed: its files are free again or taken
        services.MegaChanged += () =>
        {
            RefreshMegaTargets();
            if (!Busy && _analysis is not null)
            {
                Replan();
                RefreshCommands();
            }
        };
        RefreshMegaTargets();
        // Ponechať MP4 switched here, in Single spracovanie or in Nastavenia: the plan again (an MP4 stays MP4 or becomes MKV)
        services.KeepMp4Changed += () =>
        {
            OnPropertyChanged(nameof(KeepMp4));
            OnPropertyChanged(nameof(HiddenPanelsNote));
            if (!Busy && !_running && _analysis is not null)
            {
                Replan();
                RefreshCommands();
            }
        };
        ShowPanelsCommand = new RelayCommand(() => ShowPanels = true);
        // Zásobník TMDB (owner, 8. 11. 2026): a title from the browser with one click – for the film or the series of the
        // groups chosen, as its TMDB ID typed
        Tray = new TmdbTrayChoice(services, entry =>
        {
            if (IsMovies && CanNameFilm)
            {
                FilmTmdbId = entry.FieldText;
            }
            else
            {
                SeriesTmdbId = entry.FieldText;
            }

            return Task.CompletedTask;
        });
        SendToMegaCommand = new RelayCommand(SendToMega, () => !Busy && _subtitleReading is null && !_ranSinceAnalysis && !_needsAnalysis && _library is not null &&
                                                             _items.Any(i => i.NeedsWork));
        ShowInfoCommand = new RelayCommand(() => InfoRequested?.Invoke(_selectedFile!.Item.File.Path), () => _selectedFile is not null);
        EditInStudioCommand = new RelayCommand(() => EditInStudio(sound: false), () => !Busy && !IsRunning && _selectedFile is not null);
        EditSoundInStudioCommand = new RelayCommand(() => EditInStudio(sound: true), () => !Busy && !IsRunning && _selectedFile is not null);
        EditPictureInStudioCommand = new RelayCommand(() =>
        {
            if (Analysed() is var (analysed, group))
            {
                PictureStudioRequested?.Invoke(analysed, group.Decisions.Studio.GetValueOrDefault(analysed.Path));
            }
        }, () => !Busy && !IsRunning && _selectedFile is not null);
        ChooseOutputDirectoryCommand = new RelayCommand(ChooseOutputDirectory, () => !Busy);
        AcceptSuggestionsCommand = new RelayCommand(AcceptSuggestions, () => !Busy && !IsRunning && _library is not null && _analysis is not null);
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
        AnswerSpecialsCommand = new RelayCommand(AnswerSpecials, () => !Busy && HasSpecialsQuestions);
        Tmdb = new TmdbPicker(services, () => MediaKind.Series,
            () => _typedTmdb ?? (SeriesTitle.Length > 0 ? SeriesTitle : SuggestedSeries?.FolderName ?? ""), ChooseTmdbSeries);
        // owner, 30. 10. 2026: films have the same row as a series – the title, TMDB and the TMDB ID
        FilmTmdb = new TmdbPicker(services, () => MediaKind.Movie,
            () => FilmTitle.Length > 0 ? FilmTitle : FilmFiles.Select(p => _library?.SuggestedTitleFor(p)?.FolderName).FirstOrDefault(t => t is not null) ?? "",
            film => BackgroundWork.Run(ApplyFilmAsync(FilmFiles, film)));
        FindMoviesCommand = new AsyncRelayCommand(() => FindMoviesAsync(automatic: false), () => !Busy && _library is { Kind: MediaKind.Movie } or { Mixed: true } && _services.Titles is not null);
        services.TitlesChanged += () =>
        {
            Tmdb.Refresh();
            FilmTmdb.Refresh();
            FindMoviesCommand.NotifyCanExecuteChanged();
            // a key or token added: the names of the analysed films are looked up now (no need to analyse again)
            if (FindMoviesCommand.CanExecute(null))
            {
                BackgroundWork.Run(FindMoviesAsync(automatic: true));
            }
        };
        services.InputDirectoryChanged += () =>
        {
            OnPropertyChanged(nameof(FolderChoices));
            OnPropertyChanged(nameof(FolderChoice));
            OnPropertyChanged(nameof(AllInputs));
            OnPropertyChanged(nameof(DiskHint));
            OnPropertyChanged(nameof(HasDiskHint));
        };
        services.LibraryChanged += () =>
        {
            OnPropertyChanged(nameof(OutputDescription));
            OnPropertyChanged(nameof(DiskHint));
            OnPropertyChanged(nameof(HasDiskHint));
            if (_analysis is not null && _services.Library is { } layout)
            {
                // files of the analysis that now lie among the results (MOVIES, TVSHOWS) must be left out: analysed again first
                if (_analysis.Groups.SelectMany(g => g.Files).Any(f => _services.Libraries?.HoldsResult(f.Path) == true))
                {
                    _needsAnalysis = true;
                    Summary = Loc.T("V MOVIES alebo TVSHOWS nového výstupného priečinka sú analyzované súbory – analyzujte priečinok znova (tie sa vynechajú).");
                    RunCommand.NotifyCanExecuteChanged();
                    return;
                }

                _library = LibraryJob.Guess(layout, _analysis);
                _library.Libraries = _services.Libraries ?? _library.Libraries;
                SortGroups();
                if (ApplySeriesMemory() is { Length: > 0 } hint)
                {
                    RunState = hint.Trim();
                }

                OnLibraryChanged();
            }
        };
    }

    public RelayCommand ChooseOutputDirectoryCommand { get; }

    /// <summary>Accepts the kind, titles and episode numbers guessed from the names.</summary>
    /// <summary>
    /// "Prijať návrhy" (owner, 21. 11. 2026: one button instead of Prijať návrhy názvov and Prijať všetko v skupine).
    /// </summary>
    public RelayCommand AcceptSuggestionsCommand { get; }

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
            : library.Unsorted.Contains(first) ? Loc.T("⚠ súbor nie je v MOVIES ani v TVSHOWS – presuňte ho a dajte Analyzovať")
            : library.KindFor(first) == MediaKind.Series ? Loc.T("(podľa priečinka TVSHOWS)") : Loc.T("(podľa priečinka MOVIES)")
        : _library is { KindConfirmed: false } ? Loc.T("(návrh podľa mien súborov – potvrďte)") : "";

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
        if (Busy || IsRunning || !Confirm(Loc.T("Vymazať frontu Hromadného spracovania?\n\nZabudnú sa analyzované súbory a rozhodnutia o nich (aj súbory poslané z Noviniek). Na disku sa nič nezmení.")))
        {
            return;
        }

        _studioEdits.Clear();
        _additions.Clear();
        _revisions.Clear();
        _knownFiles.Clear();
        _groupRanks.Clear();

        _subtitleReading?.Cancel();
        _analysis = null;
        _items = [];
        _library = null;
        _only = null;
        _sentSeries = null;
        StopEditing();
        _written.Clear();
        _results = [];
        Finish = null;
        SelectedGroup = null;
        SlotRows.Clear();
        FileRows.Clear();
        Groups.Clear();
        Simulation = "";
        SummaryIsWarning = false;
        Summary = Loc.T("Fronta je prázdna – vyberte priečinok a dajte Analyzovať, alebo pošlite epizódy z Noviniek.");
        OnPropertyChanged(nameof(SeriesTitle));
        OnPropertyChanged(nameof(OtherSeries));
        OnPropertyChanged(nameof(OtherSeriesList));
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

    /// <summary>
    /// The list of groups in two (owner, 1. 11. 2026): Skupina Seriál above – the series only, Skupina Film below – the films
    /// (and groups whose kind is not known yet); both in the order of <see cref="Groups"/>.
    /// </summary>
    public ObservableCollection<GroupRow> SeriesGroups { get; } = [];

    public ObservableCollection<GroupRow> FilmGroups { get; } = [];

    /// <summary>The selected group when it is a series (the upper list); choosing in it selects the group.</summary>
    public GroupRow? SelectedSeriesGroup
    {
        get => SelectedGroup is { } row && SeriesGroups.Contains(row) ? row : null;
        set
        {
            if (value is not null)
            {
                SelectedGroup = value;
            }
        }
    }

    /// <summary>The selected group when it is a film (the lower list).</summary>
    public GroupRow? SelectedFilmGroup
    {
        get => SelectedGroup is { } row && FilmGroups.Contains(row) ? row : null;
        set
        {
            if (value is not null)
            {
                SelectedGroup = value;
            }
        }
    }

    // a group is a series by its first file (MOVIES/TVSHOWS or the group's own choice), or by the kind of the whole folder
    private bool IsSeriesGroup(GroupRow row) => _library is { } library &&
        (row.Group.Files.Count > 0 ? library.KindFor(row.Group.Files[0].Path) : library.Kind) == MediaKind.Series;

    /// <summary>Sorts the groups into the two lists again (after an analysis, a change of the kind, a new group).</summary>
    private void SortGroups()
    {
        var series = Groups.Where(IsSeriesGroup).ToList();
        var films = Groups.Where(g => !series.Contains(g)).ToList();
        var changed = Sync(SeriesGroups, series) | Sync(FilmGroups, films);
        if (changed)
        {
            OnPropertyChanged(nameof(SelectedSeriesGroup));
            OnPropertyChanged(nameof(SelectedFilmGroup));
        }

        static bool Sync(ObservableCollection<GroupRow> list, List<GroupRow> wanted)
        {
            if (list.SequenceEqual(wanted))
            {
                return false;
            }

            list.Clear();
            foreach (var row in wanted)
            {
                list.Add(row);
            }

            return true;
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsSeries) or nameof(IsMovies))
        {
            SortGroups();
        }
        else if (e.PropertyName == nameof(SelectedGroup))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedSeriesGroup)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedFilmGroup)));
        }
    }

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
                EditInStudioCommand.NotifyCanExecuteChanged();
                EditSoundInStudioCommand.NotifyCanExecuteChanged();
                EditPictureInStudioCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Opens the selected file on the Information page.</summary>
    public RelayCommand ShowInfoCommand { get; }

    /// <summary>The Information page should show this file.</summary>
    public event Action<string>? InfoRequested;

    /// <summary>Úprava titulkostopy for the selected file (owner, 30. 10. 2026: Upraviť v štúdiu → Uložiť do úlohy).</summary>
    public RelayCommand EditInStudioCommand { get; }

    /// <summary>
    /// The studio should open a file of the batch: the file as the analysis has it (with the subtitle files found with
    /// it) and its edits so far.
    /// </summary>
    public event Action<MediaFile, StudioEdits?, IReadOnlyDictionary<int, string>>? StudioRequested;

    /// <summary>Úprava audiostopy for the selected file (Upraviť zvuk v štúdiu → Uložiť do úlohy).</summary>
    public RelayCommand EditSoundInStudioCommand { get; }

    /// <summary>Úprava audiostopy should open a file of the batch, as <see cref="StudioRequested"/>.</summary>
    public event Action<MediaFile, StudioEdits?, IReadOnlyDictionary<int, string>>? SoundStudioRequested;

    /// <summary>Úprava videostopy for the selected file (owner, 2. 11. 2026: Obraz v štúdiu → Uložiť do úlohy).</summary>
    public RelayCommand EditPictureInStudioCommand { get; }

    /// <summary>Úprava videostopy should open a file of the batch (as analysed) with its edits so far.</summary>
    public event Action<MediaFile, StudioEdits?>? PictureStudioRequested;

    /// <summary>The analysed files, by group (for the Information page).</summary>
    public IEnumerable<KnownFile> KnownFiles =>
        _items.Select(i => new KnownFile(Loc.F("Hromadné spracovanie – skupina {0}", i.Group.Label), System.IO.Path.GetFileName(i.File.Path), i.File.Path));

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
                if (value is not null)
                {
                    // the end of a run is always seen
                    ShowPanels = true;
                }
            }
        }
    }

    public bool HasFinish => _finish is not null;

    /// <summary>
    /// The template, the originals and the title shown below the tables; hidden, the tables of a small window get the room
    /// (a note of 29. 10. 2026: in a window of 1024 × 700 the tables had too little of it). Remembered.
    /// </summary>
    public bool ShowPanels
    {
        get => !_services.Settings.BatchPanelsHidden;
        set
        {
            if (value == _services.Settings.BatchPanelsHidden)
            {
                _services.Settings.BatchPanelsHidden = !value;
                _services.Settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(PanelsText));
            }
        }
    }

    /// <summary>"Skryť nastavenia ▴" / "Zobraziť nastavenia ▾".</summary>
    public string PanelsText => ShowPanels ? Loc.T("Skryť nastavenia ▴") : Loc.T("Zobraziť nastavenia ▾");

    /// <summary>
    /// The settings hidden: a line in their place says so and what is chosen (owner, 7. 11. 2026: the hidden panels looked
    /// like a part of the window gone missing).
    /// </summary>
    public string HiddenPanelsNote => Loc.F("Nastavenia pod tabuľkami sú skryté – originál: {0}{1}.", Originals switch
    {
        OriginalsMode.DeleteAutomatically => Loc.T("zmazať automaticky"),
        OriginalsMode.DeleteManually => Loc.T("zmazať ručne"),
        OriginalsMode.MoveIntoLibrary => Loc.T("superrýchle"),
        _ => Loc.T("zachovať"),
    }, KeepMp4 ? Loc.T("; MP4 ostáva MP4") : "");

    /// <summary>Shows the hidden settings again (the line in their place).</summary>
    public RelayCommand ShowPanelsCommand { get; }

    /// <summary>
    /// Ponechať MP4 (owner, 7. 11. 2026): an MP4 whose header only changes stays MP4 – UNIVERSUM edits its header itself
    /// (with Superrýchle in seconds); off, every MP4 becomes MKV. The same switch as in Single spracovanie and Nastavenia.
    /// </summary>
    public bool KeepMp4
    {
        get => _services.KeepMp4;
        set
        {
            if (!_running)
            {
                _services.KeepMp4 = value;
            }
        }
    }

    private BatchFinish? _finish;

    public BatchAnalysis? Analysis => _analysis;

    public string? Folder
    {
        get => _services.Settings.BatchFolder;
        set
        {
            // a folder chosen again means the whole folder, not the files sent from Novinky or of a MEGA batch
            _only = null;
            _sentSeries = null;
            StopEditing();
            _services.Settings.BatchFolder = value;
            _services.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(FolderChoices));
            OnPropertyChanged(nameof(FolderChoice));
            OnPropertyChanged(nameof(AllInputs));
            OnPropertyChanged(nameof(DiskHint));
            OnPropertyChanged(nameof(HasDiskHint));
            RefreshCommands();
        }
    }

    /// <summary>
    /// All input folders are analysed at once (owner, 9. 11. 2026: "na hlavný disk som nedal nič, seriály na druhý disk –
    /// analyzovať vypísalo, že tam nie sú žiadne súbory"): when chosen and there is more than one.
    /// </summary>
    public bool AllInputs => _only is null && _services.Settings.BatchAllInputs && _services.InputDirectories.Count > 1 &&
                             Folder is { } folder && _services.InputDirectories.Any(i => Paths.Same(i, folder));

    /// <summary>What Hromadné analyses: all input folders, the input folder of one disk, or a folder chosen.</summary>
    public IReadOnlyList<BatchFolderChoice> FolderChoices
    {
        get
        {
            var inputs = _services.InputDirectories;
            var choices = new List<BatchFolderChoice>();
            if (inputs.Count > 1)
            {
                choices.Add(new BatchFolderChoice(null, Loc.F("Všetky vstupy ({0})", string.Join(", ", inputs.Select(Disk)))));
            }

            choices.AddRange(inputs.Select(i => new BatchFolderChoice(i, Loc.F("DISK {0}  {1}", Disk(i), i))));
            if (Folder is { } folder && !inputs.Any(i => Paths.Same(i, folder)))
            {
                choices.Add(new BatchFolderChoice(folder, folder));
            }

            return choices;
        }
    }

    /// <summary>The choice shown (the list rebuilt each time, so the one equal to it).</summary>
    public BatchFolderChoice? FolderChoice
    {
        get => FolderChoices.FirstOrDefault(c => AllInputs ? c.Folder is null : c.Folder is { } f && Folder is { } folder && Paths.Same(f, folder));
        set
        {
            if (value is null || Busy)
            {
                return;
            }

            _services.Settings.BatchAllInputs = value.Folder is null;
            Folder = value.Folder ?? _services.InputDirectories.FirstOrDefault() ?? Folder;
        }
    }

    private static string Disk(string folder) => Path.GetPathRoot(Path.GetFullPath(folder))?.TrimEnd('\\', '/') ?? folder;

    /// <summary>
    /// The folder lies on a disk without an output folder of its own (owner, 8. 11. 2026): its files would be copied to
    /// the default output on another disk – said beside the folder, with where to add one.
    /// </summary>
    public string DiskHint
    {
        get
        {
            try
            {
                if (Folder is not { Length: > 0 } chosen || _services.Libraries is not { BySourceDisk: true } libraries)
                {
                    return "";
                }

                // every input folder analysed: the first one of them on a disk without an output folder
                if ((AllInputs ? _services.InputDirectories : [chosen]).FirstOrDefault(f => !libraries.All.Any(l => Core.IO.Paths.SameVolume(l.Root, f))) is not { } folder)
                {
                    return "";
                }

                return Loc.F("Disk {0} nemá vlastný výstupný priečinok – súbory sa skopírujú do {1} na inom disku. Výstup tohto disku vytvoríte v Nastaveniach (Priečinky na diskoch → blok disku → Vytvoriť výstup).",
                    Path.GetPathRoot(Path.GetFullPath(folder))?.TrimEnd('\\'), libraries.Default.Root);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                return "";
            }
        }
    }

    public bool HasDiskHint => DiskHint.Length > 0;

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

    /// <summary>
    /// Owner, 7. 11. 2026: the audio tracks without a language are heard right after Analyzovať (a series through one
    /// episode); Rozpoznať jazyk audiostôp stays for any track by hand.
    /// </summary>
    public bool DetectAudioAfterAnalysis
    {
        get => _services.Settings.BatchDetectAudio;
        set
        {
            _services.Settings.BatchDetectAudio = value;
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
                OnFilmChanged();
                if (IsMixed)
                {
                    foreach (var name in new[] { nameof(IsMovies), nameof(IsSeries), nameof(SeriesTitle), nameof(SeriesTargetNote), nameof(SeriesSuggestion), nameof(SeriesNeedsConfirmation), nameof(KindNote), nameof(CanFindTitles) })
                    {
                        OnPropertyChanged(name);
                    }

                    ConfirmSeriesCommand?.NotifyCanExecuteChanged();
                }

                AcceptSuggestionsCommand?.NotifyCanExecuteChanged();
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
        ? Loc.T("bez šablóny – len pravidlá UNIVERSUM")
        : $"{Template.Name}: " + string.Join(", ", Template.Slots.Select(s =>
            $"{Texts.Type(s.Type)} {s.Language}{(s.Role == TrackRole.Main ? "" : " " + Texts.Role(s.Role))}{(s.Required ? Loc.T(" (povinná)") : "")}"));

    public bool RemoveUnlisted
    {
        get => Template.Unlisted == UnlistedTracks.Remove;
        set => Template = Template with { Unlisted = value ? UnlistedTracks.Remove : UnlistedTracks.Keep };
    }

    private void RefreshCommands()
    {
        foreach (var command in new IRelayCommand[]
                 {
                     ChooseFolderCommand, ClearCommand, AnalyzeCommand, ConfirmSuggestionsCommand, DetectSubtitlesCommand, DetectAudioCommand, DetectAudioSelectedCommand,
                     DetectAudioAllCommand, EditTemplateCommand, EditInStudioCommand, EditSoundInStudioCommand, EditPictureInStudioCommand, UndoMovedCommand,
                     TemplateFromGroupCommand, LoadTemplateCommand, SaveTemplateCommand, RulesOnlyCommand, RunCommand, PauseCommand,
                     StopCommand, CleanupCommand, SaveReportCommand, ChooseOutputDirectoryCommand, AcceptSuggestionsCommand, FindMoviesCommand, ConfirmKindCommand,
                     AnswerSpecialsCommand, ConfirmSeriesCommand, SendToMegaCommand, DeleteOriginalsCommand,
                 })
        {
            command.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>A choice of what Hromadné analyses: all input folders (no folder), or one folder.</summary>
public sealed record BatchFolderChoice(string? Folder, string Label);
