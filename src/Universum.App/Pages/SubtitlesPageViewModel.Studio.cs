using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Batch;
using Universum.Core.IO;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Playback;
using Universum.Core.Subtitles;

namespace Universum.App.Pages;

/// <summary>
/// Úprava titulkostopy (owner, 30. 10. 2026): the film plays with the subtitles being edited – every change of FPS,
/// speed and shift is seen at once – and with other subtitles to compare; the times are set while watching.
/// </summary>
public sealed partial class SubtitlesPageViewModel
{
    private SubtitleRow? _compareRow;
    private bool _compare;
    private string _liveStatus = "";
    private readonly HashSet<SubtitleRow> _reading = [];
    private (string Path, IReadOnlyList<MediaTrack> Externals, Func<StudioEdits, string?> Save, Action Leave)? _task;
    private bool _openingTask;
    private int _taskLoad;
    private int _quiet;
    private bool _hasWork;
    private string _workNote = "";

    /// <summary>The studio edits a file of Hromadné spracovanie (Upraviť v štúdiu): the changes go into the task.</summary>
    public bool IsTask => _task is not null;

    public string TaskText => _task is { } task
        ? $"Úloha Hromadného spracovania: {System.IO.Path.GetFileName(task.Path)}. Do úlohy sa uložia časy titulkov (FPS, rýchlosť, posun), ponechanie stôp, " +
          "ich jazyk, Forced a pridané súbory titulkov – súbor sa spracuje s ostatnými (aj v MEGA dávke). Názov stopy a príznak pre nepočujúcich dajú " +
          "pravidlá, opravy textu sa v úlohe nepoužijú."
        : "";

    /// <summary>Uložiť do úlohy: the edits go back to Hromadné spracovanie.</summary>
    public RelayCommand SaveToTaskCommand { get; private set; } = null!;

    /// <summary>Back to Hromadné spracovanie without saving.</summary>
    public RelayCommand LeaveTaskCommand { get; private set; } = null!;

    /// <summary>Uložiť prácu: the values of every track kept for this video, to go on later.</summary>
    public RelayCommand SaveWorkCommand { get; private set; } = null!;

    /// <summary>Forgets the saved work of this video (the page keeps its values).</summary>
    public RelayCommand DiscardWorkCommand { get; private set; } = null!;

    /// <summary>Work is saved for the open video.</summary>
    public bool HasWork { get => _hasWork; private set => SetProperty(ref _hasWork, value); }

    public string WorkNote { get => _workNote; private set => SetProperty(ref _workNote, value); }

    /// <summary>The player of the studio.</summary>
    public Player.PlayerViewModel Player { get; }

    /// <summary>The film is being opened in the player (the self-test waits for it).</summary>
    public Task PlayerOpening { get; private set; } = Task.CompletedTask;

    /// <summary>Show other subtitles at the top of the picture.</summary>
    public bool Compare
    {
        get => _compare;
        set
        {
            if (SetProperty(ref _compare, value))
            {
                RefreshPlayer();
            }
        }
    }

    /// <summary>The subtitles compared with (shown at the top, with their own changes).</summary>
    public SubtitleRow? CompareRow
    {
        get => _compareRow;
        set
        {
            if (SetProperty(ref _compareRow, value))
            {
                Compare = value is not null;
                RefreshPlayer();
            }
        }
    }

    /// <summary>What the last change while watching did.</summary>
    public string LiveStatus { get => _liveStatus; private set => SetProperty(ref _liveStatus, value); }

    /// <summary>The subtitle nearest to the moment starts now: the shift changes by the difference.</summary>
    public RelayCommand StartsNowCommand { get; }

    /// <summary>Synchronizácia podľa dvoch titulkov from the player: the first point (the nearest subtitle belongs now).</summary>
    public RelayCommand MarkFirstCommand { get; }

    /// <summary>…the second point, then the speed and the shift are computed.</summary>
    public RelayCommand MarkSecondCommand { get; }

    /// <summary>The player goes to a subtitle of the list (double click).</summary>
    public AsyncRelayCommand<SubtitlePreviewRow> SeekToCueCommand { get; }

    private void CreateTaskCommands()
    {
        SaveToTaskCommand = new RelayCommand(SaveToTask, () => !_busy && _task is not null);
        LeaveTaskCommand = new RelayCommand(() =>
        {
            var leave = _task?.Leave;
            EndTask();
            leave?.Invoke();
        }, () => _task is not null);
        SaveWorkCommand = new RelayCommand(SaveWork, () => !_busy && _file is not null && Rows.Count > 0 && _task is null);
        DiscardWorkCommand = new RelayCommand(() =>
        {
            if (_file is not null)
            {
                _services.StudioWork.Delete(_file.Path);
            }

            HasWork = false;
            WorkNote = "Uložená práca je zahodená – hodnoty na stránke ostali.";
            RefreshCommands();
        }, () => HasWork);
    }

