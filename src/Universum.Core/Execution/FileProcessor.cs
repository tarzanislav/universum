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
    /// <summary>The library index the processor records its results in (null without one).</summary>
    public LibraryIndex? Index => index;

    /// <summary>
    /// What <see cref="SameStructure"/> compares, as one text: a file with another text is not the file that was analysed
    /// (MEGA spracovanie keeps it with a batch).
    /// </summary>
    public static string StructureOf(MediaFile file) =>
        string.Join("\n", new[] { $"tags:{file.GlobalTagEntries};chapters:{file.ChapterEntries}" }.Concat(file.Tracks.Where(t => t.ExternalFile is null).Select(t =>
            $"{t.Id}|{t.Type}|{t.Number}|{t.Uid}|{t.CodecId}|{t.Name}|{t.Language.Legacy}|{t.Language.Ietf}|{t.Flags}|{t.TagEntries}")));

    /// <summary>Extension of results being written; not a video extension, so media servers ignore them. Safe to delete.</summary>
    public const string TemporaryExtension = ".universum-tmp";

    /// <summary>An original being moved into the library; never deleted automatically (it may be the only copy).</summary>
    public const string MovingExtension = ".universum-moving";

    /// <summary>A library file being replaced; never deleted automatically.</summary>
    public const string ReplacedExtension = ".universum-old";

    /// <summary>Before E2b: "name.universum-tmp.mkv".</summary>
    private const string LegacyTemporaryMarker = ".universum-tmp";

    private readonly MkvMergeInspector _inspector = new(installation, runner);

    /// <summary>
    /// Whether <see cref="OriginalHandling.Delete"/> is honoured. Off by default – "delete" then means "keep" (the owner's
    /// decision of 26 October 2026). Since 30 October 2026 only Hromadné spracovanie (and its MEGA batches) switches it
    /// on, when the user chose to delete the originals.
    /// </summary>
    public bool DeleteOriginalsAllowed { get; init; }

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
        if (output.Original == OriginalHandling.Delete && !DeleteOriginalsAllowed)
        {
            output = output with { Original = OriginalHandling.Keep };
        }

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

            // the plan is valid only for the file as it was analysed (external subtitle files are not in it)
            if (!SameStructure(await _inspector.InspectAsync(source, cancellationToken), plan.Target.Source with { Tracks = plan.Target.Source.Tracks.Where(t => t.ExternalFile is null).ToList() }))
            {
                return Result(plan, ProcessingStatus.Failed, null, null, ["the file changed since the analysis; analyse it again"]);
            }

            if (plan.Target.Tracks.FirstOrDefault(t => t.IsAdded && !File.Exists(t.Source.ExternalFile)) is { } missing)
            {
                return Result(plan, ProcessingStatus.Failed, null, null, [$"the added file {Path.GetFileName(missing.Source.ExternalFile)} no longer exists"]);
            }

            var sourceSize = new FileInfo(source).Length;
            // a file is reworked where it is only when it is a library result UNIVERSUM made: in the placement's folder,
            // named by the library and recorded in the library index as a result, unchanged since. Any other file – also an
            // original lying in the title folder, whatever its name ("Creep (2004) - 1080p BluRay.mkv") – is only read and
            // the result is a new file. Without an index nothing is reworked.
            var inLibrary = Paths.IsIn(source, placement.Directory) &&
                            (placement.FixedName is not null
                                ? placement.IsLibraryName(Path.GetFileName(source))
                                : placement.IsLibraryName(Path.GetFileName(source)) && index?.IsResult(source) == true);
            if (inLibrary && plan.Method == WriteMethod.None && placement.Holds(source, sourceSize))
            {
                return Result(plan, ProcessingStatus.Unchanged, source, null, []);
            }

            var originalTime = File.GetLastWriteTimeUtc(source);
            // the library index is for library places; a file next to the original (header editor) never replaces an earlier one
            var fingerprint = index is null || inLibrary || placement.FixedName is not null ? null : await LibraryIndex.FingerprintAsync(source, cancellationToken);
            // added files are read whole for it (a sound may be a gigabyte): away from the caller's thread
            var signature = await Task.Run(() => LibraryIndex.TargetSignature(plan.Target), cancellationToken).ConfigureAwait(false);
            string? previous = null;
            if (fingerprint is not null && index!.FindPrevious(placement, fingerprint, signature) is { } found)
            {
                if (found.Target == signature)
                {
                    return Result(plan, ProcessingStatus.AlreadyInLibrary, found.Path, null, []);
                }

                // made from this original earlier, with other decisions: replaced once the new result is in place
                previous = found.Path;
            }

            ProcessingResult result;
            if (inLibrary)
            {
                temporary = TemporaryPath(placement.Directory, source);
                result = await ReworkInLibraryAsync(plan, placement, temporary, progress, cancellationToken);
            }
            else if (plan.Method != WriteMethod.Remux && output.Original == OriginalHandling.Delete && output.MoveIntoLibrary &&
                     Paths.SameVolume(source, placement.Directory) && CanEditInPlace(source))
            {
                result = await MoveIntoLibraryAsync(plan, placement, cancellationToken);
            }
            else
            {
                temporary = TemporaryPath(placement.Directory, source);
                result = await WriteIntoLibraryAsync(plan, placement, output, temporary, progress, cancellationToken);
            }

            if (result.Status != ProcessingStatus.Done || result.OutputPath is null)
            {
                return result;
            }

            result = Finish(result, fingerprint, signature, output.PreserveModifiedTime ? originalTime : null);
            if (inLibrary)
            {
                // the reworked library result keeps its origin in the index under its (possibly new) name and size
                try
                {
                    index?.Replace(source, result.OutputPath!, signature);
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    result = result with { Messages = [.. result.Messages, "done, but the library index was not updated: " + ex.Message] };
                }
            }

            return previous is null ? result : ReplacePrevious(result, previous, placement, fingerprint!, signature);
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
    /// Each step is tried on its own, so a failed header restore never keeps the file from going back. Nothing is done
    /// when the file is not the one that was edited (different tracks; checked unless <paramref name="trusted"/>) or its
    /// old place is taken.
    /// </summary>
    /// <param name="currentPath">Where the file is now, when the record may not know it yet.</param>
    /// <param name="moveBackTo">Where the file belongs, when the record may not know it yet.</param>
    /// <param name="trusted">Called right after a failed step on the file it just handled: no identity check.</param>
    /// <returns>Problems; empty when the original state was restored (the record is then deleted).</returns>
    public async Task<IReadOnlyList<string>> UndoAsync(
        string undoRecordPath, string? currentPath = null, string? moveBackTo = null, bool trusted = false, CancellationToken cancellationToken = default)
    {
        UndoRecord record;
        try
        {
            record = UndoRecord.Load(undoRecordPath);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return ["the undo record cannot be read: " + ex.Message];
        }

        var path = currentPath ?? record.FilePath;
        var back = moveBackTo ?? record.MovedFrom;
        if (back is not null && Paths.Same(back, path))
        {
            back = null;
        }

        if (!File.Exists(path))
        {
            return [$"the file to restore is missing: {path}"];
        }

        if (back is not null && File.Exists(back))
        {
            return [$"cannot move the file back: {back} exists"];
        }

        if (!trusted && index is not null && index.Knows(path) && !index.IsResult(path))
        {
            return [$"{path} has changed since UNIVERSUM wrote it; nothing was restored"];
        }

        if (!trusted)
        {
            try
            {
                var uids = new Dictionary<int, ulong?>();
                foreach (var track in (await _inspector.InspectAsync(path, cancellationToken)).Tracks.Where(t => t.Number is not null))
                {
                    uids.TryAdd(track.Number!.Value, track.Uid);
                }

                if (record.Tracks.Count != uids.Count || record.Tracks.Any(t => t.Uid is not null && (!uids.TryGetValue(t.Number, out var uid) || uid != t.Uid)))
                {
                    return [$"{path} is not the file that was edited (its tracks differ); nothing was restored"];
                }
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                return [$"{path} cannot be checked: {ex.Message}; nothing was restored"];
            }
        }

        var problems = new List<string>();
        if (record.HeaderChanged)
        {
            try
            {
                // an in-place edit is never interrupted
                var result = await RunAsync(MkvToolNixTool.MkvPropEdit, UndoArguments(record, path), null, CancellationToken.None);
                if (result.ExitCode is not (0 or 1))
                {
                    problems.Add($"the header could not be restored – mkvpropedit failed ({result.ExitCode}): {Detail(result)}");
                }
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                problems.Add("the header could not be restored: " + ex.Message);
            }
        }

        try
        {
            if (record.ModifiedUtc is { } time)
            {
                File.SetLastWriteTimeUtc(path, time);
            }
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            problems.Add("the modification time could not be restored: " + ex.Message);
        }

        var finalPath = path;
        if (back is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(back)!);
                Paths.Rename(path, back);
                finalPath = back;
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                problems.Add($"the file could not be moved back to {back}: {ex.Message}");
            }
        }

        try
        {
            index?.Undone(path, finalPath, sameAsBefore: trusted);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            problems.Add("the library index could not be updated: " + ex.Message);
        }

        if (problems.Count == 0)
        {
            TryDelete(undoRecordPath);
            TryDelete(record.TagsXmlPath);
            TryDelete(record.ChaptersXmlPath);
        }
        else
        {
            // the record follows the file, so undo can be tried again
            try
            {
                (record with { FilePath = finalPath, MovedFrom = finalPath == back ? null : back }).Save(undoRecordPath);
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                problems.Add("the undo record could not be updated: " + ex.Message);
            }
        }

        return problems;
    }

    /// <summary>Where the original of an interrupted move belongs, from the undo journal; null when unknown.</summary>
    public static string? OriginalPlaceOf(string journalDirectory, string interruptedPath)
    {
        IEnumerable<string> records;
        try
        {
            records = Directory.Exists(journalDirectory) ? Directory.EnumerateFiles(journalDirectory, "*.undo.json").ToList() : [];
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return null;
        }

        foreach (var file in records)
        {
            try
            {
                var record = UndoRecord.Load(file);
                if (Paths.Same(record.FilePath, interruptedPath))
                {
                    return record.MovedFrom;
                }
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // one unreadable record does not hide the others
            }
        }

        return null;
    }

    private static List<string> UndoArguments(UndoRecord record, string path)
    {
        var arguments = new List<string> { path, "--tags", "all:" + (record.TagsXmlPath ?? "") };
        if (record.ChaptersSaved)
        {
            arguments.AddRange(["--chapters", record.ChaptersXmlPath ?? ""]);
        }

        if (record.TitleSaved)
        {
            arguments.AddRange(["--edit", "info"]);
            arguments.AddRange(record.Title is null ? ["--delete", "title"] : ["--set", "title=" + record.Title]);
        }

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
            if (track.OtherFlags is { } other)
            {
                arguments.AddRange(["--set", "flag-hearing-impaired=" + Bit(other.HearingImpaired), "--set", "flag-visual-impaired=" + Bit(other.VisualImpaired),
                    "--set", "flag-text-descriptions=" + Bit(other.TextDescriptions), "--set", "flag-original=" + Bit(other.Original),
                    "--set", "flag-commentary=" + Bit(other.Commentary)]);
            }
        }

        return arguments;
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
                Paths.Rename(source, aside);
                string final;
                try
                {
                    final = MoveToFreeName(temporary, placement);
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    try
                    {
                        Paths.Rename(aside, source);
                    }
                    catch (Exception back) when (IsFileError(back))
                    {
                        throw new IOException($"{RestoreFailedPrefix}the old library file stays as {aside} ({back.Message}); {ex.Message}", ex);
                    }

                    throw;
                }

                var messages = new List<string>();
                try
                {
                    recycleBin.MoveToRecycleBin(aside);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        var undoPath = await SaveUndoRecordAsync(plan.Target.Source, moving, source, plan.Method == WriteMethod.HeaderEdit, plan.Target.ChaptersChanged, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Rename(source, moving);
        }
        catch (Exception ex) when (IsFileError(ex) || ex is OperationCanceledException)
        {
            DeleteRecord(undoPath);
            throw;
        }

        return await EditWithUndoAsync(plan, placement, moving, source, undoPath);
    }

    /// <summary>
    /// Edits the header of <paramref name="path"/> (unless there is nothing to edit), verifies it, gives it its final name
    /// and keeps the undo record current. Any failure restores the header and puts the file back where it was.
    /// </summary>
    /// <param name="movedFrom">Where the file was before it was moved into the library (move route), else null.</param>
    private async Task<ProcessingResult> EditWithUndoAsync(EditPlan plan, Placement placement, string path, string? movedFrom, string? undoPath = null)
    {
        undoPath ??= await SaveUndoRecordAsync(plan.Target.Source with { Path = path }, path, movedFrom, plan.Method == WriteMethod.HeaderEdit, plan.Target.ChaptersChanged, CancellationToken.None);
        var home = movedFrom ?? path;
        var current = path;
        var messages = new List<string>();
        try
        {
            if (plan.Method == WriteMethod.HeaderEdit)
            {
                messages.AddRange(await EditVerifiedAsync(plan, current));
                FlushToDisk(current);
            }

            current = Rename(current, placement);
            Relocate(undoPath, current, Paths.Same(current, home) ? null : home);
            return Result(plan, ProcessingStatus.Done, current, undoPath, messages);
        }
        catch (Exception ex)
        {
            var problems = await UndoAsync(undoPath, current, Paths.Same(current, home) ? null : home, trusted: true, CancellationToken.None);
            messages.AddRange(ex is EditFailedException failed ? failed.Messages : [ex.Message]);
            messages.AddRange(problems.Count == 0 ? [RestoredMessage] : problems.Select(p => RestoreFailedPrefix + p));
            return Result(plan, ProcessingStatus.Failed, null, problems.Count == 0 ? null : undoPath, messages);
        }
    }

    /// <summary>The original went to the Recycle Bin after its result was verified in the library.</summary>
    public const string OriginalRecycledMessage = "the original went to the Recycle Bin";

    /// <summary>Start of the message of an original that was to be deleted but stays.</summary>
    public const string OriginalKeptPrefix = "the original stays in place: ";

    /// <summary>Message of a failed edit after which the original state was restored.</summary>
    public const string RestoredMessage = "the original was restored";

    /// <summary>A corrected run replaced the earlier result made from the same original (K8); the original is untouched.</summary>
    public const string ReplacedPreviousPrefix = "the earlier result of this original went to the Recycle Bin: ";

    /// <summary>Start of messages saying that something could not be put back (the user has to look).</summary>
    public const string RestoreFailedPrefix = "RESTORE FAILED: ";

    /// <summary>True when a failed result left something not fully restored.</summary>
    public static bool NeedsAttention(ProcessingResult result) =>
        result.Status == ProcessingStatus.Failed &&
        (result.UndoRecordPath is not null || result.Messages.Any(m => m.Contains(RestoreFailedPrefix, StringComparison.Ordinal)));

    /// <summary>A new file (copy or remux) under a temporary name, verified, renamed; then the original to the Recycle Bin.</summary>
    private async Task<ProcessingResult> WriteIntoLibraryAsync(
        EditPlan plan, Placement placement, OutputOptions output, string temporary, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = plan.Target.Source.Path;
        var messages = new List<string>();
        if (output.Original == OriginalHandling.Delete && index?.Knows(source) == true)
        {
            // a file UNIVERSUM made for a library (also an earlier one) is never deleted as an original
            output = output with { Original = OriginalHandling.Keep };
            messages.Add($"{OriginalKeptPrefix}it is a result of a library");
        }
        else if (output.Original == OriginalHandling.Delete && recycleBin.CannotRecycle(source) is { } reason)
        {
            // the original would be deleted permanently – never; the result is written and the original stays (owner,
            // 30. 10. 2026: a drive without a Recycle Bin does not stop the batch)
            output = output with { Original = OriginalHandling.Keep };
            messages.Add($"{OriginalKeptPrefix}it cannot go to the Recycle Bin ({reason})");
        }

        Directory.CreateDirectory(placement.Directory);
        var problems = await WriteTemporaryAsync(plan, temporary, progress, cancellationToken);
        if (problems.Count > 0)
        {
            return Result(plan, ProcessingStatus.Failed, null, null, problems);
        }

        // the result is in place (written and verified) before the original is touched
        var final = MoveToFreeName(temporary, placement);
        if (output.Original == OriginalHandling.Delete)
        {
            try
            {
                recycleBin.MoveToRecycleBin(source);
                messages.Add(OriginalRecycledMessage);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                messages.Add(OriginalKeptPrefix + ex.Message);
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
            using var chapters = ChaptersFile.For(plan.Target);
            // external text subtitles (owner, 30. 10. 2026): any format and encoding, written as SRT in UTF-8 for mkvmerge
            var folder = Path.Combine(Path.GetTempPath(), "UNIVERSUM", "external-" + Guid.NewGuid().ToString("N"));
            try
            {
                var (inputs, problem) = await Subtitles.ExternalSubtitleTracks.ConvertAsync(plan.Target, folder, cancellationToken).ConfigureAwait(false);
                if (problem is not null)
                {
                    return [problem];
                }

                result = await RunAsync(MkvToolNixTool.MkvMerge, MkvToolNixArguments.Remux(plan.Target, source, temporary, chapters.Path, inputs), progress, cancellationToken);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // a temporary folder left behind is harmless
                }
            }
        }
        else
        {
            await CopyAsync(source, temporary, progress, cancellationToken);
            if (plan.Method == WriteMethod.HeaderEdit)
            {
                using var chapters = ChaptersFile.For(plan.Target);
                result = await RunAsync(MkvToolNixTool.MkvPropEdit, MkvToolNixArguments.HeaderEdit(plan.Target, temporary, chapters.Path), null, cancellationToken);
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
        ProcessResult result;
        using (var chapters = ChaptersFile.For(plan.Target))
        {
            result = await RunAsync(MkvToolNixTool.MkvPropEdit, MkvToolNixArguments.HeaderEdit(plan.Target, path, chapters.Path), null, CancellationToken.None);
        }

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
    private ProcessingResult Finish(ProcessingResult result, string? fingerprint, string signature, DateTime? originalTime)
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
                index?.Record(result.OutputPath!, fingerprint, signature);
            }
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            messages.Add("done, but: " + ex.Message);
        }

        return result with { Messages = messages };
    }

    /// <summary>
    /// The earlier result of the same original (made with other decisions) goes to the Recycle Bin now that the new one
    /// is in place; the new one then takes the lowest free version name. If it cannot be recycled, both stay.
    /// </summary>
    private ProcessingResult ReplacePrevious(ProcessingResult result, string previous, Placement placement, string fingerprint, string signature)
    {
        var messages = result.Messages.ToList();
        try
        {
            if (!File.Exists(previous))
            {
                return result;
            }

            if (recycleBin.CannotRecycle(previous) is { } reason)
            {
                messages.Add($"the earlier result of this original stays ({reason}): {previous}");
                return result with { Messages = messages };
            }

            recycleBin.MoveToRecycleBin(previous);
            index?.Forget(previous);
            var output = result.OutputPath!;
            var lower = MoveToLowerName(output, placement);
            if (lower != output)
            {
                index?.Forget(output);
                index?.Record(lower, fingerprint, signature);
                if (result.UndoRecordPath is not null)
                {
                    Relocate(result.UndoRecordPath, lower, UndoRecord.Load(result.UndoRecordPath).MovedFrom);
                }
            }

            messages.Add(ReplacedPreviousPrefix + Path.GetFileName(previous));
            return result with { OutputPath = lower, Messages = messages };
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            messages.Add("the earlier result of this original may still be there: " + ex.Message);
            return result with { Messages = messages };
        }
    }

    /// <summary>True when the original can be edited where it lies (not read-only, not opened by another program).</summary>
    private static bool CanEditInPlace(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            {
                return false;
            }

            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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

    /// <summary>Renames a version to a lower free number when one became free; returns its path.</summary>
    private static string MoveToLowerName(string path, Placement placement)
    {
        var size = new FileInfo(path).Length;
        for (var number = 1; ; number++)
        {
            var target = placement.PathFor(size, number);
            if (Paths.Same(target, path))
            {
                return path;
            }

            if (!File.Exists(target) && !Directory.Exists(target))
            {
                File.Move(path, target, overwrite: false);
                return target;
            }
        }
    }

    private static string UniquePath(string directory, string sourcePath, string extension) =>
        Path.Combine(directory, Path.GetFileNameWithoutExtension(sourcePath) + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + extension);

    /// <summary>Points an undo record to the file's new place and to where it belongs.</summary>
    private static void Relocate(string undoPath, string newPath, string? movedFrom)
    {
        var record = UndoRecord.Load(undoPath);
        (record with { FilePath = newPath, MovedFrom = movedFrom }).Save(undoPath);
    }

    private async Task<string> SaveUndoRecordAsync(
        MediaFile file, string filePath, string? movedFrom, bool headerChanged, bool chaptersChanged, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(journalDirectory);
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
        string? tagsPath = null;
        if (file.HasTags && headerChanged)
        {
            tagsPath = Path.Combine(journalDirectory, id + ".tags.xml");
            var extract = await RunAsync(MkvToolNixTool.MkvExtract, [file.Path, "tags", tagsPath], null, cancellationToken);
            if (extract.ExitCode is not (0 or 1) || !File.Exists(tagsPath))
            {
                throw new MkvToolNixException($"The tags of '{file.Path}' could not be saved for undo: {Detail(extract)}");
            }
        }

        string? chaptersPath = null;
        if (file.HasChapters && headerChanged && chaptersChanged)
        {
            chaptersPath = Path.Combine(journalDirectory, id + ".chapters.xml");
            var extract = await RunAsync(MkvToolNixTool.MkvExtract, [file.Path, "chapters", chaptersPath], null, cancellationToken);
            if (extract.ExitCode is not (0 or 1) || !File.Exists(chaptersPath))
            {
                TryDelete(tagsPath);
                throw new MkvToolNixException($"The chapters of '{file.Path}' could not be saved for undo: {Detail(extract)}");
            }
        }

        var record = new UndoRecord
        {
            FilePath = filePath,
            ChaptersSaved = headerChanged && chaptersChanged,
            ChaptersXmlPath = chaptersPath,
            MovedFrom = movedFrom,
            HeaderChanged = headerChanged,
            TagsXmlPath = tagsPath,
            ModifiedUtc = File.GetLastWriteTimeUtc(file.Path),
            TitleSaved = true,
            Title = file.Title,
            Tracks = file.Tracks.Where(t => t.Number is not null)
                .Select(t => new UndoTrack(t.Number!.Value, t.Name, t.Language.Legacy, t.Language.Ietf, t.Flags.Default, t.Flags.Enabled, t.Flags.Forced, t.Uid,
                    new UndoFlags(t.Flags.HearingImpaired, t.Flags.VisualImpaired, t.Flags.TextDescriptions, t.Flags.Original, t.Flags.Commentary)))
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
            var record = UndoRecord.Load(undoPath);
            TryDelete(record.TagsXmlPath);
            TryDelete(record.ChaptersXmlPath);
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

/// <summary>The target's new chapters as a temporary XML file for mkvpropedit / mkvmerge; deleted on dispose.</summary>
internal sealed class ChaptersFile : IDisposable
{
    private ChaptersFile(string? path) => Path = path;

    /// <summary>Null when the target keeps its chapters or removes all of them.</summary>
    public string? Path { get; }

    public static ChaptersFile For(TargetState target)
    {
        if (target.NewChapters is not { Count: > 0 } chapters)
        {
            return new ChaptersFile(null);
        }

        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UNIVERSUM");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"chapters-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, Universum.Core.Chapters.ChapterFormats.WriteXml(chapters), new System.Text.UTF8Encoding(false));
        return new ChaptersFile(path);
    }

    public void Dispose()
    {
        try
        {
            if (Path is not null)
            {
                File.Delete(Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a left-over temporary file is harmless
        }
    }
}
