using System.Globalization;

namespace Universum.Core.Reports;

/// <summary>One thing on a day of a printed calendar.</summary>
public sealed record ReportEntry(string Time, string Title, string Detail, Rgb Color, bool Done = false);

/// <summary>A day of a printed calendar: its label, names, holiday, shading and what is on it.</summary>
public sealed record ReportDay(DateOnly Date, string Label, IReadOnlyList<string> Names, string Holiday, bool DayOff, bool SchoolHoliday, IReadOnlyList<ReportEntry> Entries)
{
    public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}

/// <summary>A task of a printed list.</summary>
public sealed record ReportTask(string Title, string Due, string Detail, bool Done, bool Overdue, int Priority, string Tags, IReadOnlyList<(string Text, bool Done)> Checklist);

/// <summary>A note of a printed list.</summary>
public sealed record ReportNote(string Title, string Text, string Where, string Tags, Rgb Color, bool Pinned);

/// <summary>
/// The printed forms of POZNÁMKY (owner, 31. 10. 2026; written anew for UNIVERSUM): the month as a grid, the days as a
/// list (a week, a month, the agenda, one day), the year in rows (days 1 – 31 by months) and as twelve small months, the
/// tasks with boxes to tick on paper, the notes. The colours are the calendar's, lightened so black-and-white print reads well.
/// </summary>
public static class AgendaReports
{
    private static readonly Rgb Holiday = new(0xC6, 0x3B, 0x3B);
    private static readonly Rgb DayOffFill = new(0xFB, 0xE4, 0xE4);
    private static readonly Rgb WeekendFill = new(0xF3, 0xF3, 0xF3);
    private static readonly Rgb SchoolFill = new(0xFC, 0xF3, 0xD7);
    private static readonly Rgb Accent = new(0xD4, 0x6A, 0x8C);

    /// <summary>A month as a grid of weeks (landscape).</summary>
    public static void MonthGrid(ReportLayout page, string title, int year, int month, IReadOnlyList<string> weekdays, Func<DateOnly, ReportDay> day)
    {
        page.Heading(title, 18);
        var first = new DateOnly(year, month, 1);
        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        var weeks = (int)Math.Ceiling((((int)first.DayOfWeek + 6) % 7 + DateTime.DaysInMonth(year, month)) / 7.0);
        var cellWidth = page.Width / 7;
        for (var d = 0; d < 7; d++)
        {
            page.Text(page.Left + (d * cellWidth) + 4, page.Y + 9, weekdays[d], 9, true, Rgb.Gray);
        }

        page.Y += 14;
        var cellHeight = (page.Room - 2) / weeks;
        for (var w = 0; w < weeks; w++)
        {
            for (var d = 0; d < 7; d++)
            {
                var date = start.AddDays((w * 7) + d);
                var info = day(date);
                var x = page.Left + (d * cellWidth);
                var y = page.Y + (w * cellHeight);
                var outside = date.Month != month;
                page.Box(x, y, cellWidth, cellHeight, Shade(info), Rgb.Line);
                var ink = outside ? Rgb.Gray : Rgb.Black;
                page.Text(x + 4, y + 13, date.Day.ToString(CultureInfo.InvariantCulture), 12, true, info.DayOff ? Holiday : ink);
                if (info.Names.Count > 0)
                {
                    var names = page.Fit(info.Names[0], cellWidth - 30, 7, false);
                    page.Text(x + cellWidth - 4 - page.Fonts.Width(names, 7), y + 10, names, 7, false, Rgb.Gray);
                }

                var line = y + 24;
                if (info.Holiday.Length > 0)
                {
                    page.Text(x + 4, line, page.Fit(info.Holiday, cellWidth - 8, 7.5, true), 7.5, true, Holiday);
                    line += 10;
                }

                var shown = 0;
                foreach (var entry in info.Entries)
                {
                    if (line + 9 > y + cellHeight - 3)
                    {
                        page.Text(x + 4, y + cellHeight - 4, "+" + (info.Entries.Count - shown).ToString(CultureInfo.InvariantCulture), 7, false, Rgb.Gray);
                        break;
                    }

                    page.Box(x + 4, line - 6, 2.5, 7, entry.Color);
                    var text = (entry.Time.Length > 0 ? entry.Time + " " : "") + entry.Title;
                    page.Text(x + 9, line, page.Fit(text, cellWidth - 13, 7.5, false), 7.5, false, entry.Done ? Rgb.Gray : ink);
                    line += 9.5;
                    shown++;
                }
            }
        }

        page.Y += weeks * cellHeight;
    }

