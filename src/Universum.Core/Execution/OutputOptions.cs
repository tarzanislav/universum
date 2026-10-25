namespace Universum.Core.Execution;

/// <summary>What happens with the original file (specification, sections 8 and 13).</summary>
public enum OriginalHandling
{
    /// <summary>The original stays untouched; the result is written into the library as a new file.</summary>
    Keep,

    /// <summary>
    /// The original is removed: moved into the library when only its header changes and it lies on the same drive,
    /// otherwise sent to the Recycle Bin after the result is in place.
    /// </summary>
    Delete,
}

/// <param name="PreserveModifiedTime">
/// The result gets the original's last-modified time plus two seconds, so libraries keep their order while
/// media servers and caches still notice the change.
/// </param>
public sealed record OutputOptions(OriginalHandling Original, bool PreserveModifiedTime = true);
