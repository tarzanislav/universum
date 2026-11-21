namespace Universum.Core.IO;

/// <summary>
/// The free space of the disks UNIVERSUM writes to (owner, 21. 11. 2026: the program must not get stuck when a disk runs
/// full): what is free, what always stays free, and a guard that stops a writing tool before the disk is full.
/// </summary>
public static class DiskSpace
{
    /// <summary>
    /// What always stays free on a disk written to – for Windows and the other programs (a torrent, a media server); a file
    /// is written only when it fits with this to spare, and a tool writing is stopped when the disk falls under half of it.
    /// </summary>
    public const long Reserve = 1L << 30;

    /// <summary>What a header edited in place needs at most (Superrýchle: the header grows at the end of the file).</summary>
    public const long InPlace = 256L << 20;

    /// <summary>The bytes free for the user on the disk of <paramref name="path"/>; null when not known (some network paths).</summary>
    public static long? Free(string path)
    {
        try
        {
            return Root(path) is { } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The root of the disk of a path ("E:\", "\\server\share\"); null when there is none.</summary>
    public static string? Root(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root ? root : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The message of a disk without room – the words of the processing log read it ("Na disku E:\ je voľných 120 MB, treba
    /// 4 200 MB."): <c>not enough free space on {root}: {free} MB free, {needed} MB needed</c>.
    /// </summary>
    public static string NotEnough(string root, long free, long needed) =>
        $"not enough free space on {root}: {Math.Max(0, free) >> 20} MB free, {needed >> 20} MB needed";
}

/// <summary>
/// Watches the disks a tool writes to while it works (owner, 21. 11. 2026: another program – a torrent – may fill the disk
/// meanwhile): when one of them falls under half of <see cref="DiskSpace.Reserve"/>, <see cref="Token"/> is cancelled, the
/// tool stops and its unfinished file is deleted, so the disk never runs full and nothing gets stuck.
/// </summary>
public sealed class DiskGuard : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly Timer _timer;
    private readonly IReadOnlyList<string> _roots;
    private int _checking;

    public DiskGuard(IEnumerable<string> paths, CancellationToken cancellationToken, TimeSpan? every = null)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _roots = [.. paths.Select(DiskSpace.Root).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
        var period = every ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => Check(), null, period, period);
    }

    /// <summary>Cancelled when the caller cancels, or a disk watched is almost full.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>The disk that was almost full (the work was stopped for it); null while all have room.</summary>
    public string? FullDisk { get; private set; }

    /// <summary>The free space of <see cref="FullDisk"/> when the work was stopped.</summary>
    public long FreeWhenStopped { get; private set; }

    /// <summary>Checks the disks now (the timer does it every few seconds).</summary>
    public void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var root in _roots)
            {
                if (DiskSpace.Free(root) is { } free && free < DiskSpace.Reserve / 2)
                {
                    FreeWhenStopped = free;
                    FullDisk = root;
                    _source.Cancel();
                    return;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // the work ended meanwhile
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>The message for the processing log when the work was stopped for a full disk.</summary>
    public string Message => DiskSpace.NotEnough(FullDisk ?? "", FreeWhenStopped, DiskSpace.Reserve / 2);

    public void Dispose()
    {
        _timer.Dispose();
        _source.Dispose();
    }
}
