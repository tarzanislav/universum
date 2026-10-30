using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.Audio;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Playback;
using Universum.Core.Processes;
using Universum.Core.Rules;
using Universum.Core.Subtitles;

namespace Universum.App.Pages;

/// <summary>
/// Úprava audiostopy (owner, 30. 10. 2026): the sounds of a video and sounds from elsewhere (a CZ dub, another release)
/// played with the film – the shift while watching, a change of frame rate (PAL 25 ↔ 23,976), automatic fitting to the
/// film's sound, the codec, stereo and the loudness, the language and the name. The result is a new file in the library;
/// the originals are never changed.
/// </summary>
public sealed class AudioPageViewModel : ObservableObject
{
    private const int FirstExternalId = 1000;

    /// <summary>Files of sound (and films whose sound is taken).</summary>
    public static readonly IReadOnlySet<string> SoundExtensions = new HashSet<string>(
        [".mka", ".ac3", ".eac3", ".ec3", ".dts", ".dtshd", ".thd", ".truehd", ".aac", ".m4a", ".mp3", ".flac", ".opus", ".ogg", ".wav", ".mkv", ".mp4", ".m4v", ".avi", ".ts", ".m2ts"],
        StringComparer.OrdinalIgnoreCase);

    private readonly AppServices _services;
    private readonly Dictionary<(string Path, int Ordinal), AudioEnvelope> _envelopes = [];
    private MediaFile? _file;
    private AudioRow? _selected;
    private AudioRow? _reference;
    private bool _busy;
    private bool _useRules = true;
    private int _nextId = FirstExternalId;
    private string _fileName = "";
    private string _summary = "Otvorte video – jeho zvukové stopy a zvuky odinakiaľ (dabing, iné vydanie) sa prehrajú s obrazom.";
    private string _result = "";
    private string _syncStatus = "";
    private double _progress;
    private CancellationTokenSource? _work;
    private (AudioRow Row, AudioSyncResult Result)? _proposal;

