using System.Net;
using System.Text;
using Universum.Core.Library;
using Universum.Core.Library.Histra;

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
