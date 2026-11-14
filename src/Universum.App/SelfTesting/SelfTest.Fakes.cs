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

// the self-test: the stand-ins of the web services (nothing leaves the computer during the self-test)
internal static partial class SelfTest
{
    /// <summary>
    /// The sources of news with invented answers: a schedule page (rules in <see cref="Rules"/>) with a new Czech
    /// series and a new season today, TVmaze with a new season on a streaming service and a new series in the USA,
    /// TMDB with two films in Czech cinemas and two in the world, and drawn posters.
    /// </summary>
    private sealed class FakeNews : System.Net.Http.HttpMessageHandler
    {
        public const string Rules = """
            {
              "format": "UNIVERSUM-zdroj 1",
              "name": "Vymyslený rozpis",
              "address": "https://rozpis.example/den/{yyyy}-{MM}-{dd}",
              "item": "div.zaznam",
              "section": "h3",
              "sections": [ { "contains": "Nové seriály", "kind": "series-premiere" }, { "contains": "Ostatné", "kind": "episode" } ],
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
              "domestic": [ "ct1", "nova" ],
              "daysBack": 1,
              "daysAhead": 2
            }
            """;

        private readonly List<byte[]> _posters = Enumerable.Range(0, 4).Select(FakeHistra.DrawPoster).ToList();

        public List<string> Requests { get; } = [];

        public List<string> UserAgents { get; } = [];

        public static DateOnly Day(int offset) => DateOnly.FromDateTime(DateTime.Now).AddDays(offset);

        private static string Iso(int offset) => Day(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add(uri.AbsoluteUri);
                if (uri.Host == "rozpis.example")
                {
                    UserAgents.Add(request.Headers.UserAgent.ToString());
                }
            }

            if (uri.Host == "image.tmdb.org")
            {
                return Answer(request, new System.Net.Http.ByteArrayContent(_posters[uri.AbsolutePath.Length % _posters.Count]));
            }

