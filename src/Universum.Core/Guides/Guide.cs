using System.Text;
using Universum.Core.Reports;
using Universum.Core.Text;

namespace Universum.Core.Guides;

/// <summary>A piece of a line of a guide: plain, bold, italic, or the name of a button or field ([Spracovať]).</summary>
public sealed record GuideSpan(string Text, bool Bold = false, bool Italic = false, bool Key = false);

public enum GuideBlockKind
{
    /// <summary>The title of the guide (# ).</summary>
    Title,

    /// <summary>A part of the guide (## ) – in the contents on the left.</summary>
    Section,

    /// <summary>A smaller heading inside a part (### ).</summary>
    Subsection,

    Paragraph,

    /// <summary>A line of a list (- ).</summary>
    Bullet,

    /// <summary>A step done in order (1. ).</summary>
    Step,

    /// <summary>A coloured box: a tip, a warning or a note (!tip, !pozor, !info).</summary>
    Box,

    /// <summary>A picture of UNIVERSUM with its caption (![caption](file.jpg)).</summary>
    Image,

    /// <summary>A button that opens a category of UNIVERSUM (@otvorit Calendar).</summary>
    Open,
}

public enum GuideBoxKind
{
    Tip,
    Warning,
    Info,
}

/// <summary>A block of a guide.</summary>
public sealed record GuideBlock(GuideBlockKind Kind, IReadOnlyList<GuideSpan> Spans, int Number = 0, GuideBoxKind Box = GuideBoxKind.Info, string Target = "")
{
    /// <summary>The text without its marks (search, contents).</summary>
    public string Plain => string.Concat(Spans.Select(s => s.Text));
}

/// <summary>
/// A guide of UNIVERSUM (owner, 31. 10. 2026 – Návody: brief, exact, understandable, with emphasis, colours, lists and
/// pictures), written in UNIVERSUM's own small markup, line by line:
/// <code>
/// # Title                      ## Part (in the contents)       ### Smaller heading
/// text with **bold**, *italic* and [Button]                   - a line of a list      1. a step
/// !tip …   !pozor …   !info …  (a coloured box; following lines starting with two spaces belong to it)
/// ![Caption](picture.jpg)      @otvorit Calendar  (a button opening that category)
/// </code>
/// An empty line ends a paragraph; lines of one paragraph are joined.
/// </summary>
public sealed class GuideDocument
{
    private GuideDocument(IReadOnlyList<GuideBlock> blocks) => Blocks = blocks;

    public IReadOnlyList<GuideBlock> Blocks { get; }

    public string Title => Blocks.FirstOrDefault(b => b.Kind == GuideBlockKind.Title)?.Plain ?? "";

    /// <summary>The parts (## ) with the index of their block: the contents.</summary>
    public IReadOnlyList<(string Title, int Index)> Sections =>
        [.. Blocks.Select((b, i) => (b, i)).Where(p => p.b.Kind == GuideBlockKind.Section).Select(p => (p.b.Plain, p.i))];

    /// <summary>The pictures the guide shows.</summary>
    public IEnumerable<string> Pictures => Blocks.Where(b => b.Kind == GuideBlockKind.Image).Select(b => b.Target);

    /// <summary>The parts whose text has all the words looked for (without diacritics, in any case).</summary>
    public IReadOnlyList<(string Title, int Index)> Find(string words)
    {
        var wanted = Words.Normalize(words).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (wanted.Length == 0)
        {
            return Sections;
        }

        var found = new List<(string, int)>();
        var sections = Sections;
        for (var s = 0; s < sections.Count; s++)
        {
            var end = s + 1 < sections.Count ? sections[s + 1].Index : Blocks.Count;
            var text = Words.Normalize(string.Join(" ", Blocks.Skip(sections[s].Index).Take(end - sections[s].Index).Select(b => b.Plain)));
            if (wanted.All(w => text.Contains(w, StringComparison.Ordinal)))
            {
                found.Add(sections[s]);
            }
        }

        return found;
    }

    public static GuideDocument Parse(string text)
    {
        var blocks = new List<GuideBlock>();
        var paragraph = new StringBuilder();
        GuideBlockKind? open = null;   // a block whose text may go on (paragraph, list line, step, box)
        var number = 0;
        var box = GuideBoxKind.Info;

        void Close()
        {
            if (open is { } kind && paragraph.Length > 0)
            {
                blocks.Add(new GuideBlock(kind, Inline(paragraph.ToString()), number, box));
            }

            paragraph.Clear();
            open = null;
        }

        var steps = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                Close();
                continue;
            }

