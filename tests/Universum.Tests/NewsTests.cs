using System.Net;
using System.Text;
using System.Text.Json;
using Universum.Core.Html;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.News;

namespace Universum.Tests;

/// <summary>Premiéry and Novinky: the HTML reader, page rules, polite reading, TVmaze, TMDB films. All pages, series and films are invented.</summary>
public sealed class NewsTests
{
    private const string Rules = """
        {
          "format": "UNIVERSUM-zdroj 1",
          "name": "Vymyslený rozpis",
          "address": "https://rozpis.example/den/{yyyy}-{MM}-{dd}?d={d}&m={M}",
          "item": "div.zaznam",
          "section": "h3",
          "sections": [ { "contains": "Nové seriály", "kind": "series-premiere" }, { "contains": "Streamy", "kind": "streaming" } ],
          "fields": {
            "title": { "select": "a.meno", "take": "own" },
            "years": { "select": "a.meno > span" },
            "original": { "select": "a.povodny" },
            "genres": { "select": "div.zaner" },
            "episode": { "select": "div.cislo", "take": "own" },
            "channel": { "select": "div.cislo span" },
            "channelId": { "select": ":self", "take": "attr:data-kanal" },
            "link": { "select": "a.meno", "take": "attr:href" }
          },
          "domestic": [ "ct1", "Nova" ],
          "daysBack": 1,
          "daysAhead": 2
        }
        """;

    private const string Page = """
        <!DOCTYPE html><html><head><meta charset="utf-8"><title>Rozpis &amp; tipy</title>
        <script>document.write('<div class="zaznam">nie</div>');</script><style>.zaznam{}</style></head>
        <body><!-- <div class="zaznam">komentár</div> -->
        <h3>Nové seriály</h3>
        <div class="zaznam a b" data-kanal="ct1"><a class="meno" href="https://rozpis.example/serial/1">Vymyslený &quot;seriál&quot; <span>(2026–????)</span></a>
          <a class="povodny">Invented Show</a><div class="zaner">Drama  /  Krimi</div><div class="cislo">01&times;01<br /><span>ČT1</span></div></div>
        <h3>Ostatné</h3>
        <div class='zaznam' data-kanal=xyz><a class="meno" href="javascript:alert(1)">Svetový seriál <span>(2019–2026)</span></a>
          <a class="povodny">World&nbsp;Invented</a><div class="cislo">03&times;09&nbsp;+&nbsp;1&nbsp;další<br><span>Nova</span></div></div>
        <div class="zaznam"><a class="meno">Bez čísla</a><div class="cislo">Špeciál</div></div>
        <p>odsek <p>druhý odsek
        <h3>Streamy</h3>
        <div class="zaznam" data-kanal="stream"><a class="meno">Streamový seriál <span>(2024–????)</span></a><div class="cislo">Nová epizoda<br><span>Stream+</span></div></div>
        </body></html>
        """;

    [Test]
    public void HtmlIsParsedTolerantly()
    {
        var root = HtmlParser.Parse(Page);
        Assert.Equal(4, root.QueryAll("div.zaznam").Count, "items (not in the script or the comment)");
        Assert.Equal("Rozpis & tipy", root.Query("title")!.TextContent, "title text decoded");
        Assert.Equal(2, root.QueryAll("p").Count, "an open paragraph is closed by the next one");
        var first = root.Query("div.zaznam.a.b[data-kanal=ct1]")!;
        Assert.Equal("Vymyslený \"seriál\"", first.Query("a.meno")!.OwnText, "own text without the span, entities decoded");
        Assert.Equal("(2026–????)", first.Query("a.meno > span")!.TextContent, "child selector");
        Assert.Equal("01×01 ČT1", first.Query("div.cislo")!.TextContent, "line break as a space");
        Assert.True(root.Query("div[data-kanal^=c] > a[href$='/1']") is not null && root.Query("div[data-kanal*=yz]") is not null, "attribute operators");
        Assert.True(root.Query("h3, p") is { Name: "h3" }, "groups");
        foreach (var bad in new[] { "div:first-child", "a::before", "div ~ p", "> a", "a >", "", "[x", ".", "a[1x]" })
        {
            Assert.Throws<FormatException>(() => HtmlSelector.Parse(bad), "refused selector: " + bad);
        }

        var deep = HtmlParser.Parse(string.Concat(Enumerable.Repeat("<div>", 5000)) + "x");
        Assert.True(deep.Descendants().Count() == 5000, "deep nesting does not break the parser");
        Assert.Throws<FormatException>(() => HtmlParser.Parse(new string('a', HtmlParser.MaxLength + 1)), "too large");
    }

