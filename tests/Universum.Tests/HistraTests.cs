using System.Net;
using System.Text;
using Universum.Core.Library;
using Universum.Core.Library.Histra;
using Universum.Core.Library.Tmdb;

namespace Universum.Tests;

/// <summary>HISTRA token: only what can safely go into a Bearer header is accepted.</summary>
public sealed class HistraTests
{
    [Test]
    public void TokenGoesIntoABearerHeader()
    {
        const string token = "hst_9fK2-x.Q~p+/abc==";
        Assert.True(HistraApi.IsValidToken(token), "token68 characters and padding");
        var header = HistraApi.Authorization(token);
        Assert.Equal("Bearer", header.Scheme);
        Assert.Equal(token, header.Parameter);
        Assert.Equal("Bearer " + token, header.ToString());
    }

    [Test]
    public void UnsafeTokensAreRefused()
    {
        foreach (var bad in new[] { null, "", "short", "abc def ghi", "abcdefgh\r\nX-Evil: 1", "\"abcdefghij\"", "========", "abc=defghij", new string('a', HistraApi.MaxTokenLength + 1) })
        {
            Assert.True(!HistraApi.IsValidToken(bad), $"refused: {bad?.Replace("\r\n", "\\r\\n")}");
        }

        Assert.True(HistraApi.TokenPage.Scheme == "https" && HistraApi.TokenPage.Host == "histra.net", "token page on histra.net over HTTPS");
    }

