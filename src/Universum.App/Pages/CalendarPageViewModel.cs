using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Agenda;
using Universum.Core.Calendar;
using Universum.Core.News;

namespace Universum.App.Pages;

/// <summary>A region of the holidays switched on or off in the calendar's settings.</summary>
public sealed class CalendarRegionSwitch(string code, string name, bool on, Action changed) : ObservableObject
{
    private bool _on = on;

    public string Code { get; } = code;

    public string Name { get; } = name;

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
}

/// <summary>
/// Kalendár (owner, 31. 10. 2026 – the group POZNÁMKY): the days in seven views (month grid, week columns, week and month
/// as lists, year as 12 months or as rows, agenda); layers switched one by one or by groups – the premieres of
/// Premiéry, the episodes of Novinky, the user's records, the holidays and memorial days of the chosen countries, the
/// school holidays of the chosen region or district, the names of the day in Slovak, Czech and English; a click on a day
/// shows all about it on the left, where records are also made and changed. Nothing is asked from the internet here: the
/// premieres and episodes are those Premiéry and Novinky keep.
/// </summary>
public sealed partial class CalendarPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly CalendarSources _sources;
    private readonly Dictionary<CalendarLayer, CalendarLayerSwitch> _layers = [];
    private CalendarView _view;
    private DateOnly _anchor;
    private DateOnly _selected;
    private AgendaEditor? _editor;
    private VideoChoice<int> _agendaSpan;
    private bool _hideEmpty;
    private string _periodText = "";
    private string _status = "";
    private IReadOnlyList<CalendarSection> _sections = [];
    private IReadOnlyList<string> _detailNames = [];
    private bool _building;

    public CalendarPageViewModel(AppServices services)
    {
        _services = services;
        _sources = new CalendarSources(services);
        _view = Enum.TryParse<CalendarView>(services.Settings.CalendarView, out var view) ? view : CalendarView.Month;
        _anchor = _selected = Today;
        _agendaSpan = AgendaSpans[1];
        var hidden = services.Settings.CalendarHiddenLayers.ToHashSet(StringComparer.Ordinal);
        CalendarLayerSwitch Layer(CalendarLayer layer, string title) =>
            _layers[layer] = new CalendarLayerSwitch(layer, title, !hidden.Contains(layer.ToString()), LayersChanged);
        LayerGroups =
        [
            new CalendarLayerGroup(L("Premiéry", "Premiéry", "Premieres"),
                [Layer(CalendarLayer.FilmsDomestic, L("filmy CZ/SK", "filmy CZ/SK", "films CZ/SK")), Layer(CalendarLayer.FilmsWorld, L("filmy SVET", "filmy SVĚT", "films WORLD")),
                 Layer(CalendarLayer.SeriesDomestic, L("seriály CZ/SK", "seriály CZ/SK", "series CZ/SK")), Layer(CalendarLayer.SeriesWorld, L("seriály SVET", "seriály SVĚT", "series WORLD"))], LayersChanged),
            new CalendarLayerGroup(L("Novinky – Seriály", "Novinky – Seriály", "New episodes"),
                [Layer(CalendarLayer.EpisodesDomestic, "CZ/SK"), Layer(CalendarLayer.EpisodesWorld, L("SVET", "SVĚT", "WORLD"))], LayersChanged),
            new CalendarLayerGroup(L("Moje", "Moje", "Mine"),
                [Layer(CalendarLayer.Events, L("udalosti", "události", "events")), Layer(CalendarLayer.Tasks, L("úlohy", "úkoly", "tasks")), Layer(CalendarLayer.Notes, L("poznámky k dňu", "poznámky ke dni", "notes of the day"))], LayersChanged),
            new CalendarLayerGroup(L("Kalendárium", "Kalendárium", "Almanac"),
                [Layer(CalendarLayer.Holidays, L("sviatky a voľné dni", "svátky a volné dny", "holidays")), Layer(CalendarLayer.Memorial, L("pamätné dni", "významné dny", "memorial days")),
                 Layer(CalendarLayer.School, L("školské prázdniny", "školní prázdniny", "school holidays")), Layer(CalendarLayer.NameDays, L("meniny", "jmeniny", "name days"))], LayersChanged),
        ];
        var regions = Regions;
        RegionSwitches = [.. HolidayRegions.All.Select(r => new CalendarRegionSwitch(r, HolidayRegions.Name(r, Loc.Language), regions.Contains(r), RegionsChanged))];
        SchoolRegions = [new VideoChoice<string?>(null, L("všetky kraje", "všechny kraje", "all regions")), .. SchoolHolidays.SlovakRegions.Select(r => new VideoChoice<string?>(r.Code, r.Name))];
        SchoolDistricts = [new VideoChoice<string?>(null, L("všetky okresy", "všechny okresy", "all districts")), .. SchoolHolidays.CzechDistricts.Select(d => new VideoChoice<string?>(d, d))];
        PreviousCommand = new RelayCommand(() => Move(-1));
        NextCommand = new RelayCommand(() => Move(1));
        TodayCommand = new RelayCommand(() =>
        {
            _anchor = Today;
            Select(Today);
            Build();
        });
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        NewEventCommand = new RelayCommand(() => Edit(null, AgendaKind.Event));
        NewTaskCommand = new RelayCommand(() => Edit(null, AgendaKind.Task));
        NewNoteCommand = new RelayCommand(() => Edit(null, AgendaKind.Note));
        services.Agenda.Changed += () =>
        {
            if (!_building)
            {
                Build();
            }
        };
        Build();
    }

    public static IReadOnlyList<VideoChoice<int>> AgendaSpans { get; } = [new(7, "7 dní"), new(30, "30 dní"), new(90, "90 dní")];

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static CultureInfo Culture => CalendarTexts.Culture(Loc.Language);

    /// <summary>Opens a premiere in Premiéry or an episode in Novinky (set by the main window): the layer and the title to look for.</summary>
    public Action<CalendarLayer, string> OpenNews { get; set; } = (_, _) => { };

    /// <summary>The premieres and episodes kept by Premiéry and Novinky are read (awaited by the self-test).</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    // ---------------------------------------------------------------- views

    public bool IsMonth { get => _view == CalendarView.Month; set => SetView(value, CalendarView.Month); }

    public bool IsWeek { get => _view == CalendarView.Week; set => SetView(value, CalendarView.Week); }

    public bool IsWeekList { get => _view == CalendarView.WeekList; set => SetView(value, CalendarView.WeekList); }

    public bool IsMonthList { get => _view == CalendarView.MonthList; set => SetView(value, CalendarView.MonthList); }

    public bool IsYearMonths { get => _view == CalendarView.YearMonths; set => SetView(value, CalendarView.YearMonths); }

    public bool IsYearRows { get => _view == CalendarView.YearRows; set => SetView(value, CalendarView.YearRows); }

    public bool IsAgenda { get => _view == CalendarView.Agenda; set => SetView(value, CalendarView.Agenda); }

    /// <summary>The lists (a week, a month, the agenda) show the days under each other.</summary>
    public bool IsList => _view is CalendarView.WeekList or CalendarView.MonthList or CalendarView.Agenda;

    public CalendarView View => _view;

    public string PeriodText { get => _periodText; private set => SetProperty(ref _periodText, value); }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>"po", "ut" … from Monday, in the language of the interface.</summary>
    public IReadOnlyList<string> WeekdayNames => [.. Enumerable.Range(1, 7).Select(i => Culture.DateTimeFormat.AbbreviatedDayNames[i % 7])];

    /// <summary>The 12 months' short names (the year in rows).</summary>
    public IReadOnlyList<string> MonthNames => [.. Culture.DateTimeFormat.AbbreviatedMonthNames.Take(12)];

    /// <summary>The days of the month grid, the week and the lists.</summary>
    public ObservableCollection<CalendarDay> Days { get; } = [];

    /// <summary>The 12 months of the year view.</summary>
    public ObservableCollection<CalendarMonth> Months { get; } = [];

    /// <summary>The 31 rows of the year in rows.</summary>
    public ObservableCollection<CalendarYearRow> YearRows { get; } = [];

    public VideoChoice<int> AgendaSpan
    {
        get => _agendaSpan;
        set
        {
            if (SetProperty(ref _agendaSpan, value))
            {
                Build();
            }
        }
    }

    /// <summary>The month as a list leaves out the days without anything.</summary>
    public bool HideEmptyDays
    {
        get => _hideEmpty;
        set
        {
            if (SetProperty(ref _hideEmpty, value))
            {
                Build();
            }
        }
    }

    // ---------------------------------------------------------------- layers and settings

    public IReadOnlyList<CalendarLayerGroup> LayerGroups { get; }

    /// <summary>Only the chosen titles (Vybrané) among the premieres and episodes.</summary>
    public bool OnlyChosen
    {
        get => _services.Settings.CalendarOnlyChosen;
        set
        {
            if (value != _services.Settings.CalendarOnlyChosen)
            {
                _services.Settings.CalendarOnlyChosen = value;
                _services.Settings.Save();
                OnPropertyChanged();
                Build();
            }
        }
    }

    public IReadOnlyList<CalendarRegionSwitch> RegionSwitches { get; }

    public bool NamesSk { get => _services.Settings.CalendarNamesSk; set => Store(value, _services.Settings.CalendarNamesSk, v => _services.Settings.CalendarNamesSk = v); }

    public bool NamesCs { get => _services.Settings.CalendarNamesCs; set => Store(value, _services.Settings.CalendarNamesCs, v => _services.Settings.CalendarNamesCs = v); }

    public bool NamesEn { get => _services.Settings.CalendarNamesEn ?? Loc.Language == "en"; set => Store(value, NamesEn, v => _services.Settings.CalendarNamesEn = v); }

    public IReadOnlyList<VideoChoice<string?>> SchoolRegions { get; }

    public IReadOnlyList<VideoChoice<string?>> SchoolDistricts { get; }

    public VideoChoice<string?> SchoolRegion
    {
        get => SchoolRegions.FirstOrDefault(r => r.Value == _services.Settings.CalendarSchoolRegion) ?? SchoolRegions[0];
        set
        {
            if (value is not null && value.Value != _services.Settings.CalendarSchoolRegion)
            {
                _services.Settings.CalendarSchoolRegion = value.Value;
                _services.Settings.Save();
                OnPropertyChanged();
                Build();
            }
        }
    }

    public VideoChoice<string?> SchoolDistrict
    {
        get => SchoolDistricts.FirstOrDefault(r => r.Value == _services.Settings.CalendarSchoolDistrict) ?? SchoolDistricts[0];
        set
        {
            if (value is not null && value.Value != _services.Settings.CalendarSchoolDistrict)
            {
                _services.Settings.CalendarSchoolDistrict = value.Value;
                _services.Settings.Save();
                OnPropertyChanged();
                Build();
            }
        }
    }

    public bool HasSlovakSchool => Regions.Contains(HolidayRegions.Slovakia);

    public bool HasCzechSchool => Regions.Contains(HolidayRegions.Czechia);

    /// <summary>The regions of the holidays: the chosen ones, else by the language of the interface.</summary>
    private IReadOnlyList<string> Regions => _services.Settings.CalendarRegions is { Count: > 0 } chosen
        ? chosen
        : [Loc.Language switch { "cs" => HolidayRegions.Czechia, "en" => HolidayRegions.England, _ => HolidayRegions.Slovakia }];

    // ---------------------------------------------------------------- the day chosen and the editor

    public DateOnly SelectedDate => _selected;

    public string DetailTitle => Capitalized(CalendarTexts.LongDate(_selected, Loc.Language));

    public string DetailWeek => L($"{CalendarTexts.Week(_selected)}. týždeň · {_selected.DayOfYear}. deň roka", $"{CalendarTexts.Week(_selected)}. týden · {_selected.DayOfYear}. den roku",
        $"week {CalendarTexts.Week(_selected)} · day {_selected.DayOfYear} of the year");

    /// <summary>"SK: Sabína", "CZ: Sabina", "EN: Sabina" – the names on and the day without a name.</summary>
    public IReadOnlyList<string> DetailNames { get => _detailNames; private set => SetProperty(ref _detailNames, value); }

    public IReadOnlyList<CalendarSection> DetailSections { get => _sections; private set => SetProperty(ref _sections, value); }

    public bool DetailIsEmpty => DetailSections.Count == 0;

    /// <summary>The editor of a record, shown on the left instead of the detail; null: the detail.</summary>
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

    /// <summary>A date to jump to.</summary>
    public DateTime? JumpDate
    {
        get => _anchor.ToDateTime(TimeOnly.MinValue);
        set
        {
            if (value is { } date)
            {
                _anchor = DateOnly.FromDateTime(date);
                Select(_anchor);
                Build();
            }
        }
    }

    public RelayCommand PreviousCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand TodayCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand NewEventCommand { get; }

    public RelayCommand NewTaskCommand { get; }

    public RelayCommand NewNoteCommand { get; }

    /// <summary>The page was opened: the kept premieres and episodes are read once.</summary>
    public void Shown()
    {
        if (!_sources.Loaded && Ready.IsCompleted)
        {
            Ready = LoadAsync();
        }
    }

    /// <summary>Chooses a day (its detail on the left; the self-test too).</summary>
    public void Select(DateOnly date)
    {
        _selected = date;
        foreach (var day in Days)
        {
            day.IsSelected = day.Date == date;
        }

        Editor = null;
        OnPropertyChanged(nameof(SelectedDate));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailWeek));
        Detail();
    }

    /// <summary>Opens the editor for a new record of the chosen day, or for a record.</summary>
    public void Edit(AgendaItem? item, AgendaKind kind) => Editor = new AgendaEditor(_services.Agenda, item, _selected, kind, () =>
    {
        Editor = null;
        Detail();
    })
    {
        Voice = _services.Voice,
    };

    private async Task LoadAsync()
    {
        Status = L("Čítam premiéry a epizódy, ktoré UNIVERSUM uchováva…", "Čtu premiéry a epizody, které UNIVERSUM uchovává…", "Reading the premieres and episodes UNIVERSUM keeps…");
        try
        {
            await _sources.LoadAsync();
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = L("Premiéry a epizódy sa nedajú prečítať: ", "Premiéry a epizody nelze přečíst: ", "The premieres and episodes cannot be read: ") + ex.Message;
        }

        Build();
    }

    private void SetView(bool chosen, CalendarView view)
    {
        if (!chosen || _view == view)
        {
            return;
        }

        _view = view;
        _services.Settings.CalendarView = view.ToString();
        _services.Settings.Save();
        if (view == CalendarView.Agenda)
        {
            _anchor = Today;
        }

        foreach (var name in new[] { nameof(IsMonth), nameof(IsWeek), nameof(IsWeekList), nameof(IsMonthList), nameof(IsYearMonths), nameof(IsYearRows), nameof(IsAgenda), nameof(IsList), nameof(View) })
        {
            OnPropertyChanged(name);
        }

        Build();
    }

    private void Move(int step)
    {
        _anchor = _view switch
        {
            CalendarView.Month or CalendarView.MonthList => _anchor.AddMonths(step),
            CalendarView.Week or CalendarView.WeekList => _anchor.AddDays(7 * step),
            CalendarView.YearMonths or CalendarView.YearRows => _anchor.AddYears(step),
            _ => _anchor.AddDays(AgendaSpan.Value * step),
        };
        Build();
    }

    private void LayersChanged()
    {
        _services.Settings.CalendarHiddenLayers = [.. _layers.Values.Where(l => !l.IsOn).Select(l => l.Layer.ToString())];
        _services.Settings.Save();
        foreach (var group in LayerGroups)
        {
            group.Refresh();
        }

        Build();
    }

    private void RegionsChanged()
    {
        _services.Settings.CalendarRegions = [.. RegionSwitches.Where(r => r.IsOn).Select(r => r.Code)];
        _services.Settings.Save();
        OnPropertyChanged(nameof(HasSlovakSchool));
        OnPropertyChanged(nameof(HasCzechSchool));
        Build();
    }

    private void Store(bool value, bool current, Action<bool> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (value != current)
        {
            set(value);
            _services.Settings.Save();
            OnPropertyChanged(name);
            Build();
        }
    }

    private bool On(CalendarLayer layer) => _layers[layer].IsOn;

    // ---------------------------------------------------------------- building the views

    /// <summary>The days of the view again (after a change of the period, the layers or the records).</summary>
    public void Build()
    {
        _building = true;
        try
        {
            var (from, to) = Span();
            var entries = Collect(from, to);
            Days.Clear();
            Months.Clear();
            YearRows.Clear();
            var cells = new Dictionary<DateOnly, CalendarDay>();
            CalendarDay Cell(DateOnly date, bool inPeriod, int shown) => cells.TryGetValue(date, out var day) ? day : cells[date] = Day(date, inPeriod, entries.GetValueOrDefault(date) ?? [], shown);
            switch (_view)
            {
                case CalendarView.Month:
                    for (var d = from; d <= to; d = d.AddDays(1))
                    {
                        Days.Add(Cell(d, d.Month == _anchor.Month, 3));
                    }

                    break;
                case CalendarView.Week:
                    for (var d = from; d <= to; d = d.AddDays(1))
                    {
                        Days.Add(Cell(d, true, 99));
                    }

                    break;
                case CalendarView.WeekList:
                case CalendarView.MonthList:
                case CalendarView.Agenda:
                    for (var d = from; d <= to; d = d.AddDays(1))
                    {
                        var day = Cell(d, true, 99);
                        if ((_view == CalendarView.Agenda || HideEmptyDays) && day.Shown.Count == 0 && !day.HasHoliday && d != Today)
                        {
                            continue;
                        }

                        Days.Add(day);
                    }

                    break;
                case CalendarView.YearMonths:
                    for (var month = 1; month <= 12; month++)
                    {
                        var first = new DateOnly(_anchor.Year, month, 1);
                        var lead = ((int)first.DayOfWeek + 6) % 7;
                        var monthCells = new CalendarDay?[42];
                        for (var i = 0; i < DateTime.DaysInMonth(first.Year, month); i++)
                        {
                            monthCells[lead + i] = Cell(first.AddDays(i), true, 0);
                        }

                        Months.Add(new CalendarMonth(Capitalized(Culture.DateTimeFormat.MonthNames[month - 1]), monthCells));
                    }

                    break;
                case CalendarView.YearRows:
                    for (var dayOfMonth = 1; dayOfMonth <= 31; dayOfMonth++)
                    {
                        var row = new CalendarDay?[12];
                        for (var month = 1; month <= 12; month++)
                        {
                            if (dayOfMonth <= DateTime.DaysInMonth(_anchor.Year, month))
                            {
                                row[month - 1] = Cell(new DateOnly(_anchor.Year, month, dayOfMonth), true, 0);
                            }
                        }

                        YearRows.Add(new CalendarYearRow(dayOfMonth.ToString(CultureInfo.InvariantCulture), row));
                    }

                    break;
            }

            PeriodText = Period(from, to);
            foreach (var day in cells.Values)
            {
                day.IsSelected = day.Date == _selected;
            }

            Detail();
        }
        finally
        {
            _building = false;
        }
    }

    // the first and the last day of the view
    private (DateOnly From, DateOnly To) Span()
    {
        var monday = _anchor.AddDays(-(((int)_anchor.DayOfWeek + 6) % 7));
        var first = new DateOnly(_anchor.Year, _anchor.Month, 1);
        return _view switch
        {
            CalendarView.Month => (first.AddDays(-(((int)first.DayOfWeek + 6) % 7)), first.AddDays(-(((int)first.DayOfWeek + 6) % 7)).AddDays(41)),
            CalendarView.Week or CalendarView.WeekList => (monday, monday.AddDays(6)),
            CalendarView.MonthList => (first, first.AddMonths(1).AddDays(-1)),
            CalendarView.YearMonths or CalendarView.YearRows => (new DateOnly(_anchor.Year, 1, 1), new DateOnly(_anchor.Year, 12, 31)),
            _ => (_anchor, _anchor.AddDays(AgendaSpan.Value - 1)),
        };
    }

    private string Period(DateOnly from, DateOnly to) => _view switch
    {
        CalendarView.Month or CalendarView.MonthList => Capitalized(Culture.DateTimeFormat.MonthNames[_anchor.Month - 1]) + " " + _anchor.Year.ToString(CultureInfo.InvariantCulture),
        CalendarView.Week or CalendarView.WeekList => L($"{CalendarTexts.Week(from)}. týždeň", $"{CalendarTexts.Week(from)}. týden", $"Week {CalendarTexts.Week(from)}") +
                                                      $" · {from:d. M.} – {to:d. M. yyyy}",
        CalendarView.YearMonths or CalendarView.YearRows => _anchor.Year.ToString(CultureInfo.InvariantCulture),
        _ => L($"Najbližších {AgendaSpan.Value} dní od {from:d. M. yyyy}", $"Nejbližších {AgendaSpan.Value} dní od {from:d. M. yyyy}", $"Next {AgendaSpan.Value} days from {from:d. M. yyyy}"),
    };

    private CalendarDay Day(DateOnly date, bool inPeriod, IReadOnlyList<CalendarEntry> entries, int shown)
    {
        var names = NameLines(date);
        var holidays = entries.Where(e => e.Layer == CalendarLayer.Holidays).ToList();
        var inCell = entries.Where(e => e.InCell && e.Layer != CalendarLayer.Holidays).OrderBy(e => e.Rank).ThenBy(e => e.Time ?? TimeOnly.MinValue).ToList();
        var shownEntries = shown >= inCell.Count ? inCell : inCell.Take(shown).ToList();
        return new CalendarDay
        {
            Date = date,
            InPeriod = inPeriod,
            IsToday = date == Today,
            IsDayOff = On(CalendarLayer.Holidays) && Regions.Any(r => Holidays.IsDayOff(date, r)),
            IsSchoolHoliday = entries.Any(e => e.Layer == CalendarLayer.School && e.Shade),
            LongText = Capitalized(date.ToString(Loc.Language == "en" ? "dddd d MMM" : "dddd d. M.", Culture)),
            Names1 = names.ElementAtOrDefault(0) ?? "",
            Names2 = names.ElementAtOrDefault(1) ?? "",
            Names3 = names.ElementAtOrDefault(2) ?? "",
            Holiday = string.Join(" · ", holidays.Select(h => h.Title).Distinct()),
            Entries = entries,
            Shown = shownEntries,
            More = inCell.Count > shownEntries.Count && shown > 0 ? "+" + (inCell.Count - shownEntries.Count).ToString(CultureInfo.InvariantCulture) : "",
            Density = Math.Min(4, entries.Count(e => e.Rank >= 3)),
            SelectCommand = new RelayCommand(() => Select(date)),
        };
    }

    // the names of the day in the languages switched on: the language of the interface first (owner, points f and g)
    private List<string> NameLines(DateOnly date)
    {
        if (!On(CalendarLayer.NameDays))
        {
            return [];
        }

        string[] order = Loc.Language switch { "cs" => ["cs", "sk", "en"], "en" => ["en", "sk", "cs"], _ => ["sk", "cs", "en"] };
        return [.. order.Where(l => l switch { "sk" => NamesSk, "cs" => NamesCs, _ => NamesEn })
            .Select(l => string.Join(", ", NameDays.Of(date, l))).Where(n => n.Length > 0)];
    }

    // everything of the days, by day, in the layers switched on
    private Dictionary<DateOnly, List<CalendarEntry>> Collect(DateOnly from, DateOnly to)
    {
        var map = new Dictionary<DateOnly, List<CalendarEntry>>();
        void Add(DateOnly day, CalendarEntry entry)
        {
            if (day >= from && day <= to && On(entry.Layer))
            {
                (map.TryGetValue(day, out var list) ? list : map[day] = []).Add(entry);
            }
        }

        foreach (var (day, entry) in _sources.Between(from, to, OnlyChosen, OpenNews))
        {
            Add(day, entry);
        }

        foreach (var occurrence in _services.Agenda.Between(from, to))
        {
            var item = occurrence.Item;
            var layer = item.Time is not null ? CalendarLayer.Events : CalendarLayer.Tasks;
            for (var day = occurrence.First; day <= occurrence.Last; day = day.AddDays(1))
            {
                Add(day, Record(item, layer, day == occurrence.First));
            }
        }

        foreach (var item in _services.Agenda.Items.Where(i => i.Note is not null && i.Time is null && i.Task is null))
        {
            foreach (var link in item.Links.Where(l => l.Kind == LinkKind.Day))
            {
                if (DateOnly.TryParseExact(link.Target, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    Add(day, Record(item, CalendarLayer.Notes, true));
                }
            }
        }

        var regions = Regions;
        var language = Loc.Language;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            foreach (var holiday in Holidays.On(day, regions))
            {
                var memorial = holiday.Kind == HolidayKind.Memorial;
                Add(day, new CalendarEntry
                {
                    Layer = memorial ? CalendarLayer.Memorial : CalendarLayer.Holidays,
                    Title = holiday.Name(language) + (regions.Count > 1 ? $" ({holiday.Region})" : ""),
                    Detail = HolidayDetail(holiday, language),
                    Color = CalendarColors.Of(memorial ? CalendarLayer.Memorial : CalendarLayer.Holidays),
                });
            }
        }

        foreach (var (country, area) in SchoolAreas())
        {
            foreach (var period in SchoolHolidays.Between(from, to, country, area))
            {
                for (var day = period.From < from ? from : period.From; day <= period.To && day <= to; day = day.AddDays(1))
                {
                    Add(day, new CalendarEntry
                    {
                        Layer = CalendarLayer.School,
                        Title = Capitalized(period.Name(language)) + (regions.Count > 1 ? $" ({country})" : ""),
                        Detail = (period.From == period.To ? $"{period.From:d. M. yyyy}" : $"{period.From:d. M.} – {period.To:d. M. yyyy}") +
                                 (period.Areas.Count > 0 && area is not null ? " · " + AreaName(country, area) : ""),
                        Shade = period.IsHoliday,
                        Color = CalendarColors.Of(CalendarLayer.School),
                        InCell = day == period.From || day == from || day.DayOfWeek == DayOfWeek.Monday,
                    });
                }
            }
        }

        return map;
    }

    private IEnumerable<(string Country, string? Area)> SchoolAreas()
    {
        if (HasSlovakSchool)
        {
            yield return (HolidayRegions.Slovakia, _services.Settings.CalendarSchoolRegion);
        }

        if (HasCzechSchool)
        {
            yield return (HolidayRegions.Czechia, _services.Settings.CalendarSchoolDistrict);
        }
    }

    private static string AreaName(string country, string area) =>
        country == HolidayRegions.Slovakia ? SchoolHolidays.SlovakRegions.FirstOrDefault(r => r.Code == area).Name ?? area : area;

    private static string HolidayDetail(Holiday holiday, string language)
    {
        var kind = (holiday.Kind, language) switch
        {
            (HolidayKind.State, "cs") => "státní svátek",
            (HolidayKind.State, "en") => "state holiday",
            (HolidayKind.State, _) => "štátny sviatok",
            (HolidayKind.Other, "cs") => "ostatní svátek",
            (HolidayKind.Other, "en") => "public holiday",
            (HolidayKind.Other, _) => "deň pracovného pokoja",
            (HolidayKind.Memorial, "cs") => "významný den",
            (HolidayKind.Memorial, "en") => "memorial day",
            (HolidayKind.Memorial, _) => "pamätný deň",
            (HolidayKind.Bank, _) => "bank holiday",
            _ => language == "en" ? "federal holiday" : language == "cs" ? "federální svátek" : "federálny sviatok",
        };
        var off = holiday.DayOff
            ? language switch { "cs" => "den pracovního klidu", "en" => "day off", _ => "deň pracovného pokoja" }
            : language switch { "cs" => "pracovní den", "en" => "working day", _ => "pracovný deň" };
        return $"{HolidayRegions.Name(holiday.Region, language)} · {kind}{(holiday.Kind == HolidayKind.Other && language == "sk" && holiday.DayOff ? "" : " · " + off)} · {holiday.Source}";
    }

    private CalendarEntry Record(AgendaItem item, CalendarLayer layer, bool firstDay)
    {
        var time = firstDay ? item.Time?.StartTime ?? item.Task?.DueTime : null;
        var parts = new List<string>();
        if (item.Time is { } t)
        {
            parts.Add(t.StartTime is { } s ? s.ToString("HH:mm", CultureInfo.InvariantCulture) + (t.EndTime is { } e ? "–" + e.ToString("HH:mm", CultureInfo.InvariantCulture) : "") : L("celý deň", "celý den", "all day"));
            if (t.Length > 1)
            {
                parts.Add($"{t.Start:d. M.} – {t.Start.AddDays(t.Length - 1):d. M. yyyy}");
            }

            if (t.Repeat is { } r)
            {
                parts.Add(AgendaEditor.Repeats.FirstOrDefault(c => c.Value == r.Every)?.Label + (r.Interval > 1 ? $" ({r.Interval}×)" : ""));
            }
        }

        if (item.Task is { } task)
        {
            parts.Add(AgendaEditor.Priorities[Math.Clamp(task.Priority, 0, 2)].Label + " " + L("priorita", "priorita", "priority"));
        }

        if (item.Text.Length > 0)
        {
            parts.Add(item.Text.Length > 140 ? item.Text[..140] + "…" : item.Text);
        }

        return new CalendarEntry
        {
            Layer = layer,
            Title = item.Title.Length > 0 ? item.Title : item.Text.Split('\n')[0],
            Detail = string.Join(" · ", parts),
            Time = time,
            Color = CalendarColors.Of(layer, item.Color),
            Done = item.Task?.State == TaskState.Done,
            Record = item,
            OpenCommand = new RelayCommand(() => Edit(item, AgendaKind.Event)),
        };
    }

    // the detail of the chosen day: everything of it, in parts
    private void Detail()
    {
        var all = Collect(_selected, _selected).GetValueOrDefault(_selected) ?? [];
        var names = new List<string>();
        foreach (var (code, l) in new[] { ("SK", "sk"), ("CZ", "cs"), ("EN", "en") })
        {
            var on = l switch { "sk" => NamesSk, "cs" => NamesCs, _ => NamesEn };
            var list = NameDays.Of(_selected, l);
            if (on && On(CalendarLayer.NameDays))
            {
                names.Add($"{code}: " + (list.Count > 0 ? string.Join(", ", list) : NameDays.DayWithoutName(_selected, l) ?? "–"));
            }
        }

        DetailNames = names;
        CalendarSection? Section(string title, CalendarLayer color, params CalendarLayer[] layers)
        {
            var entries = all.Where(e => layers.Contains(e.Layer)).OrderBy(e => e.Time ?? TimeOnly.MinValue).ToList();
            return entries.Count == 0 ? null : new CalendarSection(title, CalendarColors.Of(color), entries);
        }

        DetailSections = new[]
        {
            Section(L("Moje", "Moje", "Mine"), CalendarLayer.Events, CalendarLayer.Events, CalendarLayer.Tasks, CalendarLayer.Notes),
            Section(L("Sviatky a pamätné dni", "Svátky a významné dny", "Holidays and memorial days"), CalendarLayer.Holidays, CalendarLayer.Holidays, CalendarLayer.Memorial),
            Section(L("Školský rok", "Školní rok", "School year"), CalendarLayer.School, CalendarLayer.School),
            Section(L("Premiéry filmov", "Premiéry filmů", "Film premieres"), CalendarLayer.FilmsDomestic, CalendarLayer.FilmsDomestic, CalendarLayer.FilmsWorld),
            Section(L("Premiéry seriálov", "Premiéry seriálů", "Series premieres"), CalendarLayer.SeriesDomestic, CalendarLayer.SeriesDomestic, CalendarLayer.SeriesWorld),
            Section(L("Novinky – Seriály", "Novinky – Seriály", "New episodes"), CalendarLayer.EpisodesWorld, CalendarLayer.EpisodesDomestic, CalendarLayer.EpisodesWorld),
        }.OfType<CalendarSection>().ToList();
        OnPropertyChanged(nameof(DetailIsEmpty));
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Culture) + text[1..];

    private static string L(string sk, string cs, string en) => Loc.Language switch { "cs" => cs, "en" => en, _ => sk };
}

