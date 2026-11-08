using System.Net.Sockets;
using System.Text;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;

namespace Universum.Tests;

/// <summary>
/// Zásobník TMDB (owner, 8. 11. 2026): titles gathered from the browser in a note, the links the browser hands over, the
/// titles of TMDB's tabs, and the receiver on 127.0.0.1 that takes only TMDB pages with the key. Invented titles only.
/// </summary>
public sealed class TmdbTrayTests
{
    [Test]
    public void TitlesKeptInTheNote()
    {
        var note = TmdbTray.Add(null, new TmdbTrayEntry(MediaKind.Movie, 111, "Vymyslený film (2026)"));
        note = TmdbTray.Add(note, new TmdbTrayEntry(MediaKind.Series, 222));
        Assert.Sequence(["https://www.themoviedb.org/tv/222", "[Vymyslený film (2026)](https://www.themoviedb.org/movie/111)"], note.Split('\n'), "the newest first, a link per line");

        note = TmdbTray.Name(note, MediaKind.Series, 222, "Vymyslený seriál (2025)");
        note = TmdbTray.Add(note, new TmdbTrayEntry(MediaKind.Movie, 111));
        var entries = TmdbTray.Read("moja poznámka\n" + note);
        Assert.Sequence(["Movie 111 Vymyslený film (2026)", "Series 222 Vymyslený seriál (2025)"], entries.Select(e => $"{e.Kind} {e.Id} {e.Name}"),
            "added again: moved up, its name kept; text of the user is no title");
        Assert.Equal("https://www.themoviedb.org/movie/111", entries[0].FieldText, "a title field takes the address");
        Assert.Equal(1, TmdbTray.Read(TmdbTray.Remove(note, MediaKind.Movie, 111)).Count, "removed");
        Assert.True(TmdbTray.FromPage("https://www.themoviedb.org/tv/222-vymysleny-serial/season/1/episode/3?language=sk", null) is { Kind: MediaKind.Series, Id: 222 } &&
                    TmdbTray.FromPage("https://www.themoviedb.org/movie/111-vymysleny-film/cast", null) is { Kind: MediaKind.Movie, Id: 111 }, "a season, an episode, the cast: the same title");
        Assert.Equal("moja poznámka", TmdbTray.Clear("moja poznámka\n" + note), "emptied: the own line stays, no TMDB title");
    }

    [Test]
    public void OnlyTmdbPagesAreTaken()
    {
        var entry = TmdbTray.FromLink("universum://tmdb?u=" + Uri.EscapeDataString("https://www.themoviedb.org/tv/222-vymysleny-serial?language=sk") +
                                      "&t=" + Uri.EscapeDataString("Vymyslený seriál (TV Series 2014–2016) — The Movie Database (TMDB)"));
        Assert.True(entry is { Kind: MediaKind.Series, Id: 222, Name: "Vymyslený seriál (2014)" }, "a series of TMDB with its title – " + entry);
        Assert.True(TmdbTray.FromLink("universum://tmdb?u=" + Uri.EscapeDataString("https://www.themoviedb.org/movie/111")) is { Kind: MediaKind.Movie, Id: 111, Name: null },
            "a film without a title");
        foreach (var bad in new[]
                 {
                     "universum://tmdb?u=" + Uri.EscapeDataString("https://evil.example/movie/111"),
                     "universum://tmdb?u=" + Uri.EscapeDataString("https://www.themoviedb.org.evil.example/movie/111"),
                     "universum://tmdb?u=" + Uri.EscapeDataString("file:///C:/Windows/movie/111"),
                     "universum://tmdb?u=" + Uri.EscapeDataString("https://www.themoviedb.org/person/333"),
                     "universum://ine?u=" + Uri.EscapeDataString("https://www.themoviedb.org/movie/111"),
                     "https://www.themoviedb.org/movie/111",
                     "C:\\Filmy\\vymyslený.mkv",
                     "universum://tmdb?u=" + new string('a', 5000),
                 })
        {
            Assert.True(TmdbTray.FromLink(bad) is null, "refused: " + bad[..Math.Min(bad.Length, 80)]);
        }
    }

