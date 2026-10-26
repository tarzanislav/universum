using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;

namespace Universum.Core.Chapters;

/// <summary>What the chapter editor needs to put the chapters back (undo).</summary>
public sealed record ChapterUndoRecord(string FilePath, string? ChaptersXmlPath, DateTime ModifiedUtc, long Size);

/// <param name="UndoRecordPath">Record for putting the old chapters back; null when nothing was written.</param>
/// <param name="Messages">Tool warnings and problems (technical, English).</param>
public sealed record ChapterSaveResult(bool Saved, string? UndoRecordPath, IReadOnlyList<string> Messages);

/// <summary>
/// Reads and writes the chapters of a Matroska file in place (mkvextract, mkvpropedit). Before writing, the old
/// chapters are saved to the journal; after writing, the chapters are read back and compared – any difference puts the
/// old chapters back. The last-modified time is kept (+2 s, as with header edits).
/// </summary>
public sealed class ChapterEditor(MkvToolNixInstallation installation, IProcessRunner runner, string journalDirectory)
{
    public async Task<ChapterSet> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var xml = await ExtractAsync(path, Path.Combine(Path.GetTempPath(), "UNIVERSUM", $"chapters-{Guid.NewGuid():N}.xml"), cancellationToken);
        if (xml is null)
        {
            return ChapterSet.None;
        }

        try
        {
            return ChapterFormats.ParseXml(await File.ReadAllTextAsync(xml, cancellationToken));
        }
        finally
        {
            File.Delete(xml);
        }
    }

    /// <summary>Replaces the file's chapters (an empty list removes them).</summary>
    public async Task<ChapterSaveResult> SaveAsync(string path, IReadOnlyList<Chapter> chapters, CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(path, cancellationToken);
        if (current.Complex)
        {
            return new ChapterSaveResult(false, null, ["the file has editions, nested chapters or several names per chapter; edit them in MKVToolNix GUI"]);
        }

        Directory.CreateDirectory(journalDirectory);
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
        var oldXml = await ExtractAsync(path, Path.Combine(journalDirectory, id + ".chapters.xml"), cancellationToken);
        var info = new FileInfo(path);
        var record = new ChapterUndoRecord(path, oldXml, info.LastWriteTimeUtc, info.Length);
        var recordPath = Path.Combine(journalDirectory, id + ".chapters-undo.json");
        await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(record), cancellationToken);

        // from here on nothing is cancelled half-way
        var problems = await WriteAsync(path, chapters);
        if (problems.Count == 0)
        {
            var written = await ReadAsync(path, CancellationToken.None);
            problems.AddRange(Compare(chapters, written.Chapters));
        }

        if (problems.Count > 0)
        {
            var restore = await UndoAsync(recordPath);
            problems.AddRange(restore.Count == 0 ? ["the old chapters were put back"] : restore.Select(r => "RESTORE FAILED: " + r));
            return new ChapterSaveResult(false, restore.Count == 0 ? null : recordPath, problems);
        }

        File.SetLastWriteTimeUtc(path, record.ModifiedUtc.AddSeconds(2));
        return new ChapterSaveResult(true, recordPath, []);
    }

    /// <summary>Puts the chapters saved before an edit back; returns problems (empty = done, the record is deleted).</summary>
    public async Task<IReadOnlyList<string>> UndoAsync(string recordPath)
    {
        ChapterUndoRecord record;
        try
        {
            record = JsonSerializer.Deserialize<ChapterUndoRecord>(await File.ReadAllTextAsync(recordPath)) ?? throw new InvalidDataException("empty record");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return ["the undo record cannot be read: " + ex.Message];
        }

        if (!File.Exists(record.FilePath))
        {
            return ["the file no longer exists: " + record.FilePath];
        }

        var result = await RunAsync(MkvToolNixTool.MkvPropEdit, [record.FilePath, "--chapters", record.ChaptersXmlPath ?? ""]);
        if (result.ExitCode is not (0 or 1))
        {
            return [$"mkvpropedit failed ({result.ExitCode}): {(result.StandardOutput + result.StandardError).Trim()}"];
        }

        File.SetLastWriteTimeUtc(record.FilePath, record.ModifiedUtc);
        TryDelete(record.ChaptersXmlPath);
        TryDelete(recordPath);
        return [];
    }

    private async Task<List<string>> WriteAsync(string path, IReadOnlyList<Chapter> chapters)
    {
        string? xmlPath = null;
        try
        {
            if (chapters.Count > 0)
            {
                xmlPath = Path.Combine(Path.GetTempPath(), "UNIVERSUM", $"chapters-new-{Guid.NewGuid():N}.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
                await File.WriteAllTextAsync(xmlPath, ChapterFormats.WriteXml(chapters), new UTF8Encoding(false));
            }

            var result = await RunAsync(MkvToolNixTool.MkvPropEdit, [path, "--chapters", xmlPath ?? ""]);
            return result.ExitCode is 0 or 1 ? [] : [$"mkvpropedit failed ({result.ExitCode}): {(result.StandardOutput + result.StandardError).Trim()}"];
        }
        finally
        {
            TryDelete(xmlPath);
        }
    }

    /// <summary>Written chapters must be exactly the asked ones (sorted by start; times to the millisecond).</summary>
    private static IEnumerable<string> Compare(IReadOnlyList<Chapter> expected, IReadOnlyList<Chapter> actual)
    {
        var sorted = expected.OrderBy(c => c.Start).ToList();
        if (sorted.Count != actual.Count)
        {
            yield return $"verification: {actual.Count} chapters, expected {sorted.Count}";
            yield break;
        }

        for (var i = 0; i < sorted.Count; i++)
        {
            if (Math.Abs((sorted[i].Start - actual[i].Start).TotalMilliseconds) >= 1 || sorted[i].Name != actual[i].Name ||
                (!sorted[i].Language.IsUndetermined && sorted[i].Language.Primary != actual[i].Language.Primary))
            {
                yield return $"verification: chapter {i + 1} is '{actual[i].Name}' at {ChapterTime.Format(actual[i].Start)}, expected '{sorted[i].Name}' at {ChapterTime.Format(sorted[i].Start)}";
            }
        }
    }

    /// <summary>Extracts the chapters as XML to <paramref name="target"/>; null when the file has none.</summary>
    private async Task<string?> ExtractAsync(string path, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var result = await RunAsync(MkvToolNixTool.MkvExtract, [path, "chapters", target], cancellationToken);
        if (result.ExitCode is not (0 or 1))
        {
            throw new MkvToolNixException($"The chapters of '{path}' could not be read: {(result.StandardOutput + result.StandardError).Trim()}");
        }

        return File.Exists(target) && new FileInfo(target).Length > 0 ? target : null;
    }

    private async Task<ProcessResult> RunAsync(MkvToolNixTool tool, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        using var options = MkvToolNixOptionsFile.Create(arguments);
        return await runner.RunAsync(new ProcessRequest(installation.ToolPath(tool), [options.Argument]), cancellationToken);
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is not null)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a left-over temporary file is harmless
        }
    }
}
