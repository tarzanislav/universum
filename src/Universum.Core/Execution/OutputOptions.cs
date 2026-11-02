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

/// <summary>
/// What Hromadné spracovanie does with the originals (owner, 30. 10. 2026 – only there; Single spracovanie and the
/// other tools always keep them). Deleted originals always go to the Recycle Bin, never permanently.
/// </summary>
public enum OriginalsMode
{
    /// <summary>The originals stay.</summary>
    Keep,

    /// <summary>Each original goes to the Recycle Bin right after its result is written and verified in the library.</summary>
    DeleteAutomatically,

    /// <summary>The originals stay during the run; Zmazať originály sends them to the Recycle Bin after it.</summary>
    DeleteManually,

    /// <summary>
    /// Superrýchle (owner, 2. 11. 2026 – the program proved itself): a file whose header only changes, lying on the disk of
    /// its output folder, is moved into the library and edited there in seconds – the original becomes the result (not in
    /// the Recycle Bin; Vrátiť originál puts it back as it was). Any other file is written anew and its original goes to the
    /// Recycle Bin as with <see cref="DeleteAutomatically"/>; a file with more hard links or one in use is never moved.
    /// </summary>
    MoveIntoLibrary,
}

/// <summary>What a choice of Hromadné spracovanie and MEGA means for the processing.</summary>
public static class OriginalsModes
{
    /// <summary>Originals leave their place during the run (to the Recycle Bin, or moved into the library).</summary>
    public static bool RemovesWhileRunning(this OriginalsMode mode) => mode is OriginalsMode.DeleteAutomatically or OriginalsMode.MoveIntoLibrary;

    /// <summary>The output options of a run with this choice.</summary>
    public static OutputOptions Output(this OriginalsMode mode) =>
        new(mode.RemovesWhileRunning() ? OriginalHandling.Delete : OriginalHandling.Keep, MoveIntoLibrary: mode == OriginalsMode.MoveIntoLibrary);
}

/// <param name="PreserveModifiedTime">
/// The result gets the original's last-modified time plus two seconds, so libraries keep their order while
/// media servers and caches still notice the change.
/// </param>
/// <param name="MoveIntoLibrary">
/// With <see cref="OriginalHandling.Delete"/>, a file whose header only changes and that lies on the library's drive is
/// moved into the library and edited there (only Superrýchle of Hromadné spracovanie and MEGA, owner 2. 11. 2026). False:
/// a new file is always written and verified first and the untouched original goes to the Recycle Bin (owner 30. 10. 2026 –
/// the original can be taken back).
/// </param>
public sealed record OutputOptions(OriginalHandling Original, bool PreserveModifiedTime = true, bool MoveIntoLibrary = true);
