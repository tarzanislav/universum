using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Universum.Core.Reports;

/// <summary>The two faces of the font of a report (regular, bold): measuring text for the layout and embedding in a PDF.</summary>
public sealed class ReportFonts(TrueTypeFont regular, TrueTypeFont bold, string family)
{
    /// <summary>The fonts of Windows tried in order (regular, bold, family) – a font whose licence forbids embedding is skipped.</summary>
    public static IReadOnlyList<(string Regular, string Bold, string Family)> WindowsFaces { get; } =
    [
        ("segoeui.ttf", "segoeuib.ttf", "Segoe UI"), ("arial.ttf", "arialbd.ttf", "Arial"), ("tahoma.ttf", "tahomabd.ttf", "Tahoma"),
        ("verdana.ttf", "verdanab.ttf", "Verdana"), ("calibri.ttf", "calibrib.ttf", "Calibri"),
    ];

    public TrueTypeFont Regular { get; } = regular;

    public TrueTypeFont Bold { get; } = bold;

    /// <summary>The name of the family (printing on the screen and paper uses the same font).</summary>
    public string Family { get; } = family;

    public TrueTypeFont Of(bool bold) => bold ? Bold : Regular;

    /// <summary>The width of a text in points.</summary>
    public double Width(string text, double size, bool bold = false) => Of(bold).Width(text, size);

    /// <summary>Above the baseline, in points.</summary>
    public double Ascent(double size) => Regular.Ascent * size / Regular.UnitsPerEm;

    /// <summary>The distance of two lines, in points.</summary>
    public double LineHeight(double size) => (Regular.Ascent - Regular.Descent + Regular.LineGap) * size / Regular.UnitsPerEm;

    /// <summary>The first usable pair of faces in a folder of fonts (Windows: C:\Windows\Fonts); null when none can be used.</summary>
    public static ReportFonts? In(string folder, IEnumerable<(string Regular, string Bold, string Family)> faces)
    {
        foreach (var (regular, bold, family) in faces)
        {
            var r = Path.Combine(folder, regular);
            var b = Path.Combine(folder, bold);
            if (!File.Exists(r))
            {
                continue;
            }

            try
            {
                var font = TrueTypeFont.Load(r);
                return new ReportFonts(font, File.Exists(b) ? TrueTypeFont.Load(b) : font, family);
            }
            catch (Exception ex) when (ex is FontException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                // the next face
            }
        }

        return null;
    }
}

