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

// AppServices: Premiéry, Novinky, the marks, the posters found and drawn, OMDb, Zaujímavosti, DeepL, the weather
public sealed partial class AppServices
{
    private Universum.Core.News.SeriesNews? _seriesNews;

    /// <summary>The schedule of series episodes from the sources of the settings (web pages by their rules, TVmaze).</summary>
    public Universum.Core.News.SeriesNews SeriesNews => SeriesNewsForSelfTest ?? (_seriesNews ??= new Universum.Core.News.SeriesNews(
        new Universum.Core.News.WebPageClient(NewsHttp), new Universum.Core.News.TvMazeClient(NewsHttp), NewsCache,
        new Universum.Core.News.SeriesOrigins(Path.Combine(AppSettings.NewsCacheDirectory, "origins.json")), () => SeriesTmdb));

    /// <summary>The self-test's stand-in sources of series (invented pages and answers, nothing goes to the internet).</summary>
    public Universum.Core.News.SeriesNews? SeriesNewsForSelfTest { get; set; }

    /// <summary>The kept days of the sources of news.</summary>
    public Universum.Core.News.NewsCache NewsCache => _newsCache ??= new Universum.Core.News.NewsCache(AppSettings.NewsCacheDirectory);

    private Universum.Core.News.NewsCache? _newsCache;

    /// <summary>Film premieres from TMDB (with the user's key); null without TMDB.</summary>
    public Universum.Core.News.FilmPremieres? Films => FilmsForSelfTest ?? (Tmdb is { } tmdb ? new Universum.Core.News.FilmPremieres(tmdb) : null);

    /// <summary>The self-test's stand-in film premieres.</summary>
    public Universum.Core.News.FilmPremieres? FilmsForSelfTest { get; set; }

    /// <summary>The series of the news found on TMDB (ID and poster); null without TMDB.</summary>
    public Universum.Core.News.SeriesTmdb? SeriesTmdb => SeriesTmdbForSelfTest ?? (Tmdb is { } tmdb ? new Universum.Core.News.SeriesTmdb(tmdb) : null);

    /// <summary>The self-test's stand-in series search.</summary>
    public Universum.Core.News.SeriesTmdb? SeriesTmdbForSelfTest { get; set; }

    /// <summary>The series of the news looked up on TMDB in this run (each once).</summary>
    public Pages.SeriesLookup SeriesLookup => _seriesLookup ??= new Pages.SeriesLookup(this);

    private Pages.SeriesLookup? _seriesLookup;

    /// <summary>The premieres kept between starts (the history too).</summary>
    public Universum.Core.News.PremiereStore PremiereStore => _premiereStore ??= new Universum.Core.News.PremiereStore(AppSettings.PremieresFile);

    private Universum.Core.News.PremiereStore? _premiereStore;

    /// <summary>The user's marks of titles (chosen, notes) in Premiéry and Novinky.</summary>
    public Universum.Core.News.MarkStore Marks => _marks ??= new Universum.Core.News.MarkStore(AppSettings.MarksFile);

    private Universum.Core.News.MarkStore? _marks;

    /// <summary>Forgets the kept premieres and days of news (the history too); the marks stay.</summary>
    public void ClearNews()
    {
        NewsCache.Clear();
        PremiereStore.Clear();
        NewsCleared?.Invoke();
    }

    /// <summary>Raised when the kept premieres and news were forgotten.</summary>
    public event Action? NewsCleared;

    /// <summary>Legal availability from TMDB (JustWatch data); null without TMDB.</summary>
    public Universum.Core.News.WatchProviders? Watch => WatchForSelfTest ?? (Tmdb is { } tmdb ? new Universum.Core.News.WatchProviders(tmdb) : null);

    /// <summary>The self-test's stand-in availability.</summary>
    public Universum.Core.News.WatchProviders? WatchForSelfTest { get; set; }

    /// <summary>Where the chosen titles can be watched legally (checked once a day).</summary>
    public Pages.Watchlist Watchlist => _watchlist ??= new Pages.Watchlist(this);

    // TMDB poster (owner, 31. 10. 2026): finding titles, their detail and posters, drafts drawn by AI, other ratings

    /// <summary>TMDB for the title card and TMDB poster (the self-test's stand-in answers there); null without TMDB.</summary>
    public TmdbClient? PosterTmdb => PosterTmdbForSelfTest ?? Tmdb;

