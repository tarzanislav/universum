using System.Globalization;
using System.Text;

namespace Universum.Core.Subtitles;

/// <summary>Writes subtitles as WebVTT in UTF-8 (owner, 30. 10. 2026: WebVTT besides SRT); italic, bold and underline stay.</summary>
public static class VttWriter
{
    /// <summary>The WebVTT text: "WEBVTT", then "hh:mm:ss.mmm --> hh:mm:ss.mmm" and the text of each subtitle.</summary>
    public static string Write(SubtitleDocument document)
    {
        var text = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in document.Cues.Where(c => c.Text.Trim().Length > 0 && c.End > TimeSpan.Zero))
        {
            var start = cue.Start < TimeSpan.Zero ? TimeSpan.Zero : cue.Start;
            var end = cue.End < start ? start : cue.End;
            text.Append(Time(start)).Append(" --> ").Append(Time(end)).Append('\n')
                .Append(Escape(cue.Text.Replace("\r", "", StringComparison.Ordinal))).Append("\n\n");
        }

        return text.ToString();
    }

    /// <summary>Saves the subtitles as a new WebVTT file in UTF-8 – never over an existing file.</summary>
    public static async Task SaveAsync(SubtitleDocument document, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(Write(document).AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"01:02:03.456".</summary>
    public static string Time(TimeSpan time)
    {
        var milliseconds = (long)Math.Round(time.TotalMilliseconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{milliseconds / 3_600_000:00}:{milliseconds / 60_000 % 60:00}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}");
    }

    // &, < and > are written as entities (so "-->" never stands in a text), except the tags <i>, <b>, <u> UNIVERSUM keeps
    private static string Escape(string text)
    {
        var escaped = text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        foreach (var tag in new[] { "i", "b", "u" })
        {
            escaped = escaped.Replace($"&lt;{tag}&gt;", $"<{tag}>", StringComparison.Ordinal).Replace($"&lt;/{tag}&gt;", $"</{tag}>", StringComparison.Ordinal);
        }

        return escaped;
    }
}
