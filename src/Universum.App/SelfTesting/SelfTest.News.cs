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

// the self-test: Prehľad, Premiéry, Zaujímavosti, Novinky
internal static partial class SelfTest
{
    /// <summary>Stand-in TMDB and HISTRA: every search answers with the invented results set for each service.</summary>
    /// <summary>
    /// Dashboard: date and name day, the weather of the town from the settings (invented Open-Meteo answers – only the
    /// town and its coordinates are asked for), the overview of the tools.
    /// </summary>
    private static async Task TestDashboardAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var dashboard = model.Dashboard;
        model.Open(dashboard);
        Check(dashboard.NameDayText.Length > 0 && dashboard.DateText.Length > 0, $"dashboard: {dashboard.DateText}, {dashboard.NameDayText}");
        Check(!dashboard.HasPlace && dashboard.WeatherMessage.StartsWith("Zadajte mesto", StringComparison.Ordinal), "dashboard: no weather without a town");
        var weather = new FakeWeather();
        services.WeatherForSelfTest = new Universum.Core.Weather.OpenMeteo(new System.Net.Http.HttpClient(weather));
        await Screenshot(window, "dashboard-no-town.png");

        model.Settings.WeatherTownInput = "Nikde";
        weather.Found = false;
        await model.Settings.FindWeatherTownCommand.ExecuteAsync(null);
        Check(services.Settings.WeatherPlace is null && model.Settings.WeatherStatus.Contains("nenašlo", StringComparison.Ordinal), "settings: a town Open-Meteo does not know is not stored");
        weather.Found = true;
        model.Settings.WeatherTownInput = "Vymyslené Mesto";
        await model.Settings.FindWeatherTownCommand.ExecuteAsync(null);
        Check(services.Settings.WeatherPlace is { Name: "Vymyslené Mesto", Latitude: 48.7 }, "settings: the town stored with its coordinates – " + model.Settings.WeatherStatus);

        // a new town is read at once by the Dashboard
        for (var i = 0; i < 50 && !dashboard.HasWeather; i++)
        {
            await Task.Delay(100);
        }

        Check(dashboard.HasWeather && dashboard.TemperatureText == "−3 °C" && dashboard.WeatherDescription == "Slabé sneženie" && dashboard.WeatherDays.Count == 3,
            $"dashboard: weather {dashboard.TemperatureText}, {dashboard.WeatherDescription}, days {dashboard.WeatherDays.Count}");
        Check(dashboard.WeatherDays[0].Label == "Dnes" && dashboard.WeatherDays[1].Label == "Zajtra" && dashboard.WeatherDays[1].Rain == "zrážky 40 %",
            "dashboard: the days of the forecast – " + string.Join(", ", dashboard.WeatherDays.Select(d => $"{d.Label} {d.Temperatures} {d.Rain}")));
        Check(weather.Requests.Count == 3 && weather.Requests.Take(2).All(r => r.StartsWith("https://geocoding-api.open-meteo.com/v1/search?", StringComparison.Ordinal)) &&
              weather.Requests[2].StartsWith("https://api.open-meteo.com/v1/forecast?latitude=48.7&longitude=19.15&", StringComparison.Ordinal),
            "dashboard: only the town, then its coordinates went to Open-Meteo – " + string.Join(" | ", weather.Requests));
        weather.Fails = true;
        await dashboard.RefreshWeatherAsync(force: true);
        Check(dashboard.HasWeather && dashboard.WeatherMessage.Contains("zobrazené je počasie z", StringComparison.Ordinal), "dashboard: a failed reading keeps the last weather – " + dashboard.WeatherMessage);
        weather.Fails = false;
        await dashboard.RefreshWeatherAsync(force: true);
        dashboard.RefreshStatus();
        Check(dashboard.Queue.Text.Length > 0 && dashboard.Tools.Text.Contains("MKVToolNix", StringComparison.Ordinal) && dashboard.Status.Count == 6,
            "dashboard: the overview – " + string.Join(" | ", dashboard.Status.Select(s => $"{s.Title}: {s.Text}")));
        model.Open(dashboard);
        await Screenshot(window, "dashboard.png");
        dashboard.Library.OpenCommand.Execute(null);
        Check(model.SelectedTool.Page == model.Settings, "dashboard: a line of the overview opens its tool");

        // several places of one name (Valaská, 27. 10.): offered with district, region and height; the chosen one is stored
        var settings = model.Settings;
        settings.WeatherTownInput = "Vymyslená Lehota";
        await settings.FindWeatherTownCommand.ExecuteAsync(null);
        Check(settings.HasWeatherChoices && settings.WeatherChoices.Count == 2 && services.Settings.WeatherPlace?.Name == "Vymyslené Mesto" &&
              settings.WeatherStatus.Contains("vyberte", StringComparison.Ordinal),
            "settings: several towns of one name are offered, nothing stored yet – " + string.Join(" | ", settings.WeatherChoices.Select(c => c.FullDisplay)));
        Check(settings.WeatherChoices[1].FullDisplay == "Vymyslená Lehota – okres Druhý, Banskobystrický kraj, Slovensko · 612 m n. m.",
            "settings: a town is told apart by its district, region and height – " + settings.WeatherChoices[1].FullDisplay);
        await Settle();
        window.UpdateLayout();
        var choices = FindVisual<System.Windows.Controls.ListBox>(window, list => ReferenceEquals(list.ItemsSource, settings.WeatherChoices));
        Check(choices is { HasItems: true, IsVisible: true }, "settings: the list of places is shown");
        choices?.BringIntoView(new Rect(0, 0, choices.ActualWidth, choices.ActualHeight + 120));

