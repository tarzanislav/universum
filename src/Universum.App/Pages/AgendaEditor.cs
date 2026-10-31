using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Agenda;

namespace Universum.App.Pages;

/// <summary>What a new record of the agenda is.</summary>
public enum AgendaKind
{
    Event,
    Task,
    Note,
}

/// <summary>
/// The editor of a record of the agenda (owner, 31. 10. 2026, point e): one form for an event, a task and a note – the
/// switch on top changes what it is at any time and nothing typed is lost. An event has its days, hours, repeat and
/// reminder; a task its due day and priority; a note its text, pinned, and the day it belongs to.
/// </summary>
public sealed class AgendaEditor : ObservableObject
{
    private readonly AgendaStore _store;
    private readonly AgendaItem? _item;
    private readonly Action _closed;
    private AgendaKind _kind;
    private string _title;
    private string _text;
    private string _tags;
    private DateTime? _start;
    private DateTime? _end;
    private bool _allDay;
    private string _startTime;
    private string _endTime;
    private VideoChoice<RepeatEvery?> _repeat;
    private string _interval;
    private DateTime? _until;
    private VideoChoice<int?> _reminder;
    private bool _alsoTask;
    private DateTime? _due;
    private string _dueTime;
    private VideoChoice<int> _priority;
    private bool _done;
    private bool _pinned;
    private bool _onDay;
    private VideoChoice<string?> _color;
    private string _problem = "";

    public AgendaEditor(AgendaStore store, AgendaItem? item, DateOnly day, AgendaKind kind, Action closed)
    {
        _store = store;
        _item = item;
        _closed = closed;
        _kind = item is null ? kind : item.Time is not null ? AgendaKind.Event : item.Task is not null ? AgendaKind.Task : AgendaKind.Note;
        _title = item?.Title ?? "";
        _text = item?.Text ?? "";
        _tags = item is null ? "" : string.Join(" ", item.Tags.Select(t => "#" + t));
        var time = item?.Time;
        _start = (time?.Start ?? item?.Task?.Due ?? day).ToDateTime(TimeOnly.MinValue);
        _end = time?.End?.ToDateTime(TimeOnly.MinValue);
        _allDay = time is null || time.StartTime is null;
        _startTime = Hours(time?.StartTime) is { Length: > 0 } s ? s : "18:00";
        _endTime = Hours(time?.EndTime);
        _repeat = Repeats.First(r => r.Value == time?.Repeat?.Every);
        _interval = (time?.Repeat?.Interval ?? 1).ToString(CultureInfo.InvariantCulture);
        _until = time?.Repeat?.Until?.ToDateTime(TimeOnly.MinValue);
        _reminder = Reminders.FirstOrDefault(r => r.Value == time?.ReminderMinutes) ?? Reminders[0];
        _alsoTask = item?.Time is not null && item.Task is not null;
        _due = (item?.Task?.Due ?? day).ToDateTime(TimeOnly.MinValue);
        _dueTime = Hours(item?.Task?.DueTime);
        _priority = Priorities.First(p => p.Value == (item?.Task?.Priority ?? 1));
        _done = item?.Task?.State == TaskState.Done;
        _pinned = item?.Note?.Pinned ?? false;
        _onDay = item is null || item.Links.Any(l => l.Kind == LinkKind.Day);
        _color = Colors.FirstOrDefault(c => c.Value == item?.Color) ?? Colors[0];
        Day = day;
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(closed);
        DeleteCommand = new RelayCommand(Delete, () => _item is not null);
    }

    public static IReadOnlyList<VideoChoice<RepeatEvery?>> Repeats { get; } =
        [new(null, "neopakuje sa"), new(RepeatEvery.Day, "každý deň"), new(RepeatEvery.Week, "každý týždeň"), new(RepeatEvery.Month, "každý mesiac"), new(RepeatEvery.Year, "každý rok")];

    public static IReadOnlyList<VideoChoice<int?>> Reminders { get; } =
        [new(null, "bez pripomienky"), new(0, "v čase začiatku"), new(15, "15 minút vopred"), new(60, "1 hodinu vopred"), new(1440, "1 deň vopred"), new(10080, "1 týždeň vopred")];

    public static IReadOnlyList<VideoChoice<int>> Priorities { get; } = [new(0, "nízka"), new(1, "normálna"), new(2, "vysoká")];

