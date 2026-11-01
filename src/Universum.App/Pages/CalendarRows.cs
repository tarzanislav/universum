using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Agenda;

namespace Universum.App.Pages;

/// <summary>How the calendar shows its days (owner, 31. 10. 2026).</summary>
public enum CalendarView
{
    /// <summary>Mesiac – mriežka 7 × 6.</summary>
    Month,

    /// <summary>Týždeň – 7 stĺpcov.</summary>
    Week,

    /// <summary>Týždeň – dni pod sebou.</summary>
    WeekList,

    /// <summary>Mesiac – dni pod sebou.</summary>
    MonthList,

    /// <summary>Rok – 12 malých mesiacov.</summary>
    YearMonths,

    /// <summary>Rok – riadky 1 – 31, stĺpce mesiace.</summary>
    YearRows,

    /// <summary>Agenda – najbližšie dni, v ktorých niečo je.</summary>
    Agenda,
}

/// <summary>What the calendar can show – each switched on or off by itself (owner, 31. 10. 2026, points c and d).</summary>
public enum CalendarLayer
{
    FilmsDomestic,
    FilmsWorld,
    SeriesDomestic,
    SeriesWorld,
    EpisodesDomestic,
    EpisodesWorld,
    Events,
    Tasks,
    Notes,
    Holidays,
    Memorial,
    School,
    NameDays,

    /// <summary>The batches of MEGA spracovanie planned for a day and an hour.</summary>
    Mega,

    /// <summary>The runs of processing kept in the logs, on the day they ended.</summary>
    Processed,

    /// <summary>The records with an action UNIVERSUM does by itself at their time.</summary>
    Actions,
}

/// <summary>The colours of the layers: those of the tabs of Premiéry and Novinky, so nothing has to be learnt again.</summary>
public static class CalendarColors
{
    private static readonly Dictionary<CalendarLayer, SolidColorBrush> Brushes = new()
    {
        [CalendarLayer.FilmsDomestic] = Frozen(0xF2, 0x8C, 0x7C),
        [CalendarLayer.FilmsWorld] = Frozen(0x6F, 0xA8, 0xE8),
        [CalendarLayer.SeriesDomestic] = Frozen(0x63, 0xC4, 0x8F),
        [CalendarLayer.SeriesWorld] = Frozen(0xA7, 0x8B, 0xE6),
        [CalendarLayer.EpisodesDomestic] = Frozen(0x4F, 0xB3, 0xA9),
        [CalendarLayer.EpisodesWorld] = Frozen(0x5B, 0x8F, 0xD9),
        [CalendarLayer.Events] = Frozen(0xD4, 0x6A, 0x8C),
        [CalendarLayer.Tasks] = Frozen(0xE0, 0x9A, 0x3E),
        [CalendarLayer.Notes] = Frozen(0xC9, 0xA2, 0x27),
        [CalendarLayer.Holidays] = Frozen(0xD9, 0x4F, 0x4F),
        [CalendarLayer.Memorial] = Frozen(0x8C, 0x8C, 0x8C),
        [CalendarLayer.School] = Frozen(0xE8, 0xB9, 0x3A),
        [CalendarLayer.NameDays] = Frozen(0x7A, 0x7A, 0x9A),
        // the tints of the groups ŠABLÓNY (MEGA spracovanie) and MULTIPLEXING in the left rail
        [CalendarLayer.Mega] = Frozen(0x3E, 0xA8, 0x5F),
        [CalendarLayer.Processed] = Frozen(0x4A, 0x7B, 0xD9),
        [CalendarLayer.Actions] = Frozen(0x7A, 0x6F, 0xC9),
    };

    /// <summary>The group POZNÁMKY in the left rail – a soft rose.</summary>
    public static Color Group { get; } = Color.FromRgb(0xD4, 0x6A, 0x8C);

    public static SolidColorBrush Of(CalendarLayer layer) => Brushes[layer];

