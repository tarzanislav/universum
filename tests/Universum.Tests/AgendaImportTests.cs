using Universum.Core.Agenda;

namespace Universum.Tests;

/// <summary>POZNÁMKY, stage P4: iCalendar files read back (other programs and UNIVERSUM's own), the actions of UNIVERSUM on time.</summary>
public sealed class AgendaImportTests
{
    [Test]
    public void IcsOfAnotherProgram()
    {
        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Vymyslený program//EN\r\n" +
                           "BEGIN:VTIMEZONE\r\nTZID:Vymyslená zóna\r\nBEGIN:STANDARD\r\nDTSTART:19701025T030000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n" +
                           "BEGIN:VEVENT\r\nUID:abc-123@vymyslene.example\r\nDTSTART;VALUE=DATE:20261224\r\nDTEND;VALUE=DATE:20261227\r\n" +
                           "SUMMARY:Vymyslené sviatky u babky\r\nDESCRIPTION:Prvý riadok\\nDruhý\\, s čiarkou\r\nLOCATION:Vymyslená obec\r\nCATEGORIES:rodina,#cesta\r\nEND:VEVENT\r\n" +
                           "BEGIN:VEVENT\r\nUID:def-456@vymyslene.example\r\nDTSTART:20261105T190000\r\nDURATION:PT1H30M\r\n" +
                           "RRULE:FREQ=WEEKLY;BYDAY=TH;COUNT=5\r\nEXDATE:20261112T190000\r\nSUMMARY:Vymyslený krúžok s veľmi dlhým názvom\r\n  ktorý pokračuje na ďalšom riadku\r\n" +
                           "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT15M\r\nEND:VALARM\r\nEND:VEVENT\r\n" +
                           "BEGIN:VEVENT\r\nUID:ghi@vymyslene.example\r\nDTSTART:20261201T080000\r\nRRULE:FREQ=MONTHLY;BYDAY=1MO\r\nSUMMARY:Vymyslená porada\r\nEND:VEVENT\r\n" +
                           "BEGIN:VEVENT\r\nSUMMARY:Bez dátumu\r\nEND:VEVENT\r\n" +
                           "BEGIN:VTODO\r\nUID:todo-1@vymyslene.example\r\nDUE;VALUE=DATE:20261110\r\nPRIORITY:2\r\nSTATUS:IN-PROCESS\r\nSUMMARY:Vymyslená úloha\r\n" +
                           "DESCRIPTION:Popis\\n[x] prvý krok\\n[ ] druhý krok\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var read = AgendaImport.Ics(ics);
        Assert.True(read.Items.Count == 4 && read.Skipped == 1 && read.Simplified == 1, $"four records, one without a day, one repeat simplified – {read.Items.Count}/{read.Skipped}/{read.Simplified}");

        var holidays = read.Items[0];
        Assert.True(holidays.Title == "Vymyslené sviatky u babky" && holidays.Time is { Start.Day: 24, End.Day: 26, StartTime: null } &&
                    holidays.Text == "Prvý riadok\nDruhý, s čiarkou\nVymyslená obec" && holidays.Tags.SequenceEqual(["rodina", "cesta"]) && holidays.Uid == "abc-123@vymyslene.example",
            "whole days 24–26 December (the end is not included), the text unescaped with the place, the categories – " + holidays.Text);

        var club = read.Items[1];
        Assert.True(club.Title == "Vymyslený krúžok s veľmi dlhým názvom ktorý pokračuje na ďalšom riadku" &&
                    club.Time is { StartTime.Hour: 19, EndTime: { Hour: 20, Minute: 30 }, ReminderMinutes: 15, Repeat: { Every: RepeatEvery.Week, Count: 5, Days.Count: 0 } } &&
                    club.Time.Skipped.SequenceEqual([new DateOnly(2026, 11, 12)]), "an evening weekly five times, an hour and a half, a reminder, a cancelled day; the folded line joined");

        Assert.True(read.Items[2].Time?.Repeat is { Every: RepeatEvery.Month, Days.Count: 0 }, "the first Monday of a month kept as monthly");

        var task = read.Items[3];
        Assert.True(task.Task is { Due.Day: 10, Priority: 2, State: TaskState.InProgress } && task.Task.Checklist.Count == 2 && task.Task.Checklist[0].Done && !task.Task.Checklist[1].Done &&
                    task.Text == "Popis", "a task with its due day, priority, state and checklist");
    }

