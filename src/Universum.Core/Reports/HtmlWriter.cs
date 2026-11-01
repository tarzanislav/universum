using System.Globalization;
using System.Text;

namespace Universum.Core.Reports;

/// <summary>
/// A report as one HTML page of its own (no files beside it): the laid-out pages placed exactly as in the PDF, in the
/// font of the system – to open in a browser, share or print from there.
/// </summary>
public static class HtmlWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Write(IReadOnlyList<ReportPage> pages, ReportOptions options, string title)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"sk\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>")
            .Append(Escape(title)).Append("</title>\n<style>\n")
            .Append("body{margin:0;background:#e8e8e8;font-family:'Segoe UI',Arial,sans-serif}\n")
            .Append(".page{position:relative;background:#fff;margin:16px auto;box-shadow:0 1px 4px rgba(0,0,0,.25);overflow:hidden}\n")
            .Append(".t{position:absolute;white-space:pre;line-height:1}\n.b{position:absolute;box-sizing:border-box}\n")
            .Append("@media print{body{background:#fff}.page{margin:0;box-shadow:none;page-break-after:always}}\n</style>\n</head>\n<body>\n");
        foreach (var page in pages)
        {
            html.Append("<div class=\"page\" style=\"width:").Append(N(page.Width)).Append("pt;height:").Append(N(page.Height)).Append("pt\">\n");
            foreach (var item in page.Items)
            {
                switch (item)
                {
                    case BoxItem box when options.Fill(box.Fill) is not null || box.Stroke is not null:
                        html.Append("<div class=\"b\" style=\"left:").Append(N(box.X)).Append("pt;top:").Append(N(box.Y)).Append("pt;width:").Append(N(box.Width))
                            .Append("pt;height:").Append(N(box.Height)).Append("pt");
                        if (options.Fill(box.Fill) is { } fill)
                        {
                            html.Append(";background:").Append(fill.Hex);
                        }

                        if (box.Stroke is { } stroke)
                        {
                            html.Append(";border:").Append(N(box.StrokeWidth)).Append("pt solid ").Append(options.Ink(stroke).Hex);
                        }

                        if (box.Radius > 0)
                        {
                            html.Append(";border-radius:").Append(N(box.Radius)).Append("pt");
                        }

                        html.Append("\"></div>\n");
                        break;
                    case LineItem line:
                        var length = Math.Sqrt(Math.Pow(line.X2 - line.X1, 2) + Math.Pow(line.Y2 - line.Y1, 2));
                        var angle = Math.Atan2(line.Y2 - line.Y1, line.X2 - line.X1) * 180 / Math.PI;
                        html.Append("<div class=\"b\" style=\"left:").Append(N(line.X1)).Append("pt;top:").Append(N(line.Y1 - (line.Width / 2))).Append("pt;width:").Append(N(length))
                            .Append("pt;height:").Append(N(line.Width)).Append("pt;background:").Append(options.Ink(line.Color).Hex)
                            .Append(";transform-origin:0 50%;transform:rotate(").Append(N(angle)).Append("deg)\"></div>\n");
                        break;
                    case TextItem text:
                        // the baseline: the top of the line is about 0.8 of the size above it
                        html.Append("<div class=\"t\" style=\"left:").Append(N(text.X)).Append("pt;top:").Append(N(text.Y - (text.Size * 0.93))).Append("pt;font-size:").Append(N(text.Size))
                            .Append("pt;color:").Append(options.Ink(text.Color).Hex).Append(text.Bold ? ";font-weight:600" : "").Append("\">")
                            .Append(Escape(text.Text)).Append("</div>\n");
                        break;
                }
            }

            html.Append("</div>\n");
        }

        return html.Append("</body>\n</html>\n").ToString();
    }

    // only what HTML needs escaped: letters with diacritics stay readable (UTF-8)
    private static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string N(double value) => Math.Round(value, 2).ToString("0.##", Invariant);
}
