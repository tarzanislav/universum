using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Universum.Core.Agenda;
using Universum.Core.Calendar;
using Universum.Core.Reports;

namespace Universum.Tests;

/// <summary>
/// The printed forms of POZNÁMKY: a TrueType font read and subset, the own PDF writer (structure, embedded font, text that
/// can be found again with Slovak letters, 2 pages on a sheet), the layout, HTML, iCalendar, CSV and Markdown.
/// </summary>
public sealed class ReportTests
{
    // a font of the Linux of the tests (DejaVu or Liberation); the test is skipped without one
    private static ReportFonts? Fonts() =>
        ReportFonts.In("/usr/share/fonts/truetype/dejavu", [("DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVu Sans")]) ??
        ReportFonts.In("/usr/share/fonts/truetype/liberation", [("LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf", "Liberation Sans")]);

    [Test]
    public void FontReadAndSubset()
    {
        if (Fonts() is not { } fonts)
        {
            Assert.Skip("no TrueType font on this computer");
            return;
        }

        var font = fonts.Regular;
        Assert.True(font.Glyph('ž') > 0 && font.Glyph('Ľ') > 0 && font.Glyph('ô') > 0, "Slovak letters have glyphs");
        Assert.True(font.Width("Žltý kôň", 10) > font.Width("Žltý", 10) && font.Width("", 10) == 0, "widths of text");
        var used = "Žltý kôň úpel ďábelské ódy".EnumerateRunes().Select(r => font.Glyph(r.Value)).ToList();
        var subset = font.Subset(used);
        var again = new TrueTypeFont(subset);
        Assert.True(subset.Length < File.ReadAllBytes(Directory.GetFiles("/usr/share/fonts/truetype", "*.ttf", SearchOption.AllDirectories).First(f => Path.GetFileName(f) == "DejaVuSans.ttf" || Path.GetFileName(f) == "LiberationSans-Regular.ttf")).Length / 3,
            "the subset is much smaller – " + subset.Length);
        Assert.True(again.GlyphCount == font.GlyphCount && again.Advance(font.Glyph('ž')) == font.Advance(font.Glyph('ž')), "the numbers and widths of glyphs stay");
    }

    [Test]
    public void PdfOfAReport()
    {
        if (Fonts() is not { } fonts)
        {
            Assert.Skip("no TrueType font on this computer");
            return;
        }

        var options = new ReportOptions();
        var layout = new ReportLayout(fonts, options, "Vymyslený kalendár", landscape: false, stamp: "1. 11. 2026");
        var days = Enumerable.Range(0, 40).Select(i => new ReportDay(new DateOnly(2026, 11, 1).AddDays(i), $"Deň {i + 1}", ["Žofia"], i == 16 ? "Deň boja za slobodu a demokraciu" : "",
            i == 16, false, [new ReportEntry("20:00", "Kino – vymyslený film ďábelský", "ťažké ľúbostné ódy", new Rgb(0xD4, 0x6A, 0x8C))]));
        AgendaReports.DayList(layout, "Vymyslený zoznam dní", days);
        var pages = layout.Finish();
        Assert.True(pages.Count >= 2, "forty days need more pages – " + pages.Count);
        var pdf = PdfWriter.Write(pages, fonts, options, "Vymyslený kalendár");
        var text = Encoding.Latin1.GetString(pdf);
        Assert.True(text.StartsWith("%PDF-1.7", StringComparison.Ordinal) && text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal), "a PDF");
        Assert.True(Regex.Matches(text, @"/Type /Page\b").Count == pages.Count && text.Contains("/FontFile2", StringComparison.Ordinal) && text.Contains("/ToUnicode", StringComparison.Ordinal),
            "every page, the embedded font and its map to Unicode");

        // the cross-reference table points at the objects
        var start = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var entries = Regex.Matches(text[start..], @"(\d{10}) 00000 n ").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.True(entries.Count > 8 && entries.Select((offset, i) => text[offset..].StartsWith($"{i + 1} 0 obj", StringComparison.Ordinal)).All(ok => ok), "every offset of the table is right");

        // the map to Unicode holds the Slovak letters (the text can be found and copied)
        var maps = string.Concat(Streams(pdf).Where(s => s.Contains("beginbfchar", StringComparison.Ordinal)));
        Assert.True(maps.Contains("<017E>", StringComparison.Ordinal) && maps.Contains("<013E>", StringComparison.Ordinal) && maps.Contains("<0165>", StringComparison.Ordinal), "ž, ľ, ť mapped back");

