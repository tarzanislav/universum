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

namespace Universum.App;

/// <summary>Shared services of the running application.</summary>
public sealed class AppServices
{
    public IProcessRunner Runner { get; } = new ProcessRunner();

    public AppSettings Settings { get; } = AppSettings.Load();

    private CachingInspector? _inspector;

    /// <summary>Inspector with the persistent identification cache for the current MKVToolNix.</summary>
    public CachingInspector Inspector(MkvToolNixInstallation installation)
    {
        var inner = new MkvMergeInspector(installation, Runner);
        if (_inspector is null || _inspector.Name != inner.Name)
        {
            _inspector = new CachingInspector(inner, AppSettings.IdentificationCacheFile);
        }

        return _inspector;
    }

    /// <summary>
    /// The input folder (owner, 28. 10. 2026): where new films and episodes come; Novinky look for chosen episodes there
    /// (with its subfolders) and the file dialogs of Single and batch processing start in it. Files there are only read.
    /// </summary>
    public string? InputDirectory => Settings.InputDirectory;

    /// <summary>Sets the input folder (null clears it); <see cref="ArgumentException"/> when it lies in the library's MOVIES or TVSHOWS.</summary>
    public void SetInputDirectory(string? folder)
    {
        if (folder is not null)
        {
            folder = Path.GetFullPath(folder);
            if (Library?.ResultFolders.Any(r => Universum.Core.IO.Paths.Same(r, folder) ||
                    folder.StartsWith(Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) == true)
            {
                throw new ArgumentException("Vstupný priečinok nemôže byť v MOVIES ani v TVSHOWS knižnice – tam sú výsledky.");
            }
        }

        Settings.InputDirectory = folder;
        Settings.Save();
        EnsureSortingFolders();
        InputDirectoryChanged?.Invoke();
        RestartWatch();
    }

    private Universum.Core.IO.FolderWatch? _watch;

    /// <summary>
    /// Films and episodes that came into the input folder while UNIVERSUM runs (owner, 31. 10. 2026: tell and offer – they
    /// go to Hromadné spracovanie only on the user's click). Changed on the window's thread.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<string> Arrivals { get; } = [];

    /// <summary>How long a new file must stay the same before it is told (10 s; the self-test shortens it).</summary>
    public TimeSpan WatchSettle { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Watches the input folder (again) as the settings say; a folder that cannot be watched is simply not.</summary>
    public void RestartWatch()
    {
        _watch?.Dispose();
        _watch = null;
        if (!Settings.WatchInput || InputDirectory is not { } folder || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _watch = new Universum.Core.IO.FolderWatch(folder, WatchSettle, path => Library?.HoldsResult(path) == true);
            _watch.Arrived += files => dispatcher.BeginInvoke(() =>
            {
                foreach (var file in files.Where(f => !Arrivals.Contains(f, Universum.Core.IO.Paths.Comparer)))
                {
                    Arrivals.Add(file);
                }
            });
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _watch = null;
        }
    }

    /// <summary>
    /// Owner, 29. 10. 2026: the input folder holds MOVIES and TVSHOWS – the user sorts films and series into them, so
    /// Hromadné spracovanie tells them apart for certain (nothing is moved by the program). Created when missing.
    /// </summary>
    public void EnsureSortingFolders()
    {
        if (InputDirectory is not { } folder || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var name in new[] { Universum.Core.Library.TitleUnits.MoviesFolder, Universum.Core.Library.TitleUnits.ShowsFolder })
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a read-only folder: the user can create them
            }
        }
    }

    /// <summary>Raised when the input folder changes (all pages show the same one).</summary>
    public event Action? InputDirectoryChanged;

    private Pages.InputFolderViewModel? _inputFolder;

    /// <summary>The input folder as the pages show it (one for all of them).</summary>
    public Pages.InputFolderViewModel InputFolder => _inputFolder ??= new Pages.InputFolderViewModel(this);

    /// <summary>The library in the output folder; null until the user chooses the folder.</summary>
    public LibraryLayout? Library => Settings.OutputDirectory is { Length: > 0 } folder ? new LibraryLayout(folder) : null;

    /// <summary>Raised when the output folder changes, so the pages can update.</summary>
    public event Action? LibraryChanged;

    /// <summary>Sets the output folder and creates MOVIES and TVSHOWS in it (they are never deleted by UNIVERSUM).</summary>
    public void SetOutputDirectory(string folder)
    {
        var layout = new LibraryLayout(Path.GetFullPath(folder));
        layout.EnsureCreated();
        Settings.OutputDirectory = layout.Root;
        Settings.Save();
        LibraryChanged?.Invoke();
    }

