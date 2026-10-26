using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Jobs;
using Universum.Core.Jobs;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Muxing;
using Universum.Core.Processes;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>
/// Multiplexer (MKVToolNix GUI: Multiplexer): input files, the tracks taken from them with their names, languages and
/// flags in the output order, title, chapters, attachments and the output file. The UNIVERSUM rules fill the tracks in
/// with one click; the Check panel points out what is worth a second look. Writing runs as a job in the queue; the
/// inputs are only read and an existing file is never overwritten.
/// </summary>
public sealed class MultiplexerPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private MuxInputRow? _selectedInput;
    private MuxTrackRow? _selectedTrack;
    private string? _selectedAttachment;
    private string _outputPath = "";
    private bool _outputChosen;
    private string _title = "";
    private string _chaptersFile = "";
    private string _problems = "";
    private string _result = "";
    private bool _loading;

    public MultiplexerPageViewModel(AppServices services)
    {
        _services = services;
        AddFilesCommand = new AsyncRelayCommand(AddFilesAsync);
        RemoveInputCommand = new RelayCommand(RemoveInput, () => _selectedInput is not null);
        ClearCommand = new RelayCommand(Clear, () => Inputs.Count > 0);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => Move(+1), () => CanMove(+1));
        ApplyRulesCommand = new RelayCommand(ApplyRules, () => Tracks.Any(t => t.Include));
        ChooseOutputCommand = new RelayCommand(ChooseOutput, () => Inputs.Count > 0);
        ChooseChaptersCommand = new RelayCommand(ChooseChapters);
        ClearChaptersCommand = new RelayCommand(() => ChaptersFile = "", () => ChaptersFile.Length > 0);
        AddAttachmentCommand = new RelayCommand(AddAttachments);
        RemoveAttachmentCommand = new RelayCommand(() => Attachments.Remove(_selectedAttachment!), () => _selectedAttachment is not null);
        StartCommand = new RelayCommand(() => Enqueue(start: true), CanWrite);
        QueueCommand = new RelayCommand(() => Enqueue(start: false), CanWrite);
        Attachments.CollectionChanged += (_, _) => Recalculate();
    }

    public AsyncRelayCommand AddFilesCommand { get; }

    public RelayCommand RemoveInputCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand ApplyRulesCommand { get; }

    public RelayCommand ChooseOutputCommand { get; }

    public RelayCommand ChooseChaptersCommand { get; }

    public RelayCommand ClearChaptersCommand { get; }

    public RelayCommand AddAttachmentCommand { get; }

    public RelayCommand RemoveAttachmentCommand { get; }

    /// <summary>Adds the job and starts the queue.</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>Adds the job only.</summary>
    public RelayCommand QueueCommand { get; }

    public ObservableCollection<MuxInputRow> Inputs { get; } = [];

    /// <summary>All tracks of all inputs, in output order (tracks not taken stay in the list, unticked).</summary>
    public ObservableCollection<MuxTrackRow> Tracks { get; } = [];

    public ObservableCollection<string> Attachments { get; } = [];

    public ObservableCollection<string> Findings { get; } = [];

    public bool HasFindings => Findings.Count > 0;

    public MuxInputRow? SelectedInput { get => _selectedInput; set { if (SetProperty(ref _selectedInput, value)) { RefreshCommands(); } } }

    public MuxTrackRow? SelectedTrack { get => _selectedTrack; set { if (SetProperty(ref _selectedTrack, value)) { RefreshCommands(); } } }

    public string? SelectedAttachment { get => _selectedAttachment; set { if (SetProperty(ref _selectedAttachment, value)) { RefreshCommands(); } } }

    public string OutputPath
    {
        get => _outputPath;
        set
        {
            if (SetProperty(ref _outputPath, value))
            {
                _outputChosen = true;
                Recalculate();
            }
        }
    }

    public string Title { get => _title; set { if (SetProperty(ref _title, value)) { Recalculate(); } } }

    public string ChaptersFile { get => _chaptersFile; set { if (SetProperty(ref _chaptersFile, value)) { Recalculate(); } } }

    /// <summary>What still stops writing (from the spec's validation).</summary>
    public string Problems { get => _problems; private set => SetProperty(ref _problems, value); }

    public string Result { get => _result; private set => SetProperty(ref _result, value); }

    public string Summary => Inputs.Count == 0
        ? "Pridajte súbory (MKV, MP4, AVI, zvuk, titulky SRT/ASS…) tlačidlom alebo ich sem pretiahnite myšou."
        : $"Vstupy: {Inputs.Count}, vybrané stopy: {Tracks.Count(t => t.Include)} z {Tracks.Count}.";

    /// <summary>The spec the page describes now.</summary>
    public MuxSpec Spec()
    {
        var sources = Inputs.Select(i => i.ToSource()).ToList();
        var tracks = Tracks.Where(t => t.Include).Select(t => t.ToTrack(Inputs.IndexOf(t.Input))).ToList();
        return new MuxSpec(sources, tracks, OutputPath)
        {
            Title = Title.Length == 0 ? null : Title,
            ChaptersFile = ChaptersFile.Length == 0 ? null : ChaptersFile,
            Attachments = Attachments.Select(a => new MuxAttachment(a)).ToList(),
        };
    }

    /// <summary>Adds input files (also by drag and drop and from the self-test).</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Result = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            return;
        }

        var inspector = new MkvMergeInspector(installation, _services.Runner);
        var problems = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var file = await inspector.InspectAsync(path);
                if (!file.IsRecognized || !file.IsSupported || file.Tracks.Count == 0)
                {
                    problems.Add($"{Path.GetFileName(path)}: mkvmerge tento súbor nevie spracovať.");
                    continue;
                }

                var input = new MuxInputRow(file, Inputs.Count + 1, Recalculate);
                Inputs.Add(input);
                foreach (var track in file.Tracks)
                {
                    Tracks.Add(new MuxTrackRow(input, track, LanguageChoice.Common, Recalculate));
                }
            }
            catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException or TimeoutException)
            {
                problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (!_outputChosen && Inputs.Count > 0)
        {
            _outputPath = SuggestOutput(Inputs[0].File.Path);
            OnPropertyChanged(nameof(OutputPath));
        }

        if (Title.Length == 0 && Inputs.FirstOrDefault()?.File.Title is { Length: > 0 } title)
        {
            _title = title;
            OnPropertyChanged(nameof(Title));
        }

        Result = string.Join("\n", problems);
        Recalculate();
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

        Recalculate();
        Result = taken.Any(t => t.Language.Tag.IsUndetermined && t.Track.Type is TrackType.Audio or TrackType.Subtitles)
            ? "Pravidlá sú použité. Stopám s neurčeným jazykom ho vyberte a pravidlá použite znova."
            : "Pravidlá UNIVERSUM sú použité – skontrolujte a spustite.";
    }

    private static string SuggestOutput(string firstInput)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(firstInput))!;
        var name = Path.GetFileNameWithoutExtension(firstInput);
        var candidate = Path.Combine(folder, name + ".mkv");
        for (var number = 1; File.Exists(candidate) || Universum.Core.IO.Paths.Same(candidate, firstInput); number++)
        {
            candidate = Path.Combine(folder, $"{name} (zlúčené{(number > 1 ? " " + number : "")}).mkv");
        }

        return candidate;
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

        Findings.Clear();
        var spec = Spec();
        if (spec.Tracks.Count > 0)
        {
            foreach (var finding in MuxRules.Check(spec.Sources, spec.Tracks, _services.Profile))
            {
                Findings.Add(Describe(finding, spec));
            }
        }

        Problems = Inputs.Count == 0 ? "" : string.Join("\n", spec.Validate());
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(Summary));
        RefreshCommands();
    }

    private static string Describe(CheckFinding finding, MuxSpec spec)
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
        Recalculate();
    }

    private bool CanWrite() => Inputs.Count > 0 && Spec().Validate().Count == 0;

    private void Enqueue(bool start)
    {
        var spec = Spec();
        if (spec.Validate().Count > 0 || _services.CurrentMkvToolNix is not { } installation)
        {
            return;
        }

        var job = new MuxJob($"Zlučovanie: {Path.GetFileName(spec.OutputPath)}", new Multiplexer(installation, _services.Runner), spec);
        job.Changed += changed =>
        {
            if (changed.IsFinished)
            {
                JobRow.OnUi(() =>
                {
                    Result = $"Úloha {changed.Id}: {changed.Outcome}";
                    Recalculate();
                });
            }
        };
        _services.Jobs.Add(job, start || _services.Settings.AutoStartQueue);
        Result = $"Pridané do fronty ako úloha {job.Id}" + (start || _services.Settings.AutoStartQueue ? "." : " – frontu spustíte na stránke Fronta úloh.");
    }

    private async Task AddFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Video, zvuk a titulky|*.mkv;*.mka;*.mks;*.mp4;*.m4v;*.m4a;*.mov;*.avi;*.ts;*.m2ts;*.mpg;*.webm;*.ogg;*.opus;*.flac;*.ac3;*.eac3;*.dts;*.aac;*.mp3;*.wav;*.srt;*.ass;*.ssa;*.vtt;*.sup;*.idx|Všetky súbory|*.*",
        };
        if (dialog.ShowDialog() == true)
        {
            await AddFilesAsync(dialog.FileNames);
        }
    }

    private void RemoveInput()
    {
        if (_selectedInput is not { } input)
        {
            return;
        }

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

        SelectedInput = null;
        Recalculate();
    }

    private void Clear()
    {
        Tracks.Clear();
        Inputs.Clear();
        Attachments.Clear();
        _outputChosen = false;
        _outputPath = "";
        _title = "";
        _chaptersFile = "";
        OnPropertyChanged(nameof(OutputPath));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ChaptersFile));
        Result = "";
        Recalculate();
    }

    private void ChooseOutput()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Matroska (*.mkv)|*.mkv|Matroska zvuk (*.mka)|*.mka|WebM (*.webm)|*.webm",
            FileName = Path.GetFileName(OutputPath),
            InitialDirectory = Path.GetDirectoryName(OutputPath),
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog() == true)
        {
            OutputPath = dialog.FileName;
        }
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

    private void RefreshCommands()
    {
        RemoveInputCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        ApplyRulesCommand.NotifyCanExecuteChanged();
        ChooseOutputCommand.NotifyCanExecuteChanged();
        ClearChaptersCommand?.NotifyCanExecuteChanged();
        RemoveAttachmentCommand?.NotifyCanExecuteChanged();
        StartCommand?.NotifyCanExecuteChanged();
        QueueCommand?.NotifyCanExecuteChanged();
    }
}
