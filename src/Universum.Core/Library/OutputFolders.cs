using Universum.Core.IO;
using Universum.Core.Text;

namespace Universum.Core.Library;

/// <summary>
/// The checks of an output folder before it is used (owner, 8. 11. 2026: a source folder of another disk was added as an
/// output and the default output on E: was replaced without a question): an output is never the input folder, nor in it,
/// nor its parent; a folder with films of its own (outside MOVIES, TVSHOWS and POSTER) looks like a source folder.
/// </summary>
public static class OutputFolders
{
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".webm", ".wmv", ".vob", ".iso",
    };

    private static readonly HashSet<string> LibraryParts = new(StringComparer.OrdinalIgnoreCase) { "MOVIES", "TVSHOWS", "POSTER" };

    /// <summary>Why a folder cannot be an output beside these input folders; null when it can.</summary>
    public static string? Problem(string chosen, IEnumerable<string> inputs)
    {
        foreach (var source in inputs.Where(i => i.Trim().Length > 0))
        {
            if (Paths.IsUnder(chosen, source) || Paths.IsUnder(source, chosen))
            {
                return CoreText.F("{0} nemôže byť výstupný priečinok – je to vstupný priečinok, leží v ňom alebo ho obsahuje ({1}). Výstup musí byť samostatný priečinok.", chosen, source);
            }
        }

        return null;
    }

    /// <summary>Why a folder cannot be an input beside these output folders; null when it can.</summary>
    public static string? InputProblem(string chosen, IEnumerable<string> outputs)
    {
        foreach (var output in outputs.Where(o => o.Trim().Length > 0))
        {
            if (Paths.IsUnder(chosen, output) || Paths.IsUnder(output, chosen))
            {
                return CoreText.F("{0} nemôže byť vstupný priečinok – je to výstupný priečinok, leží v ňom alebo ho obsahuje ({1}). Vstup musí byť samostatný priečinok.", chosen, output);
            }
        }

        return null;
    }

    /// <summary>
    /// Where an output folder on the disk of an input folder is suggested: "VYSTUP" beside it ("D:\UNIVERSUM\ZDROJ" →
    /// "D:\UNIVERSUM\VYSTUP"), "UNIVERSUM\VYSTUP" when the input is the root of the disk; a name of its own when such a
    /// folder holds films already.
    /// </summary>
    public static string SuggestedOutput(string input)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        var parent = Path.GetDirectoryName(full);
        var suggested = parent is null ? Path.Combine(full, "UNIVERSUM", "VYSTUP") : Path.Combine(parent, "VYSTUP");
        return LooksLikeSource(suggested) || Paths.IsUnder(suggested, full) ? Path.Combine(parent ?? full, "UNIVERSUM-VYSTUP") : suggested;
    }

    /// <summary>
    /// The folders of UNIVERSUM by disk (owner, 8. 11. 2026: "v nastaveniach chaos – prerob"): on each disk its input and its
    /// output side by side, the disk of the default output first (marked), then the others by their names.
    /// </summary>
    public static IReadOnlyList<DiskFolderRow> Rows(IReadOnlyList<string> inputs, string? mainOutput, IReadOnlyList<string> otherOutputs)
    {
        var rows = new List<DiskFolderRow>();
        DiskFolderRow Row(string folder)
        {
            var volume = Paths.Volume(folder);
            var at = rows.FindIndex(r => string.Equals(r.Volume, volume, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                rows.Add(new DiskFolderRow(volume, null, null, false));
                at = rows.Count - 1;
            }

            return rows[at];
        }

        void Set(DiskFolderRow row, DiskFolderRow changed) => rows[rows.IndexOf(row)] = changed;
        if (mainOutput is { Length: > 0 } main)
        {
            var row = Row(main);
            Set(row, row with { Output = main, IsMain = true });
        }

        foreach (var output in otherOutputs.Where(o => o.Trim().Length > 0))
        {
            var row = Row(output);
            Set(row, row.Output is null ? row with { Output = output } : row with { ExtraOutputs = [.. row.ExtraOutputs, output] });
        }

        foreach (var input in inputs.Where(i => i.Trim().Length > 0))
        {
            var row = Row(input);
            Set(row, row.Input is null ? row with { Input = input } : row with { ExtraInputs = [.. row.ExtraInputs, input] });
        }

        return [.. rows.OrderByDescending(r => r.IsMain).ThenBy(r => r.Volume, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The folder has films of its own – outside MOVIES, TVSHOWS and POSTER, at most two levels deep: it looks like a
    /// source folder, not a library. Reads at most a few thousand names.
    /// </summary>
    public static bool LooksLikeSource(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }

        var seen = 0;
        bool Look(string directory, int depth)
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            foreach (var entry in entries)
            {
                if (++seen > 5000)
                {
                    return false;
                }

                if (Directory.Exists(entry))
                {
                    if (depth == 0 && LibraryParts.Contains(Path.GetFileName(entry)))
                    {
                        continue;
                    }

                    if (depth < 2 && Look(entry, depth + 1))
                    {
                        return true;
                    }
                }
                else if (Videos.Contains(Path.GetExtension(entry)))
                {
                    return true;
                }
            }

            return false;
        }

        return Look(folder, 0);
    }

    /// <summary>The output folders that share a disk with another one (only one output folder per disk is used).</summary>
    public static IReadOnlyList<string> SharingADisk(string? main, IReadOnlyList<string> others)
    {
        var all = (main is { Length: > 0 } ? new[] { main } : []).Concat(others.Where(o => o.Trim().Length > 0)).ToList();
        return [.. all.Where((folder, i) => all.Where((other, j) => j != i).Any(other => Paths.SameVolume(folder, other)))];
    }
}

/// <summary>A disk with UNIVERSUM's folders on it: its input folder, its output folder, and whether its output is the default one.</summary>
public sealed record DiskFolderRow(string Volume, string? Input, string? Output, bool IsMain)
{
    /// <summary>Input folders beyond the first on this disk (saved before one per disk was kept) – only the first is shown as its input.</summary>
    public IReadOnlyList<string> ExtraInputs { get; init; } = [];

    /// <summary>Output folders beyond the first on this disk – only the first is used.</summary>
    public IReadOnlyList<string> ExtraOutputs { get; init; } = [];
}
