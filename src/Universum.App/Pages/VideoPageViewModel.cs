using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.Execution;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;
using Universum.Core.Video;

namespace Universum.App.Pages;

/// <summary>A choice of the list (a label and its value).</summary>
public sealed record VideoChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A part of the film kept, as marked and as it will be written.</summary>
public sealed class CutRow(CutPart part, string written) : ObservableObject
{
    public CutPart Part { get; } = part;

    public string Start => Clock(Part.Start);

    public string End => Clock(Part.End);

    public string Length => Clock(Part.Length);

    /// <summary>"0:00:04,000 – 0:00:08,000" (moved to key frames), or "presne".</summary>
    public string Written { get; } = written;

    internal static string Clock(TimeSpan time) =>
        string.Create(CultureInfo.GetCultureInfo("sk-SK"), $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}");
}

/// <summary>
/// Úprava videostopy (owner, 30. 10. 2026): the film in the player – parts of it kept (without writing the picture: at key
/// frames; exactly: the picture written anew), the proportions it is shown at (without writing the picture), turned, of
/// another height, in another codec (the encoders of Windows). The result is a new file in the library; the original is
/// never changed.
/// </summary>
public sealed class VideoPageViewModel : ObservableObject
{
    private const int FirstAddedId = 1000;
    private readonly AppServices _services;
    private MediaFile? _file;
    private FilmKeyframes? _frames;
    private string? _framesProblem;
    private TimeSpan? _pendingStart;
    private bool _busy;
    private bool _useRules = true;
    private bool _exactCut;
    private VideoChoice<string?> _aspect;
    private VideoChoice<int> _rotation;
    private VideoChoice<int?> _height;
    private VideoChoice<VideoCodec> _codec;
    private int _quality = 75;
    private string _fileName = "";
    private string _summary = "Otvorte video – prehrá sa tu; úseky, ktoré ostanú, označíte počas pozerania.";
    private string _result = "";
    private string _status = "";
    private double _progress;
    private CancellationTokenSource? _work;
    private CancellationTokenSource _reading = new();

