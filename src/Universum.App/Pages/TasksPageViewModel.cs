using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Agenda;
using Universum.Core.Calendar;
using Universum.Core.News;
using Universum.Core.Reports;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>A task in the list or on the board of Plánované úlohy.</summary>
public sealed class TaskRow : ObservableObject
{
    private readonly TasksPageViewModel _page;

    public TaskRow(AgendaItem item, TasksPageViewModel page)
    {
        Item = item;
        _page = page;
        EditCommand = new RelayCommand(() => page.Edit(item));
        DeleteCommand = new RelayCommand(() => page.Delete(item));
        LeftCommand = new RelayCommand(() => page.Move(item, -1), () => State != TaskState.Open);
        RightCommand = new RelayCommand(() => page.Move(item, 1), () => State != TaskState.Done);
    }

    public AgendaItem Item { get; }

    private TaskState State => Item.Task?.State ?? TaskState.Open;

    public string Title => Item.Title.Length > 0 ? Item.Title : Item.Text.Split('\n')[0];

    /// <summary>Done (the box ticks it; ticked again opens it).</summary>
    public bool IsDone
    {
        get => State == TaskState.Done;
        set
        {
            if (value != IsDone)
            {
                _page.SetDone(Item, value);
            }
        }
    }

    public bool IsInProgress => State == TaskState.InProgress;

    /// <summary>"dnes 20:00", "po 3. 11.", "bez termínu".</summary>
    public string DueText { get; init; } = "";

    public bool IsOverdue { get; init; }

    public Brush PriorityColor => Item.Task?.Priority switch
    {
        2 => CalendarColors.Of(CalendarLayer.Holidays),
        0 => CalendarColors.Of(CalendarLayer.Memorial),
        _ => CalendarColors.Of(CalendarLayer.Tasks),
    };

    public string PriorityText => Item.Task?.Priority switch { 2 => "!", 0 => "↓", _ => "" };

    public string Tags => string.Join(" ", Item.Tags.Select(t => "#" + t));

    /// <summary>"2/5" of the checklist; empty without one.</summary>
    public string Progress => Item.Task?.Checklist is { Count: > 0 } list ? $"☑ {list.Count(c => c.Done)}/{list.Count}" : "";

    public string Text => Item.Text;

    public RelayCommand EditCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand LeftCommand { get; }

    public RelayCommand RightCommand { get; }
}

/// <summary>A group of the list: "Po termíne", "Dnes" …</summary>
public sealed record TaskGroup(string Title, Brush Color, IReadOnlyList<TaskRow> Rows)
{
    public string Count => Rows.Count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A task UNIVERSUM suggests from what it knows (owner, 31. 10. 2026): a chosen title in the input folder, an episode of a
/// chosen series that has aired but is not there yet, a chosen film premiering, a poster waiting, a processing with
/// problems. Suggestions are worked out when the page is shown and never kept; Prevziať makes a task, Skryť hides it.
/// </summary>
public sealed class TaskSuggestion(string key, string glyph, string title, string detail, DateOnly? due, AgendaLink? link, string? action, Func<Task>? act, TasksPageViewModel page)
{
    public string Key { get; } = key;

    public string Glyph { get; } = glyph;

    public string Title { get; } = title;

    public string Detail { get; } = detail;

    public DateOnly? Due { get; } = due;

    public AgendaLink? Link { get; } = link;

    /// <summary>The button of what can be done at once ("Spracovať", "Pozrieť záznam"); null: none.</summary>
    public string? ActionText { get; } = action;

    public bool HasAction => act is not null;

    public AsyncRelayCommand ActionCommand { get; } = new(() => act?.Invoke() ?? Task.CompletedTask);

    public RelayCommand TakeCommand { get; } = new(() => page.Take(key));

    public RelayCommand HideCommand { get; } = new(() => page.Hide(key));
}

/// <summary>
/// Plánované úlohy (owner, 31. 10. 2026 – the group POZNÁMKY, stage P2): the user's tasks from the one store of the
/// agenda (a task with a due day is in Kalendár by itself), grouped by their due day or on a board of three columns;
/// a task written in one line is understood (QuickEntry); the suggestions of UNIVERSUM on top.
/// </summary>
public sealed class TasksPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly List<TaskSuggestion> _allSuggestions = [];
    private string _quick = "";
    private string _filter = "";
    private string _tag = AllTags;
    private AgendaEditor? _editor;
    private string _status = "";
    private bool _building;