/// <summary>
/// The premieres and episodes of the calendar: what Premiéry and Novinky keep on the disk (read once when the calendar is
/// first shown, again on Obnoviť) – nothing is asked from the sources here.
/// </summary>
internal sealed partial class CalendarSources(AppServices services)
{
    private PremiereArchive _archive = new();
    private IReadOnlyList<SeriesEpisode> _episodes = [];

    public bool Loaded { get; private set; }

    public async Task LoadAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var news = services.SeriesNews;
        var archive = await Task.Run(() => services.PremiereStore.Load().Classified(news.Classify));
        var kept = await news.ReadAsync(services.NewsSources, today, offline: true);
        var history = await Task.Run(() => news.History(today.AddDays(-SeriesNewsPageViewModel.RecentDays)));
        _archive = archive;
        _episodes = SeriesNews.Merge(history.Concat(kept.Episodes));
        Loaded = true;
    }

    /// <summary>The premieres (with their VOD and TV days) and episodes of the days, as entries of the calendar.</summary>
    public IEnumerable<(DateOnly Day, CalendarEntry Entry)> Between(DateOnly from, DateOnly to, bool onlyChosen, Action<CalendarLayer, string> open)
    {
        var marks = services.Marks;
        foreach (var film in _archive.Films)
        {
            var chosen = marks.Get(MarkStore.Film(film.TmdbId)).Chosen;
            if (onlyChosen && !chosen)
            {
                continue;
            }

            var layer = film.Region == NewsRegion.Domestic ? CalendarLayer.FilmsDomestic : CalendarLayer.FilmsWorld;
            var detail = string.Join(" · ", new[] { film.LocalTitle, film.PremiereNote, film.Genre }.Where(t => t.Length > 0));
            if (film.Premiere >= from && film.Premiere <= to)
            {
                yield return (film.Premiere, Entry(layer, film.Caption, detail, chosen, open));
            }

            foreach (Match match in Availability().Matches(film.Availability))
            {
                if (DateOnly.TryParseExact($"{match.Groups[2].Value}.{match.Groups[3].Value}.{match.Groups[4].Value}", "d.M.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
                    day >= from && day <= to)
                {
                    yield return (day, Entry(layer, $"{match.Groups[1].Value}: {film.Caption}", detail, chosen, open, film.Caption));
                }
            }
        }

        foreach (var premiere in _archive.Series)
        {
            var first = premiere.First;
            var chosen = marks.Get(MarkStore.Series(first)).Chosen;
            if (first.AirDate < from || first.AirDate > to || (onlyChosen && !chosen))
            {
                continue;
            }

            var layer = first.Region == NewsRegion.Domestic ? CalendarLayer.SeriesDomestic : CalendarLayer.SeriesWorld;
            var what = first.Season is null or 1 ? "nový seriál" : $"{first.Season}. séria";
            yield return (first.AirDate, Entry(layer, first.Caption, string.Join(" · ", new[] { what, first.Channel, first.Title != first.OriginalTitle ? first.Title : "" }.Where(t => t.Length > 0)), chosen, open));
        }

        foreach (var episode in _episodes)
        {
            if (episode.AirDate < from || episode.AirDate > to)
            {
                continue;
            }

            var chosen = marks.Get(MarkStore.Series(episode)).Chosen;
            if (onlyChosen && !chosen)
            {
                continue;
            }

            var layer = episode.Region == NewsRegion.Domestic ? CalendarLayer.EpisodesDomestic : CalendarLayer.EpisodesWorld;
            yield return (episode.AirDate, Entry(layer, (episode.Caption + " " + episode.Code).Trim(),
                string.Join(" · ", new[] { episode.Channel, episode.Title != episode.OriginalTitle ? episode.Title : "" }.Where(t => t.Length > 0)), chosen, open, episode.Caption));
        }
    }

    private static CalendarEntry Entry(CalendarLayer layer, string title, string detail, bool chosen, Action<CalendarLayer, string> open, string? find = null) => new()
    {
        Layer = layer,
        Title = (chosen ? "★ " : "") + title,
        Detail = detail,
        Chosen = chosen,
        Color = CalendarColors.Of(layer),
        OpenCommand = new RelayCommand(() => open(layer, find ?? title)),
    };

    // "VOD 12. 12. 2026 · TV 1. 3. 2027 (USA)"
    [GeneratedRegex(@"\b(VOD|TV) (\d{1,2})\. ?(\d{1,2})\. ?(\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex Availability();
}
