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

    /// <summary>Minutes from the start of the film FFmpeg reads for <see cref="SampleAsync"/>.</summary>
    public const int SampleMinutes = 25;

    /// <summary>Fewer words than this from the start of the film: the whole track is read (forced subtitles).</summary>
    public const int SampleWords = 60;

    /// <summary>
    /// The text of the text subtitle tracks of a file (owner, 30. 10. 2026: the language of every subtitle track): the
    /// start of the film read by FFmpeg (one pass for all tracks, only the first minutes of the file), and the whole track
    /// read by mkvextract for the tracks whose start holds too little text (forced subtitles) – or for all of them without
    /// FFmpeg. Image subtitles (PGS, VobSub) cannot be read. Keys are track IDs; a track that could not be read is missing.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, string>> SampleAsync(
        MkvToolNixInstallation installation, IProcessRunner runner, string? ffmpeg, MediaFile file, IReadOnlyList<MediaTrack> tracks,
        CancellationToken cancellationToken = default)
    {
        var texts = new Dictionary<int, string>();
        var subtitles = file.TracksOf(TrackType.Subtitles).ToList();
        var wanted = tracks.Where(t => t.Type == TrackType.Subtitles && t.IsTextSubtitles && subtitles.Any(s => s.Id == t.Id)).ToList();
        if (wanted.Count == 0)
        {
            return texts;
        }

        var folder = Path.Combine(Path.GetTempPath(), "UNIVERSUM", "subtitles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            if (ffmpeg is not null && File.Exists(ffmpeg))
            {
                var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", file.Path };
                foreach (var track in wanted)
                {
                    var ordinal = subtitles.FindIndex(s => s.Id == track.Id).ToString(CultureInfo.InvariantCulture);
                    arguments.AddRange(["-map", "0:s:" + ordinal, "-t", (SampleMinutes * 60).ToString(CultureInfo.InvariantCulture),
                        "-c:s", "srt", "-f", "srt", Path.Combine(folder, track.Id.ToString(CultureInfo.InvariantCulture) + ".srt")]);
                }

                await runner.RunAsync(new ProcessRequest(ffmpeg, arguments) { Timeout = TimeSpan.FromMinutes(10) }, cancellationToken).ConfigureAwait(false);
                foreach (var track in wanted)
                {
                    var sample = Path.Combine(folder, track.Id.ToString(CultureInfo.InvariantCulture) + ".srt");
                    if (File.Exists(sample) && Clean(await File.ReadAllTextAsync(sample, cancellationToken).ConfigureAwait(false)) is { Length: > 0 } text)
                    {
                        texts[track.Id] = text;
                    }
                }
            }

            // the whole track where the start of the film had too little (only Matroska; mkvextract reads the whole file once)
            var whole = wanted.Where(t => !texts.TryGetValue(t.Id, out var text) || WordCount(text) < SampleWords).ToList();
            if (whole.Count > 0 && file.IsMatroska)
            {
                var arguments = new List<string> { file.Path, "tracks" };
                arguments.AddRange(whole.Select(t => t.Id.ToString(CultureInfo.InvariantCulture) + ":" + Path.Combine(folder, t.Id.ToString(CultureInfo.InvariantCulture) + ".full")));
                using var options = MkvToolNixOptionsFile.Create(arguments);
                var result = await runner.RunAsync(
                    new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvExtract), [options.Argument]) { Timeout = TimeSpan.FromMinutes(30) },
                    cancellationToken).ConfigureAwait(false);
                if (result.ExitCode is 0 or 1)
                {
                    foreach (var track in whole)
                    {
                        var full = Path.Combine(folder, track.Id.ToString(CultureInfo.InvariantCulture) + ".full");
                        if (File.Exists(full) && Clean(await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false)) is { Length: > 0 } text)
                        {
                            texts[track.Id] = text;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProcessStartException or TimeoutException)
        {
            // what was read is kept; the rest stays as the file has it
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a temporary folder left behind is harmless
            }
        }

        return texts;
    }

    /// <summary>The language of every text subtitle track of a file, read from its text (track ID → result).</summary>
    public static async Task<IReadOnlyDictionary<int, Text.SubtitleLanguageResult>> DetectAsync(
        MkvToolNixInstallation installation, IProcessRunner runner, string? ffmpeg, MediaFile file, CancellationToken cancellationToken = default)
    {
        var texts = await SampleAsync(installation, runner, ffmpeg, file, file.TracksOf(TrackType.Subtitles).ToList(), cancellationToken).ConfigureAwait(false);
        var found = new Dictionary<int, Text.SubtitleLanguageResult>();
        foreach (var (id, text) in texts)
        {
            if (Text.SubtitleLanguage.Detect(text) is { } result)
            {
                found[id] = result;
            }
        }

        return found;
    }

    /// <summary>The evidence of a language read from the text, applied as UNIVERSUM's suggestion.</summary>
    public static Batch.LanguageEvidence Evidence(Text.SubtitleLanguageResult result) =>
        new(LanguageTag.FromCode(result.Language), Batch.Confidence.High, Batch.EvidenceSource.SubtitleAnalysis)
        {
            Detail = (result.Certain ? "isté" : "najpravdepodobnejšie") + " – " + result.Detail,
        };

    private static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

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
