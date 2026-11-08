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
        using var door = TmdbTrayListener.Start(0, key, entry =>
        {
            lock (received)
            {
                received.Add(entry);
            }

            return Task.CompletedTask;
        }, () => TmdbUserScript.Text(4711, key));

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

        Assert.Throws<ArgumentException>(() => TmdbTrayListener.Start(0, "kratky", _ => Task.CompletedTask, () => ""), "a short key is no key");
    }

    [Test]
    public void TheScriptRunsOnlyOnTmdbAndTalksOnlyToThisComputer()
    {
        var script = TmdbUserScript.Text(47219, "0123456789abcdef0123456789abcdef");
        var matches = script.Split('\n').Where(l => l.Contains("@match", StringComparison.Ordinal)).Select(l => l.Split("@match")[1].Trim()).ToList();
        Assert.Sequence(["https://www.themoviedb.org/movie/*", "https://www.themoviedb.org/tv/*"], matches, "only TMDB's film and series pages");
        Assert.True(script.Contains("@connect      127.0.0.1", StringComparison.Ordinal) && !script.Contains("@connect      *", StringComparison.Ordinal) &&
                    script.Contains("'http://127.0.0.1:' + PORT", StringComparison.Ordinal), "only 127.0.0.1");
        Assert.True(TmdbUserScript.Bookmarklet.StartsWith("javascript:location.href='universum://tmdb?u='", StringComparison.Ordinal), "the bookmark");
    }
}
