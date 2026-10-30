using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
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
                LiveStatus = $"Titulky stopy {row.Track.Id} sa nepodarilo prečítať.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MkvToolNixException or Core.Processes.ProcessStartException or TimeoutException)
        {
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