            // a line indented by two spaces goes on with the list line, step or box above it
            if (raw.StartsWith("  ", StringComparison.Ordinal) && open is GuideBlockKind.Bullet or GuideBlockKind.Step or GuideBlockKind.Box)
            {
                paragraph.Append(' ').Append(trimmed);
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                // a note for the writer, not shown
                continue;
            }

            if (Heading(trimmed) is { } heading)
            {
                Close();
                blocks.Add(new GuideBlock(heading.Kind, Inline(heading.Text)));
                steps = heading.Kind is GuideBlockKind.Section or GuideBlockKind.Subsection ? 0 : steps;
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                Close();
                open = GuideBlockKind.Bullet;
                paragraph.Append(trimmed[2..].Trim());
                continue;
            }

            if (Step(trimmed) is { } step)
            {
                Close();
                open = GuideBlockKind.Step;
                number = ++steps;
                paragraph.Append(step);
                continue;
            }

            if (Box(trimmed) is { } boxed)
            {
                Close();
                open = GuideBlockKind.Box;
                box = boxed.Kind;
                paragraph.Append(boxed.Text);
                continue;
            }

            if (trimmed.StartsWith("![", StringComparison.Ordinal) && trimmed.EndsWith(')') && trimmed.IndexOf("](", StringComparison.Ordinal) is > 1 and var middle)
            {
                Close();
                blocks.Add(new GuideBlock(GuideBlockKind.Image, [new GuideSpan(trimmed[2..middle])], Target: trimmed[(middle + 2)..^1].Trim()));
                continue;
            }

            if (trimmed.StartsWith("@otvorit ", StringComparison.Ordinal))
            {
                Close();
                var target = trimmed[9..].Trim();
                var space = target.IndexOf(' ', StringComparison.Ordinal);
                blocks.Add(new GuideBlock(GuideBlockKind.Open, [new GuideSpan(space > 0 ? target[(space + 1)..].Trim() : target)], Target: space > 0 ? target[..space] : target));
                continue;
            }

            if (open != GuideBlockKind.Paragraph)
            {
                Close();
                open = GuideBlockKind.Paragraph;
            }
            else
            {
                paragraph.Append(' ');
            }

            paragraph.Append(trimmed);
        }

        Close();
        return new GuideDocument(blocks);
    }

    private static (GuideBlockKind Kind, string Text)? Heading(string line) => line switch
    {
        _ when line.StartsWith("### ", StringComparison.Ordinal) => (GuideBlockKind.Subsection, line[4..].Trim()),
        _ when line.StartsWith("## ", StringComparison.Ordinal) => (GuideBlockKind.Section, line[3..].Trim()),
        _ when line.StartsWith("# ", StringComparison.Ordinal) => (GuideBlockKind.Title, line[2..].Trim()),
        _ => null,
    };

    // "1. text", "12. text"
    private static string? Step(string line)
    {
        var dot = line.IndexOf(". ", StringComparison.Ordinal);
        return dot is > 0 and < 4 && line[..dot].All(char.IsAsciiDigit) ? line[(dot + 2)..].Trim() : null;
    }

    private static (GuideBoxKind Kind, string Text)? Box(string line)
    {
        foreach (var (mark, kind) in new[] { ("!tip ", GuideBoxKind.Tip), ("!pozor ", GuideBoxKind.Warning), ("!info ", GuideBoxKind.Info) })
        {
            if (line.StartsWith(mark, StringComparison.Ordinal))
            {
                return (kind, line[mark.Length..].Trim());
            }
        }

        return null;
    }

    /// <summary>The spans of a line: **bold**, *italic*, [Button]; a mark without its pair stays as it is written.</summary>
    public static IReadOnlyList<GuideSpan> Inline(string text)
    {
        var spans = new List<GuideSpan>();
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length > 0)
            {
                spans.Add(new GuideSpan(plain.ToString()));
                plain.Clear();
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '*' or '[' or ']' or '\\')
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (text.AsSpan(i).StartsWith("**") && text.IndexOf("**", i + 2, StringComparison.Ordinal) is > 0 and var boldEnd && boldEnd > i + 2)
            {
                Flush();
                spans.Add(new GuideSpan(text[(i + 2)..boldEnd], Bold: true));
                i = boldEnd + 2;
                continue;
            }

            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] != ' ' && text.IndexOf('*', i + 1) is > 0 and var italicEnd && italicEnd > i + 1)
            {
                Flush();
                spans.Add(new GuideSpan(text[(i + 1)..italicEnd], Italic: true));
                i = italicEnd + 1;
                continue;
            }

            if (text[i] == '[' && text.IndexOf(']', i + 1) is > 0 and var keyEnd && keyEnd > i + 1)
            {
                Flush();
                spans.Add(new GuideSpan(text[(i + 1)..keyEnd], Key: true));
                i = keyEnd + 1;
                continue;
            }

            plain.Append(text[i]);
            i++;
        }

        Flush();
        return spans;
    }
}