            var body = uri.Host switch
            {
                "rozpis.example" when uri.AbsolutePath == "/robots.txt" => "User-agent: *\nDisallow: /tajne/\n",
                "rozpis.example" => Page(uri.AbsolutePath),
                "api.tvmaze.com" => TvMaze(uri),
                "api.themoviedb.org" => Tmdb(uri),
                _ => null,
            };
            return body is null
                ? Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request })
                : Answer(request, new System.Net.Http.StringContent(body, Encoding.UTF8, uri.Host == "rozpis.example" ? "text/html" : "application/json"));
        }

        private static Task<System.Net.Http.HttpResponseMessage> Answer(System.Net.Http.HttpRequestMessage request, System.Net.Http.HttpContent content) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request, Content = content });

        // today: a new Czech series and a new season of a foreign one; every weekday an episode of a Czech series
        private static string Page(string path)
        {
            var today = path.EndsWith(Iso(0), StringComparison.Ordinal);
            var number = 10 + DateOnly.ParseExact(path[^10..], "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber % 50;
            return "<html><head><meta charset=\"utf-8\"><script>var x = '<div class=\"zaznam\">';</script></head><body>" +
                   (today
                       ? "<h3>Nové seriály</h3><div class=\"zaznam\" data-kanal=\"ct1\"><a class=\"meno\" href=\"https://rozpis.example/serial/1\">Vymyslený seriál <span>(2026–????)</span></a>" +
                         "<a class=\"povodny\">Invented Show</a><div class=\"zaner\">Drama / Krimi</div><div class=\"cislo\">01&times;01<br /><span>ČT1</span></div></div>"
                       : "") +
                   "<h3>Ostatné vysielané</h3>" +
                   (today
                       ? "<div class=\"zaznam\" data-kanal=\"netflix\"><a class=\"meno\">Svetový seriál <span>(2024–????)</span></a><a class=\"povodny\">World Invented</a>" +
                         "<div class=\"cislo\">02&times;01<br /><span>Netflix</span></div></div>"
                       : "") +
                   $"<div class=\"zaznam\" data-kanal=\"nova\"><a class=\"meno\">Domáci seriál <span>(2020–????)</span></a><a class=\"povodny\">Domáci seriál</a>" +
                   $"<div class=\"cislo\">05&times;{number}<br /><span>Nova</span></div></div><!-- <div class=\"zaznam\">komentár</div> --></body></html>";
        }

        private static string? TvMaze(Uri uri)
        {
            var today = uri.Query.Contains("date=" + Iso(0), StringComparison.Ordinal);
            if (uri.AbsolutePath == "/schedule/web")
            {
                return today
                    ? $$$$"""[{"season":3,"number":1,"type":"regular","airdate":"{{{{Iso(0)}}}}","airstamp":"{{{{Iso(0)}}}}T10:00:00+00:00","_embedded":{"show":{"id":501,"name":"Invented Stream","type":"Scripted","weight":90,"premiered":"2024-02-01","genres":["Drama"],"webChannel":{"name":"Invented+","country":null},"externals":{"imdb":"tt0000501","thetvdb":7501},"url":"https://www.tvmaze.com/shows/501/invented-stream"}}},""" +
                      """{"season":4,"number":2,"type":"regular","airdate":"2026-01-01","airstamp":"2026-01-01T10:00:00+00:00","_embedded":{"show":{"id":502,"name":"Invented News","type":"News","weight":99}}}]"""
                    : "[]";
            }

            if (uri.AbsolutePath == "/schedule")
            {
                return today
                    ? $$$$"""[{"season":1,"number":1,"type":"regular","airdate":"{{{{Iso(0)}}}}","airstamp":"{{{{Iso(0)}}}}T12:00:00+00:00","show":{"id":601,"name":"Invented Network Show","type":"Scripted","weight":80,"premiered":"{{{{Iso(0)}}}}","network":{"name":"INV","country":{"code":"US"}},"externals":{}}}]"""
                    : "[]";
            }

            if (uri.AbsolutePath == "/singlesearch/shows")
            {
                return uri.Query.Contains("Invented%20Show", StringComparison.Ordinal) ? """{"id":77,"name":"Invented Show","premiered":"2026-09-01","language":"Czech","network":{"name":"ČT1","country":{"code":"CZ"}}}""" : null;
            }

            // a season: weekly for the streaming show, weekdays for the Czech one
            var show = uri.AbsolutePath.Split('/')[2];
            var dates = show switch
            {
                "501" => Enumerable.Range(0, 8).Select(i => (3, Day(7 * i))),
                "77" => Enumerable.Range(0, 14).Select(Day).Where(d => d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday).Take(10).Select(d => (1, d)),
                _ => Enumerable.Range(0, 6).Select(i => (1, Day(i))),
            };
            return "[" + string.Join(",", dates.Select((d, i) => $$$"""{"season":{{{d.Item1}}},"number":{{{i + 1}}},"type":"regular","airdate":"{{{d.Item2:yyyy-MM-dd}}}"}""")) + "]";
        }

        private static string? Tmdb(Uri uri)
        {
            if (uri.AbsolutePath.EndsWith("/watch/providers", StringComparison.Ordinal))
            {
                return uri.AbsolutePath.StartsWith("/3/tv/", StringComparison.Ordinal)
                    ? """{"results":{"SK":{"flatrate":[{"provider_name":"Vymyslené+"}]},"US":{"flatrate":[{"provider_name":"Inde"}]}}}"""
                    : """{"results":{"SK":{"rent":[{"provider_name":"Obchod"}]}}}""";
            }

            if (uri.AbsolutePath == "/3/search/tv")
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var name = query["query"] ?? "";
                var year = query["first_air_date_year"] ?? "2024";
                var id = 700 + name.Length;
                return $$$"""{"results":[{"id":{{{id}}},"name":"{{{name}}}","original_name":"{{{name}}}","first_air_date":"{{{year}}}-01-01","poster_path":"/s{{{id}}}.jpg"}]}""";
            }

            if (uri.AbsolutePath == "/3/discover/movie")
            {
                return uri.Query.Contains("region=", StringComparison.Ordinal)
                    ? """{"page":1,"total_pages":1,"results":[{"id":101,"title":"The Invented Premiere","popularity":50.5},{"id":102,"title":"Today Invented","popularity":20}]}"""
                    : """{"page":1,"total_pages":1,"results":[{"id":201,"title":"World Invented Film","popularity":90},{"id":202,"title":"Later Invented Film","popularity":40}]}""";
            }

            var (title, original, release, cz, sk) = uri.AbsolutePath switch
            {
                "/3/movie/101" => ("Vymyslená premiéra", "The Invented Premiere", 3, 3, 4),
                "/3/movie/102" => ("Dnešný vymyslený", "Today Invented", 0, 0, 0),
                "/3/movie/201" => ("Svetový vymyslený film", "World Invented Film", -2, 40, 40),
                "/3/movie/202" => ("Neskorší vymyslený film", "Later Invented Film", 10, 30, 30),
                _ => ("", "", 0, 0, 0),
            };
            // the films of the cinemas here are Czech and Slovak, the films of the world American and British
            var countries = uri.AbsolutePath.StartsWith("/3/movie/10", StringComparison.Ordinal)
                ? """[{"iso_3166_1":"CZ"},{"iso_3166_1":"SK"}]"""
                : """[{"iso_3166_1":"US"},{"iso_3166_1":"GB"}]""";
            return title.Length == 0 ? null : $$$"""
                {"title":"{{{title}}}","original_title":"{{{original}}}","release_date":"{{{Iso(release)}}}","poster_path":"/f{{{uri.AbsolutePath[^3..]}}}.jpg",
                 "production_countries":{{{countries}}},"production_companies":[{"name":"Invented Pictures"},{"name":"Second"}],
                 "release_dates":{"results":[
                   {"iso_3166_1":"CZ","release_dates":[{"type":3,"release_date":"{{{Iso(cz)}}}T00:00:00.000Z"},{"type":4,"release_date":"{{{Iso(cz + 45)}}}T00:00:00.000Z"}]},
                   {"iso_3166_1":"SK","release_dates":[{"type":3,"release_date":"{{{Iso(sk)}}}T00:00:00.000Z"}]},
                   {"iso_3166_1":"US","release_dates":[{"type":6,"release_date":"{{{Iso(release + 200)}}}T00:00:00.000Z"}]},
                   {"iso_3166_1":"DE","release_dates":[{"type":3,"release_date":"{{{Iso(release)}}}T00:00:00.000Z"}]}]}}
                """;
        }
    }

    /// <summary>
    /// Invented sources of Zaujímavosti: a page that names its RSS feed (three English articles of the last days and one
    /// older, pictures drawn), a Czech Atom feed, and DeepL answering "SK: " + the text.
    /// </summary>
    private sealed class FakeFeeds : System.Net.Http.HttpMessageHandler
    {
        private readonly byte[] _picture = FakeHistra.DrawPoster(2);

        public List<string> Requests { get; } = [];

        public int DeepL { get; private set; }

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add(uri.AbsoluteUri);
            }

            static string Rfc(int hours) => DateTimeOffset.UtcNow.AddHours(-hours).ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);
            string? body = (uri.Host, uri.AbsolutePath) switch
            {
                ("api-free.deepl.com", _) => await TranslateAsync(request),
                (_, "/robots.txt") => null,
                ("filmy.example", "/") => """<html><head><link rel="alternate" type="application/rss+xml" href="https://filmy.example/rss"></head><body>Vymyslené</body></html>""",
                ("filmy.example", "/rss") => $$$"""
                    <rss version="2.0"><channel><title>Invented films</title>
                      <item><title>Invented festival opens</title><link>https://filmy.example/a/1</link><description>&lt;p&gt;An invented festival opened today. Stars came.&lt;/p&gt;</description>
                        <enclosure url="https://filmy.example/img/1.png" type="image/png" /><pubDate>{{{Rfc(1)}}}</pubDate></item>
                      <item><title>A review of an invented film</title><link>https://filmy.example/a/2</link><description>Good, not great.</description>
                        <enclosure url="https://filmy.example/img/2.png" type="image/png" /><pubDate>{{{Rfc(50)}}}</pubDate></item>
                      <item><title>An old invented story</title><link>https://filmy.example/a/3</link><description>From the archive.</description><pubDate>{{{Rfc(240)}}}</pubDate></item>
                    </channel></rss>
                    """,
                ("kino.example", "/atom") => $$$"""
                    <feed xmlns="http://www.w3.org/2005/Atom"><title>Vymyšlené kino</title>
                      <entry><title>Vymyšlená novinka z kina</title><link href="https://kino.example/n/1"/><published>{{{DateTimeOffset.UtcNow.AddHours(-20):yyyy-MM-ddTHH:mm:ssZ}}}</published><summary>Krátky úvod.</summary></entry>
                    </feed>
                    """,
                _ => null,
            };
            if (uri.AbsolutePath.StartsWith("/img/", StringComparison.Ordinal))
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request, Content = new System.Net.Http.ByteArrayContent(_picture) };
            }

            return body is null
                ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request }
                : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new System.Net.Http.StringContent(body, Encoding.UTF8, uri.Host == "api-free.deepl.com" ? "application/json" : "text/xml"),
                };
        }

        private async Task<string> TranslateAsync(System.Net.Http.HttpRequestMessage request)
        {
            DeepL++;
            using var document = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var texts = document.RootElement.GetProperty("text").EnumerateArray().Select(t => "SK: " + t.GetString());
            return System.Text.Json.JsonSerializer.Serialize(new { translations = texts.Select(t => new { text = t }) });
        }
    }

    private sealed class FakePosters : System.Net.Http.HttpMessageHandler
    {
        /// <summary>A small invented WebP (a test pattern, lossy with alpha – testdata/pictures/lossy-alpha.webp), as TMDB may give it.</summary>
        public static readonly byte[] WebP = Convert.FromBase64String(
            "UklGRvIEAABXRUJQVlA4WAoAAAAQAAAAXQAARQAAQUxQSCYAAAAJF6AQQAAUNBERLNREkqQck78z+Uo5+yCi/xMAa8fE+tT1RIVXA1ZQOCCmBAAAkBgAnQEq" +
            "XgBGAD6dQptIpaOioSebbkCwE4lsAM+YAH+Hvvm5VL+rfhj8def6OpXp/2XtE/Mn+S9SXpc+Yv9Uf1x92X/c/sB7rv856gH9P/jvWc/qT7AH7b+lf+4Pwgfu" +
            "56V0u51S/ABlXugH4BxDWqAfgB/APhP/q/9z5aPoD0Mus2hvCBhr1a/2/MI+aspc/+xHiSHmD0Ipyo2is/qygfhdVp1LZc3XyUftKKeWlikOYD8r3ZKDZFpz" +
            "t1u1KBi15mXN8MQw3QgEm8HDgAD+8Q0nuH//71X/3qvRNH//40PQr/xDfxDf2VymZYs12MpsEhO9q5Dzf27POLMQ22/yI7aNjj8sHKf3xzQPMRw7S8Z8L5WH" +
            "b6NYrfOIO9NaCR6tsPNsYmINyxCbA4Sw5XxSbO4rJ1MMYyzpw+5DPXeQeHha7OFGnZUuoeQyMo0aymZPEWge7FmVA22JB6MKOlP7W/D+kxIQ0YoyMW9ur+xS" +
            "vnQ2gtOQAENIaRpMKSoptRd5kvEYQEPeXs7lcm8UrbUoPeqsJK3aliHUt+mNs9u+Jsif4nJXQ2+qAo6gEdJLnQBp+mns7/aiWmHqDMwllmUvI4iWa/5CPdOy" +
            "15xLh9+fenaKP78Nxf2Tasv5n72ODq+2KqQmZT358inWzNBx8Rd6HxmDXSzr4GxkaR5dwKjbabWjYV6vGVs5owEDuat4R6mVMnGD3IWVbPlPfo3knUMY+GVF" +
            "dNBtS1lOm5lY8c/ptviP5weKIVchwNtD2T3/k7sVKytEdMr7fGWrqJet3cI8Rd6HxjoFw2L7TrjO7XRBpf3MLg5A9DO69hlRU9BTb94iFh1SnpE+8MUNA0it" +
            "uqQgo3NuiUO5U9iA/zp0aKHrBw/PzjNa0owT7fFQ+PlfNQhGY4whfuuDsJeSmC6yL05StroH3oJZldZF6cpWpDVqV0nCLdDtx7Z1QeyTBqi56cMyJ+paTJIb" +
            "RNyvERKtXlVf/LGZwsOMsTTswDiMyQggsRn2KlFxrOz5lrAsyc/VHYC0clDcoAysSKu5rOMAwPKSKJWOIvOHmvJncDhodkj/rp+qjMgvGmvmaIDAWGTE7UCA" +
            "sCQkImACvlS80AC4ecIPlOBOzaDW2FgWaj90ZUVys/Qo5dXLCJqTYM1sYAL2WJycv3Mftn2d1ONur0G5X5EKeWsJ9irmnlG1vpny/ZmRZlt3hXqffanTqe4/" +
            "ej3XHCp2OFbvlPRARDYKauxfwYa3p52CHo/k4Ml8zU9bpNWlxaARPau/XY/eYDnPYaCYvAV127tm0cX70frSIA/98dIFfmVd6CJBmoALb98MbBjR6xXp914Q" +
            "8wLSEMlIEEhb715tMYz+SIDIx1yiRMk4ksEPy5w/jjcuSX+MHe+NI4Qk60NLQzm4NrBHM28PBc5Xi/ewcgvoRAft6FGcP2yLWxVpeOYAtDi3gBSBRaAD71pd" +
            "QvPNesa3sz2vE4tSruVN+KXBuh05NqEz5A0uewHwH2K+ityPgigvN4r161KIQ9mw6iITePmEQm8ecT+qXSfYbF+XzTOdPAmfLk7udfAAR/q5mofvEAMzUxri" +
            "jEAA8Aw9vqKhTfWoAAA=");

        private readonly byte[] _poster = FakeHistra.DrawPoster(4);

        public List<string> Requests { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add(uri.AbsoluteUri);
            }

            if (uri.Host == "image.tmdb.org")
            {
                // the English poster comes as WebP under a name ending .png (owner, 7. 11. 2026)
                var picture = uri.AbsolutePath.EndsWith("/en-plagat.png", StringComparison.Ordinal) ? WebP : _poster;
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(picture), RequestMessage = request });
            }

            var body = uri.Host switch
            {
                "api.cloudflare.com" => $$"""{ "result": { "image": "{{Convert.ToBase64String(_poster)}}" }, "success": true }""",
                _ => uri.AbsolutePath switch
                {
                    "/3/search/multi" => """{ "results": [ { "media_type": "movie", "id": 777001, "title": "Vymyslený plagát", "original_title": "Vymyslený plagát", "release_date": "2026-03-01", "popularity": 3 } ] }""",
                    "/3/movie/777001" => """
                        { "id": 777001, "title": "Invented Poster", "original_title": "Vymyslený plagát", "original_language": "sk", "release_date": "2026-03-01", "runtime": 100,
                          "vote_average": 7.1, "vote_count": 42, "genres": [ { "id": 35 } ], "overview": "An invented film.", "poster_path": "/cz-plagat.png",
                          "belongs_to_collection": { "id": 777, "name": "Vymyslené plagáty" },
                          "credits": { "cast": [ { "name": "Vymyslený Herec" } ], "crew": [ { "job": "Director", "name": "Vymyslená Režisérka" } ] },
                          "external_ids": { "imdb_id": "tt7770010" },
                          "translations": { "translations": [ { "iso_639_1": "cs", "iso_3166_1": "CZ", "data": { "title": "Vymyšlený plakát", "overview": "Vymyšlený film." } } ] } }
                        """,
                    "/3/movie/777001/images" => """
                        { "posters": [ { "file_path": "/en-plagat.png", "iso_639_1": "en", "width": 2000, "height": 3000, "vote_average": 6, "vote_count": 3 },
                                       { "file_path": "/cz-plagat.png", "iso_639_1": "cs", "width": 1000, "height": 1500, "vote_average": 5.3, "vote_count": 1 } ],
                          "backdrops": [ { "file_path": "/pozadie.png", "iso_639_1": null, "width": 1920, "height": 1080, "vote_average": 5, "vote_count": 1 },
                                         { "file_path": "/pozadie-en.png", "iso_639_1": "en", "width": 1920, "height": 1080, "vote_average": 6, "vote_count": 2 } ],
                          "logos": [ { "file_path": "/logo-cz.png", "iso_639_1": "cs", "width": 400, "height": 150 } ] }
                        """,
                    "/3/collection/777" => """
                        { "name": "Vymyslené plagáty", "parts": [ { "id": 777001, "title": "Invented Poster", "original_title": "Vymyslený plagát", "release_date": "2026-03-01" },
                                                                 { "id": 777002, "title": "Invented Poster 2", "original_title": "Vymyslený plagát 2", "release_date": "2028-03-01" } ] }
                        """,
                    _ => null,
                },
            };
            return Task.FromResult(body is null
                ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FakeHistra : System.Net.Http.HttpMessageHandler
    {
        private readonly List<byte[]> _posters = Enumerable.Range(0, 6).Select(DrawPoster).ToList();

        public List<(string Method, string Address)> Requests { get; } = [];

        public HashSet<int> Deleted { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add((request.Method.Method, uri.AbsoluteUri));
            }

            if (uri.Host == "image.tmdb.org")
            {
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new System.Net.Http.ByteArrayContent(_posters[(uri.AbsolutePath.Length + uri.AbsolutePath[^5]) % _posters.Count]),
                });
            }

            var offset = System.Text.RegularExpressions.Regex.Match(uri.Query, "offset=([0-9]+)") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            string? body = null;
            if (uri.AbsolutePath == "/api/v1/lists")
            {
                body = offset > 0 ? """{"lists":[]}""" : """
                    {"lists":[
                      {"id":1,"name":"Vymyslené príbehy","description":"","visibility":"public","item_count":30,"updated_at":"2026-10-20T18:00:00.000000+00:00","owner_name":"","is_mine":true,"preview_posters":["/p1.jpg","/p2.jpg","/p3.jpg","/p4.jpg","/p5.jpg"]},
                      {"id":2,"name":"Na víkend","description":"Pár vymyslených titulov","visibility":"private","item_count":3,"updated_at":"2026-10-25T09:30:00.000000+00:00","owner_name":"","is_mine":true,"preview_posters":["/p6.jpg","/p7.jpg"]},
                      {"id":3,"name":"Cudzí list","item_count":12,"is_mine":false,"owner_name":"niekto","preview_posters":[]}
                    ]}
                    """;
            }
            else if (uri.AbsolutePath is "/api/v1/lists/1" or "/api/v1/lists/2")
            {
                var id = uri.AbsolutePath[^1] - '0';
                var total = id == 1 ? 30 : 3;
                if (!Deleted.Contains(id))
                {
                    var items = Enumerable.Range(offset + 1, Math.Max(0, Math.Min(20, total - offset)))
                        .Select(i => id == 1 ? Item(i, i % 7 == 0) : Item(100 + i, i == 3));
                    body = $$"""{"list":{"id":{{id}},"name":"{{(id == 1 ? "Vymyslené príbehy" : "Na víkend")}}","item_count":{{total}},"is_mine":true,"visibility":"public"},"items":[{{string.Join(",", items)}}]}""";
                }
            }

            return Task.FromResult(body is null
                ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request }
                : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"),
                });
        }

        private static string Item(int i, bool series)
        {
            var kind = series ? "show" : "movie";
            var word = series ? "seriál" : "príbeh";
            var original = series ? "Series" : "Story";
            var tvdb = series ? ",\"tvdb\":\"" + (7000 + i).ToString(CultureInfo.InvariantCulture) + "\"" : "";
            var status = series ? ",\"status\":\"Returning Series\"" : "";
            var rating = (5 + i % 5).ToString(CultureInfo.InvariantCulture) + ".4";
            var imdb = "tt" + (9000 + i).ToString("0000000", CultureInfo.InvariantCulture);
            return $$"""{"media_type":"{{kind}}","media_id":{{1000 + i}},"title":"Vymyslený {{word}} {{i}}","original_title":"Invented {{original}} {{i}}","year":{{1990 + i}},"overview":"Vymyslený dej číslo {{i}} – len na skúšku programu.","rating":{{rating}},"runtime":{{90 + i}},"poster_path":"/p{{i}}.jpg","external_ids":{"imdb":"{{imdb}}","tmdb":"{{9000 + i}}","trakt":"{{5000 + i}}"{{tvdb}}}{{status}},"genres":["Dráma","Sci-Fi"]}""";
        }

        // a coloured poster with a gradient (drawn on the window's thread, before any request comes)
        internal static byte[] DrawPoster(int seed)
        {
            var colors = new[] { (0x6C, 0x5C, 0xE7), (0x00, 0xB8, 0x94), (0xE1, 0x70, 0x55), (0x09, 0x84, 0xE3), (0xE8, 0x43, 0x93), (0xFD, 0xCB, 0x6E) };
            var (r, g, b) = colors[seed % colors.Length];
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new LinearGradientBrush(Color.FromRgb((byte)r, (byte)g, (byte)b), Color.FromRgb(0x1E, 0x1E, 0x2E), 90), null, new Rect(0, 0, 154, 231));
                context.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), null, new Point(77, 80), 34, 34);
            }

            var bitmap = new RenderTargetBitmap(154, 231, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }

    /// <summary>Open-Meteo with invented answers: one town, a cold day with light snow.</summary>
    private sealed class FakeWeather : System.Net.Http.HttpMessageHandler
    {
        public bool Found { get; set; } = true;

        public bool Fails { get; set; }

        public List<string> Requests { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (Fails)
            {
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            }

            var today = DateTime.Now.Date;
            var days = string.Join(",", Enumerable.Range(0, 3).Select(i => $"\"{today.AddDays(i):yyyy-MM-dd}\""));
            var body = request.RequestUri.Host.StartsWith("geocoding", StringComparison.Ordinal)
                ? !Found ? "{}"
                  : request.RequestUri.Query.Contains("Lehota", StringComparison.Ordinal)
                      ? """{"results":[{"name":"Vymyslená Lehota","latitude":48.2,"longitude":17.5,"elevation":140,"country":"Slovensko","admin1":"Trnavský kraj","admin2":"okres Prvý"},{"name":"Vymyslená Lehota","latitude":48.9,"longitude":19.6,"elevation":612,"country":"Slovensko","admin1":"Banskobystrický kraj","admin2":"okres Druhý"}]}"""
                      : """{"results":[{"name":"Vymyslené Mesto","latitude":48.7,"longitude":19.15,"country":"Slovensko"}]}"""
                : "{\"current\":{\"temperature_2m\":-2.6,\"apparent_temperature\":-6.4,\"weather_code\":71,\"wind_speed_10m\":11.8,\"is_day\":1}," +
                  "\"daily\":{\"time\":[" + days + "],\"weather_code\":[71,3,1],\"temperature_2m_max\":[-0.4,2.4,4.9],\"temperature_2m_min\":[-5.2,-3.1,-1.0]," +
                  "\"precipitation_probability_max\":[70,40,10]}}";
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeTitleServices : System.Net.Http.HttpMessageHandler
    {
        public string Tmdb { get; set; } = "";

        public string Histra { get; set; } = "";

        public System.Net.HttpStatusCode HistraStatus { get; set; } = System.Net.HttpStatusCode.OK;

        public List<string> Requests { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var histra = request.RequestUri.Host == "histra.net";
            var results = histra ? Histra : Tmdb;
            // a title read by its ID (movie/9700) is the title itself, a search a list of results
            var details = !histra && System.Text.RegularExpressions.Regex.IsMatch(request.RequestUri.AbsolutePath, @"/3/(movie|tv)/\d+$");
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(histra ? HistraStatus : System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new System.Net.Http.StringContent(details ? results : "{\"results\":[" + results + "]}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
