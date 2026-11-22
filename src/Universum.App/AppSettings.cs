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

    /// <summary>
    /// Output folders on other disks or disk arrays (owner, 2. 11. 2026): each a library of its own (MOVIES, TVSHOWS); a
    /// file goes into the one on its own disk.
    /// </summary>
    public List<string> OtherOutputDirectories { get; set; } = [];

    /// <summary>A file goes into the output folder on its own disk (off: every file into the default one, as before).</summary>
    public bool OutputBySourceDisk { get; set; } = true;

    /// <summary>On how many disks files are written at the same time (one file per disk; 1 = one after another).</summary>
    public int ParallelDisks { get; set; } = 2;

    /// <summary>Where new films and episodes come (Novinky look there); null until the user chooses it.</summary>
    public string? InputDirectory { get; set; }

    /// <summary>Input folders on other disks (owner, 8. 11. 2026), one per disk.</summary>
    public List<string> OtherInputDirectories { get; set; } = [];

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

    /// <summary>The language of the window: "sk", "cs" or "en" (owner, 31. 10. 2026; applied after a restart).</summary>
    public string UiLanguage { get; set; } = "sk";

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

    /// <summary>Zásobník TMDB: UNIVERSUM's script in the browser may send films and series (owner, 8. 11. 2026).</summary>
    public bool TmdbTrayReceive { get; set; } = true;

    /// <summary>
    /// Zásobník TMDB gathers by itself: every TMDB film or series page opened in the browser is added (owner, 8. 11. 2026:
    /// switched on and off at the top of Hromadné, Single and the other pages; off, only the button of the page adds).
    /// </summary>
    public bool TmdbTrayAuto { get; set; }

    /// <summary>Episodes into the tray with their season and number (owner, 8. 11. 2026: there, but off for now – off, an episode page adds its series).</summary>
    public bool TmdbTrayEpisodes { get; set; }

    /// <summary>The width of the note opened in Poznámky (owner, 8. 11. 2026: notes can be made larger).</summary>
    public double NotesEditorWidth { get; set; } = 340;

    /// <summary>The port of the receiver on 127.0.0.1 (the script knows it).</summary>
    public int TmdbTrayPort { get; set; } = 47219;

    /// <summary>The secret key the script sends; made at the first start, a new one makes the old script stop working.</summary>
    public string? TmdbTrayKey { get; set; }

    /// <summary>
    /// Ponechať MP4 (owner, 7. 11. 2026): an MP4 whose header only changes stays MP4 – UNIVERSUM edits its header itself
    /// (with Superrýchle in seconds); off, every MP4 is written as MKV, as before.
    /// </summary>
    public bool KeepMp4 { get; set; }

    /// <summary>Poznámky: a link of TMDB shown by the name of its film or series (owner, 7. 11. 2026: Meno namiesto linku).</summary>
    public bool NoteLinkNames { get; set; }

    /// <summary>What Hromadné spracovanie does with the originals (owner, 30. 10. 2026; kept by default).</summary>
    public OriginalsMode BatchOriginals { get; set; } = OriginalsMode.Keep;

    /// <summary>Last folder of the batch page.</summary>
    public string? BatchFolder { get; set; }

    /// <summary>Hromadné analyses every input folder at once (owner, 9. 11. 2026 – one on each disk); off: only <see cref="BatchFolder"/>.</summary>
    public bool BatchAllInputs { get; set; } = true;

    public bool BatchRecursive { get; set; } = true;

    /// <summary>Hromadné spracovanie: the language of audio tracks without one is heard right after the analysis (owner, 7. 11. 2026).</summary>
    public bool BatchDetectAudio { get; set; } = true;

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

    /// <summary>TMDB poster: the JPEG quality (50–100), the height of folder.jpg (750–3000) and Zlúčiť poster s filmom/seriálom.</summary>
    public int PosterQuality { get; set; } = 90;

    public int PosterHeight { get; set; } = 1500;

    public bool PosterMerge { get; set; }

    /// <summary>TMDB poster: Ďalšie TMDB obrázky (off by default) and which of them – background, logo, season posters, episode stills.</summary>
    public bool PosterExtras { get; set; }

    public bool PosterBackdrop { get; set; } = true;

    public bool PosterLogo { get; set; } = true;

    public bool PosterSeasons { get; set; } = true;

    public bool PosterEpisodes { get; set; } = true;

    /// <summary>TMDB poster: Zlúčiť ďalšie obrázky z TMDB s filmom/seriálom (owner, 31. 10. 2026; apart from the poster's).</summary>
    public bool PosterExtrasMerge { get; set; }

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

    /// <summary>Kalendár (owner, 31. 10. 2026): the regions of the holidays; null: by the language of the interface.</summary>
    public List<string>? CalendarRegions { get; set; }

    /// <summary>Kalendár: the Slovak names shown.</summary>
    public bool CalendarNamesSk { get; set; } = true;

    /// <summary>Kalendár: the Czech names shown.</summary>
    public bool CalendarNamesCs { get; set; } = true;

    /// <summary>Kalendár: the English equivalents shown; null: only in the English interface.</summary>
    public bool? CalendarNamesEn { get; set; }

    /// <summary>Kalendár: the Slovak region of the school holidays (kraj code); null: all.</summary>
    public string? CalendarSchoolRegion { get; set; }

    /// <summary>Kalendár: the Czech district of the school holidays; null: all.</summary>
    public string? CalendarSchoolDistrict { get; set; }

    /// <summary>Kalendár: the layers switched off.</summary>
    public List<string> CalendarHiddenLayers { get; set; } = [];

    /// <summary>Kalendár: the panel of the layers open (closed it leaves the days more room).</summary>
    public bool CalendarLayersShown { get; set; } = true;

    /// <summary>
    /// Zlučovanie: the folder of previews (owner, 1. 11. 2026: never the output folders); null: UNIVERSUM's own folder in
    /// the local application data.
    /// </summary>
    public string? MuxPreviewFolder { get; set; }

    /// <summary>Zlučovanie: how much a preview writes – "Samples" (three pieces of two minutes) or "Whole".</summary>
    public string MuxPreviewRange { get; set; } = "Samples";

    /// <summary>Kalendár: the view last chosen.</summary>
    public string CalendarView { get; set; } = "Month";

    /// <summary>Kalendár: only the chosen titles (Vybrané) of Premiéry and Novinky.</summary>
    public bool CalendarOnlyChosen { get; set; }

    /// <summary>Tlač a export of POZNÁMKY: the choices of printing (paper, orientation, pages on a sheet …).</summary>
    public Universum.Core.Reports.ReportOptions Print { get; set; } = new();

    /// <summary>Plánované úlohy: the done tasks shown in the list.</summary>
    public bool TasksShowDone { get; set; }

    /// <summary>Plánované úlohy: the board instead of the list.</summary>
    public bool TasksBoard { get; set; }

    /// <summary>Plánované úlohy: the suggestions hidden (their keys; the latest 500).</summary>
    public List<string> HiddenSuggestions { get; set; } = [];

    /// <summary>The sources of news (Premiéry, Novinky); null = the defaults (TVmaze, TMDB). Web pages are added by the user.</summary>
    public List<Universum.Core.News.NewsSource>? NewsSources { get; set; }

    /// <summary>The default folder of the previews of Zlučovanie (UNIVERSUM's own, never an output folder).</summary>
    public static string PreviewDirectory =>
        Environment.GetEnvironmentVariable("UNIVERSUM_SETTINGS_DIR") is { Length: > 0 } custom
            ? Path.Combine(custom, "nahlady")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UNIVERSUM", "Nahlady");

    /// <summary>Folder with undo records of in-place edits.</summary>
    public static string JournalDirectory =>
        Environment.GetEnvironmentVariable("UNIVERSUM_SETTINGS_DIR") is { Length: > 0 } custom
            ? Path.Combine(custom, "journal")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UNIVERSUM", "journal");

    /// <summary>Saved batch templates (*.json).</summary>
    public static string TemplateDirectory => Path.Combine(Directory, "templates");

    /// <summary>Záznamy MP4 for the developer (owner, 7. 11. 2026): the structure of an MP4 that could not be edited, no names.</summary>
    public static string Mp4RecordDirectory => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "zaznamy-mp4");

    /// <summary>The log of unexpected errors of work in the background (14. 11. 2026).</summary>
    public static string ErrorLogFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "chyby.log");

    /// <summary>Index of library files and the originals they were made from.</summary>
    public static string LibraryIndexFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "library-index.json");

    /// <summary>Cache of TMDB answers (titles only, never the key).</summary>
    public static string TmdbCacheFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "tmdb-cache.json");

    /// <summary>Cache of identification results (path, size, time → structure).</summary>
    public static string IdentificationCacheFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "identification-cache.json");

    /// <summary>Downloaded posters (each one once).</summary>
    public static string PosterDirectory => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "posters");

    /// <summary>The posters picked into the Zásobník TMDB (owner, 8. 11. 2026 – ČSFD and TMDB).</summary>
    public static string TrayPosterDirectory => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "zasobnik-plagaty");

    /// <summary>The titles of ČSFD not found on TMDB (a poster drawn by AI from what ČSFD says).</summary>
    public static string CsfdTitlesFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "csfd-tituly.json");

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
    /// <summary>Spracované tituly (the register of titles written into the libraries).</summary>
    public static string TitleRegisterFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "spracovane-tituly.json");

    public static string SeriesMemoryFile => Path.Combine(Path.GetDirectoryName(JournalDirectory)!, "napovedkyna.json");

    /// <summary>Posters waiting for their titles to be processed (TMDB poster, Zlúčiť poster s filmom/seriálom).</summary>
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
