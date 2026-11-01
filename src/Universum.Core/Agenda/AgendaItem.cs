using System.Text.Json.Serialization;

namespace Universum.Core.Agenda;

/// <summary>How often a timed record repeats.</summary>
public enum RepeatEvery
{
    Day,
    Week,
    Month,
    Year,
}

/// <summary>
/// The repeating of a timed record: every <see cref="Interval"/> days, weeks (on the chosen weekdays), months (the same
/// day; the 31st is the last day of a shorter month) or years, until a day and/or a number of times.
/// </summary>
public sealed record Recurrence
{
    public RepeatEvery Every { get; init; }

    public int Interval { get; init; } = 1;

    /// <summary>The weekdays of a weekly repeat (none: the weekday of the start).</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    /// <summary>The last day it can happen on (included).</summary>
    public DateOnly? Until { get; init; }

    /// <summary>How many times it happens at most (skipped days counted too).</summary>
    public int? Count { get; init; }
}

/// <summary>The time of a record: a day or days, with or without hours; repeating; with a reminder.</summary>
public sealed class AgendaTime
{
    public DateOnly Start { get; set; }

    /// <summary>The hour of the start; null: the whole day.</summary>
    public TimeOnly? StartTime { get; set; }

    /// <summary>The last day (several days); null: one day.</summary>
    public DateOnly? End { get; set; }

    public TimeOnly? EndTime { get; set; }

    public Recurrence? Repeat { get; set; }

    /// <summary>Minutes before the start the reminder comes; null: none.</summary>
    public int? ReminderMinutes { get; set; }

    /// <summary>Days of a repeat that were cancelled.</summary>
    public List<DateOnly> Skipped { get; set; } = [];

    /// <summary>Days the record lasts (1 for one day).</summary>
    [JsonIgnore]
    public int Length => End is { } end && end > Start ? end.DayNumber - Start.DayNumber + 1 : 1;
}

/// <summary>The state of a task.</summary>
public enum TaskState
{
    Open,
    InProgress,
    Done,
    Cancelled,
}

/// <summary>A line of a task's checklist.</summary>
public sealed class CheckItem
{
    public string Text { get; set; } = "";

    public bool Done { get; set; }
}

/// <summary>The task side of a record: what is to be done, by when, how important, its checklist.</summary>
public sealed class AgendaTask
{
    public DateOnly? Due { get; set; }

    public TimeOnly? DueTime { get; set; }

    public TaskState State { get; set; }

    /// <summary>When it was done or cancelled.</summary>
    public DateTimeOffset? Finished { get; set; }

    /// <summary>0 low, 1 normal, 2 high.</summary>
    public int Priority { get; set; } = 1;

    public List<CheckItem> Checklist { get; set; } = [];

    [JsonIgnore]
    public bool IsOpen => State is TaskState.Open or TaskState.InProgress;
}

/// <summary>The note side of a record.</summary>
public sealed class AgendaNote
{
    public bool Pinned { get; set; }

    /// <summary>The voice recording kept with the note (a file name in the agenda's folder of recordings).</summary>
    public string? Recording { get; set; }
}

/// <summary>What UNIVERSUM can do by itself at the time of a record (owner, 31. 10. 2026 – tasks for UNIVERSUM).</summary>
public enum AgendaActionKind
{
    /// <summary>The folder of Hromadné spracovanie analysed (as the button Analyzovať does).</summary>
    AnalyzeInput,

    /// <summary>Premiéry and Novinky – Seriály read again from their sources.</summary>
    RefreshNews,

    /// <summary>The lists of HISTRA synchronised.</summary>
    SyncHistra,

    /// <summary>A waiting batch of MEGA spracovanie started (added to the job queue).</summary>
    StartMega,
}

/// <summary>
/// The action side of a timed record: UNIVERSUM does it by itself at each start of the record while it runs (a time
/// missed while it was not running is not caught up, as with MEGA). Nothing irreversible is ever started this way.
/// </summary>
public sealed class AgendaAction
{
    public AgendaActionKind Kind { get; set; }

    /// <summary>The batch of MEGA spracovanie to start (<see cref="AgendaActionKind.StartMega"/>).</summary>
    public Guid? Batch { get; set; }

    /// <summary>When it was done the last time.</summary>
    public DateTimeOffset? LastRun { get; set; }

    /// <summary>How it ended the last time, in words.</summary>
    public string LastOutcome { get; set; } = "";
}

/// <summary>What a record can be linked to.</summary>
public enum LinkKind
{
    /// <summary>A film or series ("movie:603", "tv:1399" or the mark key of Premiéry and Novinky).</summary>
    Title,

    /// <summary>An episode ("tv:1399:S01E02").</summary>
    Episode,

    /// <summary>A day ("2026-10-31").</summary>
    Day,

    /// <summary>Another record (its ID).</summary>
    Record,

    /// <summary>A processing log (its ID).</summary>
    Log,

    /// <summary>A MEGA batch (its ID).</summary>
    Mega,

    /// <summary>A file or folder.</summary>
    Path,

    /// <summary>A web page.</summary>
    Web,
}

/// <summary>A link of a record: what to and how it is shown.</summary>
public sealed record AgendaLink(LinkKind Kind, string Target, string Caption);

/// <summary>
/// A record of the agenda (owner, 31. 10. 2026 – the group POZNÁMKY): one store for Kalendár, Plánované úlohy and
/// Poznámky. A record has a common core (title, text, colour, tags, links) and any of three sides – a time (an event of
/// the calendar), a task, a note – so a task with a due day is in the calendar by itself and nothing is copied.
/// </summary>
public sealed class AgendaItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Title { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>A colour of the record ("#RRGGBB"); null: by its kind.</summary>
    public string? Color { get; set; }

    public List<string> Tags { get; set; } = [];

    public List<AgendaLink> Links { get; set; } = [];

    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;

    /// <summary>When it went to the bin; null: alive.</summary>
    public DateTimeOffset? Deleted { get; set; }

    public AgendaTime? Time { get; set; }

    public AgendaTask? Task { get; set; }

    public AgendaNote? Note { get; set; }

    /// <summary>What UNIVERSUM does by itself at the record's time (only with a time of day).</summary>
    public AgendaAction? Action { get; set; }

    /// <summary>The UID of a record read from an iCalendar file (so the same file read again adds nothing twice).</summary>
    public string? Uid { get; set; }
}
