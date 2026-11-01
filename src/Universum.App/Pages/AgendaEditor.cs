using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Agenda;

namespace Universum.App.Pages;

/// <summary>A line of a task's checklist in the editor.</summary>
public sealed class CheckRow : ObservableObject
{
    private string _text;
    private bool _done;

    public CheckRow(string text, bool done, Action<CheckRow> remove)
    {
        _text = text;
        _done = done;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Text { get => _text; set => SetProperty(ref _text, value); }

    public bool Done { get => _done; set => SetProperty(ref _done, value); }

    public RelayCommand RemoveCommand { get; }
}

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
    private string _newCheck = "";
    private string? _recording;
    private bool _recordingNow;
    private string _voiceStatus = "";
    private VideoChoice<string> _voiceLanguage;
    private VideoChoice<AgendaActionKind?> _action;
    private VideoChoice<Guid?>? _megaBatch;

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
        _action = Actions.First(a => a.Value == item?.Action?.Kind);
        Day = day;
        _recording = item?.Note?.Recording;
        _voiceLanguage = VoiceLanguages.FirstOrDefault(l => l.Value == Localization.Loc.Language) ?? VoiceLanguages[0];
        RecordCommand = new AsyncRelayCommand(RecordAsync);
        PlayCommand = new RelayCommand(() =>
        {
            if (RecordingPath is { } path && System.IO.File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
        }, () => RecordingPath is not null);
        foreach (var line in item?.Task?.Checklist ?? [])
        {
            Checklist.Add(new CheckRow(line.Text, line.Done, r => Checklist.Remove(r)));
        }

        AddCheckCommand = new RelayCommand(() =>
        {
            if (NewCheckText.Trim().Length > 0)
            {
                Checklist.Add(new CheckRow(NewCheckText.Trim(), false, r => Checklist.Remove(r)));
                NewCheckText = "";
            }
        });
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(closed);
        DeleteCommand = new RelayCommand(Delete, () => _item is not null);
    }

    public static IReadOnlyList<VideoChoice<RepeatEvery?>> Repeats { get; } =
        [new(null, Loc.T("neopakuje sa")), new(RepeatEvery.Day, Loc.T("každý deň")), new(RepeatEvery.Week, Loc.T("každý týždeň")), new(RepeatEvery.Month, Loc.T("každý mesiac")), new(RepeatEvery.Year, Loc.T("každý rok"))];

    public static IReadOnlyList<VideoChoice<int?>> Reminders { get; } =
        [new(null, Loc.T("bez pripomienky")), new(0, Loc.T("v čase začiatku")), new(15, Loc.T("15 minút vopred")), new(60, Loc.T("1 hodinu vopred")), new(1440, Loc.T("1 deň vopred")), new(10080, Loc.T("1 týždeň vopred"))];

    public static IReadOnlyList<VideoChoice<int>> Priorities { get; } = [new(0, Loc.T("nízka")), new(1, Loc.T("normálna")), new(2, Loc.T("vysoká"))];

    /// <summary>Very light pastel colours of a record (owner: "jemné, príjemné").</summary>
    public static IReadOnlyList<VideoChoice<string?>> Colors { get; } =
        [new(null, Loc.T("podľa druhu")), new("#E88AA6", Loc.T("ružová")), new("#F2A65A", Loc.T("oranžová")), new("#E6C84F", Loc.T("žltá")), new("#7CC49A", Loc.T("zelená")), new("#6FB7D9", Loc.T("modrá")), new("#A58BE0", Loc.T("fialová")), new("#9AA3AD", Loc.T("sivá"))];

    /// <summary>What UNIVERSUM can do by itself at the time of an event (owner, 31. 10. 2026 – tasks for UNIVERSUM).</summary>
    public static IReadOnlyList<VideoChoice<AgendaActionKind?>> Actions { get; } =
    [
        new(null, Loc.T("nič – len udalosť")),
        new(AgendaActionKind.RefreshNews, ActionText(AgendaActionKind.RefreshNews)),
        new(AgendaActionKind.SyncHistra, ActionText(AgendaActionKind.SyncHistra)),
        new(AgendaActionKind.AnalyzeInput, ActionText(AgendaActionKind.AnalyzeInput)),
        new(AgendaActionKind.StartMega, ActionText(AgendaActionKind.StartMega)),
    ];

    /// <summary>"obnoviť Premiéry a Novinky" …</summary>
    public static string ActionText(AgendaActionKind kind) => kind switch
    {
        AgendaActionKind.RefreshNews => Localization.Loc.Language switch { "cs" => "obnovit Premiéry a Novinky", "en" => "refresh Premieres and News", _ => "obnoviť Premiéry a Novinky" },
        AgendaActionKind.SyncHistra => Localization.Loc.Language switch { "cs" => "synchronizovat listy HISTRA", "en" => "synchronize the HISTRA lists", _ => "synchronizovať listy HISTRA" },
        AgendaActionKind.AnalyzeInput => Localization.Loc.Language switch { "cs" => "analyzovat složku Hromadného zpracování", "en" => "analyze the Batch processing folder", _ => "analyzovať priečinok Hromadného spracovania" },
        _ => Localization.Loc.Language switch { "cs" => "spustit MEGA dávku", "en" => "start a MEGA batch", _ => "spustiť MEGA dávku" },
    };

