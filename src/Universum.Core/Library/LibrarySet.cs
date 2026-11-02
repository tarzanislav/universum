using Universum.Core.IO;

namespace Universum.Core.Library;

/// <summary>
/// The output folders of UNIVERSUM (owner, 2. 11. 2026 – práca na rôznych diskoch): the default one and further ones on
/// other disks or disk arrays, each a library of its own (MOVIES, TVSHOWS, POSTER). A file goes into the output folder on
/// the disk it lies on – writing on one disk spares copying between disks and lets disks work at the same time; a file on
/// a disk without an output folder (or every file, when <see cref="BySourceDisk"/> is off) goes into the default one.
/// </summary>
public sealed record LibrarySet
{
    public LibrarySet(LibraryLayout defaultLibrary, IReadOnlyList<LibraryLayout>? others = null, bool bySourceDisk = true)
    {
        Default = defaultLibrary;
        Others = (others ?? []).Where(o => !Paths.Same(o.Root, defaultLibrary.Root)).DistinctBy(o => Path.GetFullPath(o.Root), Paths.Comparer).ToList();
        BySourceDisk = bySourceDisk;
    }

    /// <summary>The default output folder (Nastavenia: Výstupný priečinok).</summary>
    public LibraryLayout Default { get; }

    /// <summary>Output folders on other disks.</summary>
    public IReadOnlyList<LibraryLayout> Others { get; }

    /// <summary>A file goes into the output folder on its own disk (else every file into the default one).</summary>
    public bool BySourceDisk { get; }

    /// <summary>Every output folder, the default one first.</summary>
    public IReadOnlyList<LibraryLayout> All => [Default, .. Others];

    /// <summary>
    /// The library a file goes into: the output folder on the file's disk (the default one when it is there too, else the
    /// first other one there); the default one for a file on a disk without one, or when <see cref="BySourceDisk"/> is off.
    /// </summary>
    public LibraryLayout For(string sourcePath)
    {
        if (!BySourceDisk || Others.Count == 0)
        {
            return Default;
        }

        string volume;
        try
        {
            volume = Paths.Volume(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return Default;
        }

        return All.FirstOrDefault(l => SameVolume(l.Root, volume)) ?? Default;
    }

    /// <summary>The library whose output folder holds <paramref name="path"/>; null when none does.</summary>
    public LibraryLayout? Holding(string path) => All.FirstOrDefault(l => l.Contains(path));

    /// <summary>True when <paramref name="path"/> lies in MOVIES or TVSHOWS of any output folder (where results are written).</summary>
    public bool HoldsResult(string path) => All.Any(l => l.HoldsResult(path));

    /// <summary>True when <paramref name="path"/> lies inside any output folder.</summary>
    public bool Contains(string path) => All.Any(l => l.Contains(path));

    /// <summary>MOVIES and TVSHOWS of every output folder.</summary>
    public IReadOnlyList<string> ResultFolders => [.. All.SelectMany(l => l.ResultFolders)];

    /// <summary>Unfinished results of every output folder.</summary>
    public IReadOnlyList<string> FindLeftovers() => [.. All.SelectMany(l => l.FindLeftovers())];

    /// <summary>Originals or replaced files left half-way in any output folder (never removed automatically).</summary>
    public IReadOnlyList<string> FindInterrupted() => [.. All.SelectMany(l => l.FindInterrupted())];

    private static bool SameVolume(string root, string volume)
    {
        try
        {
            return string.Equals(Paths.Volume(root), volume, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
