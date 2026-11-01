using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Calendar;
using Universum.Core.Jobs;
using Universum.Core.News;
using Universum.Core.Weather;

namespace Universum.App.Pages;

/// <summary>A day of the forecast on the Dashboard.</summary>
public sealed record WeatherDayRow(string Label, string Symbol, string Temperatures, string Rain, string Description);

/// <summary>A line of the UNIVERSUM overview: what a tool is doing now; the button opens it.</summary>
public sealed class DashboardStatusRow(string glyph, string title, Action open) : ObservableObject
{
    private string _text = "";
    private bool _attention;

    public string Glyph { get; } = glyph;

    public string Title { get; } = title;

    public string Text { get => _text; set => SetProperty(ref _text, value); }

    /// <summary>Something waits for the user (an error, a title to confirm, an update).</summary>
    public bool Attention { get => _attention; set => SetProperty(ref _attention, value); }

    public RelayCommand OpenCommand { get; } = new(open);
}

/// <summary>
/// The Dashboard: date, time, the Slovak name day (today and tomorrow), the weather of the town set in the settings
/// (Open-Meteo, CC BY 4.0 – named on the page), what UNIVERSUM is doing and quick starts of the tools.
/// </summary>
public sealed class DashboardPageViewModel : ObservableObject
{
    private static readonly CultureInfo Slovak = CultureInfo.GetCultureInfo("sk-SK");
    private static readonly TimeSpan WeatherAge = TimeSpan.FromMinutes(30);

    private readonly AppServices _services;
    private readonly Shell.MainViewModel _main;
    private readonly DispatcherTimer _clock;
    private DateTime _now = DateTime.Now;
    private WeatherReport? _weather;
    private string _weatherMessage = "";
    private bool _weatherLoading;
    private int _weatherVersion;
    private int _ticks;
    private (int Movies, int Shows)? _libraryCounts;
    private bool _counting;
    private bool _countAgain;

    public DashboardPageViewModel(AppServices services, Shell.MainViewModel main)
    {
        _services = services;
        _main = main;
        RefreshWeatherCommand = new AsyncRelayCommand(() => RefreshWeatherAsync(force: true), () => !_weatherLoading && Place is not null);
        OpenSettingsCommand = new RelayCommand(() => _main.Open(_main.Settings));
        OpenWeatherSiteCommand = new RelayCommand(() => OpenInBrowser(OpenMeteo.Website));
        // the tool opens; its own command runs only when the tool can take it now (not while it is working)
        AddToMuxCommand = new AsyncRelayCommand(() => OpenAndRunAsync(_main.Mux, _main.Mux.AddFilesCommand));
        BatchFolderCommand = new AsyncRelayCommand(() => OpenAndRunAsync(_main.Batch, _main.Batch.ChooseFolderCommand));
        SingleFileCommand = new AsyncRelayCommand(() => OpenAndRunAsync(_main.Rules, _main.Rules.OpenCommand));
        InfoFileCommand = new AsyncRelayCommand(() => OpenAndRunAsync(_main.Info, _main.Info.OpenCommand));

        Queue = new("", Loc.T("Fronta úloh"), () => _main.Open(_main.Queue));
        Mux = new("", Loc.T("Zlučovanie"), () => _main.Open(_main.Mux));
        Batch = new("", Loc.T("Hromadné spracovanie"), () => _main.Open(_main.Batch));
        Library = new("", Loc.T("Knižnica"), () => _main.Open(_main.Settings));
        Titles = new("", Loc.T("Názvy titulov"), () => _main.Open(_main.Settings));
        Tools = new("", Loc.T("MKVToolNix a aktualizácie"), () => _ = _main.ShowUpdatesCommand.ExecuteAsync(null));
        Status = [Queue, Mux, Batch, Library, Titles, Tools];
        Week = new WeekOverview(services, () => _main.Open(_main.SeriesNews), () => _main.Open(_main.Premieres), found => _main.ProcessFoundAsync(found));
        Agenda = new AgendaOverview(services, main);

        services.WeatherChanged += () =>
        {
            // a reading for the old town still on the way is dropped
            _weatherVersion++;
            _weatherLoading = false;
            _weather = null;
            _weatherMessage = "";
            OnWeatherChanged();
            _ = RefreshWeatherAsync(force: true);
        };
        services.LibraryChanged += () => _ = CountLibraryAsync();

        // the library is counted again when a job ended (job events come from other threads)
        services.Jobs.JobChanged += job =>
        {
            if (job.IsFinished)
            {
                Jobs.JobRow.OnUi(() => _ = CountLibraryAsync());
            }
        };

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _clock.Start();
        RefreshStatus();
    }