    [Test]
    public void OwnExportComesBackAsTheSameRecords()
    {
        var day = new DateOnly(2026, 11, 20);
        var both = new AgendaItem
        {
            Title = "Vymyslené kino",
            Tags = ["kino"],
            Time = new AgendaTime { Start = day, StartTime = new TimeOnly(20, 0), EndTime = new TimeOnly(22, 0), ReminderMinutes = 60, Repeat = new Recurrence { Every = RepeatEvery.Week, Days = [DayOfWeek.Friday, DayOfWeek.Sunday], Until = day.AddDays(30) } },
            Task = new AgendaTask { Due = day, Priority = 0, Checklist = [new CheckItem { Text = "lístky", Done = true }] },
        };
        var note = new AgendaItem { Title = "Vymyslená poznámka", Note = new AgendaNote(), Links = [new AgendaLink(LinkKind.Day, "2026-11-21", "21. 11. 2026")] };
        var holiday = Universum.Core.Calendar.Holidays.On(new DateOnly(2026, 12, 24), ["SK"]);
        var ics = AgendaExport.Ics([both, note], holiday);
        var read = AgendaImport.Ics(ics);
        Assert.True(read.Items.Count == 2 && read.Skipped == holiday.Count, $"the event and task one record, the note, the holidays left out – {read.Items.Count}/{read.Skipped}");
        var back = read.Items.Single(i => i.Id == both.Id);
        Assert.True(back.Time is { StartTime.Hour: 20, EndTime.Hour: 22, ReminderMinutes: 60 } && back.Time.Repeat!.Days.SequenceEqual([DayOfWeek.Friday, DayOfWeek.Sunday]) &&
                    back.Time.Repeat.Until == day.AddDays(30) && back.Task is { Priority: 0 } && back.Task.Checklist.Single().Done && back.Tags.SequenceEqual(["kino"]),
            "the repeat on two days until a day, the reminder, the task with its checklist");
        var noteBack = read.Items.Single(i => i.Id == note.Id);
        Assert.True(noteBack.Note is not null && noteBack.Time is null && noteBack.Links.Single().Target == "2026-11-21", "the note of a day stays a note of that day");

        var folder = Directory.CreateTempSubdirectory("universum-ics-").FullName;
        try
        {
            var store = new AgendaStore(Path.Combine(folder, "agenda.json"));
            store.Save(both);
            Assert.True(store.Add(read.Items) == 1 && store.Add(read.Items) == 0 && store.Items.Count == 2, "read into the agenda: what is there already is left out, nothing twice");
            var other = AgendaImport.Ics("BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:x@vymyslene.example\nDTSTART;VALUE=DATE:20261201\nSUMMARY:Vymyslené\nEND:VEVENT\nEND:VCALENDAR\n");
            Assert.True(store.Add(other.Items) == 1 && store.Add(AgendaImport.Ics("BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:x@vymyslene.example\nDTSTART;VALUE=DATE:20261201\nSUMMARY:Vymyslené\nEND:VEVENT\nEND:VCALENDAR\n").Items) == 0,
                "the same file of another program read twice: once");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void DurationsAndText()
    {
        Assert.True(AgendaImport.Duration("-PT15M") == TimeSpan.FromMinutes(-15) && AgendaImport.Duration("P1DT2H") == TimeSpan.FromHours(26) && AgendaImport.Duration("P1W") == TimeSpan.FromDays(7) &&
                    AgendaImport.Duration("PT") is null && AgendaImport.Duration("15M") is null && AgendaImport.Duration("P99999999999D") is null, "durations of RFC 5545");
        Assert.True(AgendaImport.Unescape(@"a\,b\;c\\d\ne") == "a,b;c\\d\ne", "the escaped letters of a text");
    }

    [Test]
    public void ActionsComeOnTime()
    {
        var folder = Directory.CreateTempSubdirectory("universum-actions-").FullName;
        try
        {
            var store = new AgendaStore(Path.Combine(folder, "agenda.json"));
            var nightly = new AgendaItem
            {
                Title = "Obnoviť novinky",
                Time = new AgendaTime { Start = new DateOnly(2026, 11, 1), StartTime = new TimeOnly(3, 0), Repeat = new Recurrence { Every = RepeatEvery.Day } },
                Action = new AgendaAction { Kind = AgendaActionKind.RefreshNews },
            };
            store.Save(nightly);
            store.Save(new AgendaItem { Title = "Bez akcie", Time = new AgendaTime { Start = new DateOnly(2026, 11, 2), StartTime = new TimeOnly(3, 0) } });
            store.Save(new AgendaItem { Title = "Akcia bez času", Time = new AgendaTime { Start = new DateOnly(2026, 11, 2) }, Action = new AgendaAction { Kind = AgendaActionKind.SyncHistra } });
            var due = store.DueActions(new DateTime(2026, 11, 2, 2, 59, 30), new DateTime(2026, 11, 2, 3, 0, 0));
            Assert.True(due.Count == 1 && due[0].Item.Id == nightly.Id && due[0].Start == new DateTime(2026, 11, 2, 3, 0, 0), "the nightly action at 3:00, not the record without an action or without an hour");
            Assert.True(store.DueActions(new DateTime(2026, 11, 2, 3, 0, 0), new DateTime(2026, 11, 2, 3, 0, 30)).Count == 0, "not twice");
            Assert.True(AgendaStore.NextStart(nightly, new DateTime(2026, 11, 2, 3, 0, 0)) == new DateTime(2026, 11, 3, 3, 0, 0), "the next start is tomorrow at 3:00");
            Assert.True(new AgendaStore(Path.Combine(folder, "agenda.json")).Find(nightly.Id)?.Action?.Kind == AgendaActionKind.RefreshNews, "the action kept in the file");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
