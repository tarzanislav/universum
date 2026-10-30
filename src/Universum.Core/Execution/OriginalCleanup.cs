using System.Globalization;
using Universum.Core.IO;

namespace Universum.Core.Execution;

/// <summary>An original whose result is in the library, as it was when the result was made (or found).</summary>
public sealed record WrittenOriginal(string Source, long Size, DateTime ModifiedUtc, string Result);

/// <summary>
/// Zmazať originály (owner, 30. 10. 2026): the originals of results in the library go to the Recycle Bin after the run –
/// never permanently, and never a file the library needs. An original stays when its result is not in the library any
/// more, when it changed after the result was made, when it lies in the library itself, or when it cannot go to the
/// Recycle Bin.
/// </summary>
public static class OriginalCleanup
{
    /// <summary>The originals of a run whose results are in the library (written now, or the same stream found there).</summary>
    public static IReadOnlyList<WrittenOriginal> Collect(IEnumerable<ProcessingResult> results) =>
        results.Where(r => r.Status is ProcessingStatus.Done or ProcessingStatus.AlreadyInLibrary &&
                           r.OutputPath is { } output && !Paths.Same(r.SourcePath, output) && File.Exists(r.SourcePath) && File.Exists(output))
            .Select(r =>
            {
                var info = new FileInfo(r.SourcePath);
                return new WrittenOriginal(r.SourcePath, info.Length, info.LastWriteTimeUtc, r.OutputPath!);
            })
            .ToList();

    /// <summary>
    /// An original found already in the library at the analysis: its size and time come from the fingerprint the library
    /// index matched (<see cref="Library.LibraryIndex.FingerprintAsync"/> – "size:ticks:hash").
    /// </summary>
    public static WrittenOriginal? FromFingerprint(string source, string fingerprint, string result)
    {
        var parts = fingerprint.Split(':');
        return parts.Length == 3 &&
               long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size) &&
               long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            ? new WrittenOriginal(source, size, new DateTime(ticks, DateTimeKind.Utc), result)
            : null;
    }

    /// <summary>
    /// Sends the originals to the Recycle Bin, one by one. <paramref name="inLibrary"/> says whether a path lies among the
    /// library's results (MOVIES, TVSHOWS) – such a file is never removed.
    /// </summary>
    /// <returns>The originals removed, and why the others stayed (Slovak, one line each).</returns>
    public static (IReadOnlyList<string> Removed, IReadOnlyList<string> Kept) Remove(
        IEnumerable<WrittenOriginal> originals, IRecycleBin recycleBin, Func<string, bool> inLibrary, CancellationToken cancellationToken = default)
    {
        var removed = new List<string>();
        var kept = new List<string>();
        foreach (var original in originals.DistinctBy(o => Path.GetFullPath(o.Source), Paths.Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(original.Source);
            if (Why(original, recycleBin, inLibrary) is { } reason)
            {
                if (reason.Length > 0)
                {
                    kept.Add($"{name}: {reason}");
                }

                continue;
            }

            try
            {
                recycleBin.MoveToRecycleBin(original.Source);
                removed.Add(original.Source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                kept.Add($"{name}: do Koša sa nedal presunúť – {ex.Message}");
            }
        }

        return (removed, kept);
    }

    // why an original stays: "" when it is gone already, null when it may go to the Recycle Bin
    private static string? Why(WrittenOriginal original, IRecycleBin recycleBin, Func<string, bool> inLibrary)
    {
        if (!File.Exists(original.Source))
        {
            return "";
        }

        if (Paths.Same(original.Source, original.Result) || inLibrary(original.Source))
        {
            return "leží v knižnici – nemaže sa";
        }

        if (!File.Exists(original.Result) || new FileInfo(original.Result).Length == 0)
        {
            return "jeho výsledok v knižnici chýba – ostal";
        }

        var info = new FileInfo(original.Source);
        if (info.Length != original.Size || info.LastWriteTimeUtc != original.ModifiedUtc)
        {
            return "po spracovaní sa zmenil – ostal";
        }

        return recycleBin.CannotRecycle(original.Source) is { } reason ? $"Kôš nie je k dispozícii ({reason}) – ostal" : null;
    }
}
