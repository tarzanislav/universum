using System.Globalization;
using System.Text.RegularExpressions;
using Universum.Core.Processes;

namespace Universum.Core.Subtitles.Sync;

/// <summary>
/// Where people speak in an audio track (automatic sync of subtitles): FFmpeg decodes parts of the track into the band of
/// the voice (mono, 8 kHz, 200–3 500 Hz), UNIVERSUM measures the loudness of every 10 ms and marks as speech what stands
/// clearly above the quiet of the part; short gaps between words are bridged, single clicks are left out.
/// </summary>
public static partial class SpeechActivity
{
    private const int SampleRate = 8000;
    private const int Frame = SampleRate / ActivityWindow.Rate;

    /// <summary>The parts of the film read for sync: eight parts of two and a half minutes at 8–92 % of the film.</summary>
    public static IReadOnlyList<TimeSpan> Parts(TimeSpan duration, int count = 8, double minutes = 2.5)
    {
        var length = TimeSpan.FromMinutes(minutes);
        if (duration <= length * 2)
        {
            return [TimeSpan.Zero];
        }

        return Enumerable.Range(0, count).Select(i => TimeSpan.FromTicks((long)(duration.Ticks * (0.08 + (0.84 * i / Math.Max(1, count - 1))))))
            .Select(t => t + length > duration ? duration - length : t).ToList();
    }

    /// <summary>
    /// The speech of parts of an audio track (<paramref name="audioOrdinal"/>: 0 = the first audio track of the file), in
    /// the times of the file's own timestamps – the times subtitles of a Matroska file have, also when its first timestamp
    /// is not 0 (mkvmerge keeps them; other containers start at 0 once remuxed).
    /// </summary>
    public static async Task<IReadOnlyList<ActivityWindow>> ReadAsync(
        IProcessRunner runner, string ffmpeg, string path, int audioOrdinal, IReadOnlyList<TimeSpan> starts, TimeSpan length,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        // FFmpeg seeks from the first timestamp of the file
        var offset = IsMatroska(path) ? await StartAsync(runner, ffmpeg, path, cancellationToken).ConfigureAwait(false) : TimeSpan.Zero;
        var folder = Path.Combine(Path.GetTempPath(), "UNIVERSUM", "speech-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var windows = new List<ActivityWindow>();
        try
        {
            for (var i = 0; i < starts.Count; i++)
            {
                progress?.Report(i);
                var raw = Path.Combine(folder, $"{i}.raw");
                var arguments = new List<string>
                {
                    "-nostdin", "-hide_banner", "-loglevel", "error",
                    "-ss", Seconds(starts[i]), "-i", path, "-t", Seconds(length),
                    "-map", "0:a:" + audioOrdinal.ToString(CultureInfo.InvariantCulture), "-vn", "-sn", "-dn",
                    "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-af", "highpass=f=200,lowpass=f=3500",
                    "-f", "s16le", "-acodec", "pcm_s16le", raw,
                };
                try
                {
                    // a large file on a network drive: FFmpeg reads the whole film for the part (the video too)
                    await runner.RunAsync(new ProcessRequest(ffmpeg, arguments) { Timeout = TimeSpan.FromMinutes(10) }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or ProcessStartException)
                {
                    continue;
                }

                if (File.Exists(raw))
                {
                    var bytes = await File.ReadAllBytesAsync(raw, cancellationToken).ConfigureAwait(false);
                    var samples = new short[bytes.Length / 2];
                    Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
                    if (samples.Length >= SampleRate * 10)
                    {
                        windows.Add(new ActivityWindow(starts[i] + offset, FromSamples(samples)));
                    }
                }
            }
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
        }

        return windows;
    }

    /// <summary>The first timestamp of a file (FFmpeg says it as "start:"); 0 when it cannot be read.</summary>
    public static async Task<TimeSpan> StartAsync(IProcessRunner runner, string ffmpeg, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            // without an output FFmpeg only describes the input (and ends with an error, as expected)
            var result = await runner.RunAsync(new ProcessRequest(ffmpeg, ["-nostdin", "-hide_banner", "-i", path]) { Timeout = TimeSpan.FromMinutes(1) }, cancellationToken)
                .ConfigureAwait(false);
            var match = StartPattern().Match(result.StandardError);
            return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && Math.Abs(seconds) < 86_400
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
        }
        catch (Exception ex) when (ex is TimeoutException or ProcessStartException)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>Speech (+1) or not (−1) for every 10 ms of 8 kHz mono samples.</summary>
    public static sbyte[] FromSamples(ReadOnlySpan<short> samples)
    {
        var frames = samples.Length / Frame;
        if (frames == 0)
        {
            return [];
        }

        var loudness = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var i = f * Frame; i < (f + 1) * Frame; i++)
            {
                sum += (double)samples[i] * samples[i];
            }

            loudness[f] = Math.Log10((sum / Frame) + 1);
        }

        // the quiet and the loud of this part: speech stands clearly above the quiet
        var sorted = loudness.Order().ToArray();
        var quiet = sorted[(int)(sorted.Length * 0.15)];
        var loud = sorted[(int)(sorted.Length * 0.95)];
        var threshold = quiet + (0.45 * (loud - quiet));
        var values = new sbyte[frames];
        for (var f = 0; f < frames; f++)
        {
            values[f] = loud - quiet < 0.3 ? (sbyte)-1 : loudness[f] > threshold ? (sbyte)1 : (sbyte)-1;
        }

        // gaps between words (under 250 ms) are speech; a click (under 80 ms) is not
        Fill(values, -1, 25, 1);
        Fill(values, 1, 8, -1);
        return values;
    }

    // runs of `value` shorter than `shorter` between other values become `into`
    private static void Fill(sbyte[] values, sbyte value, int shorter, sbyte into)
    {
        var i = 0;
        while (i < values.Length)
        {
            if (values[i] != value)
            {
                i++;
                continue;
            }

            var end = i;
            while (end < values.Length && values[end] == value)
            {
                end++;
            }

            if (end - i < shorter && i > 0 && end < values.Length)
            {
                Array.Fill(values, into, i, end - i);
            }

            i = end;
        }
    }

    [GeneratedRegex(@"Duration:[^\n]*?, start: (-?\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex StartPattern();

    private static bool IsMatroska(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mka" or ".mks" or ".mk3d" or ".webm";

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