    private LibraryIndex? _libraryIndex;

    /// <summary>Which original each library file was made from (recognises the same stream processed again).</summary>
    public LibraryIndex LibraryIndex => _libraryIndex ??= new LibraryIndex(AppSettings.LibraryIndexFile);

    /// <summary>Writes into the library with the Windows Recycle Bin and the undo journal.</summary>
    /// <param name="deleteOriginals">
    /// The originals may go to the Recycle Bin – only Hromadné spracovanie and its MEGA batches, when the user chose it
    /// (owner, 30. 10. 2026); every other tool keeps them.
    /// </param>
    public FileProcessor Processor(MkvToolNixInstallation installation, bool deleteOriginals = false) =>
        new(installation, Runner, new WindowsRecycleBin(), AppSettings.JournalDirectory, LibraryIndex)
        {
            DeleteOriginalsAllowed = deleteOriginals,
            WaitingPosters = WaitingPosters,
            // a sound written anew (Úprava audiostopy, also in Hromadné spracovanie and MEGA): FFmpeg of the installation
            FindFfmpeg = async cancellationToken => (await SpeechAsync(cancellationToken)).Ffmpeg,
        };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private TmdbClient? _tmdb;
    private TitleSearch? _titles;

    /// <summary>The user's TMDB key (UNIVERSUM_TMDB_KEY wins over the settings); null when none is set.</summary>
    public string? TmdbKey =>
        Environment.GetEnvironmentVariable("UNIVERSUM_TMDB_KEY") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : Settings.TmdbKeyProtected is { Length: > 0 } stored ? UserSecret.Unprotect(stored) : null;

    /// <summary>TMDB client with the user's key; null without a key or with connecting switched off (the key stays).</summary>
    public TmdbClient? Tmdb => _tmdb ??= Settings.TmdbEnabled && TmdbKey is { } key ? new TmdbClient(Http, key, AppSettings.TmdbCacheFile) : null;

    /// <summary>
    /// Title lookup: TMDB with the key; the HISTRA catalog without it, or when TMDB finds nothing (from HISTRA only
    /// searching is used); null when neither is set up and switched on.
    /// </summary>
    public TitleSearch? Titles =>
        TitlesForSelfTest ?? (Tmdb is null && Histra is null ? null : _titles ??= new TitleSearch(Tmdb, Histra is { } histra ? new HistraCatalog(histra) : null));

    /// <summary>The self-test's stand-in TMDB and HISTRA (invented answers, nothing goes to the internet).</summary>
    public TitleSearch? TitlesForSelfTest { get; set; }

    /// <summary>Raised when TMDB or HISTRA was set up, switched on or off (title lookup may change).</summary>
    public event Action? TitlesChanged;

    /// <summary>Switches connecting to TMDB on or off; the stored key is kept either way.</summary>
    public void SetTmdbEnabled(bool enabled)
    {
        Settings.TmdbEnabled = enabled;
        Settings.Save();
        _tmdb = null;
        _titles = null;
        TmdbChanged?.Invoke();
        TitlesChanged?.Invoke();
    }

    /// <summary>Stores the TMDB key protected for the current Windows user (null removes it).</summary>
    public void SetTmdbKey(string? key)
    {
        Settings.TmdbKeyProtected = string.IsNullOrWhiteSpace(key) ? null : UserSecret.Protect(key.Trim());
        Settings.Save();
        _tmdb = null;
        _titles = null;
        TmdbChanged?.Invoke();
        TitlesChanged?.Invoke();
    }

    /// <summary>Raised when the TMDB key changes.</summary>
    public event Action? TmdbChanged;

    /// <summary>The user's HISTRA token (UNIVERSUM_HISTRA_TOKEN wins over the settings); null when none is set.</summary>
    public string? HistraToken =>
        Environment.GetEnvironmentVariable("UNIVERSUM_HISTRA_TOKEN") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : Settings.HistraTokenProtected is { Length: > 0 } stored ? UserSecret.Unprotect(stored) : null;

    /// <summary>HISTRA is used: a token is stored and connecting is switched on.</summary>
    public bool HistraActive => Settings.HistraEnabled && HistraToken is not null;

    private HistraClient? _histra;

    /// <summary>HISTRA client with the user's token; null without a usable token or with connecting switched off.</summary>
    public HistraClient? Histra =>
        HistraActive && HistraToken is { } token && HistraApi.IsValidToken(token) ? (_histra ??= new HistraClient(Http, token)) : null;

