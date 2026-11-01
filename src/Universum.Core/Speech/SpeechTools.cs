using System.Text.Json;
using Universum.Core.Security;
using Universum.Core.Text;

namespace Universum.Core.Speech;

/// <summary>Problem of the recognition of the spoken language; the message is ready to show (Slovak).</summary>
public sealed class SpeechException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The tools of the recognition of the spoken language, installed with UNIVERSUM in its "speech" folder (decided by
/// the owner on 28. 10. 2026: part of the installation, nothing is downloaded): FFmpeg (LGPL, the whole program – it
/// reads the audio and will serve more later), whisper.cpp (MIT) and the speech model "large-v3-turbo" quantised q5_0 (MIT; owner, 1. 11. 2026: "base" mixed up
/// Czech and Slovak). The build writes
/// a manifest with the SHA-256 of every file; nothing is started before the files match it.
/// </summary>
/// <param name="Ffmpeg">ffmpeg.exe</param>
/// <param name="Whisper">whisper-cli.exe</param>
/// <param name="Model">ggml-large-v3-turbo-q5_0.bin</param>
public sealed record SpeechTools(string Folder, string Ffmpeg, string Whisper, string Model)
{
    public const string ManifestName = "manifest.json";

    /// <summary>The speech model: Whisper large-v3-turbo, quantised q5_0 (574 MB).</summary>
    public const string ModelName = "ggml-large-v3-turbo-q5_0.bin";

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>The folder the build puts them in, next to the program.</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "speech");

    /// <summary>The tools in a folder; null when a tool or the manifest is missing.</summary>
    public static SpeechTools? In(string folder)
    {
        var tools = new SpeechTools(
            folder,
            Path.Combine(folder, "ffmpeg", Exe("ffmpeg")),
            Path.Combine(folder, "whisper", Exe("whisper-cli")),
            Path.Combine(folder, "models", ModelName));
        return File.Exists(tools.Ffmpeg) && File.Exists(tools.Whisper) && File.Exists(tools.Model) && File.Exists(Path.Combine(folder, ManifestName))
            ? tools
            : null;
    }

    /// <summary>
    /// Checks every file of the manifest (SHA-256); throws <see cref="SpeechException"/> when one is missing or
    /// changed – then nothing of it is started.
    /// </summary>
    public async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechException(CoreText.T("Súbory rozpoznávania reči sa nedajú prečítať: ") + ex.Message, ex);
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string>? files;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Folder, ManifestName), cancellationToken).ConfigureAwait(false));
            files = document.RootElement.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new SpeechException(CoreText.T("Zoznam súborov rozpoznávania reči sa nedá prečítať – preinštalujte UNIVERSUM."), ex);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder));
        foreach (var required in new[] { Ffmpeg, Whisper, Model })
        {
            if (!files.ContainsKey(Path.GetRelativePath(root, required).Replace('\\', '/')))
            {
                throw new SpeechException(CoreText.F("Súbor {0} nie je v zozname súborov rozpoznávania reči – preinštalujte UNIVERSUM.", Path.GetFileName(required)));
            }
        }

        foreach (var (relative, expected) in files)
        {
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new SpeechException(CoreText.T("Zoznam súborov rozpoznávania reči je neplatný – preinštalujte UNIVERSUM."));
            }

            if (!File.Exists(path) || !FileHashes.Matches(expected, await FileHashes.Sha256Async(path, cancellationToken).ConfigureAwait(false)))
            {
                throw new SpeechException(CoreText.F("Súbor {0} rozpoznávania reči chýba alebo je zmenený – nespustil sa. Preinštalujte UNIVERSUM.", relative));
            }
        }

        // nothing else may lie there: whisper loads libraries from its folder (a stray ggml-*.dll would run unchecked)
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative != ManifestName && !files.ContainsKey(relative))
            {
                throw new SpeechException(CoreText.F("V priečinku rozpoznávania reči je neznámy súbor {0} – nespustilo sa nič. Preinštalujte UNIVERSUM.", relative));
            }
        }
    }

    /// <summary>Writes the manifest of all files in the folder (the build does this once the tools are in place).</summary>
    public static async Task WriteManifestAsync(string folder, CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative != ManifestName)
            {
                files[relative] = await FileHashes.Sha256Async(file, cancellationToken).ConfigureAwait(false);
            }
        }

        await File.WriteAllTextAsync(Path.Combine(root, ManifestName),
            JsonSerializer.Serialize(new { files }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
    }
}
