using System.Globalization;
using Universum.Core.Media;

namespace Universum.Core.Rules;

/// <summary>
/// Forced subtitles recognised by their amount (owner, 30. 10. 2026): forced subtitles hold far fewer subtitles – and
/// far fewer bytes – than full ones. The track statistics of the file (number of subtitles, bytes) are compared with the
/// fullest subtitle track of the same file, or, when it is the only one, with the length of the film. It is UNIVERSUM's
/// suggestion: a choice of the user wins.
/// </summary>
public static class ForcedSubtitles
{
    /// <summary>A track with at most this share of the fullest subtitle track's subtitles is forced.</summary>
    public const double Share = 0.3;

    /// <summary>The fullest track must hold at least this many subtitles to compare with (a short film has few anyway).</summary>
    public const int FullMinimum = 100;

    /// <summary>The only subtitle track is forced with fewer subtitles per minute of the film than this.</summary>
    public const double PerMinute = 1.5;

    /// <summary>
    /// Why the track is forced by its amount ("85 titulkov z 1 240 v najväčšej stope"), or null when it is not (or when the
    /// file has no statistics of it).
    /// </summary>
    public static string? ByAmount(MediaFile file, MediaTrack track)
    {
        if (track.Type != TrackType.Subtitles || track.StatisticsFrames is not { } frames || frames <= 0)
        {
            return null;
        }

        var fullest = file.Tracks.Where(t => t.Type == TrackType.Subtitles && t.Id != track.Id && t.StatisticsFrames is > 0)
            .OrderByDescending(t => t.StatisticsFrames).FirstOrDefault();
        if (fullest is { StatisticsFrames: { } full } && full >= FullMinimum)
        {
            // the number of subtitles decides; the bytes, when known, have to agree
            var fewer = frames <= full * Share;
            var smaller = track.StatisticsBytes is not { } bytes || fullest.StatisticsBytes is not { } fullBytes || fullBytes <= 0 || bytes <= fullBytes * Share;
            return fewer && smaller
                ? $"{Count(frames)} titulkov oproti {Count(full)} v najväčšej titulkovej stope"
                : null;
        }

        if (file.Duration is { TotalMinutes: >= 20 } duration && frames < duration.TotalMinutes * PerMinute)
        {
            return $"{Count(frames)} titulkov na {(int)duration.TotalMinutes} minút filmu";
        }

        return null;
    }

    /// <summary>A subtitle track without a single subtitle (by the statistics of the file): it is removed.</summary>
    public static bool IsEmpty(MediaTrack track) =>
        track.Type == TrackType.Subtitles && track.StatisticsFrames == 0 && track.StatisticsBytes is null or 0;

    private static string Count(long value) => value.ToString("#,0", CultureInfo.GetCultureInfo("sk-SK"));
}
