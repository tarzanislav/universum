namespace Universum.Core.IO;

/// <summary>Path comparisons as the file system makes them (case-insensitive on Windows).</summary>
public static class Paths
{
    public static StringComparison Comparison { get; } = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), Comparison);

    /// <summary>True when the file lies directly in the folder.</summary>
    public static bool IsIn(string filePath, string directory) => Same(Path.GetDirectoryName(Path.GetFullPath(filePath))!, directory);

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or lies anywhere below it.</summary>
    public static bool IsUnder(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(full, root, Comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, Comparison);
    }

    /// <summary>
    /// Finds the volume of a path. The default uses the path root ("D:\"); on Windows the platform replaces it with the
    /// real volume, so folders mounted from another drive are told apart.
    /// </summary>
    public static Func<string, string> VolumeResolver { get; set; } = path => Path.GetPathRoot(Path.GetFullPath(path)) ?? "";

    /// <summary>Volume of a path: "D:\", "\\server\share", "/" or a volume identifier.</summary>
    public static string Volume(string path) => VolumeResolver(path);

    /// <summary>
    /// Renames a file without overwriting. The default is File.Move; on Windows the platform replaces it with a rename that
    /// never falls back to copy-and-delete, so moving an original can never become a copy followed by a permanent delete.
    /// </summary>
    public static Action<string, string> Rename { get; set; } = (source, target) => File.Move(source, target, overwrite: false);

    /// <summary>True when a file can be moved between the two paths by renaming (no copying).</summary>
    public static bool SameVolume(string a, string b) => string.Equals(Volume(a), Volume(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the physical disk(s) of a path (owner, 2. 11. 2026 – práca na rôznych diskoch). The default takes the volume
    /// for the disk; on Windows the platform reads the real disks (two partitions of one disk are one disk, a volume over
    /// several disks is one lane) and whether they are solid-state.
    /// </summary>
    public static Func<string, DiskIdentity> DiskResolver { get; set; } = path => new DiskIdentity("volume:" + Volume(path), SolidState: false, Detail: Volume(path));

    /// <summary>The physical disk(s) of a path.</summary>
    public static DiskIdentity Disk(string path) => DiskResolver(path);

    /// <summary>
    /// Copies a file without overwriting, the progress reported (0–1). The default copies by streams; on Windows the
    /// platform uses the system's copy engine (unbuffered for large files, block cloning on ReFS).
    /// </summary>
    public static Func<string, string, IProgress<double>?, CancellationToken, Task>? CopyFile { get; set; }
}

/// <summary>
/// The physical disk(s) behind a path: <see cref="Key"/> is the same for paths on one disk (or one array of disks);
/// a solid-state disk (SSD, NVMe – no seek penalty) takes two files at once without slowing down.
/// </summary>
public sealed record DiskIdentity(string Key, bool SolidState, string Detail);
