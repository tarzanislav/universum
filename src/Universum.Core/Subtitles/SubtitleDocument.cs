namespace Universum.Core.Subtitles;

/// <summary>Anything shown from a time to a time (a subtitle of text or of pictures).</summary>
public interface ITimedCue
{
    TimeSpan Start { get; }

    TimeSpan End { get; }
}

/// <summary>One subtitle: when it is shown and its text (lines separated by "\n"; only &lt;i&gt;, &lt;b&gt;, &lt;u&gt; tags).</summary>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text) : ITimedCue;

/// <summary>Subtitles read from a file, in time order.</summary>
/// <param name="Format">The format they were read from ("SRT", "WebVTT", "ASS/SSA", "TTML", "MicroDVD" …).</param>
/// <param name="Encoding">The character encoding of the file ("UTF-8", "Windows-1250" …).</param>
public sealed record SubtitleDocument(IReadOnlyList<SubtitleCue> Cues, string Format, string Encoding = "UTF-8")
{
    /// <summary>The frame rate a frame-based format (MicroDVD) was read with; null for formats with times.</summary>
    public double? FramesPerSecond { get; init; }

    /// <summary>The same subtitles with other cues (the format and encoding they came from stay as a note).</summary>
    public SubtitleDocument With(IEnumerable<SubtitleCue> cues) => this with { Cues = cues.OrderBy(c => c.Start).ThenBy(c => c.End).ToList() };

    /// <summary>All the text, one subtitle a line (for the language of the subtitles).</summary>
    public string PlainText => string.Join("\n", Cues.Select(c => SubtitleCleanup.WithoutTags(c.Text).Replace('\n', ' ')));
}
