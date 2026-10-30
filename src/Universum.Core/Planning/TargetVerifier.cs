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
        Check(problems, result.ChapterEntries == target.ResultChapterEntries, $"chapters {result.ChapterEntries}, expected {target.ResultChapterEntries}");
        Check(problems, result.AttachmentCount == source.AttachmentCount, $"attachments {result.AttachmentCount}, expected {source.AttachmentCount}");
        Check(problems, !target.RemoveTags || !result.HasTags, "tags are still present");
        Check(problems, !target.TitleChanged || (result.Title ?? "") == (target.ResultTitle ?? ""), $"title '{result.Title}', expected '{target.ResultTitle}'");
        if (source.Duration is { } before && result.Duration is { } after)
        {
            // added subtitles and new times may end after the video: the file is then longer, never shorter
            Check(problems, target.AddsFiles || target.TimingChanged ? (before - after).TotalSeconds <= 1 : Math.Abs((after - before).TotalSeconds) <= 1,
                $"duration {after}, expected {before}");
        }

        return problems;
    }

    private static string CodecFamily(string? codecId) => (codecId ?? "").Split('/')[0];

    private static void Check(List<string> problems, bool condition, string problem)
    {
        if (!condition)
        {
            problems.Add(problem);
        }
    }
}
