using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Localization;
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
        // not MPEG-TS: mkvmerge lists the AC-3 core of TrueHD as a track of its own, FFmpeg does not – the tracks would not match
        [".mka", ".ac3", ".eac3", ".ec3", ".dts", ".dtshd", ".thd", ".truehd", ".aac", ".m4a", ".mp3", ".flac", ".opus", ".ogg", ".wav", ".mkv", ".mp4", ".m4v", ".avi"],
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
    private string _summary = Loc.T("Otvorte video – jeho zvukové stopy a zvuky odinakiaľ (dabing, iné vydanie) sa prehrajú s obrazom.");
    private string _result = "";
    private string _syncStatus = "";
    private double _progress;
    private CancellationTokenSource? _work;
    private (AudioRow Row, AudioSyncResult Result)? _proposal;
    private (string Path, Func<StudioEdits, string?> Save, Action Leave)? _task;
    private Dictionary<int, string> _batchNames = [];
    private int _taskLoad;
    private bool _openingTask;
    private bool _hasWork;
    private string _workNote = "";

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
                SyncStatus = Loc.F("Návrh použitý: {0}. Overte ho prehraním.", proposal.Result.Detail);
                _proposal = null;
                OnPropertyChanged(nameof(HasProposal));
                RefreshCommands();
            }
        }, () => !_busy && _proposal is not null);
        ClearCommand = new RelayCommand(() =>
        {
            Clear();
            FileName = "";
            Summary = Loc.T("Otvorte video – jeho zvukové stopy a zvuky odinakiaľ (dabing, iné vydanie) sa prehrajú s obrazom.");
            RefreshCommands();
        }, () => !_busy && _file is not null);
        SaveToTaskCommand = new RelayCommand(SaveToTask, () => !_busy && _task is not null);
        LeaveTaskCommand = new RelayCommand(() =>
        {
            var leave = _task?.Leave;
            EndTask();
            leave?.Invoke();
        }, () => _task is not null);
        SaveWorkCommand = new RelayCommand(SaveWork, () => !_busy && _file is not null && _task is null);
        DiscardWorkCommand = new RelayCommand(() =>
        {
            if (_file is not null)
            {
                _services.AudioWork.Delete(_file.Path);
                HasWork = false;
                WorkNote = Loc.T("Uložená práca je zahodená – hodnoty na stránke ostali.");
                RefreshCommands();
            }
        }, () => _hasWork && _task is null);
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

    /// <summary>Uložiť do úlohy: the sounds' edits go back to Hromadné spracovanie (a file opened with Zvuk v štúdiu).</summary>
    public RelayCommand SaveToTaskCommand { get; }

    /// <summary>Back to Hromadné spracovanie without saving.</summary>
    public RelayCommand LeaveTaskCommand { get; }

    /// <summary>Uložiť prácu: the values of every sound of the video come back when it is opened again.</summary>
    public RelayCommand SaveWorkCommand { get; }

    public RelayCommand DiscardWorkCommand { get; }

    /// <summary>A file of Hromadné spracovanie is edited (its changes go back into the task).</summary>
    public bool IsTask => _task is not null;

    public string TaskText => _task is { } task
        ? Loc.F("Úloha Hromadného spracovania: {0}. Zmeny zvuku (posun, zmena FPS, kodek, hlasitosť, jazyk, názov, pridaný dabing) sa uložia do úlohy – zapíšu sa pri jej behu.", Path.GetFileName(task.Path))
        : "";

    /// <summary>Work is saved for the video.</summary>
    public bool HasWork { get => _hasWork; private set => SetProperty(ref _hasWork, value); }

    public string WorkNote { get => _workNote; private set => SetProperty(ref _workNote, value); }

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
        : Loc.T("Pravidlá UNIVERSUM: ") + string.Join(" ", target.Issues.Where(i => i.IsBlocking).Select(i => i.Kind switch
        {
            RuleIssueKind.UndeterminedLanguage => _file.Tracks.FirstOrDefault(t => t.Id == i.TrackId)?.Type == TrackType.Subtitles
                ? Loc.F("titulková stopa {0} má neurčený jazyk (určte ho v Úprave titulkostopy alebo hlavičky).", i.TrackId)
                : Loc.F("stopa {0} má neurčený jazyk.", i.TrackId),
            RuleIssueKind.MultipleVideoTracks => Loc.T("súbor má viac video stôp."),
            RuleIssueKind.NoAudio => Loc.T("súbor nemá zvukovú stopu."),
            _ => i.Kind.ToString(),
        }).Distinct()) + Loc.T(" Určte jazyk – alebo vypnite pravidlá (stopy ostanú, ako sú).");

    /// <summary>Nothing is fitted or written now: the sounds can be changed.</summary>
    public bool IsIdle => !_busy;

    /// <summary>A video, then sounds to add (drag and drop).</summary>
    public async Task LoadAsync(IReadOnlyList<string> paths)
    {
        if (_busy)
        {
            Result = Loc.T("Práve sa napasováva alebo zapisuje – súbory pridajte potom.");
            return;
        }

        var video = paths.FirstOrDefault(p => BatchAnalyzer.VideoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
        if (_file is null || (video is not null && !paths.All(p => SoundExtensions.Contains(Path.GetExtension(p)))))
        {
            if (video is null)
            {
                Summary = Loc.T("Najprv otvorte video – zvuky sa pridávajú k nemu.");
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
    public TargetState? Target() => _file is null ? null : Build(_file, Rows.ToDictionary(r => r, r => r.Track), [.. Rows]);

    private async Task OpenVideoAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Video|*.mkv;*.mp4;*.m4v;*.avi;*.ts;*.m2ts;*.mov;*.webm|" + Loc.T("Všetky súbory") + "|*.*" };
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
            Filter = Loc.T("Zvuk a filmy") + "|" + string.Join(";", SoundExtensions.Select(e => "*" + e)) + "|" + Loc.T("Všetky súbory") + "|*.*",
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
            Summary = Loc.T("MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.");
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
                Rows.Add(new AudioRow(track, await _services.AudioOrdinalAsync(path, track, ordinal++), null, Changed));
            }

            Reference = Rows.FirstOrDefault(r => r.Track.Flags.Default) ?? Rows.FirstOrDefault();
            SelectedRow = Reference;
            OnPropertyChanged(nameof(References));
            Summary = Loc.F("{0}, zvukové stopy: {1}", _file.ContainerName, Rows.Count) +
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

        if (!_openingTask)
        {
            await RestoreWorkAsync();
        }
    }

    /// <summary>
    /// Zvuk v štúdiu: a file of Hromadné spracovanie (as analysed) with its edits so far; <paramref name="save"/> takes the
    /// edits (null when taken, else why not), <paramref name="leave"/> goes back. False when it could not be opened.
    /// </summary>
    /// <param name="batchNames">The names the batch gives the video's own tracks (by track ID): shown until the user writes his own.</param>
    public async Task<bool> EditForBatchAsync(MediaFile analysed, StudioEdits? edits, Func<StudioEdits, string?> save, Action leave, IReadOnlyDictionary<int, string>? batchNames = null)
    {
        if (_busy)
        {
            SyncStatus = Loc.T("Štúdio práve pracuje (napasováva alebo zapisuje) – skúste Zvuk v štúdiu o chvíľu.");
            return false;
        }

        var load = ++_taskLoad;
        _openingTask = true;
        try
        {
            await LoadVideoAsync(analysed.Path);
        }
        finally
        {
            if (load == _taskLoad)
            {
                _openingTask = false;
            }
        }

        if (load != _taskLoad || _file is null || !Core.IO.Paths.Same(_file.Path, analysed.Path))
        {
            return false;
        }

        _batchNames = batchNames?.ToDictionary() ?? [];
        if (edits is not null)
        {
            await RestoreAsync(edits.SoundStates, edits);
            if (load != _taskLoad)
            {
                return false;
            }
        }

        _task = (analysed.Path, save, leave);
        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(TaskText));
        Changed();
        return true;
    }

    // the sounds as they were left: the files they were added from again (when they are still there), then the values
    // of each row; a task without the values as typed gets its decisions
    private async Task RestoreAsync(IReadOnlyList<AudioTrackState> states, StudioEdits? edits)
    {
        var files = states.Select(s => s.ExternalFile).Concat(edits?.AddedSounds.Select(s => s.Path) ?? []).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).Where(p => !Rows.Any(r => r.ExternalPath is { } known && Core.IO.Paths.Same(known, p))).ToList();
        if (files.Count > 0)
        {
            await AddFilesAsync(files);
        }

        foreach (var row in Rows)
        {
            if (states.FirstOrDefault(row.Is) is { } state)
            {
                row.Restore(state);
            }
            else if (edits is not null && states.Count == 0)
            {
                if (!row.IsExternal)
                {
                    row.Include = !edits.RemovedSounds.Contains(row.Track.Id);
                    if (edits.SoundLanguages.TryGetValue(row.Track.Id, out var language))
                    {
                        row.Language = row.LanguageChoices.FirstOrDefault(c => c.Same(language)) ?? row.Language;
                    }

                    if (edits.SoundNames.TryGetValue(row.Track.Id, out var name))
                    {
                        row.WriteName(name);
                    }

                    if (edits.SoundRecipes.TryGetValue(row.Track.Id, out var recipe))
                    {
                        row.SetRecipe(recipe);
                    }
                }
                else if (edits.AddedSounds.FirstOrDefault(s => s.Is(row.Track)) is { } sound)
                {
                    row.Language = row.LanguageChoices.FirstOrDefault(c => c.Same(sound.Language)) ?? row.Language;
                    if (sound.Name is not null)
                    {
                        row.WriteName(sound.Name);
                    }

                    row.SetRecipe(sound.Recipe);
                }
                else
                {
                    row.Include = false;
                }
            }
            else if (row.IsExternal && files.Any(f => Core.IO.Paths.Same(f, row.ExternalPath!)))
            {
                // another sound of a file added for one of its sounds
                row.Include = false;
            }
        }

        SelectedRow = Rows.LastOrDefault(r => r is { IsExternal: true, Include: true }) ?? SelectedRow;
        Changed();
    }

    private async Task RestoreWorkAsync()
    {
        HasWork = false;
        WorkNote = "";
        if (_file is null || _services.AudioWork.Load(_file.Path) is not { } work)
        {
            return;
        }

        await RestoreAsync(work.Sounds, null);
        HasWork = true;
        WorkNote = Loc.F("Obnovená uložená práca z {0:d. M. yyyy H:mm}.", work.SavedUtc.ToLocalTime());
        RefreshCommands();
    }

    private void SaveWork()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            var work = _services.AudioWork.Save(new AudioWork { Video = _file.Path, SavedUtc = DateTime.UtcNow, Sounds = [.. Rows.Select(r => r.State())] });
            HasWork = true;
            WorkNote = Loc.F("Práca je uložená ({0:d. M. yyyy H:mm}) – pri ďalšom otvorení tohto videa sa obnoví.", work.SavedUtc.ToLocalTime());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WorkNote = Loc.T("Prácu sa nepodarilo uložiť: ") + ex.Message;
        }

        RefreshCommands();
    }

    private void SaveToTask()
    {
        if (_task is not { } task || _file is null)
        {
            return;
        }

        if (!Core.IO.Paths.Same(_file.Path, task.Path))
        {
            SyncStatus = Loc.T("V štúdiu je iné video ako to z úlohy – otvorte ho znova cez Zvuk v štúdiu.");
            EndTask();
            return;
        }

        if (Rows.FirstOrDefault(r => r.Include && r.Problem is not null) is { } wrong)
        {
            SyncStatus = $"{wrong.Source}: {wrong.Problem}";
            return;
        }

        var edits = new StudioEdits { SoundStates = [.. Rows.Select(r => r.State())] };
        foreach (var row in Rows)
        {
            var recipe = row.Recipe;
            if (row.IsExternal)
            {
                if (row.Include)
                {
                    edits.AddedSounds.Add(new StudioSound(row.ExternalPath!, row.Ordinal, row.Track, row.Language.Tag, row.WrittenName, recipe));
                }

                continue;
            }

            if (!row.Include)
            {
                edits.RemovedSounds.Add(row.Track.Id);
                continue;
            }

            if (!row.Language.Same(row.Track.Language))
            {
                edits.SoundLanguages[row.Track.Id] = row.Language.Tag;
            }

            if (row.WrittenName is { } name)
            {
                edits.SoundNames[row.Track.Id] = name;
            }

            if (!recipe.Timing.IsNone || recipe.NeedsEncoding)
            {
                edits.SoundRecipes[row.Track.Id] = recipe;
            }
        }

        // the task stays open when Hromadné spracovanie cannot take them now (the subtitles' part is kept by the batch)
        if (task.Save(edits) is { } refused)
        {
            SyncStatus = refused;
            return;
        }

        EndTask();
    }

    private void EndTask()
    {
        _task = null;
        _batchNames = [];
        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(TaskText));
        Changed();
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
            // a file whose sounds are here already (a saved work restored with the video dropped with it) once
            foreach (var path in paths.Where(p => !Rows.Any(r => r.ExternalPath is { } known && Core.IO.Paths.Same(known, p))))
            {
                try
                {
                    var file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
                    var ordinal = 0;
                    var added = 0;
                    foreach (var track in file.TracksOf(TrackType.Audio))
                    {
                        // a sound of another container has no Matroska codec ID: by its codec's name (the check of the result compares it)
                        var row = new AudioRow(track with
                        {
                            Id = _nextId++, ExternalFile = path, ExternalTrack = track.Id, Flags = track.Flags with { Default = false }, CodecId = AudioRecipe.CodecIdOf(track),
                        }, await _services.AudioOrdinalAsync(path, track, ordinal++), path, Changed);
                        Rows.Add(row);
                        SelectedRow = row;
                        added++;
                    }

                    if (added == 0)
                    {
                        problems.Add(Loc.F("{0}: nemá zvuk", Path.GetFileName(path)));
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
            Result = problems.Count == 0 ? "" : Loc.T("Nepridané: ") + string.Join("; ", problems);
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
        if (_task is not null)
        {
            // another video: the task of Hromadné spracovanie is left (its file is not here any more)
            _task = null;
                OnPropertyChanged(nameof(IsTask));
            OnPropertyChanged(nameof(TaskText));
        }

        _file = null;
        _batchNames = [];
        HasWork = false;
        WorkNote = "";
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

    // the names the tracks would get without the user's (the rules' or, in a task, the batch's) shown in their rows
    private void ShowNames()
    {
        if (_file is null)
        {
            return;
        }

        var target = Build(_file, Rows.ToDictionary(r => r, r => r.Track), [.. Rows], names: false);

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
            ? new PlayerSound(row.ExternalPath ?? _file.Path, row.Ordinal, row.Timing.Factor is >= 0.5 and <= 2 ? row.Timing : SubtitleTiming.None, row.KeepPitch)
            : null);
    }

    // the target state of the video: its tracks, a sound written anew in place of its track, the sounds added
    // names: with the names the user wrote (false: what the tracks would be called without them)
    private TargetState Build(MediaFile file, IReadOnlyDictionary<AudioRow, MediaTrack> written, IReadOnlyList<AudioRow> rows, bool names = true)
    {
        var tracks = new List<MediaTrack>();
        var replaced = new HashSet<int>();
        var rowOf = new Dictionary<int, AudioRow>();
        foreach (var track in file.Tracks)
        {
            tracks.Add(track);
            if (rows.FirstOrDefault(r => !r.IsExternal && r.Track.Id == track.Id) is { } row)
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

        foreach (var row in rows.Where(r => r.IsExternal))
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
                Names = NamesOf(rowOf, names),
                Timings = shifts,
            });
        }

        var edits = tracks.Where(t => !removed.Contains(t.Id)).Select(t => rowOf.TryGetValue(t.Id, out var row)
            ? new TrackEdit(t.Id, (names ? row.WrittenName : null) ?? row.InitialName, row.Language.Tag, row.IsExternal ? t.Flags : row.Track.Flags)
            : new TrackEdit(t.Id, t.Name, t.Language, t.Flags)).ToList();
        var target = ManualTarget.Create(augmented, edits, removed, title: "");
        return target with { Tracks = target.Tracks.Select(t => shifts.TryGetValue(t.TrackId, out var shift) ? t with { Timing = shift } : t).ToList() };
    }

    // the names of the result: the batch's for the video's own tracks (a task), the ones the user wrote over them
    private Dictionary<int, string> NamesOf(Dictionary<int, AudioRow> rowOf, bool written)
    {
        var names = rowOf.Where(r => r.Value is { Include: true, IsExternal: false } && _batchNames.ContainsKey(r.Value.Track.Id))
            .ToDictionary(r => r.Key, r => _batchNames[r.Value.Track.Id]);
        if (written)
        {
            foreach (var (id, row) in rowOf.Where(r => r.Value is { Include: true, WrittenName: not null }))
            {
                names[id] = row.WrittenName!;
            }
        }

        return names;
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
        try
        {
            // the sounds as they are now (the page cannot change them while it writes); a sound written anew is written by
            // the processor right before mkvmerge (as in Hromadné spracovanie)
            var rows = Rows.ToList();
            var written = rows.ToDictionary(r => r, r => r.Include && r.Recipe.NeedsEncoding
                ? new SoundEncoding(r.ExternalPath ?? file.Path, r.Ordinal, r.Track, r.Recipe).ToTrack(r.IsExternal ? r.Track.Id : _nextId++)
                : r.Track);
            Result = rows.Any(r => r is { Include: true, Recipe.NeedsEncoding: true }) ? Loc.T("Zapisujem zvuk znova a potom nový súbor…") : Loc.T("Zapisujem nový súbor…");
            var plan = EditPlan.For(Build(file, written, rows));
            var started = DateTime.UtcNow;
            var result = await _services.Processor(installation).ProcessAsync(plan, placement, new OutputOptions(OriginalHandling.Keep),
                new Progress<double>(share => Progress = share), work.Token);
            _services.LogProcessing("Úprava audiostopy", plan, result, started);
            Progress = 1;
            Result = result.Status switch
            {
                ProcessingStatus.Done => Loc.F("Uložené do knižnice: {0}. Originály ostali nezmenené.", result.OutputPath),
                ProcessingStatus.AlreadyInLibrary => Loc.F("Rovnaký výsledok už v knižnici je: {0}. Nič sa nezapísalo.", result.OutputPath),
                _ => Loc.T("Nepodarilo sa – nič sa nezmenilo.") + "\n" + string.Join("\n", result.Messages),
            };
        }
        catch (OperationCanceledException)
        {
            Result = Loc.T("Zastavené – nič sa nezapísalo.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MkvToolNixException or ProcessStartException or TimeoutException or Core.Speech.SpeechException)
        {
            Result = Loc.T("Nepodarilo sa: ") + ex.Message;
        }
        finally
        {
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
            var film = await EnvelopeAsync(ffmpeg, file.Path, reference.Ordinal, file.Duration, Loc.T("zvuk filmu"), 0, work.Token);
            var other = film is null ? null : await EnvelopeAsync(ffmpeg, row.ExternalPath ?? file.Path, row.Ordinal, file.Duration, row.Source, 0.5, work.Token);
            if (film is null || other is null)
            {
                return;
            }

            SyncStatus = Loc.T("Porovnávam zvuky…");
            var fps = file.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond;
            var result = await Task.Run(() => AudioSync.Align(film, other, fps, work.Token), work.Token);
            Progress = 1;
            if (result is null)
            {
                SyncStatus = Loc.T("Zvuky sa nedajú porovnať (príliš krátke alebo tiché).");
            }
            else if (result.Reliable)
            {
                row.SetTiming(result.Timing, result.Rates);
                SyncStatus = Loc.F("Napasované: {0}. Overte prehraním.", result.Detail);
            }
            else if (result.Parts.Count(p => p.IsClear) >= 2)
            {
                _proposal = (row, result);
                SyncStatus = Loc.F("Neistý návrh: {0}. Použite ho tlačidlom Použiť návrh a overte prehraním.", result.Detail);
            }
            else
            {
                SyncStatus = Loc.T("Zvuk sa k filmu nenašiel – zrejme iný strih alebo iný film.");
            }
        }
        catch (OperationCanceledException)
        {
            SyncStatus = Loc.T("Zastavené.");
        }
        catch (Exception ex) when (ex is IOException or ProcessStartException or Core.Speech.SpeechException)
        {
            SyncStatus = Loc.T("Nepodarilo sa: ") + ex.Message;
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

        SyncStatus = Loc.F("Čítam {0} (celý)…", label);
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
        OnPropertyChanged(nameof(IsIdle));
        OpenVideoCommand?.NotifyCanExecuteChanged();
        AddSoundCommand?.NotifyCanExecuteChanged();
        RemoveCommand?.NotifyCanExecuteChanged();
        SaveCommand?.NotifyCanExecuteChanged();
        SyncCommand?.NotifyCanExecuteChanged();
        StopCommand?.NotifyCanExecuteChanged();
        ApplyProposalCommand?.NotifyCanExecuteChanged();
        ClearCommand?.NotifyCanExecuteChanged();
        SaveToTaskCommand?.NotifyCanExecuteChanged();
        LeaveTaskCommand?.NotifyCanExecuteChanged();
        SaveWorkCommand?.NotifyCanExecuteChanged();
        DiscardWorkCommand?.NotifyCanExecuteChanged();
    }
}
