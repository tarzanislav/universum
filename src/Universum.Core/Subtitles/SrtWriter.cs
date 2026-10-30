using System.Globalization;
using System.Text;

namespace Universum.Core.Subtitles;

/// <summary>Writes subtitles as SRT in UTF-8 (with a byte order mark, so every player and mkvmerge reads the letters right).</summary>
public static class SrtWriter
{
    /// <summary>The SRT text: numbered subtitles, "hh:mm:ss,mmm --> hh:mm:ss,mmm", lines ended by CR LF.</summary>
    public static string Write(SubtitleDocument document)
    {
        var text = new StringBuilder();
        var number = 0;
        foreach (var cue in document.Cues.Where(c => c.Text.Trim().Length > 0 && c.End > TimeSpan.Zero))
        {
            var start = cue.Start < TimeSpan.Zero ? TimeSpan.Zero : cue.Start;
            var end = cue.End < start ? start : cue.End;
            text.Append((++number).ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append(Time(start)).Append(" --> ").Append(Time(end)).Append("\r\n")
                .Append(cue.Text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n\r\n");
        }

        return text.ToString();
    }

    /// <summary>Saves the subtitles as a new SRT file in UTF-8 – never over an existing file (IOException then).</summary>
    public static async Task SaveAsync(SubtitleDocument document, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteAsync(Write(document).AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"01:02:03,456".</summary>
    public static string Time(TimeSpan time)
    {
        var milliseconds = (long)Math.Round(time.TotalMilliseconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{milliseconds / 3_600_000:00}:{milliseconds / 60_000 % 60:00}:{milliseconds / 1000 % 60:00},{milliseconds % 1000:000}");
    }
}
