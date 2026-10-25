using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;

namespace Universum.Core.Execution;

/// <summary>
/// Writes an <see cref="EditPlan"/> into the library (specification, sections 8 and 13) and guarantees that an original
/// is removed only after a verified result is in place:
/// <list type="bullet">
/// <item>a header-only change of an original that is to be removed and lies on the same drive: the original is moved into
/// the library (a rename) and its header edited there, recorded first so the edit and the move can be reverted;</item>
/// <item>otherwise a new file is written under a temporary name in the target folder, read back and compared with the
/// target, renamed to its final name, and only then is the original sent to the Recycle Bin (when it is to be removed);</item>
/// <item>a file already in its library folder is edited there (header) or replaced (new file; the old one to the Recycle Bin).</item>
/// </list>
/// The final name depends on the size of the result (size class) and on names already taken (version number).
/// </summary>
public sealed class FileProcessor(
    MkvToolNixInstallation installation, IProcessRunner runner, IRecycleBin recycleBin, string journalDirectory)
{
    /// <summary>Extension of results being written; not a video extension, so media servers ignore them.</summary>
    public const string TemporaryExtension = ".universum-tmp";

    /// <summary>Before E2b: "name.universum-tmp.mkv".</summary>
    private const string LegacyTemporaryMarker = ".universum-tmp";

    private readonly MkvMergeInspector _inspector = new(installation, runner);

    /// <summary>A new, unique temporary name in <paramref name="directory"/>; left-overs after a crash are recognised by it.</summary>
    public static string TemporaryPath(string directory, string sourcePath) =>
        Path.Combine(directory, Path.GetFileNameWithoutExtension(sourcePath) + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + TemporaryExtension);

    public static bool IsTemporaryFile(string path) =>
        Path.GetExtension(path).Equals(TemporaryExtension, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileNameWithoutExtension(path).EndsWith(LegacyTemporaryMarker, StringComparison.OrdinalIgnoreCase);

    public async Task<ProcessingResult> ProcessAsync(
        EditPlan plan, Placement placement, OutputOptions output, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = plan.Target.Source.Path;
        if (plan.Target.IsBlocked)
        {
            return Result(plan, ProcessingStatus.NeedsReview, null, null, plan.Target.Issues.Where(i => i.IsBlocking).Select(i => i.Kind.ToString()));
        }

        var sourceSize = new FileInfo(source).Length;
        var inLibrary = Paths.IsIn(source, placement.Directory);
        if (inLibrary && plan.Method == WriteMethod.None && placement.Holds(source, sourceSize))
        {
            return Result(plan, ProcessingStatus.Unchanged, source, null, []);
        }

        var originalTime = File.GetLastWriteTimeUtc(source);
        var temporary = TemporaryPath(placement.Directory, source);
        try
        {
            ProcessingResult result;
            if (inLibrary)
            {
                result = await ReworkInLibraryAsync(plan, placement, temporary, progress, cancellationToken);
            }
            else if (await FindSameStreamAsync(plan, placement, sourceSize, cancellationToken) is { } existing)
            {
                return Result(plan, ProcessingStatus.AlreadyInLibrary, existing, null, []);
            }
            else if (plan.Method != WriteMethod.Remux && output.Original == OriginalHandling.Delete && Paths.SameVolume(source, placement.Directory))
            {
                result = await MoveIntoLibraryAsync(plan, placement, cancellationToken);
            }
            else
            {
                result = await WriteIntoLibraryAsync(plan, placement, output, temporary, progress, cancellationToken);
            }

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

    /// <summary>Restores the header values (and tags) saved before an edit and moves a moved file back.</summary>
    /// <returns>Problems; empty when the original state was restored.</returns>
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

        if (record.MovedFrom is { } movedFrom)
        {
            if (File.Exists(movedFrom))
            {
                return [$"cannot move the file back: {movedFrom} exists"];
            }

            Directory.CreateDirectory(Path.GetDirectoryName(movedFrom)!);
            File.Move(record.FilePath, movedFrom);
        }

        TryDelete(undoRecordPath);
        if (record.TagsXmlPath is not null)
        {
            TryDelete(record.TagsXmlPath);
        }

        return [];
    }

    /// <summary>The source already lies in its library folder: edit it there or replace it, and give it its right name.</summary>
    private async Task<ProcessingResult> ReworkInLibraryAsync(
        EditPlan plan, Placement placement, string temporary, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = plan.Target.Source.Path;
        switch (plan.Method)
        {
            case WriteMethod.None:
                return Result(plan, ProcessingStatus.Done, Rename(source, placement), null, []);

            case WriteMethod.HeaderEdit:
            {
                var edited = await EditAsync(plan, source, null, cancellationToken);
                if (edited.Status != ProcessingStatus.Done)
                {
                    return edited;
                }

                var final = Rename(source, placement);
                Relocate(edited.UndoRecordPath, final, null);
                return edited with { OutputPath = final };
            }

            default:
            {
                // the old library file is replaced by the new one; it goes to the Recycle Bin, never deleted permanently
                if (!recycleBin.CanRecycle(source))
                {
                    return Result(plan, ProcessingStatus.Failed, null, null, [$"no Recycle Bin for {source}; the library file cannot be replaced"]);
                }

                var written = await WriteTemporaryAsync(plan, temporary, progress, cancellationToken);
                if (written.Count > 0)
                {
                    return Result(plan, ProcessingStatus.Failed, null, null, written);
                }

                recycleBin.MoveToRecycleBin(source);
                return Result(plan, ProcessingStatus.Done, MoveToFreeName(temporary, placement), null, []);
            }
        }
    }

    /// <summary>Header-only change, original to be removed, same drive: move the original into the library and edit it there.</summary>
    private async Task<ProcessingResult> MoveIntoLibraryAsync(EditPlan plan, Placement placement, CancellationToken cancellationToken)
    {
        var source = plan.Target.Source.Path;
        Directory.CreateDirectory(placement.Directory);
        var undoPath = plan.Method == WriteMethod.HeaderEdit ? await SaveUndoRecordAsync(plan.Target.Source, cancellationToken) : null;
        var moved = MoveToFreeName(source, placement);
        if (undoPath is null)
        {
            return Result(plan, ProcessingStatus.Done, moved, null, []);
        }

        try
        {
            Relocate(undoPath, moved, source);
            var edited = await EditAsync(plan, moved, undoPath, cancellationToken);
            if (edited.Status != ProcessingStatus.Done)
            {
                return edited;
            }

            // the header edit can change the size a little; the size class of the name must still fit
            var final = Rename(moved, placement);
            Relocate(undoPath, final, source);
            return edited with { OutputPath = final };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MkvToolNixException or ProcessStartException or TimeoutException or OperationCanceledException)
        {
            // put the original back where it was, with its original header
            var problems = await UndoAsync(undoPath, CancellationToken.None);
            if (ex is OperationCanceledException && problems.Count == 0)
            {
                throw;
            }

            return Result(plan, ProcessingStatus.Failed, null, null, [ex.Message, .. problems]);
        }
    }

    /// <summary>A new file (copy or remux) under a temporary name, verified, renamed; then the original to the Recycle Bin.</summary>
    private async Task<ProcessingResult> WriteIntoLibraryAsync(
        EditPlan plan, Placement placement, OutputOptions output, string temporary, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = plan.Target.Source.Path;
        if (output.Original == OriginalHandling.Delete && !recycleBin.CanRecycle(source))
        {
            // the original would be deleted permanently; the specification allows only the Recycle Bin
            return Result(plan, ProcessingStatus.Failed, null, null, [$"no Recycle Bin for {source}; choose to keep the original"]);
        }

        Directory.CreateDirectory(placement.Directory);
        var problems = await WriteTemporaryAsync(plan, temporary, progress, cancellationToken);
        if (problems.Count > 0)
        {
            return Result(plan, ProcessingStatus.Failed, null, null, problems);
        }

        // the result is in place before the original is touched: an interruption never leaves the result only as a temporary file
        var final = MoveToFreeName(temporary, placement);
        var messages = new List<string>();
        if (output.Original == OriginalHandling.Delete)
        {
            try
            {
                recycleBin.MoveToRecycleBin(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                messages.Add("the original could not be moved to the Recycle Bin: " + ex.Message);
            }
        }

        return Result(plan, ProcessingStatus.Done, final, null, messages);
    }

    /// <summary>Writes and verifies the result as <paramref name="temporary"/>; returns the problems (empty = success).</summary>
    private async Task<IReadOnlyList<string>> WriteTemporaryAsync(EditPlan plan, string temporary, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = plan.Target.Source.Path;
        EnsureFreeSpace(source, temporary);
        ProcessResult? result = null;
        if (plan.Method == WriteMethod.Remux)
        {
            result = await RunAsync(MkvToolNixTool.MkvMerge, MkvToolNixArguments.Remux(plan.Target, source, temporary), progress, cancellationToken);
        }
        else
        {
            await CopyAsync(source, temporary, progress, cancellationToken);
            if (plan.Method == WriteMethod.HeaderEdit)
            {
                result = await RunAsync(MkvToolNixTool.MkvPropEdit, MkvToolNixArguments.HeaderEdit(plan.Target, temporary), null, cancellationToken);
            }
        }

        var messages = result is null ? [] : ToolMessages(result).ToList();
        if (result is not null && result.ExitCode is not (0 or 1))
        {
            TryDelete(temporary);
            messages.Add($"{(plan.Method == WriteMethod.Remux ? "mkvmerge" : "mkvpropedit")} failed ({result.ExitCode}): {Detail(result)}");
            return messages;
        }

        var problems = TargetVerifier.Compare(plan.Target, await _inspector.InspectAsync(temporary, cancellationToken));
        if (problems.Count > 0)
        {
            TryDelete(temporary);
            return [.. messages, .. problems.Select(p => "verification: " + p)];
        }

        return [];
    }

    /// <summary>Edits the header of <paramref name="path"/> with an undo record; reverts everything when it fails.</summary>
    private async Task<ProcessingResult> EditAsync(EditPlan plan, string path, string? undoPath, CancellationToken cancellationToken)
    {
        undoPath ??= await SaveUndoRecordAsync(plan.Target.Source with { Path = path }, cancellationToken);
        var result = await RunAsync(MkvToolNixTool.MkvPropEdit, MkvToolNixArguments.HeaderEdit(plan.Target, path), null, cancellationToken);
        var messages = ToolMessages(result).ToList();
        if (result.ExitCode is not (0 or 1))
        {
            messages.Add($"mkvpropedit failed ({result.ExitCode}): {Detail(result)}");
            messages.AddRange(await UndoAsync(undoPath, CancellationToken.None));
            return Result(plan, ProcessingStatus.Failed, null, null, messages);
        }

        var problems = TargetVerifier.Compare(plan.Target, await _inspector.InspectAsync(path, cancellationToken));
        if (problems.Count > 0)
        {
            messages.AddRange(problems.Select(p => "verification: " + p));
            messages.AddRange(await UndoAsync(undoPath, CancellationToken.None));
            return Result(plan, ProcessingStatus.Failed, null, null, messages);
        }

        return Result(plan, ProcessingStatus.Done, path, undoPath, messages);
    }

    /// <summary>
    /// A file in the library with the same stream (K8): same duration and tracks (type, codec, language, flags) and
    /// nearly the same size as the expected result. Track names are not compared (the video name is random).
    /// </summary>
    private async Task<string?> FindSameStreamAsync(EditPlan plan, Placement placement, long sourceSize, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(placement.Directory))
        {
            return null;
        }

        var expected = plan.Method == WriteMethod.Remux ? EstimateSize(plan.Target, sourceSize) : sourceSize;
        var tolerance = Math.Max(expected / 50, 1L << 20);
        foreach (var candidate in Directory.EnumerateFiles(placement.Directory, "*" + placement.Extension))
        {
            if (!placement.IsVersionName(Path.GetFileName(candidate)) || Math.Abs(new FileInfo(candidate).Length - expected) > tolerance)
            {
                continue;
            }

            var existing = await _inspector.InspectAsync(candidate, cancellationToken);
            if (IsSameStream(existing, plan.Target))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsSameStream(MediaFile existing, TargetState target)
    {
        if (existing.Duration is not { } duration || target.Source.Duration is not { } expected ||
            Math.Abs((duration - expected).TotalSeconds) > 0.5 || existing.Tracks.Count != target.Tracks.Count)
        {
            return false;
        }

        return existing.Tracks.Zip(target.Tracks).All(pair =>
            pair.First.Type == pair.Second.Type &&
            pair.First.CodecId == pair.Second.Source.CodecId &&
            string.Equals(pair.First.Language.Primary, pair.Second.Language.Primary, StringComparison.OrdinalIgnoreCase) &&
            pair.First.Flags.Default == pair.Second.Flags.Default &&
            pair.First.Flags.Enabled == pair.Second.Flags.Enabled &&
            pair.First.Flags.Forced == pair.Second.Flags.Forced);
    }

    /// <summary>Size of a remuxed result: the original minus the tracks left out (from their statistics, when known).</summary>
    private static long EstimateSize(TargetState target, long sourceSize)
    {
        var seconds = target.Source.Duration?.TotalSeconds ?? 0;
        var removed = target.Removed.Sum(t => (long)((t.StatisticsBitsPerSecond ?? 0) / 8.0 * seconds));
        return Math.Max(0, sourceSize - removed);
    }

    /// <summary>Gives <paramref name="path"/> (already in the placement's folder) its right name, when it does not have it.</summary>
    private static string Rename(string path, Placement placement) =>
        placement.Holds(path, new FileInfo(path).Length) ? path : MoveToFreeName(path, placement);

    /// <summary>Moves a file to the first free name of the placement (… BIG, … BIG2, … BIG3); never overwrites.</summary>
    private static string MoveToFreeName(string path, Placement placement)
    {
        var size = new FileInfo(path).Length;
        for (var number = 1; ; number++)
        {
            var target = placement.PathFor(size, number);
            if (File.Exists(target))
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

    /// <summary>Updates an undo record after its file has been moved.</summary>
    private static void Relocate(string? undoPath, string newPath, string? movedFrom)
    {
        if (undoPath is null)
        {
            return;
        }

        var record = UndoRecord.Load(undoPath);
        (record with { FilePath = newPath, MovedFrom = movedFrom ?? record.MovedFrom }).Save(undoPath);
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
            var root = Paths.Volume(target);
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
