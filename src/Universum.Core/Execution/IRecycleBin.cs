namespace Universum.Core.Execution;

/// <summary>Moves files to the Recycle Bin (Windows) so a replaced original can still be restored.</summary>
public interface IRecycleBin
{
    /// <summary>False when the file's location has no Recycle Bin (e.g. a network share), where deleting would be permanent.</summary>
    bool CanRecycle(string path);

    /// <exception cref="IOException">The file could not be moved.</exception>
    void MoveToRecycleBin(string path);
}