    public TasksPageViewModel(AppServices services)
    {
        _services = services;
        AddCommand = new RelayCommand(Add);
        NewCommand = new RelayCommand(() => Edit(null));
        RefreshCommand = new AsyncRelayCommand(SuggestAsync);
        EmptyDoneCommand = new RelayCommand(EmptyDone);
        services.Agenda.Changed += () =>
        {
            if (!_building)
            {
                Build();
            }
        };
        services.InputWatch.Changed += () => _ = SuggestAsync();
        Build();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Sends a title found in the input folder to processing (set by the main window).</summary>
    public Func<FoundInFolder, Task> ProcessFound { get; set; } = _ => Task.CompletedTask;

    /// <summary>Opens a processing log (set by the main window).</summary>
    public Action<string> OpenLog { get; set; } = _ => { };

    /// <summary>Opens a premiere or an episode (set by the main window).</summary>
    public Action<CalendarLayer, string> OpenNews { get; set; } = (_, _) => { };

    /// <summary>The suggestions are worked out (awaited by the self-test).</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    /// <summary>One line: "zajtra o 20:00 spracovať film #kino !".</summary>
    public string QuickText { get => _quick; set => SetProperty(ref _quick, value); }

    /// <summary>What the line will make, as it is understood now.</summary>
    public string QuickHint
    {
        get
        {
            if (QuickText.Trim().Length == 0)
            {
                return L("Napríklad: zajtra o 20:00 spracovať film #kino !   ·   do piatku zavolať   ·   za 3 dni …", "Například: zítra ve 20:00 zpracovat film #kino !   ·   do pátku zavolat   ·   za 3 dny …",
                    "For example: tomorrow 20:00 process the film #cinema !   ·   friday call   ·   in 3 days …");
            }

            return HintOf(QuickText);
        }
    }

    /// <summary>What a line of the quick entry will make, as it is understood now (here and on the Dashboard).</summary>
    internal static string HintOf(string line)
    {
        var task = QuickEntry.Parse(line, Today);
        return "→ " + (task.Title.Length > 0 ? task.Title : "…") + " · " + Due(task.Due, task.Time) +
               (task.Priority == 2 ? L(" · ! vysoká", " · ! vysoká", " · ! high") : task.Priority == 0 ? L(" · nízka", " · nízká", " · low") : "") +
               (task.Tags.Count > 0 ? " · " + string.Join(" ", task.Tags.Select(t => "#" + t)) : "");
    }

    public RelayCommand AddCommand { get; }

    public RelayCommand NewCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand EmptyDoneCommand { get; }

    /// <summary>Text to look for (without diacritics).</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                Build();
            }
        }
    }

    /// <summary>The choice of the tag filter that shows all tasks.</summary>
    public static string AllTags => L("všetky", "všechny", "all");

    /// <summary>The tags of the tasks, for the filter (the first: all).</summary>
    public IReadOnlyList<string> TagChoices { get; private set; } = [AllTags];

    public string Tag
    {
        get => _tag;
        set
        {
            if (SetProperty(ref _tag, value ?? AllTags))
            {
                Build();
            }
        }
    }

    public bool ShowDone
    {
        get => _services.Settings.TasksShowDone;
        set
        {
            if (value != _services.Settings.TasksShowDone)
            {
                _services.Settings.TasksShowDone = value;
                _services.Settings.Save();
                OnPropertyChanged();
                Build();
            }
        }
    }

    public bool IsBoard
    {
        get => _services.Settings.TasksBoard;
        set
        {
            if (value != _services.Settings.TasksBoard)
            {
                _services.Settings.TasksBoard = value;
                _services.Settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsList));
                Build();
            }
        }
    }

    public bool IsList
    {
        get => !IsBoard;
        set => IsBoard = !value;
    }

    /// <summary>The list: groups by the due day.</summary>
    public ObservableCollection<TaskGroup> Groups { get; } = [];

    /// <summary>The board: to do, in progress, done.</summary>
    public ObservableCollection<TaskRow> ToDo { get; } = [];

    public ObservableCollection<TaskRow> InProgress { get; } = [];

    public ObservableCollection<TaskRow> Done { get; } = [];

    public ObservableCollection<TaskSuggestion> Suggestions { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    public bool IsEmpty => Groups.Count == 0 && ToDo.Count + InProgress.Count + Done.Count == 0;

    public string Summary { get; private set; } = "";

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public AgendaEditor? Editor
    {
        get => _editor;
        private set
        {
            if (SetProperty(ref _editor, value))
            {
                OnPropertyChanged(nameof(HasEditor));
            }
        }
    }

    public bool HasEditor => Editor is not null;

    /// <summary>Tlač a export (stage P3): the tasks as they are shown (groups and filters), with boxes to tick on paper.</summary>
    public RelayCommand PrintCommand => _print ??= new RelayCommand(() => ShowReport(Report()));

    private RelayCommand? _print;

    public Action<ReportViewModel> ShowReport { get; set; } = model =>
        new ReportWindow(model) { Owner = System.Windows.Application.Current.MainWindow }.Show();

    public ReportViewModel Report()
    {
        var title = L("Plánované úlohy", "Plánované úkoly", "Planned tasks");
        var shown = Groups.SelectMany(g => g.Rows).Select(r => r.Item).ToList();
        IReadOnlyList<ReportExport> exports =
        [
            new("iCalendar (.ics)…", "iCalendar|*.ics", "UNIVERSUM – úlohy.ics", path =>
            {
                File.WriteAllText(path, AgendaExport.Ics(shown.Select(i => new AgendaItem { Id = i.Id, Title = i.Title, Text = i.Text, Tags = i.Tags, Task = i.Task, Created = i.Created })));
                return L("Uložené: ", "Uloženo: ", "Saved: ") + path;
            }, ToolTip: "Úlohy pre Outlook a Google (VTODO)"),
            new("CSV (Excel)…", "CSV|*.csv", "UNIVERSUM – úlohy.csv", path =>
            {
                File.WriteAllText(path, AgendaExport.Csv(shown, Loc.Language));
                return L("Uložené: ", "Uloženo: ", "Saved: ") + path;
            }),
            new("Markdown…", "Markdown|*.md", "UNIVERSUM – úlohy.md", path =>
            {
                File.WriteAllText(path, AgendaExport.Markdown(title, shown));
                return L("Uložené: ", "Uloženo: ", "Saved: ") + path;
            }),
        ];
        return new ReportViewModel(_services, title, [new("list", L("Ako na obrazovke", "Jako na obrazovce", "As on the screen"))], (_, options, fonts) =>
        {
            var layout = new ReportLayout(fonts, options, title, landscape: false);
            AgendaReports.Tasks(layout, title, Groups.Select(g => (g.Title, CalendarPageViewModel.ColorOf(g.Color),
                (IReadOnlyList<ReportTask>)[.. g.Rows.Select(r => new ReportTask(r.Title, r.DueText, r.Text, r.IsDone, r.IsOverdue, r.Item.Task?.Priority ?? 1, r.Tags,
                    [.. (r.Item.Task?.Checklist ?? []).Select(c => (c.Text, c.Done))]))])));
            return layout.Finish();
        }, exports);
    }

    /// <summary>The page was opened: the suggestions are worked out again.</summary>
    public void Shown()
    {
        if (Ready.IsCompleted)
        {
            Ready = SuggestAsync();
        }
    }

    public void Edit(AgendaItem? item) =>
        Editor = new AgendaEditor(_services.Agenda, item, item?.Task?.Due ?? Today, AgendaKind.Task, () => Editor = null) { Voice = _services.Voice };

    internal void Delete(AgendaItem item)
    {
        _services.Agenda.Delete(item.Id);
        Status = L("Úloha je v koši: ", "Úkol je v koši: ", "The task is in the bin: ") + item.Title;
    }

    internal void SetDone(AgendaItem item, bool done)
    {
        if (item.Task is not { } task)
        {
            return;
        }

        task.State = done ? TaskState.Done : TaskState.Open;
        task.Finished = done ? DateTimeOffset.Now : null;
        _services.Agenda.Save(item);
    }

    // the board: to do ↔ in progress ↔ done
    internal void Move(AgendaItem item, int step)
    {
        if (item.Task is not { } task)
        {
            return;
        }

        var state = (task.State, step) switch
        {
            (TaskState.Open, > 0) => TaskState.InProgress,
            (TaskState.InProgress, > 0) => TaskState.Done,
            (TaskState.InProgress, < 0) => TaskState.Open,
            (TaskState.Done, < 0) => TaskState.InProgress,
            _ => task.State,
        };
        task.State = state;
        task.Finished = state == TaskState.Done ? DateTimeOffset.Now : null;
        _services.Agenda.Save(item);
    }

    private void Add()
    {
        var line = QuickText.Trim();
        if (line.Length == 0)
        {
            return;
        }

        var item = AddQuick(_services.Agenda, line);
        QuickText = "";
        OnPropertyChanged(nameof(QuickHint));
        Status = L("Pridané: ", "Přidáno: ", "Added: ") + item.Title + " · " + Due(item.Task?.Due, item.Task?.DueTime);
    }

    /// <summary>A task made of one line of the quick entry and kept (here and on the Dashboard).</summary>
    internal static AgendaItem AddQuick(AgendaStore store, string line)
    {
        var parsed = QuickEntry.Parse(line, Today);
        var item = new AgendaItem
        {
            Title = parsed.Title.Length > 0 ? parsed.Title : line,
            Tags = [.. parsed.Tags],
            Task = new AgendaTask { Due = parsed.Due, DueTime = parsed.Time, Priority = parsed.Priority },
        };
        store.Save(item);
        return item;
    }

    private void EmptyDone()
    {
        foreach (var item in _services.Agenda.Items.Where(i => i.Task?.State is TaskState.Done or TaskState.Cancelled && i.Time is null).ToList())
        {
            _services.Agenda.Delete(item.Id);
        }

        Status = L("Hotové úlohy sú v koši.", "Hotové úkoly jsou v koši.", "The done tasks are in the bin.");
    }

    /// <summary>The list and the board again.</summary>
    public void Build()
    {
        _building = true;
        try
        {
            var today = Today;
            var wanted = Words.Normalize(Filter);
            var tasks = _services.Agenda.Items.Where(i => i.Task is not null).ToList();
            TagChoices = [AllTags, .. tasks.SelectMany(t => t.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
            OnPropertyChanged(nameof(TagChoices));
            var shown = tasks.Where(t => (Tag == AllTags || t.Tags.Contains(Tag, StringComparer.CurrentCultureIgnoreCase)) &&
                                         (wanted.Length == 0 || Words.Normalize(t.Title + " " + t.Text + " " + string.Join(" ", t.Tags)).Contains(wanted, StringComparison.Ordinal)))
                .OrderByDescending(t => t.Task!.Priority).ThenBy(t => t.Task!.Due ?? DateOnly.MaxValue).ThenBy(t => t.Task!.DueTime ?? TimeOnly.MinValue).ThenBy(t => t.Created)
                .ToList();
            TaskRow Row(AgendaItem item) => new(item, this)
            {
                DueText = Due(item.Task!.Due, item.Task.DueTime),
                IsOverdue = item.Task.IsOpen && item.Task.Due is { } due && (due < today || (due == today && item.Task.DueTime is { } at && at < TimeOnly.FromDateTime(DateTime.Now))),
            };

            Groups.Clear();
            var endOfWeek = today.AddDays(7 - (((int)today.DayOfWeek + 6) % 7) - 1);
            void Group(string title, CalendarLayer color, Func<AgendaItem, bool> belongs)
            {
                var rows = shown.Where(belongs).Select(Row).ToList();
                if (rows.Count > 0)
                {
                    Groups.Add(new TaskGroup(title, CalendarColors.Of(color), rows));
                }
            }

            bool Open(AgendaItem i) => i.Task!.IsOpen;
            Group(L("Po termíne", "Po termínu", "Overdue"), CalendarLayer.Holidays, i => Open(i) && i.Task!.Due < today);
            Group(L("Dnes", "Dnes", "Today"), CalendarLayer.Events, i => Open(i) && i.Task!.Due == today);
            Group(L("Zajtra", "Zítra", "Tomorrow"), CalendarLayer.Tasks, i => Open(i) && i.Task!.Due == today.AddDays(1));
            Group(L("Tento týždeň", "Tento týden", "This week"), CalendarLayer.SeriesDomestic, i => Open(i) && i.Task!.Due > today.AddDays(1) && i.Task.Due <= endOfWeek);
            Group(L("Neskôr", "Později", "Later"), CalendarLayer.FilmsWorld, i => Open(i) && i.Task!.Due > endOfWeek && i.Task.Due > today.AddDays(1));
            Group(L("Bez termínu", "Bez termínu", "No due day"), CalendarLayer.Memorial, i => Open(i) && i.Task!.Due is null);
            if (ShowDone)
            {
                Group(L("Hotové", "Hotové", "Done"), CalendarLayer.NameDays, i => !Open(i));
            }

            Fill(ToDo, shown.Where(i => i.Task!.State == TaskState.Open).Select(Row));
            Fill(InProgress, shown.Where(i => i.Task!.State == TaskState.InProgress).Select(Row));
            Fill(Done, shown.Where(i => i.Task!.State is TaskState.Done or TaskState.Cancelled && (ShowDone || i.Task.Finished > DateTimeOffset.Now.AddDays(-7))).Select(Row));
            var open = tasks.Count(t => t.Task!.IsOpen);
            var overdue = tasks.Count(t => t.Task!.IsOpen && t.Task.Due < today);
            Summary = L($"Otvorené: {open} · po termíne: {overdue} · hotové: {tasks.Count - open}", $"Otevřené: {open} · po termínu: {overdue} · hotové: {tasks.Count - open}",
                $"Open: {open} · overdue: {overdue} · done: {tasks.Count - open}");
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(IsEmpty));
            ShowSuggestions();
        }
        finally
        {
            _building = false;
        }
    }

    private static void Fill(ObservableCollection<TaskRow> target, IEnumerable<TaskRow> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }

    // ---------------------------------------------------------------- suggestions

    internal void Take(string key)
    {
        if (_allSuggestions.FirstOrDefault(s => s.Key == key) is not { } suggestion)
        {
            return;
        }

        var item = new AgendaItem
        {
            Title = suggestion.Title,
            Text = suggestion.Detail,
            Task = new AgendaTask { Due = suggestion.Due ?? Today },
            Links = suggestion.Link is { } link ? [link] : [],
        };
        _services.Agenda.Save(item);
        Hide(key);
        Status = L("Prevzaté do úloh: ", "Převzato do úkolů: ", "Taken as a task: ") + item.Title;
    }

    internal void Hide(string key)
    {
        if (!_services.Settings.HiddenSuggestions.Contains(key))
        {
            _services.Settings.HiddenSuggestions.Add(key);
            // the list does not grow for ever: the latest 500 are kept
            if (_services.Settings.HiddenSuggestions.Count > 500)
            {
                _services.Settings.HiddenSuggestions.RemoveRange(0, _services.Settings.HiddenSuggestions.Count - 500);
            }

            _services.Settings.Save();
        }

        ShowSuggestions();
    }

    private void ShowSuggestions()
    {
        var hidden = _services.Settings.HiddenSuggestions.ToHashSet(StringComparer.Ordinal);
        Suggestions.Clear();
        foreach (var suggestion in _allSuggestions.Where(s => !hidden.Contains(s.Key)))
        {
            Suggestions.Add(suggestion);
        }

        OnPropertyChanged(nameof(HasSuggestions));
    }

    // what UNIVERSUM knows now: the input folder, the chosen titles of Premiéry and Novinky, waiting posters, logs
    private async Task SuggestAsync()
    {
        var today = Today;
        var found = new List<TaskSuggestion>();
        foreach (var title in _services.InputWatch.Found)
        {
            var key = $"vstup|{title.Title}|{title.Episode}|{title.Files.Count}";
            found.Add(new TaskSuggestion(key, "", L("Spracovať: ", "Zpracovat: ", "Process: ") + title.Title, title.Detail, today, null,
                L("Spracovať", "Zpracovat", "Process"), () => ProcessFound(title), this));
        }

        try
        {
            var marks = _services.Marks;
            var kept = await _services.SeriesNews.ReadAsync(_services.NewsSources, today, offline: true);
            foreach (var episode in kept.Episodes.Where(e => e.AirDate <= today && e.AirDate >= today.AddDays(-7) && marks.Get(MarkStore.Series(e)).Chosen && !_services.InputWatch.Contains(e)))
            {
                var name = (episode.Caption + " " + episode.Code).Trim();
                found.Add(new TaskSuggestion($"epizoda|{MarkStore.Series(episode)}|{episode.Code}", "", L("Čaká na súbor: ", "Čeká na soubor: ", "Waiting for the file: ") + name,
                    $"{episode.AirDate:d. M. yyyy}" + (episode.Channel.Length > 0 ? " · " + episode.Channel : ""), today, new AgendaLink(LinkKind.Episode, MarkStore.Series(episode) + ":" + episode.Code, name),
                    L("Otvoriť v Novinkách", "Otevřít v Novinkách", "Open in New episodes"), () =>
                    {
                        OpenNews(episode.Region == NewsRegion.Domestic ? CalendarLayer.EpisodesDomestic : CalendarLayer.EpisodesWorld, episode.Caption);
                        return Task.CompletedTask;
                    }, this));
            }

            var archive = await Task.Run(() => _services.PremiereStore.Load());
            foreach (var film in archive.Films.Where(f => f.Premiere >= today && f.Premiere <= today.AddDays(7) && marks.Get(MarkStore.Film(f.TmdbId)).Chosen))
            {
                found.Add(new TaskSuggestion($"premiera|{film.TmdbId}|{film.Premiere:yyyy-MM-dd}", "", L("Premiéra vybraného filmu: ", "Premiéra vybraného filmu: ", "Premiere of a chosen film: ") + film.Caption,
                    $"{film.Premiere:d. M. yyyy}" + (film.PremiereNote.Length > 0 ? " · " + film.PremiereNote : ""), film.Premiere, new AgendaLink(LinkKind.Title, MarkStore.Film(film.TmdbId), film.Caption),
                    L("Otvoriť v Premiérach", "Otevřít v Premiérách", "Open in Premieres"), () =>
                    {
                        OpenNews(film.Region == NewsRegion.Domestic ? CalendarLayer.FilmsDomestic : CalendarLayer.FilmsWorld, film.Caption);
                        return Task.CompletedTask;
                    }, this));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // what Premiéry and Novinky keep cannot be read now – the other suggestions stay
        }

        foreach (var poster in _services.WaitingPosters.All)
        {
            found.Add(new TaskSuggestion($"plagat|{poster.Kind}|{poster.TmdbId}", "", L("Plagát čaká na titul: ", "Plakát čeká na titul: ", "A poster waits for: ") + poster.FolderName,
                L("Pridá sa, keď UNIVERSUM titul spracuje (čaká od ", "Přidá se, když UNIVERSUM titul zpracuje (čeká od ", "It is added when UNIVERSUM processes the title (waiting since ") + $"{poster.Since:d. M. yyyy})",
                null, null, null, null, this));
        }

        foreach (var log in _services.ProcessingLogs.All().Where(l => l.HasProblems && l.FinishedUtc > DateTime.UtcNow.AddDays(-7)))
        {
            var id = log.Id;
            found.Add(new TaskSuggestion($"zaznam|{id}", "", L("Pozrieť problémy spracovania: ", "Zkontrolovat problémy zpracování: ", "Check the problems of: ") + log.Kind,
                log.FinishedUtc.ToLocalTime().ToString("d. M. yyyy HH:mm", CultureInfo.InvariantCulture) + " · " + log.Summary, today, new AgendaLink(LinkKind.Log, id, log.Kind),
                L("Pozrieť záznam", "Zobrazit záznam", "Open the log"), () =>
                {
                    OpenLog(id);
                    return Task.CompletedTask;
                }, this));
        }

        _allSuggestions.Clear();
        _allSuggestions.AddRange(found);
        ShowSuggestions();
    }

    // ---------------------------------------------------------------- words

    internal static string Due(DateOnly? due, TimeOnly? time)
    {
        if (due is not { } day)
        {
            return L("bez termínu", "bez termínu", "no due day");
        }

        var today = Today;
        var at = time is { } t ? " " + t.ToString("HH:mm", CultureInfo.InvariantCulture) : "";
        var days = day.DayNumber - today.DayNumber;
        return days switch
        {
            0 => L("dnes", "dnes", "today") + at,
            1 => L("zajtra", "zítra", "tomorrow") + at,
            -1 => L("včera", "včera", "yesterday") + at,
            > 1 and < 7 => day.ToString("dddd", CalendarTexts.Culture(Loc.Language)) + " " + day.ToString("d. M.", CultureInfo.InvariantCulture) + at,
            _ => day.ToString("d. M. yyyy", CultureInfo.InvariantCulture) + at,
        };
    }

    private static string L(string sk, string cs, string en) => Loc.Language switch { "cs" => cs, "en" => en, _ => sk };
}
