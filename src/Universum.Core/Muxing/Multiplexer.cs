using System.Globalization;
using System.Security.Cryptography;
using Universum.Core.Execution;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;

namespace Universum.Core.Muxing;

public enum MuxStatus
{
    Done,
    Failed,
    Cancelled,
}

/// <param name="Messages">mkvmerge warnings and errors, verification problems (technical, English).</param>
public sealed record MuxResult(MuxStatus Status, string? OutputPath, IReadOnlyList<string> Messages);

/// <summary>
/// Writes a <see cref="MuxSpec"/> with mkvmerge. The inputs are only read. The output is written as
/// "*.universum-tmp" next to it, flushed, read back and compared with the spec, and only then gets its name; an
/// existing file is never overwritten.
/// </summary>
public sealed class Multiplexer(MkvToolNixInstallation installation, IProcessRunner runner)
{
    private readonly MkvMergeInspector _inspector = new(installation, runner);

    public async Task<MuxResult> MuxAsync(MuxSpec spec, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var problems = spec.Validate();
        if (problems.Count > 0)
        {
            return new MuxResult(MuxStatus.Failed, null, problems);
        }

        // in the library the folder is created and the name chosen after writing (size class); otherwise the given file
        var output = spec.Placement is { } placement
            ? Path.Combine(placement.Directory, placement.Prefix + placement.Extension)
            : Path.GetFullPath(spec.OutputPath);
        string temporary;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MuxResult(MuxStatus.Failed, null, [ex.Message]);
        }

