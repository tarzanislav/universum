using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;
using Universum.Platform.Windows;

namespace Universum.App.Pages;

/// <summary>Applying the UNIVERSUM rules to one file: preview, languages for undetermined tracks, title and place in the library, writing, undo.</summary>
public sealed class RulesPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<int, LanguageTag> _languages = [];
    private readonly Dictionary<int, string> _videoNames = [];
    private MediaFile? _file;
    private EditPlan? _plan;
    private string _fileName = "";
    private string _summary = "Otvorte súbor MKV alebo ho sem pretiahnite myšou.";
    private string _issues = "";
    private string _result = "";
    private double _progress;
    private bool _busy;
    private string? _undoRecord;
    private MediaKind _kind = MediaKind.Movie;
    private string _titleText = "";
    private string _episodeText = "";
    private Placement? _placement;
    private string _target = "";

    public RulesPageViewModel(AppServices services)
    {
        _services = services;
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !Busy);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, CanApply);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => !Busy && _undoRecord is not null);
        ChooseOutputDirectoryCommand = new RelayCommand(ChooseOutputDirectory, () => !Busy);
        services.LibraryChanged += () =>
        {
            OnPropertyChanged(nameof(OutputDescription));
            UpdateTarget();
        };
    }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand ApplyCommand { get; }

    public AsyncRelayCommand UndoCommand { get; }

    public RelayCommand ChooseOutputDirectoryCommand { get; }

    public ObservableCollection<RuleRow> Rows { get; } = [];

    public EditPlan? Plan => _plan;

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Issues { get => _issues; private set => SetProperty(ref _issues, value); }

    public string Result { get => _result; private set => SetProperty(ref _result, value); }

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

    public bool KeepOriginal
    {
        get => _services.Settings.OriginalHandling == OriginalHandling.Keep;
        set => SetHandling(value ? OriginalHandling.Keep : null);
    }

    public bool DeleteOriginal
    {
        get => _services.Settings.OriginalHandling == OriginalHandling.Delete;
        set => SetHandling(value ? OriginalHandling.Delete : null);
    }

    public string OutputDescription => _services.Library?.Root ?? "nie je nastavený – zvoľte ho tlačidlom Priečinok…";

    public bool IsMovie
    {
        get => _kind == MediaKind.Movie;
        set => SetKind(value, MediaKind.Movie);
    }

    public bool IsSeries
    {
        get => _kind == MediaKind.Series;
        set => SetKind(value, MediaKind.Series);
    }

    /// <summary>Title as in the library: "Creep (2004)" or "Nine Puzzles (2025) [tmdbid-227191]".</summary>
    public string TitleText
    {
        get => _titleText;
        set
        {
            if (SetProperty(ref _titleText, value))
            {
                UpdateTarget();
            }
        }
    }

    /// <summary>Episode of a series file: "S01E04".</summary>
    public string EpisodeText
    {
        get => _episodeText;
        set
        {
            if (SetProperty(ref _episodeText, value))
            {
                UpdateTarget();
            }
        }
    }

    /// <summary>Where the result goes (or why it cannot go anywhere yet).</summary>
    public string Target { get => _target; private set => SetProperty(ref _target, value); }

    public async Task LoadAsync(string path)
    {
        Result = "";
        _undoRecord = null;
        _languages.Clear();
        _videoNames.Clear();
        _file = null;
        _plan = null;
        Rows.Clear();
        FileName = Path.GetFileName(path);
        GuessTitle(path);
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Summary = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            RefreshCommands();
            return;
        }

        try
        {
            _file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
            Recalculate();
        }
        catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException or TimeoutException)
        {
            Summary = ex.Message;
            RefreshCommands();
        }
    }

    /// <summary>Chooses a language for an undetermined track (also used by the self-test).</summary>
    public void ChooseLanguage(int trackId, string code)
    {
        _languages[trackId] = LanguageTag.FromCode(code);
        Recalculate();
    }

    private void Recalculate()
    {
        if (_file is null)
        {
            return;
        }

        var target = UniversumRules.Apply(_file, _services.Profile, new TrackDecisions { Languages = _languages, VideoNames = _videoNames });
        foreach (var video in target.Tracks.Where(t => t.Type == TrackType.Video && t.Name is not null))
        {
            _videoNames[video.TrackId] = video.Name!;
        }

        _plan = EditPlan.For(target);
        Rows.Clear();
        for (var i = 0; i < target.Tracks.Count; i++)
        {
            var track = target.Tracks[i];
            var chosen = _languages.TryGetValue(track.TrackId, out var tag) ? Languages.Find(tag.Primary) : null;
            Rows.Add(new RuleRow(track, i + 1, chosen, row =>
            {
                // recalculate after the combo box has finished its update (the rows are rebuilt)
                if (row.ChosenLanguage is { } language)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ChooseLanguage(row.TrackId, language.Ietf));
                }
            }));
        }

        Summary = _plan.Method switch
        {
            WriteMethod.None => "Súbor už spĺňa pravidlá UNIVERSUM – len sa uloží do knižnice.",
            WriteMethod.HeaderEdit => $"Zmení sa len hlavička ({_plan.Changes.Count} zmien) – trvá to pár sekúnd.",
            _ => $"Mení sa poradie stôp – vznikne nový súbor ({_plan.Changes.Count} zmien).",
        } + (target.TagsChanged ? " Tagy sa odstránia." : "") + (target.RepairIndex ? " Vytvorí sa chýbajúci index videa." : "") + (_file.HasChapters ? " Kapitoly ostanú." : "");
        Issues = string.Join("\n", target.Issues.Select(Describe));
        UpdateTarget();
    }

    /// <summary>Kind, title and episode guessed from the file and folder names; the user can correct them.</summary>
    private void GuessTitle(string path)
    {
        var episode = EpisodeParser.FromPath(path);
        _kind = episode is null ? MediaKind.Movie : MediaKind.Series;
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        if (_kind == MediaKind.Series && EpisodeParser.SeasonOfFolder(folder) is not null)
        {
            folder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)));
        }

        var fromFolder = TitleGuess.FromName(folder);
        var fromFile = TitleGuess.FromName(Path.GetFileNameWithoutExtension(path));
        var guess = fromFolder is { Year: not null } or { TmdbId: not null } ? fromFolder : fromFile ?? fromFolder;
        _titleText = guess is null ? "" : new TitleInfo(_kind, guess.Name, guess.Year, guess.TmdbId, guess.TmdbId is not null).FolderName;
        _episodeText = episode?.Number.Tag ?? "";
        OnPropertyChanged(nameof(IsMovie));
        OnPropertyChanged(nameof(IsSeries));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(EpisodeText));
    }

    /// <summary>Computes the place in the library from the title, episode and the planned result.</summary>
    private void UpdateTarget()
    {
        _placement = null;
        var layout = _services.Library;
        var title = TitleGuess.Typed(_kind, TitleText);
        var episode = EpisodeNumber.TryParse(EpisodeText);
        if (_plan is null)
        {
            Target = "";
        }
        else if (layout is null)
        {
            Target = "Najprv nastavte výstupný priečinok (knižnicu).";
        }
        else if (title is null || title.Problem is not null)
        {
            Target = "Zadajte názov titulu, napr. Creep (2004).";
        }
        else if (_kind == MediaKind.Series && episode is null)
        {
            Target = "Zadajte epizódu, napr. S01E04.";
        }
        else
        {
            _placement = layout.Place(_plan.Target, title, episode, _services.Profile);
            var size = new FileInfo(_plan.Target.Source.Path).Length;
            Target = _placement.Holds(_plan.Target.Source.Path, size) && _plan.Method == WriteMethod.None
                ? "Súbor už je v knižnici na svojom mieste."
                : Path.GetRelativePath(layout.Root, _placement.PathFor(size)) +
                  (_placement.EstimatedPathLength(size) > LibraryNames.MaxPathLength ? "  – cesta je príliš dlhá pre Windows" : "");
        }

        RefreshCommands();
    }

    private bool CanApply() =>
        !Busy && _plan is not null && !_plan.Target.IsBlocked && _services.Settings.OriginalHandling is not null && _placement is not null &&
        !(_plan.Method == WriteMethod.None && _placement.Holds(_plan.Target.Source.Path, new FileInfo(_plan.Target.Source.Path).Length)) &&
        _placement.EstimatedPathLength(new FileInfo(_plan.Target.Source.Path).Length) <= LibraryNames.MaxPathLength;

    private void SetKind(bool selected, MediaKind kind)
    {
        if (!selected || _kind == kind)
        {
            return;
        }

        _kind = kind;
        OnPropertyChanged(nameof(IsMovie));
        OnPropertyChanged(nameof(IsSeries));
        UpdateTarget();
    }

    private async Task ApplyAsync()
    {
        if (_plan is null || _placement is null || _services.CurrentMkvToolNix is null || _services.Settings.OriginalHandling is not { } handling)
        {
            return;
        }

        Busy = true;
        Result = "Pracujem…";
        Progress = 0;
        try
        {
            var processor = new FileProcessor(_services.CurrentMkvToolNix, _services.Runner, new WindowsRecycleBin(), AppSettings.JournalDirectory);
            var result = await processor.ProcessAsync(_plan, _placement, new OutputOptions(handling), new Progress<double>(p => Progress = p));
            _undoRecord = result.UndoRecordPath;
            var cache = _services.Inspector(_services.CurrentMkvToolNix);
            cache.Invalidate(result.SourcePath);
            if (result.OutputPath is not null)
            {
                cache.Invalidate(result.OutputPath);
            }
            Result = result.Status switch
            {
                ProcessingStatus.Done => $"Hotovo: {result.OutputPath}" + (_undoRecord is null ? "" : "\nZmenu je možné vrátiť späť."),
                ProcessingStatus.Unchanged => "Súbor už spĺňa pravidlá a je v knižnici.",
                ProcessingStatus.AlreadyInLibrary => $"Rovnaký stream už v knižnici je: {result.OutputPath}\nNič sa nezapísalo, originál ostal.",
                ProcessingStatus.NeedsReview => "Súbor treba najprv skontrolovať (pozri upozornenia).",
                ProcessingStatus.Cancelled => "Zrušené.",
                _ => "Nepodarilo sa – originál ostal nezmenený.\n" + string.Join("\n", result.Messages),
            };
            if (result.Status == ProcessingStatus.Done && result.Messages.Count > 0)
            {
                Result += "\n" + string.Join("\n", result.Messages);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task UndoAsync()
    {
        if (_undoRecord is null || _services.CurrentMkvToolNix is null)
        {
            return;
        }

        Busy = true;
        try
        {
            var processor = new FileProcessor(_services.CurrentMkvToolNix, _services.Runner, new WindowsRecycleBin(), AppSettings.JournalDirectory);
            var problems = await processor.UndoAsync(_undoRecord);
            Result = problems.Count == 0 ? "Pôvodná hlavička je obnovená a súbor je na pôvodnom mieste." : "Vrátenie sa nepodarilo:\n" + string.Join("\n", problems);
            if (problems.Count == 0)
            {
                _undoRecord = null;
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Súbory Matroska (*.mkv)|*.mkv|Všetky súbory|*.*" };
        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(dialog.FileName);
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

    private void SetHandling(OriginalHandling? handling)
    {
        if (handling is null)
        {
            return;
        }

        _services.Settings.OriginalHandling = handling;
        _services.Settings.Save();
        OnPropertyChanged(nameof(KeepOriginal));
        OnPropertyChanged(nameof(DeleteOriginal));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OpenCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        ChooseOutputDirectoryCommand.NotifyCanExecuteChanged();
    }

    private string Describe(RuleIssue issue) => issue.Kind switch
    {
        RuleIssueKind.UndeterminedLanguage => $"Stopa {Rows.FirstOrDefault(r => r.TrackId == issue.TrackId)?.Position}: neurčený jazyk – vyberte ho v stĺpci Jazyk.",
        RuleIssueKind.MultipleVideoTracks => "Súbor má viac video stôp – treba ho skontrolovať ručne.",
        RuleIssueKind.NoAudio => "Súbor nemá zvukovú stopu – treba ho skontrolovať ručne.",
        RuleIssueKind.NoPreferredSubtitles => "Zvuk nie je v preferovanom jazyku a chýbajú titulky v preferovanom jazyku – žiadne titulky sa nezobrazia automaticky.",
        RuleIssueKind.MissingIndex => "Video nemá index (pretáčanie môže byť pomalé alebo nefunkčné) – nový zápis ho vytvorí.",
        RuleIssueKind.ContainerWarnings => "mkvmerge pri čítaní hlási upozornenia: " + string.Join(" ", _file?.Warnings ?? []),
        _ => issue.Kind.ToString(),
    };
}