    [Test]
    public void TitlesOfTmdbTabs()
    {
        Assert.Equal(new TabTitle("Vymyslený film", 2026, MediaKind.Movie), TmdbTray.FromTabTitle("Vymyslený film (2026) — The Movie Database (TMDB)"), "a film");
        Assert.Equal(new TabTitle("Vymyslený seriál", 2014, MediaKind.Series), TmdbTray.FromTabTitle("Vymyslený seriál (TV Series 2014-2016) - The Movie Database (TMDB)"), "a series");
        Assert.Equal(new TabTitle("Vymyslený seriál", 2024, MediaKind.Series), TmdbTray.FromTabTitle("Vymyslený seriál (TV seriál 2024– ) — The Movie Database (TMDB)"), "a series, Czech words, still running");
        Assert.Equal(new TabTitle("Vymyslený film: Časť 2", 2025, MediaKind.Movie), TmdbTray.FromTabTitle("Vymyslený film: Časť 2 (2025) — Obsadenie — The Movie Database (TMDB)"), "a sub-page");
        Assert.True(TmdbTray.FromTabTitle("Vymyslený film (2026) – YouTube") is null && TmdbTray.FromTabTitle("Popular Movies — The Movie Database (TMDB)") is null && TmdbTray.FromTabTitle(null) is null,
            "no title of a TMDB film or series");
    }

