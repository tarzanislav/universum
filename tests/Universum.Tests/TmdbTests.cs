using System.Net;
using System.Text;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;

namespace Universum.Tests;

/// <summary>TMDB client against a stand-in server with answers in the TMDB format.</summary>
public sealed class TmdbTests
{
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
        var client = new TmdbClient(server.Http, "abc123");

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
        var client = new TmdbClient(server.Http, token);
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
        var client = new TmdbClient(server.Http, "abc123");

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
        var client = new TmdbClient(server.Http, "wrong");
        var error = await ThrowsAsync(() => client.GetAsync(MediaKind.Movie, 8555));
        Assert.True(error.Message.Contains("Kľúč TMDB", StringComparison.Ordinal), error.Message);

        var cacheFile = Path.Combine(Path.GetTempPath(), "universum-tmdb-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var online = new Server();
            online.Answers["/3/movie/8555"] = Creep;
            var first = new TmdbClient(online.Http, "secret-key", cacheFile);
            await first.GetAsync(MediaKind.Movie, 8555);
            first.SaveCache();
            Assert.True(!File.ReadAllText(cacheFile).Contains("secret-key", StringComparison.Ordinal), "the key is not cached");

            var offline = new Server { Status = HttpStatusCode.InternalServerError };
            var second = new TmdbClient(offline.Http, "secret-key", cacheFile);
            Assert.Equal("Creep", (await second.GetAsync(MediaKind.Movie, 8555))?.Title, "answered from the cache");
            Assert.Equal(0, offline.Requests.Count, "no request");
        }
        finally
        {
            File.Delete(cacheFile);
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            Authorization = request.Headers.Authorization?.ToString();
            var response = Status != HttpStatusCode.OK
                ? new HttpResponseMessage(Status)
                : Answers.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