    public AudioPageViewModel(AppServices services)
    {
        _services = services;
        Player = new Player.PlayerViewModel(services);
        Library = new LibraryTargetPicker(services, Target);
        Library.Changed += RefreshCommands;
        OpenVideoCommand = new AsyncRelayCommand(OpenVideoAsync, () => !_busy);
        AddSoundCommand = new AsyncRelayCommand(AddSoundAsync, () => !_busy && _file is not null);
        RemoveCommand = new RelayCommand(Remove, () => !_busy && _selected is { IsExternal: true });
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        SyncCommand = new AsyncRelayCommand(SyncAsync, () => !_busy && _file is not null && _selected is not null && _reference is not null && _selected != _reference);
        StopCommand = new RelayCommand(() => _work?.Cancel(), () => _work is { IsCancellationRequested: false });
        ApplyProposalCommand = new RelayCommand(() =>
        {
            if (_proposal is { } proposal)
            {
                proposal.Row.SetTiming(proposal.Result.Timing, proposal.Result.Rates);
                SyncStatus = $"Návrh použitý: {proposal.Result.Detail}. Overte ho prehraním.";
                _proposal = null;
                OnPropertyChanged(nameof(HasProposal));
                RefreshCommands();
            }
        }, () => !_busy && _proposal is not null);
        ClearCommand = new RelayCommand(() =>
        {
            Clear();
            FileName = "";
            Summary = "Otvorte video – jeho zvukové stopy a zvuky odinakiaľ (dabing, iné vydanie) sa prehrajú s obrazom.";
            RefreshCommands();
        }, () => !_busy && _file is not null);
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Player.IsOpen))
            {
                ApplySound();
                RefreshCommands();
            }
        };
    }

    public Player.PlayerViewModel Player { get; }

    public LibraryTargetPicker Library { get; }

    public ObservableCollection<AudioRow> Rows { get; } = [];

    /// <summary>The film's own sounds: the one other sounds are fitted to.</summary>
    public IEnumerable<AudioRow> References => Rows.Where(r => !r.IsExternal);

    public AsyncRelayCommand OpenVideoCommand { get; }

    /// <summary>Pridať zvuk…: files of sound, or films whose sound is taken (a dub from another release).</summary>
    public AsyncRelayCommand AddSoundCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>Napasovať: the selected sound fitted to the film's sound (the shift, a change of frame rate).</summary>
    public AsyncRelayCommand SyncCommand { get; }

    /// <summary>Stops fitting or writing.</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>Applies a fit that is not sure (the owner checks it by playing).</summary>
    public RelayCommand ApplyProposalCommand { get; }

    public RelayCommand ClearCommand { get; }

    public bool HasProposal => _proposal is not null;

    /// <summary>The sound played with the film and edited.</summary>
    public AudioRow? SelectedRow
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                ApplySound();
                RefreshCommands();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>The film's sound other sounds are fitted to.</summary>
    public AudioRow? Reference { get => _reference; set { if (SetProperty(ref _reference, value)) { RefreshCommands(); } } }

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Result { get => _result; private set => SetProperty(ref _result, value); }

    public string SyncStatus { get => _syncStatus; private set => SetProperty(ref _syncStatus, value); }

    /// <summary>How far fitting or writing is (0–1).</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public bool IsVideo => _file is not null;

    /// <summary>The rules of UNIVERSUM decide the order and flags of the tracks (otherwise they stay as they are).</summary>
    public bool UseRules { get => _useRules; set { if (SetProperty(ref _useRules, value)) { Changed(); } } }

    /// <summary>The player opens the video (awaited by the self-test).</summary>
    public Task PlayerOpening { get; private set; } = Task.CompletedTask;

    /// <summary>Why the video cannot be written by the rules (a track of an undetermined language …); empty when it can.</summary>
    public string Blocked => _file is null || !UseRules || Target() is not { IsBlocked: true } target ? ""
        : "Pravidlá UNIVERSUM: " + string.Join(" ", target.Issues.Where(i => i.IsBlocking).Select(i => i.Kind switch
        {
            RuleIssueKind.UndeterminedLanguage => $"stopa {i.TrackId} má neurčený jazyk.",
            RuleIssueKind.MultipleVideoTracks => "súbor má viac video stôp.",
            RuleIssueKind.NoAudio => "súbor nemá zvukovú stopu.",
            _ => i.Kind.ToString(),
        }).Distinct()) + " Určte jazyk – alebo vypnite pravidlá (stopy ostanú, ako sú).";

    /// <summary>A video, then sounds to add (drag and drop).</summary>
    public async Task LoadAsync(IReadOnlyList<string> paths)
    {
        var video = paths.FirstOrDefault(p => BatchAnalyzer.VideoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
        if (_file is null || (video is not null && !paths.All(p => SoundExtensions.Contains(Path.GetExtension(p)))))
        {
            if (video is null)
            {
                Summary = "Najprv otvorte video – zvuky sa pridávajú k nemu.";
                return;
            }

            await LoadVideoAsync(video);
            paths = paths.Where(p => p != video).ToList();
        }

        if (paths.Count > 0)
        {
            await AddFilesAsync(paths);
        }
    }

    /// <summary>The video with the sounds as chosen (the sounds written anew not yet); null without a video.</summary>
    public TargetState? Target() => _file is null ? null : Build(_file, Rows.ToDictionary(r => r, r => r.Track));

    private async Task OpenVideoAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Video|*.mkv;*.mp4;*.m4v;*.avi;*.ts;*.m2ts;*.mov;*.webm|Všetky súbory|*.*" };
        if (dialog.ShowDialog() == true)
        {
            await LoadVideoAsync(dialog.FileName);
        }
    }

    private async Task AddSoundAsync()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Zvuk a filmy|" + string.Join(";", SoundExtensions.Select(e => "*" + e)) + "|Všetky súbory|*.*",
        };
        if (dialog.ShowDialog() == true)
        {
            await AddFilesAsync(dialog.FileNames);
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
            _file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
            var ordinal = 0;
            foreach (var track in _file.TracksOf(TrackType.Audio))
            {
                Rows.Add(new AudioRow(track, ordinal++, null, Changed));
            }

            Reference = Rows.FirstOrDefault(r => r.Track.Flags.Default) ?? Rows.FirstOrDefault();
            SelectedRow = Reference;
            OnPropertyChanged(nameof(References));
            Summary = $"{_file.ContainerName}, zvukové stopy: {Rows.Count}" +
                      (_file.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond is { } fps ? $", video {fps.ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("sk-SK"))} FPS" : "");
            Library.Guess(path);
            PlayerOpening = Player.OpenAsync(_file);
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

    // the sounds of other files: every audio track of each as a row (Include: the user decides)
    private async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (_file is null || _services.CurrentMkvToolNix is not { } installation)
        {
            return;
        }

        _busy = true;
        RefreshCommands();
        var problems = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                try
                {
                    var file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
                    var ordinal = 0;
                    var added = 0;
                    foreach (var track in file.TracksOf(TrackType.Audio))
                    {
                        var row = new AudioRow(track with { Id = _nextId++, ExternalFile = path, ExternalTrack = track.Id, Flags = track.Flags with { Default = false } }, ordinal++, path, Changed);
                        Rows.Add(row);
                        SelectedRow = row;
                        added++;
                    }

                    if (added == 0)
                    {
                        problems.Add($"{Path.GetFileName(path)}: nemá zvuk");
                    }
                }
                catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException or TimeoutException)
                {
                    problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        finally
        {
            _busy = false;
            Result = problems.Count == 0 ? "" : "Nepridané: " + string.Join("; ", problems);
            Changed();
        }
    }

    private void Remove()
    {
        if (_selected is { IsExternal: true } row)
        {
            Rows.Remove(row);
            if (_proposal?.Row == row)
            {
                _proposal = null;
                OnPropertyChanged(nameof(HasProposal));
            }

            SelectedRow = Rows.FirstOrDefault();
            Changed();
        }
    }

    private void Clear()
    {
        _work?.Cancel();
        _file = null;
        _proposal = null;
        _envelopes.Clear();
        Rows.Clear();
        _nextId = FirstExternalId;
        Reference = null;
        SelectedRow = null;
        Result = "";
        SyncStatus = "";
        Progress = 0;
        OnPropertyChanged(nameof(HasProposal));
        OnPropertyChanged(nameof(References));
        _ = Player.CloseAsync();
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Blocked));
        OnPropertyChanged(nameof(IsVideo));
        ShowNames();
        ApplySound();
        Library?.Update();
        RefreshCommands();
    }

    // the names the tracks get (the rules' until the user writes his own) shown in their rows
    private void ShowNames()
    {
        if (Target() is not { } target)
        {
            return;
        }

        foreach (var row in Rows)
        {
            if (target.Tracks.FirstOrDefault(t => t.TrackId == row.Track.Id) is { } track)
            {
                row.ShowName(track.Name);
            }
        }
    }

    // the selected sound plays with the film, with its times as they will be written
    private void ApplySound()
    {
        if (Player is not { IsOpen: true } player || _file is null)
        {
            return;
        }

        _ = player.SetSoundAsync(_selected is { } row
            ? new PlayerSound(row.ExternalPath ?? _file.Path, row.Ordinal, row.Problem is null ? row.Timing : SubtitleTiming.None, row.KeepPitch)
            : null);
    }

    // the target state of the video: its tracks, a sound written anew in place of its track, the sounds added
    private TargetState Build(MediaFile file, IReadOnlyDictionary<AudioRow, MediaTrack> written)
    {
        var tracks = new List<MediaTrack>();
        var replaced = new HashSet<int>();
        var rowOf = new Dictionary<int, AudioRow>();
        foreach (var track in file.Tracks)
        {
            tracks.Add(track);
            if (Rows.FirstOrDefault(r => !r.IsExternal && r.Track.Id == track.Id) is { } row)
            {
                var result = written[row];
                if (result.Id != track.Id)
                {
                    replaced.Add(track.Id);
                    tracks.Add(result);
                }

                rowOf[result.Id] = row;
            }
        }

        foreach (var row in Rows.Where(r => r.IsExternal))
        {
            tracks.Add(written[row]);
            rowOf[written[row].Id] = row;
        }

        var augmented = file with { Tracks = tracks };
        var removed = replaced.Concat(rowOf.Where(r => !r.Value.Include).Select(r => r.Key)).ToHashSet();
        var shifts = rowOf.Where(r => r.Value.Include && r.Value.Problem is null && r.Value.Recipe.Shift is { IsNone: false })
            .ToDictionary(r => r.Key, r => r.Value.Recipe.Shift);
        if (UseRules)
        {
            return UniversumRules.Apply(augmented, _services.Profile, new TrackDecisions
            {
                Languages = rowOf.Where(r => r.Value.Include).ToDictionary(r => r.Key, r => r.Value.Language.Tag),
                Removed = removed,
                Names = rowOf.Where(r => r.Value is { Include: true, WrittenName: not null }).ToDictionary(r => r.Key, r => r.Value.WrittenName!),
                Timings = shifts,
            });
        }

        var edits = tracks.Where(t => !removed.Contains(t.Id)).Select(t => rowOf.TryGetValue(t.Id, out var row)
            ? new TrackEdit(t.Id, row.WrittenName ?? row.InitialName, row.Language.Tag, t.Flags)
            : new TrackEdit(t.Id, t.Name, t.Language, t.Flags)).ToList();
        var target = ManualTarget.Create(augmented, edits, removed, title: "");
        return target with { Tracks = target.Tracks.Select(t => shifts.TryGetValue(t.TrackId, out var shift) ? t with { Timing = shift } : t).ToList() };
    }

    private bool CanSave() =>
        !_busy && _file is not null && Library.Placement is not null && Rows.All(r => r.Problem is null) && Blocked.Length == 0 &&
        (Target() is { HasChanges: true } || Rows.Any(r => r is { Include: true, Recipe.NeedsEncoding: true }));

    private async Task SaveAsync()
    {
        if (_file is not { } file || _services.CurrentMkvToolNix is not { } installation || Library.Placement is not { } placement)
        {
            return;
        }

        _busy = true;
        using var work = _work = new CancellationTokenSource();
        RefreshCommands();
        var folder = Path.Combine(Path.GetTempPath(), "UNIVERSUM", "zvuk-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var written = Rows.ToDictionary(r => r, r => r.Track);
            var encoded = Rows.Where(r => r is { Include: true, Recipe.NeedsEncoding: true }).ToList();
            var ffmpeg = encoded.Count == 0 ? "" : (await _services.SpeechAsync()).Ffmpeg;
            for (var i = 0; i < encoded.Count; i++)
            {
                var row = encoded[i];
                var output = Path.Combine(folder, $"{i + 1:00}.mka");
                var at = i;
                Result = $"Zapisujem zvuk {row.Source} ({i + 1} z {encoded.Count})…";
                var progress = new Progress<double>(share => Progress = (at + share) / (encoded.Count + 1));
                var problem = await AudioEncoder.EncodeAsync(_services.Runner, ffmpeg, row.ExternalPath ?? file.Path, row.Ordinal, row.Track, row.Recipe, output,
                    file.Duration, progress, work.Token);
                if (problem is not null)
                {
                    Result = $"Zvuk {row.Source} sa nepodarilo zapísať – nič sa nezapísalo. {problem}";
                    return;
                }

                var (codec, _) = row.Recipe.Encoder(row.Track);
                written[row] = row.Track with
                {
                    Id = row.IsExternal ? row.Track.Id : _nextId++, ExternalFile = output, ExternalTrack = 0, Codec = codec ?? row.Track.Codec,
                    AudioChannels = row.Stereo ? 2 : row.Track.AudioChannels, Number = null, Uid = null, StatisticsFrames = null, StatisticsBytes = null,
                };
            }

            Result = "Zapisujem nový súbor…";
            Progress = encoded.Count / (encoded.Count + 1.0);
            var result = await _services.Processor(installation).ProcessAsync(EditPlan.For(Build(file, written)), placement, new OutputOptions(OriginalHandling.Keep),
                cancellationToken: work.Token);
            Progress = 1;
            Result = result.Status switch
            {
                ProcessingStatus.Done => $"Uložené do knižnice: {result.OutputPath}. Originály ostali nezmenené.",
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
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a temporary folder left behind is harmless
            }

            _work = null;
            _busy = false;
            RefreshCommands();
        }
    }

    // the selected sound fitted to the film's: both read whole (once each), compared in many parts
    private async Task SyncAsync()
    {
        if (_file is not { } file || _selected is not { } row || _reference is not { } reference)
        {
            return;
        }

        _busy = true;
        _proposal = null;
        OnPropertyChanged(nameof(HasProposal));
        using var work = _work = new CancellationTokenSource();
        RefreshCommands();
        try
        {
            var ffmpeg = (await _services.SpeechAsync()).Ffmpeg;
            var film = await EnvelopeAsync(ffmpeg, file.Path, reference.Ordinal, file.Duration, "zvuk filmu", 0, work.Token);
            var other = film is null ? null : await EnvelopeAsync(ffmpeg, row.ExternalPath ?? file.Path, row.Ordinal, file.Duration, row.Source, 0.5, work.Token);
            if (film is null || other is null)
            {
                return;
            }

            SyncStatus = "Porovnávam zvuky…";
            var fps = file.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond;
            var result = await Task.Run(() => AudioSync.Align(film, other, fps, work.Token), work.Token);
            Progress = 1;
            if (result is null)
            {
                SyncStatus = "Zvuky sa nedajú porovnať (príliš krátke alebo tiché).";
            }
            else if (result.Reliable)
            {
                row.SetTiming(result.Timing, result.Rates);
                SyncStatus = $"Napasované: {result.Detail}. Overte prehraním.";
            }
            else if (result.Parts.Count(p => p.IsClear) >= 2)
            {
                _proposal = (row, result);
                SyncStatus = $"Neistý návrh: {result.Detail}. Použite ho tlačidlom Použiť návrh a overte prehraním.";
            }
            else
            {
                SyncStatus = "Zvuk sa k filmu nenašiel – zrejme iný strih alebo iný film.";
            }
        }
        catch (OperationCanceledException)
        {
            SyncStatus = "Zastavené.";
        }
        catch (Exception ex) when (ex is IOException or ProcessStartException or Core.Speech.SpeechException)
        {
            SyncStatus = "Nepodarilo sa: " + ex.Message;
        }
        finally
        {
            _work = null;
            _busy = false;
            OnPropertyChanged(nameof(HasProposal));
            RefreshCommands();
        }
    }

    private async Task<AudioEnvelope?> EnvelopeAsync(string ffmpeg, string path, int ordinal, TimeSpan? duration, string label, double from, CancellationToken cancellationToken)
    {
        if (_envelopes.TryGetValue((path, ordinal), out var known))
        {
            return known;
        }

        SyncStatus = $"Čítam {label} (celý)…";
        var progress = new Progress<double>(share => Progress = from + (share * 0.5));
        var (envelope, problem) = await AudioEnvelope.ReadAsync(ffmpeg, path, ordinal, duration, progress, cancellationToken);
        if (envelope is null)
        {
            SyncStatus = $"{label}: {problem}";
            return null;
        }

        _envelopes[(path, ordinal)] = envelope;
        return envelope;
    }

    private void RefreshCommands()
    {
        OpenVideoCommand?.NotifyCanExecuteChanged();
        AddSoundCommand?.NotifyCanExecuteChanged();
        RemoveCommand?.NotifyCanExecuteChanged();
        SaveCommand?.NotifyCanExecuteChanged();
        SyncCommand?.NotifyCanExecuteChanged();
        StopCommand?.NotifyCanExecuteChanged();
        ApplyProposalCommand?.NotifyCanExecuteChanged();
        ClearCommand?.NotifyCanExecuteChanged();
    }
}