    [Test]
    public async Task ReceiverTakesOnlyTmdbPagesWithTheKey()
    {
        var key = TmdbTrayListener.NewKey();
        var received = new List<TmdbTrayEntry>();
        var automatic = true;
        using var door = TmdbTrayListener.Start(0, key, entry =>
        {
            lock (received)
            {
                received.Add(entry);
            }

            return Task.CompletedTask;
        }, () => TmdbUserScript.Text(4711, key), () => automatic);

        async Task<string> Send(string request)
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", door.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        static string Post(string key, string body) =>
            $"POST /tmdb HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Universum-Key: {key}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";

        var good = """{"u":"https://www.themoviedb.org/movie/111-vymysleny-film","t":"Vymyslený film (2026) — The Movie Database (TMDB)"}""";
        Assert.True((await Send(Post(key, good))).StartsWith("HTTP/1.1 204", StringComparison.Ordinal), "a TMDB film with the key");
        Assert.True((await Send(Post("0123456789abcdef0123456789abcdef", good))).StartsWith("HTTP/1.1 403", StringComparison.Ordinal), "another key: refused");
        Assert.True((await Send(Post(key, """{"u":"https://evil.example/movie/1"}"""))).StartsWith("HTTP/1.1 422", StringComparison.Ordinal), "no TMDB page");
        Assert.True((await Send(Post(key, "nie je to JSON"))).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "no JSON");
        Assert.True((await Send("GET /tmdb HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 404", StringComparison.Ordinal), "nothing else is answered");
        Assert.True((await Send("hello\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "no HTTP");
        var script = await Send($"GET /universum.user.js?k={key} HTTP/1.1\r\n\r\n");
        Assert.True(script.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && script.Contains("// ==UserScript==", StringComparison.Ordinal) && script.Contains(key, StringComparison.Ordinal),
            "the script to the one who knows the key");
        Assert.True((await Send("GET /universum.user.js?k=zly HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 403", StringComparison.Ordinal), "not without it");
        lock (received)
        {
            Assert.True(received is [{ Kind: MediaKind.Movie, Id: 111, Name: "Vymyslený film (2026)" }], "one title received");
        }

        // the automatic gathering switched off: pages sent by themselves are not taken, the button's still are
        automatic = false;
        Assert.True((await Send(Post(key, """{"u":"https://www.themoviedb.org/movie/112","a":true}"""))).StartsWith("HTTP/1.1 202", StringComparison.Ordinal), "by itself: not taken");
        Assert.True((await Send(Post(key, """{"u":"https://www.themoviedb.org/movie/113"}"""))).StartsWith("HTTP/1.1 202", StringComparison.Ordinal), "the script 1.0 (by itself): not taken");
        Assert.True((await Send(Post(key, """{"u":"https://www.themoviedb.org/tv/114","a":false}"""))).StartsWith("HTTP/1.1 204", StringComparison.Ordinal), "the button: taken");
        automatic = true;
        Assert.True((await Send(Post(key, """{"u":"https://www.themoviedb.org/movie/115","a":true}"""))).StartsWith("HTTP/1.1 204", StringComparison.Ordinal), "switched on again: taken");
        lock (received)
        {
            Assert.Equal("111 114 115", string.Join(' ', received.Select(e => e.Id)), "what was taken");
        }

        Assert.Throws<ArgumentException>(() => TmdbTrayListener.Start(0, "kratky", _ => Task.CompletedTask, () => ""), "a short key is no key");
    }

    [Test]
    public void TheScriptRunsOnlyOnTmdbAndTalksOnlyToThisComputer()
    {
        var script = TmdbUserScript.Text(47219, "0123456789abcdef0123456789abcdef");
        var matches = script.Split('\n').Where(l => l.Contains("@match", StringComparison.Ordinal)).Select(l => l.Split("@match")[1].Trim()).ToList();
        Assert.Sequence(["https://www.themoviedb.org/movie/*", "https://www.themoviedb.org/tv/*", "https://www.csfd.cz/film/*", "https://www.csfd.sk/film/*"], matches,
            "only the film and series pages of TMDB and ČSFD");
        Assert.True(script.Contains("@connect      127.0.0.1", StringComparison.Ordinal) && !script.Contains("@connect      *", StringComparison.Ordinal) &&
                    script.Contains("'http://127.0.0.1:' + PORT", StringComparison.Ordinal), "only 127.0.0.1");
        Assert.True(script.Contains("sessionStorage.getItem(sent)", StringComparison.Ordinal) && script.Contains("AUTOMATIC && !known", StringComparison.Ordinal),
            "a film or series is sent by itself once per tab (its seasons and episodes are the same title)");
        Assert.True(script.Contains("a: !button", StringComparison.Ordinal), "it says whether it sent by itself (UNIVERSUM takes those only while gathering)");
        Assert.True(matches.Contains("https://www.csfd.cz/film/*") && script.Contains("'/poster'", StringComparison.Ordinal) && script.Contains("imdb.com/title/tt", StringComparison.Ordinal),
            "ČSFD's film pages and posters too (1.3)");
        Assert.True(TmdbUserScript.Bookmarklet.StartsWith("javascript:location.href='universum://tmdb?u='", StringComparison.Ordinal), "the bookmark");
    }

    [Test]
    public void CsfdPagesAndPostersOnlyFromTheirServers()
    {
        var page = TmdbTray.FromCsfdPage("https://www.csfd.cz/film/12345-vymysleny-film/galerie/", "Vymyslený film (2026) | ČSFD.cz", "tt0000001");
        Assert.Equal(new CsfdPage(12345, "tt0000001", new TabTitle("Vymyslený film", 2026, MediaKind.Movie)), page, "a film of ČSFD with its IMDb ID");
        Assert.Equal(new TabTitle("Vymyslený seriál", 2024, MediaKind.Series), TmdbTray.FromCsfdTitle("Vymyslený seriál (TV seriál) (2024) | ČSFD.sk"), "a series");
        Assert.True(TmdbTray.FromCsfdPage("https://www.csfd.sk/film/77-vymysleny-serial/88-epizoda/", null, "nie je to imdb") is { CsfdId: 77, Imdb: null, Title: null },
            "an episode is its series; no IMDb ID from a bad one");
        foreach (var bad in new[] { "https://evil.example/film/1", "http://www.csfd.cz/film/1", "https://www.csfd.cz/tvurce/1-vymysleny", "https://www.csfd.cz.evil.example/film/1" })
        {
            Assert.True(TmdbTray.FromCsfdPage(bad, null, null) is null, "not ČSFD: " + bad);
        }

        var csfd = TmdbTray.FromPoster("https://www.csfd.cz/film/12345-vymysleny-film/prehled/", "//image.pmgstatic.com/cache/resized/w420/files/images/film/posters/111/222/111222333_ab12cd.jpg",
            "Vymyslený film (2026) | ČSFD.cz", "tt0000001");
        Assert.True(csfd is { Source: TrayPosterSource.Csfd, Csfd.CsfdId: 12345, Entry: null } &&
                    csfd.Images[0].AbsoluteUri == "https://image.pmgstatic.com/files/images/film/posters/111/222/111222333_ab12cd.jpg" &&
                    csfd.Images.Any(i => i.AbsoluteUri.Contains("/w420/", StringComparison.Ordinal)),
            "a ČSFD poster: the original first, the copy shown too – " + string.Join(", ", csfd?.Images ?? []));
        var tmdb = TmdbTray.FromPoster("https://www.themoviedb.org/movie/111-vymysleny-film/images/posters", "https://media.themoviedb.org/t/p/w220_and_h330_face/AbCdEf123456.jpg", null, null);
        Assert.True(tmdb is { Source: TrayPosterSource.Tmdb, Entry: { Kind: MediaKind.Movie, Id: 111 }, TmdbPath: "/AbCdEf123456.jpg" } &&
                    tmdb.Images.Single().AbsoluteUri == "https://image.tmdb.org/t/p/original/AbCdEf123456.jpg", "a TMDB poster: the original");
        foreach (var (where, picture) in new[]
                 {
                     ("https://www.csfd.cz/film/1-x/", "https://evil.example/files/images/film/posters/1/2/3.jpg"),
                     ("https://www.csfd.cz/film/1-x/", "https://image.pmgstatic.com/cache/resized/w100h132crop/files/images/creator/photos/1/2/3.jpg"),
                     ("https://www.csfd.cz/film/1-x/", "http://image.pmgstatic.com/files/images/film/posters/1/2/3.jpg"),
                     ("https://www.csfd.cz/film/1-x/", "https://image.pmgstatic.com/files/images/film/posters/1/2/3.jpg?x=1"),
                     ("https://www.csfd.cz/film/1-x/", "https://image.pmgstatic.com/files/images/film/posters/../../../x.jpg"),
                     ("https://www.themoviedb.org/movie/1", "https://image.pmgstatic.com/files/images/film/posters/1/2/3.jpg"),
                     ("https://www.themoviedb.org/movie/1", "https://image.tmdb.org.evil.example/t/p/w500/abcdef.jpg"),
                     ("https://evil.example/movie/1", "https://image.tmdb.org/t/p/w500/abcdef.jpg"),
                 })
        {
            Assert.True(TmdbTray.FromPoster(where, picture, null, null) is null, $"refused: {where} {picture}");
        }
    }

    [Test]
    public void PostersKeptPerTitleAndPutAsideWhenEmptied()
    {
        var root = Path.Combine(Path.GetTempPath(), "universum-zasobnik-plagaty-" + Guid.NewGuid().ToString("N"));
        try
        {
            var posters = new TrayPosters(Path.Combine(root, "zasobnik-plagaty"));
            byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9];
            byte[] webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, 7];
            var first = posters.Add(MediaKind.Movie, 111, jpeg, TrayPosterSource.Csfd, new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc));
            Assert.True(first is not null && posters.Add(MediaKind.Movie, 111, jpeg, TrayPosterSource.Csfd) == first, "the same picture kept once");
            posters.Add(MediaKind.Movie, 111, png, TrayPosterSource.Tmdb, new DateTime(2026, 11, 8, 11, 0, 0, DateTimeKind.Utc));
            posters.Add(MediaKind.Series, 111, webp, TrayPosterSource.Csfd);
            Assert.True(posters.Add(MediaKind.Movie, 111, "<html>"u8.ToArray(), TrayPosterSource.Csfd) is null, "no picture, nothing kept");
            var of = posters.Of(MediaKind.Movie, 111);
            Assert.True(of is [{ Source: TrayPosterSource.Tmdb }, { Source: TrayPosterSource.Csfd }] && of[0].Path.EndsWith(".png", StringComparison.Ordinal), "the newest first, per title");
            Assert.Equal(1, posters.Of(MediaKind.Series, 111).Count, "a series apart from a film of the same ID");
            Assert.True(posters.Clear() && posters.Of(MediaKind.Movie, 111).Count == 0, "emptied");
            posters.Add(MediaKind.Movie, 222, jpeg, TrayPosterSource.Csfd);
            posters.Restore();
            Assert.True(posters.Of(MediaKind.Movie, 111).Count == 2 && posters.Of(MediaKind.Movie, 222).Count == 1, "back, and the one picked since stays");
            posters.Remove(posters.Of(MediaKind.Movie, 222)[0]);
            Assert.Equal(0, posters.Of(MediaKind.Movie, 222).Count, "one removed");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task ReceiverTakesCsfdPagesAndPosters()
    {
        var key = TmdbTrayListener.NewKey();
        var pages = new List<(CsfdPage Page, bool ByItself)>();
        var posters = new List<TrayPosterRequest>();
        var automatic = false;
        using var door = TmdbTrayListener.Start(0, key, (TmdbTrayEntry _, bool _) => Task.CompletedTask, () => "", () => automatic,
            (page, byItself) =>
            {
                lock (pages)
                {
                    pages.Add((page, byItself));
                }

                return Task.CompletedTask;
            },
            poster =>
            {
                lock (posters)
                {
                    posters.Add(poster);
                }

                return Task.CompletedTask;
            });

        async Task<string> Post(string path, string body)
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", door.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"POST {path} HTTP/1.1\r\nX-Universum-Key: {key}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return (await reader.ReadToEndAsync())[..12];
        }

        var film = """{"u":"https://www.csfd.cz/film/12345-vymysleny-film/","t":"Vymyslený film (2026) | ČSFD.cz","m":"tt0000001","a":true}""";
        Assert.Equal("HTTP/1.1 202", await Post("/csfd", film), "by itself while not gathering: answered");
        automatic = true;
        Assert.Equal("HTTP/1.1 202", await Post("/csfd", film), "by itself while gathering");
        Assert.Equal("HTTP/1.1 422", await Post("/csfd", """{"u":"https://evil.example/film/1"}"""), "no ČSFD page");
        Assert.Equal("HTTP/1.1 202", await Post("/poster",
            """{"u":"https://www.csfd.cz/film/12345-vymysleny-film/","i":"//image.pmgstatic.com/cache/resized/w420/files/images/film/posters/1/2/12_ab.jpg","a":false}"""), "a poster");
        Assert.Equal("HTTP/1.1 422", await Post("/poster", """{"u":"https://www.csfd.cz/film/12345-vymysleny-film/","i":"https://evil.example/x.jpg"}"""), "not from ČSFD's picture server");
        for (var i = 0; i < 50 && (pages.Count < 1 || posters.Count < 1); i++)
        {
            await Task.Delay(20);
        }

        lock (pages)
        {
            Assert.True(pages is [{ Page.CsfdId: 12345, Page.Imdb: "tt0000001", ByItself: true }], "one page taken (the first came while not gathering)");
        }

        lock (posters)
        {
            Assert.True(posters is [{ Source: TrayPosterSource.Csfd, Csfd.CsfdId: 12345 }], "one poster taken");
        }
    }
}
