namespace Universum.Core.Execution;

/// <summary>What happens with the original file (specification, section 8).</summary>
public enum OriginalHandling
{
    /// <summary>The original stays untouched; the result is written as a new file.</summary>
    Keep,

    /// <summary>The result replaces the original; a replaced original goes to the Recycle Bin.</summary>
    Delete,
}

/// <param name="TargetDirectory">Folder for kept results; null = next to the original.</param>
/// <param name="KeepSuffix">Added to the file name when the result is written next to a kept original.</param>
/// <param name="PreserveModifiedTime">
/// The result gets the original's last-modified time plus two seconds, so libraries keep their order while
/// media servers and caches still notice the change.
/// </param>
public sealed record OutputOptions(
    OriginalHandling Original,
    string? TargetDirectory = null,
    string KeepSuffix = " [UNIVERSUM]",
    bool PreserveModifiedTime = true)
{
    /// <summary>Final path of the result for a given original.</summary>
    public string ResultPath(string originalPath)
    {
        if (Original == OriginalHandling.Delete && TargetDirectory is null)
        {
            return originalPath;
        }

        var name = Path.GetFileNameWithoutExtension(originalPath);
        var extension = Path.GetExtension(originalPath);
        return TargetDirectory is null
            ? Path.Combine(Path.GetDirectoryName(originalPath)!, name + KeepSuffix + extension)
            : Path.Combine(TargetDirectory, name + extension);
    }
}
