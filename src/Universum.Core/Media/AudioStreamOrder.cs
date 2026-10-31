using System.Globalization;
using Universum.Core.Processes;

namespace Universum.Core.Media;

/// <summary>
/// FFmpeg's number of an audio track ("0:a:N") for a track as MKVToolNix lists it. In Matroska both count the same; in
/// other containers they may not (owner's note: MPEG TS) – there the stream is found by its ID, which both tell: the PID of
/// an MPEG transport stream, the track ID of MP4. When it cannot be found, MKVToolNix's order is used.
/// </summary>
public static class AudioStreamOrder
{
    /// <summary>The file is Matroska (by its extension): the orders are the same, nothing is asked.</summary>
    public static bool IsMatroska(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mka" or ".mks" or ".mk3d" or ".webm";

    /// <summary>FFmpeg's audio number of <paramref name="track"/> (<paramref name="ordinal"/>: its number among the audio tracks as MKVToolNix lists them).</summary>
    public static async Task<int> OrdinalAsync(IProcessRunner runner, string ffprobe, string path, MediaTrack track, int ordinal, CancellationToken cancellationToken = default)
    {
        if (IsMatroska(path) || track.Number is not { } id)
        {
            return ordinal;
        }

        var streams = await StreamIdsAsync(runner, ffprobe, path, cancellationToken).ConfigureAwait(false);
        var found = streams.IndexOf(id);
        return found >= 0 ? found : ordinal;
    }

    /// <summary>The IDs of the audio streams of a file in FFmpeg's order (null for a stream without one).</summary>
    public static async Task<List<long?>> StreamIdsAsync(IProcessRunner runner, string ffprobe, string path, CancellationToken cancellationToken = default)
    {
        var ids = new List<long?>();
        var result = await runner.RunAsync(new ProcessRequest(ffprobe, ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index,id", "-of", "csv=p=0", path])
        {
            Timeout = TimeSpan.FromMinutes(1),
            OnOutputLine = line =>
            {
                var parts = line.Split(',');
                ids.Add(parts.Length >= 2 ? Parse(parts[1].Trim()) : null);
            },
        }, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? ids : [];
    }

    // "0x110" (ffprobe) or "272"
    private static long? Parse(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null
            : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}
