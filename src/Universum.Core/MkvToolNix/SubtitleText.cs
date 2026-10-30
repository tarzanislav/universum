using System.Globalization;
using System.Text.RegularExpressions;
using Universum.Core.Media;
using Universum.Core.Processes;

namespace Universum.Core.MkvToolNix;

/// <summary>One part of the film FFmpeg reads for the language of the subtitles.</summary>
/// <param name="Start">Where the part starts in the film.</param>
/// <param name="Reads">The tracks read in it.</param>
public sealed record SubtitlePart(TimeSpan Start, IReadOnlyList<SubtitleRead> Reads);

/// <summary>A track read in a part: the number of its subtitles there by the index of the file, or null (to the end of the part).</summary>
public sealed record SubtitleRead(MediaTrack Track, int? Lines);

/// <summary>
/// The plain text of text subtitle tracks (SRT, ASS/SSA, WebVTT): a whole track by mkvextract (it reads the whole
/// file – only on request), and a few parts of the film by FFmpeg for the language of every track.
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

    /// <summary>
    /// A whole text subtitle track of a Matroska file with its times (Titulky: conversion to SRT, repairs of the text):
    /// mkvextract writes it in its own format (SRT, ASS/SSA, WebVTT), UNIVERSUM reads it. mkvextract reads the whole
    /// file. Null when the track cannot be read.
    /// </summary>
    public static async Task<Subtitles.SubtitleDocument?> ExtractDocumentAsync(
        MkvToolNixInstallation installation, IProcessRunner runner, MediaFile file, MediaTrack track, CancellationToken cancellationToken = default)
    {
        if (track.Type != TrackType.Subtitles || !track.IsTextSubtitles || !file.IsMatroska)
        {
            return null;
        }

        var extension = track.CodecId?.ToUpperInvariant() switch
        {
            "S_TEXT/ASS" or "S_ASS" => ".ass",
            "S_TEXT/SSA" or "S_SSA" => ".ssa",
            "S_TEXT/WEBVTT" or "D_WEBVTT/SUBTITLES" => ".vtt",
            _ => ".srt",
        };
        var target = Path.Combine(Path.GetTempPath(), "UNIVERSUM", $"track-{Guid.NewGuid():N}{extension}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using var options = MkvToolNixOptionsFile.Create([file.Path, "tracks", track.Id.ToString(CultureInfo.InvariantCulture) + ":" + target]);
            var result = await runner.RunAsync(
                new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvExtract), [options.Argument]) { Timeout = TimeSpan.FromMinutes(30) },
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode is not (0 or 1) || !File.Exists(target))
            {
                return null;
            }

            try
            {
                return await Subtitles.SubtitleReader.ReadFileAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>Where the parts of the film start (like the pieces of the audio): 20, 35, 50, 65 and 80 % of the film.</summary>
    public static readonly IReadOnlyList<double> PartPositions = [0.2, 0.35, 0.5, 0.65, 0.8];

    /// <summary>How long a part of the film is.</summary>
    public static readonly TimeSpan PartLength = TimeSpan.FromSeconds(90);

    /// <summary>At most this many subtitles of a track are read in one part.</summary>
    public const int LinesPerPart = 25;

    /// <summary>A track with fewer subtitles in the parts (forced) is read also where the index says its subtitles are.</summary>
    public const int EnoughLines = 10;

    /// <summary>At most this many parts more for such a track.</summary>
    public const int ExtraParts = 3;

    // FFmpeg seeks to a key frame before the part: it starts a little earlier, so no subtitle at the start is missed
    private static readonly TimeSpan Preroll = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The parts of the film FFmpeg reads (owner, 30. 10. 2026: not all the subtitles – a few parts, read and analysed,
    /// like the language of the audio): five parts of 90 seconds at 20–80 % of the film, and for a track with few
    /// subtitles there (forced) up to three parts more where the index of the file (<see cref="MatroskaCues"/>) says its
    /// subtitles are. A track the index knows is read exactly up to its last subtitle in the part (FFmpeg then stops,
    /// nothing more is read); a full track the index does not know, to the end of the part; a track with few subtitles the
    /// index does not know is not read (FFmpeg would have to read to the end of the file to find there is nothing more).
    /// </summary>
    /// <param name="cues">The cue times by the Matroska track number (<see cref="MatroskaCues"/>).</param>
    public static IReadOnlyList<SubtitlePart> Plan(MediaFile file, IReadOnlyList<MediaTrack> tracks, IReadOnlyDictionary<int, IReadOnlyList<TimeSpan>> cues)
    {
        IReadOnlyList<TimeSpan> CuesOf(MediaTrack track) =>
            track.Number is { } number && cues.TryGetValue(number, out var times) ? times : [];

        var length = file.Duration ?? tracks.SelectMany(CuesOf).DefaultIfEmpty().Max();
        var starts = length <= PartLength * PartPositions.Count
            ? [TimeSpan.Zero]
            : PartPositions.Select(p => TimeSpan.FromTicks((long)(length.Ticks * p))).ToList();

        var parts = new List<SubtitlePart>();
        var planned = tracks.ToDictionary(t => t.Id, _ => 0);
        foreach (var start in starts)
        {
            var reads = new List<SubtitleRead>();
            foreach (var track in tracks)
            {
                var times = CuesOf(track);
                if (times.Count > 0)
                {
                    var lines = Math.Min(LinesPerPart, times.Count(t => t >= start && t < start + PartLength));
                    if (lines > 0)
                    {
                        reads.Add(new SubtitleRead(track, lines));
                        planned[track.Id] += lines;
                    }
                }
                else if (track.StatisticsFrames is not (> 0 and < ForcedSubtitlesFull))
                {
                    reads.Add(new SubtitleRead(track, null));
                }
            }

            if (reads.Count > 0)
            {
                parts.Add(new SubtitlePart(start, reads));
            }
        }

        // forced subtitles where the index has them: a few places spread over the track
        var extra = new SortedSet<TimeSpan>();
        foreach (var track in tracks.Where(t => planned[t.Id] < EnoughLines))
        {
            var times = CuesOf(track);
            for (var k = 0; k < ExtraParts && times.Count > planned[track.Id]; k++)
            {
                var at = times[(int)((k + 0.5) / ExtraParts * times.Count)];
                var start = at > Preroll ? at - Preroll : TimeSpan.Zero;
                if (!parts.Any(p => start >= p.Start && start < p.Start + PartLength) && !extra.Any(e => start >= e && start < e + PartLength))
                {
                    extra.Add(start);
                }
            }
        }

        foreach (var start in extra)
        {
            var reads = tracks.Where(t => planned[t.Id] < EnoughLines)
                .Select(t => new SubtitleRead(t, Math.Min(LinesPerPart, CuesOf(t).Count(c => c >= start && c < start + PartLength))))
                .Where(r => r.Lines > 0)
                .ToList();
            if (reads.Count > 0)
            {
                parts.Add(new SubtitlePart(start, reads));
            }
        }

        return parts.OrderBy(p => p.Start).ToList();
    }

    private const int ForcedSubtitlesFull = Rules.ForcedSubtitles.FullMinimum;

    /// <summary>
    /// The text of the text subtitle tracks of a file (owner, 30. 10. 2026: the language of every subtitle track), read in
    /// a few parts of the film (<see cref="Plan"/>) by FFmpeg – one short run for each part. Without FFmpeg the whole
    /// tracks are read by mkvextract (Matroska only). Image subtitles (PGS, VobSub) cannot be read, empty tracks are left
    /// out. Keys are track IDs; a track that could not be read is missing.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, string>> SampleAsync(
        MkvToolNixInstallation installation, IProcessRunner runner, string? ffmpeg, MediaFile file, IReadOnlyList<MediaTrack> tracks,
        CancellationToken cancellationToken = default)
    {
        var texts = new Dictionary<int, string>();
        var subtitles = file.TracksOf(TrackType.Subtitles).ToList();
        // an empty track (no subtitle by the statistics) has nothing to read – and it is removed anyway
        var wanted = tracks.Where(t => t.Type == TrackType.Subtitles && t.IsTextSubtitles && !Rules.ForcedSubtitles.IsEmpty(t) && subtitles.Any(s => s.Id == t.Id)).ToList();
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
                var cues = file.IsMatroska ? await MatroskaCues.ReadAsync(file.Path, cancellationToken).ConfigureAwait(false) : new Dictionary<int, IReadOnlyList<TimeSpan>>();
                var readable = wanted.Where(t => !file.IsMatroska || Converts(t)).ToList();
                var collected = readable.ToDictionary(t => t.Id, _ => new List<string>());
                var number = 0;
                foreach (var part in Plan(file, readable, cues))
                {
                    number++;
                    var arguments = new List<string>
                    {
                        "-nostdin", "-hide_banner", "-loglevel", "error",
                        "-ss", Seconds(part.Start > Preroll ? part.Start - Preroll : TimeSpan.Zero), "-i", file.Path,
                    };
                    foreach (var read in part.Reads)
                    {
                        var ordinal = subtitles.FindIndex(s => s.Id == read.Track.Id).ToString(CultureInfo.InvariantCulture);
                        arguments.AddRange(["-map", "0:s:" + ordinal]);
                        arguments.AddRange(read.Lines is { } lines
                            ? ["-frames:s", lines.ToString(CultureInfo.InvariantCulture)]
                            : ["-t", Seconds(PartLength + Preroll)]);
                        arguments.AddRange(["-flush_packets", "1", "-c:s", "srt", "-f", "srt", PartFile(folder, read.Track, number)]);
                    }

                    try
                    {
                        await runner.RunAsync(new ProcessRequest(ffmpeg, arguments) { Timeout = TimeSpan.FromMinutes(2) }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is TimeoutException or ProcessStartException)
                    {
                        // what FFmpeg wrote so far is read
                    }

                    foreach (var read in part.Reads)
                    {
                        var sample = PartFile(folder, read.Track, number);
                        if (File.Exists(sample) && Clean(await File.ReadAllTextAsync(sample, cancellationToken).ConfigureAwait(false)) is { Length: > 0 } text)
                        {
                            collected[read.Track.Id].Add(text);
                        }
                    }
                }

                foreach (var (id, pieces) in collected.Where(c => c.Value.Count > 0))
                {
                    texts[id] = string.Join("\n", pieces);
                }

                return texts;
            }

            // without FFmpeg (not installed): mkvextract reads the whole tracks in one pass (only Matroska)
            if (file.IsMatroska)
            {
                var arguments = new List<string> { file.Path, "tracks" };
                arguments.AddRange(wanted.Select(t => t.Id.ToString(CultureInfo.InvariantCulture) + ":" + Path.Combine(folder, t.Id.ToString(CultureInfo.InvariantCulture) + ".full")));
                using var options = MkvToolNixOptionsFile.Create(arguments);
                var result = await runner.RunAsync(
                    new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvExtract), [options.Argument]) { Timeout = TimeSpan.FromMinutes(30) },
                    cancellationToken).ConfigureAwait(false);
                if (result.ExitCode is 0 or 1)
                {
                    foreach (var track in wanted)
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

    private static string PartFile(string folder, MediaTrack track, int part) =>
        Path.Combine(folder, $"{track.Id.ToString(CultureInfo.InvariantCulture)}-{part.ToString(CultureInfo.InvariantCulture)}.srt");

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The language of every text subtitle track of a file, read from its text (track ID → result).</summary>
    public static async Task<IReadOnlyDictionary<int, Text.SubtitleLanguageResult>> DetectAsync(
        MkvToolNixInstallation installation, IProcessRunner runner, string? ffmpeg, MediaFile file, CancellationToken cancellationToken = default)
    {
        var texts = await SampleAsync(installation, runner, ffmpeg, file, file.TracksOf(TrackType.Subtitles).ToList(), cancellationToken).ConfigureAwait(false);
        var found = new Dictionary<int, Text.SubtitleLanguageResult>();
        foreach (var (id, text) in texts)
        {
            // a mere guess never replaces another language written in the file (SubtitleLanguageResult.Replaces)
            if (Text.SubtitleLanguage.Detect(text) is { } result && file.Tracks.FirstOrDefault(t => t.Id == id) is { } track && result.Replaces(track.Language))
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

    // the Matroska text formats FFmpeg converts to SRT (USF, Kate … are read by mkvextract)
    private static bool Converts(MediaTrack track) =>
        track.CodecId?.ToUpperInvariant() is "S_TEXT/UTF8" or "S_TEXT/ASCII" or "S_TEXT/ASS" or "S_TEXT/SSA" or "S_ASS" or "S_SSA" or "S_TEXT/WEBVTT" or "D_WEBVTT/SUBTITLES";

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