        var twoUp = PdfWriter.Write(pages, fonts, options with { PagesPerSheet = 2 }, "Vymyslený kalendár");
        Assert.True(Regex.Matches(Encoding.Latin1.GetString(twoUp), @"/Type /Page\b").Count == (pages.Count + 1) / 2, "two pages on a sheet");
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "universum-test-report.pdf"), pdf);
    }

    [Test]
    public void CalendarFormsFitTheirPages()
    {
        if (Fonts() is not { } fonts)
        {
            Assert.Skip("no TrueType font on this computer");
            return;
        }

        ReportDay Day(DateOnly d) => new(d, d.ToString("ddd", System.Globalization.CultureInfo.GetCultureInfo("sk-SK")), NameDays.Slovak(d),
            string.Join(" · ", Holidays.On(d, [HolidayRegions.Slovakia]).Where(h => h.DayOff).Select(h => h.NameSk)), Holidays.IsDayOff(d, HolidayRegions.Slovakia), false,
            d.Day % 5 == 0 ? [new ReportEntry("", "Vymyslená premiéra", "", new Rgb(0x6F, 0xA8, 0xE8))] : []);
        string[] weekdays = ["po", "ut", "st", "št", "pi", "so", "ne"];
        string[] months = ["jan", "feb", "mar", "apr", "máj", "jún", "júl", "aug", "sep", "okt", "nov", "dec"];
        foreach (var form in new Action<ReportLayout>[]
                 {
                     l => AgendaReports.MonthGrid(l, "November 2026", 2026, 11, weekdays, Day),
                     l => AgendaReports.YearRows(l, "2026", 2026, months, Day),
                     l => AgendaReports.YearMonths(l, "2026", 2026, months, weekdays, Day),
                 })
        {
            var layout = new ReportLayout(fonts, new ReportOptions(), "Vymyslený", landscape: true);
            form(layout);
            var pages = layout.Finish();
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"universum-test-form-{pages[0].Items.Count}.pdf"), PdfWriter.Write(pages, fonts, new ReportOptions(), "Vymyslený"));
            Assert.True(pages.Count == 1 && pages[0].Width > pages[0].Height && pages[0].Items.OfType<TextItem>().All(t => t.Y <= pages[0].Height && t.X >= 0 && t.X <= pages[0].Width),
                "one landscape page, everything on it");
        }

        var tasks = new ReportLayout(fonts, new ReportOptions { PunchMargin = true }, "Úlohy", landscape: false);
        AgendaReports.Tasks(tasks, "Vymyslené úlohy", [("Dnes", new Rgb(0xD4, 0x6A, 0x8C), [new ReportTask("Spracovať vymyslený film", "dnes 20:00", "", false, false, 2, "#kino", [("krok", true), ("ďalší krok", false)])])]);
        var notes = new ReportLayout(fonts, new ReportOptions(), "Poznámky", landscape: false);
        AgendaReports.Notes(notes, "Vymyslené poznámky", [new ReportNote("Nápad", string.Join(" ", Enumerable.Repeat("vymyslený text poznámky", 400)), "k dňu 1. 11. 2026", "#nápad", new Rgb(0xC9, 0xA2, 0x27), true)]);
        var taskPages = tasks.Finish();
        Assert.True(taskPages[0].Items.OfType<TextItem>().Where(t => t.Size > 8.5).Min(t => t.X) >= 36 + 22 && notes.Finish().Count >= 2, "the punch margin; a long note goes on");
        var html = HtmlWriter.Write(taskPages, new ReportOptions(), "Úlohy");
        Assert.True(html.Contains("Spracovať vymyslený film", StringComparison.Ordinal) && html.Contains("class=\"page\"", StringComparison.Ordinal), "HTML of the report");
    }

    [Test]
    public void IcsCsvAndMarkdown()
    {
        var cinema = new AgendaItem
        {
            Title = "Kino; vymyslený film, večer",
            Text = "Dlhý vymyslený popis, ktorý je určite dlhší než sedemdesiatpäť bajtov, aby sa riadok musel zalomiť – ľščťžýáíé.",
            Tags = ["kino"],
            Time = new AgendaTime
            {
                Start = new DateOnly(2026, 11, 6), StartTime = new TimeOnly(20, 0), EndTime = new TimeOnly(22, 15), ReminderMinutes = 60,
                Repeat = new Recurrence { Every = RepeatEvery.Week, Interval = 2, Days = [DayOfWeek.Friday], Until = new DateOnly(2026, 12, 31) }, Skipped = [new DateOnly(2026, 11, 20)],
            },
        };
        var task = new AgendaItem
        {
            Title = "Spracovať vymyslený seriál",
            Task = new AgendaTask { Due = new DateOnly(2026, 11, 7), Priority = 2, Checklist = [new CheckItem { Text = "stiahnuť", Done = true }, new CheckItem { Text = "spracovať" }] },
        };
        var ics = AgendaExport.Ics([cinema, task], Holidays.Of(2026, HolidayRegions.Slovakia).Where(h => h.Date.Month == 11));
        var lines = ics.Split("\r\n");
        Assert.True(lines[0] == "BEGIN:VCALENDAR" && lines.Contains("DTSTART:20261106T200000") && lines.Contains("DTEND:20261106T221500") &&
                    lines.Contains("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=FR;UNTIL=20261231T235959") && lines.Contains("EXDATE:20261120T200000") && lines.Contains("TRIGGER:-PT60M"),
            "the event, its repeat, a cancelled day, the reminder");
        Assert.True(lines.Contains(@"SUMMARY:Kino\; vymyslený film\, večer") && lines.Contains("DUE;VALUE=DATE:20261107") && lines.Contains("PRIORITY:1") &&
                    lines.Contains("STATUS:NEEDS-ACTION") && ics.Contains("[x] stiahnuť", StringComparison.Ordinal), "the task, escaping");
        Assert.True(lines.All(l => Encoding.UTF8.GetByteCount(l) <= 75) && lines.Any(l => l.StartsWith(' ')), "lines folded at 75 bytes");
        Assert.True(lines.Count(l => l == "BEGIN:VEVENT") == 1 + 2, "the event and the two November holidays (1 and 17 November)");

        var csv = AgendaExport.Csv([cinema, task]);
        Assert.True(csv.StartsWith("﻿Druh;Názov;", StringComparison.Ordinal) && csv.Contains("\"Kino; vymyslený film, večer\"", StringComparison.Ordinal) &&
                    csv.Contains("2026-11-07", StringComparison.Ordinal), "CSV for Excel");
        var md = AgendaExport.Markdown("Vymyslené", [task, new AgendaItem { Title = "Nápad", Text = "Vymyslený text", Note = new AgendaNote() }]);
        Assert.True(md.Contains("- [ ] Spracovať vymyslený seriál (do 7. 11. 2026)", StringComparison.Ordinal) && md.Contains("  - [x] stiahnuť", StringComparison.Ordinal) &&
                    md.Contains("## Nápad", StringComparison.Ordinal), "Markdown");
    }

    [Test]
    public void BackupAndImport()
    {
        var folder = Directory.CreateTempSubdirectory("universum-backup-").FullName;
        try
        {
            var store = new AgendaStore(Path.Combine(folder, "a.json"));
            var note = new AgendaItem { Title = "Vymyslená poznámka", Note = new AgendaNote() };
            store.Save(note);
            var backup = Path.Combine(folder, "zaloha.json");
            store.Export(backup);
            var other = new AgendaStore(Path.Combine(folder, "b.json"));
            Assert.True(other.Import(backup) == 1 && other.Items.Single().Title == "Vymyslená poznámka" && other.Import(backup) == 0, "imported once; the same again changes nothing");
            File.WriteAllText(Path.Combine(folder, "zle.json"), "{}");
            Assert.Throws<System.Text.Json.JsonException>(() => other.Import(Path.Combine(folder, "zle.json")), "not a backup");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // the streams of a PDF unpacked
    private static IEnumerable<string> Streams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        foreach (Match m in Regex.Matches(text, @"/Length (\d+) /Filter /FlateDecode[^>]*>>\nstream\n"))
        {
            var length = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            using var zlib = new ZLibStream(new MemoryStream(pdf, m.Index + m.Length, length), CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.Latin1);
            yield return reader.ReadToEnd();
        }
    }
}
