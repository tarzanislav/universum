using System.Text;
using Universum.Core.Guides;
using Universum.Core.Reports;

namespace Universum.Tests;

/// <summary>Návody: UNIVERSUM's own markup of a guide, the contents and search, and the guide laid out with a picture into a PDF.</summary>
public sealed class GuideTests
{
    // a 16 × 10 JPEG (baseline, colour) made for the test
    private const string Picture =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAAKABADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDGooorc/Sj/9k=";

    private const string Text = """
        # Vymyslený návod
        Krátky úvod s **tučným** slovom,
        *kurzívou* a tlačidlom [Spracovať].

        ## Prvá časť
        - jedna odrážka
          pokračuje na ďalšom riadku
        - druhá odrážka
        1. prvý krok
        2. druhý krok
        !tip Užitočná rada.
          Ešte jedna veta rady.
        ![Vymyslený obrázok](obrazok.jpg)
        // poznámka pre autora, nezobrazí sa
        @otvorit Calendar Otvoriť Kalendár

        ## Druhá časť
        ### Menší nadpis
        !pozor Pozor na čiaru * bez páru a \[hranaté\] zátvorky.
        """;

    [Test]
    public void MarkupOfAGuide()
    {
        var guide = GuideDocument.Parse(Text);
        var kinds = guide.Blocks.Select(b => b.Kind).ToList();
        Assert.True(kinds.SequenceEqual([GuideBlockKind.Title, GuideBlockKind.Paragraph, GuideBlockKind.Section, GuideBlockKind.Bullet, GuideBlockKind.Bullet, GuideBlockKind.Step,
                GuideBlockKind.Step, GuideBlockKind.Box, GuideBlockKind.Image, GuideBlockKind.Open, GuideBlockKind.Section, GuideBlockKind.Subsection, GuideBlockKind.Box]),
            "the blocks in order – " + string.Join(", ", kinds));
        Assert.True(guide.Title == "Vymyslený návod" && guide.Sections.Select(s => s.Title).SequenceEqual(["Prvá časť", "Druhá časť"]), "the title and the contents");

        var intro = guide.Blocks[1].Spans;
        Assert.True(intro.Any(s => s is { Text: "tučným", Bold: true }) && intro.Any(s => s is { Text: "kurzívou", Italic: true }) && intro.Any(s => s is { Text: "Spracovať", Key: true }) &&
                    guide.Blocks[1].Plain == "Krátky úvod s tučným slovom, kurzívou a tlačidlom Spracovať.", "bold, italic, a button; the lines of a paragraph joined – " + guide.Blocks[1].Plain);
        Assert.True(guide.Blocks[3].Plain == "jedna odrážka pokračuje na ďalšom riadku" && guide.Blocks[6].Number == 2, "a list line going on; the steps numbered");
        Assert.True(guide.Blocks[7] is { Box: GuideBoxKind.Tip } tip && tip.Plain == "Užitočná rada. Ešte jedna veta rady.", "a tip box of two lines");
        Assert.True(guide.Blocks[8] is { Target: "obrazok.jpg" } image && image.Plain == "Vymyslený obrázok" && guide.Pictures.SequenceEqual(["obrazok.jpg"]), "a picture with its caption");
        Assert.True(guide.Blocks[9] is { Target: "Calendar" } open && open.Plain == "Otvoriť Kalendár", "a button opening a category");
        Assert.True(guide.Blocks[12] is { Box: GuideBoxKind.Warning } warning && warning.Plain == "Pozor na čiaru * bez páru a [hranaté] zátvorky.", "a lone mark and escaped brackets stay text – " + guide.Blocks[12].Plain);

        Assert.True(guide.Find("rada ODRAZKA").Select(s => s.Title).SequenceEqual(["Prvá časť"]) && guide.Find("hranate").Select(s => s.Title).SequenceEqual(["Druhá časť"]) &&
                    guide.Find("").Count == 2 && guide.Find("nič také").Count == 0, "looked for in the parts without diacritics");
    }

    [Test]
    public void JpegHeader()
    {
        var jpeg = Jpeg.Read(Convert.FromBase64String(Picture));
        Assert.True(jpeg is { Width: 16, Height: 10, Components: 3 }, "the size and colours of a JPEG from its frame header");
        Assert.True(Jpeg.Read([0x89, 0x50, 0x4E, 0x47]) is null && Jpeg.Read([0xFF, 0xD8, 0xFF, 0xC2, 0x00, 0x11, 0x08, 0x00, 0x0A, 0x00, 0x10, 0x03]) is null, "not a PNG, not a progressive JPEG");
    }

    [Test]
    public void GuideInAPdfWithItsPicture()
    {
        var fonts = ReportFonts.In("/usr/share/fonts/truetype/dejavu", [("DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVu Sans")]) ??
                    ReportFonts.In("/usr/share/fonts/truetype/liberation", [("LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf", "Liberation Sans")]);
        if (fonts is null)
        {
            Assert.Skip("no TrueType font on this computer");
            return;
        }

        var jpeg = Jpeg.Read(Convert.FromBase64String(Picture))!;
        var guide = GuideDocument.Parse(Text);
        var pages = GuideReport.Layout(guide, fonts, new ReportOptions(), new Rgb(0xD4, 0x6A, 0x8C), name => name == "obrazok.jpg" ? jpeg : null);
        var items = pages.SelectMany(p => p.Items).ToList();
        Assert.True(items.OfType<ImageItem>().Count() == 1 && items.OfType<TextItem>().Any(t => t is { Text: "Spracovať", Bold: true }) &&
                    items.OfType<TextItem>().Any(t => t.Text == "tučným" && t.Bold), "the picture placed, the button and the bold word in their style");

        var pdf = PdfWriter.Write(pages, fonts, new ReportOptions(), guide.Title);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "universum-navod.pdf"), pdf);
        var raw = Encoding.Latin1.GetString(pdf);
        Assert.True(raw.Contains("/Subtype /Image /Width 16 /Height 10 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", StringComparison.Ordinal) &&
                    System.Text.RegularExpressions.Regex.IsMatch(raw, @"/XObject << /Im(\d+) \1 0 R >>"),
            "the JPEG in the PDF as it is, named in the resources");
        var html = HtmlWriter.Write(pages, new ReportOptions(), guide.Title);
        Assert.True(html.Contains("data:image/jpeg;base64,/9j/", StringComparison.Ordinal), "the picture in the HTML");
    }
}