    /// <summary>TMDB and HISTRA both switched on: titles are looked up on TMDB, what it does not find in the HISTRA catalog.</summary>
    public bool SyncActive => Tmdb is not null && HistraActive;

    /// <summary>Switches connecting to HISTRA on or off; the stored token is kept either way.</summary>
    public void SetHistraEnabled(bool enabled)
    {
        Settings.HistraEnabled = enabled;
        Settings.Save();
        _histra = null;
        _titles = null;
        HistraChanged?.Invoke();
        TitlesChanged?.Invoke();
    }

    /// <summary>Stores the HISTRA token protected for the current Windows user (null removes it).</summary>
    public void SetHistraToken(string? token)
    {
        Settings.HistraTokenProtected = string.IsNullOrWhiteSpace(token) ? null : UserSecret.Protect(token.Trim());
        Settings.Save();
        _histra = null;
        _titles = null;
        HistraChanged?.Invoke();
        TitlesChanged?.Invoke();
    }

    /// <summary>Raised when the HISTRA token changes.</summary>
    public event Action? HistraChanged;

    private SpeechTools? _speech;

    /// <summary>
    /// The tools of the recognition of the spoken language, installed with the program in "speech" (UNIVERSUM_SPEECH_DIR
    /// for tests), checked against their manifest once per run; <see cref="SpeechException"/> when missing or changed.
    /// </summary>
    /// <summary>
    /// FFmpeg's number of an audio track (<paramref name="ordinal"/>: its number as MKVToolNix lists the audio tracks): the
    /// same in Matroska; in another container (MPEG TS) found by the stream's ID – MKVToolNix's order when FFmpeg is not there.
    /// </summary>
    public async Task<int> AudioOrdinalAsync(string path, MediaTrack track, int ordinal, CancellationToken cancellationToken = default)
    {
        if (AudioStreamOrder.IsMatroska(path))
        {
            return ordinal;
        }

        try
        {
            var ffmpeg = (await SpeechAsync(cancellationToken)).Ffmpeg;
            var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            return File.Exists(ffprobe) ? await AudioStreamOrder.OrdinalAsync(Runner, ffprobe, path, track, ordinal, cancellationToken) : ordinal;
        }
        catch (Exception ex) when (ex is SpeechException or IOException or ProcessStartException or TimeoutException)
        {
            return ordinal;
        }
    }

    public async Task<SpeechTools> SpeechAsync(CancellationToken cancellationToken = default)
    {
        if (_speech is { } verified)
        {
            return verified;
        }

        var folder = Environment.GetEnvironmentVariable("UNIVERSUM_SPEECH_DIR") is { Length: > 0 } custom ? custom : SpeechTools.DefaultFolder;
        var tools = SpeechTools.In(folder) ?? throw new SpeechException("Rozpoznávanie reči nie je nainštalované – nainštalujte UNIVERSUM znova (inštalátor ho obsahuje).");
        await tools.VerifyAsync(cancellationToken);
        return _speech = tools;
    }

    private PosterCache? _posters;

    /// <summary>
    /// Posters of films and series from the server in the settings (TMDB's image server by default), kept on the disk;
    /// null when posters are switched off.
    /// </summary>
    public PosterCache? Posters =>
        PostersForSelfTest ?? (!Settings.PostersEnabled ? null
            : _posters ??= new PosterCache(Http, AppSettings.PosterDirectory, PosterCache.ParseServer(Settings.PosterServer) ?? PosterCache.DefaultServer));

    /// <summary>The self-test's stand-in poster server (invented pictures, nothing goes to the internet).</summary>
    public PosterCache? PostersForSelfTest { get; set; }

    /// <summary>Switches posters on or off and sets their server (null = TMDB's image server).</summary>
    public void SetPosters(bool enabled, Uri? server)
    {
        Settings.PostersEnabled = enabled;
        Settings.PosterServer = server is null || server == PosterCache.DefaultServer ? null : server.AbsoluteUri;
        Settings.Save();
        _posters = null;
        PostersChanged?.Invoke();
    }

    /// <summary>Raised when posters are switched on or off or their server changes.</summary>
    public event Action? PostersChanged;

    /// <summary>The user's HISTRA lists, read only (null without a usable token or with HISTRA switched off).</summary>
    public HistraLists? HistraLists => HistraListsForSelfTest ?? (Histra is { } histra ? new HistraLists(histra) : null);

    /// <summary>The self-test's stand-in HISTRA lists (invented answers, nothing goes to the internet).</summary>
    public HistraLists? HistraListsForSelfTest { get; set; }