    [Test]
    public void PageRulesReadTheEpisodes()
    {
        var rules = PageRules.Load(Rules);
        Assert.Equal("https://rozpis.example/den/2026-11-05?d=5&m=11", rules.PageAddress(new DateOnly(2026, 11, 5)).AbsoluteUri, "address of a day");
        var day = new DateOnly(2026, 10, 28);
        var episodes = rules.Read(Page, day, "Vymyslený rozpis");
        Assert.Equal(3, episodes.Count, "an item without a number outside a streaming section is skipped");

        var premiere = episodes[0];
        Assert.True(premiere.Kind == EpisodeKind.SeriesPremiere && premiere.Region == NewsRegion.Domestic, "a new Czech series");
        Assert.Equal("Invented Show (2026)", premiere.Caption, "original title and year");
        Assert.Equal("Vymyslený \"seriál\"", premiere.Title, "local title");
        Assert.Equal("S01E01", premiere.Code, "episode");
        Assert.Equal("ČT1", premiere.Channel, "channel");
        Assert.Equal("Drama, Krimi", premiere.Genres, "genres");
        Assert.Equal("https://rozpis.example/serial/1", premiere.Link, "link of the series");
        Assert.Equal(day, premiere.AirDate, "the page's day");

        var episode = episodes[1];
        Assert.True(episode.Kind == EpisodeKind.Episode && episode.Region == NewsRegion.Domestic, "a channel named in the domestic list (by name)");
        Assert.Equal("S03E09-E10", episode.Code, "two episodes at once");
        Assert.Equal<string?>(null, episode.Link, "only https links");
        Assert.Equal("World Invented (2019)", episode.Caption, "non-breaking spaces");

        var stream = episodes[2];
        Assert.True(stream.Kind == EpisodeKind.Streaming && stream.Code.Length == 0 && stream.Region == NewsRegion.World, "a new episode on a streaming service, no number");
    }

    [Test]
    public void BadRulesAreRefused()
    {
        foreach (var (change, what) in new[]
        {
            ("\"format\": \"UNIVERSUM-zdroj 1\"", "\"format\": \"iné\""),
            ("https://rozpis.example/den/{yyyy}-{MM}-{dd}?d={d}&m={M}", "http://rozpis.example/{yyyy}"),
            ("https://rozpis.example/den/{yyyy}-{MM}-{dd}?d={d}&m={M}", "https://rozpis.example/{rok}"),
            ("https://rozpis.example/den/{yyyy}-{MM}-{dd}?d={d}&m={M}", "https://user:pw@rozpis.example/{yyyy}"),
            ("\"item\": \"div.zaznam\"", "\"item\": \"div:hover\""),
            ("\"genres\": { \"select\": \"div.zaner\" }", "\"poster\": { \"select\": \"img\" }"),
            ("\"take\": \"own\" },\n    \"years\"", "\"take\": \"script\" },\n    \"years\""),
            ("\"daysAhead\": 2", "\"daysAhead\": 365"),
        })
        {
            var text = Rules.Replace(change, what, StringComparison.Ordinal);
            Assert.True(text != Rules, "changed: " + what);
            Assert.Throws<FormatException>(() => PageRules.Load(text), "refused: " + what);
        }

        Assert.Throws<FormatException>(() => PageRules.Load("{ nonsense"), "not JSON");
        Assert.Throws<FormatException>(() => PageRules.Load("{}"), "nothing in it");
    }

