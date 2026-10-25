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
            // compare the codec family only: mkvmerge may modernise IDs such as A_AC3/BSID9 -> A_AC3
            Check(problems, CodecFamily(actual.CodecId) == CodecFamily(expected.Source.CodecId), $"{where}: codec {actual.CodecId}, expected {expected.Source.CodecId}");
            Check(problems, (actual.Name ?? "") == (expected.Name ?? ""), $"{where}: name '{actual.Name}', expected '{expected.Name}'");
            Check(problems, expected.Language.IsUndetermined || actual.Language.Primary == expected.Language.Primary,
                $"{where}: language {actual.Language}, expected {expected.Language}");
            Check(problems, actual.Flags.Default == expected.Flags.Default, $"{where}: default {actual.Flags.Default}, expected {expected.Flags.Default}");
            Check(problems, actual.Flags.Enabled == expected.Flags.Enabled, $"{where}: enabled {actual.Flags.Enabled}, expected {expected.Flags.Enabled}");
            Check(problems, actual.Flags.Forced == expected.Flags.Forced, $"{where}: forced {actual.Flags.Forced}, expected {expected.Flags.Forced}");
        }

        Check(problems, result.ChapterEntries == source.ChapterEntries, $"chapters {result.ChapterEntries}, expected {source.ChapterEntries}");
        Check(problems, result.AttachmentCount == source.AttachmentCount, $"attachments {result.AttachmentCount}, expected {source.AttachmentCount}");
        Check(problems, !target.RemoveTags || !result.HasTags, "tags are still present");
        if (source.Duration is { } before && result.Duration is { } after)
        {
            Check(problems, Math.Abs((after - before).TotalSeconds) <= 1, $"duration {after}, expected {before}");
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
