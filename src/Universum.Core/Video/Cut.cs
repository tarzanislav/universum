using System.Globalization;
using Universum.Core.Processes;

namespace Universum.Core.Video;

/// <summary>A part of a film that is kept (Úprava videostopy: strih), from <see cref="Start"/> to <see cref="End"/>.</summary>
public sealed record CutPart(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Length => End - Start;
}

/// <summary>
/// The key frames of a film on the time mkvmerge and the player of UNIVERSUM count (a Matroska file: its own timestamps;
/// another one: from its first timestamp), and where the film ends on it (null when that is not known).
/// </summary>
public sealed record FilmKeyframes(IReadOnlyList<TimeSpan> Times, TimeSpan? End);

/// <summary>
/// Cutting a film without writing its picture anew (mkvmerge "--split parts"): a part starts and ends at a key frame –
/// the first one at or after the time marked; the parts kept follow one another in one file.
/// </summary>
public static class Cut
{
    /// <summary>
    /// The parts as mkvmerge will write them: each mark moved to the next key frame (none after it: the end of the film,
    /// <paramref name="end"/>); a part with no key frame inside it is left out.
    /// </summary>
    public static IReadOnlyList<CutPart> Snap(IReadOnlyList<CutPart> parts, IReadOnlyList<TimeSpan> keyframes, TimeSpan end)
    {
        TimeSpan Next(TimeSpan time)
        {
            int low = 0, high = keyframes.Count - 1, found = -1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (keyframes[middle] >= time - TimeSpan.FromMilliseconds(1))
                {
                    found = middle;
                    high = middle - 1;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return found < 0 ? end : keyframes[found];
        }

        return Normalized(parts.Select(p => new CutPart(Next(p.Start), p.End >= end ? end : Next(p.End))).ToList());
    }

    /// <summary>The parts in order of time, overlapping ones joined, empty ones left out.</summary>
    public static IReadOnlyList<CutPart> Normalized(IEnumerable<CutPart> parts)
    {
        var result = new List<CutPart>();
        foreach (var part in parts.Where(p => p.End > p.Start).OrderBy(p => p.Start))
        {
            if (result.Count > 0 && part.Start <= result[^1].End)
            {
                result[^1] = result[^1] with { End = part.End > result[^1].End ? part.End : result[^1].End };
            }
            else
            {
                result.Add(part);
            }
        }

        return result;
    }

    /// <summary>
    /// The subtitles within the parts kept (on the film's time, as mkvmerge cuts): a subtitle shown at a cut is shortened to
    /// it – mkvmerge measures what it leaves out by the subtitles' durations too, so none may reach out of a part.
    /// </summary>
    public static Subtitles.SubtitleDocument Clip(Subtitles.SubtitleDocument document, IReadOnlyList<CutPart> parts)
    {
        var cues = new List<Subtitles.SubtitleCue>();
        foreach (var cue in document.Cues)
        {
            foreach (var part in parts)
            {
                var start = cue.Start > part.Start ? cue.Start : part.Start;
                var end = cue.End < part.End ? cue.End : part.End;
                if (end - start >= TimeSpan.FromMilliseconds(50))
                {
                    cues.Add(cue with { Start = start, End = end });
                }
            }
        }

        return document.With(cues);
    }

    /// <summary>mkvmerge's value: "parts:00:01:00.000-00:05:00.000,+00:10:00.000-00:20:00.000" (one file).</summary>
    public static string MkvMergeValue(IReadOnlyList<CutPart> parts) =>
        "parts:" + string.Join(",", parts.Select((p, i) => (i > 0 ? "+" : "") + Clock(p.Start) + "-" + Clock(p.End)));

    /// <summary>
    /// The key frames of the first video track (ffprobe reads the packets only, without decoding), on the film's time:
    /// mkvmerge keeps the timestamps of a Matroska file and counts another one from its first timestamp – as the player.
    /// </summary>
    public static async Task<FilmKeyframes> KeyframesAsync(IProcessRunner runner, string ffprobe, string path, bool matroska, CancellationToken cancellationToken = default)
    {
        var keyframes = new List<TimeSpan>();
        double? start = null, duration = null;
        static double? Number(ReadOnlySpan<char> text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        var result = await runner.RunAsync(new ProcessRequest(ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags:format=start_time,duration", "-of", "csv=p=0", path])
        {
            Timeout = TimeSpan.FromHours(1),
            OnOutputLine = line =>
            {
                // a packet: "12.345000,K__"; the file (at the end): "0.000000,5400.123000"
                var comma = line.IndexOf(',', StringComparison.Ordinal);
                if (comma <= 0)
                {
                    return;
                }

                var first = Number(line.AsSpan(0, comma));
                if (Number(line.AsSpan(comma + 1)) is { } second)
                {
                    (start, duration) = (first, second);
                }
                else if (first is { } seconds && line.AsSpan(comma + 1).Contains('K'))
                {
                    keyframes.Add(TimeSpan.FromSeconds(seconds));
                }
            },
        }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException("ffprobe: " + result.StandardError.Trim());
        }

        var origin = TimeSpan.FromSeconds(start ?? 0);
        var shift = matroska ? TimeSpan.Zero : origin;
        keyframes.Sort();
        return new FilmKeyframes(keyframes.Select(k => k - shift).ToList(), duration is { } length ? origin - shift + TimeSpan.FromSeconds(length) : null);
    }

    private static string Clock(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
}