    public VideoPageViewModel(AppServices services)
    {
        _services = services;
        Player = new Player.PlayerViewModel(services);
        Library = new LibraryTargetPicker(services, Target);
        Library.Changed += RefreshCommands;
        _aspect = Aspects[0];
        _rotation = Rotations[0];
        _height = Heights[0];
        _codec = Codecs[0];
        OpenVideoCommand = new AsyncRelayCommand(OpenVideoAsync, () => !_busy);
        MarkStartCommand = new RelayCommand(() =>
        {
            _pendingStart = Player.Now;
            Status = $"Začiatok úseku: {CutRow.Clock(Player.Now)} – nájdite koniec a stlačte Koniec úseku.";
            RefreshCommands();
        }, () => !_busy && Player.IsOpen);
        MarkEndCommand = new RelayCommand(MarkEnd, () => !_busy && Player.IsOpen && _pendingStart is not null);
        RemovePartCommand = new RelayCommand<CutRow>(row =>
        {
            if (row is not null)
            {
                _parts.Remove(row.Part);
                Changed();
            }
        }, _ => !_busy);
        WholeFilmCommand = new RelayCommand(() =>
        {
            _parts.Clear();
            _pendingStart = null;
            Status = "Celý film – nič sa nevystrihne.";
            Changed();
        }, () => !_busy && _parts.Count > 0);
        SeekToPartCommand = new AsyncRelayCommand<CutRow>(row => row is null ? Task.CompletedTask : Player.SeekAsync(row.Part.Start));
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        StopCommand = new RelayCommand(() => _work?.Cancel(), () => _work is { IsCancellationRequested: false });
        ClearCommand = new RelayCommand(() =>
        {
            Clear();
            FileName = "";
            Summary = "Otvorte video – prehrá sa tu; úseky, ktoré ostanú, označíte počas pozerania.";
        }, () => !_busy && _file is not null);
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Player.IsOpen))
            {
                RefreshCommands();
            }
        };
    }

    private readonly List<CutPart> _parts = [];

    public Player.PlayerViewModel Player { get; }

    public LibraryTargetPicker Library { get; }

    public AsyncRelayCommand OpenVideoCommand { get; }

    /// <summary>Začiatok úseku: a part kept starts now.</summary>
    public RelayCommand MarkStartCommand { get; }

    /// <summary>Koniec úseku: …and ends now (the part is added).</summary>
    public RelayCommand MarkEndCommand { get; }

    public RelayCommand<CutRow> RemovePartCommand { get; }

    /// <summary>Celý film: no part marked (nothing cut).</summary>
    public RelayCommand WholeFilmCommand { get; }

    /// <summary>The player goes to the start of a part (double click).</summary>
    public AsyncRelayCommand<CutRow> SeekToPartCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand ClearCommand { get; }

    /// <summary>The parts kept, as marked and as they will be written.</summary>
    public ObservableCollection<CutRow> Parts { get; } = [];

    public IReadOnlyList<VideoChoice<string?>> Aspects { get; } =
    [
        new(null, "Ponechať"), new("16/9", "16:9"), new("4/3", "4:3"), new("37/20", "1,85:1"), new("47/20", "2,35:1"), new("12/5", "2,40:1"), new("64/27", "21:9"), new("1/1", "1:1"),
    ];

    public IReadOnlyList<VideoChoice<int>> Rotations { get; } = [new(0, "neotáčať"), new(90, "o 90° doprava"), new(180, "o 180°"), new(270, "o 90° doľava")];

    public IReadOnlyList<VideoChoice<int?>> Heights { get; } =
        [new(null, "Ponechať"), new(2160, "2160 (4K)"), new(1080, "1080"), new(720, "720"), new(576, "576"), new(480, "480")];

    public IReadOnlyList<VideoChoice<VideoCodec>> Codecs { get; } =
        [new(VideoCodec.Keep, "Ponechať (pri zápise H.264 alebo H.265 ako má)"), new(VideoCodec.H264, "H.264"), new(VideoCodec.Hevc, "H.265 (HEVC)")];

    /// <summary>The proportions the picture is shown at (without writing it anew).</summary>
    public VideoChoice<string?> Aspect { get => _aspect; set { if (SetProperty(ref _aspect, value)) { Changed(); } } }

    public VideoChoice<int> Rotation { get => _rotation; set { if (SetProperty(ref _rotation, value)) { Changed(); } } }

    public VideoChoice<int?> Height { get => _height; set { if (SetProperty(ref _height, value)) { Changed(); } } }

    public VideoChoice<VideoCodec> Codec { get => _codec; set { if (SetProperty(ref _codec, value)) { Changed(); } } }

    /// <summary>The quality of the encoders of Windows (1–100).</summary>
    public int Quality { get => _quality; set { if (SetProperty(ref _quality, Math.Clamp(value, 1, 100))) { Changed(); } } }

    /// <summary>Presný strih: the parts begin and end exactly where marked (the picture is written anew, with key frames there).</summary>
    public bool ExactCut { get => _exactCut; set { if (SetProperty(ref _exactCut, value)) { Changed(); } } }

    public bool UseRules { get => _useRules; set { if (SetProperty(ref _useRules, value)) { Changed(); } } }

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Result { get => _result; private set => SetProperty(ref _result, value); }

    /// <summary>What the marks and the key frames say.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public bool IsVideo => _file is not null;

    public bool IsIdle => !_busy;

    /// <summary>The player opens the video (awaited by the self-test).</summary>
    public Task PlayerOpening { get; private set; } = Task.CompletedTask;

    /// <summary>The key frames are read (awaited by the self-test).</summary>
    public Task KeyframesReading { get; private set; } = Task.CompletedTask;

    /// <summary>What becomes of the picture.</summary>
    public VideoRecipe Recipe => new(Rotation.Value, Codec.Value, Quality, Height.Value);

    /// <summary>The picture is written anew (a change of it, or the exact cut).</summary>
    public bool WritesPicture => Recipe.NeedsEncoding || (ExactCut && _parts.Count > 0);

    /// <summary>The changes in words.</summary>
    public string Changes
    {
        get
        {
            if (_file?.TracksOf(TrackType.Video).FirstOrDefault() is not { } video)
            {
                return "";
            }

            var parts = new List<string>();
            if (_parts.Count > 0)
            {
                var kept = WrittenParts().Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Length);
                parts.Add($"strih: {_parts.Count} {(_parts.Count == 1 ? "úsek" : _parts.Count < 5 ? "úseky" : "úsekov")}, ostane {CutRow.Clock(kept)}{(WritesPicture ? " (presne)" : " (na kľúčových snímkach)")}");
            }

            if (Aspect.Value is not null)
            {
                parts.Add($"pomer strán {Aspect.Label}" + (Turned ? " (obraz otočený na výšku)" : ""));
            }

            if (WritesPicture)
            {
                parts.Add((Recipe.Describe(video) is var described && described != "bez zmeny" ? described : $"{Recipe.Encoder(video).Name} kvalita {Quality}") + " – obraz sa zapíše znova");
            }

            return parts.Count == 0 ? "Bez zmeny." : string.Join("; ", parts) + ".";
        }
    }

    public string? Problem => Recipe.Problem ?? CutProblem();

    // turned by 90°: the proportions chosen for the film are those of its picture turned
    private bool Turned => Rotation.Value % 180 == 90;

    // where the film ends on its time (key frames read by ffprobe; else MKVToolNix's duration)
    private TimeSpan? FilmEnd => _frames?.End ?? (_file?.Duration is { } length ? TimeSpan.FromSeconds(Player.Start) + length : null);

    // a cut without writing the picture: possible only with the key frames, and no part may be lost between two of them
    private string? CutProblem()
    {
        if (_parts.Count == 0 || WritesPicture)
        {
            return null;
        }

        if (_frames is null)
        {
            return _framesProblem is { } failed
                ? $"Kľúčové snímky sa nedajú prečítať ({failed}) – strih bez prekódovania nevie, kde úseky začnú; zapnite Presný strih."
                : "Kľúčové snímky sa ešte čítajú – strih bez prekódovania počká na ne (alebo zapnite Presný strih).";
        }

        var keyframes = _frames.Times;
        if (keyframes.Count == 0 || FilmEnd is not { } end)
        {
            return "Video nemá označené kľúčové snímky – strih bez prekódovania nie je možný; zapnite Presný strih.";
        }

        var lost = _parts.FindIndex(p => Cut.Snap([p], keyframes, end).Count == 0);
        return lost < 0 ? null
            : $"Úsek {lost + 1} je kratší ako vzdialenosť kľúčových snímok – nezostalo by z neho nič; predĺžte ho alebo zapnite Presný strih.";
    }

    /// <summary>Something of the picture changes (a cut, the proportions, the picture) – the rules alone are not saved here.</summary>
    public bool HasOwnChange => _parts.Count > 0 || Aspect.Value is not null || WritesPicture;

    /// <summary>Why the video cannot be written by the rules; empty when it can.</summary>
    public string Blocked => _file is null || !UseRules || Target() is not { IsBlocked: true } target ? ""
        : "Pravidlá UNIVERSUM: " + string.Join(" ", target.Issues.Where(i => i.IsBlocking).Select(i => i.Kind switch
        {
            RuleIssueKind.UndeterminedLanguage => $"stopa {i.TrackId} má neurčený jazyk.",
            RuleIssueKind.MultipleVideoTracks => "súbor má viac video stôp.",
            RuleIssueKind.NoAudio => "súbor nemá zvukovú stopu.",
            _ => i.Kind.ToString(),
        }).Distinct()) + " Určte jazyk v Úprave hlavičky – alebo vypnite pravidlá (stopy ostanú, ako sú).";

    /// <summary>A video (drag and drop).</summary>
    public async Task LoadAsync(IReadOnlyList<string> paths)
    {
        if (!_busy && paths.FirstOrDefault() is { } path)
        {
            await LoadVideoAsync(path);
        }
    }

    /// <summary>The video with its changes (the picture not yet written anew); null without a video.</summary>
    public TargetState? Target() => _file is null ? null : Build(_file);

    /// <summary>Adds a part kept (also the self-test).</summary>
    public void AddPart(TimeSpan start, TimeSpan end)
    {
        if (end <= start)
        {
            Status = "Koniec úseku musí byť po jeho začiatku.";
            return;
        }

        _parts.Add(new CutPart(start, end));
        _pendingStart = null;
        var normalized = Cut.Normalized(_parts);
        _parts.Clear();
        _parts.AddRange(normalized);
        Status = _frames is null && !WritesPicture ? "Úsek pridaný (kľúčové snímky sa ešte čítajú – potom sa ukáže, kam sa úsek posunie)." : "Úsek pridaný.";
        Changed();
    }

    private void MarkEnd()
    {
        if (_pendingStart is { } start)
        {
            AddPart(start, Player.Now);
        }
    }

    private async Task OpenVideoAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Video|*.mkv;*.mp4;*.m4v;*.avi;*.ts;*.m2ts;*.mov;*.webm|Všetky súbory|*.*" };
        if (dialog.ShowDialog() == true)
        {
            await LoadVideoAsync(dialog.FileName);
        }
    }

    private async Task LoadVideoAsync(string path)
    {
        Clear();
        FileName = Path.GetFileName(path);
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Summary = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            return;
        }

        _busy = true;
        RefreshCommands();
        try
        {
            var file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
            if (file.TracksOf(TrackType.Video).FirstOrDefault() is not { } video)
            {
                Summary = "Súbor nemá video stopu.";
                return;
            }

            _file = file;
            Summary = $"{file.ContainerName}, video {video.Codec} {video.PixelDimensions}" +
                      (video.FramesPerSecond is { } fps ? $", {fps.ToString("0.###", CultureInfo.GetCultureInfo("sk-SK"))} FPS" : "") +
                      (file.Duration is { } length ? $", {CutRow.Clock(length)}" : "");
            Library.Guess(path);
            PlayerOpening = Player.OpenAsync(file);
            KeyframesReading = ReadKeyframesAsync(file, _reading.Token);
        }
        catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException or TimeoutException)
        {
            Summary = ex.Message;
        }
        finally
        {
            _busy = false;
            Changed();
        }
    }

    // the key frames of the picture (ffprobe beside FFmpeg reads the packets only): where a cut without writing lands
    private async Task ReadKeyframesAsync(MediaFile file, CancellationToken cancellationToken)
    {
        try
        {
            Status = "Čítam kľúčové snímky videa…";
            var ffmpeg = (await _services.SpeechAsync(cancellationToken)).Ffmpeg;
            var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            var frames = await Cut.KeyframesAsync(_services.Runner, ffprobe, file.Path, file.IsMatroska, cancellationToken);
            if (ReferenceEquals(_file, file))
            {
                _frames = frames;
                Status = frames.Times.Count == 0 ? "Video nemá označené kľúčové snímky – strihať sa dá len presne (obraz sa zapíše znova)."
                    : $"Kľúčové snímky: {frames.Times.Count} (strih bez prekódovania sa posunie na najbližšiu nasledujúcu).";
                Changed();
            }
        }
        catch (OperationCanceledException)
        {
            // another video
        }
        catch (Exception ex) when (ex is IOException or ProcessStartException or Core.Speech.SpeechException or TimeoutException)
        {
            if (ReferenceEquals(_file, file))
            {
                _framesProblem = ex.Message;
                Status = "Kľúčové snímky sa nedajú prečítať (" + ex.Message + ") – strihať sa dá len presne.";
                Changed();
            }
        }
    }

    private void Clear()
    {
        _work?.Cancel();
        _reading.Cancel();
        _reading.Dispose();
        _reading = new CancellationTokenSource();
        _file = null;
        _frames = null;
        _framesProblem = null;
        _pendingStart = null;
        _parts.Clear();
        Result = "";
        Status = "";
        Progress = 0;
        _ = Player.CloseAsync();
        Changed();
    }

    private void Changed()
    {
        // each part as it will be written: exactly, or moved to key frames on its own (two parts may meet there)
        Parts.Clear();
        foreach (var part in _parts)
        {
            Parts.Add(new CutRow(part, WritesPicture ? "presne"
                : _frames is not { Times.Count: > 0 } frames || FilmEnd is not { } end ? ""
                : Cut.Snap([part], frames.Times, end) is [var moved] ? $"{CutRow.Clock(moved.Start)} – {CutRow.Clock(moved.End)}"
                : "nič – kratší ako vzdialenosť kľúčových snímok"));
        }

        OnPropertyChanged(nameof(Changes));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(Blocked));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(WritesPicture));
        Library?.Update();
        RefreshCommands();
    }

    // the parts as they will be written: exactly when the picture is written anew (key frames there), else at key frames
    private IReadOnlyList<CutPart> WrittenParts()
    {
        var parts = Cut.Normalized(_parts);
        return WritesPicture || _frames is not { Times.Count: > 0 } frames || FilmEnd is not { } end ? parts : Cut.Snap(parts, frames.Times, end);
    }

    // the target of the video: its picture written anew in a track of its own in the place of the old one (key frames
    // wherever a part begins and ends – the cut is then exact)
    private TargetState Build(MediaFile file)
    {
        var video = file.TracksOf(TrackType.Video).First();
        var tracks = file.Tracks.ToList();
        var removed = new HashSet<int>();
        var picture = video.Id;
        if (WritesPicture)
        {
            var marks = Cut.Normalized(_parts).SelectMany(p => new[] { p.Start, p.End }).Distinct().Order().ToList();
            var anew = new VideoEncoding(file.Path, video, Recipe, marks, file.IsMatroska).ToTrack(FirstAddedId);
            tracks.Insert(tracks.IndexOf(video) + 1, anew);
            removed.Add(video.Id);
            picture = anew.Id;
        }

        var augmented = file with { Tracks = tracks };
        TargetState target;
        if (UseRules)
        {
            target = UniversumRules.Apply(augmented, _services.Profile, new TrackDecisions { Removed = removed });
        }
        else
        {
            var edits = tracks.Where(t => !removed.Contains(t.Id)).Select(t => new TrackEdit(t.Id, t.Name, t.Language, t.Id == picture ? video.Flags : t.Flags)).ToList();
            target = ManualTarget.Create(augmented, edits, removed, title: "");
        }

        return target with
        {
            Cut = _parts.Count > 0 && WrittenParts() is { Count: > 0 } kept ? kept : null,
            Tracks = target.Tracks.Select(t => t.TrackId == picture && Aspect.Value is { } aspect ? t with { AspectRatio = Turned ? Upright(aspect) : aspect } : t).ToList(),
        };
    }

    // "16/9" of a picture turned by 90°: "9/16"
    private static string Upright(string aspect) => aspect.Split('/') is [var width, var height] ? $"{height}/{width}" : aspect;

    private bool CanSave() =>
        !_busy && _file is not null && HasOwnChange && Library.Placement is not null && Problem is null && Blocked.Length == 0 && Target() is { HasChanges: true };

    private async Task SaveAsync()
    {
        if (_file is not { } file || _services.CurrentMkvToolNix is not { } installation || Library.Placement is not { } placement)
        {
            return;
        }

        _busy = true;
        using var work = _work = new CancellationTokenSource();
        RefreshCommands();
        try
        {
            Result = WritesPicture ? "Zapisujem obraz znova a potom nový súbor (môže to trvať dlho)…" : "Zapisujem nový súbor…";
            var plan = EditPlan.For(Build(file));
            var started = DateTime.UtcNow;
            var result = await _services.Processor(installation).ProcessAsync(plan, placement,
                new OutputOptions(OriginalHandling.Keep), new Progress<double>(share => Progress = share), work.Token);
            _services.LogProcessing("Úprava videostopy", plan, result, started);
            Progress = 1;
            Result = result.Status switch
            {
                ProcessingStatus.Done => $"Uložené do knižnice: {result.OutputPath}. Originál ostal nezmenený.",
                ProcessingStatus.AlreadyInLibrary => $"Rovnaký výsledok už v knižnici je: {result.OutputPath}. Nič sa nezapísalo.",
                _ => "Nepodarilo sa – nič sa nezmenilo.\n" + string.Join("\n", result.Messages),
            };
        }
        catch (OperationCanceledException)
        {
            Result = "Zastavené – nič sa nezapísalo.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MkvToolNixException or ProcessStartException or TimeoutException or Core.Speech.SpeechException)
        {
            Result = "Nepodarilo sa: " + ex.Message;
        }
        finally
        {
            _work = null;
            _busy = false;
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(IsIdle));
        OpenVideoCommand?.NotifyCanExecuteChanged();
        MarkStartCommand?.NotifyCanExecuteChanged();
        MarkEndCommand?.NotifyCanExecuteChanged();
        RemovePartCommand?.NotifyCanExecuteChanged();
        WholeFilmCommand?.NotifyCanExecuteChanged();
        SaveCommand?.NotifyCanExecuteChanged();
        StopCommand?.NotifyCanExecuteChanged();
        ClearCommand?.NotifyCanExecuteChanged();
    }
}