        temporary = Path.Combine(Path.GetDirectoryName(output)!,
            Path.GetFileNameWithoutExtension(output) + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + FileProcessor.TemporaryExtension);
        var converted = Path.Combine(Path.GetTempPath(), "UNIVERSUM", "srt-" + Guid.NewGuid().ToString("N"));
        try
        {
            EnsureFreeSpace(spec, output);

            // owner, 1. 11. 2026: subtitles the user ticked written as SRT first; the SRT takes the track's place
            var (withSrt, srtProblem) = await ToSrtAsync(spec, converted, cancellationToken);
            if (srtProblem is not null)
            {
                return new MuxResult(MuxStatus.Failed, null, [srtProblem]);
            }

            spec = withSrt;
            ProcessResult result;
            using (var options = MkvToolNixOptionsFile.Create(MuxArguments.Build(spec, temporary)))
            {
                result = await runner.RunAsync(
                    new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvMerge), [options.Argument])
                    {
                        OnOutputLine = progress is null ? null : line => ReportProgress(line, progress),
                    },
                    cancellationToken);
            }

            var messages = ToolMessages(result).ToList();
            if (result.ExitCode is not (0 or 1))
            {
                TryDelete(temporary);
                messages.Add($"mkvmerge failed ({result.ExitCode})" + (messages.Count == 0 ? ": " + (result.StandardError + result.StandardOutput).Trim() : ""));
                return new MuxResult(MuxStatus.Failed, null, messages);
            }

            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.Flush(flushToDisk: true);
            }

            var differences = MuxVerifier.Compare(spec, await _inspector.InspectAsync(temporary, cancellationToken));
            if (differences.Count > 0)
            {
                TryDelete(temporary);
                return new MuxResult(MuxStatus.Failed, null, [.. messages, .. differences.Select(d => "verification: " + d)]);
            }

            if (spec.Placement is { } place)
            {
                output = MoveToFreeName(temporary, place);
            }
            else
            {
                File.Move(temporary, output, overwrite: false);
            }

            return new MuxResult(MuxStatus.Done, output, messages);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            return new MuxResult(MuxStatus.Cancelled, null, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MkvToolNixException or ProcessStartException or TimeoutException)
        {
            TryDelete(temporary);
            return new MuxResult(MuxStatus.Failed, null, [ex.Message]);
        }
        finally
        {
            try
            {
                if (Directory.Exists(converted))
                {
                    Directory.Delete(converted, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a temporary folder left behind is harmless
            }
        }
    }

    /// <summary>
    /// The tracks with <see cref="MuxTrackOptions.ToSrt"/> written as SRT in UTF-8 into <paramref name="folder"/>: each SRT is
    /// one more input whose only track takes the place (name, language, flags, delay) of the track it was made of; the
    /// track of the other format is not taken. Tracks of files read with appended files or parts are left as they are.
    /// </summary>
    public async Task<(MuxSpec Spec, string? Problem)> ToSrtAsync(MuxSpec spec, string folder, CancellationToken cancellationToken = default)
    {
        if (!spec.Tracks.Any(t => t.Options.ToSrt))
        {
            return (spec, null);
        }

        var sources = spec.Sources.ToList();
        var tracks = new List<MuxTrack>(spec.Tracks.Count);
        foreach (var track in spec.Tracks)
        {
            var source = spec.Sources[track.Source];
            var media = source.File.Tracks.FirstOrDefault(t => t.Id == track.TrackId);
            if (!track.Options.ToSrt || media is null || !Subtitles.SrtConversion.Offers(media) || source.Appended.Count > 0 || source.AdditionalParts.Count > 0)
            {
                tracks.Add(track.Options.ToSrt ? track with { Options = track.Options with { ToSrt = false } } : track);
                continue;
            }

            var srt = Path.Combine(folder, $"srt-{sources.Count:00}.srt");
            if (!await Subtitles.SrtConversion.WriteAsync(installation, runner, source.File, media, srt, null, cancellationToken).ConfigureAwait(false))
            {
                return (spec, $"the subtitles of track {track.TrackId} of {Path.GetFileName(source.File.Path)} cannot be read to write them as SRT");
            }

            var file = new Media.MediaFile
            {
                Path = srt,
                ContainerType = "SRT subtitles",
                Tracks =
                [
                    new Media.MediaTrack
                    {
                        Id = 0, Type = Media.TrackType.Subtitles, CodecId = "S_TEXT/UTF8", Codec = "SubRip/SRT", IsTextSubtitles = true,
                        Language = media.Language, Name = media.Name, Flags = media.Flags,
                    },
                ],
            };
            sources.Add(new MuxSource(file, KeepChapters: false, KeepAttachments: false, KeepTags: false));
            tracks.Add(track with { Source = sources.Count - 1, TrackId = 0, Options = track.Options with { ToSrt = false, SubtitleCharset = "UTF-8" } });
        }

        return (spec with { Sources = sources, Tracks = tracks }, null);
    }

    /// <summary>The first free name of the placement for the written size (… BIG, … BIG2 …); never overwrites.</summary>
    private static string MoveToFreeName(string path, Library.Placement placement)
    {
        var size = new FileInfo(path).Length;
        for (var number = 1; ; number++)
        {
            var target = placement.PathFor(size, number);
            if (File.Exists(target) || Directory.Exists(target))
            {
                continue;
            }

            try
            {
                File.Move(path, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // taken in the meantime; try the next number
            }
        }
    }

    private static void EnsureFreeSpace(MuxSpec spec, string output)
    {
        try
        {
            var root = Path.GetPathRoot(output);
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            // a preview of parts is a small piece of the film
            var needed = (spec.PreviewParts is null ? spec.Sources.SelectMany(s => s.AllPaths).Sum(p => new FileInfo(p).Length) : 0) + (64L << 20);
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < needed)
            {
                throw new IOException($"not enough free space on {root}: {free >> 20} MB free, up to {needed >> 20} MB needed");
            }
        }
        catch (ArgumentException)
        {
            // no drive information (some network paths): mkvmerge reports a full disk itself
        }
    }

    private static void ReportProgress(string line, IProgress<double> progress)
    {
        const string prefix = "#GUI#progress ";
        if (line.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(line.AsSpan(prefix.Length).TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
        {
            progress.Report(percent / 100.0);
        }
    }

    private static IEnumerable<string> ToolMessages(ProcessResult result) =>
        (result.StandardOutput + "\n" + result.StandardError).Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Warning:", StringComparison.Ordinal) || l.StartsWith("#GUI#warning", StringComparison.Ordinal)
                        || l.StartsWith("Error:", StringComparison.Ordinal) || l.StartsWith("#GUI#error", StringComparison.Ordinal))
            .Distinct();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a left-over temporary file is recognised by its extension
        }
    }
}
