using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Agenda;
using Universum.Core.Calendar;

namespace Universum.App.Pages;

/// <summary>A line of the Dashboard's calendar: the day, the hour, the colour of its layer and what it is; a click opens it.</summary>
public sealed class DashboardDayRow
{
    public required string Day { get; init; }

    public string Time { get; init; } = "";

    public required string Title { get; init; }

    public string Detail { get; init; } = "";

    public required Brush Color { get; init; }

    public bool IsToday { get; init; }

    /// <summary>The first line of its day (the day's label is shown only there).</summary>
    public bool FirstOfDay { get; init; }

    public string ToolTip => Detail.Length > 0 ? Title + "\n" + Detail : Title;

    public required RelayCommand OpenCommand { get; init; }
}

/// <summary>An open task on the Dashboard: ticked off here, opened in Plánované úlohy.</summary>
public sealed class DashboardTaskRow
{
    private readonly AgendaItem _item;
    private readonly Action<AgendaItem> _done;

    public DashboardTaskRow(AgendaItem item, string due, bool late, Action<AgendaItem> done, Action<AgendaItem> open)
    {
        _item = item;
        _done = done;
        Title = item.Title;
        Due = due;
        Late = late;
        High = item.Task?.Priority == 2;
        Tags = string.Join(" ", item.Tags.Select(t => "#" + t));
        OpenCommand = new RelayCommand(() => open(item));
    }

    public string Title { get; }

    public string Due { get; }

    /// <summary>Its day is over.</summary>
    public bool Late { get; }

    public bool High { get; }

    public string Tags { get; }

    /// <summary>Ticked: done (it leaves the list).</summary>
    public bool IsDone
    {
        get => false;
        set
        {
            if (value)
            {
                _done(_item);
            }
        }
    }

    public RelayCommand OpenCommand { get; }
}

/// <summary>A pinned note on the Dashboard.</summary>
public sealed class DashboardNoteRow
{
    public DashboardNoteRow(AgendaItem item, Brush color, Action<AgendaItem> open)
    {
        Title = item.Title.Length > 0 ? item.Title : FirstLine(item.Text);
        Preview = item.Title.Length > 0 ? FirstLine(item.Text) : "";
        Color = color;
        HasRecording = item.Note?.Recording is not null;
        OpenCommand = new RelayCommand(() => open(item));
    }

    public string Title { get; }

    /// <summary>The beginning of its text.</summary>
    public string Preview { get; }

    public bool HasPreview => Preview.Length > 0;

    public Brush Color { get; }

    public bool HasRecording { get; }

    public RelayCommand OpenCommand { get; }

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 110 ? line[..110].TrimEnd() + "…" : line;
    }
}

/// <summary>
/// POZNÁMKY on the Dashboard (owner, 31. 10. 2026 – stage P3b): the calendar of the coming week as the Kalendár shows it
/// (the user's events and notes of a day, the holidays, the batches of MEGA planned for an hour), the open tasks of today,
/// those already late and those of the coming days, the pinned notes, and a task written in one line right here.
/// </summary>
public sealed class AgendaOverview : ObservableObject
{
    /// <summary>The days of the Dashboard's calendar: today and six more.</summary>
    public const int Days = 7;

    /// <summary>At most so many tasks and notes are listed (the rest is in their pages).</summary>
    public const int MostTasks = 8;

    public const int MostNotes = 5;

    // the premieres and episodes have their own panels on the Dashboard, the tasks their own list here
    private static readonly IReadOnlyCollection<CalendarLayer> CalendarLayers =
        [CalendarLayer.Events, CalendarLayer.Notes, CalendarLayer.Holidays, CalendarLayer.Mega];

    private readonly AppServices _services;
    private readonly Shell.MainViewModel _main;
    private IReadOnlyList<DashboardDayRow> _calendar = [];
    private IReadOnlyList<DashboardTaskRow> _tasks = [];
    private IReadOnlyList<DashboardNoteRow> _notes = [];
    private string _quick = "";
    private string _status = "";
    private int _moreTasks;
    private bool _stale = true;
    private DateOnly _shownOn;

