using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;

namespace Universum.Core.Execution;

/// <summary>
/// Writes an <see cref="EditPlan"/> with MKVToolNix and guarantees that an original is replaced only by a verified
/// result: new files are written under a temporary name next to their destination, read back and compared with the
/// target; in-place header edits are recorded first so they can be reverted.
/// </summary>
public sealed class FileProcessor(
    MkvToolNixInstallation installation, IProcessRunner runner, IRecycleBin recycleBin, string journalDirectory)
{
    private const string TemporaryMarker = ".universum-tmp";

    private readonly MkvMergeInspector _inspector = new(installation, runner);

    /// <summary>Temporary name of a result being written; left-overs after a crash can be recognised by it.</summary>
    public static string TemporaryPath(string resultPath) =>
        Path.Combine(Path.GetDirectoryName(resultPath)!, Path.GetFileNameWithoutExtension(resultPath) + TemporaryMarker + Path.GetExtension(resultPath));

    public static bool IsTemporaryFile(string path) => Path.GetFileNameWithoutExtension(path).EndsWith(TemporaryMarker, StringComparison.OrdinalIgnoreCase);

    public async Task<ProcessingResult> ProcessAsync(
        EditPlan plan, OutputOptions output, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = plan.Target.Source.Path;
        if (plan.Target.IsBlocked)
        {
            return Result(plan, ProcessingStatus.NeedsReview, null, null, plan.Target.Issues.Where(i => i.IsBlocking).Select(i => i.Kind.ToString()));
        }

        if (plan.Method == WriteMethod.None)
        {
            return Result(plan, ProcessingStatus.Unchanged, source, null, []);
        }

        var resultPath = Path.GetFullPath(output.ResultPath(source));
        var inPlace = SamePath(resultPath, source);
        if (!inPlace && File.Exists(resultPath))
        {
            return Result(plan, ProcessingStatus.Failed, null, null, [$"destination already exists: {resultPath}"]);
        }

        var replacesOriginal = output.Original == OriginalHandling.Delete && !(inPlace && plan.Method == WriteMethod.HeaderEdit);
        if (replacesOriginal && !recycleBin.CanRecycle(source))
        {
            // the original would be deleted permanently; the specification allows only the Recycle Bin
            return Result(plan, ProcessingStatus.Failed, null, null, [$"no Recycle Bin for {source}; choose to keep the original"]);
        }

        var originalTime = File.GetLastWriteTimeUtc(source);
        var temporary = TemporaryPath(resultPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            var result = plan.Method == WriteMethod.HeaderEdit && inPlace
                ? await EditInPlaceAsync(plan, cancellationToken)
                : await WriteNewFileAsync(plan, output, resultPath, temporary, progress, cancellationToken);
            if (result.Status == ProcessingStatus.Done && output.PreserveModifiedTime && result.OutputPath is not null)
            {
                // two seconds later than the original: the library order stays, but media servers, backups and caches
                // still see that the file has changed (2 s is the coarsest file system time resolution, FAT)
                File.SetLastWriteTimeUtc(result.OutputPath, originalTime.AddSeconds(2));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            return Result(plan, ProcessingStatus.Cancelled, null, null, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MkvToolNixException or ProcessStartException or TimeoutException)
        {
            TryDelete(temporary);
            return Result(plan, ProcessingStatus.Failed, null, null, [ex.Message]);
        }
    }

    /// <summary>Restores the header values (and tags) saved before an in-place edit.</summary>
    /// <returns>Problems; empty when the original values were written back.</returns>
    public async Task<IReadOnlyList<string>> UndoAsync(string undoRecordPath, CancellationToken cancellationToken = default)
    {
        var record = UndoRecord.Load(undoRecordPath);
        var arguments = new List<string> { record.FilePath, "--tags", "all:" + (record.TagsXmlPath ?? "") };
        foreach (var track in record.Tracks)
        {
            arguments.AddRange(["--edit", "track:@" + track.Number.ToString(CultureInfo.InvariantCulture)]);
            arguments.AddRange(track.Name is null ? ["--delete", "name"] : ["--set", "name=" + track.Name]);
            arguments.AddRange(["--set", "language=" + track.Language]);
            if (track.LanguageIetf is not null)
            {
                arguments.AddRange(["--set", "language-ietf=" + track.LanguageIetf]);
            }

            arguments.AddRange(["--set", "flag-default=" + Bit(track.Default), "--set", "flag-enabled=" + Bit(track.Enabled), "--set", "flag-forced=" + Bit(track.Forced)]);
        }

        var result = await RunAsync(MkvToolNixTool.MkvPropEdit, arguments, null, cancellationToken);
        if (result.ExitCode is not (0 or 1))
        {
            return [$"mkvpropedit failed ({result.ExitCode}): {Detail(result)}"];
        }

        TryDelete(undoRecordPath);
        if (record.TagsXmlPath is not null)
        {
            TryDelete(record.TagsXmlPath);
        }

        return [];
    }

    private async Task<ProcessingResult> EditInPlaceAsync(EditPlan plan, CancellationToken cancellationToken)
    {
        var source = plan.Target.Source.Path;
        var undoPath = await SaveUndoRecordAsync(plan.Target.Source, cancellationToken);
        var result = await RunAsync(MkvToolNixTool.MkvPropEdit, MkvToolNixArguments.HeaderEdit(plan.Target, source), null, cancellationToken);
        var messages = ToolMessages(result).ToList();
        if (result.ExitCode is not (0 or 1))
        {
            messages.Add($"mkvpropedit failed ({result.ExitCode}): {Detail(result)}");
            messages.AddRange(await UndoAsync(undoPath, CancellationToken.None));
            return Result(plan, ProcessingStatus.Failed, null, null, messages);
        }

        var problems = TargetVerifier.Compare(plan.Target, await _inspector.InspectAsync(source, cancellationToken));
        if (problems.Count > 0)
        {
            messages.AddRange(problems.Select(p => "verification: " + p));
            messages.AddRange(await UndoAsync(undoPath, CancellationToken.None));
            return Result(plan, ProcessingStatus.Failed, null, null, messages);
        }

        return Result(plan, ProcessingStatus.Done, source, undoPath, messages);
    }

    private async Task<ProcessingResult> WriteNewFileAsync(
        EditPlan plan, OutputOptions output, string resultPath, string temporary, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = plan.Target.Source.Path;
        EnsureFreeSpace(source, temporary);
        TryDelete(temporary);

        ProcessResult result;
        if (plan.Method == WriteMethod.HeaderEdit)
        {
            await CopyAsync(source, temporary, progress, cancellationToken);
            result = await RunAsync(MkvToolNixTool.MkvPropEdit, MkvToolNixArguments.HeaderEdit(plan.Target, temporary), null, cancellationToken);
        }
        else
        {
            result = await RunAsync(MkvToolNixTool.MkvMerge, MkvToolNixArguments.Remux(plan.Target, source, temporary), progress, cancellationToken);
        }

        var messages = ToolMessages(result).ToList();
        if (result.ExitCode is not (0 or 1))
        {
            TryDelete(temporary);
            messages.Add($"{(plan.Method == WriteMethod.Remux ? "mkvmerge" : "mkvpropedit")} failed ({result.ExitCode}): {Detail(result)}");
            return Result(plan, ProcessingStatus.Failed, null, null, messages);
        }

        var problems = TargetVerifier.Compare(plan.Target, await _inspector.InspectAsync(temporary, cancellationToken));
        if (problems.Count > 0)
        {
            TryDelete(temporary);
            messages.AddRange(problems.Select(p => "verification: " + p));
            return Result(plan, ProcessingStatus.Failed, null, null, messages);
        }

        // the result is verified: only now is the original touched
        if (output.Original == OriginalHandling.Delete)
        {
            recycleBin.MoveToRecycleBin(source);
        }

        File.Move(temporary, resultPath);
        return Result(plan, ProcessingStatus.Done, resultPath, null, messages);
    }

    private async Task<string> SaveUndoRecordAsync(MediaFile file, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(journalDirectory);
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Path)))[..12]}";
        string? tagsPath = null;
        if (file.HasTags)
        {
            tagsPath = Path.Combine(journalDirectory, id + ".tags.xml");
            var extract = await RunAsync(MkvToolNixTool.MkvExtract, [file.Path, "tags", tagsPath], null, cancellationToken);
            if (extract.ExitCode is not (0 or 1) || !File.Exists(tagsPath))
            {
                throw new MkvToolNixException($"The tags of '{file.Path}' could not be saved for undo: {Detail(extract)}");
            }
        }

        var record = new UndoRecord
        {
            FilePath = file.Path,
            TagsXmlPath = tagsPath,
            Tracks = file.Tracks.Where(t => t.Number is not null)
                .Select(t => new UndoTrack(t.Number!.Value, t.Name, t.Language.Legacy, t.Language.Ietf, t.Flags.Default, t.Flags.Enabled, t.Flags.Forced))
                .ToList(),
        };
        var path = Path.Combine(journalDirectory, id + ".undo.json");
        record.Save(path);
        return path;
    }

    private async Task<ProcessResult> RunAsync(MkvToolNixTool tool, IEnumerable<string> arguments, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var options = MkvToolNixOptionsFile.Create(arguments);
        return await runner.RunAsync(
            new ProcessRequest(installation.ToolPath(tool), [options.Argument]) { OnOutputLine = progress is null ? null : line => ReportProgress(line, progress) },
            cancellationToken);
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

    private static async Task CopyAsync(string source, string target, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            done += read;
            progress?.Report(input.Length == 0 ? 1 : (double)done / input.Length);
        }
    }

    private static void EnsureFreeSpace(string source, string target)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(target));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var needed = new FileInfo(source).Length + (64L << 20);
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < needed)
            {
                throw new IOException($"not enough free space on {root}: {free >> 20} MB free, {needed >> 20} MB needed");
            }
        }
        catch (ArgumentException)
        {
            // drive information not available (e.g. some network paths); the tool reports a full disk itself
        }
    }

    private static IEnumerable<string> ToolMessages(ProcessResult result) =>
        (result.StandardOutput + "\n" + result.StandardError).Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Warning:", StringComparison.Ordinal) || l.StartsWith("#GUI#warning", StringComparison.Ordinal)
                        || l.StartsWith("Error:", StringComparison.Ordinal) || l.StartsWith("#GUI#error", StringComparison.Ordinal))
            .Distinct();

    private static string Detail(ProcessResult result)
    {
        var errors = ToolMessages(result).ToList();
        return errors.Count > 0 ? string.Join(" | ", errors) : (result.StandardError + result.StandardOutput).Trim();
    }

    private static ProcessingResult Result(EditPlan plan, ProcessingStatus status, string? output, string? undo, IEnumerable<string> messages) =>
        new(plan.Target.Source.Path, status, plan.Method, output, undo, messages.ToList());

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Bit(bool value) => value ? "1" : "0";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; temporary files are recognised by their name later
        }
    }
}
