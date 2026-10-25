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
                .Append(track.Type == TrackType.Video ? "" : track.Name ?? "").Append('\n');
        }

        text.Append("removed:").AppendJoin(',', target.Removed.Select(t => t.Id)).Append(";tags:").Append(target.RemoveTags).Append(";index:").Append(target.RepairIndex);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..24];
    }

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
                    entry.Size == new FileInfo(file).Length)
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

    /// <summary>Records that <paramref name="resultPath"/> was made from an original with this fingerprint into this target.</summary>
    public void Record(string resultPath, string sourceFingerprint, string targetSignature)
    {
        lock (_entries)
        {
            _entries[Path.GetFullPath(resultPath)] = new Entry(sourceFingerprint, new FileInfo(resultPath).Length, DateTime.UtcNow, targetSignature);
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

    public sealed record Entry(string Source, long Size, DateTime RecordedUtc, string Target = "");
}