    // --- date, time, name day

    public string Greeting => _now.Hour switch
    {
        < 5 => Loc.T("Dobrú noc"),
        < 10 => Loc.T("Dobré ráno"),
        < 18 => Loc.T("Dobrý deň"),
        < 22 => Loc.T("Dobrý večer"),
        _ => Loc.T("Dobrú noc"),
    };

    public string TimeText => _now.ToString("HH:mm", Slovak);

    public string DateText => Capitalized(CalendarTexts.LongDate(Today, Language));

    /// <summary>The name day of today in the language of the interface (owner, 31. 10. 2026: UNIVERSUM's own calendar of names).</summary>
    public string NameDayText => NameDays.Text(Today, Language);

    public string TomorrowText
    {
        get
        {
            var tomorrow = Today.AddDays(1);
            var names = NameDays.Of(tomorrow, Language);
            return (Language, names.Count) switch
            {
                (_, 0) => (Language switch { "cs" => "Zítra: ", "en" => "Tomorrow: ", _ => "Zajtra: " }) + (NameDays.DayWithoutName(tomorrow, Language) ?? "–"),
                ("cs", 1) => "Zítra má svátek " + names[0],
                ("cs", _) => "Zítra mají svátek " + string.Join(", ", names),
                ("en", _) => "Tomorrow: " + string.Join(", ", names),
                (_, 1) => "Zajtra má meniny " + names[0],
                _ => "Zajtra majú meniny " + string.Join(", ", names),
            };
        }
    }

    // the language of the names: the language of the interface
    private static string Language => Localization.Loc.Language;

    public string DayInfo => Loc.F("{0}. týždeň · {1}. deň roka · ", ISOWeek.GetWeekOfYear(_now), _now.DayOfYear) +
                             (new DateTime(_now.Year, 12, 31) - _now.Date).Days switch
                             {
                                 0 => Loc.T("posledný deň roka"),
                                 1 => Loc.T("do konca roka 1 deň"),
                                 var left and < 5 => Loc.F("do konca roka {0} dni", left),
                                 var left => Loc.F("do konca roka {0} dní", left),
                             };

    private DateOnly Today => DateOnly.FromDateTime(_now);

    // --- weather

    public WeatherPlace? Place => _services.Settings.WeatherPlace;

    public bool HasPlace => Place is not null;

    public bool HasWeather => _weather is not null;

    public string PlaceText => Place?.Display ?? "";

    public string WeatherSymbol => _weather is { } w ? OpenMeteo.Symbol(w.Code, w.IsDay) : "";

    public string TemperatureText => _weather is { } w ? Degrees(w.Temperature) : "";

    public string WeatherDescription => _weather is { } w ? Capitalized(w.Description) : "";

    public string WeatherDetail => _weather is not { } w
        ? ""
        : string.Join(" · ", new[]
        {
            w.FeelsLike is { } feels ? Loc.F("pocitovo {0}", Degrees(feels)) : null,
            w.Wind is { } wind ? Loc.F("vietor {0} km/h", Whole(wind)) : null,
        }.OfType<string>());

    // days already over (a forecast read before midnight) are not shown
    public IReadOnlyList<WeatherDayRow> WeatherDays => _weather?.Days.Where(day => day.Date >= Today).Select(day => new WeatherDayRow(
        DayLabel(day.Date),
        OpenMeteo.Symbol(day.Code),
        $"{Whole(day.Max)}° / {Whole(day.Min)}°",
        day.PrecipitationChance is { } chance ? Loc.F("zrážky {0} %", chance) : "",
        OpenMeteo.Describe(day.Code))).ToList() ?? [];

