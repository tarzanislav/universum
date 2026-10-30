using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using System.Text;
using Universum.Core.IO;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.Core.Library;

/// <summary>
/// Remembers from which original each library file was made (a fingerprint of the original) and with which result
/// (a signature of the target state), so processing the same original again is recognised (specification 13, K8).
/// It never guesses: a library file counts as the same stream only when it was made from an original with the same
/// fingerprint into the same target and has not changed since. A file it does not know is never skipped, at worst it
/// becomes another version.
/// </summary>
public sealed class LibraryIndex
{
    private const int SampleSize = 1 << 20;

    private readonly string _file;
    private readonly Dictionary<string, Entry> _entries;

    public LibraryIndex(string file)
    {
        _file = file;
        _entries = Load(file);
    }

    /// <summary>Size, modification time and SHA-256 of the start, middle and end of a file (1 MiB each).</summary>
    public static async Task<string> FingerprintAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, SampleSize, useAsync: true);
        var length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[SampleSize];
        foreach (var offset in new[] { 0, Math.Max(0, (length / 2) - (SampleSize / 2)), Math.Max(0, length - SampleSize) }.Distinct())
        {
            stream.Position = offset;
            var read = 0;
            int count;
            while (read < buffer.Length && (count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false)) > 0)
            {
                read += count;
            }

            hash.AppendData(buffer, 0, read);
        }

        return length.ToString(CultureInfo.InvariantCulture) + ":" +
               File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture) + ":" +
               Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// What the result will be: track order, languages, flags, roles, names (except the random video name), removed
    /// tracks, tags and index repair.
    /// </summary>
    public static string TargetSignature(TargetState target)
    {
        var text = new StringBuilder();
        foreach (var track in target.Tracks)
        {
            text.Append(CultureInfo.InvariantCulture, $"{track.TrackId}|{track.Type}|{track.Language.Ietf ?? track.Language.Legacy}|{track.Flags.Default}{track.Flags.Enabled}{track.Flags.Forced}|{track.Role}|")
                .Append(track.Type == TrackType.Video ? "" : track.Name ?? "");
            if (track.Timing is { IsNone: false } timing)
            {
                text.Append("|sync:").Append(timing.MkvMergeValue);
            }

            if (track.Source.ExternalFile is { } external)
            {
                text.Append("|file:").Append(ContentHash(external));
            }

            text.Append('\n');
        }

        text.Append("removed:").AppendJoin(',', target.RemovedFromFile.Select(t => t.Id)).Append(";tags:").Append(target.RemoveTags).Append(";index:").Append(target.RepairIndex);
        if (target.TitleChanged)
        {
            text.Append(";title:").Append(target.ResultTitle);
        }

        if (target.NewChapters is { } chapters)
        {
            text.Append(";chapters:").AppendJoin('|', chapters.OrderBy(c => c.Start).Select(c => $"{c.Start.Ticks}/{c.Name}/{c.Language.Primary}"));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..24];
    }

    // an added subtitle file is known by its content (another file of the same name is another result)
    private static string ContentHash(string path)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..16];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "missing";
        }
    }

    /// <summary>Target of a result whose edit was undone: what it holds is not known, a later run replaces it.</summary>
    public const string UndoneTarget = "undone";

    /// <summary>
    /// A version of the placement made from the same original and unchanged since (preferring one made into the same
    /// target); null when there is none. Entries without a target signature are not trusted.
    /// </summary>
    public (string Path, string Target)? FindPrevious(Placement placement, string sourceFingerprint, string targetSignature)
    {
        if (!Directory.Exists(placement.Directory))
        {
            return null;
        }

        (string Path, string Target)? first = null;
        lock (_entries)
        {
            foreach (var file in Directory.EnumerateFiles(placement.Directory, "*" + placement.Extension))
            {
                if (placement.IsVersionName(Path.GetFileName(file)) &&
                    _entries.TryGetValue(Path.GetFullPath(file), out var entry) &&
                    entry.Target.Length > 0 &&
                    entry.Source == sourceFingerprint &&
                    Unchanged(entry, file))
                {
                    if (entry.Target == targetSignature)
                    {
                        return (file, entry.Target);
                    }

                    first ??= (file, entry.Target);
                }
            }
        }

        return first;
    }

    /// <summary>
    /// The target signature of a result in the placement's folder – under any name, other decisions give another name –
    /// made from this original after <paramref name="sinceUtc"/> and unchanged since; null when there is none (MEGA
    /// spracovanie: a batch never adds to or replaces a result made after it was prepared – its decisions are older).
    /// </summary>
    public string? MadeSince(Placement placement, string sourceFingerprint, DateTime sinceUtc)
    {
        if (!Directory.Exists(placement.Directory))
        {
            return null;
        }

        lock (_entries)
        {
            foreach (var file in Directory.EnumerateFiles(placement.Directory, "*" + placement.Extension))
            {
                if (_entries.TryGetValue(Path.GetFullPath(file), out var entry) &&
                    entry.Source == sourceFingerprint && entry.RecordedUtc > sinceUtc && Unchanged(entry, file))
                {
                    return entry.Target;
                }
            }
        }

        return null;
    }

    /// <summary>Records that <paramref name="resultPath"/> was made from an original with this fingerprint into this target.</summary>
    public void Record(string resultPath, string sourceFingerprint, string targetSignature)
    {
        lock (_entries)
        {
            _entries[Path.GetFullPath(resultPath)] = new Entry(sourceFingerprint, new FileInfo(resultPath).Length, DateTime.UtcNow, targetSignature, File.GetLastWriteTimeUtc(resultPath));
            Save();
        }
    }

    /// <summary>
    /// True when UNIVERSUM made <paramref name="path"/> and it has not changed since (same size and modification time):
    /// only such a file may be edited or replaced in the library. Any other file – also one with a library-like name, or
    /// a result changed or replaced outside UNIVERSUM – is treated as an original.
    /// </summary>
    public bool IsResult(string path)
    {
        lock (_entries)
        {
            return File.Exists(path) && _entries.TryGetValue(Path.GetFullPath(path), out var entry) && Unchanged(entry, path);
        }
    }

    /// <summary>True when the index has an entry for <paramref name="path"/> (whether or not the file is still the result).</summary>
    public bool Knows(string path)
    {
        lock (_entries)
        {
            return _entries.ContainsKey(Path.GetFullPath(path));
        }
    }

    /// <summary>
    /// True when <paramref name="resultPath"/> is a result UNIVERSUM made from an original with this fingerprint and it has
    /// not changed since (Zmazať originály: the original may go only when its copy in the library is surely made from it).
    /// </summary>
    public bool IsMadeFrom(string resultPath, string sourceFingerprint)
    {
        lock (_entries)
        {
            return File.Exists(resultPath) && _entries.TryGetValue(Path.GetFullPath(resultPath), out var entry) &&
                   entry.Source == sourceFingerprint && Unchanged(entry, resultPath);
        }
    }

    /// <summary>A library result was reworked: it may have a new path, size and target; it still comes from the same original.</summary>
    public void Replace(string oldPath, string newPath, string targetSignature)
    {
        lock (_entries)
        {
            if (!_entries.Remove(Path.GetFullPath(oldPath), out var entry))
            {
                return;
            }

            _entries[Path.GetFullPath(newPath)] = entry with
            {
                Size = new FileInfo(newPath).Length, RecordedUtc = DateTime.UtcNow, Target = targetSignature, WrittenUtc = File.GetLastWriteTimeUtc(newPath),
            };
            Save();
        }
    }

    /// <summary>
    /// An edit of a library result was undone: the entry follows the file (its size and time, maybe its name back). What
    /// the result now holds is not known, so a later run of the original replaces it rather than finding it done – unless
    /// a failed edit was put back (<paramref name="sameAsBefore"/>): then it holds what it held.
    /// </summary>
    public void Undone(string path, string finalPath, bool sameAsBefore = false)
    {
        lock (_entries)
        {
            if (_entries.Remove(Path.GetFullPath(path), out var entry) && File.Exists(finalPath))
            {
                _entries[Path.GetFullPath(finalPath)] = entry with
                {
                    Size = new FileInfo(finalPath).Length, Target = sameAsBefore ? entry.Target : UndoneTarget, WrittenUtc = File.GetLastWriteTimeUtc(finalPath),
                };
            }

            Save();
        }
    }

    /// <summary>Forgets a library file (it went to the Recycle Bin or was renamed).</summary>
    public void Forget(string resultPath)
    {
        lock (_entries)
        {
            if (_entries.Remove(Path.GetFullPath(resultPath)))
            {
                Save();
            }
        }
    }

    private static bool Unchanged(Entry entry, string file) =>
        entry.Size == new FileInfo(file).Length && entry.WrittenUtc == File.GetLastWriteTimeUtc(file);

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
        var temporary = _file + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
        File.Move(temporary, _file, overwrite: true);
    }

    private static Dictionary<string, Entry> Load(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) is { } entries)
            {
                return new Dictionary<string, Entry>(entries, Paths.Comparer);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // without the index nothing is recognised; files become versions instead of being skipped
        }

        return new Dictionary<string, Entry>(Paths.Comparer);
    }

    /// <param name="WrittenUtc">Modification time of the result when UNIVERSUM last wrote it; entries of older versions have none and are not trusted.</param>
    public sealed record Entry(string Source, long Size, DateTime RecordedUtc, string Target = "", DateTime? WrittenUtc = null);
}
