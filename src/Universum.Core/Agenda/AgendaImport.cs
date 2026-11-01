using System.Globalization;
using System.Text;

namespace Universum.Core.Agenda;

/// <summary>What an iCalendar file gave: the records, and how many of its parts were left out or simplified.</summary>
/// <param name="Skipped">Parts without a usable day, and the holidays UNIVERSUM exported (it knows them itself).</param>
/// <param name="Simplified">Repeats UNIVERSUM keeps only in part (by the hour, "the first Monday", several rules).</param>
public sealed record IcsImport(IReadOnlyList<AgendaItem> Items, int Skipped, int Simplified);

/// <summary>
/// Reading an iCalendar file (.ics, RFC 5545) from Outlook, Google or UNIVERSUM itself (owner, 31. 10. 2026 – POZNÁMKY,
/// stage P4, written anew): events with their days, hours (in UTC, a named time zone or the local time), repeat, cancelled
/// days, reminder and categories; tasks with their due day, state, priority and checklist. A record exported by
/// UNIVERSUM keeps its identity, so its event and task become one record again and nothing comes twice.
/// </summary>
public static class AgendaImport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IcsImport Ics(string text)
    {
        var items = new List<AgendaItem>();
        var own = new Dictionary<Guid, AgendaItem>();
        var skipped = 0;
        var simplified = 0;
        var stack = new Stack<Component>();
        foreach (var line in Unfolded(text))
        {
            if (Parse(line) is not { } property)
            {
                continue;
            }

            if (property.Name == "BEGIN")
            {
                stack.Push(new Component(property.Value.ToUpperInvariant()));
                continue;
            }

            if (property.Name == "END")
            {
                if (stack.Count == 0)
                {
                    continue;
                }

                var done = stack.Pop();
                if (done.Name == "VALARM" && stack.Count > 0)
                {
                    stack.Peek().Alarms.Add(done);
                }
                else if (done.Name is "VEVENT" or "VTODO" && !stack.Any(c => c.Name == "VTIMEZONE"))
                {
                    var (item, partial) = done.Name == "VEVENT" ? Event(done) : Todo(done);
                    if (item is null)
                    {
                        skipped++;
                        continue;
                    }

                    simplified += partial ? 1 : 0;
                    if (OwnId(item.Uid) is { } id)
                    {
                        // UNIVERSUM's own record: its event and its task are one record
                        if (own.TryGetValue(id, out var first))
                        {
                            first.Time ??= item.Time;
                            first.Task ??= item.Task;
                            first.Note ??= item.Note;
                            continue;
                        }

                        item = Identified(item, id);
                        own[id] = item;
                    }

                    items.Add(item);
                }

                continue;
            }

            if (stack.Count > 0)
            {
                stack.Peek().Properties.Add(property);
            }
        }

        return new IcsImport(items, skipped, simplified);
    }

    // ---------------------------------------------------------------- components

    private static (AgendaItem? Item, bool Simplified) Event(Component component)
    {
        if (component.First("UID") is { } holidayUid && IsHoliday(holidayUid.Value))
        {
            return (null, false);
        }

        if (component.First("DTSTART") is not { } startProperty || Moment(startProperty) is not { } start)
        {
            return (null, false);
        }

        var time = new AgendaTime { Start = start.Day, StartTime = start.Time };
        if (component.First("DTEND") is { } endProperty && Moment(endProperty) is { } end)
        {
            SetEnd(time, end);
        }
        else if (component.First("DURATION") is { } duration && Duration(duration.Value) is { } length)
        {
            var finish = start.Day.ToDateTime(start.Time ?? TimeOnly.MinValue) + length;
            SetEnd(time, new Point(DateOnly.FromDateTime(finish), start.Time is null ? null : TimeOnly.FromDateTime(finish)));
        }

        var partial = false;
        var rules = component.All("RRULE").ToList();
        if (rules.Count > 0)
        {
            (time.Repeat, partial) = Rule(rules[0].Value, start.Day);
            partial |= rules.Count > 1 || component.First("RDATE") is not null;
        }

        foreach (var exdate in component.All("EXDATE"))
        {
            foreach (var value in exdate.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Moment(exdate with { Value = value }) is { } cancelled)
                {
                    time.Skipped.Add(cancelled.Day);
                }
            }
        }

        foreach (var alarm in component.Alarms)
        {
            if (alarm.First("TRIGGER") is { } trigger && !trigger.Has("VALUE", "DATE-TIME") && !trigger.Has("RELATED", "END") &&
                Duration(trigger.Value) is { } before && before <= TimeSpan.Zero)
            {
                time.ReminderMinutes = (int)Math.Round(-before.TotalMinutes);
                break;
            }
        }

        var item = Common(component);
        item.Time = time;
        // a note of a day exported by UNIVERSUM comes back as a note of that day
        if (component.First("UID")?.Value.EndsWith("-poznamka@universum", StringComparison.OrdinalIgnoreCase) == true && time.StartTime is null && time.Repeat is null)
        {
            item.Time = null;
            item.Note = new AgendaNote();
            item.Links.Add(new AgendaLink(LinkKind.Day, start.Day.ToString("yyyy-MM-dd", Invariant), start.Day.ToString("d. M. yyyy", Invariant)));
        }

        return (item, partial);
    }

    private static (AgendaItem? Item, bool Simplified) Todo(Component component)
    {
        var item = Common(component);
        var task = new AgendaTask();
        if (component.First("DUE") is { } dueProperty && Moment(dueProperty) is { } due)
        {
            task.Due = due.Day;
            task.DueTime = due.Time;
        }
        else if (component.First("DTSTART") is { } startProperty && Moment(startProperty) is { } start)
        {
            task.Due = start.Day;
            task.DueTime = start.Time;
        }

        task.State = component.First("STATUS")?.Value.ToUpperInvariant() switch
        {
            "COMPLETED" => TaskState.Done,
            "CANCELLED" => TaskState.Cancelled,
            "IN-PROCESS" => TaskState.InProgress,
            _ => TaskState.Open,
        };
        if (task.State == TaskState.Done)
        {
            task.Finished = component.First("COMPLETED") is { } completed && Moment(completed) is { } at
                ? new DateTimeOffset(at.Day.ToDateTime(at.Time ?? TimeOnly.MinValue))
                : DateTimeOffset.Now;
        }

        // RFC 5545: 1–4 high, 5 normal, 6–9 low, 0 not given
        task.Priority = int.TryParse(component.First("PRIORITY")?.Value, NumberStyles.None, Invariant, out var priority) ? priority switch
        {
            >= 1 and <= 4 => 2,
            >= 6 and <= 9 => 0,
            _ => 1,
        } : 1;

        // "[x] step" lines of the description are the checklist (UNIVERSUM writes it so)
        var text = new List<string>();
        foreach (var line in item.Text.Split('\n'))
        {
            if (line.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("[ ] ", StringComparison.Ordinal))
            {
                task.Checklist.Add(new CheckItem { Text = line[4..].Trim(), Done = line[1] is 'x' or 'X' });
            }
            else
            {
                text.Add(line);
            }
        }

        item.Text = string.Join("\n", text).Trim();
        item.Task = task;
        return (item, false);
    }

    // title, text, place, categories, UID
    private static AgendaItem Common(Component component)
    {
        var text = Unescape(component.First("DESCRIPTION")?.Value ?? "");
        if (component.First("LOCATION") is { Value.Length: > 0 } location)
        {
            text = (text.Length > 0 ? text + "\n" : "") + Unescape(location.Value);
        }

        var tags = component.All("CATEGORIES")
            .SelectMany(c => SplitList(c.Value))
            .Select(t => Unescape(t).Trim().TrimStart('#'))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase);
        return new AgendaItem
        {
            Title = Unescape(component.First("SUMMARY")?.Value ?? "").Trim(),
            Text = text.Trim(),
            Tags = [.. tags],
            Uid = component.First("UID")?.Value.Trim() is { Length: > 0 } uid ? uid : null,
        };
    }

    private static void SetEnd(AgendaTime time, Point end)
    {
        if (time.StartTime is null)
        {
            // the end of a whole day is the next day (not included)
            var last = end.Day.AddDays(-1);
            time.End = last > time.Start ? last : null;
            return;
        }

        time.EndTime = end.Time;
        time.End = end.Day > time.Start ? end.Day : null;
    }

    // ---------------------------------------------------------------- repeat

    private static (Recurrence? Repeat, bool Simplified) Rule(string value, DateOnly start)
    {
        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First()[1].Trim());
        RepeatEvery? every = parts.GetValueOrDefault("FREQ")?.ToUpperInvariant() switch
        {
            "DAILY" => RepeatEvery.Day,
            "WEEKLY" => RepeatEvery.Week,
            "MONTHLY" => RepeatEvery.Month,
            "YEARLY" => RepeatEvery.Year,
            _ => null,
        };
        if (every is null)
        {
            // by the hour or minute: kept as one occurrence
            return (null, true);
        }

        var simplified = parts.Keys.Any(k => k is "BYSETPOS" or "BYMONTHDAY" or "BYYEARDAY" or "BYWEEKNO" or "BYMONTH" or "BYHOUR" or "BYMINUTE");
        var days = new List<DayOfWeek>();
        if (parts.GetValueOrDefault("BYDAY") is { } byDay)
        {
            foreach (var code in byDay.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = code.Trim().ToUpperInvariant();
                // "1MO", "-1FR": a weekday of the month – kept as the same day of the month
                if (name.Length > 2)
                {
                    simplified = true;
                    continue;
                }

                if (Weekday(name) is { } day)
                {
                    days.Add(day);
                }
            }

            if (every != RepeatEvery.Week && days.Count > 0)
            {
                // every day on workdays and the like: kept as weekly on those days
                if (every == RepeatEvery.Day)
                {
                    every = RepeatEvery.Week;
                }
                else
                {
                    simplified = true;
                    days.Clear();
                }
            }
        }

        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), NumberStyles.None, Invariant, out var n) && n is >= 1 and <= 999 ? n : 1;
        int? count = int.TryParse(parts.GetValueOrDefault("COUNT"), NumberStyles.None, Invariant, out var c) && c > 0 ? c : null;
        DateOnly? until = parts.GetValueOrDefault("UNTIL") is { Length: >= 8 } u && DateOnly.TryParseExact(u[..8], "yyyyMMdd", Invariant, DateTimeStyles.None, out var d) ? d : null;
        return (new Recurrence { Every = every.Value, Interval = interval, Days = days.Count > 0 && !(days.Count == 1 && days[0] == start.DayOfWeek) ? days : [], Count = count, Until = until }, simplified);
    }

    private static DayOfWeek? Weekday(string code) => code switch
    {
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        "SU" => DayOfWeek.Sunday,
        _ => null,
    };

    // ---------------------------------------------------------------- values

    /// <summary>A day with or without an hour (local time).</summary>
    private sealed record Point(DateOnly Day, TimeOnly? Time);

    // "20261101" (a day), "20261101T200000Z" (UTC), "20261101T200000" with TZID or floating (local)
    private static Point? Moment(Property property)
    {
        var value = property.Value.Trim();
        if (value.Length < 8 || !DateOnly.TryParseExact(value[..8], "yyyyMMdd", Invariant, DateTimeStyles.None, out var day))
        {
            return null;
        }

        if (property.Has("VALUE", "DATE") || value.Length == 8)
        {
            return new Point(day, null);
        }

        if (value.Length < 15 || value[8] != 'T' || !TimeOnly.TryParseExact(value[9..15], "HHmmss", Invariant, DateTimeStyles.None, out var hour))
        {
            return new Point(day, null);
        }

        var moment = day.ToDateTime(hour);
        if (value.EndsWith('Z'))
        {
            moment = DateTime.SpecifyKind(moment, DateTimeKind.Utc).ToLocalTime();
        }
        else if (property.Parameter("TZID") is { } zone && Zone(zone) is { } info)
        {
            try
            {
                moment = TimeZoneInfo.ConvertTime(moment, info, TimeZoneInfo.Local);
            }
            catch (ArgumentException)
            {
                // a moment that does not exist in that zone (the hour skipped in spring) stays as written
            }
        }

        return new Point(DateOnly.FromDateTime(moment), TimeOnly.FromDateTime(moment));
    }

    private static TimeZoneInfo? Zone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim('"', ' ', '/'));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            // an unknown zone: the hour as written (the local time)
            return null;
        }
    }

    // "-PT15M", "PT1H30M", "-P1D", "P1W", "-P1DT2H"
    internal static TimeSpan? Duration(string text)
    {
        var value = text.Trim().ToUpperInvariant();
        var sign = 1;
        if (value.StartsWith('-') || value.StartsWith('+'))
        {
            sign = value[0] == '-' ? -1 : 1;
            value = value[1..];
        }

        if (!value.StartsWith('P'))
        {
            return null;
        }

        var total = TimeSpan.Zero;
        var number = 0;
        var digits = false;
        var inTime = false;
        var parts = 0;
        foreach (var ch in value[1..])
        {
            if (char.IsAsciiDigit(ch))
            {
                number = (number * 10) + (ch - '0');
                digits = true;
                if (number > 100000)
                {
                    return null;
                }

                continue;
            }

            if (ch == 'T')
            {
                inTime = true;
                continue;
            }

            if (!digits)
            {
                return null;
            }

            TimeSpan? part = (ch, inTime) switch
            {
                ('W', false) => TimeSpan.FromDays(7 * number),
                ('D', false) => TimeSpan.FromDays(number),
                ('H', true) => TimeSpan.FromHours(number),
                ('M', true) => TimeSpan.FromMinutes(number),
                ('S', true) => TimeSpan.FromSeconds(number),
                _ => null,
            };
            if (part is not { } add)
            {
                return null;
            }

            total += add;
            parts++;

            number = 0;
            digits = false;
        }

        return digits || parts == 0 ? null : total * sign;
    }

    // \n \N \, \; \\ of a text value
    internal static string Unescape(string text)
    {
        var output = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                var next = text[++i];
                output.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else
            {
                output.Append(text[i]);
            }
        }

        return output.ToString().Replace("\r", "", StringComparison.Ordinal);
    }

    // a list separated by commas that are not escaped
    private static IEnumerable<string> SplitList(string value)
    {
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
            }
            else if (value[i] == ',')
            {
                yield return value[start..i];
                start = i + 1;
            }
        }

        yield return value[start..];
    }

    // "0f8f…@universum", "0f8f…-uloha@universum", "0f8f…-poznamka@universum": the ID of a record exported by UNIVERSUM
    private static Guid? OwnId(string? uid) =>
        uid is { Length: >= 42 } && uid.EndsWith("@universum", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(uid[..32], "N", out var id) && (uid.Length == 42 || uid[32] == '-') ? id : null;

    // "SK-20261224-0-s@universum": a holiday UNIVERSUM exported (it knows the holidays itself)
    private static bool IsHoliday(string uid) => uid.EndsWith("@universum", StringComparison.OrdinalIgnoreCase) && OwnId(uid) is null;

    private static AgendaItem Identified(AgendaItem item, Guid id) => new()
    {
        Id = id,
        Title = item.Title,
        Text = item.Text,
        Tags = item.Tags,
        Uid = item.Uid,
        Time = item.Time,
        Task = item.Task,
        Note = item.Note,
        Links = item.Links,
    };

    // ---------------------------------------------------------------- lines

    private sealed record Property(string Name, IReadOnlyList<(string Name, string Value)> Parameters, string Value)
    {
        public string? Parameter(string name) => Parameters.FirstOrDefault(p => p.Name == name).Value;

        public bool Has(string name, string value) => string.Equals(Parameter(name), value, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Component(string name)
    {
        public string Name { get; } = name;

        public List<Property> Properties { get; } = [];

        public List<Component> Alarms { get; } = [];

        public Property? First(string name) => Properties.FirstOrDefault(p => p.Name == name);

        public IEnumerable<Property> All(string name) => Properties.Where(p => p.Name == name);
    }

    // lines folded by a line end and a space or tab joined again
    private static IEnumerable<string> Unfolded(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }

            current.Clear().Append(raw);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    // NAME;PARAM=value;PARAM="a:b":value
    private static Property? Parse(string line)
    {
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }

        if (colon <= 0)
        {
            return null;
        }

        var head = line[..colon].Split(';');
        var parameters = new List<(string, string)>();
        foreach (var parameter in head.Skip(1))
        {
            var pair = parameter.Split('=', 2);
            if (pair.Length == 2)
            {
                parameters.Add((pair[0].Trim().ToUpperInvariant(), pair[1].Trim().Trim('"')));
            }
        }

        return new Property(head[0].Trim().ToUpperInvariant(), parameters, line[(colon + 1)..]);
    }
}
