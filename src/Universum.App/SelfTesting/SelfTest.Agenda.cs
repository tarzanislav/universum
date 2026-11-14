using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Universum.App.Pages;
using Universum.App.Setup;
using Universum.App.Shell;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Platform.Windows;

namespace Universum.App.SelfTesting;

// the self-test: Kalendár, Úlohy, Poznámky
internal static partial class SelfTest
{
    /// <summary>
    /// The Dashboard's POZNÁMKY (owner, 31. 10. 2026, stage P3b): the calendar of the coming week without the tasks (they
    /// have their own list), the late and today's tasks (not those of a month later), the pinned note with the beginning of
    /// its text, a task written in one line, a task ticked off, an event and a note opened in their pages; invented texts.
    /// </summary>
    private static async Task TestDashboardAgendaAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var store = services.Agenda;
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslený koncert", Time = new Core.Agenda.AgendaTime { Start = today.AddDays(2), StartTime = new TimeOnly(19, 30) } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená úloha po termíne", Task = new Core.Agenda.AgendaTask { Due = today.AddDays(-1) } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená úloha na dnes", Task = new Core.Agenda.AgendaTask { Due = today, Priority = 2 } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená úloha o mesiac", Task = new Core.Agenda.AgendaTask { Due = today.AddDays(30) } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená pripnutá poznámka", Text = "Prvý riadok textu.\nĎalší riadok.", Note = new Core.Agenda.AgendaNote { Pinned = true } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená obyčajná poznámka", Note = new Core.Agenda.AgendaNote() });

        var dashboard = model.Dashboard;
        var agenda = dashboard.Agenda;
        model.Open(dashboard);
        agenda.Refresh();
        Check(agenda.Calendar.Any(r => r.Title == "Vymyslený koncert" && r.Time == "19:30" && !r.IsToday) && !agenda.Calendar.Any(r => r.Title.StartsWith("Vymyslená úloha", StringComparison.Ordinal)),
            "dashboard: the event of the coming week in the calendar, the tasks not there – " + string.Join(" | ", agenda.Calendar.Select(r => $"{r.Day} {r.Time} {r.Title}")));
        Check(agenda.Tasks.Select(t => t.Title).SequenceEqual(["Vymyslená úloha po termíne", "Vymyslená úloha na dnes"]) && agenda.Tasks[0].Late && agenda.Tasks[1].High && !agenda.Tasks[1].Late,
            "dashboard: the late task first, then today's, the one of a month later not yet – " + string.Join(" | ", agenda.Tasks.Select(t => $"{t.Due} {t.Title}")));
        Check(agenda.Notes.Count == 1 && agenda.Notes[0].Title == "Vymyslená pripnutá poznámka" && agenda.Notes[0].Preview == "Prvý riadok textu.",
            "dashboard: only the pinned note, with the beginning of its text");

        agenda.QuickText = "zajtra 18:00 vymyslená úloha z dashboardu #test !";
        Check(agenda.QuickHint.Contains("vymyslená úloha z dashboardu", StringComparison.Ordinal) && agenda.QuickHint.Contains("18:00", StringComparison.Ordinal),
            "dashboard: the line understood before it is added – " + agenda.QuickHint);
        agenda.AddCommand.Execute(null);
        var quick = store.Items.SingleOrDefault(i => i.Title == "vymyslená úloha z dashboardu");
        Check(quick?.Task is { Priority: 2, DueTime.Hour: 18 } && quick.Task.Due == today.AddDays(1) && quick.Tags.SequenceEqual(["test"]) && agenda.QuickText.Length == 0 &&
              agenda.Tasks.Any(t => t.Title == quick.Title && t.High), "dashboard: a task written in one line – " + agenda.Status);
        await Screenshot(window, "dashboard-agenda.png");

        agenda.Tasks.First(t => t.Title == "Vymyslená úloha po termíne").IsDone = true;
        Check(store.Items.Single(i => i.Title == "Vymyslená úloha po termíne").Task!.State == Core.Agenda.TaskState.Done && agenda.Tasks.All(t => t.Title != "Vymyslená úloha po termíne"),
            "dashboard: a task ticked off – done, it leaves the list");

        agenda.Calendar.First(r => r.Title == "Vymyslený koncert").OpenCommand.Execute(null);
        Check(model.SelectedTool.Page == model.Calendar && model.Calendar.SelectedDate == today.AddDays(2) && model.Calendar.Editor is { Title: "Vymyslený koncert" },
            "dashboard: an event opens in Kalendár on its day, in its editor");
        model.Calendar.Editor?.CancelCommand.Execute(null);
        model.Open(dashboard);
        agenda.Notes[0].OpenCommand.Execute(null);
        Check(model.SelectedTool.Page == model.AgendaNotes && model.AgendaNotes.Editor is AgendaEditor { Title: "Vymyslená pripnutá poznámka" }, "dashboard: a pinned note opens in Poznámky");
        (model.AgendaNotes.Editor as AgendaEditor)?.CancelCommand.Execute(null);

        foreach (var record in store.Items.ToList())
        {
            store.Delete(record.Id);
        }

        store.EmptyBin();
        model.Open(dashboard);
        Check(!agenda.Calendar.Any(r => r.Title.StartsWith("Vymysl", StringComparison.Ordinal)) && agenda.NoTasks && !agenda.HasNotes,
            "dashboard: no record left when the records are gone (the holidays stay) – " + string.Join(", ", agenda.Calendar.Select(r => r.Title)));
    }

    /// <summary>
    /// Poznámky (owner, 31. 10. 2026, stage P2): a quick note (its first sentence the title, #tags), a note of a day from
    /// Kalendár, the note of a title of Premiéry kept with the title, pinning, search, the bin, and a recording written
    /// down in the computer (the English speech of the test file, whisper.cpp); invented texts only.
    /// </summary>
    private static async Task TestNotesAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var notes = model.AgendaNotes;
        var today = DateOnly.FromDateTime(DateTime.Now);
        model.Open(notes);
        await notes.Ready;
        notes.ShowAll = true;
        notes.QuickText = "Vymyslený nápad na plagát. Skúsiť tmavšie pozadie #nápad";
        notes.AddCommand.Execute(null);
        var idea = services.Agenda.Items.Single(i => i.Title == "Vymyslený nápad na plagát.");
        Check(idea.Text == "Skúsiť tmavšie pozadie" && idea.Tags.SequenceEqual(["nápad"]) && idea.Note is not null && notes.Cards.Any(c => c.Record == idea),
            "notes: a quick note – the first sentence its title, the tag – " + notes.Status);

        // owner, 7. 11. 2026: links one under another, each on a row of its own, shortened by the row, opened by a click;
        // Meno namiesto linku remembered (invented addresses)
        services.Agenda.Save(new Core.Agenda.AgendaItem
        {
            Title = "Vymyslené odkazy",
            Text = "https://www.themoviedb.org/tv/111-vymysleny-serial\nhttps://www.themoviedb.org/movie/222-vymysleny-film\nupútavka: https://video.example/vymyslena-upoutavka",
            Note = new Core.Agenda.AgendaNote(),
        });
        var linked = notes.Cards.Single(c => c.Title == "Vymyslené odkazy");
        Check(linked.Rows.Count(r => r.IsLink) == 3 && linked.Rows[0].Link!.Shown == "themoviedb.org/tv/111-vymysleny-serial" && linked.Rows[2].Text == "upútavka:" &&
              linked.Rows[0].Link!.Row.Tmdb == new Core.Library.TmdbReference(MediaKind.Series, 111) && linked.Rows[0].Link!.ToolTip.Contains("https://www.themoviedb.org/tv/111-vymysleny-serial", StringComparison.Ordinal),
            "notes: every link on a row of its own – " + string.Join(" | ", linked.Rows.Select(r => r.Text ?? r.Link!.Shown)));
        notes.Edit(linked.Record);
        Check(notes.Editor is AgendaEditor { HasLinks: true, Links.Count: 3 }, "notes: the opened note shows its links to open");
        var namesBefore = notes.LinkNames;
        notes.LinkNames = true;
        Check(services.Settings.NoteLinkNames && notes.Cards.Single(c => c.Title == "Vymyslené odkazy").Rows.Count(r => r.IsLink) == 3,
            "notes: Meno namiesto linku remembered – " + notes.Status);
        notes.LinkNames = namesBefore;
        ((AgendaEditor)notes.Editor!).CancelCommand.Execute(null);
        // gone for good (the bin is counted below)
        services.Agenda.Delete(linked.Record!.Id);
        services.Agenda.EmptyBin();

        model.Calendar.Select(today);
        model.Calendar.NewNoteCommand.Execute(null);
        model.Calendar.Editor!.Title = "Vymyslená poznámka k dňu";
        model.Calendar.Editor.SaveCommand.Execute(null);
        model.Open(notes);
        notes.ShowDays = true;
        Check(notes.Cards.Count == 1 && notes.Cards[0].Title == "Vymyslená poznámka k dňu" && notes.Cards[0].Where.StartsWith("k dňu", StringComparison.Ordinal),
            "notes: the note of a day written in Kalendár – " + string.Join(", ", notes.Cards.Select(c => c.Title + " / " + c.Where)));

        var film = services.PremiereStore.Load().Films.First();
        var key = Core.News.MarkStore.Film(film.TmdbId);
        var before = services.Marks.Get(key);
        services.Marks.Set(key, before with { Note = "Vymyslená poznámka k titulu" });
        notes.ShowTitles = true;
        var titleCard = notes.Cards.SingleOrDefault(c => c.TitleKey == key);
        Check(titleCard is not null && titleCard.Title == film.Caption && titleCard.Text == "Vymyslená poznámka k titulu", "notes: the note of a title of Premiéry, by its name – " + titleCard?.Title);
        titleCard!.OpenCommand.Execute(null);
        var titleEditor = (TitleNoteEditor)notes.Editor!;
        titleEditor.Text = "Vymyslená poznámka k titulu – upravená";
        titleEditor.SaveCommand.Execute(null);
        Check(services.Marks.Get(key).Note == "Vymyslená poznámka k titulu – upravená" && !notes.HasEditor, "notes: the note of a title changed – kept with the title (Premiéry show it too)");

        notes.ShowAll = true;
        notes.Cards.First(c => c.Record == idea).PinCommand!.Execute(null);
        Check(idea.Note!.Pinned && notes.Cards.First(c => !c.IsTray).Record == idea, "notes: a pinned note first (after the notes of the Zásobník TMDB)");
        notes.Filter = "pozadie";
        Check(notes.Cards.Count == 1 && notes.Cards[0].Record == idea, "notes: looked for in the text");
        notes.Filter = "";
        await Screenshot(window, "notes.png");
        notes.Cards.First(c => c.Record == idea).DeleteCommand.Execute(null);
        notes.ShowBin = true;
        Check(notes.Cards.Any(c => c.Record == idea && c.InBin) && notes.BinText.EndsWith("(1)", StringComparison.Ordinal), "notes: in the bin – " + notes.BinText);
        notes.Cards.First(c => c.Record == idea).RestoreCommand!.Execute(null);
        notes.ShowAll = true;
        Check(notes.Cards.Any(c => c.Record == idea && !c.InBin), "notes: taken back from the bin");

        // the voice written down in the computer: the English sentences of the test file (synthetic voice)
        notes.Edit(idea);
        var editor = (AgendaEditor)notes.Editor!;
        Check(editor.HasVoice && editor.IsNote, "notes: the editor of a note offers the voice");
        try
        {
            var tools = await services.SpeechAsync();
            var samples = Path.Combine(_outputDirectory, "hlas.pcm");
            var wave = Path.Combine(_outputDirectory, "hlas.wav");
            var media = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "speech-und.mkv");
            await services.Runner.RunAsync(Core.Speech.VoiceNotes.SamplesFromFileRequest(tools, media, 1, TimeSpan.Zero, TimeSpan.FromSeconds(28), samples));
            Core.Speech.VoiceNotes.Wave(samples, wave);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (text, problem) = await services.Voice.TranscribeAsync(wave, "en");
            Write($"notes: the voice written down ({watch.Elapsed.TotalSeconds:0.0} s): {text ?? problem}");
            Check(text is not null && new[] { "evening", "village", "miller", "river" }.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)),
                "notes: English speech written down in the computer – " + (text ?? problem));
            editor.AddSpoken(text ?? "", null);
            Check(editor.Text.StartsWith("Skúsiť tmavšie pozadie", StringComparison.Ordinal) && editor.Text.Length > "Skúsiť tmavšie pozadie".Length + 10, "notes: the written text added to the note");
        }
        catch (Core.Speech.SpeechException ex)
        {
            Write("notes: the voice not tried – " + ex.Message);
        }

        await Screenshot(window, "notes-editor.png");
        editor.CancelCommand.Execute(null);

        services.Marks.Set(key, before);
        foreach (var record in services.Agenda.Items.ToList())
        {
            services.Agenda.Delete(record.Id);
        }

        services.Agenda.EmptyBin();
    }

    /// <summary>
    /// Plánované úlohy (owner, 31. 10. 2026, stage P2): tasks written in one line and understood, the groups by due day,
    /// done, the board, the checklist in the editor, the filters, the suggestions of UNIVERSUM (a chosen title in the input
    /// folder), and the task in Kalendár; invented titles only.
    /// </summary>
    private static async Task TestTasksAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var tasks = model.AgendaTasks;
        var today = DateOnly.FromDateTime(DateTime.Now);
        model.Open(tasks);
        await tasks.Ready;
        tasks.IsList = true;
        tasks.QuickText = "zajtra o 20:00 vymyslená úloha #test !";
        Check(tasks.QuickHint.Contains("vymyslená úloha", StringComparison.Ordinal) && tasks.QuickHint.Contains("20:00", StringComparison.Ordinal), "tasks: the line understood before it is added – " + tasks.QuickHint);
        tasks.AddCommand.Execute(null);
        var made = services.Agenda.Items.Single(i => i.Title == "vymyslená úloha");
        Check(made.Task is { Priority: 2, DueTime.Hour: 20 } && made.Task.Due == today.AddDays(1) && made.Tags.SequenceEqual(["test"]) && tasks.QuickText.Length == 0 &&
              tasks.Groups.Any(g => g.Title == "Zajtra" && g.Rows.Any(r => r.Item == made)), "tasks: a task of tomorrow 20:00, high priority, tagged – " + tasks.Status);
        tasks.QuickText = "skontrolovať vymyslené titulky";
        tasks.AddCommand.Execute(null);
        services.Agenda.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená úloha po termíne", Task = new Core.Agenda.AgendaTask { Due = today.AddDays(-2) } });
        Check(tasks.Groups.Select(g => g.Title).SequenceEqual(["Po termíne", "Zajtra", "Bez termínu"]) && tasks.Groups[0].Rows[0].IsOverdue,
            "tasks: the groups by due day, the overdue one first – " + string.Join(", ", tasks.Groups.Select(g => g.Title)));
        await Screenshot(window, "tasks-list.png");

        tasks.Groups.First(g => g.Title == "Po termíne").Rows[0].IsDone = true;
        Check(!tasks.Groups.Any(g => g.Title == "Po termíne") && services.Agenda.Items.Single(i => i.Title == "Vymyslená úloha po termíne").Task!.State == Core.Agenda.TaskState.Done,
            "tasks: a task done leaves the open groups");
        tasks.ShowDone = true;
        Check(tasks.Groups.Any(g => g.Title == "Hotové"), "tasks: the done tasks shown on demand");
        tasks.ShowDone = false;

        tasks.Edit(made);
        var editor = tasks.Editor!;
        editor.NewCheckText = "vymyslený krok 1";
        editor.AddCheckCommand.Execute(null);
        editor.NewCheckText = "vymyslený krok 2";
        editor.AddCheckCommand.Execute(null);
        editor.Checklist[0].Done = true;
        await Screenshot(window, "tasks-editor.png");
        editor.SaveCommand.Execute(null);
        Check(!tasks.HasEditor && made.Task!.Checklist.Count == 2 && made.Task.Checklist.Count(c => c.Done) == 1 &&
              tasks.Groups.SelectMany(g => g.Rows).First(r => r.Item == made).Progress == "☑ 1/2", "tasks: the checklist of the task – 1 of 2 done");

        tasks.Filter = "titulky";
        Check(tasks.Groups.SelectMany(g => g.Rows).Select(r => r.Title).SequenceEqual(["skontrolovať vymyslené titulky"]), "tasks: looked for without diacritics");
        tasks.Filter = "";
        tasks.Tag = "test";
        Check(tasks.Groups.SelectMany(g => g.Rows).All(r => r.Item == made), "tasks: by the tag");
        tasks.Tag = TasksPageViewModel.AllTags;

        tasks.IsBoard = true;
        var plain = tasks.ToDo.First(r => r.Title == "skontrolovať vymyslené titulky");
        plain.RightCommand.Execute(null);
        Check(tasks.InProgress.Any(r => r.Title == "skontrolovať vymyslené titulky"), "tasks: on the board to In progress");
        await Screenshot(window, "tasks-board.png");
        tasks.InProgress.First(r => r.Title == "skontrolovať vymyslené titulky").RightCommand.Execute(null);
        Check(tasks.Done.Any(r => r.Title == "skontrolovať vymyslené titulky"), "tasks: on the board to Done");
        tasks.IsBoard = false;

        if (services.InputWatch.Found.Count > 0)
        {
            var suggestion = tasks.Suggestions.FirstOrDefault(s => s.Key.StartsWith("vstup|", StringComparison.Ordinal));
            Check(suggestion is { HasAction: true }, "tasks: UNIVERSUM suggests to process a chosen title found in the input folder – " + string.Join(", ", tasks.Suggestions.Select(s => s.Title)));
            suggestion!.TakeCommand.Execute(null);
            Check(services.Agenda.Items.Any(i => i.Title == suggestion.Title && i.Task?.Due == today) && !tasks.Suggestions.Contains(suggestion) &&
                  services.Settings.HiddenSuggestions.Contains(suggestion.Key), "tasks: a suggestion taken as a task of today, then hidden");
        }

        model.Calendar.Select(today.AddDays(1));
        Check(model.Calendar.DetailSections.Any(s => s.Entries.Any(e => e.Record == made && e.TimeText == "20:00")), "tasks: the task of tomorrow is in Kalendár");

        foreach (var record in services.Agenda.Items.ToList())
        {
            services.Agenda.Delete(record.Id);
        }

        services.Agenda.EmptyBin();
        services.Settings.HiddenSuggestions.Clear();
        services.Settings.Save();
    }

    /// <summary>
    /// Kalendár (owner, 31. 10. 2026): the kept premieres and episodes of the tests before on their days, the holidays and
    /// names, the school holidays of a district, the layers, a record made in the editor (repeating, with a reminder), a
    /// task and a note of the day, the seven views and the way to Novinky; invented titles only.
    /// </summary>
    private static async Task TestCalendarAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var calendar = model.Calendar;
        model.Open(calendar);
        await calendar.Ready;
        // the calendar was shown once before (all pages are opened at the start): what Premiéry and Novinky keep now is read again
        await calendar.RefreshCommand.ExecuteAsync(null);
        calendar.IsMonth = true;
        calendar.TodayCommand.Execute(null);
        Check(calendar.Days.Count == 42 && calendar.Days.Count(d => d.IsToday) == 1 && calendar.Status.Length == 0, "calendar: the month of 42 days with today – " + calendar.Status);
        var todayCell = calendar.Days.First(d => d.IsToday);
        Check(todayCell.Entries.Any(e => e.Layer == CalendarLayer.EpisodesDomestic && e.Title.Contains("Domáci seriál", StringComparison.Ordinal)),
            "calendar: today's episode of Novinky – " + string.Join(", ", todayCell.Entries.Select(e => e.Title)));
        calendar.Select(FakeNews.Day(-2));
        Check(calendar.DetailSections.Any(s => s.Entries.Any(e => e.Layer == CalendarLayer.FilmsWorld && e.Title.Contains("World Invented Film", StringComparison.Ordinal))),
            "calendar: the premiere of Premiéry on its day – " + string.Join(" | ", calendar.DetailSections.Select(s => s.Title + ": " + string.Join(", ", s.Entries.Select(e => e.Title)))));
        calendar.LayerGroups[0].AllOn = false;
        Check(calendar.LayerGroups[0].Layers.All(l => !l.IsOn) && !calendar.DetailSections.Any(s => s.Entries.Any(e => e.Layer == CalendarLayer.FilmsWorld)) &&
              services.Settings.CalendarHiddenLayers.Contains(nameof(CalendarLayer.FilmsWorld)), "calendar: the premieres switched off all at once (kept in the settings)");
        calendar.LayerGroups[0].Layers[1].IsOn = true;
        Check(calendar.LayerGroups[0].AllOn is null, "calendar: one layer of the group on – the group's switch shows some");
        calendar.LayerGroups[0].AllOn = true;

        calendar.Select(new DateOnly(2026, 12, 24));
        Check(calendar.DetailSections.Any(s => s.Entries.Any(e => e.Layer == CalendarLayer.Holidays && e.Title == "Štedrý deň")) &&
              calendar.DetailNames.Contains("SK: Adam, Eva") && calendar.DetailNames.Contains("CZ: Adam, Eva"),
            "calendar: a holiday and the names of the day – " + string.Join(", ", calendar.DetailNames));
        calendar.RegionSwitches.First(r => r.Code == Core.Calendar.HolidayRegions.Czechia).IsOn = true;
        calendar.SchoolDistrict = calendar.SchoolDistricts.First(d => d.Value == "Zlín");
        calendar.Select(new DateOnly(2027, 3, 10));
        Check(calendar.HasCzechSchool && calendar.DetailSections.Any(s => s.Entries.Any(e => e.Layer == CalendarLayer.School && e.Title.StartsWith("Jarné prázdniny", StringComparison.Ordinal))),
            "calendar: the spring holidays of the district Zlín");
        calendar.Select(new DateOnly(2026, 9, 28));
        Check(calendar.DetailSections.Any(s => s.Entries.Any(e => e.Title.Contains("Den české státnosti", StringComparison.Ordinal) || e.Title.Contains("Deň českej štátnosti", StringComparison.Ordinal))),
            "calendar: the Czech holidays switched on");
        calendar.RegionSwitches.First(r => r.Code == Core.Calendar.HolidayRegions.Czechia).IsOn = false;

        // a record of the day: an event of the evening, weekly three times, an hour's reminder
        var eventDay = FakeNews.Day(3);
        calendar.Select(eventDay);
        calendar.NewEventCommand.Execute(null);
        var editor = calendar.Editor!;
        editor.SaveCommand.Execute(null);
        Check(calendar.HasEditor && editor.Problem.Length > 0, "calendar: a record without a name is not saved – " + editor.Problem);
        editor.Title = "Kino – vymyslený film";
        editor.AllDay = false;
        editor.StartTimeText = "20:00";
        editor.EndTimeText = "22:15";
        editor.Repeat = AgendaEditor.Repeats.First(r => r.Value == Core.Agenda.RepeatEvery.Week);
        editor.UntilDate = eventDay.AddDays(14).ToDateTime(TimeOnly.MinValue);
        editor.Reminder = AgendaEditor.Reminders.First(r => r.Value == 60);
        editor.TagsText = "#kino";
        await Screenshot(window, "calendar-editor.png");
        editor.SaveCommand.Execute(null);
        var cinema = editor.Saved!;
        Check(!calendar.HasEditor && calendar.DetailSections.Any(s => s.Entries.Any(e => e.Record == cinema && e.TimeText == "20:00")) &&
              services.Agenda.Between(eventDay, eventDay.AddDays(30)).Count(o => o.Item == cinema) == 3 && cinema.Tags.SequenceEqual(["kino"]),
            "calendar: the event saved, weekly three times");
        model.CheckReminders(eventDay.ToDateTime(new TimeOnly(19, 5)));
        Check(model.HasReminder && model.ReminderText.Contains("Kino – vymyslený film", StringComparison.Ordinal), "calendar: the reminder an hour before – " + model.ReminderText);
        await Screenshot(window, "calendar-reminder.png");
        model.DismissReminderCommand.Execute(null);

        calendar.NewTaskCommand.Execute(null);
        calendar.Editor!.Title = "Spracovať vymyslený seriál";
        calendar.Editor.SaveCommand.Execute(null);
        calendar.NewNoteCommand.Execute(null);
        calendar.Editor!.Title = "Poznámka k vymyslenému dňu";
        calendar.Editor.Text = "Vymyslený text poznámky.";
        calendar.Editor.SaveCommand.Execute(null);
        var mine = calendar.DetailSections.FirstOrDefault(s => s.Entries.Any(e => e.Record is not null));
        Check(mine is not null && mine.Entries.Any(e => e.Layer == CalendarLayer.Tasks) && mine.Entries.Any(e => e.Layer == CalendarLayer.Notes) && mine.Entries.Any(e => e.Layer == CalendarLayer.Events),
            "calendar: the event, the task and the note of the day together – " + string.Join(", ", mine?.Entries.Select(e => e.Layer + " " + e.Title) ?? []));
        mine!.Entries.First(e => e.Record == cinema).OpenCommand!.Execute(null);
        Check(calendar.Editor is { IsEvent: true, Title: "Kino – vymyslený film", HasRepeat: true }, "calendar: a record opens in the editor");
        await Screenshot(window, "calendar-detail.png");
        calendar.Editor!.CancelCommand.Execute(null);

        calendar.TodayCommand.Execute(null);
        // the panel of the layers folded: the month has the room
        calendar.ShowLayers = false;
        Check(!services.Settings.CalendarLayersShown, "calendar: the panel of the layers folded (kept in the settings)");
        await Screenshot(window, "calendar-month.png");
        calendar.ShowLayers = true;
        calendar.IsWeek = true;
        Check(calendar.Days.Count == 7, "calendar: the week of seven days");
        await Screenshot(window, "calendar-week.png");
        calendar.IsWeekList = true;
        Check(calendar.Days.Count == 7 && calendar.IsList, "calendar: the week as a list");
        await Screenshot(window, "calendar-weeklist.png");
        calendar.IsMonthList = true;
        var today = DateOnly.FromDateTime(DateTime.Now);
        Check(calendar.Days.Count == DateTime.DaysInMonth(today.Year, today.Month), "calendar: the month as a list");
        calendar.HideEmptyDays = true;
        Check(calendar.Days.All(d => d.Shown.Count > 0 || d.HasHoliday || d.IsToday), "calendar: the empty days left out");
        await Screenshot(window, "calendar-monthlist.png");
        calendar.HideEmptyDays = false;
        calendar.IsYearMonths = true;
        Check(calendar.Months.Count == 12 && calendar.Months[1].Cells.Count(c => c is not null) == DateTime.DaysInMonth(today.Year, 2), "calendar: the year as twelve months");
        await Screenshot(window, "calendar-year.png");
        calendar.IsYearRows = true;
        Check(calendar.YearRows.Count == 31 && calendar.YearRows[30].Months.Count(m => m is not null) == 7, "calendar: the year in rows (31 days in seven months)");
        await Screenshot(window, "calendar-yearrows.png");
        calendar.IsAgenda = true;
        Check(calendar.Days.Count > 0 && calendar.Days.All(d => d.Shown.Count > 0 || d.HasHoliday || d.IsToday) && calendar.Days.Any(d => d.Date == eventDay),
            "calendar: the agenda – only the days with something");
        await Screenshot(window, "calendar-agenda.png");
        Check(services.Settings.CalendarView == nameof(CalendarView.Agenda), "calendar: the view kept in the settings");

        // a click on an episode opens Novinky with the series looked for
        calendar.IsMonth = true;
        calendar.TodayCommand.Execute(null);
        var episode = calendar.DetailSections.SelectMany(s => s.Entries).First(e => e.Layer == CalendarLayer.EpisodesDomestic);
        episode.OpenCommand!.Execute(null);
        Check(model.SelectedTool.Page == model.SeriesNews && model.SeriesNews.Filter.Length > 0 && episode.Title.TrimStart('★', ' ').StartsWith(model.SeriesNews.Filter, StringComparison.Ordinal),
            $"calendar: an episode opens in Novinky with its series looked for – {episode.Title} → {model.SeriesNews.Filter}");
        model.SeriesNews.Filter = "";

        // the layer UNIVERSUM: today's runs of processing of the tests before, a run opened in Záznamy spracovania
        model.Open(calendar);
        calendar.TodayCommand.Execute(null);
        var runs = calendar.DetailSections.FirstOrDefault(s => s.Title == "UNIVERSUM");
        var run = runs?.Entries.FirstOrDefault(e => e.Layer == CalendarLayer.Processed);
        Check(run is not null && run.HasTime && run.Detail.Length > 0, "calendar: today's runs of processing in the layer UNIVERSUM – " + string.Join(", ", runs?.Entries.Select(e => $"{e.TimeText} {e.Title}") ?? []));
        run!.OpenCommand!.Execute(null);
        Check(model.SelectedTool.Page == model.Logs && model.Logs.Selected is { } shownLog && shownLog.Kind == run.Title.TrimStart('⚠', ' '),
            "calendar: a run opens its log – " + run.Title);
        model.Open(calendar);

        foreach (var record in services.Agenda.Items.ToList())
        {
            services.Agenda.Delete(record.Id);
        }

        services.Agenda.EmptyBin();
        services.Settings.CalendarRegions = null;
        services.Settings.CalendarSchoolDistrict = null;
        services.Settings.Save();
        model.Open(calendar);
        calendar.Build();
    }
}
