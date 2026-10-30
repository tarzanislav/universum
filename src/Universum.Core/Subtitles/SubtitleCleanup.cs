using System.Text.RegularExpressions;

namespace Universum.Core.Subtitles;

/// <summary>
/// Repairs of subtitle text (owner, 30. 10. 2026: other functions useful for UNIVERSUM): subtitles for the hearing
/// impaired made into ordinary ones, formatting removed, overlapping and too short times fixed.
/// </summary>
public static partial class SubtitleCleanup
{
    /// <summary>A subtitle is shown at least this long when the next one leaves room.</summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(700);

    /// <summary>The text without &lt;i&gt;, &lt;b&gt;, &lt;u&gt; (and any other tags).</summary>
    public static string WithoutTags(string text) => Tags().Replace(text, "");

    /// <summary>Formatting left out (italic, bold, underline).</summary>
    public static SubtitleDocument RemoveFormatting(SubtitleDocument document) =>
        document.With(document.Cues.Select(c => c with { Text = WithoutTags(c.Text).Trim() }).Where(c => c.Text.Length > 0));

    /// <summary>
    /// Subtitles for the hearing impaired (SDH) made into ordinary ones: sounds in brackets ([hudba], (smiech)), names of
    /// speakers at the start of a line ("JOHN:", "MUŽ:"), music signs; a subtitle left without text is removed.
    /// </summary>
    public static SubtitleDocument RemoveHearingImpaired(SubtitleDocument document) =>
        document.With(document.Cues.Select(c => c with { Text = HearingImpaired(c.Text) }).Where(c => WithoutTags(c.Text).Trim().Length > 0));

    private static string HearingImpaired(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = Bracketed().Replace(raw, " ");
            line = Speaker().Replace(line, "$1");
            line = Music().Replace(line, "");
            line = Regex.Replace(line, @"\s{2,}", " ").Trim();
            // a dash left alone after a removed sound
            if (WithoutTags(line).Trim() is "" or "-" or "–")
            {
                continue;
            }

            lines.Add(line);
        }

        // one line of a dialogue left: no dash in front of it
        if (lines.Count == 1)
        {
            lines[0] = Regex.Replace(lines[0], @"^((?:<[ibu]>)*)\s*[-–]\s*", "$1");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Times repaired: a subtitle ends before the next one starts, a very short one lasts at least
    /// <see cref="MinimumDuration"/> when there is room.
    /// </summary>
    public static SubtitleDocument FixTimes(SubtitleDocument document)
    {
        var cues = document.Cues.OrderBy(c => c.Start).ToList();
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var next = i + 1 < cues.Count ? cues[i + 1].Start : (TimeSpan?)null;
            var end = cue.End;
            if (end - cue.Start < MinimumDuration)
            {
                end = cue.Start + MinimumDuration;
            }

            if (next is { } nextStart && end > nextStart - TimeSpan.FromMilliseconds(1) && nextStart > cue.Start)
            {
                end = nextStart - TimeSpan.FromMilliseconds(1);
            }

            cues[i] = cue with { End = end < cue.Start ? cue.Start : end };
        }

        return document.With(cues);
    }

    /// <summary>How many subtitles overlap the next one.</summary>
    public static int Overlaps(SubtitleDocument document)
    {
        var cues = document.Cues.OrderBy(c => c.Start).ToList();
        return cues.Zip(cues.Skip(1)).Count(pair => pair.First.End > pair.Second.Start);
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    // [sound], (sound), ♪ … ♪ is music
    [GeneratedRegex(@"\s*(\[[^\]]*\]|\([^)]*\))\s*")]
    private static partial Regex Bracketed();

    // "JOHN:", "- MUŽ 2:", "Speaker (V.O.):" at the start of a line – a speaker's name in capitals
    [GeneratedRegex(@"^((?:<[ibu]>)*\s*[-–]?\s*)[\p{Lu}][\p{Lu}\d .'’-]{1,30}:\s*")]
    private static partial Regex Speaker();

    [GeneratedRegex(@"[♪♫]+")]
    private static partial Regex Music();
}