    public TmdbClient? PosterTmdbForSelfTest { get; set; }

    /// <summary>Finds films and series as the user writes them; null without TMDB.</summary>
    public Universum.Core.Posters.TitleFinder? Finder => PosterTmdb is { } tmdb ? new Universum.Core.Posters.TitleFinder(tmdb) : null;

    /// <summary>The detail, posters and collection of a title; null without TMDB.</summary>
    public TmdbTitleDetails? TitleDetails => PosterTmdb is { } tmdb ? new TmdbTitleDetails(tmdb) : null;

    private Universum.Core.Posters.PendingPosters? _waitingPosters;

    /// <summary>Posters waiting for their titles to be processed (Zlúčiť poster s filmom/seriálom).</summary>
    public Universum.Core.Posters.PendingPosters WaitingPosters => _waitingPosters ??= new Universum.Core.Posters.PendingPosters(AppSettings.WaitingPosterDirectory, () => Settings.OutputDirectory, () => Settings.OtherOutputDirectories);

    /// <summary>Posters drawn by AI (OpenAI with the user's key, else Cloudflare with the user's account).</summary>
    public Universum.Core.Posters.PosterDrafts Drafts => DraftsForSelfTest ?? new Universum.Core.Posters.PosterDrafts(DraftHttp);

    public Universum.Core.Posters.PosterDrafts? DraftsForSelfTest { get; set; }

    /// <summary>The keys of the services that draw posters (the token and the key protected for this Windows account).</summary>
    public Universum.Core.Posters.DraftKeys DraftKeys => DraftKeysForSelfTest ?? new(Settings.CloudflareAccount,
        Settings.CloudflareTokenProtected is { Length: > 0 } token ? UserSecret.Unprotect(token) : null,
        Settings.OpenAiKeyProtected is { Length: > 0 } key ? UserSecret.Unprotect(key) : null);

    public Universum.Core.Posters.DraftKeys? DraftKeysForSelfTest { get; set; }

    /// <summary>Stores the Cloudflare account and token (null removes them).</summary>
    public void SetCloudflare(string? account, string? token)
    {
        Settings.CloudflareAccount = string.IsNullOrWhiteSpace(account) ? null : account.Trim();
        Settings.CloudflareTokenProtected = string.IsNullOrWhiteSpace(token) ? null : UserSecret.Protect(token.Trim());
        Settings.Save();
    }

    /// <summary>Stores the OpenAI key (null removes it).</summary>
    public void SetOpenAiKey(string? key)
    {
        Settings.OpenAiKeyProtected = string.IsNullOrWhiteSpace(key) ? null : UserSecret.Protect(key.Trim());
        Settings.Save();
    }

    /// <summary>The user's OMDb key; null without one.</summary>
    public string? OmdbKey => Settings.OmdbKeyProtected is { Length: > 0 } key ? UserSecret.Unprotect(key) : null;

    private OmdbClient? _omdb;

    /// <summary>The ratings of IMDb, Rotten Tomatoes and Metacritic (OMDb with the user's key); null without one.</summary>
    public OmdbClient? Omdb => OmdbForSelfTest ?? (OmdbKey is { } key && OmdbClient.IsValidKey(key) ? _omdb ??= new OmdbClient(Http, key, AppSettings.OmdbCacheFile) : null);

    public OmdbClient? OmdbForSelfTest { get; set; }

    /// <summary>Stores the OMDb key (null removes it).</summary>
    public void SetOmdbKey(string? key)
    {
        Settings.OmdbKeyProtected = string.IsNullOrWhiteSpace(key) ? null : UserSecret.Protect(key.Trim());
        Settings.Save();
        _omdb = null;
    }

    private Pages.Watchlist? _watchlist;

    /// <summary>The chosen titles found in the input folder.</summary>
    public Pages.InputWatch InputWatch => _inputWatch ??= new Pages.InputWatch(this);

    private Pages.InputWatch? _inputWatch;

    /// <summary>The sources of Zaujímavosti (added by the user; none in a new installation).</summary>
    public IReadOnlyList<Universum.Core.News.FeedSource> FeedSources => Settings.FeedSources ?? [];

    /// <summary>Stores the sources of Zaujímavosti.</summary>
    public void SetFeedSources(IEnumerable<Universum.Core.News.FeedSource> sources)
    {
        Settings.FeedSources = sources.ToList();
        Settings.Save();
        FeedSourcesChanged?.Invoke();
    }