    /// <summary>Days under each other: each with its names, holiday and everything on it (portrait).</summary>
    public static void DayList(ReportLayout page, string title, IEnumerable<ReportDay> days)
    {
        page.Heading(title, 16);
        foreach (var day in days)
        {
            var names = string.Join("  ·  ", day.Names);
            page.Ensure(30 + (Math.Min(3, day.Entries.Count) * 12));
            var top = page.Y;
            page.Box(page.Left, top, page.Width, 16, Shade(day) ?? new Rgb(0xF7, 0xF7, 0xF7), radius: 3);
            page.Text(page.Left + 6, top + 11.5, day.Label, 10.5, true, day.DayOff ? Holiday : Rgb.Black);
            var labelWidth = page.Fonts.Width(day.Label, 10.5, true);
            if (names.Length > 0)
            {
                page.Text(page.Left + 16 + labelWidth, top + 11.5, page.Fit(names, page.Width - labelWidth - 30, 8.5, false), 8.5, false, Rgb.Gray);
            }

            page.Y += 19;
            if (day.Holiday.Length > 0)
            {
                page.Paragraph(day.Holiday, 9, Holiday, 6, bold: true);
            }

            foreach (var entry in day.Entries)
            {
                page.Ensure(12);
                var lineTop = page.Y;
                page.Box(page.Left + 6, lineTop + 3, 6, 6, entry.Color, radius: 3);
                page.Text(page.Left + 18, lineTop + 9, entry.Time, 9, true, Rgb.Black);
                var lines = page.Wrap(entry.Title + (entry.Detail.Length > 0 ? " – " + entry.Detail : ""), page.Width - 62, 9, false);
                foreach (var text in lines)
                {
                    page.Ensure(11.5);
                    page.Text(page.Left + 56, page.Y + 9, text, 9, false, entry.Done ? Rgb.Gray : Rgb.Black);
                    page.Y += 11.5;
                }
            }

            page.Space(6);
        }
    }

    /// <summary>The year on one page: rows = days 1 – 31, columns = months (landscape).</summary>
    public static void YearRows(ReportLayout page, string title, int year, IReadOnlyList<string> months, Func<DateOnly, ReportDay> day)
    {
        page.Heading(title, 16);
        const double numberWidth = 18;
        var columnWidth = (page.Width - numberWidth) / 12;
        for (var m = 0; m < 12; m++)
        {
            page.Text(page.Left + numberWidth + (m * columnWidth) + 3, page.Y + 8, months[m], 8, true, Rgb.Gray);
        }

        page.Y += 11;
        var rowHeight = Math.Min(16, (page.Room - 2) / 31);
        for (var d = 1; d <= 31; d++)
        {
            var y = page.Y + ((d - 1) * rowHeight);
            page.Text(page.Left + 2, y + rowHeight - 3.5, d.ToString(CultureInfo.InvariantCulture), 7, false, Rgb.Gray);
            for (var m = 1; m <= 12; m++)
            {
                if (d > DateTime.DaysInMonth(year, m))
                {
                    continue;
                }

                var info = day(new DateOnly(year, m, d));
                var x = page.Left + numberWidth + ((m - 1) * columnWidth);
                page.Box(x, y, columnWidth, rowHeight, Shade(info), Rgb.Line, 0.3);
                var letter = info.Label.Length > 0 ? info.Label[..Math.Min(2, info.Label.Length)] : "";
                page.Text(x + 2, y + rowHeight - 3.5, letter, 5.5, false, Rgb.Gray);
                var text = info.Holiday.Length > 0 ? info.Holiday : info.Entries.FirstOrDefault()?.Title ?? "";
                if (text.Length > 0)
                {
                    page.Text(x + 11, y + rowHeight - 3.5, page.Fit(text, columnWidth - 13, 5.5, false), 5.5, false, info.Holiday.Length > 0 ? Holiday : Rgb.Black);
                }
            }
        }

        page.Y += 31 * rowHeight;
    }

    /// <summary>The year as twelve small months, four in a row (landscape).</summary>
    public static void YearMonths(ReportLayout page, string title, int year, IReadOnlyList<string> months, IReadOnlyList<string> weekdays, Func<DateOnly, ReportDay> day)
    {
        page.Heading(title, 16);
        const int across = 4;
        var monthWidth = page.Width / across;
        var monthHeight = (page.Room - 4) / 3;
        var cell = Math.Min((monthWidth - 12) / 7, (monthHeight - 30) / 7);
        for (var m = 0; m < 12; m++)
        {
            var x0 = page.Left + (m % across * monthWidth) + 4;
            var y0 = page.Y + (m / across * monthHeight);
            page.Text(x0, y0 + 11, months[m], 10, true, Accent);
            for (var d = 0; d < 7; d++)
            {
                page.Text(x0 + (d * cell) + (cell / 2) - (page.Fonts.Width(weekdays[d], 6.5) / 2), y0 + 22, weekdays[d], 6.5, false, Rgb.Gray);
            }

            var first = new DateOnly(year, m + 1, 1);
            var lead = ((int)first.DayOfWeek + 6) % 7;
            for (var i = 0; i < DateTime.DaysInMonth(year, m + 1); i++)
            {
                var date = first.AddDays(i);
                var info = day(date);
                var column = (lead + i) % 7;
                var row = (lead + i) / 7;
                var x = x0 + (column * cell);
                var y = y0 + 26 + (row * cell);
                if (Shade(info) is { } fill)
                {
                    page.Box(x + 0.5, y + 0.5, cell - 1, cell - 1, fill, radius: 2);
                }

                var number = date.Day.ToString(CultureInfo.InvariantCulture);
                page.Text(x + (cell / 2) - (page.Fonts.Width(number, 7.5) / 2), y + (cell / 2) + 2.5, number, 7.5, false, info.DayOff ? Holiday : Rgb.Black);
                if (info.Entries.Count > 0)
                {
                    page.Box(x + cell - 4, y + cell - 4, 2.5, 2.5, Accent, radius: 1.25);
                }
            }
        }

        page.Y += 3 * monthHeight;
    }

