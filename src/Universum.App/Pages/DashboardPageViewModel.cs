using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Calendar;
using Universum.Core.Jobs;
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

    public DashboardPageViewModel(AppServices services, Shell.MainViewModel main)
    {
        _services = services;
        _main = main;
        RefreshWeatherCommand = new AsyncRelayCommand(() => RefreshWeatherAsync(force: true), () => !_weatherLoading && Place is not null);
        OpenSettingsCommand = new RelayCommand(() => _main.Open(_main.Settings));
        OpenWeatherSiteCommand = new RelayCommand(() => OpenInBrowser(OpenMeteo.Website));
        AddToMuxCommand = new AsyncRelayCommand(async () =>
        {
            _main.Open(_main.Mux);
            await _main.Mux.AddFilesCommand.ExecuteAsync(null);
        });
        BatchFolderCommand = new RelayCommand(() =>
        {
            _main.Open(_main.Batch);
            if (_main.Batch.ChooseFolderCommand.CanExecute(null))
            {
                _main.Batch.ChooseFolderCommand.Execute(null);
            }
        });
        SingleFileCommand = new AsyncRelayCommand(async () =>
        {
            _main.Open(_main.Rules);
            await _main.Rules.OpenCommand.ExecuteAsync(null);
        });
        InfoFileCommand = new AsyncRelayCommand(async () =>
        {
            _main.Open(_main.Info);
            await _main.Info.OpenCommand.ExecuteAsync(null);
        });

        Queue = new("", "Fronta úloh", () => _main.Open(_main.Queue));
        Mux = new("", "Zlučovanie", () => _main.Open(_main.Mux));
        Batch = new("", "Hromadné spracovanie", () => _main.Open(_main.Batch));
        Library = new("", "Knižnica", () => _main.Open(_main.Settings));
        Titles = new("", "Názvy titulov", () => _main.Open(_main.Settings));
        Tools = new("", "MKVToolNix a aktualizácie", () => _ = _main.ShowUpdatesCommand.ExecuteAsync(null));
        Status = [Queue, Mux, Batch, Library, Titles, Tools];

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
        services.Jobs.Changed += () => _ = CountLibraryAsync();

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _clock.Start();
        RefreshStatus();
    }

    // --- date, time, name day

    public string Greeting => _now.Hour switch
    {
        < 5 => "Dobrú noc",
        < 10 => "Dobré ráno",
        < 18 => "Dobrý deň",
        < 22 => "Dobrý večer",
        _ => "Dobrú noc",
    };

    public string TimeText => _now.ToString("HH:mm", Slovak);

    public string DateText => Capitalized(SlovakCalendar.LongDate(Today));

    public string NameDayText => SlovakCalendar.NameDayText(Today);

    public string TomorrowText => SlovakCalendar.NameDay(Today.AddDays(1)) switch
    {
        { Count: 0 } => "Zajtra: " + (SlovakCalendar.DayWithoutName(Today.AddDays(1)) ?? ""),
        { Count: 1 } names => "Zajtra má meniny " + names[0],
        var names => "Zajtra majú meniny " + string.Join(", ", names),
    };

    public string DayInfo =>
        $"{ISOWeek.GetWeekOfYear(_now)}. týždeň · {_now.DayOfYear}. deň roka · do konca roka {(new DateTime(_now.Year, 12, 31) - _now.Date).Days} dní";

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
            w.FeelsLike is { } feels ? "pocitovo " + Degrees(feels) : null,
            w.Wind is { } wind ? $"vietor {Whole(wind)} km/h" : null,
        }.OfType<string>());

    public IReadOnlyList<WeatherDayRow> WeatherDays => _weather?.Days.Select(day => new WeatherDayRow(
        DayLabel(day.Date),
        OpenMeteo.Symbol(day.Code),
        $"{Whole(day.Max)}° / {Whole(day.Min)}°",
        day.PrecipitationChance is { } chance ? $"zrážky {chance} %" : "",
        OpenMeteo.Describe(day.Code))).ToList() ?? [];

    /// <summary>No town, loading, a problem, or when the weather was read.</summary>
    public string WeatherMessage => Place is null
        ? "Zadajte mesto v Nastaveniach (karta Počasie) – bez neho sa počasie nezisťuje."
        : _weatherLoading ? "Zisťujem počasie…"
        : _weatherMessage.Length > 0 ? _weatherMessage
        : _weather is { } w ? "Aktualizované o " + w.TakenUtc.ToLocalTime().ToString("HH:mm", Slovak)
        : "";

    public string WeatherAttribution => OpenMeteo.Attribution;

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

    public RelayCommand BatchFolderCommand { get; }

    public AsyncRelayCommand SingleFileCommand { get; }

    public AsyncRelayCommand InfoFileCommand { get; }

    /// <summary>The page is shown: the overview now, the weather when it is old, the library counted again.</summary>
    public void Shown()
    {
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
                _weatherMessage = ex.Message + (_weather is { } shown ? $" (zobrazené je počasie z {shown.TakenUtc.ToLocalTime():HH:mm})" : "");
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
        Queue.Text = (jobs.Running is { } running ? $"Beží: {running.Description}" + (jobs.WaitingCount > 0 ? $" · čaká {jobs.WaitingCount}" : "")
                      : jobs.WaitingCount > 0 ? $"Čaká úloh: {jobs.WaitingCount}"
                      : "Prázdna") +
                     (failed > 0 ? $" · zlyhalo: {failed}" : "");
        Queue.Attention = failed > 0;

        var open = _main.Mux.Settings.Count(s => s.SourceRows.Count > 0);
        var unprocessed = _main.Mux.Unprocessed.Count;
        Mux.Text = open == 0 ? "Žiadne súbory" : $"Nastavenia so súbormi: {open}" + (unprocessed > 0 ? $" · nie sú vo fronte: {unprocessed}" : "");
        Mux.Attention = false;

        var unverified = _main.Batch.FileRows.Count(f => f.IsUnverified);
        Batch.Text = _main.Batch.IsRunning ? "Beží spracovanie priečinka"
            : _main.Batch.FileRows.Count > 0 ? $"{Path.GetFileName(_main.Batch.Folder?.TrimEnd('\\', '/')) ?? ""}: súborov {_main.Batch.FileRows.Count}" +
                                               (unverified > 0 ? $" · titulov na potvrdenie: {unverified}" : "")
            : "Nie je otvorený žiadny priečinok";
        Batch.Attention = unverified > 0;

        Library.Text = _services.Library is { } library
            ? library.Root + (_libraryCounts is { } counts ? $" · filmy: {counts.Movies} · seriály: {counts.Shows}" : "")
            : "Nie je nastavená – výstupný priečinok zvolíte v Nastaveniach";
        Library.Attention = _services.Library is null;

        Titles.Text = string.Join(" · ",
            "TMDB: " + (_services.Tmdb is not null ? "zapnuté" : _services.TmdbKey is null ? "bez kľúča" : "vypnuté"),
            "HISTRA: " + (_services.Histra is not null ? "zapnutá" : _services.HistraToken is null ? "bez tokenu" : "vypnutá"));

        Tools.Text = _main.MkvToolNixStatus + " · " + (_main.HasUpdate ? _main.UpdateBadge : _services.Updates.LastReport is null ? "aktualizácie sa ešte nekontrolovali" : "všetko je aktuálne");
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

    private async Task CountLibraryAsync()
    {
        if (_services.Library is not { } library)
        {
            _libraryCounts = null;
            RefreshStatus();
            return;
        }

        _libraryCounts = await Task.Run(() => (Count(library.Movies), Count(library.Shows)));
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
        date == Today ? "Dnes" : date == Today.AddDays(1) ? "Zajtra" : Capitalized(date.ToString("dddd", Slovak));

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