    /// <summary>The HISTRA lists imported into UNIVERSUM (a file of this Windows account, never sent anywhere).</summary>
    public HistraListStore HistraListStore => _histraListStore ??= new(AppSettings.HistraListsFile);

    private HistraListStore? _histraListStore;

    /// <summary>
    /// Napovedkyňa (owner, 30. 10. 2026): the series processed before with the titles chosen for them on TMDB and HISTRA –
    /// the next episodes get them without a search (a file of this Windows account, never sent anywhere).
    /// </summary>
    public SeriesMemory SeriesMemory => _seriesMemory ??= new(AppSettings.SeriesMemoryFile);

    private SeriesMemory? _seriesMemory;

    /// <summary>The saved work of Úprava titulkostopy (Uložiť prácu; owner, 30. 10. 2026).</summary>
    public StudioWorkStore StudioWork => _studioWork ??= new(AppSettings.StudioWorkDirectory);

    /// <summary>Saved work of Úprava audiostopy (a folder of its own beside the subtitles').</summary>
    public Core.Audio.AudioWorkStore AudioWork => _audioWork ??= new(Path.Combine(AppSettings.StudioWorkDirectory, "zvuk"));

    private Core.Audio.AudioWorkStore? _audioWork;

    private StudioWorkStore? _studioWork;

    /// <summary>Napovedkyňa for one file (Single spracovanie, the tools): the remembered series its names point to, or null.</summary>
    public TitleInfo? RememberedSeriesFor(string path)
    {
        var guess = TitleGuess.ForFile(path);
        return guess.Kind == MediaKind.Series ? SeriesMemory.Find([guess.Title, LibraryJob.SeriesNameOfFiles([path])]) : null;
    }

