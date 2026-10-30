using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Universum.Core.Subtitles;

/// <summary>One subtitle track as it was left in Úprava titulkostopy (the values of the page, so they come back as they were).</summary>
public sealed record StudioTrackState
{
    /// <summary>A track of the video (its ID), or …</summary>
    public int? TrackId { get; init; }

    /// <summary>…an external subtitle file.</summary>
    public string? ExternalFile { get; init; }

    public bool Include { get; init; } = true;

    /// <summary>The language code ("cs", "fil").</summary>
    public string? Language { get; init; }

    public string? Name { get; init; }

    public bool Forced { get; init; }

    public bool HearingImpaired { get; init; }

    /// <summary>Zmena FPS (labels of <see cref="FrameRate.All"/>).</summary>
    public string? FpsFrom { get; init; }

    public string? FpsTo { get; init; }

    public string Speed { get; init; } = "0";

    public string Delay { get; init; } = "0";

    public bool ToSrt { get; init; }

    public bool RemoveHearingImpaired { get; init; }

    public bool RemoveFormatting { get; init; }

    public bool FixTimes { get; init; }

    /// <summary>Texts of single subtitles changed by hand (by their place in the subtitles as read; null = left out).</summary>
    public Dictionary<int, string?>? Texts { get; init; }

    /// <summary>The texts those subtitles had: the changes are used only on the same subtitles.</summary>
    public Dictionary<int, string>? TextsBefore { get; init; }
}

/// <summary>The work on the subtitles of one video, saved to go on later (owner, 30. 10. 2026: Uložiť prácu).</summary>
public sealed record StudioWork
{
    public required string Video { get; init; }

    /// <summary>The video as it was (a changed video does not get work done on another one).</summary>
    public long Size { get; init; }

    public DateTime ModifiedUtc { get; init; }

    public DateTime SavedUtc { get; init; }

    public List<StudioTrackState> Tracks { get; init; } = [];
}

/// <summary>
/// Saved work of Úprava titulkostopy: one JSON file a video in a folder of the Windows account (named by a hash of the
/// video's path, never sent anywhere); it is offered again when the same, unchanged video is opened.
/// </summary>
public sealed class StudioWorkStore(string folder)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Folder { get; } = folder;

    /// <summary>The work saved for the video as it is now, or null.</summary>
    public StudioWork? Load(string video)
    {
        try
        {
            var file = FileOf(video);
            if (!File.Exists(file) || JsonSerializer.Deserialize<StudioWork>(File.ReadAllBytes(file), Options) is not { } work)
            {
                return null;
            }

            var info = new FileInfo(video);
            return info.Exists && info.Length == work.Size && info.LastWriteTimeUtc == work.ModifiedUtc ? work : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Saves the work (the video's size and time are taken now).</summary>
    public StudioWork Save(string video, IReadOnlyList<StudioTrackState> tracks, DateTime nowUtc)
    {
        var info = new FileInfo(video);
        var work = new StudioWork { Video = video, Size = info.Length, ModifiedUtc = info.LastWriteTimeUtc, SavedUtc = nowUtc, Tracks = [.. tracks] };
        Directory.CreateDirectory(Folder);
        var file = FileOf(video);
        var temporary = file + ".part";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(work, Options));
        File.Move(temporary, file, overwrite: true);
        return work;
    }

    public void Delete(string video)
    {
        try
        {
            File.Delete(FileOf(video));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // it stays; it is offered only for the same video
        }
    }

    private string FileOf(string video)
    {
        var path = Path.GetFullPath(video);
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        return Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant() + ".json");
    }
}
