using Universum.Core.IO;
using Universum.Core.Media;
using Universum.Core.Rules;
using Universum.Core.Subtitles;

namespace Universum.Core.Batch;

/// <summary>An external subtitle file added to a video in Úprava titulkostopy (not one the analysis found with it).</summary>
public sealed record StudioFile(string Path, LanguageTag Language, bool Forced, bool HearingImpaired, string CodecId, string Codec, bool IsText, long? Frames)
{
    /// <summary>The file as a subtitle track of the video (<paramref name="id"/>: after the video's own tracks).</summary>
    public MediaTrack ToTrack(int id) => new()
    {
        Id = id, Type = TrackType.Subtitles, CodecId = CodecId, Codec = Codec, IsTextSubtitles = IsText, Language = Language,
        Flags = new TrackFlags(Default: false, Forced: Forced, HearingImpaired: HearingImpaired), StatisticsFrames = Frames, ExternalFile = Path,
    };
}

/// <summary>
/// What Úprava titulkostopy decided for one file of Hromadné spracovanie (owner, 30. 10. 2026: Upraviť v štúdiu → Uložiť
/// do úlohy): new times of subtitle tracks, the tracks left out, their languages and the subtitle files added. The
/// video's own tracks are known by their IDs, external files by their paths (their IDs are given by each analysis).
/// The batch, MEGA batches and the job apply it; the repairs of the text are not part of it.
/// </summary>
public sealed record StudioEdits
{
    /// <summary>New times of the video's own subtitle tracks, by track ID.</summary>
    public Dictionary<int, SubtitleTiming> Timings { get; init; } = [];

    /// <summary>New times of external subtitle files (found by the analysis or added here), by path.</summary>
    public Dictionary<string, SubtitleTiming> FileTimings { get; init; } = [];

    /// <summary>Languages chosen for the video's own subtitle tracks, by track ID.</summary>
    public Dictionary<int, LanguageTag> Languages { get; init; } = [];

    /// <summary>Languages chosen for external files found by the analysis, by path.</summary>
    public Dictionary<string, LanguageTag> FileLanguages { get; init; } = [];

    /// <summary>Subtitle tracks of the video left out.</summary>
    public List<int> Removed { get; init; } = [];

    /// <summary>External files found by the analysis but left out.</summary>
    public List<string> RemovedFiles { get; init; } = [];

    /// <summary>External files added in the studio.</summary>
    public List<StudioFile> Added { get; init; } = [];

    public bool IsEmpty => Timings.Count == 0 && FileTimings.Count == 0 && Languages.Count == 0 && FileLanguages.Count == 0 && Removed.Count == 0 &&
                           RemovedFiles.Count == 0 && Added.Count == 0;

    /// <summary>In words, for the batch: "časy titulkov 2, pridané 1, vynechané 1".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Timings.Count + FileTimings.Count is > 0 and var timed)
        {
            parts.Add($"časy titulkov {timed}");
        }

        if (Languages.Count + FileLanguages.Count is > 0 and var languages)
        {
            parts.Add($"jazyk {languages}");
        }

        if (Added.Count > 0)
        {
            parts.Add($"pridané {Added.Count}");
        }

        if (Removed.Count + RemovedFiles.Count is > 0 and var removed)
        {
            parts.Add($"vynechané {removed}");
        }

        return parts.Count == 0 ? "bez zmien" : string.Join(", ", parts);
    }

    /// <summary>The file with the subtitle files added in the studio (a file already among its tracks is not added twice).</summary>
    public MediaFile Apply(MediaFile file)
    {
        var added = Added.Where(a => !file.Tracks.Any(t => t.ExternalFile is { } known && Paths.Same(known, a.Path))).ToList();
        if (added.Count == 0)
        {
            return file;
        }

        var next = file.Tracks.Count == 0 ? 0 : file.Tracks.Max(t => t.Id) + 1;
        return file with { Tracks = [.. file.Tracks, .. added.Select((a, i) => a.ToTrack(next + i))] };
    }

    /// <summary>The decisions of the file (its tracks with the added files – <see cref="Apply(MediaFile)"/>) with these edits.</summary>
    public TrackDecisions Apply(TrackDecisions decisions, MediaFile file)
    {
        var timings = decisions.Timings.ToDictionary();
        var languages = decisions.Languages.ToDictionary();
        var removed = decisions.Removed.ToHashSet();
        foreach (var track in file.TracksOf(TrackType.Subtitles))
        {
            var path = track.ExternalFile;
            if ((path is null ? Timings.GetValueOrDefault(track.Id) : ByPath(FileTimings, path)) is { IsNone: false } timing)
            {
                timings[track.Id] = timing;
            }

            if (path is null && Languages.TryGetValue(track.Id, out var language))
            {
                languages[track.Id] = language;
            }
            else if (path is not null && FileLanguages.FirstOrDefault(l => Paths.Same(l.Key, path)) is { Key: not null } chosen)
            {
                languages[track.Id] = chosen.Value;
            }

            if (path is null ? Removed.Contains(track.Id) : RemovedFiles.Any(r => Paths.Same(r, path)))
            {
                removed.Add(track.Id);
            }
        }

        return decisions with { Timings = timings, Languages = languages, Removed = removed };
    }

    // by path, as Windows compares names
    private static SubtitleTiming? ByPath(Dictionary<string, SubtitleTiming> values, string path) =>
        values.FirstOrDefault(v => Paths.Same(v.Key, path)).Value;
}
