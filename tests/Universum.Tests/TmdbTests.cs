using System.Net;
using System.Text;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;

namespace Universum.Tests;

/// <summary>TMDB client against a stand-in server with answers in the TMDB format.</summary>
public sealed class TmdbTests
{
    private static readonly RequestPacer Fast = new(1000);

    private const string Creep = """{"id":8555,"title":"Creep","original_title":"Creep","original_language":"en","release_date":"2004-09-10"}""";

    private const string Kantara =
        """{"id":1083637,"title":"Kantara: A Legend Chapter-1","original_title":"ಕಾಂತಾರ","original_language":"kn","release_date":"2025-10-02"}""";

    private const string NinePuzzles =
        """{"id":227191,"name":"Nine Puzzles","original_name":"나인 퍼즐","original_language":"ko","first_air_date":"2025-05-21","seasons":[{"season_number":1,"episode_count":11}]}""";

    [Test]
    public async Task DetailsGiveTheLibraryTitle()
    {
        var server = new Server();
        server.Answers["/3/movie/8555"] = Creep;
        server.Answers["/3/movie/1083637"] = Kantara;
        server.Answers["/3/tv/227191"] = NinePuzzles;
        var client = new TmdbClient(server.Http, "abc123", pacer: Fast);

        Assert.Equal("Creep (2004)", (await client.GetAsync(MediaKind.Movie, 8555))!.ToTitleInfo()!.FolderName);
        Assert.Equal("Kantara A Legend Chapter-1 (2025) [tmdbid-1083637]", (await client.GetAsync(MediaKind.Movie, 1083637))!.ToTitleInfo()!.FolderName);
        var series = (await client.GetAsync(MediaKind.Series, 227191))!;
        Assert.Equal("Nine Puzzles (2025) [tmdbid-227191]", series.ToTitleInfo()!.FolderName);
        Assert.True(series.HasEpisode(new EpisodeNumber(1, 11)) && !series.HasEpisode(new EpisodeNumber(1, 12)) && !series.HasEpisode(new EpisodeNumber(2, 1)), "episodes checked");
        Assert.Equal<TmdbTitle?>(null, await client.GetAsync(MediaKind.Movie, 1), "unknown ID");

        Assert.True(server.Requests.All(r => r.Contains("api_key=abc123", StringComparison.Ordinal) && r.Contains("language=en-US", StringComparison.Ordinal)), "v3 key as parameter, English data");
    }

    [Test]
    public async Task ReadAccessTokenIsSentAsBearer()
    {
        var server = new Server();
        server.Answers["/3/movie/8555"] = Creep;
        var token = "eyJhbGciOiJIUzI1NiJ9." + new string('a', 60) + ".signature";
        var client = new TmdbClient(server.Http, token, pacer: Fast);
        Assert.True(client.UsesBearerToken, "token recognised");
        await client.GetAsync(MediaKind.Movie, 8555);
        Assert.Equal("Bearer " + token, server.Authorization);
        Assert.True(!server.Requests.Single().Contains("api_key", StringComparison.Ordinal), "no key in the address");
    }

    [Test]
    public async Task FindByAddressOrByNameAndYear()
    {
        var server = new Server();
        server.Answers["/3/tv/227191"] = NinePuzzles;
        server.Answers["/3/search/movie"] = """{"results":[""" + Creep + """,{"id":1,"title":"Creep","original_title":"Creep","release_date":"2014-01-17"}]}""";
        var client = new TmdbClient(server.Http, "abc123", pacer: Fast);

        var byAddress = await client.FindAsync(MediaKind.Movie, "https://www.themoviedb.org/tv/227191-nine-puzzles");
        Assert.Equal(MediaKind.Series, byAddress.Single().Kind, "the address decides the kind");

        var results = await client.FindAsync(MediaKind.Movie, "Creep (2004)");
        Assert.Equal(2, results.Count);
        Assert.True(server.Requests[^1].Contains("query=Creep", StringComparison.Ordinal) && server.Requests[^1].Contains("year=2004", StringComparison.Ordinal), server.Requests[^1]);
        Assert.Equal(8555, TmdbClient.ExactMatch(results, "creep", 2004)?.Id, "name and year");
        Assert.Equal<TmdbTitle?>(null, TmdbClient.ExactMatch(results, "Creep", null), "two films of that name");
    }

