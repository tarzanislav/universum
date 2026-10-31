using System.Text.Json;
using System.Text.Json.Serialization;

namespace Universum.Core.Agenda;

/// <summary>A record on a day of the calendar: an event (one of its repeats) or a task due that day.</summary>
/// <param name="First">The first day of this occurrence.</param>
/// <param name="Last">The last day (several days: later).</param>
public sealed record Occurrence(AgendaItem Item, DateOnly First, DateOnly Last)
{
    public bool Covers(DateOnly day) => First <= day && day <= Last;
}

/// <summary>The days a timed record happens on: its repeats in a span of days.</summary>
public static class Recurrences
{
    /// <summary>
    /// The first days of the occurrences of the time that touch the days <paramref name="from"/> – <paramref name="to"/>
    /// (an event of several days that began before is included); skipped days left out.
    /// </summary>
    public static IEnumerable<DateOnly> Starts(AgendaTime time, DateOnly from, DateOnly to)
    {
        var earliest = from.AddDays(1 - time.Length);
        var skipped = time.Skipped.ToHashSet();
        foreach (var start in All(time))
        {
            if (start > to)
            {
                yield break;
            }

            if (start >= earliest && !skipped.Contains(start))
            {
                yield return start;
            }
        }
    }

    // all first days in order, from the start; endless without Until and Count (the caller stops)
    private static IEnumerable<DateOnly> All(AgendaTime time)
    {
        if (time.Repeat is not { } repeat)
        {
            yield return time.Start;
            yield break;
        }

        var interval = Math.Max(1, repeat.Interval);
        var count = 0;
        foreach (var day in Candidates(time.Start, repeat, interval))
        {
            if ((repeat.Until is { } until && day > until) || (repeat.Count is { } most && count >= most) || day.Year > 9998)
            {
                yield break;
            }

            count++;
            yield return day;
        }
    }

    private static IEnumerable<DateOnly> Candidates(DateOnly start, Recurrence repeat, int interval)
    {
        switch (repeat.Every)
        {
            case RepeatEvery.Day:
                for (var day = start; ; day = day.AddDays(interval))
                {
                    yield return day;
                }

            case RepeatEvery.Week:
                // the weeks counted from the Monday of the start's week; in each week the chosen weekdays from Monday on
                var days = (repeat.Days.Count > 0 ? repeat.Days : [start.DayOfWeek]).Select(d => ((int)d + 6) % 7).Distinct().Order().ToArray();
                var monday = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                for (var week = monday; ; week = week.AddDays(7 * interval))
                {
                    foreach (var offset in days)
                    {
                        var day = week.AddDays(offset);
                        if (day >= start)
                        {
                            yield return day;
                        }
                    }
                }

            case RepeatEvery.Month:
                for (var months = 0; ; months += interval)
                {
                    var first = new DateOnly(start.Year, start.Month, 1).AddMonths(months);
                    yield return first.AddDays(Math.Min(start.Day, DateTime.DaysInMonth(first.Year, first.Month)) - 1);
                }

            default:
                for (var years = 0; ; years += interval)
                {
                    var year = start.Year + years;
                    yield return new DateOnly(year, start.Month, Math.Min(start.Day, DateTime.DaysInMonth(year, start.Month)));
                }
        }
    }
}

