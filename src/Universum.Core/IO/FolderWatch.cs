namespace Universum.Core.IO;

/// <summary>
/// The input folder watched (owner, 31. 10. 2026: tell and offer, never process by itself): a film or episode that comes
/// into the folder (or any folder below it) is reported once it has settled – its size has not changed for
/// <see cref="Settle"/> and it can be read while nothing writes it any more (a copy in progress keeps it open). Files of
/// UNIVERSUM itself and downloads in progress are not reported. Reports come on a worker thread, several files at once.
/// </summary>
public sealed class FolderWatch : IDisposable
{
    private static readonly HashSet<string> Videos = new([".mkv", ".mp4", ".m4v", ".avi", ".ts", ".m2ts", ".mts", ".mov", ".webm", ".wmv", ".mpg", ".mpeg"], StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Unfinished = [".part", ".crdownload", ".!qb", ".tmp", ".partial", ".download"];

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly Func<string, bool> _skip;
    private readonly Dictionary<string, Pending> _pending = new(Paths.Comparer);
    private bool _disposed;

    /// <param name="skip">Paths not to report (the library's results, files already known).</param>
    public FolderWatch(string folder, TimeSpan settle, Func<string, bool>? skip = null)
    {
        Folder = folder;
        Settle = settle;
        _skip = skip ?? (_ => false);
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Touch(e.FullPath);
        _watcher.Changed += (_, e) => Touch(e.FullPath);
        _watcher.Renamed += (_, e) => Touch(e.FullPath);
        _watcher.Error += (_, _) => { /* a lost event: the file comes with its next change, or the next start */ };
        _timer = new Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
        var period = TimeSpan.FromMilliseconds(Math.Clamp(settle.TotalMilliseconds / 2, 100, 2000));
        _timer.Change(period, period);
    }

    public string Folder { get; }

    /// <summary>How long a file must stay the same before it is reported.</summary>
    public TimeSpan Settle { get; }

    /// <summary>Films and episodes that came and settled.</summary>
    public event Action<IReadOnlyList<string>>? Arrived;

    /// <summary>A film or episode by its name (a video file, not a download in progress nor a file of UNIVERSUM).</summary>
    public static bool IsVideo(string path)
    {
        var name = Path.GetFileName(path);
        return Videos.Contains(Path.GetExtension(path)) && !name.StartsWith('~') && !name.StartsWith('.') &&
               !name.Contains(".universum-", StringComparison.OrdinalIgnoreCase) &&
               !Unfinished.Any(u => name.EndsWith(u, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        lock (_pending)
        {
            _disposed = true;
        }

        _watcher.Dispose();
        _timer.Dispose();
    }

    // a file changed, or a folder came (copied or moved in whole): its films and episodes wait to settle
    private void Touch(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(5000))
                {
                    Touch(file);
                }

                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (!IsVideo(path) || _skip(path))
        {
            return;
        }

        lock (_pending)
        {
            if (!_disposed)
            {
                _pending[path] = new Pending(Size(path), DateTime.UtcNow);
            }
        }
    }

    private void Check()
    {
        var ready = new List<string>();
        lock (_pending)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var (path, pending) in _pending.ToList())
            {
                var size = Size(path);
                if (size < 0)
                {
                    // gone (moved on, deleted)
                    _pending.Remove(path);
                }
                else if (size != pending.Size)
                {
                    _pending[path] = new Pending(size, DateTime.UtcNow);
                }
                else if (DateTime.UtcNow - pending.Since >= Settle && size > 0 && CanRead(path))
                {
                    _pending.Remove(path);
                    ready.Add(path);
                }
            }
        }

        if (ready.Count > 0)
        {
            Arrived?.Invoke(ready);
        }
    }

    private static long Size(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    // nothing writes the file any more: it opens for reading while denying writers
    private static bool CanRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record Pending(long Size, DateTime Since);
}