    /// <summary>Remembers the series a file goes into the library as, under the names the file carries (napovedkyňa).</summary>
    public void RememberSeries(TitleInfo? series, string path)
    {
        if (series is { Kind: MediaKind.Series })
        {
            SeriesMemory.Remember(series, [TitleGuess.ForFile(path).Title?.Name, LibraryJob.SeriesNameOfFiles([path])?.Name], DateTime.UtcNow);
        }
    }

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
    })
    { Timeout = TimeSpan.FromSeconds(30) };

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

    /// <summary>Posters waiting for their titles to be processed (Zlúčiť poster s filmom).</summary>
    public Universum.Core.Posters.PendingPosters WaitingPosters => _waitingPosters ??= new Universum.Core.Posters.PendingPosters(AppSettings.WaitingPosterDirectory, () => Settings.OutputDirectory);

    // drawing takes up to a minute or two
    private static readonly HttpClient DraftHttp = new() { Timeout = TimeSpan.FromMinutes(3) };

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

    // owner, 30. 10. 2026: subtitles from OpenSubtitles.com and translations by svsub.net with the user's own accounts,
    // kept encrypted for this Windows account (like the DeepL key)
    private static readonly HttpClient SubtitleHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>The name and version UNIVERSUM introduces itself with to subtitle services.</summary>
    public static string SubtitleUserAgent { get; } = "UNIVERSUM v" + (typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0");

    /// <summary>OpenSubtitles.com: the API key, the user name and the password (null when not set).</summary>
    public (string? Key, string? User, string? Password) OpenSubtitlesAccount =>
        (Settings.OpenSubtitlesKeyProtected is { Length: > 0 } key ? UserSecret.Unprotect(key) : null, Settings.OpenSubtitlesUser,
            Settings.OpenSubtitlesPasswordProtected is { Length: > 0 } password ? UserSecret.Unprotect(password) : null);

    /// <summary>Stores the OpenSubtitles.com account (empty removes it); the key and the password encrypted.</summary>
    public void SetOpenSubtitlesAccount(string? key, string? user, string? password)
    {
        Settings.OpenSubtitlesKeyProtected = string.IsNullOrWhiteSpace(key) ? null : UserSecret.Protect(key.Trim());
        Settings.OpenSubtitlesUser = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        Settings.OpenSubtitlesPasswordProtected = string.IsNullOrEmpty(password) ? null : UserSecret.Protect(password);
        Settings.Save();
    }

    /// <summary>An OpenSubtitles.com client with the user's key; null without one.</summary>
    public Core.Subtitles.Download.OpenSubtitlesClient? OpenSubtitles() =>
        OpenSubtitlesAccount.Key is { } key ? new Core.Subtitles.Download.OpenSubtitlesClient(SubtitleHttp, key, SubtitleUserAgent) : null;

    /// <summary>The svsub.net token (null when not set).</summary>
    public string? SvSubToken => Settings.SvSubTokenProtected is { Length: > 0 } token ? UserSecret.Unprotect(token) : null;

    /// <summary>The AI provider svsub.net translates with ("openai" by default).</summary>
    public string SvSubProvider => Settings.SvSubProvider is { Length: > 0 } provider ? provider : "openai";

    /// <summary>The AI model svsub.net translates with (owner: "gpt-6-astra").</summary>
    public string SvSubModel => Settings.SvSubModel ?? "gpt-6-astra";

    /// <summary>Stores the svsub.net token (empty removes it, encrypted), the AI provider and the model (empty = the service's default).</summary>
    public void SetSvSub(string? token, string? provider, string? model = null)
    {
        Settings.SvSubTokenProtected = string.IsNullOrWhiteSpace(token) ? null : UserSecret.Protect(token.Trim());
        Settings.SvSubProvider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();
        if (model is not null)
        {
            Settings.SvSubModel = model.Trim();
        }

        Settings.Save();
    }

    /// <summary>An svsub.net client with the user's token; null without one.</summary>
    public Core.Subtitles.Download.SvSubClient? SvSub() =>
        SvSubToken is { } token ? new Core.Subtitles.Download.SvSubClient(SubtitleHttp, token) : null;

    /// <summary>The user's Downloads folder (titulky.com: the downloaded subtitles are taken from there).</summary>
    public static string DownloadsFolder => Environment.GetEnvironmentVariable("UNIVERSUM_DOWNLOADS_DIR") is { Length: > 0 } custom ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

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

    /// <summary>The job queue (one for the whole program; jobs run one after another).</summary>
    public Universum.Core.Jobs.JobQueue Jobs => _jobs ??= Queue();

    private Universum.Core.Jobs.JobQueue? _jobs;

    /// <summary>How the end of the queue is announced (the self-test records it instead of a sound).</summary>
    public Action<bool> PlayFinishSound { get; set; } = FinishSound.Play;

    /// <summary>
    /// The logs of processing (owner, 31. 10. 2026): every run of Hromadné and Single spracovanie, MEGA, the studios and the
    /// other editors, in words; kept in the settings folder until the user deletes them.
    /// </summary>
    public Universum.Core.Jobs.ProcessingLogStore ProcessingLogs => _processingLogs ??= new(Path.Combine(AppSettings.Directory, "zaznamy"));

    private Universum.Core.Jobs.ProcessingLogStore? _processingLogs;

    // jobs already in the logs: the job and the end of its run (a job run again is logged again)
    private readonly HashSet<(int, DateTime?)> _loggedJobs = [];

    /// <summary>A file processed directly by a page (not through the queue) into the logs.</summary>
    public void LogProcessing(string kind, Universum.Core.Planning.EditPlan plan, Universum.Core.Execution.ProcessingResult result, DateTime startedUtc)
    {
        var file = Universum.Core.Execution.ProcessingWords.Describe(plan.Target.Source.Path, plan, result);
        KeepLog(new Universum.Core.Jobs.ProcessingLog(kind, startedUtc, DateTime.UtcNow, file.Headline, [file]));
    }

    // a log written to its file; a log that cannot be written is not worth a failure of the processing
    private void KeepLog(Universum.Core.Jobs.ProcessingLog log)
    {
        try
        {
            ProcessingLogs.Add(log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the processing itself is done; only its log is missing
        }
    }

    // the queue, with the sound of its end (owner, 29. 10. 2026: Hromadné spracovanie and the queue; can be switched off) and
    // the logs of its jobs
    private Universum.Core.Jobs.JobQueue Queue()
    {
        var queue = new Universum.Core.Jobs.JobQueue();
        queue.JobChanged += job =>
        {
            if (job.IsFinished && job is Universum.Core.Jobs.ILoggedJob logged)
            {
                lock (_loggedJobs)
                {
                    if (!_loggedJobs.Add((job.Id, job.FinishedUtc)))
                    {
                        return;
                    }
                }

                KeepLog(new Universum.Core.Jobs.ProcessingLog(logged.LogKind, job.StartedUtc ?? job.AddedUtc, job.FinishedUtc ?? DateTime.UtcNow, job.Outcome, logged.LoggedFiles));
            }
        };
        queue.Finished += jobs =>
        {
            if (Settings.FinishSound)
            {
                PlayFinishSound(jobs.Any(j => j.Status is Universum.Core.Jobs.JobStatus.Failed || j.Errors > 0));
            }
        };
        return queue;
    }

    /// <summary>Adds a job; starts the queue when the settings say so.</summary>
    public void Enqueue(Universum.Core.Jobs.Job job) => Jobs.Add(job, Settings.AutoStartQueue);

    private Universum.Core.Mega.MegaStore? _mega;

    /// <summary>
    /// The batches of MEGA spracovanie (owner, 29. 10. 2026). A list that cannot be read is put aside (renamed, never
    /// deleted) and a new one starts; <see cref="MegaProblem"/> says so.
    /// </summary>
    public Universum.Core.Mega.MegaStore Mega
    {
        get
        {
            if (_mega is null)
            {
                try
                {
                    _mega = new Universum.Core.Mega.MegaStore(AppSettings.MegaFile);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    var aside = AppSettings.MegaFile + ".poskodeny-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    var file = AppSettings.MegaFile;
                    try
                    {
                        File.Move(AppSettings.MegaFile, aside);
                    }
                    catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                    {
                        // it stays where it is; the new list is kept beside it
                        aside = AppSettings.MegaFile;
                        file = AppSettings.MegaFile + ".novy";
                    }

                    MegaProblem = $"Zoznam dávok sa nedal prečítať ({ex.Message}); pôvodný je odložený v {aside}.";
                    _mega = new Universum.Core.Mega.MegaStore(file);
                }
            }

            return _mega;
        }
    }

    /// <summary>Why the list of MEGA batches could not be read (empty when it was).</summary>
    public string MegaProblem { get; private set; } = "";

    /// <summary>Raised when a batch was added or changed outside the MEGA page (Hromadné spracovanie sent one).</summary>
    public event Action? MegaChanged;

    public void NotifyMegaChanged() => MegaChanged?.Invoke();

    private Updates.UpdateService? _updates;

    /// <summary>Update check of MKVToolNix and UNIVERSUM (one for the whole program).</summary>
    public Updates.UpdateService Updates => _updates ??= new Updates.UpdateService(this);

    /// <summary>Rules profile chosen in the settings (or the default of the specification).</summary>
    public RuleProfile Profile => Settings.Profile ?? RuleProfile.Default;

    /// <summary>MKVToolNix found by the last search; null when none is usable.</summary>
    public MkvToolNixInstallation? CurrentMkvToolNix { get; private set; }

    public async Task<MkvToolNixInstallation?> FindMkvToolNixAsync(CancellationToken cancellationToken = default)
    {
        var locator = new MkvToolNixLocator(Runner, [new RegistryMkvToolNixLocations()]);
        CurrentMkvToolNix = await locator.FindAsync(Settings.MkvToolNixDirectory, cancellationToken);
        await LoadLanguagesAsync(CurrentMkvToolNix, cancellationToken);
        return CurrentMkvToolNix;
    }

    /// <summary>Uses a folder chosen by the user; it is remembered only when it contains working tools.</summary>
    public async Task<MkvToolNixInstallation?> UseMkvToolNixDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        var installation = await MkvToolNixInstallation.OpenAsync(directory, Runner, cancellationToken);
        if (installation is not null)
        {
            Settings.MkvToolNixDirectory = installation.Directory;
            Settings.Save();
            CurrentMkvToolNix = installation;
            await LoadLanguagesAsync(installation, cancellationToken);
        }

        return installation;
    }

    // owner, 30. 10. 2026: every language MKVToolNix knows in the lists (below the common ones), e.g. Filipino
    private async Task LoadLanguagesAsync(MkvToolNixInstallation? installation, CancellationToken cancellationToken)
    {
        if (installation is not null && Languages.All.Count == Languages.Common.Count)
        {
            Languages.Extend(await MkvToolNixLanguages.ReadAsync(installation, Runner, cancellationToken));
        }
    }
}