    /// <summary>Very light pastel colours of a record (owner: "jemné, príjemné").</summary>
    public static IReadOnlyList<VideoChoice<string?>> Colors { get; } =
        [new(null, "podľa druhu"), new("#E88AA6", "ružová"), new("#F2A65A", "oranžová"), new("#E6C84F", "žltá"), new("#7CC49A", "zelená"), new("#6FB7D9", "modrá"), new("#A58BE0", "fialová"), new("#9AA3AD", "sivá")];

    /// <summary>The day the editor was opened for.</summary>
    public DateOnly Day { get; }

    public string Heading => (_item is null ? "Nový záznam" : "Upraviť záznam") + " – " + Day.ToString("d. M. yyyy", CultureInfo.InvariantCulture);

    public bool IsEvent { get => _kind == AgendaKind.Event; set => SetKind(value, AgendaKind.Event); }

    public bool IsTask { get => _kind == AgendaKind.Task; set => SetKind(value, AgendaKind.Task); }

    public bool IsNote { get => _kind == AgendaKind.Note; set => SetKind(value, AgendaKind.Note); }

    public string Title { get => _title; set => SetProperty(ref _title, value); }

    public string Text { get => _text; set => SetProperty(ref _text, value); }

    /// <summary>"#kino #seriál".</summary>
    public string TagsText { get => _tags; set => SetProperty(ref _tags, value); }

    public DateTime? StartDate { get => _start; set => SetProperty(ref _start, value); }

    public DateTime? EndDate { get => _end; set => SetProperty(ref _end, value); }

    public bool AllDay
    {
        get => _allDay;
        set
        {
            if (SetProperty(ref _allDay, value))
            {
                OnPropertyChanged(nameof(HasHours));
            }
        }
    }

    public bool HasHours => !AllDay;

    public string StartTimeText { get => _startTime; set => SetProperty(ref _startTime, value); }

    public string EndTimeText { get => _endTime; set => SetProperty(ref _endTime, value); }

    public VideoChoice<RepeatEvery?> Repeat
    {
        get => _repeat;
        set
        {
            if (SetProperty(ref _repeat, value))
            {
                OnPropertyChanged(nameof(HasRepeat));
            }
        }
    }

    public bool HasRepeat => Repeat.Value is not null;

    public string IntervalText { get => _interval; set => SetProperty(ref _interval, value); }

    public DateTime? UntilDate { get => _until; set => SetProperty(ref _until, value); }

    public VideoChoice<int?> Reminder { get => _reminder; set => SetProperty(ref _reminder, value); }

    /// <summary>An event that is also a task (it is then in Plánované úlohy too).</summary>
    public bool AlsoTask { get => _alsoTask; set => SetProperty(ref _alsoTask, value); }

    public DateTime? DueDate { get => _due; set => SetProperty(ref _due, value); }

    public string DueTimeText { get => _dueTime; set => SetProperty(ref _dueTime, value); }

    public VideoChoice<int> Priority { get => _priority; set => SetProperty(ref _priority, value); }

    public bool Done { get => _done; set => SetProperty(ref _done, value); }

    public bool Pinned { get => _pinned; set => SetProperty(ref _pinned, value); }

    /// <summary>A note shown in the calendar on its day.</summary>
    public bool OnDay { get => _onDay; set => SetProperty(ref _onDay, value); }

    public VideoChoice<string?> Color { get => _color; set => SetProperty(ref _color, value); }

    public string Problem { get => _problem; private set => SetProperty(ref _problem, value); }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand DeleteCommand { get; }

    /// <summary>The record saved last (the self-test looks at it).</summary>
    public AgendaItem? Saved { get; private set; }

    private void SetKind(bool chosen, AgendaKind kind)
    {
        if (chosen && _kind != kind)
        {
            _kind = kind;
            OnPropertyChanged(nameof(IsEvent));
            OnPropertyChanged(nameof(IsTask));
            OnPropertyChanged(nameof(IsNote));
        }
    }

    private void Save()
    {
        if (Build() is not { } item)
        {
            return;
        }

        _store.Save(item);
        Saved = item;
        _closed();
    }

    private void Delete()
    {
        if (_item is not null)
        {
            _store.Delete(_item.Id);
            _closed();
        }
    }

