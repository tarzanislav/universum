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

/// <summary>
/// Shared services of the running application. The parts of this class are kept by subject in AppServices.*.cs (14. 11.
/// 2026, step 2 of the review of the code); this part: the tools, the settings, processing, speech, updates and MKVToolNix.
/// </summary>
public sealed partial class AppServices
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
            Register = TitleRegister,
            ParallelDisks = Math.Clamp(Settings.ParallelDisks, 1, 8),
            // a sound written anew (Úprava audiostopy, also in Hromadné spracovanie and MEGA): FFmpeg of the installation
            FindFfmpeg = async cancellationToken => (await SpeechAsync(cancellationToken)).Ffmpeg,
            // owner, 7. 11. 2026: what went wrong with an MP4, without names, for the developer
            Mp4RecordFolder = AppSettings.Mp4RecordDirectory,
        };

    /// <summary>Zásobník TMDB: the films and series picked in the browser, kept in a note of Poznámky (owner, 8. 11. 2026).</summary>
    public Pages.TmdbTrayService TmdbTray => _tmdbTray ??= new Pages.TmdbTrayService(this);

    private Pages.TmdbTrayService? _tmdbTray;

    /// <summary>
    /// The multiplexer of Zlučovanie, with FFmpeg (its ffprobe checks an MP4 that stays MP4) and the folder of the záznamy
    /// MP4 (owner, 7. 11. 2026).
    /// </summary>
    public Core.Muxing.Multiplexer Multiplexer(MkvToolNixInstallation installation) =>
        new(installation, Runner)
        {
            FindFfmpeg = async cancellationToken => (await SpeechAsync(cancellationToken)).Ffmpeg,
            Mp4RecordFolder = AppSettings.Mp4RecordDirectory,
        };

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
        var tools = SpeechTools.In(folder) ?? throw new SpeechException(Loc.T("Rozpoznávanie reči nie je nainštalované – nainštalujte UNIVERSUM znova (inštalátor ho obsahuje)."));
        await tools.VerifyAsync(cancellationToken);
        return _speech = tools;
    }

    private Updates.UpdateService? _updates;

    /// <summary>Update check of MKVToolNix and UNIVERSUM (one for the whole program).</summary>
    public Updates.UpdateService Updates => _updates ??= new Updates.UpdateService(this);

    /// <summary>
    /// Rules profile chosen in the settings (or the default of the specification), with Ponechať MP4 of the settings
    /// (owner, 7. 11. 2026: one switch for Hromadné, Single spracovanie and Nastavenia).
    /// </summary>
    public RuleProfile Profile
    {
        get
        {
            var profile = Settings.Profile ?? RuleProfile.Default;
            return profile.KeepMp4 == Settings.KeepMp4 ? profile : profile with { KeepMp4 = Settings.KeepMp4 };
        }
    }

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
