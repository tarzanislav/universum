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

// AppServices: the titles – TMDB, HISTRA, the posters of the library, the register of titles, the studios
public sealed partial class AppServices
{
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

    /// <summary>Spracované tituly (owner, 7. 11. 2026): the films and series written into the libraries; the user's own data.</summary>
    public TitleRegister TitleRegister
    {
        get
        {
            if (_titleRegister is null)
            {
                _titleRegister = new(AppSettings.TitleRegisterFile);
                // never on the thread that processes files (owner, 7. 11. 2026: Superrýchle must stay fast)
                _titleRegister.Changed += () => _ = Task.Run(FillTitleNamesAsync);
            }

            return _titleRegister;
        }
    }

    private TitleRegister? _titleRegister;
    private int _fillingNames;

    /// <summary>
    /// Owner, 7. 11. 2026: the Czech and English names of the titles in Spracované tituly, from TMDB by their IDs (one
    /// question per title, kept 30 days in TMDB's cache; only the ID is sent). Without a TMDB key nothing is asked.
    /// </summary>
    public async Task FillTitleNamesAsync()
    {
        if (Tmdb is not { } tmdb || Interlocked.Exchange(ref _fillingNames, 1) == 1)
        {
            return;
        }

        try
        {
            // the answers are written 25 titles at a time (not the whole register for each title)
            var details = new TmdbTitleDetails(tmdb);
            var names = new List<(MediaKind, int, string, string)>();
            try
            {
                foreach (var title in TitleRegister.WithoutNames)
                {
                    var found = await details.GetAsync(title.Kind, title.TmdbId!.Value).ConfigureAwait(false);
                    names.Add((title.Kind, title.TmdbId!.Value, found?.CzechTitle ?? "", found?.EnglishTitle ?? ""));
                    if (names.Count == 25)
                    {
                        TitleRegister.SetNames(names);
                        names.Clear();
                    }
                }
            }
            finally
            {
                if (names.Count > 0)
                {
                    TitleRegister.SetNames(names);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or TitleServiceException)
        {
            // asked again with the next change or start of Spracované tituly
        }
        finally
        {
            Volatile.Write(ref _fillingNames, 0);
        }
    }

    private SeriesMemory? _seriesMemory;

    /// <summary>The saved work of Úprava titulkostopy (Uložiť prácu; owner, 30. 10. 2026).</summary>
    public StudioWorkStore StudioWork => _studioWork ??= new(AppSettings.StudioWorkDirectory);

    /// <summary>Saved work of Úprava audiostopy (a folder of its own beside the subtitles').</summary>
    public Core.Audio.AudioWorkStore AudioWork => _audioWork ??= new(Path.Combine(AppSettings.StudioWorkDirectory, "zvuk"));

    private Core.Audio.AudioWorkStore? _audioWork;

    private StudioWorkStore? _studioWork;

    /// <summary>Napovedkyňa for one file (Single spracovanie, the tools): the remembered series or film its names point to, or null.</summary>
    public TitleInfo? RememberedTitleFor(string path)
    {
        var guess = TitleGuess.ForFile(path);
        return guess.Kind == MediaKind.Series ? SeriesMemory.Find([guess.Title, LibraryJob.SeriesNameOfFiles([path])])
            : SeriesMemory.Find([guess.Title], MediaKind.Movie);
    }

    /// <summary>Remembers the series or film a file goes into the library as, under the names the file carries (napovedkyňa).</summary>
    public void RememberTitle(TitleInfo? title, string path)
    {
        if (title is { Kind: MediaKind.Series })
        {
            SeriesMemory.Remember(title, [TitleGuess.ForFile(path).Title?.Name, LibraryJob.SeriesNameOfFiles([path])?.Name], DateTime.UtcNow);
        }
        else if (title is { Kind: MediaKind.Movie })
        {
            SeriesMemory.Remember(title, [TitleGuess.ForFile(path).Title?.Name], DateTime.UtcNow);
        }
    }
}
