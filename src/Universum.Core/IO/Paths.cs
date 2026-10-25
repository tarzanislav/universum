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

    /// <summary>Drive (or share) of a path: "D:\", "\\server\share", "/".</summary>
    public static string Volume(string path) => Path.GetPathRoot(Path.GetFullPath(path)) ?? "";

    /// <summary>True when a file can be moved between the two paths by renaming (no copying).</summary>
    public static bool SameVolume(string a, string b) => string.Equals(Volume(a), Volume(b), StringComparison.OrdinalIgnoreCase);
}