    /// <summary>No town, loading, a problem, or when the weather was read.</summary>
    public string WeatherMessage => Place is null
        ? Loc.T("Zadajte mesto v Nastaveniach (karta Počasie) – bez neho sa počasie nezisťuje.")
        : _weatherLoading ? Loc.T("Zisťujem počasie…")
        : _weatherMessage.Length > 0 ? _weatherMessage
        : _weather is { } w ? Loc.F("Aktualizované o {0}", w.TakenUtc.ToLocalTime().ToString("HH:mm", Slovak))
        : "";

    public string WeatherAttribution => Loc.T(OpenMeteo.Attribution);

    public AsyncRelayCommand RefreshWeatherCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    public RelayCommand OpenWeatherSiteCommand { get; }

    // --- UNIVERSUM

    public IReadOnlyList<DashboardStatusRow> Status { get; }

    public DashboardStatusRow Queue { get; }

    public DashboardStatusRow Mux { get; }

    public DashboardStatusRow Batch { get; }

    public DashboardStatusRow Library { get; }

    public DashboardStatusRow Titles { get; }

    public DashboardStatusRow Tools { get; }

    public AsyncRelayCommand AddToMuxCommand { get; }

    public AsyncRelayCommand BatchFolderCommand { get; }

    public AsyncRelayCommand SingleFileCommand { get; }

    public AsyncRelayCommand InfoFileCommand { get; }

    /// <summary>The episodes and films of this week.</summary>
    public WeekOverview Week { get; }

    /// <summary>POZNÁMKY: the coming week of the calendar, the open tasks, the pinned notes.</summary>
    public AgendaOverview Agenda { get; }

    /// <summary>The page is shown: the overview now, the weather when it is old, the library counted again.</summary>
    public void Shown()
    {
        _ = Week.RefreshAsync();
        Agenda.Shown();
        _ = _services.InputWatch.ScanAsync();
        RefreshStatus();
        _ = CountLibraryAsync();
        _ = RefreshWeatherAsync(force: false);
    }

    /// <summary>Reads the weather (when it is older than half an hour, or always with <paramref name="force"/>).</summary>
    public async Task RefreshWeatherAsync(bool force)
    {
        if (Place is not { } place || _weatherLoading || (!force && _weather is { } old && DateTime.UtcNow - old.TakenUtc < WeatherAge))
        {
            return;
        }

        var version = ++_weatherVersion;
        _weatherLoading = true;
        OnWeatherChanged();
        try
        {
            var report = await _services.Weather.ForecastAsync(place.Latitude, place.Longitude);
            if (version == _weatherVersion && Place == place)
            {
                _weather = report;
                _weatherMessage = "";
            }
        }
        catch (WeatherException ex)
        {
            if (version == _weatherVersion)
            {
                // the last weather stays shown with its time; the message says why it is not newer
                _weatherMessage = ex.Message + (_weather is { } shown ? Loc.F(" (zobrazené je počasie z {0:HH:mm})", shown.TakenUtc.ToLocalTime()) : "");
            }
        }
        finally
        {
            if (version == _weatherVersion)
            {
                _weatherLoading = false;
            }

            OnWeatherChanged();
        }
    }