    public AgendaOverview(AppServices services, Shell.MainViewModel main)
    {
        _services = services;
        _main = main;
        OpenCalendarCommand = new RelayCommand(() =>
        {
            _main.Open(_main.Calendar);
            _main.Calendar.Show(Today);
        });
        OpenTasksCommand = new RelayCommand(() => _main.Open(_main.AgendaTasks));
        OpenNotesCommand = new RelayCommand(() => _main.Open(_main.AgendaNotes));
        NewEventCommand = new RelayCommand(() =>
        {
            _main.Open(_main.Calendar);
            _main.Calendar.Show(Today);
            _main.Calendar.Edit(null, AgendaKind.Event);
        });
        AddCommand = new RelayCommand(Add);
        // a change is shown when the Dashboard is seen (a record kept elsewhere: at once if it is open)
        services.Agenda.Changed += () => Jobs.JobRow.OnUi(() =>
        {
            _stale = true;
            if (_main.SelectedTool?.Page == _main.Dashboard)
            {
                Refresh();
            }
        });
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public IReadOnlyList<DashboardDayRow> Calendar { get => _calendar; private set => SetProperty(ref _calendar, value); }

    public bool NoCalendar => Calendar.Count == 0;

    public IReadOnlyList<DashboardTaskRow> Tasks { get => _tasks; private set => SetProperty(ref _tasks, value); }

    public bool NoTasks => Tasks.Count == 0;

    /// <summary>"a ďalšie 3 v Plánovaných úlohách" when the list is longer.</summary>
    public string MoreTasks => _moreTasks == 0 ? "" : L($"a ďalšie ({_moreTasks}) v Plánovaných úlohách", $"a další ({_moreTasks}) v Plánovaných úkolech", $"and {_moreTasks} more in Planned tasks");

    public bool HasMoreTasks => _moreTasks > 0;

    public IReadOnlyList<DashboardNoteRow> Notes { get => _notes; private set => SetProperty(ref _notes, value); }

    public bool HasNotes => Notes.Count > 0;

    /// <summary>A task in one line: "zajtra o 20:00 spracovať film #kino !".</summary>
    public string QuickText
    {
        get => _quick;
        set
        {
            if (SetProperty(ref _quick, value))
            {
                OnPropertyChanged(nameof(QuickHint));
            }
        }
    }

    public string QuickHint => QuickText.Trim().Length == 0
        ? L("Úloha jedným riadkom, napr. „zajtra 18:00 skontrolovať titulky #film !“ – Enter ju pridá.", "Úkol jedním řádkem, např. „zítra 18:00 zkontrolovat titulky #film !“ – Enter ho přidá.",
            "A task in one line, e.g. “tomorrow 18:00 check the subtitles #film !” – Enter adds it.")
        : TasksPageViewModel.HintOf(QuickText);

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public RelayCommand OpenCalendarCommand { get; }

    public RelayCommand OpenTasksCommand { get; }

    public RelayCommand OpenNotesCommand { get; }

    public RelayCommand NewEventCommand { get; }

    public RelayCommand AddCommand { get; }

    /// <summary>The Dashboard is shown: what changed since is listed again (a new day too).</summary>
    public void Shown()
    {
        if (_stale || _shownOn != Today)
        {
            Refresh();
        }
    }

    /// <summary>The calendar, the tasks and the notes as they are now.</summary>
    public void Refresh()
    {
        _stale = false;
        var today = _shownOn = Today;
        var culture = CalendarTexts.Culture(Loc.Language);

        // the calendar: today and the six days after it
        var rows = new List<DashboardDayRow>();
        DateOnly? last = null;
        foreach (var (day, entry) in _main.Calendar.EntriesOf(today, today.AddDays(Days - 1), CalendarLayers))
        {
            rows.Add(new DashboardDayRow
            {
                Day = DayLabel(day, today, culture),
                Time = entry.TimeText,
                Title = entry.Title,
                Detail = entry.Detail,
                Color = entry.Color,
                IsToday = day == today,
                FirstOfDay = day != last,
                OpenCommand = new RelayCommand(() => Open(day, entry)),
            });
            last = day;
        }

        Calendar = rows;
        OnPropertyChanged(nameof(NoCalendar));

        // the open tasks: late ones, today's, then those of the coming days – the most important first on a day
        var open = _services.Agenda.Items
            .Where(i => i.Task is { IsOpen: true } && When(i) is { } due && due <= today.AddDays(Days - 1))
            .OrderBy(i => When(i))
            .ThenBy(i => i.Task!.DueTime ?? i.Time?.StartTime ?? TimeOnly.MaxValue)
            .ThenByDescending(i => i.Task!.Priority)
            .ToList();
        _moreTasks = Math.Max(0, open.Count - MostTasks);
        Tasks = [.. open.Take(MostTasks).Select(i => new DashboardTaskRow(i, TasksPageViewModel.Due(When(i), i.Task!.DueTime ?? i.Time?.StartTime), When(i) < today, Done, OpenTask))];
        OnPropertyChanged(nameof(NoTasks));
        OnPropertyChanged(nameof(MoreTasks));
        OnPropertyChanged(nameof(HasMoreTasks));

        // the pinned notes, the newest first
        Notes = [.. _services.Agenda.Items.Where(i => i.Note is { Pinned: true }).OrderByDescending(i => i.Modified).Take(MostNotes)
            .Select(i => new DashboardNoteRow(i, CalendarColors.Of(CalendarLayer.Notes, i.Color), OpenNote))];
        OnPropertyChanged(nameof(HasNotes));
    }

    // the day of a task: its due day, or the day of its time
    private static DateOnly? When(AgendaItem item) => item.Task?.Due ?? item.Time?.Start;

    private void Add()
    {
        var line = QuickText.Trim();
        if (line.Length == 0)
        {
            return;
        }

        var item = TasksPageViewModel.AddQuick(_services.Agenda, line);
        QuickText = "";
        Status = L("Pridané: ", "Přidáno: ", "Added: ") + item.Title + " · " + TasksPageViewModel.Due(item.Task?.Due, item.Task?.DueTime);
        Refresh();
    }

    private void Done(AgendaItem item)
    {
        _main.AgendaTasks.SetDone(item, true);
        Status = L("Hotovo: ", "Hotovo: ", "Done: ") + item.Title;
        Refresh();
    }

    private void OpenTask(AgendaItem item)
    {
        _main.Open(_main.AgendaTasks);
        _main.AgendaTasks.Edit(item);
    }

    private void OpenNote(AgendaItem item)
    {
        _main.Open(_main.AgendaNotes);
        _main.AgendaNotes.Edit(item);
    }

    // a batch of MEGA opens in MEGA spracovanie; the rest in the calendar on its day (a record in its editor)
    private void Open(DateOnly day, CalendarEntry entry)
    {
        if (entry.Layer == CalendarLayer.Mega)
        {
            entry.OpenCommand?.Execute(null);
            return;
        }

        _main.Open(_main.Calendar);
        _main.Calendar.Show(day);
        if (entry.Record is not null)
        {
            entry.OpenCommand?.Execute(null);
        }
    }

    private static string DayLabel(DateOnly day, DateOnly today, CultureInfo culture) => (day.DayNumber - today.DayNumber) switch
    {
        0 => L("Dnes", "Dnes", "Today"),
        1 => L("Zajtra", "Zítra", "Tomorrow"),
        _ => Capitalized(day.ToString("ddd", culture)) + " " + day.ToString("d. M.", CultureInfo.InvariantCulture),
    };

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string L(string sk, string cs, string en) => Loc.Language switch { "cs" => cs, "en" => en, _ => sk };
}
