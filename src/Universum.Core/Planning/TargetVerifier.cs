using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.Core.Planning;

/// <summary>
/// Compares a written file with its target state. Any difference means the write did not do what was planned,
/// so the original must not be replaced.
/// </summary>
public static class TargetVerifier
{
    public static IReadOnlyList<string> Compare(TargetState target, MediaFile result)
    {
        var problems = new List<string>();
        var source = target.Source;
        if (result.Tracks.Count != target.Tracks.Count)
        {
            problems.Add($"track count {result.Tracks.Count}, expected {target.Tracks.Count}");
            return problems;
        }

        for (var i = 0; i < target.Tracks.Count; i++)
        {
            var expected = target.Tracks[i];
            var actual = result.Tracks[i];
            var where = $"track {i + 1}";
            Check(problems, actual.Type == expected.Type, $"{where}: type {actual.Type}, expected {expected.Type}");
            if (source.IsMatroska)
            {
                // compare the codec family only: mkvmerge may modernise IDs such as A_AC3/BSID9 -> A_AC3
                Check(problems, CodecFamily(actual.CodecId) == CodecFamily(expected.Source.CodecId), $"{where}: codec {actual.CodecId}, expected {expected.Source.CodecId}");
            }

            // from another container (MP4, AVI, TS…) there is no Matroska codec ID to compare, and mkvmerge names the same
            // codec differently in the MKV ("AVC/H.264" in FLV, "AVC/H.264/MPEG-4p10" in MKV; MP4 timed text becomes SRT):
            // the type and the order of the tracks, their names, languages and flags are what is checked
            Check(problems, (actual.Name ?? "") == (expected.Name ?? ""), $"{where}: name '{actual.Name}', expected '{expected.Name}'");
            Check(problems, expected.Language.IsUndetermined || actual.Language.Primary == expected.Language.Primary,
                $"{where}: language {actual.Language}, expected {expected.Language}");
            Check(problems, actual.Flags.Default == expected.Flags.Default, $"{where}: default {actual.Flags.Default}, expected {expected.Flags.Default}");
            Check(problems, actual.Flags.Enabled == expected.Flags.Enabled, $"{where}: enabled {actual.Flags.Enabled}, expected {expected.Flags.Enabled}");
            Check(problems, actual.Flags.Forced == expected.Flags.Forced, $"{where}: forced {actual.Flags.Forced}, expected {expected.Flags.Forced}");
            if (expected.OtherFlagsChanged)
            {
                var flags = actual.Flags with { Default = expected.Flags.Default, Enabled = expected.Flags.Enabled, Forced = expected.Flags.Forced };
                Check(problems, flags == expected.Flags, $"{where}: flags {actual.Flags}, expected {expected.Flags}");
            }
        }

        Check(problems, !target.RepairIndex || result.TracksOf(TrackType.Video).All(v => v.IndexEntries is null or > 0), "video index still missing");
        // a film cut keeps the chapters of its parts only
        Check(problems, target.IsCut || result.ChapterEntries == target.ResultChapterEntries, $"chapters {result.ChapterEntries}, expected {target.ResultChapterEntries}");
        Check(problems, result.AttachmentCount == source.AttachmentCount, $"attachments {result.AttachmentCount}, expected {source.AttachmentCount}");
        Check(problems, !target.RemoveTags || !result.HasTags, "tags are still present");
        Check(problems, !target.TitleChanged || (result.Title ?? "") == (target.ResultTitle ?? ""), $"title '{result.Title}', expected '{target.ResultTitle}'");
        if (target.Cut is { Count: > 0 } parts && result.Duration is { } cut)
        {
            // the parts kept as written (at key frames, or exactly): never shorter – a shorter film means a broken timeline
            var kept = parts.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Length);
            Check(problems, cut >= kept - TimeSpan.FromSeconds(0.5) && cut <= kept + TimeSpan.FromSeconds(1 + parts.Count), $"duration {cut}, expected {kept}");
        }
        else if (source.Duration is { } before && result.Duration is { } after)
        {
            // added subtitles and new times may end after the video: the file is then longer, never shorter. A track
            // removed may have reached further than the rest (owner's report, 31. 10. 2026: subtitles or a sound longer
            // than the picture): then the file ends with the picture kept – never shorter than the picture itself
            var shortest = before;
            if (target.RemovedFromFile.Any() && VideoLength(target) is { } picture && picture < before)
            {
                shortest = picture;
            }

            var longer = target.AddsFiles || target.TimingChanged;
            Check(problems, after >= shortest - TimeSpan.FromSeconds(1) && (longer || after <= before + TimeSpan.FromSeconds(1)),
                $"duration {after}, expected {before}" + (shortest < before ? $" (the picture kept {shortest})" : ""));
        }

        return problems;
    }

    // the length of the video kept, from its frames and frame duration (mkvmerge's statistics); null when not known
    private static TimeSpan? VideoLength(TargetState target) =>
        target.Tracks.FirstOrDefault(t => t.Type == TrackType.Video && !t.IsAdded)?.Source is { StatisticsFrames: > 0 and var frames, DefaultDurationNanoseconds: > 0 and var duration }
            ? TimeSpan.FromTicks(frames * duration / 100)
            : null;

    private static string CodecFamily(string? codecId) => (codecId ?? "").Split('/')[0];

    private static void Check(List<string> problems, bool condition, string problem)
    {
        if (!condition)
        {
            problems.Add(problem);
        }
    }
}