/// <summary>
/// A guide laid out for printing, PDF and HTML: its headings in the colour of its group, lists, numbered steps, coloured
/// boxes, the names of buttons framed, and its pictures (JPEG) fitted to the width of the page.
/// </summary>
public static class GuideReport
{
    private static readonly Rgb Ink = new(0x22, 0x22, 0x22);
    private static readonly Rgb ItalicInk = new(0x2E, 0x6F, 0x8E);

    public static IReadOnlyList<ReportPage> Layout(GuideDocument guide, ReportFonts fonts, ReportOptions options, Rgb color, Func<string, Jpeg?> picture)
    {
        var layout = new ReportLayout(fonts, options, guide.Title, landscape: false);
        foreach (var block in guide.Blocks)
        {
            switch (block.Kind)
            {
                case GuideBlockKind.Title:
                    layout.Heading(block.Plain, 20, color);
                    layout.Space(4);
                    break;
                case GuideBlockKind.Section:
                    layout.Space(8);
                    layout.Ensure(60);
                    layout.Heading(block.Plain, 14, color);
                    layout.Line(layout.Left, layout.Y - 2, layout.Right, layout.Y - 2, color.Lighter(0.5), 0.75);
                    layout.Space(4);
                    break;
                case GuideBlockKind.Subsection:
                    layout.Space(4);
                    layout.Ensure(40);
                    layout.Heading(block.Plain, 11.5, color);
                    break;
                case GuideBlockKind.Paragraph:
                    Rich(layout, block.Spans, 0, 10.5);
                    layout.Space(5);
                    break;
                case GuideBlockKind.Bullet:
                    Marked(layout, "•", block.Spans, color);
                    break;
                case GuideBlockKind.Step:
                    Marked(layout, block.Number + ".", block.Spans, color);
                    break;
                case GuideBlockKind.Box:
                    Box(layout, block, fonts);
                    break;
                case GuideBlockKind.Image when picture(block.Target) is { } jpeg:
                    Image(layout, jpeg, block.Plain);
                    break;
                case GuideBlockKind.Open:
                    // a button of the program: on paper only its name
                    Rich(layout, [new GuideSpan("→ "), new GuideSpan(block.Plain, Key: true)], 0, 10);
                    layout.Space(5);
                    break;
            }
        }

        return layout.Finish();
    }

    private static void Marked(ReportLayout layout, string mark, IReadOnlyList<GuideSpan> spans, Rgb color)
    {
        layout.Ensure(layout.Fonts.LineHeight(10.5));
        layout.Text(layout.Left + 6, layout.Y + layout.Fonts.Ascent(10.5), mark, 10.5, true, color);
        Rich(layout, spans, 22, 10.5);
        layout.Space(2);
    }

    private static readonly Dictionary<GuideBoxKind, (Rgb Fill, Rgb Edge)> BoxColors = new()
    {
        [GuideBoxKind.Tip] = (new Rgb(0xE6, 0xF5, 0xEA), new Rgb(0x3E, 0xA8, 0x5F)),
        [GuideBoxKind.Warning] = (new Rgb(0xFD, 0xEE, 0xE2), new Rgb(0xD9, 0x7A, 0x2B)),
        [GuideBoxKind.Info] = (new Rgb(0xE6, 0xF0, 0xFB), new Rgb(0x4A, 0x7B, 0xD9)),
    };

    private static void Box(ReportLayout layout, GuideBlock block, ReportFonts fonts)
    {
        var (fill, edge) = BoxColors[block.Box];
        var lines = Lines(layout, block.Spans, layout.Width - 24, 10);
        var height = (lines.Count * fonts.LineHeight(10)) + 12;
        layout.Ensure(height + 6);
        var top = layout.Y;
        layout.Box(layout.Left, top, layout.Width, height, fill, null, 0, 4);
        layout.Box(layout.Left, top, 3, height, edge);
        layout.Y = top + 6;
        Draw(layout, lines, 14, 10);
        layout.Y = top + height + 6;
    }