        await Screenshot(window, "settings-weather-choice.png");
        settings.SelectedWeatherChoice = settings.WeatherChoices[1];
        settings.UseWeatherChoiceCommand.Execute(null);
        Check(services.Settings.WeatherPlace is { District: "okres Druhý", Latitude: 48.9 } && !settings.HasWeatherChoices &&
              settings.WeatherStatus.StartsWith("Počasie pre: Vymyslená Lehota – okres Druhý", StringComparison.Ordinal) &&
              dashboard.PlaceText == "Vymyslená Lehota, okres Druhý",
            $"settings: the chosen town is stored and shown with its district – {settings.WeatherStatus} | {dashboard.PlaceText}");
    }

    /// <summary>
    /// Premiéry with invented sources (an invented page read by imported rules, TVmaze and TMDB answer here, nothing goes
    /// to the internet): the rules imported in the settings, the four tables, robots.txt asked first, only the days of
    /// the window read, periodicity and episode counts from TVmaze, and the kept days used again without requests.
    /// </summary>
    private static async Task TestPremieresAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var news = new FakeNews();
        var http = new System.Net.Http.HttpClient(news);
        var cache = Path.Combine(_outputDirectory, "news-cache");
        Core.News.WebPageClient.PagesPerSecond = 1000;
        services.SeriesNewsForSelfTest = new Core.News.SeriesNews(new Core.News.WebPageClient(http), new Core.News.TvMazeClient(http, new RequestPacer(1000)), new Core.News.NewsCache(cache));
        var tmdb = new Core.Library.Tmdb.TmdbClient(http, "selftest-key", null, new RequestPacer(1000));
        services.FilmsForSelfTest = new Core.News.FilmPremieres(tmdb);
        services.SeriesTmdbForSelfTest = new Core.News.SeriesTmdb(tmdb);
        services.PostersForSelfTest = new PosterCache(http, Path.Combine(_outputDirectory, "posters"), PosterCache.DefaultServer);

        var rulesFile = Path.Combine(_outputDirectory, "vymysleny-zdroj.json");
        File.WriteAllText(rulesFile, FakeNews.Rules);
        var sources = model.Settings.NewsSources;
        Check(sources.Rows.Count == 2 && sources.Rows[0].Name == "TVmaze" && sources.Rows[1].Name == "TMDB – filmy", "sources: TVmaze and TMDB by default");
        File.WriteAllText(rulesFile + ".bad", "{}");
        Check(!sources.ImportFile(rulesFile + ".bad") && sources.Status.StartsWith("Zdroj sa nepridal", StringComparison.Ordinal),
            "sources: a file that is no rules is refused – " + sources.Status);
        Check(sources.ImportFile(rulesFile) && sources.Rows.Count == 3 && sources.Rows[2].Address == "rozpis.example",
            "sources: an invented page added from its rules – " + sources.Status);
        model.SelectedTool = model.Tools.First(t => t.Page == model.Settings);
        await Screenshot(window, "settings-sources.png");

        var page = model.Premieres;
        model.Open(page);
        await page.LoadAsync(refresh: true);
        var films = page.Tab(PremiereTab.FilmDomestic).Rows.OfType<PremiereFilmRow>().ToList();
        var world = page.Tab(PremiereTab.FilmWorld).Rows.OfType<PremiereFilmRow>().ToList();
        var domesticSeries = page.Tab(PremiereTab.SeriesDomestic).Rows.OfType<PremiereSeriesRow>().ToList();
        var worldSeries = page.Tab(PremiereTab.SeriesWorld).Rows.OfType<PremiereSeriesRow>().ToList();
        Check(!page.HasProblems, "premieres: all sources read – " + page.Problems);
        Check(films.Count == 2 && films[0].IsToday && films[0].When == "DNES" && films[1].Caption == $"The Invented Premiere ({FakeNews.Day(3).Year})" &&
              films[1].Where == $"CZ · SK {FakeNews.Day(4).Day}. {FakeNews.Day(4).Month}." && films[1].Availability.Contains("VOD ", StringComparison.Ordinal) && films[1].Availability.StartsWith("kino ", StringComparison.Ordinal) &&
              films[1].Production == "ČR, SR · Invented Pictures" && films[1].When == "o 3 dni",
            "premieres: Czech and Slovak films in the cinemas here, the nearest first – " + string.Join(" | ", films.Select(f => $"{f.Caption} {f.Date} {f.When} {f.Where} {f.Production} {f.Availability}")));
        Check(world.Count == 1 && world[0].Caption.StartsWith("Later Invented Film", StringComparison.Ordinal),
            "premieres: only coming films of the world, the past one is in the history – " + string.Join(" | ", world.Select(f => $"{f.Caption} {f.Date}")));
        Check(domesticSeries.Count == 1 && domesticSeries[0].Caption == "Invented Show (2026)" && domesticSeries[0].LocalTitle == "Vymyslený seriál" &&
              domesticSeries[0].Production == "ČT1" && domesticSeries[0].Periodicity == "každý pracovný deň (po–pi)" && domesticSeries[0].Episodes == "10 epizód",
            "premieres: a new series on a Czech channel, its rhythm from TVmaze – " +
            string.Join(" | ", domesticSeries.Select(r => $"{r.Caption} {r.Production} {r.What} {r.Periodicity} {r.Episodes}")));
        Check(worldSeries.Count == 3 && worldSeries.Any(r => r.Caption == "Invented Stream (2024)" && r.What.StartsWith("3. séria", StringComparison.Ordinal) &&
                                                            r.Periodicity.StartsWith("raz do týždňa – ", StringComparison.Ordinal) && r.Episodes == "8 epizód"),
            "premieres: new series and seasons of the world – " + string.Join(" | ", worldSeries.Select(r => $"{r.Caption} {r.Production} {r.What} {r.Periodicity} {r.Episodes}")));
        var pages = news.Requests.Where(r => r.Contains("rozpis.example/den/", StringComparison.Ordinal)).ToList();
        Check(pages.Count == 1 + Core.News.SeriesNews.PageDaysAhead + 1 && pages.Distinct().Count() == pages.Count &&
              news.Requests.IndexOf("https://rozpis.example/robots.txt") < news.Requests.FindIndex(r => r.Contains("rozpis.example/den/", StringComparison.Ordinal)),
            $"premieres: robots.txt first, then each day of the window once – Czech channels {Core.News.SeriesNews.PageDaysAhead} days ahead ({pages.Count} days)");
        Check(news.Requests.All(r => r.StartsWith("https://", StringComparison.Ordinal) &&
                                     new Uri(r).Host is "rozpis.example" or "api.tvmaze.com" or "api.themoviedb.org" or "image.tmdb.org"),
            "premieres: only the sources were asked");
        Check(news.UserAgents.All(a => a.StartsWith("UNIVERSUM/", StringComparison.Ordinal)), "premieres: UNIVERSUM introduces itself to the pages");

        // marks: chosen and a note, kept; only the chosen ones
        films[1].IsChosen = true;
        films[1].Note = "  teším sa  ";
        page.OnlyChosen = true;
        Check(page.Tab(PremiereTab.FilmDomestic).Rows.Count == 1 && page.Tab(PremiereTab.FilmDomestic).Rows[0] == films[1] &&
              services.Marks.Get(Core.News.MarkStore.Film(films[1].Film.TmdbId)) == new Core.News.TitleMark(true, "teším sa"),
            "premieres: Zobraziť vybrané shows only the chosen film, its note kept");
        page.OnlyChosen = false;
        page.Filter = "teSim";
        Check(page.Tab(PremiereTab.FilmDomestic).Rows.Count == 1, "premieres: the note is searched too");
        page.Filter = "";
        // owner, 29. 10. 2026: a link is only a word beside the note's text, the address hides in it
        films[1].AddLink("Kino", new Uri("https://kino.example/vymyslena-premiera"));
        films[1].AddLink("  ", new Uri("https://www.trailer.example/vymyslena"));
        Check(films[1].NoteText == "teším sa" && string.Join(", ", films[1].Links.Select(l => l.Name)) == "Kino, trailer.example",
            "premieres: the note's text without addresses, its links as words – " + films[1].Note);
        films[1].Links[1].RemoveCommand.Execute(null);
        films[1].NoteText = "teším sa veľmi";
        Check(films[1].Note == "teším sa veľmi [Kino](https://kino.example/vymyslena-premiera)" && films[1].Links.Count == 1,
            "premieres: a link removed, the text changed, the other link kept – " + films[1].Note);
        Check(films[1].HasLinks && services.Marks.Get(Core.News.MarkStore.Film(films[1].Film.TmdbId)).Note.Contains("vymyslena-premiera", StringComparison.Ordinal),
            "premieres: a web address in the note is kept and shown as a link");
        page.PosterScale = 2;
        Check(page.PosterWidth == 76 && page.PosterHeight == 114 && services.Settings.PremierePosterScale == 2, "premieres: posters up to twice as large, kept");

        page.SelectedTab = page.Tab(PremiereTab.FilmDomestic);
        await WaitFor(() => films.All(f => f.Poster.Image is not null));
        await Screenshot(window, "premieres-films.png");
        page.PosterScale = 1;
        page.SelectedTab = page.Tab(PremiereTab.SeriesWorld);
        await WaitFor(() => worldSeries.All(r => r.Poster.Image is not null));
        Check(worldSeries.All(r => r.Poster.Image is not null), "premieres: posters of the series found on TMDB");
        await Screenshot(window, "premieres-series.png");
        page.ShowPosters = false;
        Check(!services.Settings.PremierePosters, "premieres: posters can be hidden (kept in the settings)");
        await Screenshot(window, "premieres-series-noposters.png");
        page.ShowPosters = true;

        page.ShowRecent = true;
        page.SelectedTab = page.Tab(PremiereTab.FilmWorld);
        Check(page.SelectedTab.Rows.Count == 1 && page.SelectedTab.Rows[0].Caption.StartsWith("World Invented Film", StringComparison.Ordinal) &&
              page.SelectedTab.Rows[0].When == "pred 2 dňami",
            "premieres: Posledný týždeň shows the premieres of the last days – " + string.Join(", ", page.SelectedTab.Rows.Select(r => $"{r.Caption} {r.When}")));
        page.ShowHistory = true;
        Check(page.SelectedTab.Rows.Count == 1 && page.SelectedTab.Rows[0] is PremiereFilmRow { Caption: var past } && past.StartsWith("World Invented Film", StringComparison.Ordinal),
            "premieres: the film that is over is in the history");
        await Screenshot(window, "premieres-history.png");
        page.ShowAll = true;
        var everything = page.SelectedTab.Rows;
        Check(everything.Count == page.SelectedTab.All.Count && everything.Any(r => r.SortDate < DateOnly.FromDateTime(DateTime.Now)) &&
              everything.Zip(everything.Skip(1)).All(p => p.First.SortDate >= p.Second.SortDate),
            $"premieres: Zobraziť všetko shows the history and the coming premieres, the latest first ({everything.Count} of {page.SelectedTab.All.Count})");
        page.ShowUpcoming = true;

        int Days() => news.Requests.Count(r => r.Contains("rozpis.example/den/", StringComparison.Ordinal) || r.Contains("/schedule", StringComparison.Ordinal));
        var asked = Days();
        await page.LoadAsync(refresh: false);
        Check(Days() == asked, $"premieres: the kept days are used again without asking the sources ({asked} → {Days()})");

        var requests = news.Requests.Count;
        var again = new PremieresPageViewModel(services);
        again.Shown();
        Check(news.Requests.Count == requests && again.Tab(PremiereTab.FilmDomestic).Rows.Count == 2 && again.Tab(PremiereTab.SeriesWorld).Rows.Count == 3 &&
              again.Status.StartsWith("Uložené premiéry z", StringComparison.Ordinal) && File.Exists(AppSettings.PremieresFile),
            "premieres: kept for the next start and shown at once, without asking the sources – " + again.Status);

        services.SeriesNewsForSelfTest = null;
        services.FilmsForSelfTest = null;
        services.SeriesTmdbForSelfTest = null;
        services.PostersForSelfTest = null;
        services.SetNewsSources(Core.News.NewsSource.Defaults);
    }

    /// <summary>
    /// Zaujímavosti with invented sources (a page naming its RSS feed, a Czech Atom feed, pictures) and an invented DeepL:
    /// sources added in the settings, articles read, translated once, kept for the next start, filtered and starred.
    /// </summary>
    private static async Task TestInterestsAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var feeds = new FakeFeeds();
        var http = new System.Net.Http.HttpClient(feeds);
        Core.News.WebPageClient.PagesPerSecond = 1000;
        services.InterestsForSelfTest = new Core.News.Interests(new Core.News.WebPageClient(http), new Core.News.InterestStore(AppSettings.InterestsFile));
        services.TranslatorForSelfTest = new Core.News.DeepLClient(http, "selftest-0000-0000-0000-000000000000:fx");
        services.ImagesForSelfTest = new Core.News.WebImageCache(http, Path.Combine(_outputDirectory, "images"));

        var settings = model.Settings.InterestSources;
        Check(settings.AddSource("Vymyslené filmy", "https://filmy.example/", "en") && !settings.AddSource("Zlý", "http://filmy.example/", "en"),
            "interests: a source added in the settings (https only) – " + settings.Status);
        var file = Path.Combine(_outputDirectory, "vymyslene-zdroje.json");
        File.WriteAllText(file, """{"format":"UNIVERSUM-zaujimavosti 1","sources":[{"name":"Vymyslené kino","url":"https://kino.example/atom","language":"cs"}]}""");
        Check(settings.ImportFile(file) && services.FeedSources.Count == 2 && settings.Rows.Count == 2, "interests: sources imported from a file – " + settings.Status);

        var page = model.Interests;
        model.Open(page);
        Check(page.IsEmpty, "interests: nothing kept yet");
        await page.RefreshAsync();
        Check(!page.HasProblems && page.Featured is { } featured && featured.Title == "SK: Invented festival opens" && page.Cards.Count == 2,
            $"interests: this week's articles, translated – {page.Featured?.Title} + {page.Cards.Count} | {page.Problems}");
        var translated = feeds.DeepL;
        Check(translated == 1 && page.Cards.All(c => c.Title.StartsWith("SK: ", StringComparison.Ordinal)), "interests: one batch to DeepL, Czech too");
        page.ShowAll = true;
        Check(page.Cards.Count == 3, "interests: Všetko shows the history");
        page.Source = "Vymyslené kino";
        Check(page.Featured?.Source == "Vymyslené kino" && page.Cards.Count == 0, "interests: only one source");
        page.Source = null;
        page.ShowOriginal = true;
        Check(page.Featured?.Title == "Invented festival opens", "interests: the original text");
        page.ShowOriginal = false;
        page.Featured!.FavoriteCommand.Execute(null);
        page.OnlyFavorites = true;
        Check(page.Featured?.IsFavorite == true && page.Cards.Count == 0, "interests: the starred article");
        page.OnlyFavorites = false;
        page.ShowWeek = true;
        await WaitFor(() => page.Featured?.Image is not null && page.Cards.All(c => !c.HasImage || c.Image is not null));
        Check(page.Featured?.Image is not null, "interests: pictures of the articles");
        await Screenshot(window, "zaujimavosti.png");

        await page.RefreshAsync();
        Check(feeds.DeepL == translated, "interests: nothing translated twice");
        var again = new InterestsPageViewModel(services);
        again.ShowAll = true;
        again.Shown();
        Check(again.Cards.Count + (again.Featured is null ? 0 : 1) == 4 && again.Status.StartsWith("Uložené zaujímavosti", StringComparison.Ordinal),
            "interests: kept for the next start");
        Check(feeds.Requests.All(r => new Uri(r).Host is "filmy.example" or "kino.example" or "api-free.deepl.com"), "interests: only the sources and DeepL were asked");
        model.SelectedTool = model.Tools.First(t => t.Page == model.Settings);
        await Screenshot(window, "settings-interests.png");

        services.SetFeedSources([]);
        services.InterestsForSelfTest = null;
        services.TranslatorForSelfTest = null;
        services.ImagesForSelfTest = null;
    }

    /// <summary>
    /// Novinky – Seriály with the invented sources: the input folder (one setting for all pages), one ticked episode
    /// found goes to Single spracovanie with its series, several episodes of a series go to Hromadné spracovanie (only
    /// those files), an episode not in the folder waits and is found by "Skontrolovať znova".
    /// </summary>
    private static async Task TestSeriesNewsAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var news = new FakeNews();
        var http = new System.Net.Http.HttpClient(news);
        // an old day kept from earlier: the history
        var cache = new Core.News.NewsCache(Path.Combine(_outputDirectory, "news-cache-2"));
        cache.Put("Vymyslený rozpis", FakeNews.Day(-20), [new Core.News.SeriesEpisode
        {
            Source = "Vymyslený rozpis", Region = Core.News.NewsRegion.Domestic, Title = "Starý seriál", OriginalTitle = "Starý seriál", Year = 2019,
            Season = 2, Number = 5, AirDate = FakeNews.Day(-20), Channel = "Nova",
        }]);
        services.SeriesNewsForSelfTest = new Core.News.SeriesNews(new Core.News.WebPageClient(http), new Core.News.TvMazeClient(http, new RequestPacer(1000)), cache);
        services.SeriesTmdbForSelfTest = new Core.News.SeriesTmdb(new Core.Library.Tmdb.TmdbClient(http, "selftest-key", null, new RequestPacer(1000)));
        services.PostersForSelfTest = new PosterCache(http, Path.Combine(_outputDirectory, "posters"), PosterCache.DefaultServer);
        services.SetNewsSources([.. Core.News.NewsSource.Defaults, new Core.News.NewsSource
        {
            Name = "Vymyslený rozpis", Kind = Core.News.NewsSourceKind.Page, Rules = Core.News.PageRules.Load(FakeNews.Rules),
        }]);

        // the input folder: the new Czech series and two days of a Czech series (copies of a test file)
        var input = Path.Combine(_outputDirectory, "vstup");
        var sample = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv");
        int Number(int offset) => 10 + FakeNews.Day(offset).DayNumber % 50;
        Directory.CreateDirectory(Path.Combine(input, "Invented Show"));
        File.Copy(sample, Path.Combine(input, "Invented Show", "Invented.Show.S01E01.1080p.WEB.mkv"), overwrite: true);
        File.Copy(sample, Path.Combine(input, $"Domaci serial S05E{Number(0):00}.mkv"), overwrite: true);
        File.Copy(sample, Path.Combine(input, $"Domaci.serial.S05E{Number(-1):00}.mkv"), overwrite: true);
        File.Copy(sample, Path.Combine(input, "Iny serial S05E01.mkv"), overwrite: true);
        var before = Directory.GetFiles(input, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);

        var page = model.SeriesNews;
        model.Open(page);
        await page.LoadAsync(refresh: true);
        var domestic = page.Tabs[0];
        Check(!page.HasProblems && string.Join(", ", domestic.Rows.Select(r => r.Caption)) == "Domáci seriál (2020), Invented Show (2026)",
            "novinky: only the series, alphabetically – " + string.Join(", ", domestic.Rows.Select(r => r.Caption)) + " " + page.Problems);
        var home = domestic.Rows[0];
        var show = domestic.Rows[1];
        Check(home.NearestCode == $"S05E{Number(0):00}" && home.When == "DNES" && home.Episodes.Count == 2 && home.Episodes.All(e => e.CanCheck && e.State == "odvysielaná") &&
              home.Rhythm == "každý deň",
            $"novinky: the last week – the nearest episode, the tree of the aired ones, the rhythm – {home.NearestCode} {home.When} {home.Episodes.Count} {home.Rhythm}");
        page.ShowUpcoming = true;
        var coming = domestic.Rows.First(r => r.Caption == "Domáci seriál (2020)");
        // the invented numbers repeat after 50 days (the same episode is shown once), so the far days are checked by date
        Check(coming.Episodes.Count >= 45 && coming.Episodes.Max(e => e.Episode.AirDate) >= FakeNews.Day(Core.News.SeriesNews.PageDaysAhead - 12) &&
              coming.Episodes[^1].State == "nadchádzajúca" && !coming.Episodes[^1].CanCheck,
            $"novinky: Nadchádzajúce – the episodes to come, {Core.News.SeriesNews.PageDaysAhead} days ahead ({coming.Episodes.Count})");
        page.ShowRecent = true;
        home = domestic.Rows[0];
        show = domestic.Rows[1];
        Check(show.NearestCode == "S01E01" && show.NearestNote.StartsWith("nový seriál", StringComparison.Ordinal), "novinky: a new series – " + show.NearestNote);

        services.InputFolder.Set(null);
        var premiere = show.Episodes[0];
        premiere.IsChecked = true;
        Check(page.SendText == "Odoslať na spracovanie (1)" && show.IsChecked && show.CheckedText == "1/1", "novinky: one episode ticked – " + page.SendText);
        await page.SendAsync();
        Check(page.Status.StartsWith("Najprv nastavte vstupný priečinok", StringComparison.Ordinal) && model.SelectedTool.Page == page,
            "novinky: without the input folder nothing is sent – " + page.Status);
        Check(!services.InputFolder.Set(Path.Combine(services.Library!.Root, "MOVIES")) && services.InputFolder.Message.Contains("MOVIES", StringComparison.Ordinal),
            "input folder: not in the library – " + services.InputFolder.Message);
        Check(services.InputFolder.Set(input) && services.Settings.InputDirectory == Path.GetFullPath(input) && model.Premieres.InputFolder.Folder == Path.GetFullPath(input),
            "input folder: one setting for every page");
        Check(page.Status.StartsWith("Vstupný priečinok je nastavený", StringComparison.Ordinal), "novinky: the old message goes once the folder is set – " + page.Status);

        // owner, 31. 10. 2026: the input folder watched – a film copied in is told (nothing is processed by itself)
        services.WatchSettle = TimeSpan.FromMilliseconds(500);
        services.RestartWatch();
        var arriving = Path.Combine(input, "Vymyslený.Prichádzajúci.Film.2026.mkv");
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"), arriving, overwrite: true);
        await WaitFor(() => services.Arrivals.Any(a => a.EndsWith("Prichádzajúci.Film.2026.mkv", StringComparison.Ordinal)));
        Check(model.HasArrivals && model.ArrivalsText.Contains("Prichádzajúci", StringComparison.Ordinal), "input folder: a new film is told – " + model.ArrivalsText);
        await Screenshot(window, "input-arrivals.png");
        model.DismissArrivalsCommand.Execute(null);
        Check(!model.HasArrivals, "input folder: the notice hidden");
        File.Delete(arriving);

        // the mark of a series: the same in Premiéry
        show.IsChosen = true;
        show.Note = "pozrieť s rodinou";
        Check(model.Premieres.Tab(PremiereTab.SeriesDomestic).All.Any(r => r.Caption == "Invented Show (2026)" && r.IsChosen && r.Note == "pozrieť s rodinou"),
            "novinky: a series chosen here is chosen in Premiéry too");
        page.OnlyChosen = true;
        Check(domestic.Rows.Count == 1 && domestic.Rows[0] == show, "novinky: Zobraziť vybrané");
        page.OnlyChosen = false;
        home.IsExpanded = true;
        await WaitFor(() => domestic.Rows.All(r => r.Poster.Image is not null));
        Check(domestic.Rows.All(r => r.Poster.Image is not null), "novinky: posters of the whole series");
        await Screenshot(window, "novinky.png");
        home.IsExpanded = false;

        await page.SendAsync();
        Check(model.SelectedTool.Page == model.Rules && model.Rules.IsSeries && model.Rules.TitleText == "Invented Show (2026)" &&
              model.Rules.Plan?.Target.Source.Path.EndsWith("Invented.Show.S01E01.1080p.WEB.mkv", StringComparison.Ordinal) == true,
            $"novinky: one file goes to Single spracovanie with its series – {page.Status} | {model.Rules.TitleText} | {model.Rules.EpisodeText}");
        await Screenshot(window, "novinky-single.png");

        model.Open(page);
        home.IsChecked = true;
        Check(home.CheckedText == "2/2" && home.Episodes.Where(e => !e.CanCheck).All(e => !e.IsChecked), "novinky: the series' tick ticks its aired episodes only – " + home.CheckedText);
        var world = page.Tabs[1].Rows.First(r => r.Caption == "World Invented (2024)");
        world.Episodes[0].IsChecked = true;
        await page.SendAsync();
        Check(model.SelectedTool.Page == model.Batch && model.Batch.ChosenFiles?.Count == 2 && model.Batch.IsSeries &&
              model.Batch.SeriesTitle.StartsWith("Domáci seriál (2020)", StringComparison.Ordinal) && model.Batch.Analysis?.FileCount == 2,
            $"novinky: episodes of a series go to Hromadné spracovanie, only their files – {page.Status} | {model.Batch.SeriesTitle} | {model.Batch.Summary}");
        // owner, 1. 11. 2026: the groups in two lists – a series in Skupina Seriál, nothing among the films
        Check(model.Batch.SeriesGroups.Count == model.Batch.Groups.Count && model.Batch.FilmGroups.Count == 0 &&
              (model.Batch.SelectedGroup is null || model.Batch.SelectedSeriesGroup == model.Batch.SelectedGroup && model.Batch.SelectedFilmGroup is null),
            $"batch: a series is in Skupina Seriál – series {model.Batch.SeriesGroups.Count}, films {model.Batch.FilmGroups.Count}");
        Check(page.Waiting.Count == 1 && page.Waiting[0].Episode.OriginalTitle == "World Invented" && page.Status.Contains("čakajú nižšie", StringComparison.Ordinal),
            "novinky: an episode not in the input folder waits – " + page.Status);
        await Screenshot(window, "novinky-batch.png");

        model.Open(page);
        File.Copy(sample, Path.Combine(input, "World.Invented.S02E01.mkv"), overwrite: true);
        await page.CheckAgainCommand.ExecuteAsync(null);
        Check(page.Waiting.Count == 0 && model.SelectedTool.Page == model.Rules && model.Rules.TitleText == "World Invented (2024)",
            "novinky: Skontrolovať znova finds the episode now in the folder – " + page.Status);
        Check(before.All(f => File.Exists(f.Key) && File.GetLastWriteTimeUtc(f.Key) == f.Value), "novinky: files in the input folder are only read");

        // episodes of two series at once: one batch, each into its own series (owner, 29. 10. 2026)
        model.Open(page);
        domestic.Rows.First(r => r.Caption == "Domáci seriál (2020)").IsChecked = true;
        page.Tabs[1].Rows.First(r => r.Caption == "World Invented (2024)").Episodes[0].IsChecked = true;
        await page.SendAsync();
        Check(model.SelectedTool.Page == model.Batch && model.Batch.ChosenFiles?.Count == 3 && model.Batch.SeriesTitle.StartsWith("Domáci seriál (2020)", StringComparison.Ordinal) &&
              model.Batch.OtherSeriesList.Contains("World Invented (2024)", StringComparison.Ordinal) && page.Ready.Count == 0,
            $"novinky: episodes of two series go to one batch – {page.Status} | {model.Batch.SeriesTitle} | {model.Batch.OtherSeriesList}");
        await Screenshot(window, "novinky-batch-two.png");

        // Vymazať frontu (owner, 29. 10. 2026): the analysed files and the opened file are forgotten, nothing on the disk changes
        model.Batch.Confirm = _ => true;
        model.Batch.ClearCommand.Execute(null);
        Check(model.Batch.Groups.Count == 0 && model.Batch.ChosenFiles is null && model.Batch.OtherSeries.Length == 0 && model.Batch.Analysis is null,
            "batch: Vymazať frontu forgets the files not processed – " + model.Batch.Summary);
        model.Rules.Confirm = _ => true;
        model.Rules.ClearCommand.Execute(null);
        Check(model.Rules.FileName.Length == 0 && model.Rules.Plan is null && model.Rules.Rows.Count == 0, "single: Vymazať frontu forgets the opened file");
        Check(before.All(f => File.Exists(f.Key)), "Vymazať frontu: the files stay on the disk");

        model.Open(page);
        page.ShowHistory = true;
        await WaitFor(() => domestic.Rows.Count > 0);
        Check(domestic.Rows.Count == 1 && domestic.Rows[0].Caption == "Starý seriál (2019)" && domestic.Rows[0].NearestCode == "S02E05",
            "novinky: older episodes are in the history – " + string.Join(", ", domestic.Rows.Select(r => r.Caption)));
        await Screenshot(window, "novinky-history.png");
        page.ShowAll = true;
        Check(new[] { "Domáci seriál (2020)", "Invented Show (2026)", "Starý seriál (2019)" }.All(c => domestic.Rows.Any(r => r.Caption == c)) &&
              domestic.Rows.First(r => r.Caption == "Domáci seriál (2020)").Episodes.Count >= 45,
            "novinky: Zobraziť všetko shows the history, the last week and the coming episodes – " + string.Join(", ", domestic.Rows.Select(r => $"{r.Caption} {r.Episodes.Count}")));
        page.ShowRecent = true;

        // Kde pozerať: the chosen series and film on services in Slovakia; the chosen titles in the input folder
        services.WatchForSelfTest = new Core.News.WatchProviders(new Core.Library.Tmdb.TmdbClient(http, "selftest-key", null, new RequestPacer(1000)));
        Check(await services.Watchlist.CheckAsync() is null && services.Watchlist.Offered() is var offered &&
              offered.Any(o => o.Title.Caption == "Invented Show (2026)" && o.Title.Text == "Vymyslené+ (SK)" && o.IsNew) &&
              offered.Any(o => o.Title.Text == "Obchod (SK) – požičanie"),
            "watch: where the chosen titles can be watched legally – " + string.Join(" | ", services.Watchlist.Offered().Select(o => $"{o.Title.Caption}: {o.Title.Text}")));
        Check(model.Premieres.Tab(PremiereTab.SeriesDomestic).All.First(r => r.Caption == "Invented Show (2026)").Streaming == "▶ Vymyslené+ (SK)",
            "watch: shown under the title in Premiéry");
        await services.InputWatch.ScanAsync();
        Check(services.InputWatch.Found.Any(f => f.Title == "Invented Show (2026)" && f.IsSeries && f.Files.Count == 1) &&
              domestic.Rows.First(r => r.Caption == "Invented Show (2026)") is { FolderText: "✓ v priečinku: 1" } found && found.Episodes[0].State == "✓ vo vstupnom priečinku",
            "input watch: an episode found in the input folder – " + string.Join(" | ", services.InputWatch.Found.Select(f => $"{f.Title} {f.Detail}")));
        Check(services.InputWatch.Found.Any(f => f.Title == "Domáci seriál (2020)" && f.IsSeries) && !services.Marks.Get(Core.News.MarkStore.Series(home.Episodes[0].Episode)).Chosen,
            "input watch: episodes of series not chosen are found too (owner, 29. 10. 2026)");

        // the Dashboard's week: the chosen series' episodes and the premieres, the films of the week
        model.Open(model.Dashboard);
        await model.Dashboard.Week.RefreshAsync();
        Check(model.Dashboard.Week.Episodes.Any(e => e.Title == "Invented Show (2026)" && e.Chosen && e.IsToday) && model.Dashboard.Week.Films.Count >= 2 &&
              model.Dashboard.Week.Films.Any(f => f.Chosen) && model.Dashboard.Week.Offers.Count == 2 && model.Dashboard.Week.Found.Count >= 1,
            "dashboard: this week's episodes and films – " + string.Join(" | ", model.Dashboard.Week.Episodes.Select(e => $"{e.Day} {e.Title} {e.Detail}")));
        await Screenshot(window, "dashboard-week.png");
        services.WatchForSelfTest = null;
        model.Open(page);

        var asked = news.Requests.Count;
        var again = new SeriesNewsPageViewModel(services);
        again.Shown();
        await WaitFor(() => again.Tabs[0].Rows.Count > 0);
        Check(news.Requests.Count == asked && again.Tabs[0].Rows.Count == 2, "novinky: the kept days are shown at once at the next start, without asking the sources");

        await TestCalendarAsync(model, window, services);
        await TestTasksAsync(model, window, services);
        await TestNotesAsync(model, window, services);
        await TestBackgroundWorkAsync();
        await TestTmdbTrayAsync(model, services);
        await TestReportsAsync(model, services);
        await TestDashboardAgendaAsync(model, window, services);
        await TestActionsAsync(model, window, services);
        await TestGuidesAsync(model, window);

        services.SeriesTmdbForSelfTest = null;
        services.PostersForSelfTest = null;
        services.SeriesNewsForSelfTest = null;
        services.SetNewsSources(Core.News.NewsSource.Defaults);
        model.Open(model.Dashboard);
    }

    // owner, 2. 11. 2026: a film of Premiéry and an episode of Novinky added to Poznámky and Plánované úlohy, opened to be
    // edited there; the same film again opens its note (invented titles)
    private static async Task TestNewsToAgendaAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var film = new Core.News.FilmPremiere
        {
            TmdbId = 990101, OriginalTitle = "Vymyslený agendový film", EnglishTitle = "Invented Agenda Film", Region = Core.News.NewsRegion.World,
            Premiere = DateOnly.FromDateTime(DateTime.Now).AddDays(10), Year = DateTime.Now.Year,
        };
        var (note, task) = NewsToAgenda.Commands(services, () => NewsToAgenda.Film(film), _ => { });
        note.Execute(null);
        await Settle();
        var kept = services.Agenda.Items.Where(i => i.Links.Any(l => l.Target == Core.News.MarkStore.Film(990101))).ToList();
        Check(model.SelectedTool.Page == model.AgendaNotes && model.AgendaNotes.Editor is AgendaEditor && kept.Count == 1 && kept[0].Note is not null &&
              kept[0].Text.Contains("Premiéra", StringComparison.Ordinal),
            $"news → agenda: the film in Poznámky, opened to edit ({kept.Count} records)");
        await Screenshot(window, "news-to-note.png");
        (model.AgendaNotes.Editor as AgendaEditor)?.CancelCommand.Execute(null);
        note.Execute(null);
        Check(services.Agenda.Items.Count(i => i.Links.Any(l => l.Target == Core.News.MarkStore.Film(990101)) && i.Note is not null) == 1,
            "news → agenda: the same film again – its note, not a second one");
        (model.AgendaNotes.Editor as AgendaEditor)?.CancelCommand.Execute(null);

        var episode = new Core.News.SeriesEpisode
        {
            Source = "Vymyslený zdroj", Region = Core.News.NewsRegion.World, Title = "Vymyslený agendový seriál", OriginalTitle = "Vymyslený agendový seriál",
            AirDate = DateOnly.FromDateTime(DateTime.Now).AddDays(3), Season = 1, Number = 2,
        };
        var (_, episodeTask) = NewsToAgenda.Commands(services, () => NewsToAgenda.Episode(episode), _ => { });
        episodeTask.Execute(null);
        await Settle();
        var due = services.Agenda.Items.FirstOrDefault(i => i.Task is not null && i.Links.Any(l => l.Kind == Core.Agenda.LinkKind.Episode))?.Task?.Due;
        Check(model.SelectedTool.Page == model.AgendaTasks && model.AgendaTasks.Editor is not null && due == episode.AirDate,
            $"news → agenda: the episode in Plánované úlohy, due on its airing ({due})");
        await Screenshot(window, "news-to-task.png");
        model.AgendaTasks.Editor?.CancelCommand.Execute(null);
        foreach (var item in services.Agenda.Items.Where(i => i.Title.Contains("Vymyslený agendový", StringComparison.Ordinal)).ToList())
        {
            services.Agenda.Delete(item.Id);
        }

        services.Agenda.EmptyBin();
    }
}
