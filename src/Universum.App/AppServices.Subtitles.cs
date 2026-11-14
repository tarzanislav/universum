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

// AppServices: the subtitles from OpenSubtitles.com and svsub.net
public sealed partial class AppServices
{
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
}