    /// <summary>The overview of the tools as they are now.</summary>
    public void RefreshStatus()
    {
        var jobs = _services.Jobs;
        var failed = jobs.Jobs.Count(j => j.Status == JobStatus.Failed);
        Queue.Text = (jobs.Running is { } running ? Loc.F("Beží: {0}", running.Description) + (jobs.WaitingCount > 0 ? Loc.F(" · čaká {0}", jobs.WaitingCount) : "")
                      : jobs.WaitingCount > 0 ? Loc.F("Čaká úloh: {0}", jobs.WaitingCount)
                      : Loc.T("Prázdna")) +
                     (failed > 0 ? Loc.F(" · zlyhalo: {0}", failed) : "");
        Queue.Attention = failed > 0;

        var open = _main.Mux.Settings.Count(s => s.SourceRows.Count > 0);
        var unprocessed = _main.Mux.Unprocessed.Count;
        Mux.Text = open == 0 ? Loc.T("Žiadne súbory") : Loc.F("Nastavenia so súbormi: {0}", open) + (unprocessed > 0 ? Loc.F(" · nie sú vo fronte: {0}", unprocessed) : "");
        Mux.Attention = false;

        var unverified = _main.Batch.FileRows.Count(f => f.IsUnverified);
        Batch.Text = _main.Batch.IsRunning ? Loc.T("Beží spracovanie priečinka")
            : _main.Batch.FileRows.Count > 0 ? Loc.F("{0}: súborov {1}", Path.GetFileName(_main.Batch.Folder?.TrimEnd('\\', '/')) ?? "", _main.Batch.FileRows.Count) +
                                               (unverified > 0 ? Loc.F(" · titulov na potvrdenie: {0}", unverified) : "")
            : Loc.T("Nie je otvorený žiadny priečinok");
        Batch.Attention = unverified > 0;

        Library.Text = _services.Library is { } library
            ? library.Root + (_libraryCounts is { } counts ? Loc.F(" · filmy: {0} · seriály: {1}", counts.Movies, counts.Shows) : "")
            : Loc.T("Nie je nastavená – výstupný priečinok zvolíte v Nastaveniach");
        Library.Attention = _services.Library is null;

        Titles.Text = string.Join(" · ",
            "TMDB: " + (_services.Tmdb is not null ? Loc.T("zapnuté") : _services.TmdbKey is null ? Loc.T("bez kľúča") : Loc.T("vypnuté")),
            "HISTRA: " + (_services.Histra is not null ? Loc.T("zapnutá") : _services.HistraToken is null ? Loc.T("bez tokenu") : Loc.T("vypnutá")));

        Tools.Text = _main.MkvToolNixStatus + " · " + (_main.HasUpdate ? _main.UpdateBadge : _services.Updates.LastReport is null ? Loc.T("aktualizácie sa ešte nekontrolovali") : Loc.T("všetko je aktuálne"));
        Tools.Attention = _main.HasUpdate || _services.CurrentMkvToolNix is not { IsSupportedVersion: true };
    }

    private void Tick()
    {
        var before = _now;
        _now = DateTime.Now;
        if (before.Ticks / TimeSpan.TicksPerMinute != _now.Ticks / TimeSpan.TicksPerMinute)
        {
            OnPropertyChanged(nameof(TimeText));
            OnPropertyChanged(nameof(Greeting));
        }

        if (before.Date != _now.Date)
        {
            OnPropertyChanged(nameof(DateText));
            OnPropertyChanged(nameof(NameDayText));
            OnPropertyChanged(nameof(TomorrowText));
            OnPropertyChanged(nameof(DayInfo));
            OnPropertyChanged(nameof(WeatherDays));

            // a new day: the forecast for it, the days of the calendar from it
            _ = RefreshWeatherAsync(force: true);
            Agenda.Refresh();
        }

        // the overview every 5 seconds, the weather every half hour (only with a town)
        if (++_ticks % 5 == 0)
        {
            RefreshStatus();
        }

        if (_ticks % 60 == 0)
        {
            _ = RefreshWeatherAsync(force: false);
        }
    }

    // one count at a time (on the UI thread); a request during a count counts once more afterwards
    private async Task CountLibraryAsync()
    {
        if (_counting)
        {
            _countAgain = true;
            return;
        }

        _counting = true;
        try
        {
            do
            {
                _countAgain = false;
                _libraryCounts = _services.Library is { } library
                    ? await Task.Run(() => (Count(library.Movies), Count(library.Shows)))
                    : null;
            }
            while (_countAgain);
        }
        finally
        {
            _counting = false;
        }

        RefreshStatus();

        static int Count(string folder)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder).Count() : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    private async Task OpenAndRunAsync(object page, System.Windows.Input.ICommand command)
    {
        _main.Open(page);
        if (!command.CanExecute(null))
        {
            return;
        }

        if (command is IAsyncRelayCommand asynchronous)
        {
            await asynchronous.ExecuteAsync(null);
        }
        else
        {
            command.Execute(null);
        }
    }

