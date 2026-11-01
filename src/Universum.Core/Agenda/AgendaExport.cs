using System.Globalization;
using System.Text;
using Universum.Core.Calendar;

namespace Universum.Core.Agenda;

/// <summary>
/// The agenda saved for other programs (owner, 31. 10. 2026 – POZNÁMKY, written anew): iCalendar (.ics, RFC 5545) for
/// Outlook and Google – events with their repeat, cancelled days and reminder, tasks with due day, state, priority and
/// checklist, and the holidays when asked – CSV for Excel (UTF-8, ";"), and Markdown for notes and tasks.
/// </summary>
public static class AgendaExport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The records (events and tasks; notes with a day as events of the day) and the holidays of the regions in the years asked.</summary>
    public static string Ics(IEnumerable<AgendaItem> items, IEnumerable<Holiday>? holidays = null, string language = "sk")
    {
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//UNIVERSUM//POZNAMKY//" + language.ToUpperInvariant(), "CALSCALE:GREGORIAN" };
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", Invariant);
        foreach (var item in items)
        {
            if (item.Time is { } time)
            {
                lines.Add("BEGIN:VEVENT");
                lines.Add($"UID:{item.Id:N}@universum");
                lines.Add("DTSTAMP:" + stamp);
                if (time.StartTime is { } start)
                {
                    lines.Add("DTSTART:" + Local(time.Start, start));
                    var end = time.EndTime ?? start.AddHours(1);
                    lines.Add("DTEND:" + Local(time.End ?? (end < start ? time.Start.AddDays(1) : time.Start), end));
                }
                else
                {
                    lines.Add("DTSTART;VALUE=DATE:" + Day(time.Start));
                    lines.Add("DTEND;VALUE=DATE:" + Day((time.End ?? time.Start).AddDays(1)));
                }

                if (time.Repeat is { } repeat)
                {
                    lines.Add("RRULE:" + Rule(repeat, time.StartTime));
                }

                foreach (var skipped in time.Skipped)
                {
                    lines.Add(time.StartTime is { } at ? "EXDATE:" + Local(skipped, at) : "EXDATE;VALUE=DATE:" + Day(skipped));
                }

                Common(lines, item);
                if (time.ReminderMinutes is { } minutes)
                {
                    lines.AddRange(["BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:" + Text(item.Title), $"TRIGGER:-PT{minutes}M", "END:VALARM"]);
                }

                lines.Add("END:VEVENT");
            }

            if (item.Task is { } task)
            {
                lines.Add("BEGIN:VTODO");
                lines.Add($"UID:{item.Id:N}-uloha@universum");
                lines.Add("DTSTAMP:" + stamp);
                if (task.Due is { } due)
                {
                    lines.Add(task.DueTime is { } at ? "DUE:" + Local(due, at) : "DUE;VALUE=DATE:" + Day(due));
                }

                lines.Add("STATUS:" + task.State switch { TaskState.Done => "COMPLETED", TaskState.Cancelled => "CANCELLED", TaskState.InProgress => "IN-PROCESS", _ => "NEEDS-ACTION" });
                if (task.Finished is { } finished && task.State == TaskState.Done)
                {
                    lines.Add("COMPLETED:" + finished.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", Invariant));
                }

                lines.Add("PRIORITY:" + task.Priority switch { 2 => "1", 0 => "9", _ => "5" });
                Common(lines, item, task.Checklist.Count > 0 ? string.Join("\n", task.Checklist.Select(c => (c.Done ? "[x] " : "[ ] ") + c.Text)) : null);
                lines.Add("END:VTODO");
            }

            if (item is { Time: null, Task: null, Note: not null } && item.Links.FirstOrDefault(l => l.Kind == LinkKind.Day) is { } day &&
                DateOnly.TryParseExact(day.Target, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date))
            {
                lines.AddRange(["BEGIN:VEVENT", $"UID:{item.Id:N}-poznamka@universum", "DTSTAMP:" + stamp, "DTSTART;VALUE=DATE:" + Day(date), "DTEND;VALUE=DATE:" + Day(date.AddDays(1)), "TRANSP:TRANSPARENT"]);
                Common(lines, item);
                lines.Add("END:VEVENT");
            }
        }

        foreach (var holiday in holidays ?? [])
        {
            lines.AddRange(["BEGIN:VEVENT", $"UID:{holiday.Region}-{holiday.Date:yyyyMMdd}-{(int)holiday.Kind}-{(holiday.Substitute ? "n" : "s")}@universum", "DTSTAMP:" + stamp,
                "DTSTART;VALUE=DATE:" + Day(holiday.Date), "DTEND;VALUE=DATE:" + Day(holiday.Date.AddDays(1)), "SUMMARY:" + Text(holiday.Name(language)),
                "TRANSP:TRANSPARENT", "CATEGORIES:" + Text(HolidayRegions.Name(holiday.Region, language)), "END:VEVENT"]);
        }

        lines.Add("END:VCALENDAR");
        var ics = new StringBuilder();
        foreach (var line in lines)
        {
            Fold(ics, line);
        }

        return ics.ToString();
    }

    /// <summary>All records as a table for Excel (UTF-8 with its mark, columns separated by ";").</summary>
    public static string Csv(IEnumerable<AgendaItem> items, string language = "sk")
    {
        string[] header = language switch
        {
            "cs" => ["Druh", "Název", "Od", "Do", "Čas od", "Čas do", "Opakování", "Termín", "Stav", "Priorita", "Štítky", "Text"],
            "en" => ["Kind", "Title", "From", "To", "Time from", "Time to", "Repeat", "Due", "State", "Priority", "Tags", "Text"],
            _ => ["Druh", "Názov", "Od", "Do", "Čas od", "Čas do", "Opakovanie", "Termín", "Stav", "Priorita", "Štítky", "Text"],
        };
        var csv = new StringBuilder("﻿").Append(string.Join(';', header)).Append("\r\n");
        foreach (var item in items)
        {
            var kind = item.Time is not null ? "udalosť" : item.Task is not null ? "úloha" : "poznámka";
            string[] row =
            [
                kind, item.Title,
                item.Time?.Start.ToString("yyyy-MM-dd", Invariant) ?? "", item.Time?.End?.ToString("yyyy-MM-dd", Invariant) ?? "",
                item.Time?.StartTime?.ToString("HH:mm", Invariant) ?? "", item.Time?.EndTime?.ToString("HH:mm", Invariant) ?? "",
                item.Time?.Repeat is { } r ? Rule(r, item.Time.StartTime) : "",
                (item.Task?.Due?.ToString("yyyy-MM-dd", Invariant) ?? "") + (item.Task?.DueTime is { } due ? " " + due.ToString("HH:mm", Invariant) : ""),
                item.Task?.State.ToString() ?? "", item.Task is { } t ? t.Priority.ToString(Invariant) : "",
                string.Join(' ', item.Tags.Select(tag => "#" + tag)), item.Text,
            ];
            csv.Append(string.Join(';', row.Select(Cell))).Append("\r\n");
        }

        return csv.ToString();
    }

    /// <summary>Notes and tasks in Markdown: a heading per note, a checkbox per task (and its checklist).</summary>
    public static string Markdown(string title, IEnumerable<AgendaItem> items)
    {
        var md = new StringBuilder("# ").Append(title).Append("\n\n");
        foreach (var item in items)
        {
            var tags = item.Tags.Count > 0 ? " " + string.Join(' ', item.Tags.Select(t => "#" + t)) : "";
            if (item.Task is { } task)
            {
                var due = task.Due is { } d ? $" (do {d:d. M. yyyy}{(task.DueTime is { } at ? " " + at.ToString("HH:mm", Invariant) : "")})" : "";
                md.Append(task.State == TaskState.Done ? "- [x] " : "- [ ] ").Append(item.Title).Append(due).Append(tags).Append('\n');
                foreach (var line in task.Checklist)
                {
                    md.Append(line.Done ? "  - [x] " : "  - [ ] ").Append(line.Text).Append('\n');
                }

                if (item.Text.Length > 0)
                {
                    md.Append("  ").Append(item.Text.Replace("\n", "\n  ", StringComparison.Ordinal)).Append('\n');
                }

                continue;
            }

            md.Append("## ").Append(item.Title.Length > 0 ? item.Title : "–").Append(tags).Append("\n\n");
            if (item.Text.Length > 0)
            {
                md.Append(item.Text).Append("\n\n");
            }
        }

        return md.ToString();
    }

    // FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,FR;UNTIL=…;COUNT=…
    private static string Rule(Recurrence repeat, TimeOnly? time)
    {
        var rule = new StringBuilder("FREQ=").Append(repeat.Every switch { RepeatEvery.Day => "DAILY", RepeatEvery.Week => "WEEKLY", RepeatEvery.Month => "MONTHLY", _ => "YEARLY" });
        if (repeat.Interval > 1)
        {
            rule.Append(";INTERVAL=").Append(repeat.Interval.ToString(Invariant));
        }

        if (repeat.Every == RepeatEvery.Week && repeat.Days.Count > 0)
        {
            rule.Append(";BYDAY=").Append(string.Join(',', repeat.Days.Select(d => d switch
            {
                DayOfWeek.Monday => "MO",
                DayOfWeek.Tuesday => "TU",
                DayOfWeek.Wednesday => "WE",
                DayOfWeek.Thursday => "TH",
                DayOfWeek.Friday => "FR",
                DayOfWeek.Saturday => "SA",
                _ => "SU",
            })));
        }

        if (repeat.Until is { } until)
        {
            rule.Append(";UNTIL=").Append(time is not null ? Local(until, new TimeOnly(23, 59, 59)) : Day(until));
        }

        if (repeat.Count is { } count)
        {
            rule.Append(";COUNT=").Append(count.ToString(Invariant));
        }

        return rule.ToString();
    }

    private static void Common(List<string> lines, AgendaItem item, string? extra = null)
    {
        lines.Add("SUMMARY:" + Text(item.Title.Length > 0 ? item.Title : item.Text.Split('\n')[0]));
        var description = string.Join("\n\n", new[] { item.Text, extra }.Where(t => !string.IsNullOrEmpty(t)));
        if (description.Length > 0)
        {
            lines.Add("DESCRIPTION:" + Text(description));
        }

        if (item.Tags.Count > 0)
        {
            lines.Add("CATEGORIES:" + string.Join(',', item.Tags.Select(Text)));
        }
    }

    private static string Day(DateOnly date) => date.ToString("yyyyMMdd", Invariant);

    private static string Local(DateOnly date, TimeOnly time) => date.ToString("yyyyMMdd", Invariant) + "T" + time.ToString("HHmmss", Invariant);

    // the text of a property: \ ; , and line ends escaped
    private static string Text(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    // lines longer than 75 bytes (UTF-8) folded: CRLF and a space, never inside a letter
    private static void Fold(StringBuilder output, string line)
    {
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > 75)
            {
                output.Append("\r\n ");
                bytes = 1;
            }

            output.Append(rune.ToString());
            bytes += size;
        }

        output.Append("\r\n");
    }

    private static string Cell(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}
