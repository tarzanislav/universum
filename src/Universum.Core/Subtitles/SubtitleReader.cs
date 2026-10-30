using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Universum.Core.Subtitles;

/// <summary>
/// Reads text subtitles of the common formats (owner, 30. 10. 2026: conversion to SRT from every text format): SRT,
/// WebVTT, ASS/SSA, TTML/DFXP, SAMI, SubViewer, MicroDVD ({frame}{frame}), MPL2 ([ds][ds]) and TMPlayer (hh:mm:ss:) –
/// the format is told by the content, not by the extension (a ".txt" or ".sub" may be any of the last four). Italic,
/// bold and underline stay as &lt;i&gt;, &lt;b&gt;, &lt;u&gt;; other styling is left out.
/// </summary>
public static partial class SubtitleReader
{
    /// <summary>The frame rate MicroDVD subtitles are read with when neither the file nor the caller gives one.</summary>
    public const double DefaultFramesPerSecond = 24000.0 / 1001;

    /// <summary>Extensions of subtitle files UNIVERSUM reads as text.</summary>
    public static readonly IReadOnlySet<string> TextExtensions = new HashSet<string>(
        [".srt", ".vtt", ".webvtt", ".ass", ".ssa", ".ttml", ".dfxp", ".xml", ".smi", ".sami", ".sub", ".txt", ".mpl"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a subtitle file in any encoding.</summary>
    /// <param name="framesPerSecond">The frame rate of the video, for MicroDVD (frame numbers) when the file does not say it.</param>
    /// <exception cref="FormatException">The file is not text subtitles UNIVERSUM can read.</exception>
    public static async Task<SubtitleDocument> ReadFileAsync(string path, double? framesPerSecond = null, CancellationToken cancellationToken = default)
    {
        var (text, encoding) = SubtitleEncoding.Decode(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        return Read(text, framesPerSecond) with { Encoding = encoding };
    }

    /// <summary>Reads subtitles from text; the format is told by the content.</summary>
    /// <exception cref="FormatException">The text is not subtitles UNIVERSUM can read.</exception>
    public static SubtitleDocument Read(string text, double? framesPerSecond = null)
    {
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var start = text.TrimStart();
        if (start.StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            return new SubtitleDocument(WebVtt(text), "WebVTT");
        }

        if (text.Contains("[Events]", StringComparison.OrdinalIgnoreCase) || text.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase))
        {
            return new SubtitleDocument(Ass(text), "ASS/SSA");
        }

        if (start.StartsWith('<'))
        {
            if (Regex.IsMatch(start, @"<sami[\s>]", RegexOptions.IgnoreCase))
            {
                return new SubtitleDocument(Sami(text), "SAMI");
            }

            if (Regex.IsMatch(text, @"<(\w+:)?tt[\s>]"))
            {
                return new SubtitleDocument(Ttml(text), "TTML");
            }
        }

        if (SrtTime().IsMatch(text))
        {
            return new SubtitleDocument(Srt(text), "SRT");
        }

        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count > 0 && lines.Count(l => MicroDvdLine().IsMatch(l)) * 2 > lines.Count)
        {
            var (cues, fps) = MicroDvd(lines, framesPerSecond);
            return new SubtitleDocument(cues, "MicroDVD") { FramesPerSecond = fps };
        }

        if (lines.Count > 0 && lines.Count(l => Mpl2Line().IsMatch(l)) * 2 > lines.Count)
        {
            return new SubtitleDocument(Mpl2(lines), "MPL2");
        }

        if (lines.Count > 0 && lines.Count(l => TmPlayerLine().IsMatch(l)) * 2 > lines.Count)
        {
            return new SubtitleDocument(TmPlayer(lines), "TMPlayer");
        }

        if (text.Contains("[INFORMATION]", StringComparison.OrdinalIgnoreCase) || SubViewerTime().IsMatch(text))
        {
            return new SubtitleDocument(SubViewer(text), "SubViewer");
        }

        throw new FormatException("Neznámy formát titulkov – nie sú to SRT, WebVTT, ASS/SSA, TTML, SAMI, SubViewer, MicroDVD, MPL2 ani TMPlayer.");
    }

    private static List<SubtitleCue> Srt(string text)
    {
        var cues = new List<SubtitleCue>();
        foreach (var block in Regex.Split(text, @"\n\s*\n"))
        {
            var lines = block.Split('\n').ToList();
            var at = lines.FindIndex(l => SrtTime().IsMatch(l));
            if (at < 0)
            {
                continue;
            }

            var match = SrtTime().Match(lines[at]);
            var body = string.Join("\n", lines.Skip(at + 1).Select(l => l.TrimEnd()).Where(l => l.Length > 0));
            cues.Add(new SubtitleCue(Clock(match.Groups["start"].Value), Clock(match.Groups["end"].Value), Html(body)));
        }

        return Sorted(cues);
    }

    private static List<SubtitleCue> WebVtt(string text)
    {
        var cues = new List<SubtitleCue>();
        foreach (var block in Regex.Split(text, @"\n\s*\n"))
        {
            var lines = block.Split('\n').ToList();
            var at = lines.FindIndex(l => l.Contains("-->", StringComparison.Ordinal));
            if (at < 0 || lines[0].StartsWith("NOTE", StringComparison.Ordinal) || lines[0].StartsWith("STYLE", StringComparison.Ordinal))
            {
                continue;
            }

            var times = lines[at].Split("-->", 2);
            var end = times[1].Trim().Split(' ', 2)[0];
            var body = string.Join("\n", lines.Skip(at + 1).Where(l => l.Trim().Length > 0));
            cues.Add(new SubtitleCue(Clock(times[0].Trim()), Clock(end), Html(VttVoice().Replace(body, ""))));
        }

        return Sorted(cues);
    }

    private static List<SubtitleCue> Ass(string text)
    {
        var cues = new List<SubtitleCue>();
        string[]? format = null;
        var inEvents = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                format = line[7..].Split(',').Select(f => f.Trim()).ToArray();
                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            format ??= ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];
            var parts = line[9..].Split(',', format.Length);
            if (parts.Length < format.Length)
            {
                continue;
            }

            var startAt = Array.FindIndex(format, f => f.Equals("Start", StringComparison.OrdinalIgnoreCase));
            var endAt = Array.FindIndex(format, f => f.Equals("End", StringComparison.OrdinalIgnoreCase));
            var textAt = Array.FindIndex(format, f => f.Equals("Text", StringComparison.OrdinalIgnoreCase));
            if (startAt < 0 || endAt < 0 || textAt < 0)
            {
                continue;
            }

            var body = parts[textAt];
            // drawings ({\p1} … {\p0}) are pictures, not text
            if (Regex.IsMatch(body, @"\{[^}]*\\p[1-9]"))
            {
                continue;
            }

            body = body.Replace("\\N", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\h", " ", StringComparison.Ordinal);
            body = AssOverride().Replace(body, m =>
            {
                var tags = new StringBuilder();
                foreach (Match tag in AssStyle().Matches(m.Value))
                {
                    var on = tag.Groups["value"].Value != "0";
                    tags.Append(on ? "<" : "</").Append(tag.Groups["tag"].Value).Append('>');
                }

                return tags.ToString();
            });
            var cleaned = Balanced(string.Join("\n", body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)));
            if (cleaned.Length > 0)
            {
                cues.Add(new SubtitleCue(Clock(parts[startAt].Trim()), Clock(parts[endAt].Trim()), cleaned));
            }
        }