/// <summary>
/// UNIVERSUM's own writer of PDF (owner, 31. 10. 2026: "robíme nanovo, nič nepreberáme"): the laid-out pages of a report
/// on sheets (one page, or 2 or 4 on a sheet), text in the embedded subset of a TrueType font (Identity-H, with a
/// ToUnicode map so the text can be found and copied), filled and outlined boxes with rounded corners and lines; the
/// streams compressed (Flate). PDF 1.7.
/// </summary>
public static class PdfWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The PDF of the pages.</summary>
    public static byte[] Write(IReadOnlyList<ReportPage> pages, ReportFonts fonts, ReportOptions options, string title)
    {
        var used = new[] { new Dictionary<ushort, int>(), new Dictionary<ushort, int>() };   // glyph → character, regular and bold
        var sheets = Sheets.Of(pages, options.PagesPerSheet);
        var contents = sheets.Select(sheet => Content(sheet, fonts, options, used)).ToList();

        var pdf = new PdfFile();
        var catalog = pdf.Reserve();
        var pagesTree = pdf.Reserve();
        var fontObjects = new int[2];
        for (var f = 0; f < 2; f++)
        {
            fontObjects[f] = used[f].Count == 0 && f == 1 ? 0 : EmbedFont(pdf, fonts.Of(f == 1), used[f]);
        }

        var resources = "<< /Font << /F1 " + fontObjects[0] + " 0 R" + (fontObjects[1] > 0 ? " /F2 " + fontObjects[1] + " 0 R" : "") + " >> >>";
        var kids = new List<int>();
        for (var i = 0; i < sheets.Count; i++)
        {
            var stream = pdf.Stream("", contents[i], compress: true);
            kids.Add(pdf.Add($"<< /Type /Page /Parent {pagesTree} 0 R /MediaBox [0 0 {N(sheets[i].Width)} {N(sheets[i].Height)}] /Resources {resources} /Contents {stream} 0 R >>"));
        }

        pdf.Set(pagesTree, $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => k + " 0 R"))}] /Count {kids.Count} >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pagesTree} 0 R >>");
        var info = pdf.Add($"<< /Producer (UNIVERSUM) /Title {Utf16(title)} /CreationDate (D:{DateTime.UtcNow:yyyyMMddHHmmss}Z) >>");
        return pdf.Bytes(catalog, info);
    }

    // the drawing of a sheet; the glyphs used are noted
    private static byte[] Content((double Width, double Height, IReadOnlyList<(ReportPage Page, double X, double Y, double Scale)> Pages) sheet, ReportFonts fonts, ReportOptions options,
        Dictionary<ushort, int>[] used)
    {
        var s = new StringBuilder();
        foreach (var (page, x, y, scale) in sheet.Pages)
        {
            // page points (from the top left) on the sheet: y grows downwards
            s.Append("q ").Append(N(scale)).Append(" 0 0 ").Append(N(-scale)).Append(' ').Append(N(x)).Append(' ').Append(N(sheet.Height - y)).Append(" cm\n");
            if (sheet.Pages.Count > 1)
            {
                s.Append("0.8 0.8 0.8 RG 0.5 w 0 0 ").Append(N(page.Width)).Append(' ').Append(N(page.Height)).Append(" re S\n");
            }

            foreach (var item in page.Items)
            {
                switch (item)
                {
                    case BoxItem box:
                        Box(s, box, options);
                        break;
                    case LineItem line:
                        s.Append(Color(options.Ink(line.Color), stroke: true)).Append(N(line.Width)).Append(" w ").Append(N(line.X1)).Append(' ').Append(N(line.Y1))
                            .Append(" m ").Append(N(line.X2)).Append(' ').Append(N(line.Y2)).Append(" l S\n");
                        break;
                    case TextItem text when text.Text.Length > 0:
                        var face = text.Bold ? 1 : 0;
                        var font = fonts.Of(text.Bold);
                        var glyphs = new StringBuilder();
                        foreach (var rune in text.Text.EnumerateRunes())
                        {
                            var glyph = font.Glyph(rune.Value);
                            used[face].TryAdd(glyph, rune.Value);
                            glyphs.Append(glyph.ToString("X4", Invariant));
                        }

                        s.Append(Color(options.Ink(text.Color), stroke: false)).Append("BT /F").Append(face + 1).Append(' ').Append(N(text.Size)).Append(" Tf 1 0 0 -1 ")
                            .Append(N(text.X)).Append(' ').Append(N(text.Y)).Append(" Tm <").Append(glyphs).Append("> Tj ET\n");
                        break;
                }
            }

            s.Append("Q\n");
        }

        return Encoding.ASCII.GetBytes(s.ToString());
    }

    private static void Box(StringBuilder s, BoxItem box, ReportOptions options)
    {
        var fill = options.Fill(box.Fill);
        var stroke = box.Stroke is { } line ? options.Ink(line) : (Rgb?)null;
        if (fill is null && stroke is null)
        {
            return;
        }

        if (fill is { } f)
        {
            s.Append(Color(f, stroke: false));
        }

        if (stroke is { } st)
        {
            s.Append(Color(st, stroke: true)).Append(N(box.StrokeWidth)).Append(" w ");
        }

        var r = Math.Min(box.Radius, Math.Min(box.Width, box.Height) / 2);
        if (r <= 0)
        {
            s.Append(N(box.X)).Append(' ').Append(N(box.Y)).Append(' ').Append(N(box.Width)).Append(' ').Append(N(box.Height)).Append(" re ");
        }
        else
        {
            // a rounded rectangle: four lines and four quarter circles (Bézier, k = 0.5523)
            double x0 = box.X, y0 = box.Y, x1 = box.X + box.Width, y1 = box.Y + box.Height, k = r * 0.5523;
            s.Append(N(x0 + r)).Append(' ').Append(N(y0)).Append(" m ")
                .Append(N(x1 - r)).Append(' ').Append(N(y0)).Append(" l ")
                .Append(N(x1 - r + k)).Append(' ').Append(N(y0)).Append(' ').Append(N(x1)).Append(' ').Append(N(y0 + r - k)).Append(' ').Append(N(x1)).Append(' ').Append(N(y0 + r)).Append(" c ")
                .Append(N(x1)).Append(' ').Append(N(y1 - r)).Append(" l ")
                .Append(N(x1)).Append(' ').Append(N(y1 - r + k)).Append(' ').Append(N(x1 - r + k)).Append(' ').Append(N(y1)).Append(' ').Append(N(x1 - r)).Append(' ').Append(N(y1)).Append(" c ")
                .Append(N(x0 + r)).Append(' ').Append(N(y1)).Append(" l ")
                .Append(N(x0 + r - k)).Append(' ').Append(N(y1)).Append(' ').Append(N(x0)).Append(' ').Append(N(y1 - r + k)).Append(' ').Append(N(x0)).Append(' ').Append(N(y1 - r)).Append(" c ")
                .Append(N(x0)).Append(' ').Append(N(y0 + r)).Append(" l ")
                .Append(N(x0)).Append(' ').Append(N(y0 + r - k)).Append(' ').Append(N(x0 + r - k)).Append(' ').Append(N(y0)).Append(' ').Append(N(x0 + r)).Append(' ').Append(N(y0)).Append(" c h ");
        }

        s.Append(fill is not null && stroke is not null ? "B\n" : fill is not null ? "f\n" : "S\n");
    }

    // the font: Type0 → CIDFontType2 (glyph numbers as CIDs) with the subset and the map back to Unicode
    private static int EmbedFont(PdfFile pdf, TrueTypeFont font, Dictionary<ushort, int> used)
    {
        var tag = string.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(font.Name + string.Join(",", used.Keys.Order()))).Take(6).Select(b => (char)('A' + (b % 26))));
        var name = tag + "+" + font.Name;
        var subset = font.Subset(used.Keys);
        var file = pdf.Stream($"/Length1 {subset.Length}", subset, compress: true);
        double Scale(double units) => units * 1000 / font.UnitsPerEm;
        var box = font.BoundingBox;
        var descriptor = pdf.Add($"<< /Type /FontDescriptor /FontName /{name} /Flags 32 /FontBBox [{N(Scale(box.XMin))} {N(Scale(box.YMin))} {N(Scale(box.XMax))} {N(Scale(box.YMax))}] " +
                                 $"/ItalicAngle 0 /Ascent {N(Scale(font.Ascent))} /Descent {N(Scale(font.Descent))} /CapHeight {N(Scale(font.CapHeight))} /StemV 80 /FontFile2 {file} 0 R >>");
        var widths = string.Join(" ", used.Keys.Order().Select(g => $"{g} [{N(Scale(font.Advance(g)))}]"));
        var cid = pdf.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                          $"/FontDescriptor {descriptor} 0 R /DW {N(Scale(font.Advance(0)))} /W [{widths}] /CIDToGIDMap /Identity >>");
        var map = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
                                    "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var chunk in used.Where(u => u.Key != 0).OrderBy(u => u.Key).Chunk(100))
        {
            map.Append(chunk.Length).Append(" beginbfchar\n");
            foreach (var (glyph, character) in chunk)
            {
                map.Append('<').Append(glyph.ToString("X4", Invariant)).Append("> <").Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(char.ConvertFromUtf32(character)))).Append(">\n");
            }

            map.Append("endbfchar\n");
        }

        map.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var unicode = pdf.Stream("", Encoding.ASCII.GetBytes(map.ToString()), compress: true);
        return pdf.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {unicode} 0 R >>");
    }

    private static string Color(Rgb c, bool stroke) =>
        $"{N(c.R / 255.0)} {N(c.G / 255.0)} {N(c.B / 255.0)} {(stroke ? "RG" : "rg")} ";

    // a number as PDF writes it: at most 3 decimals, no exponent
    private static string N(double value) => Math.Round(value, 3).ToString("0.###", Invariant);

    private static string Utf16(string text) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    // the objects of a PDF and their byte offsets
    private sealed class PdfFile
    {
        private readonly List<byte[]?> _objects = [];

        public int Reserve()
        {
            _objects.Add(null);
            return _objects.Count;
        }

        public int Add(string dictionary)
        {
            _objects.Add(Encoding.ASCII.GetBytes(dictionary));
            return _objects.Count;
        }

        public void Set(int number, string dictionary) => _objects[number - 1] = Encoding.ASCII.GetBytes(dictionary);

        public int Stream(string entries, byte[] data, bool compress)
        {
            var body = data;
            if (compress)
            {
                using var packed = new MemoryStream();
                using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
                {
                    zlib.Write(data);
                }

                body = packed.ToArray();
            }

            var head = Encoding.ASCII.GetBytes($"<< /Length {body.Length}{(compress ? " /Filter /FlateDecode" : "")} {entries} >>\nstream\n");
            var tail = Encoding.ASCII.GetBytes("\nendstream");
            _objects.Add([.. head, .. body, .. tail]);
            return _objects.Count;
        }

        public byte[] Bytes(int catalog, int info)
        {
            var output = new MemoryStream();
            void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
            Write("%PDF-1.7\n");
            output.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);   // binary marker
            var offsets = new long[_objects.Count];
            for (var i = 0; i < _objects.Count; i++)
            {
                offsets[i] = output.Length;
                Write($"{i + 1} 0 obj\n");
                output.Write(_objects[i] ?? Encoding.ASCII.GetBytes("null"));
                Write("\nendobj\n");
            }

            var xref = output.Length;
            Write($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                Write($"{offset:D10} 00000 n \n");
            }

            Write($"trailer\n<< /Size {_objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return output.ToArray();
        }
    }
}