/// <summary>User settings stored as JSON in %APPDATA%\UNIVERSUM\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Folder for settings; the self-test points it to a temporary folder.</summary>
    public static string Directory =>
        Environment.GetEnvironmentVariable("UNIVERSUM_SETTINGS_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UNIVERSUM");

    public string? MkvToolNixDirectory { get; set; }

    /// <summary>Keep or delete (to the Recycle Bin) the original; null until the user chooses the first time.</summary>
    public OriginalHandling? OriginalHandling { get; set; }

    /// <summary>Output folder with MOVIES and TVSHOWS (specification, section 13); must be set before processing.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Where new films and episodes come (Novinky look there); null until the user chooses it.</summary>
    public string? InputDirectory { get; set; }

    /// <summary>Check for updates at start and then once a day.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>The job queue starts as soon as a job is added.</summary>
    public bool AutoStartQueue { get; set; } = true;

    /// <summary>The input folder is watched: new films and episodes are told (owner, 31. 10. 2026).</summary>
    public bool WatchInput { get; set; } = true;

    /// <summary>Hromadné spracovanie: the panels below the tables hidden (more room in a small window).</summary>
    public bool BatchPanelsHidden { get; set; }

    /// <summary>A sound when the queue (Hromadné spracovanie too) has done all its jobs (owner, 29. 10. 2026).</summary>
    public bool FinishSound { get; set; } = true;

    /// <summary>Text z obrázkov (OCR): the text read is checked by the language and mended (owner, 31. 10. 2026).</summary>
    public bool OcrCheckText { get; set; } = true;

    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>"Skip this version" (MKVToolNix, UNIVERSUM).</summary>
    public string? SkippedMkvToolNixVersion { get; set; }

    public string? SkippedUniversumVersion { get; set; }

    /// <summary>"Remind me later": no offer before this time (security fixes excepted).</summary>
    public DateTime? RemindUpdatesAfterUtc { get; set; }

    /// <summary>The user's TMDB key, protected with the Windows data protection API (readable only by this account).</summary>
    public string? TmdbKeyProtected { get; set; }

    /// <summary>The user's HISTRA token, protected with the Windows data protection API (readable only by this account).</summary>
    public string? HistraTokenProtected { get; set; }

    /// <summary>Connect to TMDB (the key stays stored when switched off).</summary>
    public bool TmdbEnabled { get; set; } = true;

    /// <summary>Connect to HISTRA (the token stays stored when switched off).</summary>
    public bool HistraEnabled { get; set; } = true;

    /// <summary>Own rules profile; null = the profile of the specification.</summary>
    public RuleProfile? Profile { get; set; }

    /// <summary>What Hromadné spracovanie does with the originals (owner, 30. 10. 2026; kept by default).</summary>
    public OriginalsMode BatchOriginals { get; set; } = OriginalsMode.Keep;

    /// <summary>Last folder of the batch page.</summary>
    public string? BatchFolder { get; set; }

    public bool BatchRecursive { get; set; } = true;

    /// <summary>Multiplexer: what to do with several added files without asking (null = ask).</summary>
    public string? MuxAddAction { get; set; }

    /// <summary>Multiplexer: files with video always get new multiplex settings (with the remembered action).</summary>
    public bool MuxNewSettingsForVideo { get; set; }

    /// <summary>Town of the Dashboard weather as Open-Meteo found it; null = no weather is asked for.</summary>
    public WeatherPlace? WeatherPlace { get; set; }

    /// <summary>Posters are shown (Listy z HISTRA, Premiéry, Novinky).</summary>
    public bool PostersEnabled { get; set; } = true;

    /// <summary>The server posters come from; null = TMDB's image server (HISTRA gives poster paths in TMDB's form).</summary>
    public string? PosterServer { get; set; }

    /// <summary>The sources of Zaujímavosti (null = none yet).</summary>
    public List<Universum.Core.News.FeedSource>? FeedSources { get; set; }

    /// <summary>The DeepL key, protected for the Windows account.</summary>
    public string? DeepLKeyProtected { get; set; }

    /// <summary>TMDB poster: the Cloudflare account ID, its API token (protected), the OpenAI key (protected), the OMDb key (protected).</summary>
    public string? CloudflareAccount { get; set; }

    public string? CloudflareTokenProtected { get; set; }

    public string? OpenAiKeyProtected { get; set; }

    public string? OmdbKeyProtected { get; set; }

    /// <summary>TMDB poster: the JPEG quality (50–100), the height of folder.jpg (750–3000) and Zlúčiť poster s filmom.</summary>
    public int PosterQuality { get; set; } = 90;

    public int PosterHeight { get; set; } = 1500;

    public bool PosterMerge { get; set; }

    /// <summary>TMDB poster: Ďalšie TMDB obrázky (off by default) and which of them – background, logo, season posters, episode stills.</summary>
    public bool PosterExtras { get; set; }

    public bool PosterBackdrop { get; set; } = true;

    public bool PosterLogo { get; set; } = true;

    public bool PosterSeasons { get; set; } = true;

    public bool PosterEpisodes { get; set; } = true;

    /// <summary>OpenSubtitles.com: the API key (protected), the user name, the password (protected).</summary>
    public string? OpenSubtitlesKeyProtected { get; set; }

    public string? OpenSubtitlesUser { get; set; }

    public string? OpenSubtitlesPasswordProtected { get; set; }

    /// <summary>svsub.net: the token (protected) and the AI provider ("openai").</summary>
    public string? SvSubTokenProtected { get; set; }

    public string? SvSubProvider { get; set; }

    /// <summary>svsub.net: the AI model (null = "gpt-6-astra", empty = the service's default).</summary>
    public string? SvSubModel { get; set; }

    /// <summary>The language of Zaujímavosti ("sk", later "cs" or "en").</summary>
    public string InterestLanguage { get; set; } = "sk";

    /// <summary>Zaujímavosti show the pictures of articles.</summary>
    public bool InterestImages { get; set; } = true;

    /// <summary>The day Premiéry, Novinky and Zaujímavosti were last brought up to date at the start (once a day).</summary>
    public string? LastDailyUpdate { get; set; }

    /// <summary>Premiéry show posters.</summary>
    public bool PremierePosters { get; set; } = true;

    /// <summary>Novinky – Seriály show posters (of the whole series).</summary>
    public bool NewsPosters { get; set; } = true;

    /// <summary>The size of the posters of Premiéry, 1× to 2×.</summary>
    public double PremierePosterScale { get; set; } = 1;

    /// <summary>The size of the posters of Novinky – Seriály, 1× to 2×.</summary>
    public double NewsPosterScale { get; set; } = 1;

    /// <summary>The sources of news (Premiéry, Novinky); null = the defaults (TVmaze, TMDB). Web pages are added by the user.</summary>
    public List<Universum.Core.News.NewsSource>? NewsSources { get; set; }

    /// <summary>Folder with undo records of in-place edits.</summary>
    public static string JournalDirectory =>
        Environment.GetEnvironmentVariable("UNIVERSUM_SETTINGS_DIR") is { Length: > 0 } custom
            ? Path.Combine(custom, "journal")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UNIVERSUM", "journal");

    /// <summary>Saved batch templates (*.json).</summary>
    public static string TemplateDirectory => Path.Combine(Directory, "templates");

    /// <summary>Index of library files and the originals they were made from.</summary>
    public static string LibraryIndexFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "library-index.json");

    /// <summary>Cache of TMDB answers (titles only, never the key).</summary>
    public static string TmdbCacheFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "tmdb-cache.json");

    /// <summary>Cache of identification results (path, size, time → structure).</summary>
    public static string IdentificationCacheFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "identification-cache.json");

    /// <summary>Downloaded posters (each one once).</summary>
    public static string PosterDirectory => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "posters");

    /// <summary>The days read from the sources of news.</summary>
    public static string NewsCacheDirectory => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "news");

    /// <summary>Where the chosen titles can be watched legally, as last checked.</summary>
    public static string AvailabilityFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "availability.json");

    /// <summary>The batches of MEGA spracovanie.</summary>
    public static string MegaFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "mega.json");

    /// <summary>The articles of Zaujímavosti read so far (their history).</summary>
    public static string InterestsFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "interests.json");

    /// <summary>Downloaded pictures of articles.</summary>
    public static string ImageDirectory => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "images");

    /// <summary>The premieres read so far (the history of Premiéry).</summary>
    public static string PremieresFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "premieres.json");

    /// <summary>The user's marks of titles (chosen, notes).</summary>
    public static string MarksFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "news-marks.json");

    /// <summary>The saved work of Úprava titulkostopy (one file a video).</summary>
    public static string StudioWorkDirectory => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "studio");

    /// <summary>Napovedkyňa: the series processed before and their titles.</summary>
    public static string SeriesMemoryFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "napovedkyna.json");

    /// <summary>Posters waiting for their titles to be processed (TMDB poster, Zlúčiť poster s filmom).</summary>
    public static string WaitingPosterDirectory => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "posters-waiting");

    /// <summary>OMDb's ratings kept 7 days.</summary>
    public static string OmdbCacheFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "omdb-cache.json");

    /// <summary>The HISTRA lists imported into UNIVERSUM.</summary>
    public static string HistraListsFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "histra-lists.json");

    public static AppSettings Load()
    {
        try
        {
            var path = Path.Combine(Directory, "settings.json");
            var settings = File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();

            // originals are always kept outside Hromadné spracovanie (owner's decisions of 26 and 30 October 2026)
            settings.OriginalHandling = Universum.Core.Execution.OriginalHandling.Keep;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new() { OriginalHandling = Universum.Core.Execution.OriginalHandling.Keep };
        }
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(Path.Combine(Directory, "settings.json"), JsonSerializer.Serialize(this, JsonOptions));
    }
}
