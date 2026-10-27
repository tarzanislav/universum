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
public sealed class MuxSettingsViewModel : ObservableObject
{
    private static int _created;
    private readonly AppServices _services;
    private MuxSourceRow? _selectedSource;
    private MuxTrackRow? _selectedTrack;
    private string? _selectedAttachment;
    private string _title = "";
    private string _chaptersFile = "";
    private string _problems = "";
    private string _result = "";
    private string _outputPath = "";
    private bool _outputEdited;
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
        Library.Changed += () =>
        {
            UpdateOutput();
            RefreshCommands();
        };
        RemoveSourceCommand = new RelayCommand(RemoveSource, () => _selectedSource is not null);
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

    /// <summary>Number of the settings in this session ("Bez názvu 2").</summary>
    public int Number { get; }

    public RelayCommand RemoveSourceCommand { get; }

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
                    SelectedTrack = null;
                }

                OnPropertiesShown();
            }
        }
    }

    public MuxTrackRow? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (SetProperty(ref _selectedTrack, value))
            {
                if (value is not null)
                {
                    SelectedSource = null;
                }

                OnPropertiesShown();
            }
        }
    }

    /// <summary>The properties panel shows the selected track.</summary>
    public bool ShowsTrack => _selectedTrack is not null;

    /// <summary>The properties panel shows the selected source file.</summary>
    public bool ShowsSource => _selectedTrack is null && _selectedSource is not null;

    public bool ShowsNothing => _selectedTrack is null && _selectedSource is null;

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
                UpdateOutput();
                Recalculate();
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
            if (SetProperty(ref _outputPath, value))
            {
                _outputEdited = true;
                OnPropertyChanged(nameof(OutputNote));
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(ShortName));
                Recalculate();
            }
        }
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
        var (path, placement) = Output();
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

        if (Title.Length == 0 && Inputs.FirstOrDefault()?.File.Title is { Length: > 0 } title)
        {
            _title = title;
            OnPropertyChanged(nameof(Title));
        }

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
        if (_selectedTrack is not { } row || !CanMove(step))
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

        var position = 1;
        foreach (var track in Tracks)
        {
            track.Position = track.Include ? position++ : 0;
        }

        Library.Update();
        UpdateOutput();
        Findings.Clear();
        var spec = Spec();
        if (spec.Tracks.Count > 0)
        {
            foreach (var finding in MuxRules.Check(spec.Sources, spec.Tracks, _services.Profile))
            {
                Findings.Add(Describe(finding));
            }
        }

        Problems = Inputs.Count == 0 ? "" : string.Join("\n", spec.Validate().Append(_useLibrary && Library.Placement is null ? Library.Description : "").Where(p => p.Length > 0));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(FindingsText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsUnprocessed));
        RefreshCommands();
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
            return (_outputPath, null);
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

        return new LibraryLayout(folder).Place(target, title, Library.Episode, _services.Profile) with { Directory = folder };
    }

    /// <summary>As MKVToolNix: the first source's name with " (1)" (or the next free number) beside it.</summary>
    private string DefaultPath()
    {
        if (Inputs.Count == 0 || Path.GetDirectoryName(Inputs[0].File.Path) is not { } folder)
        {
            return "";
        }

        var name = Path.GetFileNameWithoutExtension(Inputs[0].File.Path);
        var inputs = Files.ToList();
        for (var number = 1; ; number++)
        {
            var candidate = Path.Combine(folder, $"{name} ({number}).mkv");
            if (!File.Exists(candidate) && !inputs.Any(i => Core.IO.Paths.Same(i, candidate)))
            {
                return candidate;
            }
        }
    }

    /// <summary>Shows the output as it will be (the size class of a rules name is estimated from the sources).</summary>
    private void UpdateOutput()
    {
        if (!_outputEdited)
        {
            var estimate = Files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            var (path, placement) = Output();
            var shown = placement?.PathFor(estimate) ?? path;
            if (_outputPath != shown)
            {
                _outputPath = shown;
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
        if (_useLibrary)
        {
            _useLibrary = false;
            OnPropertyChanged(nameof(UseLibrary));
            OnPropertyChanged(nameof(OutputEditable));
        }

        UpdateOutput();
        Recalculate();
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
        foreach (var input in Inputs)
        {
            SourceRows.Add(new MuxSourceRow(input, MuxSourceKind.File, input.File.Path, input.File.ContainerType ?? ""));
            foreach (var part in input.Parts)
            {
                SourceRows.Add(new MuxSourceRow(input, MuxSourceKind.Part, part, input.File.ContainerType ?? ""));
            }

            foreach (var appended in input.Appended)
            {
                SourceRows.Add(new MuxSourceRow(input, MuxSourceKind.Appended, appended.Path, appended.ContainerType ?? ""));
            }
        }

        _selectedSource = SourceRows.FirstOrDefault(r => r.Path == selected);
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertiesShown();
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
        if (_selectedTrack is null)
        {
            return false;
        }

        var index = Tracks.IndexOf(_selectedTrack) + step;
        return index >= 0 && index < Tracks.Count;
    }

    private bool CanWrite() => Inputs.Count > 0 && (!_useLibrary || Library.Placement is not null) && Spec().Validate().Count == 0;

    /// <summary>The output as one target state (its tracks give the name by the rules); null with no track taken.</summary>
    private TargetState? LibraryTarget()
    {
        var tracks = Tracks.Where(t => t.Include).Select(t => t.ToTrack(Inputs.IndexOf(t.Input))).ToList();
        return tracks.Count == 0 ? null : MuxRules.Target(Inputs.Select(i => i.ToSource()).ToList(), tracks);
    }

    private void Enqueue(bool start)
    {
        var spec = Spec();
        if (spec.Validate().Count > 0 || _services.CurrentMkvToolNix is not { } installation)
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
        _services.Jobs.Add(job, starts);
        Progress = 0;
        ProgressText = "čaká vo fronte";
        IsModified = false;
        OnPropertyChanged(nameof(IsUnprocessed));
        Result = $"Pridané do fronty ako úloha {job.Id}" + (starts ? "." : " – frontu spustíte na stránke Fronta úloh.");
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
        OnPropertyChanged(nameof(ShowsSource));
        OnPropertyChanged(nameof(ShowsNothing));
        if (_selectedTrack?.HasAdvanced == true)
        {
            ShowAdvanced = true;
        }

        RefreshCommands();
    }

    private void RefreshCommands()
    {
        RemoveSourceCommand?.NotifyCanExecuteChanged();
        MoveUpCommand?.NotifyCanExecuteChanged();
        MoveDownCommand?.NotifyCanExecuteChanged();
        ApplyRulesCommand?.NotifyCanExecuteChanged();
        ClearChaptersCommand?.NotifyCanExecuteChanged();
        RemoveAttachmentCommand?.NotifyCanExecuteChanged();
        ChooseOutputCommand?.NotifyCanExecuteChanged();
        StartCommand?.NotifyCanExecuteChanged();
        QueueCommand?.NotifyCanExecuteChanged();
    }
}