    private void OnWeatherChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(Place), nameof(HasPlace), nameof(HasWeather), nameof(PlaceText), nameof(WeatherSymbol), nameof(TemperatureText),
                     nameof(WeatherDescription), nameof(WeatherDetail), nameof(WeatherDays), nameof(WeatherMessage),
                 })
        {
            OnPropertyChanged(name);
        }

        RefreshWeatherCommand.NotifyCanExecuteChanged();
    }

    private string DayLabel(DateOnly date) =>
        date == Today ? Loc.T("Dnes") : date == Today.AddDays(1) ? Loc.T("Zajtra") : Capitalized(date.ToString("dddd", CalendarTexts.Culture(Loc.Language)));

    private static string Degrees(double value) => $"{Whole(value)} °C";

    // whole degrees with a real minus sign, never "-0"
    private static string Whole(double value) => ((int)Math.Round(value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture).Replace("-", "−", StringComparison.Ordinal);

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Slovak) + text[1..];

    private static void OpenInBrowser(Uri address)
    {
        if (address.Scheme == Uri.UriSchemeHttps)
        {
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
    }
}

/// <summary>A line of the week's overview: the day, the title and what comes.</summary>
public sealed record WeekRow(string Day, string Title, string Detail, bool Chosen, bool IsToday);

/// <summary>A chosen title on a streaming service, on the Dashboard.</summary>
public sealed record OfferRow(string Title, string Where, bool IsNew);

/// <summary>A chosen title found in the input folder, on the Dashboard; the button sends it to processing.</summary>
public sealed class FoundRow(FoundInFolder found, Func<FoundInFolder, Task> process)
{
    public string Title => found.Title;

    public string Detail => found.Detail;

    public string Files => string.Join("\n", found.Files.Select(System.IO.Path.GetFileName));

    public AsyncRelayCommand ProcessCommand { get; } = new(() => process(found));
}

