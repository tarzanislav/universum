using System.Globalization;
using Universum.Core.Processes;

namespace Universum.Core.Video;

/// <summary>A part of a film that is kept (Úprava videostopy: strih), from <see cref="Start"/> to <see cref="End"/>.</summary>
public sealed record CutPart(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Length => End - Start;
}

/// <summary>
/// Cutting a film without writing its picture anew (mkvmerge "--split parts"): a part starts and ends at a key frame –
/// the first one at or after the time marked; the parts kept follow one another in one file.
/// </summary>
public static class Cut
{
    /// <summary>The parts as mkvmerge will write them: each mark moved to the next key frame (none after it: the end of the film).</summary>
    public static IReadOnlyList<CutPart> Snap(IReadOnlyList<CutPart> parts, IReadOnlyList<TimeSpan> keyframes, TimeSpan duration)
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

            return found < 0 ? duration : keyframes[found];
        }

        return Normalized(parts.Select(p => new CutPart(Next(p.Start), p.End >= duration ? duration : Next(p.End))).ToList());
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

    /// <summary>mkvmerge's value: "parts:00:01:00.000-00:05:00.000,+00:10:00.000-00:20:00.000" (one file).</summary>
    public static string MkvMergeValue(IReadOnlyList<CutPart> parts) =>
        "parts:" + string.Join(",", parts.Select((p, i) => (i > 0 ? "+" : "") + Clock(p.Start) + "-" + Clock(p.End)));

    /// <summary>The key frames of the first video track (ffprobe reads the packets only, without decoding).</summary>
    public static async Task<IReadOnlyList<TimeSpan>> KeyframesAsync(IProcessRunner runner, string ffprobe, string path, CancellationToken cancellationToken = default)
    {
        var keyframes = new List<TimeSpan>();
        var result = await runner.RunAsync(new ProcessRequest(ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path])
        {
            Timeout = TimeSpan.FromHours(1),
            OnOutputLine = line =>
            {
                var comma = line.IndexOf(',', StringComparison.Ordinal);
                if (comma > 0 && line.AsSpan(comma + 1).Contains('K') &&
                    double.TryParse(line.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    keyframes.Add(TimeSpan.FromSeconds(seconds));
                }
            },
        }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException("ffprobe: " + result.StandardError.Trim());
        }

        keyframes.Sort();
        return keyframes;
    }

    private static string Clock(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
}
