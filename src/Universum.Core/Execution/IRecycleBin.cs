namespace Universum.Core.Execution;

/// <summary>Moves files to the Recycle Bin (Windows) so a removed original can still be restored.</summary>
public interface IRecycleBin
{
    /// <summary>
    /// Why the file cannot be moved to the Recycle Bin – deleting it would be permanent (a network share, a USB drive,
    /// a switched-off or too small Recycle Bin); null when it can.
    /// </summary>
    string? CannotRecycle(string path);

    /// <exception cref="IOException">The file could not be moved to the Recycle Bin; it is still in place.</exception>
    void MoveToRecycleBin(string path);
}

public static class RecycleBinExtensions
{
    public static bool CanRecycle(this IRecycleBin bin, string path) => bin.CannotRecycle(path) is null;
}