/// <summary>
/// The Dashboard's week (owner, 29. 10. 2026): which episodes of which series come this week and on which day – of the
/// chosen series (Vybrané) and the premieres of new series and seasons – and the films premiering this week; where
/// the chosen titles can be watched legally; and the chosen titles already in the input folder. From what Premiéry,
/// Novinky and the checks have kept, nothing is asked here.
/// </summary>
public sealed class WeekOverview : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<FoundInFolder, Task> _process;
    private IReadOnlyList<WeekRow> _episodes = [];
    private IReadOnlyList<WeekRow> _films = [];
    private IReadOnlyList<OfferRow> _offers = [];
    private IReadOnlyList<FoundRow> _found = [];
    private string _watchStatus = "";

    public WeekOverview(AppServices services, Action openNews, Action openPremieres, Func<FoundInFolder, Task> process)
    {
        _services = services;
        _process = process;
        OpenNewsCommand = new(openNews);
        OpenPremieresCommand = new(openPremieres);
        CheckWatchCommand = new AsyncRelayCommand(async () =>
        {
            WatchStatus = Loc.T("Zisťujem, kde sa dajú vybrané tituly pozerať…");
            WatchStatus = await _services.Watchlist.CheckAsync() ?? Loc.F("Skontrolované {0:H:mm}.", DateTime.Now);
        });
        _services.Watchlist.Changed += ShowOffers;
        _services.InputWatch.Changed += ShowFound;
        ShowOffers();
    }

    public IReadOnlyList<OfferRow> Offers { get => _offers; private set => SetProperty(ref _offers, value); }

    public bool NoOffers => Offers.Count == 0;

    public IReadOnlyList<FoundRow> Found { get => _found; private set => SetProperty(ref _found, value); }

    public bool HasFound => Found.Count > 0;

    public string WatchStatus { get => _watchStatus; private set => SetProperty(ref _watchStatus, value); }

    public AsyncRelayCommand CheckWatchCommand { get; }

    public string WatchAttribution => Loc.T(WatchProviders.Attribution);

    private void ShowOffers()
    {
        Offers = _services.Watchlist.Offered().Select(o => new OfferRow(o.Title.Caption, o.Title.Text, o.IsNew)).ToList();
        OnPropertyChanged(nameof(NoOffers));
    }

    private void ShowFound()
    {
        Found = _services.InputWatch.Found.Select(f => new FoundRow(f, _process)).ToList();
        OnPropertyChanged(nameof(HasFound));
    }

    public IReadOnlyList<WeekRow> Episodes { get => _episodes; private set => SetProperty(ref _episodes, value); }

    public IReadOnlyList<WeekRow> Films { get => _films; private set => SetProperty(ref _films, value); }

    public bool NoEpisodes => Episodes.Count == 0;

    public bool NoFilms => Films.Count == 0;

    public RelayCommand OpenNewsCommand { get; }

    public RelayCommand OpenPremieresCommand { get; }

    public async Task RefreshAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var last = today.AddDays(6);
        var marks = _services.Marks;
        var archive = await Task.Run(_services.PremiereStore.Load);
        Films = Universum.Core.News.FilmPremieres.Classify(archive.Films).Where(f => f.Premiere >= today && f.Premiere <= last)
            .GroupBy(f => f.TmdbId).Select(g => g.OrderBy(f => f.Region).First())
            .Select(f => (Film: f, Chosen: marks.Get(Universum.Core.News.MarkStore.Film(f.TmdbId)).Chosen))
            .OrderBy(f => f.Film.Premiere).ThenByDescending(f => f.Chosen).ThenByDescending(f => f.Film.Popularity).Take(14)
            .Select(f => new WeekRow(Day(f.Film.Premiere), f.Film.Caption,
                f.Film.Region == Universum.Core.News.NewsRegion.Domestic ? Loc.F("kino {0}", f.Film.PremiereNote)
                    : f.Film.PremiereNote.Length > 0 ? Loc.F("svetová premiéra · {0}", f.Film.PremiereNote) : Loc.T("svetová premiéra"), f.Chosen, f.Film.Premiere == today))
            .ToList();

        var schedule = await _services.SeriesNews.ReadAsync(_services.NewsSources, today, offline: true);
        Episodes = schedule.Episodes.Where(e => e.AirDate >= today && e.AirDate <= last)
            .Select(e => (Episode: e, Chosen: marks.Get(Universum.Core.News.MarkStore.Series(e)).Chosen))
            .Where(e => e.Chosen || e.Episode.IsPremiere)
            .OrderBy(e => e.Episode.AirDate).ThenByDescending(e => e.Chosen).ThenBy(e => e.Episode.Caption, StringComparer.CurrentCulture).Take(30)
            .Select(e => new WeekRow(Day(e.Episode.AirDate), e.Episode.Caption,
                string.Join(" · ", new[] { e.Episode.Code, e.Episode.Kind switch
                {
                    Universum.Core.News.EpisodeKind.SeriesPremiere => Loc.T("nový seriál"),
                    Universum.Core.News.EpisodeKind.SeasonPremiere => Loc.T("nová séria"),
                    _ => "",
                }, e.Episode.Channel }.Where(t => t.Length > 0)), e.Chosen, e.Episode.AirDate == today))
            .ToList();
        OnPropertyChanged(nameof(NoEpisodes));
        OnPropertyChanged(nameof(NoFilms));
    }

    // the week's films and episodes: "nedeľa 1. 11." in the language of the window
    private static string Day(DateOnly day) => Loc.Language == "sk"
        ? $"{Universum.Core.News.Periodicity.DayName(day)} {day.Day}. {day.Month}."
        : day.ToString(Loc.Language == "en" ? "dddd d MMM" : "dddd d. M.", CalendarTexts.Culture(Loc.Language));
}