    private static void Image(ReportLayout layout, Jpeg jpeg, string caption)
    {
        // the picture at most the width of the text and half the page high, never enlarged beyond its pixels at 110 dpi
        var width = Math.Min(layout.Width, jpeg.Width * 72.0 / 110);
        var height = width * jpeg.Height / jpeg.Width;
        var most = (layout.Bottom - layout.Top) * 0.5;
        if (height > most)
        {
            width *= most / height;
            height = most;
        }

        layout.Ensure(height + 22);
        var x = layout.Left + ((layout.Width - width) / 2);
        layout.Page.Items.Add(new ImageItem(x, layout.Y, width, height, jpeg));
        layout.Box(x, layout.Y, width, height, null, Rgb.Line, 0.5);
        layout.Y += height + 3;
        if (caption.Length > 0)
        {
            var text = layout.Fit(caption, layout.Width, 8.5, false);
            layout.Text(layout.Left + ((layout.Width - layout.Fonts.Width(text, 8.5)) / 2), layout.Y + layout.Fonts.Ascent(8.5), text, 8.5, false, Rgb.Gray);
            layout.Y += layout.Fonts.LineHeight(8.5);
        }

        layout.Space(8);
    }

    // a paragraph of spans wrapped by words, each word in its own style
    private static void Rich(ReportLayout layout, IReadOnlyList<GuideSpan> spans, double indent, double size) =>
        Draw(layout, Lines(layout, spans, layout.Width - indent, size), indent, size);

    private sealed record Word(string Text, GuideSpan Style, bool SpaceBefore);

    private static List<List<Word>> Lines(ReportLayout layout, IReadOnlyList<GuideSpan> spans, double width, double size)
    {
        var words = new List<Word>();
        var space = false;
        foreach (var span in spans)
        {
            var parts = span.Key ? [span.Text] : span.Text.Split(' ');
            for (var p = 0; p < parts.Length; p++)
            {
                if (parts[p].Length == 0)
                {
                    space = true;
                    continue;
                }

                words.Add(new Word(parts[p], span, space || (p > 0 && words.Count > 0)));
                space = false;
            }

            space = span.Text.EndsWith(' ');
        }

        var lines = new List<List<Word>>();
        var line = new List<Word>();
        var used = 0.0;
        foreach (var word in words)
        {
            var w = WordWidth(layout, word, size) + (word.SpaceBefore && line.Count > 0 ? layout.Fonts.Width(" ", size) : 0);
            if (line.Count > 0 && used + w > width)
            {
                lines.Add(line);
                line = [];
                used = 0;
                w = WordWidth(layout, word, size);
            }

            line.Add(word);
            used += w;
        }

        if (line.Count > 0)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static double WordWidth(ReportLayout layout, Word word, double size) =>
        layout.Fonts.Width(word.Text, size, word.Style.Bold || word.Style.Key) + (word.Style.Key ? 8 : 0);

    private static void Draw(ReportLayout layout, List<List<Word>> lines, double indent, double size)
    {
        foreach (var line in lines)
        {
            layout.Ensure(layout.Fonts.LineHeight(size));
            var x = layout.Left + indent;
            var baseline = layout.Y + layout.Fonts.Ascent(size);
            for (var i = 0; i < line.Count; i++)
            {
                var word = line[i];
                if (i > 0 && word.SpaceBefore)
                {
                    x += layout.Fonts.Width(" ", size);
                }

                var bold = word.Style.Bold || word.Style.Key;
                if (word.Style.Key)
                {
                    // the name of a button, framed as a button
                    var w = layout.Fonts.Width(word.Text, size, true) + 8;
                    layout.Box(x, layout.Y + 0.5, w, layout.Fonts.LineHeight(size) - 1, new Rgb(0xF1, 0xF1, 0xF4), new Rgb(0xB8, 0xB8, 0xC4), 0.5, 2.5);
                    layout.Text(x + 4, baseline, word.Text, size, true, Ink);
                    x += w;
                    continue;
                }

                layout.Text(x, baseline, word.Text, size, bold, word.Style.Italic ? ItalicInk : Ink);
                x += layout.Fonts.Width(word.Text, size, bold);
            }

            layout.Y += layout.Fonts.LineHeight(size);
        }
    }
}