    /// <summary>The batches of MEGA spracovanie that wait for their start (the action "spustiť MEGA dávku").</summary>
    public static IReadOnlyList<VideoChoice<Guid?>> MegaChoices(Core.Mega.MegaStore store, Guid? keep = null) =>
        [.. store.Batches.Where(b => b.IsOpen || b.Id == keep).Select(b => new VideoChoice<Guid?>(b.Id, b.Label + (b.Name.Length > 0 ? ": " + b.Name : "") + $" ({b.Files.Count})"))];

    /// <summary>The batches offered (set by the page opening the editor).</summary>
    public IReadOnlyList<VideoChoice<Guid?>> MegaBatches { get; init; } = [];

    /// <summary>What UNIVERSUM does at the time of the event.</summary>
    public VideoChoice<AgendaActionKind?> Action
    {
        get => _action;
        set
        {
            if (SetProperty(ref _action, value))
            {
                if (value.Value is not null)
                {
                    // an action needs an hour
                    AllDay = false;
                }

                OnPropertyChanged(nameof(HasAction));
                OnPropertyChanged(nameof(IsMegaAction));
            }
        }
    }

    public bool HasAction => Action.Value is not null;

    public bool IsMegaAction => Action.Value == AgendaActionKind.StartMega;

    public VideoChoice<Guid?>? MegaBatch
    {
        get => _megaBatch ?? MegaBatches.FirstOrDefault(b => b.Value == _item?.Action?.Batch) ?? MegaBatches.FirstOrDefault();
        set => SetProperty(ref _megaBatch, value);
    }

    public bool NoMegaBatch => MegaBatches.Count == 0;

    /// <summary>How the action works, and how it ended the last time.</summary>
    public string ActionHint => Loc.T("Vykoná sa len vtedy, keď UNIVERSUM beží (zmeškaný čas sa nedobieha); nič nezvratné sa samo nespúšťa.") +
                                (_item?.Action is { LastRun: { } last } action ? Loc.F("\nNaposledy {0:d. M. yyyy HH:mm}: {1}", last.LocalDateTime, action.LastOutcome) : "");

    /// <summary>A new action of UNIVERSUM (Plánované úlohy): every night at three, the kind chosen.</summary>
    public void PresetAction(AgendaActionKind kind)
    {
        IsEvent = true;
        Action = Actions.First(a => a.Value == kind);
        StartTimeText = "03:00";
        Repeat = Repeats.First(r => r.Value == RepeatEvery.Day);
        if (Title.Trim().Length == 0)
        {
            Title = char.ToUpper(ActionText(kind)[0], CultureInfo.CurrentCulture) + ActionText(kind)[1..];
        }
    }

    /// <summary>The day the editor was opened for.</summary>
    public DateOnly Day { get; }

    public string Heading => (_item is null ? Loc.T("Nový záznam") : Loc.T("Upraviť záznam")) + " – " + Day.ToString("d. M. yyyy", CultureInfo.InvariantCulture);

    public bool IsEvent { get => _kind == AgendaKind.Event; set => SetKind(value, AgendaKind.Event); }

    /// <summary>The languages the voice is written down in.</summary>
    public static IReadOnlyList<VideoChoice<string>> VoiceLanguages { get; } = [new("sk", Loc.T("slovensky")), new("cs", Loc.T("česky")), new("en", Loc.T("anglicky"))];

    /// <summary>The recording and writing down of the voice (null: without it – the editor of the self-test's first steps).</summary>
    public VoiceRecorder? Voice { get; init; }

    public bool HasVoice => Voice is not null;

    public VideoChoice<string> VoiceLanguage { get => _voiceLanguage; set => SetProperty(ref _voiceLanguage, value); }

    /// <summary>Nahrať hlasom / Zastaviť a prepísať.</summary>
    public AsyncRelayCommand RecordCommand { get; }

    public RelayCommand PlayCommand { get; }

    public bool IsRecording { get => _recordingNow; private set { if (SetProperty(ref _recordingNow, value)) { OnPropertyChanged(nameof(RecordText)); } } }

    public string RecordText => IsRecording ? Loc.T("Zastaviť a prepísať") : Loc.T("Nahrať hlasom");

    public string VoiceStatus { get => _voiceStatus; private set => SetProperty(ref _voiceStatus, value); }

    /// <summary>The voice recording kept with the note.</summary>
    public string? RecordingPath => _recording is { Length: > 0 } name ? System.IO.Path.Combine(VoiceRecorder.Folder, name) : null;

    public bool HasRecording => RecordingPath is not null;

