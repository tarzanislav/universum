using System.Text.Json;

namespace Universum.Core.Media;

/// <summary>
/// Remembers identification results by path, size and modification time, so re-analysing a folder of thousands of
/// files only reads the files that changed (specification, section 12). Thread-safe.
/// </summary>
public sealed class CachingInspector : IMediaInspector
{
    private const int MaxEntries = 100_000;

    private readonly IMediaInspector _inner;
    private readonly string _cacheFile;
    private readonly Dictionary<string, Entry> _entries;
    private readonly object _lock = new();
    private bool _dirty;

    public CachingInspector(IMediaInspector inner, string cacheFile)
    {
        _inner = inner;
        _cacheFile = cacheFile;
        _entries = Load(cacheFile);
    }

    public string Name => _inner.Name;

    public int Hits { get; private set; }

    public async Task<MediaFile> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        var key = info.FullName;
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Size == info.Length &&
                entry.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks && entry.Inspector == _inner.Name)
            {
                Hits++;
                return entry.File with { Path = key };
            }
        }

        var file = await _inner.InspectAsync(key, cancellationToken).ConfigureAwait(false);
        info.Refresh();
        lock (_lock)
        {
            _entries[key] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, _inner.Name, file);
            _dirty = true;
        }

        return file;
    }

    /// <summary>Forgets a file, e.g. after UNIVERSUM itself has written it.</summary>
    public void Invalidate(string path)
    {
        lock (_lock)
        {
            _dirty |= _entries.Remove(Path.GetFullPath(path));
        }
    }

    /// <summary>Writes the cache (atomically) when something changed.</summary>
    public void Save()
    {
        Dictionary<string, Entry> snapshot;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            snapshot = _entries.Count <= MaxEntries
                ? new Dictionary<string, Entry>(_entries)
                : _entries.TakeLast(MaxEntries).ToDictionary(e => e.Key, e => e.Value);
            _dirty = false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cacheFile))!);
        var temporary = _cacheFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
        File.Move(temporary, _cacheFile, overwrite: true);
    }

    private static Dictionary<string, Entry> Load(string cacheFile)
    {
        try
        {
            return File.Exists(cacheFile)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(cacheFile)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return []; // a damaged cache is simply rebuilt
        }
    }

    private sealed record Entry(long Size, long LastWriteUtcTicks, string Inspector, MediaFile File);
}
