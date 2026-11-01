using System.Globalization;

namespace Universum.Core.Reports;

/// <summary>A colour of a report.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Black { get; } = new(0x1B, 0x1B, 0x1B);

    public static Rgb Gray { get; } = new(0x6B, 0x6B, 0x6B);

    public static Rgb Line { get; } = new(0xC8, 0xC8, 0xC8);

    public static Rgb White { get; } = new(0xFF, 0xFF, 0xFF);

    /// <summary>"#RRGGBB" (another text: <paramref name="fallback"/>).</summary>
    public static Rgb Parse(string? text, Rgb fallback) =>
        text is { Length: 7 } && text[0] == '#' && int.TryParse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? new Rgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : fallback;

    /// <summary>The colour mixed with white (0: itself, 1: white).</summary>
    public Rgb Lighter(double white) => new(Mix(R, white), Mix(G, white), Mix(B, white));

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    private static byte Mix(byte value, double white) => (byte)Math.Round(value + ((255 - value) * Math.Clamp(white, 0, 1)));
}

/// <summary>Something on a laid-out page (points from the top left corner).</summary>
public abstract record PageItem;

/// <summary>A line of text; <see cref="Y"/> is its baseline.</summary>
public sealed record TextItem(double X, double Y, string Text, double Size, bool Bold, Rgb Color) : PageItem;

/// <summary>A rectangle, filled and/or outlined, with rounded corners.</summary>
public sealed record BoxItem(double X, double Y, double Width, double Height, Rgb? Fill, Rgb? Stroke = null, double StrokeWidth = 0.5, double Radius = 0) : PageItem;

/// <summary>A straight line.</summary>
public sealed record LineItem(double X1, double Y1, double X2, double Y2, Rgb Color, double Width = 0.5) : PageItem;

/// <summary>A laid-out page of a report.</summary>
public sealed class ReportPage(double width, double height)
{
    public double Width { get; } = width;

    public double Height { get; } = height;

    public List<PageItem> Items { get; } = [];
}

/// <summary>The paper of a report.</summary>
public enum Paper
{
    A4,
    A3,
    A5,
    Letter,
}

/// <summary>
/// The choices of printing and saving a report (owner, 31. 10. 2026 – as SkVP has them, written anew): the paper and its
/// orientation, several pages on a sheet, lighter print (less toner), no coloured backgrounds, a margin for punching,
/// the header and the footer.
/// </summary>
public sealed record ReportOptions
{
    public Paper Paper { get; init; } = Paper.A4;

    /// <summary>Landscape (null: the report's own choice – a month grid and the year are landscape).</summary>
    public bool? Landscape { get; init; }

    /// <summary>Pages of the report on one sheet: 1, 2 or 4.</summary>
    public int PagesPerSheet { get; init; } = 1;

    /// <summary>Colours mixed with white (saves toner).</summary>
    public bool Lighter { get; init; }

    /// <summary>No filled backgrounds (only the outlines and the text).</summary>
    public bool NoBackgrounds { get; init; }

    /// <summary>A wider left margin to punch the sheets (on the right on the back pages of double-sided print).</summary>
    public bool PunchMargin { get; init; }

    public bool DoubleSided { get; init; }

    /// <summary>The title and the date on top and the page numbers below.</summary>
    public bool HeaderAndFooter { get; init; } = true;

    /// <summary>The size of the paper in points (portrait).</summary>
    public (double Width, double Height) PaperSize => Paper switch
    {
        Paper.A3 => (841.89, 1190.55),
        Paper.A5 => (419.53, 595.28),
        Paper.Letter => (612, 792),
        _ => (595.28, 841.89),
    };

    /// <summary>A colour as it is printed with these choices.</summary>
    public Rgb Ink(Rgb color) => Lighter ? color.Lighter(0.35) : color;

    /// <summary>A fill as it is printed (null: none).</summary>
    public Rgb? Fill(Rgb? color) => NoBackgrounds || color is null ? null : Lighter ? color.Value.Lighter(0.45) : color;
}

/// <summary>
/// The sheets of paper of a report: its pages one by one, or 2 or 4 on a sheet (scaled down, in reading order). Both the
/// PDF and the printing use it, so they place the pages the same.
/// </summary>
public static class Sheets
{
    /// <summary>
    /// The sheets: for each its size and the pages on it with where they go (the offset and the scale).
    /// </summary>
    public static IReadOnlyList<(double Width, double Height, IReadOnlyList<(ReportPage Page, double X, double Y, double Scale)> Pages)> Of(IReadOnlyList<ReportPage> pages, int perSheet)
    {
        if (perSheet is not (2 or 4) || pages.Count == 0)
        {
            return [.. pages.Select(p => (p.Width, p.Height, (IReadOnlyList<(ReportPage, double, double, double)>)[(p, 0d, 0d, 1d)]))];
        }

        var first = pages[0];
        // 2 on a sheet: the sheet turned (two portrait pages side by side on a landscape sheet and so on); 4: the same orientation
        var (width, height) = perSheet == 2 ? (first.Height, first.Width) : (first.Width, first.Height);
        var (columns, rows) = perSheet == 2 ? (width > height ? (2, 1) : (1, 2)) : (2, 2);
        var cellWidth = width / columns;
        var cellHeight = height / rows;
        var sheets = new List<(double, double, IReadOnlyList<(ReportPage, double, double, double)>)>();
        for (var start = 0; start < pages.Count; start += perSheet)
        {
            var placed = new List<(ReportPage, double, double, double)>();
            for (var i = 0; i < perSheet && start + i < pages.Count; i++)
            {
                var page = pages[start + i];
                var scale = Math.Min(cellWidth / page.Width, cellHeight / page.Height);
                var x = (i % columns * cellWidth) + ((cellWidth - (page.Width * scale)) / 2);
                var y = (i / columns * cellHeight) + ((cellHeight - (page.Height * scale)) / 2);
                placed.Add((page, x, y, scale));
            }

            sheets.Add((width, height, placed));
        }

        return sheets;
    }
}