    [Test]
    public async Task ErrorsAndCache()
    {
        var server = new Server { Status = HttpStatusCode.Unauthorized };
        var client = new TmdbClient(server.Http, "wrong", pacer: Fast);
        var error = await ThrowsAsync(() => client.GetAsync(MediaKind.Movie, 8555));
        Assert.True(error.Message.Contains("Kľúč TMDB", StringComparison.Ordinal), error.Message);

        var cacheFile = Path.Combine(Path.GetTempPath(), "universum-tmdb-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var online = new Server();
            online.Answers["/3/movie/8555"] = Creep;
            var first = new TmdbClient(online.Http, "secret-key", cacheFile, Fast);
            await first.GetAsync(MediaKind.Movie, 8555);
            first.SaveCache();
            Assert.True(!File.ReadAllText(cacheFile).Contains("secret-key", StringComparison.Ordinal), "the key is not cached");

            var offline = new Server { Status = HttpStatusCode.InternalServerError };
            var second = new TmdbClient(offline.Http, "secret-key", cacheFile, Fast);
            Assert.Equal("Creep", (await second.GetAsync(MediaKind.Movie, 8555))?.Title, "answered from the cache");
            Assert.Equal(0, offline.Requests.Count, "no request");
        }
        finally
        {
            File.Delete(cacheFile);
        }
    }

    // an invented film: English title, Czech and Slovak translations, a Czech alternative title
    private const string GoldenCarpFound =
        """{"id":990001,"title":"The Golden Carp","original_title":"The Golden Carp","original_language":"en","release_date":"2021-11-05"}""";

    private const string GoldenCarp =
        """
        {"id":990001,"title":"The Golden Carp","original_title":"The Golden Carp","original_language":"en","release_date":"2021-11-05",
         "alternative_titles":{"titles":[{"iso_3166_1":"CZ","title":"Zlatý kapr: Návrat","type":""}]},
         "translations":{"translations":[
           {"iso_3166_1":"CZ","iso_639_1":"cs","data":{"title":"Zlatý kapr","overview":"Dlhý popis deja, ktorý sa do cache neukladá."}},
           {"iso_3166_1":"SK","iso_639_1":"sk","data":{"title":"Zlatý kapor","overview":""}},
           {"iso_3166_1":"US","iso_639_1":"en","data":{"title":"","overview":""}}]}}
        """;

    private const string OtherCarp = """{"id":990002,"title":"Carp Kingdom","original_title":"Carp Kingdom","original_language":"en","release_date":"2022-03-01"}""";

    [Test]
    public async Task FileNamedInCzechGetsTheOriginalTitle()
    {
        var server = new Server();
        server.Answers["/3/search/movie"] = """{"results":[""" + OtherCarp + "," + GoldenCarpFound + "]}";
        server.Answers["/3/movie/990001"] = GoldenCarp;
        var cacheFile = Path.Combine(Path.GetTempPath(), "universum-tmdb-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var tmdb = new TmdbClient(server.Http, "abc123", cacheFile, Fast);
            var search = new TitleSearch(tmdb, null);

            // the Czech name of the file is one of the film's translations: the original title and TMDB's year
            var guess = TitleGuess.ForFile(Path.Combine("x", "Filmy", "Zlatý kapr.2021.WEB-DL.CZ.1080p.mkv")).Title!;
            Assert.Equal(new NameGuess("Zlatý kapr", 2021, null), guess, "dots after a name with spaces");
            var results = await search.SearchAsync(MediaKind.Movie, guess.Name, guess.Year);
            Assert.Equal("The Golden Carp (2021)", results.Best(guess.Name, guess.Year)?.ToTitleInfo()?.FolderName, "translation");
            Assert.Equal("The Golden Carp (2021)", results.Best("Zlaty kapor", 2021)?.ToTitleInfo()?.FolderName, "Slovak, without diacritics");
            Assert.Equal("The Golden Carp (2021)", results.Best("Zlatý kapr - Návrat", 2021)?.ToTitleInfo()?.FolderName, "alternative title");

            // the year of the release here, a year after the premiere
            Assert.Equal(990001, (await search.SearchAsync(MediaKind.Movie, "Zlatý kapr", 2022)).Best("Zlatý kapr", 2022)?.Id, "a year later");

            // a name no title carries: no match; the only title of the year is a suggestion to confirm
            var unknown = await search.SearchAsync(MediaKind.Movie, "Kapor v rybníku", 2022);
            Assert.Equal<TmdbTitle?>(null, unknown.Best("Kapor v rybníku", 2022), "no name matches");
            Assert.Equal<TmdbTitle?>(null, unknown.OnlyOne(2022), "two titles of 2021–2023");
            Assert.Equal(990002, unknown.OnlyOne(2023)?.Id, "the only one of 2022–2024");

            tmdb.SaveCache();
            var cache = File.ReadAllText(cacheFile);
            Assert.True(cache.Contains("Zlat", StringComparison.Ordinal) && !cache.Contains("popis", StringComparison.Ordinal), "only the names are cached");
            Assert.True(server.Requests.Count(r => r.Contains("append_to_response=alternative_titles,translations", StringComparison.Ordinal)) <= 2,
                "the names are read once per title");
        }
        finally
        {
            File.Delete(cacheFile);
        }
    }

