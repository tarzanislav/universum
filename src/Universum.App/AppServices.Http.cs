using System.IO;
using System.Net.Http;
using System.Text.Json;
using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Library.Histra;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Processes;
using Universum.Core.Speech;
using Universum.Core.Subtitles;
using Universum.Core.Weather;
using Universum.Platform.Windows;
using Universum.App.Localization;

namespace Universum.App;

// AppServices: the HTTP clients – all in this one part, so that they are made in this order (Shared first)
public sealed partial class AppServices
{
    /// <summary>
    /// One pool of connections for the services of the program – TMDB, HISTRA, OMDb, DeepL, the weather, the posters, the
    /// subtitles, the drafts (14. 11. 2026, step 1 of the review of the code): no cookies (no site can follow the user),
    /// compressed answers (the JSON of TMDB is several times smaller), connections renewed every 10 minutes (a server
    /// that moved is found again); each client keeps its own time limit.
    /// </summary>
    private static readonly SocketsHttpHandler Shared = new()
    {
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxAutomaticRedirections = 10,
    };

    private static readonly HttpClient Http = new(Shared, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The web client of UNIVERSUM (the posters of the Zásobník TMDB are downloaded with it).</summary>
    public static HttpClient WebClient => Http;

    /// <summary>
    /// The HTTP client of the news: without cookies (no site can follow the user), at most three redirects (each checked
    /// to stay on the site), compressed answers allowed.
    /// </summary>
    private static readonly HttpClient NewsHttp = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    // drawing takes up to a minute or two
    private static readonly HttpClient DraftHttp = new(Shared, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(3) };

    // owner, 30. 10. 2026: subtitles from OpenSubtitles.com and translations by svsub.net with the user's own accounts,
    // kept encrypted for this Windows account (like the DeepL key)
    private static readonly HttpClient SubtitleHttp = new(Shared, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
}
