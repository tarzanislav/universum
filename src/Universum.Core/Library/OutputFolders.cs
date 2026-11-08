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

    /// <summary>Why a folder cannot be an output; null when it can.</summary>
    public static string? Problem(string chosen, string? input)
    {
        if (input is { Length: > 0 } source && (Paths.IsUnder(chosen, source) || Paths.IsUnder(source, chosen)))
        {
            return CoreText.F("{0} nemôže byť výstupný priečinok – je to vstupný priečinok, leží v ňom alebo ho obsahuje ({1}). Výstup musí byť samostatný priečinok.", chosen, source);
        }

        return null;
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