    [Test]
    public void RobotsTxtIsRespected()
    {
        var robots = RobotsRules.Parse("""
            User-agent: Googlebot
            Disallow: /

            User-agent: *
            Disallow: /tajne/
            Disallow: /*.pdf$
            Allow: /tajne/verejne
            """, "universum");
        Assert.True(robots.Allows("/den/2026-10-28") && robots.Allows("/tajne/verejne/x") && robots.Allows("/subor.pdf?x=1"), "allowed paths");
        Assert.True(!robots.Allows("/tajne/x") && !robots.Allows("/dokument.pdf"), "disallowed paths");
        var own = RobotsRules.Parse("User-agent: *\nDisallow:\n\nUser-agent: UNIVERSUM\nDisallow: /den/\n", "universum");
        Assert.True(!own.Allows("/den/1") && own.Allows("/ine"), "the own group wins over *");
        Assert.True(RobotsRules.AllowAll.Allows("/x") && !RobotsRules.DisallowAll.Allows("/x"), "all or nothing");
    }

    [Test]
    public async Task PagesAreReadPolitely()
    {
        WebPageClient.PagesPerSecond = 1000;
        var server = new Server();
        server.Pages["https://rozpis.example/robots.txt"] = "User-agent: *\nDisallow: /tajne/";
        server.Pages["https://rozpis.example/den/1"] = "<html>ok</html>";
        var client = new WebPageClient(server.Http);

        Assert.Equal("<html>ok</html>", await client.GetAsync(new Uri("https://rozpis.example/den/1")), "page read");
        var refused = await Assert.ThrowsAsync<NewsSourceException>(() => client.GetAsync(new Uri("https://rozpis.example/tajne/1")), "robots.txt");
        Assert.True(refused.Message.Contains("robots.txt", StringComparison.Ordinal), "reason: " + refused.Message);
        await Assert.ThrowsAsync<NewsSourceException>(() => client.GetAsync(new Uri("http://rozpis.example/den/1")), "only https");
        Assert.Equal(1, server.Requests.Count(r => r.EndsWith("/robots.txt", StringComparison.Ordinal)), "robots.txt asked once");
        Assert.True(server.UserAgents.All(a => a.StartsWith("UNIVERSUM/", StringComparison.Ordinal)), "UNIVERSUM introduces itself");

        server.RedirectTo = new Uri("https://evil.example/");
        await Assert.ThrowsAsync<NewsSourceException>(() => client.GetAsync(new Uri("https://rozpis.example/den/1")), "a redirect to another site");
        server.RedirectTo = null;

        server.Pages["https://rozpis.example/velka"] = new string('x', WebPageClient.MaxBytes + 10);
        await Assert.ThrowsAsync<NewsSourceException>(() => client.GetAsync(new Uri("https://rozpis.example/velka")), "too large");

        // no robots.txt (404) = all allowed; a server error = nothing
        var other = new WebPageClient(server.Http);
        server.Pages["https://iny.example/a"] = "a";
        Assert.Equal("a", await other.GetAsync(new Uri("https://iny.example/a")), "no robots.txt");
        server.Pages["https://chyba.example/robots.txt"] = "ERROR";
        server.Pages["https://chyba.example/a"] = "a";
        await Assert.ThrowsAsync<NewsSourceException>(() => other.GetAsync(new Uri("https://chyba.example/a")), "robots.txt unavailable");
    }

