using System.Globalization;
using Universum.Core.Processes;

namespace Universum.Core.Media;

/// <summary>
/// Where the data of a file really end (owner's report, 31. 10. 2026: a file whose header says 22:46 while its picture and
/// sound end at 22:00 – mkvmerge and MKVToolNix write 22:00, nothing is missing): ffprobe reads only the packets at the
/// end of the file (it seeks there), the last timestamp with its duration is the end.
/// </summary>
public static class MediaEnd
{
    /// <summary>
    /// The end of the last packet of any stream (<paramref name="videoOnly"/>: of the picture); null when ffprobe cannot
    /// tell. Counted from the file's first timestamp except in Matroska (mkvmerge keeps Matroska's timestamps and counts
    /// other containers from their first one).
    /// </summary>
    public static async Task<TimeSpan?> LastAsync(IProcessRunner runner, string ffprobe, string path, CancellationToken cancellationToken = default, bool videoOnly = false)
    {
        double? end = null;
        double start = 0;
        List<string> arguments = ["-v", "error", "-read_intervals", "99999999", "-show_entries", "packet=pts_time,duration_time:format=start_time", "-of", "csv=p=0"];
        if (videoOnly)
        {
            arguments.AddRange(["-select_streams", "v:0"]);
        }

        arguments.Add(path);
        var result = await runner.RunAsync(new ProcessRequest(ffprobe, arguments)
        {
            Timeout = TimeSpan.FromMinutes(5),
            OnOutputLine = line =>
            {
                var parts = line.Split(',');
                if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var first))
                {
                    // the file's first timestamp (the format section comes last, one number)
                    start = first;
                }
                else if (parts.Length >= 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
                {
                    var length = parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                    end = Math.Max(end ?? 0, pts + length);
                }
            },
        }, cancellationToken).ConfigureAwait(false);
        var shift = AudioStreamOrder.IsMatroska(path) ? 0 : Math.Max(0, start);
        return result.ExitCode == 0 && end is > 0 ? TimeSpan.FromSeconds(end.Value - shift) : null;
    }
}