    [Test]
    public async Task ATranslationOfTheYearWinsOverANeighbour()
    {
        // two invented films carry the same Czech translation; the file has the year of the second
        const string earlier = """{"id":990011,"title":"Carp Returns","original_title":"Carp Returns","release_date":"2020-05-01"}""";
        const string exact = """{"id":990012,"title":"The Carp Comes Back","original_title":"The Carp Comes Back","release_date":"2021-05-01"}""";
        const string names = ""","alternative_titles":{"titles":[]},"translations":{"translations":[{"iso_639_1":"cs","data":{"title":"Kapr návrat"}}]}}""";
        var server = new Server();
        server.Answers["/3/search/movie"] = """{"results":[""" + earlier + "," + exact + "]}";
        server.Answers["/3/movie/990011"] = earlier[..^1] + names;
        server.Answers["/3/movie/990012"] = exact[..^1] + names;
        var search = new TitleSearch(new TmdbClient(server.Http, "abc123", pacer: Fast), null);

        var results = await search.SearchAsync(MediaKind.Movie, "Kapr návrat", 2021);
        Assert.Equal(990012, results.Best("Kapr návrat", 2021)?.Id, "the film of the year, though TMDB lists the neighbour first");
        Assert.Equal(990011, (await search.SearchAsync(MediaKind.Movie, "Kapr návrat", 2019)).Best("Kapr návrat", 2019)?.Id, "a neighbour when no film of the year has the name");

        // two films of the same year with the same translation: none is chosen (the user picks)
        const string twin = """{"id":990013,"title":"Carp Again","original_title":"Carp Again","release_date":"2021-09-01"}""";
        server.Answers["/3/search/movie"] = """{"results":[""" + exact + "," + twin + "]}";
        server.Answers["/3/movie/990013"] = twin[..^1] + names;
        var twins = await new TitleSearch(new TmdbClient(server.Http, "abc123", pacer: Fast), null).SearchAsync(MediaKind.Movie, "Kapr návrat", 2021);
        Assert.Equal<TmdbTitle?>(null, twins.Best("Kapr návrat", 2021), "twins of one year");
    }

    [Test]
    public async Task RequestsAreSpacedOut()
    {
        var pacer = new RequestPacer(20);
        var running = 0;
        var most = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => pacer.RunAsync(async () =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await Task.Delay(5);
            Interlocked.Decrement(ref running);
            return 0;
        })));
        Assert.Equal(1, most, "one request at a time");
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(240), $"6 requests at 20 per second take at least 0.25 s ({watch.Elapsed.TotalMilliseconds:0} ms)");
        Assert.Equal(6, pacer.Started);
    }

    [Test]
    public async Task TooManyRequestsIsWaitedOut()
    {
        var server = new Server { Limited = 2, RetryAfterSeconds = 1 };
        server.Answers["/3/movie/8555"] = Creep;
        var client = new TmdbClient(server.Http, "abc123", pacer: new RequestPacer(100));
        var waits = new List<TimeSpan>();
        client.Waiting += waits.Add;
        RequestPacer.DelayScale = 0.05;
        try
        {
            Assert.Equal("Creep", (await client.GetAsync(MediaKind.Movie, 8555))?.Title, "answer after waiting");
            Assert.Equal(3, server.Requests.Count, "two refused, the third answered");
            Assert.Sequence([TimeSpan.FromSeconds(0.05), TimeSpan.FromSeconds(0.05)], waits, "waited as long as TMDB asked (Retry-After)");

            var always = new Server { Limited = int.MaxValue };
            var stubborn = new TmdbClient(always.Http, "abc123", pacer: new RequestPacer(100));
            var error = await ThrowsAsync(() => stubborn.GetAsync(MediaKind.Movie, 8555));
            Assert.True(error.Message.Contains("obmedzilo", StringComparison.Ordinal), error.Message);
            Assert.Equal(5, always.Requests.Count, "four retries, then a clear message");
        }
        finally
        {
            RequestPacer.DelayScale = 1;
        }
    }

    private static async Task<TmdbException> ThrowsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (TmdbException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("expected TmdbException");
    }

    /// <summary>Answers requests by path; records them.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public Server() => Http = new HttpClient(this);

        public HttpClient Http { get; }

        public Dictionary<string, string> Answers { get; } = [];

        public List<string> Requests { get; } = [];

        public string? Authorization { get; private set; }

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        /// <summary>How many requests are answered with 429 first.</summary>
        public int Limited { get; set; }

        public int? RetryAfterSeconds { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            Authorization = request.Headers.Authorization?.ToString();
            if (Limited > 0)
            {
                Limited--;
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                if (RetryAfterSeconds is { } seconds)
                {
                    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                }

                return Task.FromResult(limited);
            }

            var response = Status != HttpStatusCode.OK
                ? new HttpResponseMessage(Status)
                : Answers.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