/// <summary>
/// The agenda's records kept in UNIVERSUM's folder (one JSON file, written whole through a temporary file so a failure
/// never leaves half a file); deleted records stay in the bin until emptied, so a deletion can be taken back. Nothing is
/// sent anywhere.
/// </summary>
public sealed class AgendaStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _file;
    private readonly Lock _gate = new();
    private List<AgendaItem>? _items;

    public AgendaStore(string file) => _file = file;

    /// <summary>Raised after a change (on the thread that made it).</summary>
    public event Action? Changed;

    /// <summary>The records not in the bin.</summary>
    public IReadOnlyList<AgendaItem> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. Loaded().Where(i => i.Deleted is null)];
            }
        }
    }

    /// <summary>The records in the bin, the latest deleted first.</summary>
    public IReadOnlyList<AgendaItem> Bin
    {
        get
        {
            lock (_gate)
            {
                return [.. Loaded().Where(i => i.Deleted is not null).OrderByDescending(i => i.Deleted)];
            }
        }
    }

    public AgendaItem? Find(Guid id)
    {
        lock (_gate)
        {
            return Loaded().FirstOrDefault(i => i.Id == id);
        }
    }

    /// <summary>Adds a record or keeps the changes of one already there.</summary>
    public void Save(AgendaItem item)
    {
        lock (_gate)
        {
            var items = Loaded();
            item.Modified = DateTimeOffset.Now;
            if (!items.Contains(item))
            {
                items.RemoveAll(i => i.Id == item.Id);
                items.Add(item);
            }

            Write(items);
        }

        Changed?.Invoke();
    }

    /// <summary>Moves a record to the bin.</summary>
    public void Delete(Guid id) => Change(id, i => i.Deleted = DateTimeOffset.Now);

    /// <summary>Takes a record back from the bin.</summary>
    public void Restore(Guid id) => Change(id, i => i.Deleted = null);

    /// <summary>Empties the bin (all, or what has been there longer than <paramref name="olderThan"/>); how many went.</summary>
    public int EmptyBin(TimeSpan? olderThan = null)
    {
        int removed;
        lock (_gate)
        {
            var items = Loaded();
            var limit = DateTimeOffset.Now - (olderThan ?? TimeSpan.Zero);
            removed = items.RemoveAll(i => i.Deleted is { } deleted && deleted <= limit);
            if (removed > 0)
            {
                Write(items);
            }
        }

        if (removed > 0)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    /// <summary>
    /// The records on the days <paramref name="from"/> – <paramref name="to"/>: every occurrence of a timed record, and the
    /// tasks without a time on their due day.
    /// </summary>
    public IReadOnlyList<Occurrence> Between(DateOnly from, DateOnly to)
    {
        var found = new List<Occurrence>();
        foreach (var item in Items)
        {
            if (item.Time is { } time)
            {
                foreach (var start in Recurrences.Starts(time, from, to))
                {
                    found.Add(new Occurrence(item, start, start.AddDays(time.Length - 1)));
                }
            }
            else if (item.Task is { Due: { } due } && due >= from && due <= to)
            {
                found.Add(new Occurrence(item, due, due));
            }
        }

        return [.. found.OrderBy(o => o.First).ThenBy(o => o.Item.Time?.StartTime ?? o.Item.Task?.DueTime ?? TimeOnly.MinValue)];
    }

    /// <summary>The hour a reminder of a whole day comes at (8:00 of the day, minus the time asked before).</summary>
    public static readonly TimeOnly WholeDayAt = new(8, 0);

    /// <summary>
    /// The reminders due after <paramref name="after"/> up to <paramref name="upTo"/>: each occurrence of a timed record
    /// with a reminder whose moment (the start minus the minutes asked) falls in that span; the start is given.
    /// </summary>
    public IReadOnlyList<(AgendaItem Item, DateTime Start)> Reminders(DateTime after, DateTime upTo)
    {
        var found = new List<(AgendaItem, DateTime)>();
        var from = DateOnly.FromDateTime(after);
        var to = DateOnly.FromDateTime(upTo).AddDays(8);
        foreach (var item in Items)
        {
            if (item.Time is not { ReminderMinutes: { } minutes } time || item.Task?.State is TaskState.Done or TaskState.Cancelled)
            {
                continue;
            }

            foreach (var start in Recurrences.Starts(time, from, to))
            {
                var moment = start.ToDateTime(time.StartTime ?? WholeDayAt);
                var remind = moment.AddMinutes(-minutes);
                if (remind > after && remind <= upTo)
                {
                    found.Add((item, moment));
                }
            }
        }

        return [.. found.OrderBy(f => f.Item2)];
    }

    private void Change(Guid id, Action<AgendaItem> change)
    {
        lock (_gate)
        {
            var items = Loaded();
            if (items.FirstOrDefault(i => i.Id == id) is not { } item)
            {
                return;
            }

            change(item);
            item.Modified = DateTimeOffset.Now;
            Write(items);
        }

        Changed?.Invoke();
    }

    private List<AgendaItem> Loaded()
    {
        if (_items is not null)
        {
            return _items;
        }

        try
        {
            _items = File.Exists(_file) ? JsonSerializer.Deserialize<List<AgendaItem>>(File.ReadAllText(_file), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            // a damaged file is kept aside, never overwritten silently
            File.Copy(_file, _file + ".poskodeny-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: false);
            _items = [];
        }

        return _items;
    }

    private void Write(List<AgendaItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
        var temporary = _file + ".part";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, Json));
        File.Move(temporary, _file, overwrite: true);
    }
}