        return Sorted(cues);
    }

    private static List<SubtitleCue> Ttml(string text)
    {
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var root = document.Root!;
        double FrameRate() => double.TryParse(Attribute(root, "frameRate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate > 0 ? rate : 30;
        double TickRate() => double.TryParse(Attribute(root, "tickRate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate > 0 ? rate : 1;
        TimeSpan? Time(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            value = value.Trim();
            if (TtmlOffset().Match(value) is { Success: true } offset)
            {
                var number = double.Parse(offset.Groups["n"].Value, CultureInfo.InvariantCulture);
                return offset.Groups["unit"].Value switch
                {
                    "h" => TimeSpan.FromHours(number),
                    "m" => TimeSpan.FromMinutes(number),
                    "s" => TimeSpan.FromSeconds(number),
                    "ms" => TimeSpan.FromMilliseconds(number),
                    "f" => TimeSpan.FromSeconds(number / FrameRate()),
                    _ => TimeSpan.FromSeconds(number / TickRate()),
                };
            }

            var parts = value.Split(':');
            if (parts.Length == 4)
            {
                // hh:mm:ss:frames
                return Clock(string.Join(":", parts.Take(3))) + TimeSpan.FromSeconds(int.Parse(parts[3], CultureInfo.InvariantCulture) / FrameRate());
            }

            return Clock(value);
        }

        var cues = new List<SubtitleCue>();
        foreach (var p in root.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            var begin = Time(Attribute(p, "begin"));
            var end = Time(Attribute(p, "end")) ?? (begin is { } b && Time(Attribute(p, "dur")) is { } duration ? b + duration : null);
            if (begin is null || end is null)
            {
                continue;
            }

            var body = new StringBuilder();
            Collect(p, body);
            var cleaned = string.Join("\n", body.ToString().Split('\n').Select(l => Regex.Replace(l, @"\s+", " ").Trim()).Where(l => l.Length > 0));
            if (cleaned.Length > 0)
            {
                cues.Add(new SubtitleCue(begin.Value, end.Value, cleaned));
            }
        }

        return Sorted(cues);

        static void Collect(XElement element, StringBuilder body)
        {
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText textNode:
                        body.Append(textNode.Value.Replace('\n', ' '));
                        break;
                    case XElement { Name.LocalName: "br" }:
                        body.Append('\n');
                        break;
                    case XElement child:
                        var italic = Attribute(child, "fontStyle") is "italic" or "oblique";
                        var bold = Attribute(child, "fontWeight") is "bold";
                        body.Append(italic ? "<i>" : "").Append(bold ? "<b>" : "");
                        Collect(child, body);
                        body.Append(bold ? "</b>" : "").Append(italic ? "</i>" : "");
                        break;
                }
            }
        }
    }

    // an attribute by its local name, whatever its namespace (tts:, ttp: …)
    private static string? Attribute(XElement element, string name) => element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static List<SubtitleCue> Sami(string text)
    {
        var syncs = SamiSync().Matches(text).Select(m => (Start: TimeSpan.FromMilliseconds(long.Parse(m.Groups["ms"].Value, CultureInfo.InvariantCulture)), Body: m.Groups["body"].Value)).ToList();
        var cues = new List<SubtitleCue>();
        for (var i = 0; i < syncs.Count; i++)
        {
            var body = Html(Regex.Replace(syncs[i].Body, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase));
            body = string.Join("\n", body.Split('\n').Select(l => System.Net.WebUtility.HtmlDecode(l).Replace('\u00A0', ' ').Trim()).Where(l => l.Length > 0));
            if (body.Length == 0)
            {
                continue;
            }

            var end = i + 1 < syncs.Count ? syncs[i + 1].Start : syncs[i].Start + TimeSpan.FromSeconds(4);
            cues.Add(new SubtitleCue(syncs[i].Start, end, body));
        }

        return Sorted(cues);
    }

    private static List<SubtitleCue> SubViewer(string text)
    {
        var cues = new List<SubtitleCue>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (SubViewerTime().Match(lines[i].Trim()) is not { Success: true } match)
            {
                continue;
            }

            var body = new List<string>();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
            {
                body.Add(lines[i].Trim());
            }

            cues.Add(new SubtitleCue(Clock(match.Groups["start"].Value), Clock(match.Groups["end"].Value),
                Html(string.Join("\n", body).Replace("[br]", "\n", StringComparison.OrdinalIgnoreCase))));
        }

        return Sorted(cues);
    }

    private static (List<SubtitleCue> Cues, double Fps) MicroDvd(List<string> lines, double? framesPerSecond)
    {
        // "{1}{1}23.976" at the start: the frame rate the subtitles were made for (the video's, when known, wins)
        double? stated = null;
        foreach (var line in lines.Take(3))
        {
            if (MicroDvdLine().Match(line) is { Success: true } header && long.Parse(header.Groups["start"].Value, CultureInfo.InvariantCulture) <= 1 &&
                double.TryParse(header.Groups["text"].Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate is > 1 and < 200)
            {
                stated = rate;
            }
        }

        var fps = framesPerSecond ?? stated ?? DefaultFramesPerSecond;
        var cues = new List<SubtitleCue>();
        foreach (var line in lines)
        {
            if (MicroDvdLine().Match(line) is not { Success: true } match)
            {
                continue;
            }

            var startFrame = long.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
            var body = match.Groups["text"].Value;
            if (startFrame <= 1 && double.TryParse(body.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            var endFrame = match.Groups["end"].Value.Length > 0 ? long.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture) : startFrame + (long)(3 * fps);
            cues.Add(new SubtitleCue(TimeSpan.FromSeconds(startFrame / fps), TimeSpan.FromSeconds(endFrame / fps), PipeLines(MicroDvdStyle(body))));
        }

        return (Sorted(cues), fps);
    }

    private static List<SubtitleCue> Mpl2(List<string> lines)
    {
        var cues = new List<SubtitleCue>();
        foreach (var line in lines)
        {
            if (Mpl2Line().Match(line) is not { Success: true } match)
            {
                continue;
            }

            var start = TimeSpan.FromMilliseconds(long.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture) * 100);
            var end = match.Groups["end"].Value.Length > 0 ? TimeSpan.FromMilliseconds(long.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture) * 100) : start + TimeSpan.FromSeconds(3);
            var body = string.Join("\n", match.Groups["text"].Value.Split('|').Select(l => l.StartsWith('/') ? "<i>" + l[1..] + "</i>" : l));
            cues.Add(new SubtitleCue(start, end, body.Trim()));
        }

        return Sorted(cues);
    }

    private static List<SubtitleCue> TmPlayer(List<string> lines)
    {
        var timed = lines.Select(l => TmPlayerLine().Match(l)).Where(m => m.Success)
            .Select(m => (Start: new TimeSpan(int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture)),
                Text: m.Groups["text"].Value))
            .ToList();
        var cues = new List<SubtitleCue>();
        for (var i = 0; i < timed.Count; i++)
        {
            var body = PipeLines(timed[i].Text);
            if (body.Length == 0)
            {
                continue;
            }

            // TMPlayer has no end: until the next subtitle, at most five seconds
            var next = i + 1 < timed.Count ? timed[i + 1].Start : timed[i].Start + TimeSpan.FromSeconds(5);
            var end = next - timed[i].Start > TimeSpan.FromSeconds(5) ? timed[i].Start + TimeSpan.FromSeconds(5) : next;
            cues.Add(new SubtitleCue(timed[i].Start, end, body));
        }

        return Sorted(cues);
    }

    private static string PipeLines(string text) => string.Join("\n", text.Split('|').Select(l => l.Trim()).Where(l => l.Length > 0));

    // MicroDVD styles: {y:i} italic, {y:b} bold for the line
    private static string MicroDvdStyle(string text) =>
        string.Join("|", text.Split('|').Select(line =>
        {
            var italic = Regex.IsMatch(line, @"\{[yY]:[^}]*i");
            var bold = Regex.IsMatch(line, @"\{[yY]:[^}]*b");
            var clean = Regex.Replace(line, @"\{[^}]*\}", "").Trim();
            if (clean.StartsWith('/'))
            {
                italic = true;
                clean = clean[1..];
            }

            return (italic ? "<i>" : "") + (bold ? "<b>" : "") + clean + (bold ? "</b>" : "") + (italic ? "</i>" : "");
        }));

    // HTML-like text: only <i>, <b>, <u> stay (lower case); other tags (font, c, v, span …) are left out
    private static string Html(string text) =>
        Balanced(Regex.Replace(text, @"<\s*(/?)\s*([a-zA-Z][a-zA-Z0-9.]*)[^>]*>", m =>
        {
            var tag = m.Groups[2].Value.ToLowerInvariant();
            return tag is "i" or "b" or "u" ? "<" + m.Groups[1].Value + tag + ">" : "";
        }).Replace("{\\an8}", "", StringComparison.Ordinal));

    // closes tags left open (an ASS line ends without {\i0}) and drops closings without an opening
    private static string Balanced(string text)
    {
        var open = new List<string>();
        var result = Regex.Replace(text, @"<(/?)([ibu])>", m =>
        {
            var tag = m.Groups[2].Value;
            if (m.Groups[1].Value.Length == 0)
            {
                if (open.Contains(tag))
                {
                    return "";
                }

                open.Add(tag);
                return m.Value;
            }

            if (!open.Remove(tag))
            {
                return "";
            }

            return m.Value;
        });
        for (var i = open.Count - 1; i >= 0; i--)
        {
            result += "</" + open[i] + ">";
        }

        return Regex.Replace(result, @"<([ibu])></\1>", "").Trim();
    }

    /// <summary>"01:02:03,456", "1:02:03.45", "02:03.456" (WebVTT), "0:00:01.00" (ASS) → time.</summary>
    public static TimeSpan Clock(string value)
    {
        var parts = value.Trim().Replace(',', '.').Split(':');
        double seconds = 0;
        foreach (var part in parts)
        {
            seconds = (seconds * 60) + double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        return TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    }

    private static List<SubtitleCue> Sorted(List<SubtitleCue> cues) =>
        cues.Where(c => c.Text.Length > 0).Select(c => c.End < c.Start ? c with { End = c.Start } : c).OrderBy(c => c.Start).ThenBy(c => c.End).ToList();

    [GeneratedRegex(@"(?<start>\d{1,2}:\d{1,2}:\d{1,2}[,.]\d{1,3})\s*-->\s*(?<end>\d{1,2}:\d{1,2}:\d{1,2}[,.]\d{1,3})")]
    private static partial Regex SrtTime();

    [GeneratedRegex(@"^\{(?<start>\d+)\}\{(?<end>\d*)\}(?<text>.*)$")]
    private static partial Regex MicroDvdLine();

    [GeneratedRegex(@"^\[(?<start>\d+)\]\[(?<end>\d*)\](?<text>.*)$")]
    private static partial Regex Mpl2Line();

    [GeneratedRegex(@"^(?<h>\d{1,2}):(?<m>\d{2}):(?<s>\d{2})(?:[:=]|,\d+=)(?<text>.*)$")]
    private static partial Regex TmPlayerLine();

    [GeneratedRegex(@"^(?<start>\d{1,2}:\d{2}:\d{2}\.\d{1,3}),(?<end>\d{1,2}:\d{2}:\d{2}\.\d{1,3})$", RegexOptions.Multiline)]
    private static partial Regex SubViewerTime();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssOverride();

    [GeneratedRegex(@"\\(?<tag>[ibu])(?<value>\d)")]
    private static partial Regex AssStyle();

    [GeneratedRegex(@"<v(\.[^ >]*)?\s[^>]*>")]
    private static partial Regex VttVoice();

    [GeneratedRegex(@"^(?<n>\d+(\.\d+)?)(?<unit>h|ms|m|s|f|t)$")]
    private static partial Regex TtmlOffset();

    [GeneratedRegex(@"<sync\s+start\s*=\s*""?(?<ms>\d+)""?[^>]*>(?<body>.*?)(?=<sync|</body|\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SamiSync();
}