    [Test]
    public void RequestsGoOnlyToHistra()
    {
        using var request = HistraApi.Request(HttpMethod.Get, "api/v1/me", "hst_0123456789");
        Assert.Equal("https://histra.net/api/v1/me", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer hst_0123456789", request.Headers.Authorization!.ToString());

        foreach (var foreign in new[] { "https://evil.example/x", "//evil.example/x", "http://histra.net/x", "\\\\evil\\x" })
        {
            var refused = false;
            try
            {
                HistraApi.Request(HttpMethod.Get, foreign, "hst_0123456789").Dispose();
            }
            catch (ArgumentException)
            {
                refused = true;
            }

            Assert.True(refused, "token never sent elsewhere: " + foreign);
        }
    }

    private static readonly RequestPacer Fast = new(1000);

    [Test]
    public async Task ClientSendsTheTokenAndJson()
    {
        var server = new Server();
        server.Answers["/api/v1/me"] = (HttpStatusCode.OK, """{"user":"tester"}""");
        server.Answers["/api/v1/items"] = (HttpStatusCode.Created, """{"id":7}""");
        server.Answers["/api/v1/empty"] = (HttpStatusCode.NoContent, "");
        var client = new HistraClient(server.Http, "hst_0123456789", Fast);

        Assert.Equal("tester", (await client.GetAsync("api/v1/me"))!.RootElement.GetProperty("user").GetString());
        Assert.Equal("Bearer hst_0123456789", server.Authorization);
        Assert.Equal(7, (await client.SendAsync(HttpMethod.Post, "api/v1/items", new { tmdb = 8555, kind = "movie" }))!.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("""{"tmdb":8555,"kind":"movie"}""", server.LastBody);
        Assert.Equal<System.Text.Json.JsonDocument?>(null, await client.GetAsync("api/v1/empty"), "no content");
        Assert.Equal<System.Text.Json.JsonDocument?>(null, await client.GetAsync("api/v1/unknown"), "404");
        Assert.True(server.Requests.All(r => r.StartsWith("https://histra.net/", StringComparison.Ordinal)), "only histra.net");
    }

    [Test]
    public async Task ClientWaitsWhenLimitedAndReportsProblems()
    {
        RequestPacer.DelayScale = 0.01;
        try
        {
            var server = new Server { Limited = 2 };
            server.Answers["/api/v1/me"] = (HttpStatusCode.OK, "{}");
            var client = new HistraClient(server.Http, "hst_0123456789", Fast);
            var waits = 0;
            client.Waiting += _ => waits++;
            Assert.True(await client.GetAsync("api/v1/me") is not null && waits == 2, "429 waited out twice");

            server.Answers["/api/v1/me"] = (HttpStatusCode.Unauthorized, "");
            Assert.Equal("Token HISTRA nie je platný – skontrolujte ho v Nastaveniach.", await MessageOf(() => client.GetAsync("api/v1/me")));
            server.Answers["/api/v1/me"] = (HttpStatusCode.OK, "not json");
            Assert.Equal("HISTRA vrátila nečitateľnú odpoveď.", await MessageOf(() => client.GetAsync("api/v1/me")));
            server.Answers["/api/v1/me"] = (HttpStatusCode.OK, new string(' ', HistraClient.MaxResponseBytes + 1));
            Assert.Equal("Odpoveď HISTRA je neprimerane veľká – nepoužila sa.", await MessageOf(() => client.GetAsync("api/v1/me")));
            server.Answers["/api/v1/me"] = (HttpStatusCode.OK, "{}");
            server.RedirectTo = new Uri("https://evil.example/api/v1/me");
            Assert.True((await MessageOf(() => client.GetAsync("api/v1/me")))!.StartsWith("HISTRA presmerovala", StringComparison.Ordinal), "redirect elsewhere refused");
        }
        finally
        {
            RequestPacer.DelayScale = 1;
        }

        var refused = false;
        try
        {
            _ = new HistraClient(new HttpClient(), "bad token");
        }
        catch (ArgumentException)
        {
            refused = true;
        }

        Assert.True(refused, "client refuses an unsafe token");
    }

    // an invented catalog answer: two exact names (one without a year), a sequel, a non-Latin original, one without a TMDB ID
    private const string Films = """
        {"results":[
          {"id":12,"title":"Vymyslený vlak 2","original_title":"Vymyslený vlak 2","release_date":"2021-03-01","external_ids":{"tmdb":"9002"}},
          {"id":0,"title":"Vymyslený vlak","original_title":"Vymyslený vlak","release_date":"2019-05-30","rating":7.5,"external_ids":{"tmdb":"9001"}},
          {"id":0,"title":"Invented Train","original_title":"架空の列車","release_date":"2005-01-01","external_ids":{"tmdb":"9003"}},
          {"id":0,"title":"Bez čísla TMDB","original_title":"Bez čísla TMDB","release_date":"2010-01-01","external_ids":{}},
          {"id":0,"title":"Vymyslený vlak","original_title":"Vymyslený vlak","external_ids":{"tmdb":9004}}
        ]}
        """;

    [Test]
    public async Task CatalogSearchGivesTitlesWithTheirTmdbIds()
    {
        var server = new Server();
        server.Answers["/api/v1/catalog/movies/search"] = (HttpStatusCode.OK, Films);
        server.Answers["/api/v1/catalog/shows/search"] = (HttpStatusCode.OK,
            """{"results":[{"id":30,"title":"Vymyslený seriál","original_title":"Vymyslený seriál","first_air_date":"2008-01-20","episode_count":62,"external_ids":{"tmdb":"9100"}}]}""");
        var catalog = new HistraCatalog(new HistraClient(server.Http, "hst_test-0123456789", new RequestPacer(100)));

        var all = await catalog.SearchAsync(MediaKind.Movie, "Vymyslený vlak", null);
        Assert.Equal("9001,9004,9002,9003", string.Join(",", all.Select(t => t.Id)), "exact names first, no title without a TMDB ID");
        Assert.True(server.Requests[0] == "https://histra.net/api/v1/catalog/movies/search?q=Vymyslený vlak", "only the name is sent (English titles): " + server.Requests[0]);
        Assert.Equal("Bearer hst_test-0123456789", server.Authorization);
        Assert.Equal(2019, all[0].Year);
        Assert.True(all.Single(t => t.Id == 9002).HistraId == 12 && all[0].HistraId is null, "HISTRA's own catalog number; 0 = only passed on from TMDB");
        Assert.Equal(null, all[1].Year);

        var ofYear = await catalog.SearchAsync(MediaKind.Movie, "vymysleny VLAK", 2019);
        Assert.Equal("9001", string.Join(",", ofYear.Select(t => t.Id)), "with a year only that year's title");
        Assert.Equal(2, server.Requests.Count);
        _ = await catalog.SearchAsync(MediaKind.Movie, "Vymyslený vlak", 2021);
        Assert.Equal(2, server.Requests.Count, "the same name is asked once a session");

        // the library name follows the specification as with TMDB: Latin original, else the English title with the ID
        Assert.Equal("Vymyslený vlak (2019)", all[0].ToTitleInfo()!.FolderName);
        Assert.Equal("Invented Train (2005) [tmdbid-9003]", all.Single(t => t.Id == 9003).ToTitleInfo()!.FolderName);

        var series = await catalog.SearchAsync(MediaKind.Series, "Vymyslený seriál", 2008);
        Assert.True(series is [{ Kind: MediaKind.Series, Id: 9100, Year: 2008 }], "series by first_air_date");
    }

    [Test]
    public async Task TitlesComeFromHistraWithoutTmdbOrWhenTmdbFindsNothing()
    {
        var server = new Server();
        server.Answers["/api/v1/catalog/movies/search"] = (HttpStatusCode.OK, Films);
        var histra = new HistraCatalog(new HistraClient(server.Http, "hst_test-0123456789", new RequestPacer(100)));

        // no TMDB key
        var only = new TitleSearch(null, histra);
        var found = await only.FindAsync(MediaKind.Movie, "Vymyslený vlak (2019)");
        Assert.True(found is { Source: "HISTRA", Note: "", Titles: [{ Id: 9001 }] }, "found in the catalog: " + found.Note);
        var otherYear = await only.FindAsync(MediaKind.Movie, "Vymyslený vlak (2018)");
        Assert.Equal(4, otherYear.Titles.Count, "no title of that year: all of the name are offered");
        var byId = await only.FindAsync(MediaKind.Movie, "Vymyslený vlak [tmdbid-9004]");
        Assert.True(byId.Titles is [{ Id: 9004 }], "an ID in the name picks that title");
        var address = await only.FindAsync(MediaKind.Movie, "https://www.themoviedb.org/movie/9001");
        Assert.True(address.Titles.Count == 0 && address.Note.Contains("len TMDB", StringComparison.Ordinal), "an address needs TMDB: " + address.Note);

        // TMDB finds nothing / does not accept the key: the catalog is searched
        server.Answers["/3/search/movie"] = (HttpStatusCode.OK, """{"results":[]}""");
        var tmdb = new TmdbClient(server.Http, "abc123", pacer: new RequestPacer(100));
        var both = new TitleSearch(tmdb, histra);
        var fallback = await both.SearchAsync(MediaKind.Movie, "Vymyslený vlak", 2019);
        Assert.True(fallback is { Source: "HISTRA", Titles: [{ Id: 9001 }] } && fallback.Note.StartsWith("TMDB nič nenašlo", StringComparison.Ordinal), "after TMDB: " + fallback.Note);
        server.Answers["/3/search/movie"] = (HttpStatusCode.Unauthorized, "");
        var refused = await both.FindAsync(MediaKind.Movie, "Vymyslený vlak (2019)");
        Assert.True(refused is { Source: "HISTRA", Titles: [{ Id: 9001 }] } && refused.Note.Contains("Kľúč TMDB", StringComparison.Ordinal), "TMDB refused: " + refused.Note);

        // TMDB alone reports its problem as before
        var refusedAlone = await MessageOfTitles(() => new TitleSearch(tmdb, null).FindAsync(MediaKind.Movie, "Vymyslený vlak (2019)"));
        Assert.True(refusedAlone?.Contains("Kľúč TMDB", StringComparison.Ordinal) == true, "without HISTRA the TMDB problem is shown");
    }

    [Test]
    public async Task HistraChecksWhatTmdbFound()
    {
        // TMDB's answer (search/movie) and HISTRA's answer for the same name are invented
        (TitleSearch Search, Server Server) Setup(string tmdbResults, string histraResults)
        {
            var server = new Server();
            server.Answers["/3/search/movie"] = (HttpStatusCode.OK, "{\"results\":[" + tmdbResults + "]}");
            server.Answers["/api/v1/catalog/movies/search"] = (HttpStatusCode.OK, "{\"results\":[" + histraResults + "]}");
            var tmdb = new TmdbClient(server.Http, "abc123", pacer: new RequestPacer(100));
            return (new TitleSearch(tmdb, new HistraCatalog(new HistraClient(server.Http, "hst_test-0123456789", new RequestPacer(100)))), server);
        }

        const string train2019 = """{"id":9001,"title":"Vymyslený vlak","original_title":"Vymyslený vlak","release_date":"2019-05-30"}""";
        const string otherTrain2019 = """{"id":9500,"title":"Vymyslený vlak","original_title":"Vymyslený vlak","release_date":"2019-02-01"}""";
        const string histraTrain = """{"id":77,"title":"Vymyslený vlak","original_title":"Vymyslený vlak","release_date":"2019-05-30","external_ids":{"tmdb":"9001"}}""";
        const string histraOther = """{"id":0,"title":"Vymyslený vlak","original_title":"Vymyslený vlak","release_date":"2019-02-01","external_ids":{"tmdb":"9500"}}""";

        // the same TMDB ID in HISTRA: confirmed; only the name went to HISTRA
        var (same, server) = Setup(train2019, histraTrain);
        var confirmed = await same.FindAsync(MediaKind.Movie, "Vymyslený vlak (2019)");
        Assert.True(confirmed is { Source: "TMDB", Titles: [{ Id: 9001 }] } && confirmed.Check(confirmed.Titles[0]) == TitleCheck.Confirmed, "confirmed by HISTRA");
        Assert.True(server.Requests.Single(r => r.Contains("histra.net", StringComparison.Ordinal)) == "https://histra.net/api/v1/catalog/movies/search?q=Vymyslený vlak",
            "HISTRA gets only the name: " + string.Join(" | ", server.Requests));

        // HISTRA knows another title of the name and year: TMDB's waits for the user
        var (other, _) = Setup(otherTrain2019, histraTrain);
        var different = await other.FindAsync(MediaKind.Movie, "Vymyslený vlak (2019)");
        Assert.Equal(TitleCheck.Different, different.Check(different.Titles[0]));
        Assert.Equal(9001, different.Alternative(different.Titles[0])?.Id);

        // HISTRA finds nothing: waits for the user too
        var (nothing, _) = Setup(train2019, "");
        var notFound = await nothing.SearchAsync(MediaKind.Movie, "Vymyslený vlak", 2019);
        Assert.Equal(TitleCheck.NotFound, notFound.Check(notFound.Titles[0]));

        // two titles of the same name and year on TMDB: the one HISTRA confirms is the one meant
        var (two, _) = Setup(train2019 + "," + otherTrain2019, histraTrain);
        var both = await two.SearchAsync(MediaKind.Movie, "Vymyslený vlak", 2019);
        Assert.Equal(9001, both.Best("Vymyslený vlak", 2019)?.Id, "chosen by HISTRA");
        // HISTRA has both: the one in its own catalog
        var (twoInHistra, _) = Setup(train2019 + "," + otherTrain2019, histraTrain + "," + histraOther);
        var catalog = await twoInHistra.SearchAsync(MediaKind.Movie, "Vymyslený vlak", 2019);
        Assert.Equal(9001, catalog.Best("Vymyslený vlak", 2019)?.Id, "chosen by HISTRA's own catalog");
        // without HISTRA nothing can be told
        Assert.Equal(null, (both with { HistraTitles = null }).Best("Vymyslený vlak", 2019));

        // HISTRA cannot be asked: TMDB's title stands, not checked, with a note
        var (refused, refusedServer) = Setup(train2019, histraTrain);
        refusedServer.Answers["/api/v1/catalog/movies/search"] = (HttpStatusCode.Unauthorized, "");
        var unchecked_ = await refused.FindAsync(MediaKind.Movie, "Vymyslený vlak (2019)");
        Assert.True(unchecked_.Check(unchecked_.Titles[0]) == TitleCheck.Unavailable && unchecked_.Check(unchecked_.Titles[0]).NeedsConfirmation() &&
                    unchecked_.Note.Contains("nedala opýtať", StringComparison.Ordinal), "HISTRA set up but not reachable: waits for confirmation – " + unchecked_.Note);
        Assert.True(!TitleCheck.NotChecked.NeedsConfirmation() && !TitleCheck.Confirmed.NeedsConfirmation(), "without HISTRA, or confirmed: no wait");

        // nothing anywhere
        var (none, _) = Setup("", "");
        var nowhere = await none.FindAsync(MediaKind.Movie, "Neexistujúci film (2019)");
        Assert.True(nowhere.FoundNowhere, "found nowhere");

        // an address TMDB does not know: HISTRA was not searched, so it is not "found nowhere"
        var unknown = await none.FindAsync(MediaKind.Movie, "https://www.themoviedb.org/movie/424242");
        Assert.True(unknown.Titles.Count == 0 && !unknown.FoundNowhere, "an unknown address is not found nowhere: " + unknown.Note);

        // no exact name (typed differently): the only title of the year in HISTRA's own catalog is meant
        var (loose, _) = Setup(
            """{"id":9001,"title":"The Invented Train","original_title":"The Invented Train","release_date":"2019-05-30"},""" +
            """{"id":9600,"title":"Invented Train Story","original_title":"Invented Train Story","release_date":"2019-01-01"}""",
            """{"id":77,"title":"The Invented Train","original_title":"The Invented Train","release_date":"2019-05-30","external_ids":{"tmdb":"9001"}},""" +
            """{"id":0,"title":"Invented Train Story","original_title":"Invented Train Story","release_date":"2019-01-01","external_ids":{"tmdb":"9600"}}""");
        var looseResults = await loose.SearchAsync(MediaKind.Movie, "Invented Train", 2019);
        Assert.Equal(9001, looseResults.Best("Invented Train", 2019)?.Id, "the one in HISTRA's own catalog");
    }

    [Test]
    public async Task FilmAddressWhereASeriesIsExpectedIsCheckedAmongFilms()
    {
        var server = new Server();
        server.Answers["/3/movie/9001"] = (HttpStatusCode.OK, """{"id":9001,"title":"Vymyslený vlak","original_title":"Vymyslený vlak","release_date":"2019-05-30"}""");
        server.Answers["/api/v1/catalog/movies/search"] = (HttpStatusCode.OK,
            """{"results":[{"id":"77","title":"Vymyslený vlak","original_title":"Vymyslený vlak","release_date":"2019-05-30","external_ids":{"tmdb":"9001"}}]}""");
        var search = new TitleSearch(new TmdbClient(server.Http, "abc123", pacer: new RequestPacer(100)),
            new HistraCatalog(new HistraClient(server.Http, "hst_test-0123456789", new RequestPacer(100))));
        var found = await search.FindAsync(MediaKind.Series, "https://www.themoviedb.org/movie/9001");
        Assert.True(found.Titles is [{ Kind: MediaKind.Movie, Id: 9001 }] && found.Check(found.Titles[0]) == TitleCheck.Confirmed,
            "checked in the films catalog: " + string.Join(" | ", server.Requests));
        Assert.True(found.HistraTitles is [{ HistraId: null }], "an \"id\" that is not a number is ignored, not a crash");
        var checkedAlone = await search.CheckAsync(found.Titles[0]);
        Assert.Equal(TitleCheck.Confirmed, checkedAlone.Check(found.Titles[0]));
    }

    [Test]
    public void TitlesHistraDidNotConfirmWaitForTheirOwnConfirmation()
    {
        var job = new LibraryJob(new LibraryLayout(Path.Combine(Path.GetTempPath(), "universum-unverified")));
        var confirmed = new TitleInfo(MediaKind.Movie, "Vymyslený vlak", 2019, 9001);
        var doubtful = new TitleInfo(MediaKind.Movie, "Iný vlak", 2020, 9500);
        job.MovieSuggestions["a.mkv"] = confirmed;
        job.MovieSuggestions["b.mkv"] = doubtful;
        job.UnverifiedTitles.Add("b.mkv");
        job.ConfirmSuggestions();
        Assert.True(job.Movies.ContainsKey("a.mkv") && !job.Movies.ContainsKey("b.mkv"), "accept suggestions skips the unconfirmed film");
        job.ConfirmTitle("b.mkv");
        Assert.True(job.Movies["b.mkv"] == doubtful && job.UnverifiedTitles.Count == 0, "confirmed for its file");

        var series = new LibraryJob(new LibraryLayout(Path.Combine(Path.GetTempPath(), "universum-unverified"))) { Kind = MediaKind.Series };
        series.SeriesSuggestion = new TitleInfo(MediaKind.Series, "Vymyslený seriál", 2008, 9100);
        series.SeriesUnverified = true;
        series.ConfirmSuggestions();
        Assert.True(series.Series is null, "an unconfirmed series is not accepted with the suggestions");
        series.ConfirmSeries();
        Assert.True(series.Series?.TmdbId == 9100 && !series.SeriesUnverified, "the series confirmed on its own");
    }

    private static async Task<string?> MessageOfTitles(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (TitleServiceException ex)
        {
            return ex.Message;
        }
    }

    private static async Task<string?> MessageOf(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (HistraException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Stand-in HISTRA server.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public Server() => Http = new HttpClient(this);

        public HttpClient Http { get; }

        public Dictionary<string, (HttpStatusCode Status, string Body)> Answers { get; } = [];

        public List<string> Requests { get; } = [];

        public string? Authorization { get; private set; }

        public string? LastBody { get; private set; }

        public int Limited { get; set; }

        public Uri? RedirectTo { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            Authorization = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Limited > 0)
            {
                Limited--;
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            }

            if (RedirectTo is not null)
            {
                request.RequestUri = RedirectTo; // what the handler reports after following a redirect
            }

            var (status, body) = Answers.TryGetValue(request.RequestUri!.AbsolutePath, out var answer) || Answers.TryGetValue(new Uri(Requests[^1]).AbsolutePath, out answer)
                ? answer
                : (HttpStatusCode.NotFound, "");
            return new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
