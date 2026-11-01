using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Batch;
using Universum.Core.IO;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Playback;
using Universum.Core.Subtitles;
using Universum.Core.Subtitles.Pictures;

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

    // a task of Hromadné spracovanie: the names the batch gives the video's own tracks
    private Dictionary<int, string> _batchNames = [];

    // ends the reading of picture subtitles of a video closed
    private CancellationTokenSource _readingStop = new();

    // the picture subtitles (PGS) read for the player, by row
    private readonly Dictionary<SubtitleRow, IReadOnlyList<PictureCue>> _pictures = [];
    private (string Path, IReadOnlyList<MediaTrack> Externals, Func<StudioEdits, string?> Save, Action Leave)? _task;
    private bool _openingTask;
    private int _taskLoad;
    private int _quiet;
    private bool _hasWork;
    private string _workNote = "";

    /// <summary>The studio edits a file of Hromadné spracovanie (Upraviť v štúdiu): the changes go into the task.</summary>
    public bool IsTask => _task is not null;

    public string TaskText => _task is { } task
        ? Loc.F("Úloha Hromadného spracovania: {0}. Do úlohy sa uložia časy titulkov (FPS, rýchlosť, posun), ponechanie stôp, ich jazyk, Forced a pridané súbory titulkov – súbor sa spracuje s ostatnými (aj v MEGA dávke). Názov stopy a príznak pre nepočujúcich dajú pravidlá, opravy textu sa v úlohe nepoužijú.",
            System.IO.Path.GetFileName(task.Path))
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

    /// <summary>Leaves the subtitle of the list out (Časy titulkov, the menu of the right button).</summary>
    public RelayCommand<SubtitlePreviewRow> RemoveCueCommand { get; private set; } = null!;

    /// <summary>Undoes every change of single subtitles of the selected ones.</summary>
    public RelayCommand UndoCueEditsCommand { get; private set; } = null!;

    /// <summary>The player goes to a subtitle of the list (double click).</summary>
    public AsyncRelayCommand<SubtitlePreviewRow> SeekToCueCommand { get; }

    private void CreateTaskCommands()
    {
        RemoveCueCommand = new RelayCommand<SubtitlePreviewRow>(cue =>
        {
            if (cue?.Index is { } index && _selected is { } row)
            {
                row.EditCue(index, null);
            }
        }, cue => cue?.IsEditable == true);
        UndoCueEditsCommand = new RelayCommand(() => _selected?.SetCueEdits(null), () => _selected?.TextEdited == true);
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
            WorkNote = Loc.T("Uložená práca je zahodená – hodnoty na stránke ostali.");
            RefreshCommands();
        }, () => HasWork);
    }

    /// <summary>
    /// Upraviť v štúdiu (owner, 30. 10. 2026): a file of Hromadné spracovanie as the analysis has it (with the subtitle
    /// files found with it) and its edits so far; <paramref name="save"/> gets the edits (Uložiť do úlohy) and says why
    /// Hromadné spracovanie cannot take them (null: taken). False when
    /// the studio is busy (saving, syncing) – it says so.
    /// </summary>
    /// <param name="batchNames">The names the batch gives the video's own tracks (by track ID): shown until the user writes his own.</param>
    public async Task<bool> EditForBatchAsync(MediaFile analysed, StudioEdits? edits, Func<StudioEdits, string?> save, Action leave,
        IReadOnlyDictionary<int, string>? batchNames = null)
    {
        if (_busy)
        {
            LiveStatus = Loc.T("Štúdio práve pracuje (ukladá, napasováva alebo sťahuje) – skúste Upraviť v štúdiu o chvíľu.");
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

        _batchNames = batchNames?.ToDictionary() ?? [];
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

                if (edits.Names.TryGetValue(row.Track.Id, out var name))
                {
                    row.WriteName(name);
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

            if (edits.FileNames.FirstOrDefault(n => Paths.Same(n.Key, path)).Value is { } fileName)
            {
                row.WriteName(fileName);
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
            LiveStatus = Loc.T("V štúdiu je iné video ako to z úlohy – otvorte ho znova cez Upraviť v štúdiu.");
            EndTask();
            return;
        }

        if (Rows.FirstOrDefault(r => r.Include && r.Problem is not null) is { } wrong)
        {
            LiveStatus = $"{wrong.Source}: {wrong.Problem}";
            return;
        }

        if (Rows.Any(r => r.Include && (r.RemoveHearingImpaired || r.RemoveFormatting || r.FixTimes || r.TextEdited || (!r.IsExternal && r.ToSrt))))
        {
            LiveStatus = Loc.T("Opravy textu (zmenené titulky, konverzia stopy, prvky pre nepočujúcich, formátovanie, prekrývanie) sa v úlohe nepoužijú – zrušte ich, alebo uložte video hneď tlačidlom Uložiť.");
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

                if (row.WrittenName is { } name)
                {
                    edits.Names[row.Track.Id] = name;
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

            if (row.WrittenName is { } fileName && row.Include)
            {
                edits.FileNames[path] = fileName;
            }
        }

        // the task stays open when Hromadné spracovanie cannot take the edits now (it runs, it ran); the sounds' edits stay
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
        _batchNames = [];
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
            WorkNote = Loc.F("Práca je uložená ({0:d. M. yyyy H:mm}) – pri ďalšom otvorení tohto videa sa obnoví.", work.SavedUtc.ToLocalTime());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WorkNote = Loc.T("Prácu sa nepodarilo uložiť: ") + ex.Message;
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
        WorkNote = Loc.F("Obnovená uložená práca z {0:d. M. yyyy H:mm}.", work.SavedUtc.ToLocalTime());
        RefreshCommands();
    }

    // the subtitles of the player: the selected ones as they will be, and the ones compared with
    private void RefreshPlayer()
    {
        if (Player is null)
        {
            return;
        }

        var compared = Compare && _compareRow != _selected ? _compareRow : null;
        Player.SetSubtitles(Timeline(_selected), Timeline(compared));
        Player.SetPictures(Pictures(_selected), Pictures(compared));
    }

    private SubtitleTimeline Timeline(SubtitleRow? row)
    {
        if (row is null || !Rows.Contains(row) || !row.IsText)
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

    // picture subtitles with the row's times
    private PictureTimeline Pictures(SubtitleRow? row)
    {
        if (row is null || !Rows.Contains(row) || row.IsText)
        {
            return PictureTimeline.Empty;
        }

        if (!_pictures.TryGetValue(row, out var pictures))
        {
            ReadTrack(row);
            return PictureTimeline.Empty;
        }

        var timing = row.Problem is null ? row.Timing : SubtitleTiming.None;
        return new PictureTimeline(timing.IsNone ? pictures : pictures.Select(c => c with { Start = timing.Apply(c.Start), End = timing.Apply(c.End) }));
    }

    // a track is read once for the player (a track of the video: mkvextract reads the whole file); PGS and VobSub are drawn
    private void ReadTrack(SubtitleRow row)
    {
        if (!row.IsText && !PictureSubtitles.CanShow(row.Track))
        {
            LiveStatus = Loc.F("Stopa {0} má obrázkové titulky {1} – tie prehrávač nezobrazí (PGS a VobSub áno); časy sa uložia správne.", row.Track.Id, row.Format);
            return;
        }

        if (_file is not { } file || _services.CurrentMkvToolNix is not { } installation || (!file.IsMatroska && row.ExternalPath is null) || !_reading.Add(row))
        {
            return;
        }

        LiveStatus = row.IsExternal ? Loc.T("Načítavam titulky súboru pre prehrávač…") : Loc.F("Načítavam titulky stopy {0} pre prehrávač (číta sa celý súbor)…", row.Track.Id);
        _ = row.IsText ? ReadTrackAsync(row, file, installation) : ReadPicturesAsync(row, file, installation);
    }

    // picture subtitles are read once a video (mkvextract reads the whole file): also none or a failure is kept, so a
    // change of the times does not read them again; another video stops the reading
    private async Task ReadPicturesAsync(SubtitleRow row, Core.Media.MediaFile file, MkvToolNixInstallation installation)
    {
        var stop = _readingStop.Token;
        try
        {
            var pictures = await PictureSubtitles.ExtractAsync(installation, _services.Runner, file, row.Track, stop);
            if (!ReferenceEquals(_file, file))
            {
                return;
            }

            _pictures[row] = pictures;
            LiveStatus = pictures.Count == 0 ? Loc.F("Obrázkové titulky {0} sa nepodarilo prečítať (žiadne titulky).", row.Source)
                : Loc.F("Obrázkové titulky {0} sú načítané ({1}).", row.Source, pictures.Count);
            UpdatePreview();
            RefreshPlayer();
        }
        catch (OperationCanceledException)
        {
            // another video
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or MkvToolNixException or Core.Processes.ProcessStartException or TimeoutException)
        {
            if (ReferenceEquals(_file, file))
            {
                _pictures[row] = [];
                LiveStatus = Loc.F("Obrázkové titulky {0} sa nepodarilo prečítať: {1}", row.Source, ex.Message);
            }
        }
    }

    private async Task ReadTrackAsync(SubtitleRow row, Core.Media.MediaFile file, MkvToolNixInstallation installation)
    {
        try
        {
            var document = await SubtitleText.ExtractDocumentAsync(installation, _services.Runner, file, row.Track);
            if (ReferenceEquals(_file, file) && document is not null)
            {
                row.Document ??= document;
                LiveStatus = Loc.F("Titulky stopy {0} sú načítané ({1}).", row.Track.Id, document.Cues.Count);
                UpdatePreview();
                RefreshPlayer();
            }
            else if (document is null)
            {
                // not read again for every change (the whole file is read): when the video is opened again
                LiveStatus = Loc.F("Titulky stopy {0} sa nepodarilo prečítať.", row.Track.Id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MkvToolNixException or Core.Processes.ProcessStartException or TimeoutException)
        {
            LiveStatus = Loc.F("Titulky stopy {0} sa nepodarilo prečítať: {1}", row.Track.Id, ex.Message);
        }
    }

    // the subtitle of the selected ones nearest to the moment: its time shown and its time as read (text or picture)
    private (TimeSpan Shown, TimeSpan Read, string Label)? NearestCue(SubtitleRow row)
    {
        var now = Player.Now;
        var timing = row.Problem is null ? row.Timing : SubtitleTiming.None;
        if (row.IsText && Player.Primary.Nearest(now) is { } cue)
        {
            var read = row.Repaired(row.Document)?.Cues.FirstOrDefault(c => c.Text == cue.Text && (timing.Apply(c.Start) - cue.Start).Duration() < TimeSpan.FromMilliseconds(2));
            return read is null ? null : (cue.Start, read.Start, Short(cue.Text));
        }

        if (!row.IsText && _pictures.TryGetValue(row, out var pictures) &&
            pictures.MinBy(p => (timing.Apply(p.Start) - now).Duration()) is { } picture)
        {
            return (timing.Apply(picture.Start), picture.Start, Loc.F("obrázok z {0}", SubtitleRow.Clock(picture.Start)));
        }

        return null;
    }

    private void StartsNow()
    {
        if (_selected is not { } row || NearestCue(row) is not { } cue)
        {
            LiveStatus = Loc.T("Pri prehrávači nie je žiadny titulok vybraných titulkov.");
            return;
        }

        var now = Player.Now;
        var by = (long)Math.Round((now - cue.Shown).TotalMilliseconds);
        row.DelayText = (row.DelayMilliseconds + by).ToString(CultureInfo.InvariantCulture);
        LiveStatus = Loc.F("Posun {0}{1} ms: „{2}“ začína v {3} (posun spolu {4} ms).", by >= 0 ? "+" : "−", Math.Abs(by).ToString("#,0", CultureInfo.GetCultureInfo("sk-SK")),
            cue.Label, SubtitleRow.Clock(now), row.DelayMilliseconds.ToString("#,0", CultureInfo.GetCultureInfo("sk-SK")));
    }

    private void MarkPoint(bool first)
    {
        if (_selected is not { } row || NearestCue(row) is not { } cue)
        {
            LiveStatus = Loc.T("Pri prehrávači nie je žiadny titulok vybraných titulkov.");
            return;
        }

        var now = SubtitleRow.Clock(Player.Now);
        if (first)
        {
            row.FirstNow = SubtitleRow.Clock(cue.Read);
            row.FirstTarget = now;
            LiveStatus = Loc.F("1. bod: „{0}“ ({1}) patrí do {2}. Pustite film ďalej a pri titulku ku koncu dajte 2. bod.", cue.Label, row.FirstNow, now);
        }
        else
        {
            row.SecondNow = SubtitleRow.Clock(cue.Read);
            row.SecondTarget = now;
            row.TwoPointsCommand.Execute(null);
            LiveStatus = Loc.F("2. bod: „{0}“ ({1}) patrí do {2}. {3}", cue.Label, row.SecondNow, now, row.SyncNote);
        }
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
