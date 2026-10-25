using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;

namespace Universum.Core.Execution;

/// <summary>
/// Writes an <see cref="EditPlan"/> into the library (specification, sections 8 and 13). An original is never lost:
/// <list type="bullet">
/// <item>Before anything is written the file is read again; if it changed since the analysis, nothing happens.</item>
/// <item>Header-only change of an original that is to be removed, on the same drive: an undo record is written first,
/// the original is moved into the library under a name media servers ignore, edited, verified and only then given its
/// final name. Any failure – also an exception – restores the header and moves the file back.</item>
/// <item>Otherwise the result is written under a temporary name in the target folder, flushed, read back and verified,
/// renamed to its final name, and only then is the original sent to the Recycle Bin (only where Windows really keeps
/// it). A failure leaves the original untouched.</item>
/// <item>A file already in its library folder is edited there with undo, or replaced: the old file is first renamed
/// aside, the new one takes its place, then the old one goes to the Recycle Bin.</item>
/// <item>In-place edits are not cancelled half-way; cancelling takes effect before or after them.</item>
/// </list>
/// The final name depends on the size of the result (size class) and on names already taken (version number).
/// </summary>
public sealed class FileProcessor(
    MkvToolNixInstallation installation, IProcessRunner runner, IRecycleBin recycleBin, string journalDirectory, LibraryIndex? index = null)
{
    /// <summary>Extension of results being written; not a video extension, so media servers ignore them. Safe to delete.</summary>
    public const string TemporaryExtension = ".universum-tmp";

    /// <summary>An original being moved into the library; never deleted automatically (it may be the only copy).</summary>
    public const string MovingExtension = ".universum-moving";

    /// <summary>A library file being replaced; never deleted automatically.</summary>
    public const string ReplacedExtension = ".universum-old";

    /// <summary>Before E2b: "name.universum-tmp.mkv".</summary>
    private const string LegacyTemporaryMarker = ".universum-tmp";

    private readonly MkvMergeInspector _inspector = new(installation, runner);

    /// <summary>A new, unique temporary name in <paramref name="directory"/>; left-overs after a crash are recognised by it.</summary>
    public static string TemporaryPath(string directory, string sourcePath) => UniquePath(directory, sourcePath, TemporaryExtension);

    public static bool IsTemporaryFile(string path) =>
        Path.GetExtension(path).Equals(TemporaryExtension, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileNameWithoutExtension(path).EndsWith(LegacyTemporaryMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>Files left by an interrupted move or replacement: possibly the only copy, to be checked by the user.</summary>
    public static bool IsInterruptedFile(string path) =>
        Path.GetExtension(path).Equals(MovingExtension, StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(ReplacedExtension, StringComparison.OrdinalIgnoreCase);

    public async Task<ProcessingResult> ProcessAsync(
        EditPlan plan, Placement placement, OutputOptions output, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = plan.Target.Source.Path;
        if (plan.Target.IsBlocked)
        {
            return Result(plan, ProcessingStatus.NeedsReview, null, null, plan.Target.Issues.Where(i => i.IsBlocking).Select(i => i.Kind.ToString()));
        }

        string? temporary = null;
        try
        {
            if (!File.Exists(source))
            {
                return Result(plan, ProcessingStatus.Failed, null, null, ["the file no longer exists; analyse the folder again"]);
            }

            // the plan is valid only for the file as it was analysed
            if (!SameStructure(await _inspector.InspectAsync(source, cancellationToken), plan.Target.Source))
            {
                return Result(plan, ProcessingStatus.Failed, null, null, ["the file changed since the analysis; analyse it again"]);
            }

            var sourceSize = new FileInfo(source).Length;
            var inLibrary = Paths.IsIn(source, placement.Directory);
            if (inLibrary && plan.Method == WriteMethod.None && placement.Holds(source, sourceSize))
            {
                return Result(plan, ProcessingStatus.Unchanged, source, null, []);
            }

            var originalTime = File.GetLastWriteTimeUtc(source);
            var fingerprint = index is null || inLibrary ? null : await LibraryIndex.FingerprintAsync(source, cancellationToken);
            if (fingerprint is not null && index!.FindSameStream(placement, fingerprint) is { } existing)
            {
                return Result(plan, ProcessingStatus.AlreadyInLibrary, existing, null, []);
            }

            ProcessingResult result;
            if (inLibrary)
            {
                temporary = TemporaryPath(placement.Directory, source);
                result = await ReworkInLibraryAsync(plan, placement, temporary, progress, cancellationToken);
            }
            else if (plan.Method != WriteMethod.Remux && output.Original == OriginalHandling.Delete && Paths.SameVolume(source, placement.Directory))
            {
                result = await MoveIntoLibraryAsync(plan, placement, cancellationToken);
            }
            else
            {
                temporary = TemporaryPath(placement.Directory, source);
                result = await WriteIntoLibraryAsync(plan, placement, output, temporary, progress, cancellationToken);
            }

            return result.Status == ProcessingStatus.Done && result.OutputPath is not null
                ? Finish(result, fingerprint, output.PreserveModifiedTime ? originalTime : null)
                : result;
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            return Result(plan, ProcessingStatus.Cancelled, null, null, []);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            TryDelete(temporary);
            return Result(plan, ProcessingStatus.Failed, null, null, [ex.Message]);
        }
    }

    /// <summary>
    /// Restores the header values (and tags, and the modification time) saved before an edit and moves a moved file back.
    /// Nothing is done when the file is not the one that was edited (different tracks) or its old place is taken.
    /// </summary>
    /// <param name="currentPath">Where the file is now, when the record may not know it yet.</param>
    /// <returns>Problems; empty when the original state was restored.</returns>
    public async Task<IReadOnlyList<string>> UndoAsync(string undoRecordPath, string? currentPath = null, CancellationToken cancellationToken = default)
    {
        UndoRecord record;
        try
        {
            record = UndoRecord.Load(undoRecordPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return ["the undo record cannot be read: " + ex.Message];
        }

        var path = currentPath ?? record.FilePath;
        if (!File.Exists(path))
        {
            return [$"the file to restore is missing: {path}"];
        }

        if (record.MovedFrom is { } movedFrom && File.Exists(movedFrom))
        {
            return [$"cannot move the file back: {movedFrom} exists"];
        }

        var current = await _inspector.InspectAsync(path, cancellationToken);
        var uids = current.Tracks.Where(t => t.Number is not null).ToDictionary(t => t.Number!.Value, t => t.Uid);
        if (record.Tracks.Any(t => t.Uid is not null && (!uids.TryGetValue(t.Number, out var uid) || uid != t.Uid)) || record.Tracks.Count != uids.Count)
        {
            return [$"{path} is not the file that was edited (its tracks differ); nothing was restored"];
        }

        var arguments = new List<string> { path, "--tags", "all:" + (record.TagsXmlPath ?? "") };
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

        // an in-place edit is never interrupted
        var result = await RunAsync(MkvToolNixTool.MkvPropEdit, arguments, null, CancellationToken.None);
        if (result.ExitCode is not (0 or 1))
        {
            return [$"mkvpropedit failed ({result.ExitCode}): {Detail(result)}"];
        }

        if (record.ModifiedUtc is { } time)
        {
            File.SetLastWriteTimeUtc(path, time);
        }

        if (record.MovedFrom is { } back)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(back)!);
            File.Move(path, back);
        }

        TryDelete(undoRecordPath);
        TryDelete(record.TagsXmlPath);
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
                cancellationToken.ThrowIfCancellationRequested();
                return await EditWithUndoAsync(plan, placement, source, movedFrom: null);

            default:
            {
                // the old library file is replaced; it goes to the Recycle Bin, never deleted permanently
                if (recycleBin.CannotRecycle(source) is { } reason)
                {
                    return Result(plan, ProcessingStatus.Failed, null, null, [$"the library file cannot be replaced: {reason}"]);
                }

                var problems = await WriteTemporaryAsync(plan, temporary, progress, cancellationToken);
                if (problems.Count > 0)
                {
                    return Result(plan, ProcessingStatus.Failed, null, null, problems);
                }

                // the old file steps aside (a name media servers ignore), the new one takes its place, then the old one is recycled
                var aside = UniquePath(placement.Directory, source, ReplacedExtension);
                File.Move(source, aside);
                string final;
                try
                {
                    final = MoveToFreeName(temporary, placement);
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    File.Move(aside, source);
                    throw;
                }

                var messages = new List<string>();
                try
                {
                    recycleBin.MoveToRecycleBin(aside);
                }
                catch (IOException ex)
                {
                    messages.Add($"the replaced file stays as {aside}: {ex.Message}");
                }

                return Result(plan, ProcessingStatus.Done, final, null, messages);
            }
        }
    }

    /// <summary>Header-only change, original to be removed, same drive: move the original into the library and edit it there.</summary>
    private async Task<ProcessingResult> MoveIntoLibraryAsync(EditPlan plan, Placement placement, CancellationToken cancellationToken)
    {
        var source = plan.Target.Source.Path;
        Directory.CreateDirectory(placement.Directory);
        var moving = UniquePath(placement.Directory, source, MovingExtension);

        // the record comes first: whatever happens after the move, it knows where the original belongs
        var undoPath = await SaveUndoRecordAsync(plan.Target.Source, moving, source, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Move(source, moving);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            DeleteRecord(undoPath);
            throw;
        }

        return await EditWithUndoAsync(plan, placement, moving, source, undoPath);
    }

    /// <summary>
    /// Edits the header of <paramref name="path"/> (unless there is nothing to edit), verifies it, gives it its final name
    /// and keeps the undo record current. Any failure restores the header and, after a move, moves the file back.
    /// </summary>
    private async Task<ProcessingResult> EditWithUndoAsync(EditPlan plan, Placement placement, string path, string? movedFrom, string? undoPath = null)
    {
        undoPath ??= await SaveUndoRecordAsync(plan.Target.Source with { Path = path }, path, movedFrom, CancellationToken.None);
        var current = path;
        var messages = new List<string>();
        try
        {
            if (plan.Method == WriteMethod.HeaderEdit)
            {
                messages.AddRange(await EditVerifiedAsync(plan, current));
            }

            FlushToDisk(current);
            var final = Rename(current, placement);
            current = final;
            Relocate(undoPath, final);
            return Result(plan, ProcessingStatus.Done, final, undoPath, messages);
        }
        catch (Exception ex) when (IsFileError(ex) || ex is EditFailedException)
        {
            var problems = await UndoAsync(undoPath, current, CancellationToken.None);
            messages.AddRange(ex is EditFailedException failed ? failed.Messages : [ex.Message]);
            messages.AddRange(problems.Count == 0 ? ["the original was restored"] : problems.Select(p => "RESTORE FAILED: " + p));
            return Result(plan, ProcessingStatus.Failed, null, problems.Count == 0 ? null : undoPath, messages);
        }
    }

    /// <summary>A new file (copy or remux) under a temporary name, verified, renamed; then the original to the Recycle Bin.</summary>
    private async Task<ProcessingResult> WriteIntoLibraryAsync(
        EditPlan plan, Placement placement, OutputOptions output, string temporary, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = plan.Target.Source.Path;
        if (output.Original == OriginalHandling.Delete && recycleBin.CannotRecycle(source) is { } reason)
        {
            // the original would be deleted permanently; the specification allows only the Recycle Bin
            return Result(plan, ProcessingStatus.Failed, null, null, [$"the original cannot go to the Recycle Bin ({reason}); choose to keep it"]);
        }

        Directory.CreateDirectory(placement.Directory);
        var problems = await WriteTemporaryAsync(plan, temporary, progress, cancellationToken);
        if (problems.Count > 0)
        {
            return Result(plan, ProcessingStatus.Failed, null, null, problems);
        }

        // the result is in place before the original is touched
        var final = MoveToFreeName(temporary, placement);
        var messages = new List<string>();
        if (output.Original == OriginalHandling.Delete)
        {
            try
            {
                recycleBin.MoveToRecycleBin(source);
            }
            catch (IOException ex)
            {
                messages.Add("the original stays in place: " + ex.Message);
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

        FlushToDisk(temporary);
        var problems = TargetVerifier.Compare(plan.Target, await _inspector.InspectAsync(temporary, cancellationToken));
        if (problems.Count > 0)
        {
            TryDelete(temporary);
            return [.. messages, .. problems.Select(p => "verification: " + p)];
        }

        return [];
    }

    /// <summary>Edits a header in place (never interrupted) and verifies it; throws <see cref="EditFailedException"/>.</summary>
    private async Task<IReadOnlyList<string>> EditVerifiedAsync(EditPlan plan, string path)
    {
        var result = await RunAsync(MkvToolNixTool.MkvPropEdit, MkvToolNixArguments.HeaderEdit(plan.Target, path), null, CancellationToken.None);
        var messages = ToolMessages(result).ToList();
        if (result.ExitCode is not (0 or 1))
        {
            throw new EditFailedException([.. messages, $"mkvpropedit failed ({result.ExitCode}): {Detail(result)}"]);
        }

        var problems = TargetVerifier.Compare(plan.Target, await _inspector.InspectAsync(path, CancellationToken.None));
        if (problems.Count > 0)
        {
            throw new EditFailedException([.. messages, .. problems.Select(p => "verification: " + p)]);
        }

        return messages;
    }

    /// <summary>Records the stream in the library index and sets the modification time; failures here only add messages.</summary>
    private ProcessingResult Finish(ProcessingResult result, string? fingerprint, DateTime? originalTime)
    {
        var messages = result.Messages.ToList();
        try
        {
            if (originalTime is { } time)
            {
                // two seconds later than the original: the library order stays, but media servers, backups and caches
                // still see that the file has changed (2 s is the coarsest file system time resolution, FAT)
                File.SetLastWriteTimeUtc(result.OutputPath!, time.AddSeconds(2));
            }

            if (fingerprint is not null)
            {
                index?.Record(result.OutputPath!, fingerprint);
            }
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            messages.Add("done, but: " + ex.Message);
        }

        return result with { Messages = messages };
    }

    /// <summary>The file has the same tracks, tags and chapters as when it was analysed.</summary>
    private static bool SameStructure(MediaFile now, MediaFile analysed) =>
        now.GlobalTagEntries == analysed.GlobalTagEntries &&
        now.ChapterEntries == analysed.ChapterEntries &&
        now.Tracks.Count == analysed.Tracks.Count &&
        now.Tracks.Zip(analysed.Tracks).All(pair =>
            pair.First.Id == pair.Second.Id &&
            pair.First.Type == pair.Second.Type &&
            pair.First.Number == pair.Second.Number &&
            pair.First.Uid == pair.Second.Uid &&
            pair.First.CodecId == pair.Second.CodecId &&
            pair.First.Name == pair.Second.Name &&
            pair.First.Language == pair.Second.Language &&
            pair.First.Flags == pair.Second.Flags &&
            pair.First.TagEntries == pair.Second.TagEntries);

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

    private static string UniquePath(string directory, string sourcePath, string extension) =>
        Path.Combine(directory, Path.GetFileNameWithoutExtension(sourcePath) + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + extension);

    /// <summary>Points an undo record to the file's new place.</summary>
    private static void Relocate(string undoPath, string newPath)
    {
        var record = UndoRecord.Load(undoPath);
        (record with { FilePath = newPath }).Save(undoPath);
    }

    private async Task<string> SaveUndoRecordAsync(MediaFile file, string filePath, string? movedFrom, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(journalDirectory);
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
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
            FilePath = filePath,
            MovedFrom = movedFrom,
            TagsXmlPath = tagsPath,
            ModifiedUtc = File.GetLastWriteTimeUtc(file.Path),
            Tracks = file.Tracks.Where(t => t.Number is not null)
                .Select(t => new UndoTrack(t.Number!.Value, t.Name, t.Language.Legacy, t.Language.Ietf, t.Flags.Default, t.Flags.Enabled, t.Flags.Forced, t.Uid))
                .ToList(),
        };
        var path = Path.Combine(journalDirectory, id + ".undo.json");
        record.Save(path);
        return path;
    }

    private static void DeleteRecord(string undoPath)
    {
        try
        {
            TryDelete(UndoRecord.Load(undoPath).TagsXmlPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            // only clean-up
        }

        TryDelete(undoPath);
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
        long length;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            length = input.Length;
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                progress?.Report(length == 0 ? 1 : (double)done / length);
            }

            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        if (new FileInfo(target).Length != length)
        {
            throw new IOException($"the copy of {source} is incomplete");
        }
    }

    /// <summary>Makes sure the file's data is on the disk before the original is touched.</summary>
    private static void FlushToDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.Flush(flushToDisk: true);
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

    private static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or MkvToolNixException or ProcessStartException or TimeoutException
            or JsonException or InvalidDataException;

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

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; temporary files are recognised by their name later
        }
    }

    /// <summary>An in-place edit failed (tool error or verification); the messages explain why.</summary>
    private sealed class EditFailedException(IReadOnlyList<string> messages) : Exception(string.Join("; ", messages))
    {
        public IReadOnlyList<string> Messages { get; } = messages;
    }
}
