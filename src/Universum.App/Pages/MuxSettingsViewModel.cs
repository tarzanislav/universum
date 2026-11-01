using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Jobs;
using Universum.Core.Jobs;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.Muxing;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>
/// One multiplex settings (MKVToolNix GUI: a tab of the multiplexer): the source files, the tracks taken from them
/// with their properties in the output order, title, chapters, attachments and where the output goes. The inputs are
/// only read and an existing file is never overwritten.
/// </summary>
public sealed partial class MuxSettingsViewModel : ObservableObject
{
    private static int _created;
    private readonly AppServices _services;
    private MuxSourceRow? _selectedSource;
    private object? _selectedTrackItem;
    private string? _selectedAttachment;
    private string _title = "";
    private string _chaptersFile = "";
    private string _problems = "";
    private string _result = "";
    private string _outputPath = "";
    private string _typedPath = "";
    private bool _outputEdited;
    private bool _recalculating;
    private (bool, string, string) _libraryInput;

    // the output as last shown (a default name is taken as shown, even if another tab freed a lower number since)
    private (string Path, Placement? Placement) _output = ("", null);
    private bool _useLibrary;
    private bool _showAdvanced;
    private bool _loading;
    private bool _isModified;
    private bool _isCurrent;
    private double _progress;
    private string _progressText = "";

