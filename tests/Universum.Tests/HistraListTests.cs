using System.Net;
using System.Text;
using Universum.Core.Library;
using Universum.Core.Library.Histra;
using Universum.Core.Text;

namespace Universum.Tests;

/// <summary>
/// HISTRA lists (only read, owner's decision 28. 10. 2026), posters and title links. All lists and titles here are
/// invented.
/// </summary>
public sealed class HistraListTests
{
    private static readonly RequestPacer Fast = new(1000);

    [Test]
    public async Task OverviewIsReadInPages()
    {
        var server = new Server(uri => uri.Query.Contains("offset=0", StringComparison.Ordinal)
            ? """
              {"lists":[
                {"id":7,"name":"Vymyslené filmy‮","description":"","visibility":"public","item_count":3,"updated_at":"2026-10-20T10:00:00.123456+00:00","owner_name":"","is_mine":true,"preview_posters":["/abc123.jpg","../etc/passwd","https://evil.example/x.jpg","/def_456-x.png"]},
                {"id":"8","name":"","item_count":0,"is_mine":false}
              ]}
              """
            : """{"lists":[{"id":7,"name":"Vymyslené filmy","item_count":3}]}""");
        var lists = await new HistraLists(new HistraClient(server.Http, "hst_0123456789", Fast)).OverviewAsync();

        Assert.Equal(2, lists.Count, "lists");
        Assert.Equal("Vymyslené filmy", lists[0].Name, "hidden characters removed");
        Assert.Sequence(["/abc123.jpg", "/def_456-x.png"], lists[0].PreviewPosters, "only poster paths of the TMDB form");
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 0, 0, 123, TimeSpan.Zero).AddTicks(4560), lists[0].UpdatedAt, "updated");
        Assert.True(lists[0].IsMine && lists[0].ItemCount == 3, "mine, three titles");
        Assert.Equal("List 8", lists[1].Name, "a list without a name, its number as a text");
        Assert.Sequence(
            ["https://histra.net/api/v1/lists?limit=500&offset=0", "https://histra.net/api/v1/lists?limit=500&offset=2"],
            server.Requests, "pages until nothing new comes");
    }

    [Test]
    public async Task ListIsReadPageByPageWhenHistraCutsTheLimit()
    {
        // HISTRA gives at most two titles per page here, whatever the limit
        var server = new Server(uri =>
        {
            var offset = uri.Query.Contains("offset=2", StringComparison.Ordinal) ? 2 : 0;
            var items = offset == 0
                ? """
                  {"media_type":"movie","media_id":101,"title":"Vymyšlený návrat","original_title":"The Invented Return","year":2024,"overview":"Popis\u0007 filmu.","rating":7.25,"runtime":118,"poster_path":"/inv1.jpg","backdrop_path":"/b.jpg","external_ids":{"imdb":"tt0000101","tmdb":"9101","trakt":"501","tvdb":null},"genres":["Drama","Sci-Fi"],"certification":"12"},
                  {"media_type":"show","media_id":"102","title":"Seriál z ničoho","original_title":"Seriál z ničoho","year":2021,"status":"Ended","poster_path":"/../x.jpg","external_ids":{"imdb":"bad","tmdb":9102,"tvdb":"7102"}}
                  """
                : """
                  {"media_type":"movie","media_id":103,"title":"Bez originálu","year":1999,"external_ids":{}},
                  {"media_type":"person","media_id":104,"title":"Nie je titul"}
                  """;
            return $$"""{"list":{"id":7,"name":"Vymyslené","item_count":4,"is_mine":true},"items":[{{items}}]}""";
        });
        var reports = new List<(int, int)>();
        var list = await new HistraLists(new HistraClient(server.Http, "hst_0123456789", Fast))
            .ReadAsync(7, new Progress<(int Read, int Total)>(reports.Add));

        Assert.True(list is not null, "list read");
        Assert.Sequence(
            ["https://histra.net/api/v1/lists/7?extended=full&lang=cs-CZ&limit=500&offset=0",
             "https://histra.net/api/v1/lists/7?extended=full&lang=cs-CZ&limit=500&offset=2",
             "https://histra.net/api/v1/lists/7?extended=full&lang=cs-CZ&limit=500&offset=4"],
            server.Requests, "the next page starts after what came, until a page brings nothing new");
        Assert.Equal(3, list!.Items.Count, "the person is no title");
        var film = list.Items[0];
        Assert.Equal(MediaKind.Movie, film.Kind, "film");
        Assert.Equal("The Invented Return (2024)", film.Caption, "original title and year");
        Assert.Equal("Vymyšlený návrat", film.Title, "Czech title");
        Assert.Equal("Popis filmu.", film.Overview, "control characters removed");
        Assert.Equal(7.3, film.Rating, "rating");
        Assert.Equal(new TitleIds(101, 9101, "tt0000101", 501, null), film.Ids, "IDs as texts or numbers");
        Assert.Equal("/inv1.jpg", film.PosterPath, "poster");
        Assert.Sequence(["genres: Drama, Sci-Fi", "certification: 12"], film.Other.Select(o => $"{o.Name}: {o.Value}"), "further values as they came");
        var series = list.Items[1];
        Assert.True(series.Kind == MediaKind.Series && series.Status == "Ended" && series.PosterPath is null, "series, unsafe poster path left out");
        Assert.Equal(new TitleIds(102, 9102, null, null, 7102), series.Ids, "invalid IMDb ID left out");
        Assert.Equal("Bez originálu (1999)", list.Items[2].Caption, "without an original title the title is used");
    }

    [Test]
    public async Task ListUnknownToHistraIsNull()
    {
        var server = new Server(_ => null);
        Assert.Equal<HistraList?>(null, await new HistraLists(new HistraClient(server.Http, "hst_0123456789", Fast)).ReadAsync(9), "404");
    }

    [Test]
    public void StoreKeepsTheLists()
    {
        var folder = Path.Combine(Path.GetTempPath(), "universum-lists-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HistraListStore(Path.Combine(folder, "histra-lists.json"));
            Assert.Equal(0, store.Load().Count, "nothing yet");
            var item = new HistraListItem(MediaKind.Series, "Názov", "Original", 2020, "Popis", 8.1, 45, "Ended", "/p.jpg",
                new TitleIds(5, 6, "tt0000006", null, 7), [new TitleDetail("genres", "Drama")]);
            var list = new HistraList(new HistraListInfo(3, "Vymyslený list", "", "private", 1, null, "", true, ["/p.jpg"]), [item],
                new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.FromHours(1))) { MissingInHistra = true };
            store.Save([list]);
            var loaded = store.Load();
            Assert.Equal(1, loaded.Count, "one list");
            Assert.True(loaded[0].MissingInHistra && loaded[0].Info.Name == "Vymyslený list" && loaded[0].ReadAt == list.ReadAt, "list kept");
            Assert.Equal(item.Ids, loaded[0].Items[0].Ids, "IDs kept");
            Assert.Equal("Original (2020)", loaded[0].Items[0].Caption, "title kept");
            Assert.Equal("genres", loaded[0].Items[0].Other[0].Name, "further values kept");
            File.WriteAllText(store.File, "{broken");
            Assert.Equal(0, store.Load().Count, "an unreadable file gives no lists");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void WebTextIsCleaned()
    {
        Assert.Equal("a b c", WebText.Clean(" a\r\n\tb \u0000 c "), "white space and control characters");
        Assert.Equal("evilgpj.exe", WebText.Clean("evil‮gpj.exe"), "direction override removed");
        Assert.Equal("ab", WebText.Clean("a​b"), "zero-width space removed");
        Assert.Equal("abc", WebText.Clean("abcdef", 3), "cut");
        Assert.Equal("", WebText.Clean(null), "null");
    }

    [Test]
    public void TitleLinksGoToKnownSites()
    {
        Assert.Equal("https://histra.net/movie/29", TitleLinks.Histra(MediaKind.Movie, 29)!.AbsoluteUri, "HISTRA film");
        Assert.Equal("https://histra.net/show/30", TitleLinks.Histra(MediaKind.Series, 30)!.AbsoluteUri, "HISTRA series");
        Assert.Equal("https://www.themoviedb.org/tv/1396", TitleLinks.Tmdb(MediaKind.Series, 1396)!.AbsoluteUri, "TMDB series");
        Assert.Equal("https://www.themoviedb.org/movie/603", TitleLinks.Tmdb(MediaKind.Movie, 603)!.AbsoluteUri, "TMDB film");
        Assert.Equal("https://www.imdb.com/title/tt0133093/", TitleLinks.Imdb("tt0133093")!.AbsoluteUri, "IMDb");
        Assert.True(TitleLinks.Imdb("tt01/../x") is null && TitleLinks.Histra(MediaKind.Movie, 0) is null && TitleLinks.Tmdb(MediaKind.Movie, null) is null, "no link without a valid ID");
        Assert.Equal("https://www.csfd.cz/hledat/?q=Vymy%C5%A1len%C3%BD%20film%20%26%20%3F", TitleLinks.CsfdSearch(" Vymyšlený film & ? ")!.AbsoluteUri, "ČSFD search, encoded");
    }

    [Test]
    public async Task PostersAreCheckedAndKept()
    {
        var folder = Path.Combine(Path.GetTempPath(), "universum-posters-" + Guid.NewGuid().ToString("N"));
        try
        {
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0 };
            var server = new Server(_ => null)
            {
                Files =
                {
                    ["/t/p/w154/good.jpg"] = jpeg,
                    ["/t/p/w342/good.jpg"] = jpeg,
                    ["/t/p/w154/script.jpg"] = Encoding.ASCII.GetBytes("<script>alert(1)</script>"),
                    ["/t/p/w154/huge.jpg"] = [.. jpeg, .. new byte[PosterCache.MaxBytes]],
                },
            };
            var posters = new PosterCache(server.Http, folder, PosterCache.DefaultServer);

            var small = await posters.GetAsync("/good.jpg", PosterSize.Small);
            Assert.True(small is not null && File.Exists(small) && small.Contains("w154", StringComparison.Ordinal), "small poster stored");
            Assert.True(await posters.GetAsync("/good.jpg", PosterSize.Large) is { } large && large.Contains("w342", StringComparison.Ordinal), "large poster stored");
            var asked = server.Requests.Count;
            Assert.Equal(small, await posters.GetAsync("/good.jpg", PosterSize.Small), "from the disk");
            Assert.Equal(asked, server.Requests.Count, "downloaded once");
            Assert.Equal<string?>(null, await posters.GetAsync("/script.jpg", PosterSize.Small), "not a picture");
            Assert.Equal<string?>(null, await posters.GetAsync("/huge.jpg", PosterSize.Small), "too large");
            Assert.Equal<string?>(null, await posters.GetAsync("/missing.jpg", PosterSize.Small), "404");
            asked = server.Requests.Count;
            Assert.Equal<string?>(null, await posters.GetAsync("/missing.jpg", PosterSize.Small), "a failed poster");
            Assert.Equal(asked, server.Requests.Count, "is not asked for again in the same run");
            Assert.Equal<string?>(null, await posters.GetAsync("../../x.jpg", PosterSize.Small), "path outside");
            Assert.True(server.Requests.All(r => r.StartsWith("https://image.tmdb.org/t/p/w", StringComparison.Ordinal)), "only the poster server");

            server.RedirectTo = new Uri("https://evil.example/t/p/w154/good.jpg");
            Assert.Equal<string?>(null, await posters.GetAsync("/other.jpg", PosterSize.Small), "a redirect elsewhere is refused");
            Assert.True(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).All(f => f.EndsWith("good.jpg", StringComparison.Ordinal)), "only good posters on the disk");

            Assert.Equal("https://img.example/posters/", PosterCache.ParseServer(" https://img.example/posters ")!.AbsoluteUri, "slash added");
            foreach (var bad in new[] { "http://image.tmdb.org/t/p/", "https://user:pw@img.example/", "https://img.example/?q=1", "ftp://x/", "nonsense", "" })
            {
                Assert.True(PosterCache.ParseServer(bad) is null, "refused server: " + bad);
            }
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>HISTRA and the poster server with invented answers: JSON by address, or files.</summary>
    private sealed class Server(Func<Uri, string?> answer) : HttpMessageHandler
    {
        private HttpClient? _http;

        public HttpClient Http => _http ??= new HttpClient(this);

        public List<string> Requests { get; } = [];

        public Dictionary<string, byte[]> Files { get; } = [];

        public Uri? RedirectTo { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (RedirectTo is not null)
            {
                request.RequestUri = RedirectTo;
            }

            if (Files.TryGetValue(request.RequestUri!.AbsolutePath, out var bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
            }

            var body = Files.Count == 0 ? answer(request.RequestUri) : null;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
