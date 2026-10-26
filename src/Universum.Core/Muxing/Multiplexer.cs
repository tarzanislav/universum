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

        var output = Path.GetFullPath(spec.OutputPath);
        var temporary = Path.Combine(Path.GetDirectoryName(output)!,
            Path.GetFileNameWithoutExtension(output) + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + FileProcessor.TemporaryExtension);
        try
        {
            EnsureFreeSpace(spec, output);
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

            File.Move(temporary, output, overwrite: false);
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

            var needed = spec.Sources.Sum(s => new FileInfo(s.File.Path).Length) + (64L << 20);
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