    /// <summary>
    /// Upraviť v štúdiu (owner, 30. 10. 2026): a file of Hromadné spracovanie as the analysis has it (with the subtitle
    /// files found with it) and its edits so far; <paramref name="save"/> gets the edits (Uložiť do úlohy) and says why
    /// Hromadné spracovanie cannot take them (null: taken). False when
    /// the studio is busy (saving, syncing) – it says so.
    /// </summary>
    public async Task<bool> EditForBatchAsync(MediaFile analysed, StudioEdits? edits, Func<StudioEdits, string?> save, Action leave)
    {
        if (_busy)
        {
            LiveStatus = "Štúdio práve pracuje (ukladá, napasováva alebo sťahuje) – skúste Upraviť v štúdiu o chvíľu.";
            return false;
        }

        // a newer task (or another video opened) wins
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

        if (load != _taskLoad || _file is null || !Paths.Same(_file.Path, analysed.Path))
        {
            return false;
        }

        // the subtitle files the analysis found with the video, with its languages, and those added here before
        var externals = analysed.Tracks.Where(t => t.ExternalFile is not null).ToList();
        await AddFilesAsync([.. externals.Select(t => t.ExternalFile!).Concat(edits?.Added.Select(a => a.Path) ?? []).Where(File.Exists)]);
        if (load != _taskLoad || _file is null || !Paths.Same(_file.Path, analysed.Path))
        {
            return false;
        }

        _quiet++;
        try
        {
            foreach (var track in externals)
            {
                RowOf(track.ExternalFile!)?.Choose(track.Language);
            }

            if (edits is not null)
            {
                Apply(edits);
            }
        }
        finally
        {
            _quiet--;
            Changed();
        }

        _task = (analysed.Path, externals, save, leave);
        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(TaskText));
        RefreshCommands();
        return true;
    }

    // the edits of a task opened again: the rows' own values when they were saved, else the times and choices
    private void Apply(StudioEdits edits)
    {
        foreach (var file in edits.Added)
        {
            if (RowOf(file.Path) is { } row)
            {
                row.Choose(file.Language);
                row.Forced = file.Forced;
                row.HearingImpaired = file.HearingImpaired;
            }
        }

        foreach (var row in Rows)
        {
            var path = row.ExternalPath;
            if (edits.States.FirstOrDefault(s => path is null ? s.TrackId == row.Track.Id : s.ExternalFile is { } known && Paths.Same(known, path)) is { } state)
            {
                row.Restore(state);
                continue;
            }

            if (path is null)
            {
                if (edits.Timings.TryGetValue(row.Track.Id, out var timing))
                {
                    row.SetTiming(timing);
                }

                if (edits.Languages.TryGetValue(row.Track.Id, out var language))
                {
                    row.Choose(language);
                }

                row.Include = !edits.Removed.Contains(row.Track.Id);
                continue;
            }

            if (edits.FileTimings.FirstOrDefault(t => Paths.Same(t.Key, path)).Value is { } fileTiming)
            {
                row.SetTiming(fileTiming);
            }

            if (edits.FileLanguages.FirstOrDefault(l => Paths.Same(l.Key, path)) is { Key: not null } fileLanguage)
            {
                row.Choose(fileLanguage.Value);
            }

            row.Include = !edits.RemovedFiles.Any(r => Paths.Same(r, path));
        }
    }

    private SubtitleRow? RowOf(string externalFile) => Rows.FirstOrDefault(r => r.ExternalPath is { } path && Paths.Same(path, externalFile));

    private void SaveToTask()
    {
        if (_task is not { } task)
        {
            return;
        }

        if (_file is null || !Paths.Same(_file.Path, task.Path))
        {
            LiveStatus = "V štúdiu je iné video ako to z úlohy – otvorte ho znova cez Upraviť v štúdiu.";
            EndTask();
            return;
        }

        if (Rows.FirstOrDefault(r => r.Include && r.Problem is not null) is { } wrong)
        {
            LiveStatus = $"{wrong.Source}: {wrong.Problem}";
            return;
        }

        if (Rows.Any(r => r.Include && (r.RemoveHearingImpaired || r.RemoveFormatting || r.FixTimes || (!r.IsExternal && r.ToSrt))))
        {
            LiveStatus = "Opravy textu (konverzia stopy, prvky pre nepočujúcich, formátovanie, prekrývanie) sa v úlohe nepoužijú – zrušte ich, " +
                         "alebo uložte video hneď tlačidlom Uložiť.";
            return;
        }

        var edits = new StudioEdits { States = [.. Rows.Select(r => r.State())] };

        // a file of the analysis taken off the list (or gone from the disk) is left out
        edits.RemovedFiles.AddRange(task.Externals.Select(t => t.ExternalFile!).Where(p => RowOf(p) is null));
        foreach (var row in Rows)
        {
            var timing = row.Timing;
            if (row.ExternalPath is not { } path)
            {
                if (!row.Include)
                {
                    edits.Removed.Add(row.Track.Id);
                }

                if (!timing.IsNone)
                {
                    edits.Timings[row.Track.Id] = timing;
                }

                if (!row.Language.Same(row.Track.Language))
                {
                    edits.Languages[row.Track.Id] = row.Language.Tag;
                }

                if (row.Forced != row.InitialForced)
                {
                    edits.Forced[row.Track.Id] = row.Forced;
                }

                continue;
            }

            if (task.Externals.FirstOrDefault(t => Paths.Same(t.ExternalFile!, path)) is { } found)
            {
                if (!row.Include)
                {
                    edits.RemovedFiles.Add(path);
                }

                if (!row.Language.Same(found.Language))
                {
                    edits.FileLanguages[path] = row.Language.Tag;
                }

                if (row.Forced != row.InitialForced)
                {
                    edits.FileForced[path] = row.Forced;
                }
            }
            else if (row.Include)
            {
                edits.Added.Add(new StudioFile(path, row.Language.Tag, row.Forced, row.HearingImpaired, row.Track.CodecId ?? "", row.Track.Codec,
                    row.Track.IsTextSubtitles, row.Track.StatisticsFrames));
            }

            if (!timing.IsNone && row.Include)
            {
                edits.FileTimings[path] = timing;
            }
        }

        // the task stays open when Hromadné spracovanie cannot take the edits now (it runs, it ran)
        if (task.Save(edits) is { } refused)
        {
            LiveStatus = refused;
            return;
        }

        EndTask();
    }

    private void EndTask()
    {
        _task = null;
        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(TaskText));
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
            var work = _services.StudioWork.Save(_file.Path, Rows.Select(r => r.State()).ToList(), DateTime.UtcNow);
            HasWork = true;
            WorkNote = $"Práca je uložená ({work.SavedUtc.ToLocalTime():d. M. yyyy H:mm}) – pri ďalšom otvorení tohto videa sa obnoví.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WorkNote = "Prácu sa nepodarilo uložiť: " + ex.Message;
        }

        RefreshCommands();
    }

    // the work saved for the video opened (not when a task of Hromadné spracovanie opens it)
    private async Task RestoreWorkAsync()
    {
        HasWork = false;
        WorkNote = "";
        if (_openingTask || _file is null || _services.StudioWork.Load(_file.Path) is not { } work)
        {
            return;
        }

        var video = _file.Path;
        await AddFilesAsync([.. work.Tracks.Select(t => t.ExternalFile).OfType<string>().Where(p => File.Exists(p) && RowOf(p) is null)]);
        if (_file is null || !Paths.Same(_file.Path, video))
        {
            return;
        }

        _quiet++;
        try
        {
            foreach (var state in work.Tracks)
            {
                var row = state.TrackId is { } id ? Rows.FirstOrDefault(r => !r.IsExternal && r.Track.Id == id) : state.ExternalFile is { } path ? RowOf(path) : null;
                row?.Restore(state);
            }
        }
        finally
        {
            _quiet--;
            Changed();
        }

        HasWork = true;
        WorkNote = $"Obnovená uložená práca z {work.SavedUtc.ToLocalTime():d. M. yyyy H:mm}.";
        RefreshCommands();
    }

    // the subtitles of the player: the selected ones as they will be, and the ones compared with
    private void RefreshPlayer()
    {
        if (Player is null)
        {
            return;
        }

        Player.SetSubtitles(Timeline(_selected), Compare && _compareRow != _selected ? Timeline(_compareRow) : SubtitleTimeline.Empty);
    }

    private SubtitleTimeline Timeline(SubtitleRow? row)
    {
        if (row is null || !Rows.Contains(row))
        {
            return SubtitleTimeline.Empty;
        }

        if (row.Document is null)
        {
            ReadTrack(row);
            return SubtitleTimeline.Empty;
        }

        var timing = row.Problem is null ? row.Timing : SubtitleTiming.None;
        return row.Repaired(row.Document) is { } document ? new SubtitleTimeline(timing.Apply(document).Cues) : SubtitleTimeline.Empty;
    }

    // a text track of the video is read once for the player (mkvextract reads the whole file); pictures (PGS) are not drawn
    private void ReadTrack(SubtitleRow row)
    {
        if (!row.IsText)
        {
            LiveStatus = $"Stopa {row.Track.Id} má obrázkové titulky ({row.Format}) – prehrávač ich zatiaľ nezobrazí; časy sa uložia správne.";
            return;
        }

        if (_file is not { IsMatroska: true } file || _services.CurrentMkvToolNix is not { } installation || !_reading.Add(row))
        {
            return;
        }

        LiveStatus = $"Načítavam titulky stopy {row.Track.Id} pre prehrávač (číta sa celý súbor)…";
        _ = ReadTrackAsync(row, file, installation);
    }

    private async Task ReadTrackAsync(SubtitleRow row, Core.Media.MediaFile file, MkvToolNixInstallation installation)
    {
        try
        {
            var document = await SubtitleText.ExtractDocumentAsync(installation, _services.Runner, file, row.Track);
            if (ReferenceEquals(_file, file) && document is not null)
            {
                row.Document ??= document;
                LiveStatus = $"Titulky stopy {row.Track.Id} sú načítané ({document.Cues.Count}).";
                UpdatePreview();
                RefreshPlayer();
            }
            else if (document is null)
            {
                _reading.Remove(row);
                LiveStatus = $"Titulky stopy {row.Track.Id} sa nepodarilo prečítať.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MkvToolNixException or Core.Processes.ProcessStartException or TimeoutException)
        {
            // tried again when the track is chosen again
            _reading.Remove(row);
            LiveStatus = $"Titulky stopy {row.Track.Id} sa nepodarilo prečítať: {ex.Message}";
        }
    }

    private void StartsNow()
    {
        if (_selected is not { } row || Player.Primary.Nearest(Player.Now) is not { } cue)
        {
            LiveStatus = "Pri prehrávači nie je žiadny titulok vybraných titulkov.";
            return;
        }

        var now = Player.Now;
        var by = (long)Math.Round((now - cue.Start).TotalMilliseconds);
        row.DelayText = (row.DelayMilliseconds + by).ToString(CultureInfo.InvariantCulture);
        LiveStatus = $"Posun {(by >= 0 ? "+" : "−")}{Math.Abs(by).ToString("#,0", CultureInfo.GetCultureInfo("sk-SK"))} ms: „{Short(cue.Text)}“ začína v {SubtitleRow.Clock(now)} " +
                     $"(posun spolu {row.DelayMilliseconds.ToString("#,0", CultureInfo.GetCultureInfo("sk-SK"))} ms).";
    }

    private void MarkPoint(bool first)
    {
        if (_selected is not { } row || Player.Primary.Nearest(Player.Now) is not { } cue || Original(row, cue) is not { } original)
        {
            LiveStatus = "Pri prehrávači nie je žiadny titulok vybraných titulkov.";
            return;
        }

        var now = SubtitleRow.Clock(Player.Now);
        if (first)
        {
            row.FirstNow = SubtitleRow.Clock(original);
            row.FirstTarget = now;
            LiveStatus = $"1. bod: „{Short(cue.Text)}“ ({row.FirstNow}) patrí do {now}. Pustite film ďalej a pri titulku ku koncu dajte 2. bod.";
        }
        else
        {
            row.SecondNow = SubtitleRow.Clock(original);
            row.SecondTarget = now;
            row.TwoPointsCommand.Execute(null);
            LiveStatus = $"2. bod: „{Short(cue.Text)}“ ({row.SecondNow}) patrí do {now}. {row.SyncNote}";
        }
    }

    // the time a subtitle shown by the player has in the subtitles themselves (before the changes)
    private static TimeSpan? Original(SubtitleRow row, SubtitleCue shown)
    {
        var timing = row.Problem is null ? row.Timing : SubtitleTiming.None;
        return row.Repaired(row.Document)?.Cues.FirstOrDefault(c => c.Text == shown.Text && (timing.Apply(c.Start) - shown.Start).Duration() < TimeSpan.FromMilliseconds(2))?.Start;
    }

    private async Task SeekToCueAsync(SubtitlePreviewRow? row)
    {
        if (row?.At is { } at && Player.IsOpen)
        {
            await Player.SeekAsync(at - TimeSpan.FromMilliseconds(300));
        }
    }

    private static string Short(string text)
    {
        var plain = SubtitleCleanup.WithoutTags(text).Replace('\n', ' ');
        return plain.Length > 50 ? plain[..47] + "…" : plain;
    }
}