    /// <summary>Raised when a source of Zaujímavosti was added, removed, switched on or off.</summary>
    public event Action? FeedSourcesChanged;

    /// <summary>The user's DeepL key (protected for this Windows account); null without one.</summary>
    public string? DeepLKey => Settings.DeepLKeyProtected is { Length: > 0 } stored ? UserSecret.Unprotect(stored) : null;

    /// <summary>Stores the DeepL key protected for the current Windows user (null removes it).</summary>
    public void SetDeepLKey(string? key)
    {
        Settings.DeepLKeyProtected = string.IsNullOrWhiteSpace(key) ? null : UserSecret.Protect(key.Trim());
        Settings.Save();
        TranslationChanged?.Invoke();
    }

    /// <summary>The language Zaujímavosti are translated into ("sk"; "cs" and "en" ready).</summary>
    public string InterestLanguage => Settings.InterestLanguage is "cs" or "en" ? Settings.InterestLanguage : "sk";

    public void SetInterestLanguage(string language)
    {
        Settings.InterestLanguage = language;
        Settings.Save();
        TranslationChanged?.Invoke();
    }

    /// <summary>Raised when the DeepL key or the language changes.</summary>
    public event Action? TranslationChanged;

    /// <summary>DeepL with the user's key; null without one.</summary>
    public Universum.Core.News.DeepLClient? Translator =>
        TranslatorForSelfTest ?? (DeepLKey is { } key && Universum.Core.News.DeepLClient.IsValidKey(key) ? new Universum.Core.News.DeepLClient(Http, key) : null);

    /// <summary>The self-test's stand-in DeepL.</summary>
    public Universum.Core.News.DeepLClient? TranslatorForSelfTest { get; set; }

    /// <summary>Zaujímavosti: the articles of the sources, kept for good.</summary>
    public Universum.Core.News.Interests Interests => InterestsForSelfTest ?? (_interests ??= new Universum.Core.News.Interests(
        new Universum.Core.News.WebPageClient(NewsHttp), new Universum.Core.News.InterestStore(AppSettings.InterestsFile)));

    private Universum.Core.News.Interests? _interests;

    /// <summary>The self-test's stand-in sources of Zaujímavosti.</summary>
    public Universum.Core.News.Interests? InterestsForSelfTest { get; set; }

    /// <summary>The pictures of articles; null when they are switched off.</summary>
    public Universum.Core.News.WebImageCache? Images =>
        ImagesForSelfTest ?? (!Settings.InterestImages ? null : _images ??= new Universum.Core.News.WebImageCache(NewsHttp, AppSettings.ImageDirectory));

    private Universum.Core.News.WebImageCache? _images;

    /// <summary>The self-test's stand-in picture cache.</summary>
    public Universum.Core.News.WebImageCache? ImagesForSelfTest { get; set; }

    /// <summary>The sources of news of the settings (TVmaze and TMDB in a new installation).</summary>
    public IReadOnlyList<Universum.Core.News.NewsSource> NewsSources => Settings.NewsSources ?? Universum.Core.News.NewsSource.Defaults;

    /// <summary>Stores the sources of news.</summary>
    public void SetNewsSources(IEnumerable<Universum.Core.News.NewsSource> sources)
    {
        Settings.NewsSources = sources.ToList();
        Settings.Save();
        NewsSourcesChanged?.Invoke();
    }

    /// <summary>Raised when a source of news was added, removed, switched on or off.</summary>
    public event Action? NewsSourcesChanged;

    private OpenMeteo? _weather;

    /// <summary>Weather of the Dashboard (Open-Meteo, no key; only the town and then its coordinates are sent).</summary>
    public OpenMeteo Weather => WeatherForSelfTest ?? (_weather ??= new OpenMeteo(Http));

    /// <summary>The self-test's stand-in weather service (invented answers, nothing goes to the internet).</summary>
    public OpenMeteo? WeatherForSelfTest { get; set; }

    /// <summary>Stores the town of the weather (null removes it: no weather is asked for).</summary>
    public void SetWeatherPlace(WeatherPlace? place)
    {
        Settings.WeatherPlace = place;
        Settings.Save();
        WeatherChanged?.Invoke();
    }

    /// <summary>Raised when the town of the weather changes.</summary>
    public event Action? WeatherChanged;
}