    /// <summary>A text written down from a recording is added to the text (also the self-test).</summary>
    public void AddSpoken(string text, string? recording)
    {
        if (text.Length > 0)
        {
            Text = Text.Trim().Length == 0 ? text : Text.TrimEnd() + "\n" + text;
        }

        if (recording is not null)
        {
            _recording = recording;
            OnPropertyChanged(nameof(RecordingPath));
            OnPropertyChanged(nameof(HasRecording));
            PlayCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RecordAsync()
    {
        if (Voice is not { } voice)
        {
            return;
        }

        if (!IsRecording)
        {
            VoiceStatus = Loc.T("Spúšťam mikrofón…");
            var problem = await voice.StartAsync();
            IsRecording = problem is null;
            VoiceStatus = problem ?? Loc.T("Nahrávam – hovorte. Keď skončíte, kliknite na Zastaviť a prepísať.");
            return;
        }

        IsRecording = false;
        VoiceStatus = Loc.T("Prepisujem nahrávku (v počítači, bez internetu)…");
        var (recording, text, trouble) = await voice.StopAsync(VoiceLanguage.Value);
        if (text is not null)
        {
            AddSpoken(text, recording);
        }
        else if (recording is not null)
        {
            AddSpoken("", recording);
        }

        VoiceStatus = trouble ?? Loc.T("Prepísané – skontrolujte text. Nahrávka ostáva pri poznámke.");
    }

    /// <summary>The checklist of a task (also of an event that is a task).</summary>
    public ObservableCollection<CheckRow> Checklist { get; } = [];

    public string NewCheckText { get => _newCheck; set => SetProperty(ref _newCheck, value); }

    public RelayCommand AddCheckCommand { get; }

    public bool HasChecklist => IsTask || (IsEvent && AlsoTask);

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
    public bool AlsoTask
    {
        get => _alsoTask;
        set
        {
            if (SetProperty(ref _alsoTask, value))
            {
                OnPropertyChanged(nameof(HasChecklist));
            }
        }
    }

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
            OnPropertyChanged(nameof(HasChecklist));
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
            Problem = Loc.T("Napíšte názov.");
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

                if (Action.Value is { } kind)
                {
                    if (time.StartTime is null)
                    {
                        Problem = Loc.T("Akcia UNIVERSUM potrebuje čas (zrušte Celý deň).");
                        return null;
                    }

                    if (kind == AgendaActionKind.StartMega && MegaBatch?.Value is null)
                    {
                        Problem = Loc.T("Zvoľte MEGA dávku, ktorá čaká na spustenie.");
                        return null;
                    }

                    item.Action = new AgendaAction
                    {
                        Kind = kind,
                        Batch = kind == AgendaActionKind.StartMega ? MegaBatch!.Value : null,
                        LastRun = item.Action?.LastRun,
                        LastOutcome = item.Action?.LastOutcome ?? "",
                    };
                }
                else
                {
                    item.Action = null;
                }

                item.Time = time;
                item.Task = AlsoTask ? MakeTask(item.Task, time.Start, time.StartTime) : null;
                item.Note = null;
                break;
            case AgendaKind.Task:
                if (DueDate is null && DueTimeText.Trim().Length > 0)
                {
                    Problem = Loc.T("Čas úlohy potrebuje aj deň.");
                    return null;
                }

                if (!TryHours(DueTimeText, out var dueTime))
                {
                    Problem = Loc.T("Čas úlohy napíšte ako 20:00.");
                    return null;
                }

                item.Time = null;
                item.Task = MakeTask(item.Task, DueDate is { } due ? DateOnly.FromDateTime(due) : null, dueTime);
                item.Note = null;
                item.Action = null;
                break;
            default:
                item.Time = null;
                item.Task = null;
                item.Action = null;
                item.Note = new AgendaNote { Pinned = Pinned, Recording = _recording };
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
            Checklist = [.. Checklist.Where(c => c.Text.Trim().Length > 0).Select(c => new CheckItem { Text = c.Text.Trim(), Done = c.Done })],
        };
    }

    private AgendaTime? EventTime()
    {
        if (StartDate is not { } startDate)
        {
            Problem = Loc.T("Zvoľte deň začiatku.");
            return null;
        }

        var start = DateOnly.FromDateTime(startDate);
        var end = EndDate is { } endDate ? DateOnly.FromDateTime(endDate) : (DateOnly?)null;
        if (end < start)
        {
            Problem = Loc.T("Koniec je pred začiatkom.");
            return null;
        }

        TimeOnly? startTime = null, endTime = null;
        if (!AllDay)
        {
            if (!TryHours(StartTimeText, out startTime) || startTime is null || !TryHours(EndTimeText, out endTime))
            {
                Problem = Loc.T("Čas napíšte ako 20:00 (alebo zaškrtnite Celý deň).");
                return null;
            }

            if (endTime is not null && (end ?? start) == start && endTime < startTime)
            {
                Problem = Loc.T("Koniec je pred začiatkom.");
                return null;
            }
        }

        Recurrence? repeat = null;
        if (Repeat.Value is { } every)
        {
            if (!int.TryParse(IntervalText, NumberStyles.None, CultureInfo.InvariantCulture, out var interval) || interval is < 1 or > 999)
            {
                Problem = Loc.T("Opakovanie: koľko (1 – 999).");
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