    // the record from the form; null with the problem told
    internal AgendaItem? Build()
    {
        if (Title.Trim().Length == 0 && !(IsNote && Text.Trim().Length > 0))
        {
            Problem = "Napíšte názov.";
            return null;
        }

        var item = _item ?? new AgendaItem();
        item.Title = Title.Trim();
        item.Text = Text.Trim();
        item.Color = Color.Value;
        item.Tags = [.. TagsText.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.TrimStart('#')).Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase)];
        item.Links.RemoveAll(l => l.Kind == LinkKind.Day);
        switch (_kind)
        {
            case AgendaKind.Event:
                if (EventTime() is not { } time)
                {
                    return null;
                }

                item.Time = time;
                item.Task = AlsoTask ? MakeTask(item.Task, time.Start, time.StartTime) : null;
                item.Note = null;
                break;
            case AgendaKind.Task:
                if (DueDate is null && DueTimeText.Trim().Length > 0)
                {
                    Problem = "Čas úlohy potrebuje aj deň.";
                    return null;
                }

                if (!TryHours(DueTimeText, out var dueTime))
                {
                    Problem = "Čas úlohy napíšte ako 20:00.";
                    return null;
                }

                item.Time = null;
                item.Task = MakeTask(item.Task, DueDate is { } due ? DateOnly.FromDateTime(due) : null, dueTime);
                item.Note = null;
                break;
            default:
                item.Time = null;
                item.Task = null;
                item.Note = new AgendaNote { Pinned = Pinned, Recording = item.Note?.Recording };
                if (OnDay)
                {
                    item.Links.Add(new AgendaLink(LinkKind.Day, Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Day.ToString("d. M. yyyy", CultureInfo.InvariantCulture)));
                }

                break;
        }

        Problem = "";
        return item;
    }

    private AgendaTask MakeTask(AgendaTask? old, DateOnly? due, TimeOnly? time)
    {
        var state = Done ? TaskState.Done : old?.State is TaskState.InProgress ? TaskState.InProgress : TaskState.Open;
        return new AgendaTask
        {
            Due = due,
            DueTime = time,
            Priority = Priority.Value,
            State = state,
            Finished = state == TaskState.Done ? old?.Finished ?? DateTimeOffset.Now : null,
            Checklist = old?.Checklist ?? [],
        };
    }

    private AgendaTime? EventTime()
    {
        if (StartDate is not { } startDate)
        {
            Problem = "Zvoľte deň začiatku.";
            return null;
        }

        var start = DateOnly.FromDateTime(startDate);
        var end = EndDate is { } endDate ? DateOnly.FromDateTime(endDate) : (DateOnly?)null;
        if (end < start)
        {
            Problem = "Koniec je pred začiatkom.";
            return null;
        }

        TimeOnly? startTime = null, endTime = null;
        if (!AllDay)
        {
            if (!TryHours(StartTimeText, out startTime) || startTime is null || !TryHours(EndTimeText, out endTime))
            {
                Problem = "Čas napíšte ako 20:00 (alebo zaškrtnite Celý deň).";
                return null;
            }

            if (endTime is not null && (end ?? start) == start && endTime < startTime)
            {
                Problem = "Koniec je pred začiatkom.";
                return null;
            }
        }

        Recurrence? repeat = null;
        if (Repeat.Value is { } every)
        {
            if (!int.TryParse(IntervalText, NumberStyles.None, CultureInfo.InvariantCulture, out var interval) || interval is < 1 or > 999)
            {
                Problem = "Opakovanie: koľko (1 – 999).";
                return null;
            }

            repeat = new Recurrence { Every = every, Interval = interval, Until = UntilDate is { } until ? DateOnly.FromDateTime(until) : null };
        }

        return new AgendaTime
        {
            Start = start,
            End = end > start ? end : null,
            StartTime = startTime,
            EndTime = endTime,
            Repeat = repeat,
            ReminderMinutes = Reminder.Value,
            Skipped = _item?.Time?.Skipped ?? [],
        };
    }

    private static string Hours(TimeOnly? time) => time?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";

    // "20:00", "8:30", "2030" or empty
    internal static bool TryHours(string text, out TimeOnly? time)
    {
        time = null;
        var t = text.Trim().Replace('.', ':');
        if (t.Length == 0)
        {
            return true;
        }

        if (t.Length == 4 && !t.Contains(':'))
        {
            t = t[..2] + ":" + t[2..];
        }

        if (TimeOnly.TryParseExact(t, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            time = parsed;
            return true;
        }

        return false;
    }
}