    [Test]
    public void PagesAreDecodedByTheirCharset()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var text = "Žluťoučký kůň";
        Assert.Equal(text, WebPageClient.Decode(Encoding.GetEncoding(1250).GetBytes(text), "windows-1250"), "header");
        var page = Encoding.GetEncoding(1250).GetBytes("<meta charset=\"windows-1250\">" + text);
        Assert.True(WebPageClient.Decode(page, null).EndsWith(text, StringComparison.Ordinal), "meta tag");
        Assert.Equal(text, WebPageClient.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)], null), "UTF-8 with BOM");
        Assert.Equal(text, WebPageClient.Decode(Encoding.UTF8.GetBytes(text), "nonsense"), "unknown name → UTF-8");
    }

    [Test]
    public void TvMazeScheduleIsFilteredAndDated()
    {
        using var document = JsonDocument.Parse("""
            [
              {"season":3,"number":1,"type":"regular","airdate":"2026-10-28","airstamp":"2026-10-28T23:30:00+00:00","_embedded":{"show":{"id":5,"name":"Invented Stream","type":"Scripted","weight":90,"premiered":"2024-01-01","genres":["Drama","Sci-Fi"],"webChannel":{"name":"Invented+","country":null},"externals":{"imdb":"tt0000005","thetvdb":55},"url":"https://www.tvmaze.com/shows/5/x"}}},
              {"season":1,"number":4,"type":"regular","airdate":"2026-10-28","airstamp":"2026-10-28T19:00:00+00:00","show":{"id":6,"name":"Czech Invented","type":"Scripted","weight":75,"premiered":"2026-09-01","network":{"name":"ČT1","country":{"code":"CZ"}},"externals":{"imdb":"bad"},"url":"https://evil.example/"}},
              {"season":9,"number":9,"type":"regular","airdate":"2026-10-28","show":{"id":7,"name":"Invented News","type":"News","weight":99}},
              {"season":9,"number":9,"type":"regular","airdate":"2026-10-28","show":{"id":8,"name":"Unknown Invented","type":"Scripted","weight":20}},
              {"season":1,"number":null,"type":"insignificant_special","airdate":"2026-10-28","show":{"id":9,"name":"Special","type":"Scripted","weight":99}}
            ]
            """);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus1", TimeSpan.FromHours(1), "Plus1", "Plus1");
        var episodes = TvMazeClient.ParseSchedule(document, TvMazeFilter.Default, zone);
        Assert.Equal(2, episodes.Count, "news, unpopular shows and insignificant specials left out");
        var stream = episodes[0];
        Assert.True(stream.Kind == EpisodeKind.SeasonPremiere && stream.Region == NewsRegion.World && stream.Caption == "Invented Stream (2024)", "a new season in the world");
        Assert.Equal(new DateOnly(2026, 10, 29), stream.AirDate, "the day in the user's time zone");
        Assert.True(stream.Imdb == "tt0000005" && stream.Tvdb == 55 && stream.TvMaze == 5 && stream.Genres == "Drama, Sci-Fi" && stream.Channel == "Invented+", "IDs and details");
        var czech = episodes[1];
        Assert.True(czech.Region == NewsRegion.Domestic && czech.Kind == EpisodeKind.Episode && czech.Imdb is null && czech.Link is null && czech.Code == "S01E04",
            "a Czech channel is domestic; a bad IMDb ID and a foreign link left out");
    }

    [Test]
    public void SameEpisodeFromTwoSourcesIsShownOnce()
    {
        var day = new DateOnly(2026, 10, 28);
        SeriesEpisode Episode(string source, string title, int number, DateOnly airDate, int? tvmaze = null) => new()
        {
            Source = source, Region = NewsRegion.World, Title = title, OriginalTitle = "Invented Show", Year = 2020, Season = 2, Number = number, AirDate = airDate, TvMaze = tvmaze,
        };

        var merged = SeriesNews.Merge(
        [
            Episode("TVmaze", "Invented Show", 5, day, 77),
            Episode("Vymyslený rozpis", "Vymyslený seriál", 5, day),
            Episode("Vymyslený rozpis", "Vymyslený seriál", 5, day.AddDays(1)),
            Episode("Vymyslený rozpis", "Vymyslený seriál", 6, day.AddDays(7)),
        ]);
        Assert.Equal(2, merged.Count, "one entry per episode");
        Assert.True(merged[0].Title == "Vymyslený seriál" && merged[0].TvMaze == 77 && merged[0].AirDate == day, "the page's entry with TVmaze's ID, earliest day");
    }

    [Test]
    public void PeriodicityIsDescribed()
    {
        var monday = new DateOnly(2026, 10, 5);
        Assert.Equal("týždenne v pondelok", Periodicity.Describe(Enumerable.Range(0, 6).Select(i => monday.AddDays(7 * i))), "weekly");
        Assert.Equal("denne (po–pi)", Periodicity.Describe(Enumerable.Range(0, 14).Select(monday.AddDays).Where(d => d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)), "weekdays");
        Assert.Equal("denne", Periodicity.Describe(Enumerable.Range(0, 10).Select(monday.AddDays)), "every day");
        Assert.Equal("všetky naraz", Periodicity.Describe(Enumerable.Repeat(monday, 8)), "binge release");
        Assert.Equal("2× týždenne (po, št)", Periodicity.Describe(Enumerable.Range(0, 4).SelectMany(w => new[] { monday.AddDays(7 * w), monday.AddDays(7 * w + 3) })), "twice a week");
        Assert.Equal("každé 2 týždne", Periodicity.Describe(Enumerable.Range(0, 5).Select(i => monday.AddDays(14 * i))), "every two weeks");
        Assert.Equal("", Periodicity.Describe([monday]), "one episode");
        Assert.Equal("streda", Periodicity.DayName(new DateOnly(2026, 10, 28)), "day name");
    }

    [Test]
    public async Task FilmPremieresComeFromTmdb()
    {
        var server = new Server();
        string Iso(int days) => new DateOnly(2026, 11, 1).AddDays(days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        server.Handler = uri => uri.AbsolutePath switch
        {
            "/3/discover/movie" when uri.Query.Contains("region=CZ", StringComparison.Ordinal) => """{"total_pages":1,"results":[{"id":11,"title":"Invented Film","popularity":5}]}""",
            "/3/discover/movie" when uri.Query.Contains("region=SK", StringComparison.Ordinal) => """{"total_pages":1,"results":[{"id":11,"title":"Invented Film","popularity":5},{"id":12,"title":"Only Abroad","popularity":9}]}""",
            "/3/discover/movie" => """{"total_pages":1,"results":[{"id":13,"title":"Seoul Invented","popularity":7}]}""",
            "/3/movie/11" => $$$"""{"title":"Vymyslený film","original_title":"Invented Film","release_date":"{{{Iso(-10)}}}","poster_path":"/a.jpg","production_countries":[{"iso_3166_1":"CZ"},{"iso_3166_1":"XX"}],"production_companies":[{"name":"Studio"}],"release_dates":{"results":[{"iso_3166_1":"CZ","release_dates":[{"type":3,"release_date":"{{{Iso(2)}}}T00:00:00.000Z"},{"type":4,"release_date":"{{{Iso(40)}}}T00:00:00.000Z"}]},{"iso_3166_1":"SK","release_dates":[{"type":3,"release_date":"{{{Iso(9)}}}T00:00:00.000Z"},{"type":6,"release_date":"{{{Iso(90)}}}T00:00:00.000Z"}]}]}}""",
            "/3/movie/12" => $$$"""{"title":"Len v zahraničí","original_title":"Only Abroad","release_date":"{{{Iso(1)}}}","release_dates":{"results":[{"iso_3166_1":"US","release_dates":[{"type":3,"release_date":"{{{Iso(1)}}}T00:00:00.000Z"}]}]}}""",
            "/3/movie/13" => $$$"""{"title":"Soul","original_title":"서울","release_date":"{{{Iso(3)}}}","poster_path":"../x.jpg","release_dates":{"results":[{"iso_3166_1":"US","release_dates":[{"type":4,"release_date":"{{{Iso(20)}}}T00:00:00.000Z"}]}]}}""",
            _ => null,
        };
        var films = new FilmPremieres(new TmdbClient(server.Http, "test-key", null, new RequestPacer(1000)));
        var from = new DateOnly(2026, 10, 25);
        var to = new DateOnly(2026, 12, 31);

        var domestic = await films.DomesticAsync(from, to);
        Assert.Equal(1, domestic.Count, "a film without a Czech or Slovak premiere is left out");
        var film = domestic[0];
        Assert.Equal("Invented Film (2026)", film.Caption, "library title");
        Assert.Equal(new DateOnly(2026, 11, 3), film.Premiere, "the earlier of the Czech and Slovak premieres");
        Assert.Equal("CZ · SK 10. 11.", film.PremiereNote, "where");
        Assert.Equal("ČR, XX · Studio", film.Production, "production");
        Assert.Equal("VOD 11. 12. 2026 · TV 30. 1. 2027 (SK)", film.Availability, "VOD here, TV in Slovakia");
        Assert.Equal("Vymyslený film", film.LocalTitle, "Czech title");
        Assert.Equal(11, film.TmdbId, "TMDB ID");

        var world = await films.WorldAsync(from, to);
        Assert.Equal(1, world.Count, "world premieres");
        Assert.True(world[0].Caption == "Seoul Invented (2026) [tmdbid-13]" && world[0].PosterPath is null && world[0].Availability == "VOD 21. 11. 2026 (USA)",
            $"a title not in Latin script gets the English title and the ID; an unsafe poster path is left out – {world[0].Caption} | {world[0].Availability}");
        Assert.True(server.Requests.All(r => r.StartsWith("https://api.themoviedb.org/3/", StringComparison.Ordinal)), "only TMDB");
    }

    [Test]
    public void KeptDaysExpire()
    {
        var folder = Path.Combine(Path.GetTempPath(), "universum-news-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new NewsCache(folder);
            var day = new DateOnly(2026, 10, 28);
            Assert.Equal<IReadOnlyList<SeriesEpisode>?>(null, cache.Get("Zdroj / s lomkou", day, TimeSpan.FromHours(1)), "nothing kept");
            cache.Put("Zdroj / s lomkou", day, [new SeriesEpisode { Source = "x", Region = NewsRegion.Domestic, Title = "A", OriginalTitle = "B", AirDate = day, Season = 1, Number = 2 }]);
            var kept = cache.Get("Zdroj / s lomkou", day, TimeSpan.FromHours(1));
            Assert.True(kept is [{ OriginalTitle: "B", Code: "S01E02" }], "kept and read back");
            Assert.Equal<IReadOnlyList<SeriesEpisode>?>(null, cache.Get("Zdroj / s lomkou", day, TimeSpan.Zero), "expired");
            Assert.True(Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories).All(f => !f.Contains("lomkou", StringComparison.Ordinal)), "the source's name is no path");
            cache.Clear();
            Assert.True(!Directory.Exists(folder), "cleared");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Test]
    public void EpisodesAreFoundInTheInputFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "universum-input-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new[]
            {
                Path.Combine("Vymyslený seriál", "Vymysleny.serial.S02E05.1080p.mkv"),
                Path.Combine("Invented Show (2020)", "Season 02", "Invented Show S02E05.mkv"),
                "Invented.Show.US.S02E06.mp4",
                "Invented.Show.S02E07-E08.mkv",
                "Invented Show (2009) S02E05.mkv",
                "Other Show S02E05.mkv",
                "Invented Show S02E05.txt",
                Path.Combine("MOVIES", "Invented Show S02E05.mkv"),
            };
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, file))!);
                File.WriteAllText(Path.Combine(folder, file), "x");
            }

            SeriesEpisode Episode(int number, int? last = null) => new()
            {
                Source = "x", Region = NewsRegion.World, Title = "Vymyslený seriál", OriginalTitle = "Invented Show", Year = 2020,
                Season = 2, Number = number, LastNumber = last, AirDate = new DateOnly(2026, 10, 28),
            };

            var found = EpisodeFinder.Find([Episode(5), Episode(6), Episode(7, 8), Episode(9)], folder, [Path.Combine(folder, "MOVIES")]);
            string Names(int i) => string.Join(", ", found[i].Files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal("Invented Show S02E05.mkv, Vymysleny.serial.S02E05.1080p.mkv", Names(0),
                "original or local title, season folder; another year, another series, a text file and the library are left out");
            Assert.Equal("Invented.Show.US.S02E06.mp4", Names(1), "a title that starts with the series' title");
            Assert.Equal("Invented.Show.S02E07-E08.mkv", Names(2), "two episodes aired together");
            Assert.Equal(0, found[3].Files.Count, "not there yet");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Web pages and APIs with invented answers.</summary>
    private sealed class Server : HttpMessageHandler
    {
        private HttpClient? _http;

        public HttpClient Http => _http ??= new HttpClient(this);

        public Dictionary<string, string> Pages { get; } = [];

        public Func<Uri, string?>? Handler { get; set; }

        public List<string> Requests { get; } = [];

        public List<string> UserAgents { get; } = [];

        public Uri? RedirectTo { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            if (RedirectTo is not null && !request.RequestUri.AbsolutePath.EndsWith("robots.txt", StringComparison.Ordinal))
            {
                request.RequestUri = RedirectTo;
            }

            var body = Handler?.Invoke(request.RequestUri!) ?? (Pages.TryGetValue(request.RequestUri!.GetLeftPart(UriPartial.Path), out var page) ? page : null);
            return Task.FromResult(body switch
            {
                null => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request },
                "ERROR" => new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "text/html") },
            });
        }
    }
}