    /// <summary>Tasks by groups, each with a box to tick on paper and its checklist.</summary>
    public static void Tasks(ReportLayout page, string title, IEnumerable<(string Group, Rgb Color, IReadOnlyList<ReportTask> Tasks)> groups)
    {
        page.Heading(title, 16);
        foreach (var (group, color, tasks) in groups)
        {
            page.Ensure(34);
            page.Text(page.Left, page.Y + 11, group + "  (" + tasks.Count.ToString(CultureInfo.InvariantCulture) + ")", 11.5, true, color);
            page.Y += 16;
            foreach (var task in tasks)
            {
                page.Ensure(16);
                var top = page.Y;
                page.Box(page.Left + 2, top + 2, 8.5, 8.5, null, Rgb.Black, 0.7);
                if (task.Done)
                {
                    page.Line(page.Left + 3.5, top + 6.5, page.Left + 6, top + 9, Rgb.Black, 1);
                    page.Line(page.Left + 6, top + 9, page.Left + 10, top + 3, Rgb.Black, 1);
                }

                var mark = task.Priority == 2 ? "! " : "";
                var head = mark + task.Title;
                var lines = page.Wrap(head, page.Width - 130, 10, true);
                page.Text(page.Right - page.Fonts.Width(task.Due, 9), top + 9.5, task.Due, 9, task.Overdue, task.Overdue ? Holiday : Rgb.Gray);
                foreach (var line in lines)
                {
                    page.Ensure(12.5);
                    page.Text(page.Left + 16, page.Y + 9.5, line, 10, true, task.Done ? Rgb.Gray : Rgb.Black);
                    page.Y += 12.5;
                }

                var extra = string.Join("  ·  ", new[] { task.Tags, task.Detail }.Where(t => t.Length > 0));
                if (extra.Length > 0)
                {
                    page.Paragraph(extra, 8.5, Rgb.Gray, 16);
                }

                foreach (var (text, done) in task.Checklist)
                {
                    page.Ensure(11);
                    page.Box(page.Left + 18, page.Y + 2, 6.5, 6.5, null, Rgb.Gray, 0.5);
                    if (done)
                    {
                        page.Line(page.Left + 19, page.Y + 5.5, page.Left + 21, page.Y + 7.5, Rgb.Gray, 0.8);
                        page.Line(page.Left + 21, page.Y + 7.5, page.Left + 24, page.Y + 3, Rgb.Gray, 0.8);
                    }

                    page.Text(page.Left + 29, page.Y + 8.5, page.Fit(text, page.Width - 40, 8.5, false), 8.5, false, done ? Rgb.Gray : Rgb.Black);
                    page.Y += 11;
                }

                page.Space(4);
            }

            page.Space(6);
        }
    }

    /// <summary>Notes, each with its colour on the left, its title, where it belongs and its text.</summary>
    public static void Notes(ReportLayout page, string title, IEnumerable<ReportNote> notes)
    {
        page.Heading(title, 16);
        foreach (var note in notes)
        {
            page.Ensure(40);
            var top = page.Y;
            page.Text(page.Left + 10, page.Y + 11, page.Fit(note.Title, page.Width - 14, 11, true), 11, true, Rgb.Black);
            page.Y += 15;
            var meta = string.Join("  ·  ", new[] { note.Where, note.Tags }.Where(t => t.Length > 0));
            if (meta.Length > 0)
            {
                page.Paragraph(meta, 8.5, Rgb.Gray, 10);
            }

            if (note.Text.Length > 0)
            {
                page.Space(2);
                page.Paragraph(note.Text, 9.5, Rgb.Black, 10);
            }

            // the colour of the note along its left edge (on the page where it began)
            page.Box(page.Left, top, 4, Math.Max(12, (page.Y > top ? page.Y : page.Bottom) - top), note.Color, radius: 2);
            page.Space(10);
        }
    }

    private static Rgb? Shade(ReportDay day) => day.DayOff ? DayOffFill : day.SchoolHoliday ? SchoolFill : day.IsWeekend ? WeekendFill : null;
}
