using System.Globalization;
using System.Text.RegularExpressions;
using Universum.Core.Media;
using Universum.Core.Processes;

namespace Universum.Core.MkvToolNix;

/// <summary>
/// Extracts the plain text of a text subtitle track (SRT, ASS/SSA, WebVTT) with mkvextract. Note that mkvextract
/// reads the whole file, so this is done only on request.
/// </summary>
public static partial class SubtitleText
{
    public static async Task<string?> ExtractAsync(
        MkvToolNixInstallation installation, IProcessRunner runner, MediaFile file, MediaTrack track, CancellationToken cancellationToken = default)
    {
        if (track.Type != TrackType.Subtitles || !track.IsTextSubtitles)
        {
            return null;
        }

        var target = Path.Combine(Path.GetTempPath(), "UNIVERSUM", $"subtitles-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using var options = MkvToolNixOptionsFile.Create([file.Path, "tracks", track.Id.ToString(CultureInfo.InvariantCulture) + ":" + target]);
            var result = await runner.RunAsync(
                new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvExtract), [options.Argument]) { Timeout = TimeSpan.FromMinutes(30) },
                cancellationToken).ConfigureAwait(false);
            return result.ExitCode is 0 or 1 && File.Exists(target) ? Clean(await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false)) : null;
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>Only the spoken text: no numbers, timings, ASS headers or formatting tags.</summary>
    public static string Clean(string raw)
    {
        var lines = new List<string>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(',', 10);
                line = parts.Length == 10 ? parts[9].Replace("\\N", " ", StringComparison.Ordinal).Replace("\\n", " ", StringComparison.Ordinal) : "";
            }
            else if (line.Length == 0 || line.Contains("-->", StringComparison.Ordinal) || line.All(char.IsAsciiDigit) ||
                     line.StartsWith('[') || Header().IsMatch(line) || line.StartsWith("WEBVTT", StringComparison.Ordinal))
            {
                continue;
            }

            line = Spaces().Replace(Tags().Replace(line, " "), " ").Trim();
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>ASS/SSA header lines such as "Format: Layer, Start" or "PlayResX: 1920".</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z ]{0,24}:", RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    [GeneratedRegex(@"\{[^}]*\}|<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