    /// <summary>A record's own colour ("#RRGGBB"), else its layer's.</summary>
    public static SolidColorBrush Of(CalendarLayer layer, string? color)
    {
        if (color is { Length: 7 } && color[0] == '#' && int.TryParse(color[1..], System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return Frozen((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return Of(layer);
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>A switch of one layer.</summary>
public sealed class CalendarLayerSwitch(CalendarLayer layer, string title, bool on, Action changed) : ObservableObject
{
    private bool _on = on;

    public CalendarLayer Layer { get; } = layer;

    public string Title { get; } = title;

    public Brush Color => CalendarColors.Of(Layer);

    public bool IsOn
    {
        get => _on;
        set
        {
            if (SetProperty(ref _on, value))
            {
                changed();
            }
        }
    }

    // set by the group's switch without telling each change
    internal void Set(bool value)
    {
        _on = value;
        OnPropertyChanged(nameof(IsOn));
    }
}

/// <summary>A group of layers with its own switch: all on, all off, or some (owner: "jednotlivo aj všetky naraz").</summary>
public sealed class CalendarLayerGroup : ObservableObject
{
    private readonly Action _changed;

    public CalendarLayerGroup(string title, IReadOnlyList<CalendarLayerSwitch> layers, Action changed)
    {
        Title = title;
        Layers = layers;
        _changed = changed;
    }

    public string Title { get; }

    public IReadOnlyList<CalendarLayerSwitch> Layers { get; }

    /// <summary>All on (true), all off (false), some (null); setting it switches all.</summary>
    public bool? AllOn
    {
        get => Layers.All(l => l.IsOn) ? true : Layers.Any(l => l.IsOn) ? null : false;
        set
        {
            var on = value ?? false;
            foreach (var layer in Layers)
            {
                layer.Set(on);
            }

            OnPropertyChanged();
            _changed();
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(AllOn));
}

/// <summary>One thing on a day: a premiere, an episode, a holiday, a record of the agenda …</summary>
public sealed class CalendarEntry
{
    public required CalendarLayer Layer { get; init; }

    public required string Title { get; init; }

    public string Detail { get; init; } = "";

    public TimeOnly? Time { get; init; }

    /// <summary>"20:00", or empty for a whole day.</summary>
    public string TimeText => Time?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    public bool HasTime => Time is not null;

    public Brush Color { get; init; } = Brushes.Gray;

    /// <summary>A chosen title (Vybrané).</summary>
    public bool Chosen { get; init; }

    /// <summary>A task done (crossed out).</summary>
    public bool Done { get; init; }

    /// <summary>The day is shaded (a day of school holidays).</summary>
    public bool Shade { get; init; }

    /// <summary>Shown in the day's cell too (a holiday period only on its first day there; always in the detail).</summary>
    public bool InCell { get; init; } = true;

    /// <summary>The record of the agenda; null for what other parts of UNIVERSUM give.</summary>
    public AgendaItem? Record { get; init; }

    public string ToolTip => Detail.Length > 0 ? Title + "\n" + Detail : Title;

    /// <summary>Opens it: a record in the editor, a premiere in Premiéry, an episode in Novinky.</summary>
    public RelayCommand? OpenCommand { get; init; }

    public bool CanOpen => OpenCommand is not null;

    /// <summary>The order on a day: holidays, records by their hour, premieres, episodes, what UNIVERSUM plans and did.</summary>
    internal int Rank => Layer switch
    {
        CalendarLayer.Holidays => 0,
        CalendarLayer.School => 1,
        CalendarLayer.Memorial => 2,
        CalendarLayer.Events or CalendarLayer.Tasks or CalendarLayer.Notes => 3,
        CalendarLayer.FilmsDomestic or CalendarLayer.FilmsWorld => 4,
        CalendarLayer.SeriesDomestic or CalendarLayer.SeriesWorld => 5,
        CalendarLayer.Mega or CalendarLayer.Processed or CalendarLayer.Actions => 7,
        _ => 6,
    };
}

/// <summary>A day of the calendar as a view shows it.</summary>
public sealed class CalendarDay : ObservableObject
{
    private bool _selected;

    public required DateOnly Date { get; init; }

    /// <summary>The day belongs to the period shown (a month grid shows the days around it greyed).</summary>
    public bool InPeriod { get; init; } = true;

    public bool IsToday { get; init; }

    public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public bool IsDayOff { get; init; }

    /// <summary>A day of school holidays (shaded).</summary>
    public bool IsSchoolHoliday { get; init; }

    public string DayText => Date.Day.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"pondelok 2. 11." in the lists.</summary>
    public string LongText { get; init; } = "";

    /// <summary>The names of the day: the first language on top, the others under it (owner, points f and g).</summary>
    public string Names1 { get; init; } = "";

    public string Names2 { get; init; } = "";

    public string Names3 { get; init; } = "";

    /// <summary>The holiday or holidays of the day ("Štedrý deň").</summary>
    public string Holiday { get; init; } = "";

    public bool HasHoliday => Holiday.Length > 0;

    public IReadOnlyList<CalendarEntry> Entries { get; init; } = [];

    /// <summary>What the cell shows (at most a few) and how many more there are.</summary>
    public IReadOnlyList<CalendarEntry> Shown { get; init; } = [];

    public string More { get; init; } = "";

    public bool HasEntries => Entries.Count > 0;

    /// <summary>How much happens (0 – 4) – the dots of the year.</summary>
    public int Density { get; init; }

    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }

    public RelayCommand? SelectCommand { get; init; }

    public string ToolTip => string.Join("\n", new[] { LongText, Holiday, Names1 }.Where(t => t.Length > 0).Concat(Entries.Where(e => e.InCell).Take(8).Select(e => (e.TimeText.Length > 0 ? e.TimeText + " " : "") + e.Title)));
}

/// <summary>A month of the year view: its name and 42 cells (empty around the month).</summary>
public sealed record CalendarMonth(string Title, IReadOnlyList<CalendarDay?> Cells);

/// <summary>A row of the year in rows: a day of the month and that day in each of the 12 months (empty when it does not exist).</summary>
public sealed record CalendarYearRow(string Day, IReadOnlyList<CalendarDay?> Months);

/// <summary>A part of the detail of a day: a heading in its colour and what belongs there.</summary>
public sealed record CalendarSection(string Title, Brush Color, IReadOnlyList<CalendarEntry> Entries);
