using System.Globalization;

namespace Universum.Core.Reports;

/// <summary>
/// Lays out a report on pages (owner, 31. 10. 2026 – POZNÁMKY): the paper and margins of the choices (a wider margin for
/// punching), the title and the date on top, text measured by the report's font and wrapped by words, a new page when the
/// space runs out, and the page numbers ("2 / 5") at the end. What is laid out here prints and saves (PDF, HTML) the same.
/// </summary>
public sealed class ReportLayout
{
    private const double Margin = 36;
    private const double Punch = 22;
    private readonly List<ReportPage> _pages = [];
    private readonly string _title;
    private readonly string _stamp;

    public ReportLayout(ReportFonts fonts, ReportOptions options, string title, bool landscape, string? stamp = null)
    {
        Fonts = fonts;
        Options = options;
        _title = title;
        _stamp = stamp ?? DateTime.Now.ToString("d. M. yyyy HH:mm", CultureInfo.InvariantCulture);
        var (w, h) = options.PaperSize;
        (PageWidth, PageHeight) = options.Landscape ?? landscape ? (h, w) : (w, h);
        NewPage();
    }

    public ReportFonts Fonts { get; }

    public ReportOptions Options { get; }

    public double PageWidth { get; }

    public double PageHeight { get; }

    /// <summary>The page being laid out.</summary>
    public ReportPage Page { get; private set; } = null!;

    /// <summary>Where the next line goes (its top).</summary>
    public double Y { get; set; }

    public double Left { get; private set; }

    public double Right { get; private set; }

    public double Width => Right - Left;

    public double Top { get; private set; }

    public double Bottom => PageHeight - Margin - (Options.HeaderAndFooter ? 14 : 0);

    /// <summary>The space left on the page.</summary>
    public double Room => Bottom - Y;

    public void NewPage()
    {
        Page = new ReportPage(PageWidth, PageHeight);
        _pages.Add(Page);
        var back = Options.DoubleSided && _pages.Count % 2 == 0;
        Left = Margin + (Options.PunchMargin && !back ? Punch : 0);
        Right = PageWidth - Margin - (Options.PunchMargin && back ? Punch : 0);
        Top = Margin;
        if (Options.HeaderAndFooter)
        {
            Text(Left, Top + 8, Fit(_title, Width * 0.7, 8.5, false), 8.5, false, Rgb.Gray);
            var stamp = "UNIVERSUM · " + _stamp;
            Text(Right - Fonts.Width(stamp, 8), Top + 8, stamp, 8, false, Rgb.Gray);
            Line(Left, Top + 12, Right, Top + 12, Rgb.Line);
            Top += 20;
        }

        Y = Top;
    }

    /// <summary>A new page when <paramref name="height"/> does not fit any more.</summary>
    public void Ensure(double height)
    {
        if (Y + height > Bottom && Y > Top)
        {
            NewPage();
        }
    }

    public void Text(double x, double baseline, string text, double size, bool bold, Rgb color) =>
        Page.Items.Add(new TextItem(x, baseline, text, size, bold, color));

    public void Box(double x, double y, double width, double height, Rgb? fill, Rgb? stroke = null, double strokeWidth = 0.5, double radius = 0) =>
        Page.Items.Add(new BoxItem(x, y, width, height, fill, stroke, strokeWidth, radius));

    public void Line(double x1, double y1, double x2, double y2, Rgb color, double width = 0.5) =>
        Page.Items.Add(new LineItem(x1, y1, x2, y2, color, width));

    /// <summary>A heading of the report.</summary>
    public void Heading(string text, double size = 16, Rgb? color = null)
    {
        Ensure(Fonts.LineHeight(size) + 6);
        foreach (var line in Wrap(text, Width, size, true))
        {
            Text(Left, Y + Fonts.Ascent(size), line, size, true, color ?? Rgb.Black);
            Y += Fonts.LineHeight(size);
        }

        Y += 4;
    }

    /// <summary>A paragraph wrapped by words (a page break between its lines when needed).</summary>
    public void Paragraph(string text, double size = 10, Rgb? color = null, double indent = 0, bool bold = false)
    {
        foreach (var line in text.Split('\n').SelectMany(l => Wrap(l, Width - indent, size, bold)))
        {
            Ensure(Fonts.LineHeight(size));
            Text(Left + indent, Y + Fonts.Ascent(size), line, size, bold, color ?? Rgb.Black);
            Y += Fonts.LineHeight(size);
        }
    }

    public void Space(double height) => Y += height;

    /// <summary>The lines of a text wrapped to a width (a word longer than the line is broken).</summary>
    public IReadOnlyList<string> Wrap(string text, double width, double size, bool bold)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (Fonts.Width(candidate, size, bold) <= width)
            {
                line = candidate;
                continue;
            }

            if (line.Length > 0)
            {
                lines.Add(line);
            }

            line = word;
            while (Fonts.Width(line, size, bold) > width && line.Length > 1)
            {
                var cut = line.Length - 1;
                while (cut > 1 && Fonts.Width(line[..cut], size, bold) > width)
                {
                    cut--;
                }

                lines.Add(line[..cut]);
                line = line[cut..];
            }
        }

        if (line.Length > 0 || lines.Count == 0)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>The text cut to a width with "…".</summary>
    public string Fit(string text, double width, double size, bool bold)
    {
        if (Fonts.Width(text, size, bold) <= width)
        {
            return text;
        }

        var cut = text.Length;
        while (cut > 0 && Fonts.Width(text[..cut] + "…", size, bold) > width)
        {
            cut--;
        }

        return cut == 0 ? "" : text[..cut].TrimEnd() + "…";
    }

    /// <summary>The pages with their numbers ("2 / 5").</summary>
    public IReadOnlyList<ReportPage> Finish()
    {
        if (Options.HeaderAndFooter)
        {
            for (var i = 0; i < _pages.Count; i++)
            {
                var number = $"{i + 1} / {_pages.Count}";
                var page = _pages[i];
                page.Items.Add(new TextItem((page.Width - Fonts.Width(number, 8)) / 2, page.Height - Margin + 4, number, 8, false, Rgb.Gray));
            }
        }

        return _pages;
    }
}