    public MuxSettingsViewModel(AppServices services)
    {
        _services = services;
        Number = Interlocked.Increment(ref _created);
        Library = new LibraryTargetPicker(services, LibraryTarget);
        _libraryInput = LibraryInput();
        Library.Changed += () =>
        {
            if (_recalculating)
            {
                UpdateOutput();
                RefreshCommands();
            }
            else if (LibraryInput() != _libraryInput)
            {
                // the kind, title or episode changed (typed or chosen on TMDB): a change of the settings
                _libraryInput = LibraryInput();
                Changed();
            }
            else
            {
                // the library folder or TMDB changed: the output follows, the settings are not changed by it
                Recalculate();
            }
        };
        RemoveSourceCommand = new RelayCommand(RemoveSource, () => _selectedSource is not null);
        AddSourcesCommand = new AsyncRelayCommand(() => AddRequested?.Invoke(MuxAddAction.AddToCurrent, null) ?? Task.CompletedTask);
        AppendFilesCommand = new AsyncRelayCommand(
            () => AddRequested?.Invoke(MuxAddAction.Append, _selectedSource?.Input) ?? Task.CompletedTask, () => _selectedSource is not null);
        AddPartsCommand = new AsyncRelayCommand(
            () => AddRequested?.Invoke(MuxAddAction.AdditionalParts, _selectedSource?.Input) ?? Task.CompletedTask, () => _selectedSource is not null);
        RemoveAllSourcesCommand = new RelayCommand(RemoveAllSources, () => Inputs.Count > 0);
        OutputFromSourceCommand = new RelayCommand(OutputFromSource, () => _selectedSource is not null && !UseLibrary);
        SourceInfoCommand = new RelayCommand(() => InfoRequested?.Invoke(_selectedSource!.Path), () => _selectedSource is not null);
        TakeAllTracksOfSourceCommand = new RelayCommand(TakeAllTracksOfSource, () => _selectedSource is not null);
        SelectMainTrackCommand = new RelayCommand(() => SelectedTrack = SelectedAppendedTrack?.Parent, () => SelectedAppendedTrack is not null);
        Tracks.CollectionChanged += (_, _) => SyncTrackRows();
        MoveUpCommand = new RelayCommand(() => Move(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => Move(+1), () => CanMove(+1));
        ApplyRulesCommand = new RelayCommand(ApplyRules, () => Tracks.Any(t => t.Include));
        ChooseChaptersCommand = new RelayCommand(ChooseChapters);
        ClearChaptersCommand = new RelayCommand(() => ChaptersFile = "", () => ChaptersFile.Length > 0);
        AddAttachmentCommand = new RelayCommand(AddAttachments);
        RemoveAttachmentCommand = new RelayCommand(() => Attachments.Remove(_selectedAttachment!), () => _selectedAttachment is not null);
        ChooseOutputCommand = new RelayCommand(ChooseOutput, () => !UseLibrary);
        ResetOutputCommand = new RelayCommand(ResetOutput);
        StartCommand = new RelayCommand(() => Enqueue(start: true), CanWrite);
        QueueCommand = new RelayCommand(() => Enqueue(start: false), CanWrite);
        Attachments.CollectionChanged += (_, _) => Changed();
    }

    /// <summary>Output files of the other settings and of jobs not yet finished (a default name avoids them).</summary>
    public Func<IEnumerable<string>> ReservedOutputs { get; set; } = () => [];

    /// <summary>Number of the settings in this session ("Bez názvu 2").</summary>
    public int Number { get; }

    public RelayCommand RemoveSourceCommand { get; }

    /// <summary>Asks the page to pick files and add them with this action (to the given source for append and parts).</summary>
    public Func<MuxAddAction, MuxInputRow?, Task>? AddRequested { get; set; }

    /// <summary>Asks the page to show a file on the Information page.</summary>
    public Action<string>? InfoRequested { get; set; }

    // the source list's menu (MKVToolNix GUI: the context menu of Source files)

    /// <summary>Pridať súbory… – as new sources of these settings, without asking.</summary>
    public AsyncRelayCommand AddSourcesCommand { get; }

    /// <summary>Pripojiť súbory… – appended to the selected file (mkvmerge "+").</summary>
    public AsyncRelayCommand AppendFilesCommand { get; }

    /// <summary>Pridať súbory ako ďalšie časti… – read as one file with the selected one (mkvmerge "( a b )").</summary>
    public AsyncRelayCommand AddPartsCommand { get; }

    /// <summary>Odstrániť všetky súbory.</summary>
    public RelayCommand RemoveAllSourcesCommand { get; }

    /// <summary>Nastaviť názov výstupného súboru podľa vybraného súboru.</summary>
    public RelayCommand OutputFromSourceCommand { get; }

    /// <summary>Informácie o súbore – the selected file on the Information page.</summary>
    public RelayCommand SourceInfoCommand { get; }

    /// <summary>Vybrať všetky stopy zo súboru – every track of the selected file is taken.</summary>
    public RelayCommand TakeAllTracksOfSourceCommand { get; }

    /// <summary>From a track of an appended file to the main track it continues (that one is edited).</summary>
    public RelayCommand SelectMainTrackCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand ApplyRulesCommand { get; }

    public RelayCommand ChooseChaptersCommand { get; }

    public RelayCommand ClearChaptersCommand { get; }

    public RelayCommand AddAttachmentCommand { get; }

    public RelayCommand RemoveAttachmentCommand { get; }

    public RelayCommand ChooseOutputCommand { get; }

    /// <summary>Back to the default output: beside the first source, named by the rules when the title is known.</summary>
    public RelayCommand ResetOutputCommand { get; }

    /// <summary>Adds the job and starts the queue.</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>Adds the job only.</summary>
    public RelayCommand QueueCommand { get; }

    /// <summary>Title, episode and TMDB of the output (its name by the rules); the library is optional.</summary>
    public LibraryTargetPicker Library { get; }

    public ObservableCollection<MuxInputRow> Inputs { get; } = [];

    /// <summary>The source list: inputs with their additional parts and appended files.</summary>
    public ObservableCollection<MuxSourceRow> SourceRows { get; } = [];

    /// <summary>All tracks of all inputs, in output order (tracks not taken stay in the list, unticked).</summary>
    public ObservableCollection<MuxTrackRow> Tracks { get; } = [];

    /// <summary>The track list as shown: each track, under it the tracks of the appended files that continue it.</summary>
    public ObservableCollection<object> TrackRows { get; } = [];

    public ObservableCollection<string> Attachments { get; } = [];

    public ObservableCollection<string> Findings { get; } = [];

    public bool HasFindings => Findings.Count > 0;

    /// <summary>The findings on one line (all of them in the tooltip).</summary>
    public string FindingsText => string.Join("   •   ", Findings);

    /// <summary>The tab of this settings is the one shown.</summary>
    public bool IsCurrent { get => _isCurrent; set => SetProperty(ref _isCurrent, value); }

    /// <summary>Changed since it was created or last added to the queue (closing asks first).</summary>
    public bool IsModified { get => _isModified; private set => SetProperty(ref _isModified, value); }

    /// <summary>Has something that was not yet put into the queue.</summary>
    public bool IsUnprocessed => IsModified && Inputs.Count > 0;

    public MuxSourceRow? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
            {
                if (value is not null)
                {
                    SelectedTrackItem = null;
                }

                OnPropertiesShown();
            }
        }
    }

    /// <summary>The row selected in the track list: a track, or a track of an appended file.</summary>
    public object? SelectedTrackItem
    {
        get => _selectedTrackItem;
        set
        {
            if (SetProperty(ref _selectedTrackItem, value))
            {
                if (value is not null)
                {
                    SelectedSource = null;
                }

                OnPropertyChanged(nameof(SelectedTrack));
                OnPropertyChanged(nameof(SelectedAppendedTrack));
                OnPropertiesShown();
            }
        }
    }

    public MuxTrackRow? SelectedTrack { get => _selectedTrackItem as MuxTrackRow; set => SelectedTrackItem = value; }

    /// <summary>A track of an appended file is selected (its main track is the one edited).</summary>
    public MuxAppendedTrackRow? SelectedAppendedTrack => _selectedTrackItem as MuxAppendedTrackRow;

    /// <summary>The properties panel shows the selected track.</summary>
    public bool ShowsTrack => SelectedTrack is not null;

    /// <summary>The properties panel explains the selected track of an appended file.</summary>
    public bool ShowsAppendedTrack => SelectedAppendedTrack is not null;

    /// <summary>The properties panel shows the selected source file.</summary>
    public bool ShowsSource => _selectedTrackItem is null && _selectedSource is not null;

    public bool ShowsNothing => _selectedTrackItem is null && _selectedSource is null;

    /// <summary>The advanced track properties (timestamps, video, subtitles) are shown.</summary>
    public bool ShowAdvanced { get => _showAdvanced; set => SetProperty(ref _showAdvanced, value); }

    public string? SelectedAttachment { get => _selectedAttachment; set { if (SetProperty(ref _selectedAttachment, value)) { RefreshCommands(); } } }

    public string Title { get => _title; set { if (SetProperty(ref _title, value)) { Changed(); } } }

    public string ChaptersFile { get => _chaptersFile; set { if (SetProperty(ref _chaptersFile, value)) { Changed(); } } }

    /// <summary>
    /// The output goes into the library (MOVIES / TVSHOWS, named by the rules); the output file box is then greyed out.
    /// Off by default: the output goes beside the first source.
    /// </summary>
    public bool UseLibrary
    {
        get => _useLibrary;
        set
        {
            if (SetProperty(ref _useLibrary, value))
            {
                OnPropertyChanged(nameof(OutputEditable));
                Changed();
            }
        }
    }

    public bool OutputEditable => !_useLibrary;

    /// <summary>The library is set in the settings (only then can the output go there).</summary>
    public bool CanUseLibrary => _services.Library is not null;

    /// <summary>The output file; typed by the user it stays as typed (until Reset), otherwise it follows the sources and the title.</summary>
    public string OutputPath
    {
        get => _outputPath;
        set
        {
            if (!_useLibrary && _outputPath != value)
            {
                TypeOutput(value);
            }
        }
    }

    // a path chosen by the user (typed, from the dialog or from a source's name) stays until Reset
    private void TypeOutput(string path)
    {
        SetProperty(ref _outputPath, path, nameof(OutputPath));
        _outputEdited = true;
        _typedPath = path;
        OnPropertyChanged(nameof(OutputNote));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ShortName));
        Changed();
    }

    /// <summary>How the output name came about.</summary>
    public string OutputNote =>
        _useLibrary ? "Do knižnice, názov podľa pravidiel (veľkosť sa doplní po zápise)."
        : _outputEdited ? "Zadané ručne."
        : AutoPlacement() is not null ? "Vedľa zdroja, názov podľa pravidiel z titulu (veľkosť sa doplní po zápise)."
        : Inputs.Count > 0 ? "Vedľa prvého zdroja. Zadajte titul alebo ho nájdite na TMDB – názov sa urobí podľa pravidiel."
        : "";

    /// <summary>Name of the output (as the tab shows it): the output file, the first source or "Bez názvu".</summary>
    public string DisplayName =>
        OutputPath.Length > 0 ? Path.GetFileName(OutputPath)
        : Inputs.Count > 0 ? Inputs[0].FileName
        : $"Bez názvu {Number}";

    /// <summary>The beginning of the name for the tab (the whole name is in the tooltip).</summary>
    public string ShortName => DisplayName.Length <= 30 ? DisplayName : DisplayName[..28].TrimEnd() + "…";

    /// <summary>What still stops writing (from the spec's validation).</summary>
    public string Problems { get => _problems; private set => SetProperty(ref _problems, value); }

    public string Result { get => _result; private set => SetProperty(ref _result, value); }

    /// <summary>Progress of this settings' last job (0–1).</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public string ProgressText
    {
        get => _progressText;
        private set
        {
            if (SetProperty(ref _progressText, value))
            {
                OnPropertyChanged(nameof(HasProgress));
            }
        }
    }

    /// <summary>This settings has a job in the queue (its progress is shown).</summary>
    public bool HasProgress => _progressText.Length > 0;

    public string Summary => Inputs.Count == 0
        ? "Pridajte súbory (MKV, MP4, AVI, zvuk, titulky SRT/ASS…) tlačidlom alebo ich sem pretiahnite myšou."
        : $"Zdroje: {Inputs.Count}, vybrané stopy: {Tracks.Count(t => t.Include)} z {Tracks.Count}.";

    /// <summary>All files this settings reads (for the Information page).</summary>
    public IEnumerable<string> Files => Inputs.SelectMany(i => i.ToSource().AllPaths);

    /// <summary>The spec the settings describe now.</summary>
    public MuxSpec Spec()
    {
        var sources = Inputs.Select(i => i.ToSource()).ToList();
        var tracks = Tracks.Where(t => t.Include).Select(t => t.ToTrack(Inputs.IndexOf(t.Input))).ToList();
        var (path, placement) = _output;
        return new MuxSpec(sources, tracks, path)
        {
            Placement = placement,
            Title = Title.Length == 0 ? null : Title,
            ChaptersFile = ChaptersFile.Length == 0 ? null : ChaptersFile,
            Attachments = Attachments.Select(a => new MuxAttachment(a)).ToList(),
        };
    }

    /// <summary>Adds inspected files as new sources with all their tracks.</summary>
    public void AddFiles(IEnumerable<MediaFile> files)
    {
        foreach (var file in files)
        {
            var input = new MuxInputRow(file, Inputs.Count + 1, Changed);
            Inputs.Add(input);
            foreach (var track in file.Tracks)
            {
                Tracks.Add(new MuxTrackRow(input, track, LanguageChoice.Common, Changed));
            }
        }

        if (Library.TitleText.Length == 0 && Inputs.Count > 0)
        {
            Library.Guess(Inputs[0].File.Path);
        }

        // owner, 29. 10. 2026: the title of a source is never taken over (the result has none unless typed)
        RefreshSources();
        Changed();
    }

    /// <summary>Appends files to a source (mkvmerge "+"): they continue its tracks.</summary>
    public void Append(MuxInputRow input, IEnumerable<MediaFile> files)
    {
        foreach (var file in files)
        {
            input.Appended.Add(file);
        }

        RefreshSources();
        Changed();
    }

    /// <summary>Adds further parts of a source, read as one file with it (mkvmerge "( a b )").</summary>
    public void AddParts(MuxInputRow input, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            input.Parts.Add(path);
        }

        RefreshSources();
        Changed();
    }

    /// <summary>The settings were closed: they stop following the library and TMDB.</summary>
    public void Detach()
    {
        Library.Detach();
        ForgetPreview();
    }

    /// <summary>Fills names, languages, flags and the order in by the UNIVERSUM rules (for the tracks taken).</summary>
    public void ApplyRules()
    {
        var taken = Tracks.Where(t => t.Include).ToList();
        if (taken.Count == 0)
        {
            return;
        }

        var sources = Inputs.Select(i => i.ToSource()).ToList();
        var ruled = MuxRules.Apply(sources, taken.Select(t => t.ToTrack(Inputs.IndexOf(t.Input))).ToList(), _services.Profile);
        _loading = true;
        try
        {
            var ordered = ruled.Select(r => taken.First(t => Inputs.IndexOf(t.Input) == r.Source && t.Track.Id == r.TrackId)).ToList();
            foreach (var (row, track) in ordered.Zip(ruled))
            {
                row.Take(track);
            }

            var rest = Tracks.Where(t => !t.Include).ToList();
            Tracks.Clear();
            foreach (var row in ordered.Concat(rest))
            {
                Tracks.Add(row);
            }

            foreach (var input in Inputs)
            {
                input.KeepTags = !_services.Profile.RemoveTags && input.KeepTags;
            }
        }
        finally
        {
            _loading = false;
        }

        Changed();
        Result = taken.Any(t => t.Language.Tag.IsUndetermined && t.Track.Type is TrackType.Audio or TrackType.Subtitles)
            ? "Pravidlá sú použité. Stopám s neurčeným jazykom ho vyberte a pravidlá použite znova."
            : "Pravidlá UNIVERSUM sú použité – skontrolujte a spustite.";
    }

    /// <summary>Moves the selected track in the output order (also Ctrl+↑/↓).</summary>
    public void Move(int step)
    {
        if (SelectedTrack is not { } row || !CanMove(step))
        {
            return;
        }

        var index = Tracks.IndexOf(row);
        Tracks.Move(index, index + step);
        SelectedTrack = row;
        Changed();
    }

    /// <summary>A change by the user: the settings are modified and recalculated.</summary>
    private void Changed()
    {
        if (_loading)
        {
            return;
        }

        IsModified = true;
        Recalculate();
    }

    private void Recalculate()
    {
        if (_loading)
        {
            return;
        }

        _recalculating = true;
        try
        {
            var position = 1;
            foreach (var track in Tracks)
            {
                track.Position = track.Include ? position++ : 0;
            }

            Library.Update();
            UpdateOutput();
        }
        finally
        {
            _recalculating = false;
        }

        Findings.Clear();
        var spec = Spec();
        if (spec.Tracks.Count > 0)
        {
            foreach (var finding in MuxRules.Check(spec.Sources, spec.Tracks, _services.Profile))
            {
                Findings.Add(Describe(finding));
            }
        }

        Problems = string.Join("\n", ProblemList(spec));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(FindingsText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsUnprocessed));
        RefreshCommands();
    }

    /// <summary>What stops writing: the spec's problems, the library's question, a delay that is not a number.</summary>
    private List<string> ProblemList(MuxSpec spec)
    {
        if (Inputs.Count == 0)
        {
            return [];
        }

        var problems = spec.Validate().ToList();

        // a title HISTRA did not confirm (or found nowhere) waits for the user before anything is named by it
        if (Library.Tmdb.NeedsConfirmation)
        {
            problems.Add(Library.Tmdb.Warning + " Potvrďte ho tlačidlom Potvrdiť titul.");
        }

        if (_useLibrary && Library.Placement is null)
        {
            // the output box is greyed out: what is missing is the title (or the episode), not a file name
            problems.Remove(MuxSpec.MissingOutput);
            if (!Library.Tmdb.NeedsConfirmation)
            {
                problems.Add(Library.Description);
            }
        }

        problems.AddRange(Tracks.Where(t => t.Include && t.HasInvalidDelay)
            .Select(t => $"Stopa {t.Position}: oneskorenie zadajte v celých milisekundách, napr. -120."));
        return problems.Where(p => p.Length > 0).ToList();
    }

    /// <summary>The output: library place, typed path, rules name beside the first source, or "name (1).mkv" there.</summary>
    private (string Path, Placement? Placement) Output()
    {
        if (_useLibrary)
        {
            return ("", Library.Placement);
        }

        if (_outputEdited)
        {
            return (_typedPath, null);
        }

        return AutoPlacement() is { } placement ? ("", placement) : (DefaultPath(), null);
    }

    /// <summary>Beside the first source, named by the rules – when the title (and for a series the episode) is known.</summary>
    private Placement? AutoPlacement()
    {
        if (Inputs.Count == 0 || Library.Title is not { } title || LibraryTarget() is not { } target ||
            (title.Kind == MediaKind.Series && Library.Episode is null) || Path.GetDirectoryName(Inputs[0].File.Path) is not { } folder)
        {
            return null;
        }

        return new LibraryLayout(folder).Place(target, title, Library.Episode, _services.Profile).WithAddition(Library.Addition) with { Directory = folder };
    }

    /// <summary>As MKVToolNix: the first source's name with " (1)" (or the next free number) beside it.</summary>
    private string DefaultPath()
    {
        if (Inputs.Count == 0 || Path.GetDirectoryName(Inputs[0].File.Path) is not { } folder)
        {
            return "";
        }

        var name = Path.GetFileNameWithoutExtension(Inputs[0].File.Path);
        var taken = Files.Concat(ReservedOutputs()).ToList();
        for (var number = 1; ; number++)
        {
            var candidate = Path.Combine(folder, $"{name} ({number}).mkv");
            if (!File.Exists(candidate) && !taken.Any(i => SamePath(i, candidate)))
            {
                return candidate;
            }
        }
    }

    // a path typed in another tab may be anything ("   ", "a|b"): it is then simply not the same
    private static bool SamePath(string a, string b)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(a) && Core.IO.Paths.Same(a, b);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The name a placement will get, as far as it can be told before writing: the first one not taken.</summary>
    private string FreeName(Placement placement, long estimate)
    {
        var taken = ReservedOutputs().ToList();
        for (var number = 1; number < 100; number++)
        {
            var candidate = placement.PathFor(estimate, number);
            if (!File.Exists(candidate) && !taken.Any(t => SamePath(t, candidate)))
            {
                return candidate;
            }
        }

        return placement.PathFor(estimate);
    }

    /// <summary>Shows the output as it will be (the size class of a rules name is estimated from the sources).</summary>
    private void UpdateOutput()
    {
        if (_useLibrary || !_outputEdited)
        {
            // the library place, or the default beside the source (a typed path stays as typed)
            var estimate = Files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            _output = Output();
            var (path, placement) = _output;
            var shown = placement is null ? path : FreeName(placement, estimate);
            if (_outputPath != shown)
            {
                _outputPath = shown;
                OnPropertyChanged(nameof(OutputPath));
            }
        }
        else
        {
            _output = Output();
            if (_outputPath != _typedPath)
            {
                // back from the library: the typed path again
                _outputPath = _typedPath;
                OnPropertyChanged(nameof(OutputPath));
            }
        }

        OnPropertyChanged(nameof(OutputNote));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ShortName));
        OnPropertyChanged(nameof(CanUseLibrary));
    }

    private void ResetOutput()
    {
        _outputEdited = false;
        _typedPath = "";
        if (_useLibrary)
        {
            _useLibrary = false;
            OnPropertyChanged(nameof(UseLibrary));
            OnPropertyChanged(nameof(OutputEditable));
        }

        Changed();
    }

    private void ChooseOutput()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Matroska (*.mkv)|*.mkv|Matroska zvuk (*.mka)|*.mka|WebM (*.webm)|*.webm",
            FileName = Path.GetFileName(OutputPath),
            InitialDirectory = Path.GetDirectoryName(OutputPath) is { Length: > 0 } folder && Directory.Exists(folder) ? folder : "",
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog() == true)
        {
            OutputPath = dialog.FileName;
        }
    }

    private void RefreshSources()
    {
        var selected = _selectedSource?.Path;
        SourceRows.Clear();

        // a colour for each file in order (MKVToolNix GUI: the first green, the file appended to it blue…); parts share
        // the colour of their file, which they are read as one with
        var color = 0;
        var colors = new Dictionary<MediaFile, int>();
        foreach (var input in Inputs)
        {
            input.ColorIndex = color++;
            SourceRows.Add(new MuxSourceRow(input, MuxSourceKind.File, input.File.Path, input.File.ContainerType ?? "", input.ColorIndex));
            foreach (var part in input.Parts)
            {
                SourceRows.Add(new MuxSourceRow(input, MuxSourceKind.Part, part, input.File.ContainerType ?? "", input.ColorIndex));
            }

            foreach (var appended in input.Appended)
            {
                colors[appended] = color;
                SourceRows.Add(new MuxSourceRow(input, MuxSourceKind.Appended, appended.Path, appended.ContainerType ?? "", color++));
            }
        }

        // the tracks of the appended files, under the tracks they continue (the same ID)
        foreach (var track in Tracks)
        {
            track.Children = track.Input.Appended
                .Select(file => new MuxAppendedTrackRow(track, file, file.Tracks.FirstOrDefault(t => t.Id == track.Track.Id), colors[file]))
                .ToList();
            track.RefreshSource();
        }

        SyncTrackRows();
        _selectedSource = SourceRows.FirstOrDefault(r => r.Path == selected);
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertiesShown();
    }

    /// <summary>
    /// The shown track list follows the tracks: rows are moved, not replaced, so the selection and the keyboard focus
    /// stay (Ctrl+↑/↓ moves a track again and again).
    /// </summary>
    private void SyncTrackRows()
    {
        var wanted = Tracks.SelectMany(t => t.Children.Prepend<object>(t)).ToList();
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < TrackRows.Count && ReferenceEquals(TrackRows[i], wanted[i]))
            {
                continue;
            }

            var at = -1;
            for (var j = i + 1; j < TrackRows.Count; j++)
            {
                if (ReferenceEquals(TrackRows[j], wanted[i]))
                {
                    at = j;
                    break;
                }
            }

            if (at >= 0)
            {
                TrackRows.Move(at, i);
            }
            else
            {
                TrackRows.Insert(i, wanted[i]);
            }
        }

        while (TrackRows.Count > wanted.Count)
        {
            TrackRows.RemoveAt(TrackRows.Count - 1);
        }

        if (_selectedTrackItem is not null && !TrackRows.Contains(_selectedTrackItem))
        {
            SelectedTrackItem = null;
        }
    }

    private void RemoveAllSources()
    {
        _selectedSource = null;
        _selectedTrackItem = null;
        Tracks.Clear();
        Inputs.Clear();

        _loading = true;
        try
        {
            Library.ForgetGuess();
        }
        finally
        {
            _loading = false;
        }

        _libraryInput = LibraryInput();
        OnPropertyChanged(nameof(SelectedTrackItem));
        OnPropertyChanged(nameof(SelectedTrack));
        OnPropertyChanged(nameof(SelectedAppendedTrack));
        RefreshSources();
        Changed();
    }

    /// <summary>
    /// As MKVToolNix GUI: the output gets the selected file's name (in the output's folder, or beside that file), never
    /// the name of an existing file or of a source – then with " (1)", " (2)"…
    /// </summary>
    private void OutputFromSource()
    {
        if (_selectedSource is not { } row || UseLibrary)
        {
            return;
        }

        var folder = Path.GetDirectoryName(OutputPath) is { Length: > 0 } current ? current : Path.GetDirectoryName(row.Path) ?? "";
        var name = Path.GetFileNameWithoutExtension(row.Path);
        var extension = Path.GetExtension(OutputPath) is { Length: > 0 } ext ? ext : ".mkv";
        var taken = Files.Concat(ReservedOutputs()).ToList();
        var candidate = Path.Combine(folder, name + extension);
        for (var number = 1; File.Exists(candidate) || taken.Any(t => SamePath(t, candidate)); number++)
        {
            candidate = Path.Combine(folder, $"{name} ({number}){extension}");
        }

        // chosen even when it is the name shown already: it no longer follows the sources and the title
        TypeOutput(candidate);
    }

    private void TakeAllTracksOfSource()
    {
        if (_selectedSource is not { } row)
        {
            return;
        }

        foreach (var track in Tracks.Where(t => t.Input == row.Input))
        {
            track.Include = true;
        }
    }

    private void RemoveSource()
    {
        if (_selectedSource is not { } row)
        {
            return;
        }

        var input = row.Input;
        switch (row.Kind)
        {
            case MuxSourceKind.Part:
                input.Parts.Remove(row.Path);
                break;
            case MuxSourceKind.Appended:
                input.Appended.Remove(input.Appended.First(a => a.Path == row.Path));
                break;
            default:
                foreach (var track in Tracks.Where(t => t.Input == input).ToList())
                {
                    Tracks.Remove(track);
                }

                Inputs.Remove(input);
                for (var i = 0; i < Inputs.Count; i++)
                {
                    Inputs[i].Number = i + 1;
                }

                foreach (var track in Tracks)
                {
                    track.RefreshSource();
                }

                break;
        }

        _selectedSource = null;
        RefreshSources();
        Changed();
    }

    private static string Describe(CheckFinding finding)
    {
        var where = finding.TrackId is { } index ? $"Stopa {index + 1}: " : "";
        var kind = finding.Type switch
        {
            TrackType.Video => "videa",
            TrackType.Audio => "zvuku",
            TrackType.Subtitles => "titulkov",
            _ => "stôp",
        };
        return finding.Kind switch
        {
            CheckKind.UndeterminedLanguage => where + "neurčený jazyk (und).",
            CheckKind.SeveralDefaults => $"Viac stôp {kind} má Default – prehrávač vyberie ktorúkoľvek.",
            CheckKind.NoneEnabled => $"Žiadna stopa {kind} nie je zapnutá (Enabled).",
            CheckKind.ForcedWithoutDefault => where + "forced titulky bez Default – väčšina prehrávačov ich sama nezobrazí.",
            CheckKind.TagsRemain => "Výsledok si ponechá tagy zo vstupov.",
            CheckKind.OrderNotPreferred => $"Poradie {kind} nie je CZ → SK → ostatné (komentáre na koniec).",
            _ => finding.Kind.ToString(),
        };
    }

    private bool CanMove(int step)
    {
        if (SelectedTrack is not { } selected)
        {
            return false;
        }

        var index = Tracks.IndexOf(selected) + step;
        return index >= 0 && index < Tracks.Count;
    }

    private bool CanWrite() => Inputs.Count > 0 && ProblemList(Spec()).Count == 0;

    private (bool, string, string) LibraryInput() => (Library.IsSeries, Library.TitleText, Library.EpisodeText);

    /// <summary>The output as one target state (its tracks give the name by the rules); null with no track taken.</summary>
    private TargetState? LibraryTarget()
    {
        var tracks = Tracks.Where(t => t.Include).Select(t => t.ToTrack(Inputs.IndexOf(t.Input))).ToList();
        return tracks.Count == 0 ? null : MuxRules.Target(Inputs.Select(i => i.ToSource()).ToList(), tracks);
    }

    private void Enqueue(bool start)
    {
        var spec = Spec();
        if (ProblemList(spec).Count > 0 || _services.CurrentMkvToolNix is not { } installation)
        {
            return;
        }

        var job = new MuxJob($"Zlučovanie: {DisplayName} ({spec.Tracks.Count} stôp)", new Multiplexer(installation, _services.Runner), spec);
        job.Changed += changed => JobRow.OnUi(() =>
        {
            Progress = changed.Progress;
            ProgressText = changed.Status switch
            {
                JobStatus.Waiting => "čaká vo fronte",
                JobStatus.Running => $"beží {changed.Progress:P0}",
                _ => changed.Outcome,
            };
            if (changed.IsFinished)
            {
                Result = $"Úloha {changed.Id}: {changed.Outcome}";
                Recalculate();
            }
        });
        var starts = start || _services.Settings.AutoStartQueue;

        // shown before the job is added: a job that starts (or fails) at once reports over it
        Progress = 0;
        ProgressText = "čaká vo fronte";
        IsModified = false;
        OnPropertyChanged(nameof(IsUnprocessed));
        Result = $"Pridané do fronty ako úloha {job.Id}" + (starts ? "." : " – frontu spustíte na stránke Fronta úloh.");
        _services.Jobs.Add(job, starts);
    }

    private void ChooseChapters()
    {
        var dialog = new OpenFileDialog { Filter = "Kapitoly (*.xml;*.txt;*.cue)|*.xml;*.txt;*.cue|Všetky súbory|*.*" };
        if (dialog.ShowDialog() == true)
        {
            ChaptersFile = dialog.FileName;
        }
    }

    private void AddAttachments()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "Písma a obrázky|*.ttf;*.otf;*.ttc;*.jpg;*.jpeg;*.png|Všetky súbory|*.*" };
        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames.Where(f => !Attachments.Contains(f)))
            {
                Attachments.Add(file);
            }
        }
    }

    private void OnPropertiesShown()
    {
        OnPropertyChanged(nameof(ShowsTrack));
        OnPropertyChanged(nameof(ShowsAppendedTrack));
        OnPropertyChanged(nameof(ShowsSource));
        OnPropertyChanged(nameof(ShowsNothing));
        if (SelectedTrack?.HasAdvanced == true)
        {
            ShowAdvanced = true;
        }

        RefreshCommands();
    }

    private void RefreshCommands()
    {
        RemoveSourceCommand?.NotifyCanExecuteChanged();
        AppendFilesCommand?.NotifyCanExecuteChanged();
        AddPartsCommand?.NotifyCanExecuteChanged();
        RemoveAllSourcesCommand?.NotifyCanExecuteChanged();
        OutputFromSourceCommand?.NotifyCanExecuteChanged();
        SourceInfoCommand?.NotifyCanExecuteChanged();
        TakeAllTracksOfSourceCommand?.NotifyCanExecuteChanged();
        SelectMainTrackCommand?.NotifyCanExecuteChanged();
        MoveUpCommand?.NotifyCanExecuteChanged();
        MoveDownCommand?.NotifyCanExecuteChanged();
        ApplyRulesCommand?.NotifyCanExecuteChanged();
        ClearChaptersCommand?.NotifyCanExecuteChanged();
        RemoveAttachmentCommand?.NotifyCanExecuteChanged();
        ChooseOutputCommand?.NotifyCanExecuteChanged();
        StartCommand?.NotifyCanExecuteChanged();
        QueueCommand?.NotifyCanExecuteChanged();
        _previewCommand?.NotifyCanExecuteChanged();
        _sendToMegaCommand?.NotifyCanExecuteChanged();
        if (_previewPath is not null)
        {
            OnPropertyChanged(nameof(PreviewIsOld));
        }
    }
}
