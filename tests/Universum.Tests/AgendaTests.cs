using Universum.Core.Agenda;

namespace Universum.Tests;

/// <summary>The agenda of POZNÁMKY: repeats of timed records, tasks on their due day, the bin, a damaged file kept aside.</summary>
public sealed class AgendaTests
{
    private static List<DateOnly> Starts(AgendaTime time, DateOnly from, DateOnly to) => [.. Recurrences.Starts(time, from, to)];

    [Test]
    public void RepeatsOfDaysWeeksMonthsAndYears()
    {
        var daily = new AgendaTime { Start = new(2026, 11, 1), Repeat = new Recurrence { Every = RepeatEvery.Day, Interval = 3, Count = 4 } };
        Assert.True(Starts(daily, new(2026, 1, 1), new(2027, 1, 1)).SequenceEqual([new(2026, 11, 1), new(2026, 11, 4), new(2026, 11, 7), new(2026, 11, 10)]), "every 3 days, 4 times");

        var weekly = new AgendaTime { Start = new(2026, 11, 4), Repeat = new Recurrence { Every = RepeatEvery.Week, Interval = 2, Days = [DayOfWeek.Monday, DayOfWeek.Friday] } };
        Assert.True(Starts(weekly, new(2026, 11, 1), new(2026, 11, 30)).SequenceEqual([new(2026, 11, 6), new(2026, 11, 16), new(2026, 11, 20), new(2026, 11, 30)]),
            "every second week on Monday and Friday, from Wednesday 4 November – " + string.Join(", ", Starts(weekly, new(2026, 11, 1), new(2026, 11, 30))));

        var monthly = new AgendaTime { Start = new(2027, 1, 31), Repeat = new Recurrence { Every = RepeatEvery.Month, Until = new(2027, 5, 1) } };
        Assert.True(Starts(monthly, new(2027, 1, 1), new(2027, 12, 31)).SequenceEqual([new(2027, 1, 31), new(2027, 2, 28), new(2027, 3, 31), new(2027, 4, 30)]), "the 31st: the last day of shorter months, until 1 May");

        var birthday = new AgendaTime { Start = new(2024, 2, 29), Repeat = new Recurrence { Every = RepeatEvery.Year } };
        Assert.True(Starts(birthday, new(2025, 1, 1), new(2028, 12, 31)).SequenceEqual([new(2025, 2, 28), new(2026, 2, 28), new(2027, 2, 28), new(2028, 2, 29)]), "29 February in other years: 28 February");

        var skipped = new AgendaTime { Start = new(2026, 11, 2), Repeat = new Recurrence { Every = RepeatEvery.Week }, Skipped = [new(2026, 11, 9)] };
        Assert.True(Starts(skipped, new(2026, 11, 1), new(2026, 11, 20)).SequenceEqual([new(2026, 11, 2), new(2026, 11, 16)]), "a cancelled repeat");

        var festival = new AgendaTime { Start = new(2026, 10, 30), End = new(2026, 11, 2) };
        Assert.True(Starts(festival, new(2026, 11, 1), new(2026, 11, 30)).SequenceEqual([new(2026, 10, 30)]) && festival.Length == 4, "several days that began in the month before");
    }

    [Test]
    public void StoreKeepsRecordsAndTheBin()
    {
        var folder = Directory.CreateTempSubdirectory("universum-agenda-").FullName;
        try
        {
            var file = Path.Combine(folder, "agenda.json");
            var store = new AgendaStore(file);
            var changes = 0;
            store.Changed += () => changes++;
            var cinema = new AgendaItem { Title = "Kino – Vymyslený film", Time = new AgendaTime { Start = new(2026, 11, 6), StartTime = new(20, 0), ReminderMinutes = 60 } };
            var task = new AgendaItem { Title = "Spracovať vymyslený seriál", Task = new AgendaTask { Due = new(2026, 11, 7), Priority = 2 } };
            var note = new AgendaItem { Title = "Nápad", Text = "Vymyslená poznámka", Note = new AgendaNote { Pinned = true }, Tags = ["nápad"] };
            store.Save(cinema);
            store.Save(task);
            store.Save(note);
            var week = store.Between(new(2026, 11, 2), new(2026, 11, 8));
            Assert.True(week.Select(o => o.Item.Title).SequenceEqual(["Kino – Vymyslený film", "Spracovať vymyslený seriál"]), "the event and the task due that week; the note without a day is not on a day");

            var again = new AgendaStore(file);
            Assert.True(again.Items.Count == 3 && again.Find(cinema.Id) is { Time: { StartTime: { Hour: 20 }, ReminderMinutes: 60 } } &&
                        again.Find(task.Id) is { Task: { Priority: 2, State: TaskState.Open } } && again.Find(note.Id) is { Note.Pinned: true, Tags: ["nápad"] },
                "kept for the next start");

            store.Delete(task.Id);
            Assert.True(store.Items.Count == 2 && store.Bin.Count == 1 && store.Between(new(2026, 11, 2), new(2026, 11, 8)).Count == 1, "in the bin, off the calendar");
            store.Restore(task.Id);
            Assert.True(store.Items.Count == 3 && store.Bin.Count == 0, "taken back");
            store.Delete(note.Id);
            Assert.True(store.EmptyBin(TimeSpan.FromDays(30)) == 0 && store.EmptyBin() == 1 && new AgendaStore(file).Items.Count == 2, "the bin emptied – for good");
            Assert.True(changes == 7, "every change told – " + changes);

            var reminders = store.Reminders(new DateTime(2026, 11, 6, 18, 0, 0), new DateTime(2026, 11, 6, 19, 0, 0));
            Assert.True(reminders is [{ Start.Hour: 20 }] && store.Reminders(new DateTime(2026, 11, 6, 19, 0, 0), new DateTime(2026, 11, 6, 21, 0, 0)).Count == 0,
                "the reminder an hour before the cinema, once");

            File.WriteAllText(file, "{ not json");
            var damaged = new AgendaStore(file);
            Assert.True(damaged.Items.Count == 0 && Directory.GetFiles(folder, "agenda.json.poskodeny-*").Length == 1, "a damaged file is kept aside");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
