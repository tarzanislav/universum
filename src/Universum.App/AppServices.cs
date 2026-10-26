using System.IO;
using System.Net.Http;
using System.Text.Json;
using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Processes;
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
    public FileProcessor Processor(MkvToolNixInstallation installation) =>
        new(installation, Runner, new WindowsRecycleBin(), AppSettings.JournalDirectory, LibraryIndex);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private TmdbClient? _tmdb;

    /// <summary>The user's TMDB key (UNIVERSUM_TMDB_KEY wins over the settings); null when none is set.</summary>
    public string? TmdbKey =>
        Environment.GetEnvironmentVariable("UNIVERSUM_TMDB_KEY") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : Settings.TmdbKeyProtected is { Length: > 0 } stored ? UserSecret.Unprotect(stored) : null;

    /// <summary>TMDB client with the user's key; null without a key or with connecting switched off (the key stays).</summary>
    public TmdbClient? Tmdb => _tmdb ??= Settings.TmdbEnabled && TmdbKey is { } key ? new TmdbClient(Http, key, AppSettings.TmdbCacheFile) : null;

    /// <summary>Switches connecting to TMDB on or off; the stored key is kept either way.</summary>
    public void SetTmdbEnabled(bool enabled)
    {
        Settings.TmdbEnabled = enabled;
        Settings.Save();
        _tmdb = null;
        TmdbChanged?.Invoke();
    }

    /// <summary>Stores the TMDB key protected for the current Windows user (null removes it).</summary>
    public void SetTmdbKey(string? key)
    {
        Settings.TmdbKeyProtected = string.IsNullOrWhiteSpace(key) ? null : UserSecret.Protect(key.Trim());
        Settings.Save();
        _tmdb = null;
        TmdbChanged?.Invoke();
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

    /// <summary>
    /// TMDB and HISTRA both switched on: the two are to be kept in sync (what is synchronised is decided with the HISTRA
    /// API description; until then nothing is sent).
    /// </summary>
    public bool SyncActive => Tmdb is not null && HistraActive;

    /// <summary>Switches connecting to HISTRA on or off; the stored token is kept either way.</summary>
    public void SetHistraEnabled(bool enabled)
    {
        Settings.HistraEnabled = enabled;
        Settings.Save();
        HistraChanged?.Invoke();
    }

    /// <summary>Stores the HISTRA token protected for the current Windows user (null removes it).</summary>
    public void SetHistraToken(string? token)
    {
        Settings.HistraTokenProtected = string.IsNullOrWhiteSpace(token) ? null : UserSecret.Protect(token.Trim());
        Settings.Save();
        HistraChanged?.Invoke();
    }

    /// <summary>Raised when the HISTRA token changes.</summary>
    public event Action? HistraChanged;

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
        }

        return installation;
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

    /// <summary>Check for updates at start and then once a day.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

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

    /// <summary>Last folder of the batch page.</summary>
    public string? BatchFolder { get; set; }

    public bool BatchRecursive { get; set; } = true;

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

    public static AppSettings Load()
    {
        try
        {
            var path = Path.Combine(Directory, "settings.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(Path.Combine(Directory, "settings.json"), JsonSerializer.Serialize(this, JsonOptions));
    }
}
