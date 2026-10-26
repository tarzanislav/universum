using Universum.Core.Media;

namespace Universum.Core.Muxing;

/// <summary>Compares the written file with what was asked for; any difference means the output is not kept.</summary>
public static class MuxVerifier
{
    public static IReadOnlyList<string> Compare(MuxSpec spec, MediaFile result)
    {
        var problems = new List<string>();
        if (result.Tracks.Count != spec.Tracks.Count)
        {
            problems.Add($"track count {result.Tracks.Count}, expected {spec.Tracks.Count}");
            return problems;
        }

        for (var i = 0; i < spec.Tracks.Count; i++)
        {
            var expected = spec.Tracks[i];
            var source = spec.TrackOf(expected);
            var actual = result.Tracks[i];
            var where = $"track {i + 1}";
            Check(problems, actual.Type == source.Type, $"{where}: type {actual.Type}, expected {source.Type}");
            Check(problems, (actual.Name ?? "") == (expected.Name ?? ""), $"{where}: name '{actual.Name}', expected '{expected.Name}'");
            Check(problems, expected.Language.IsUndetermined || actual.Language.Primary == expected.Language.Primary,
                $"{where}: language {actual.Language}, expected {expected.Language}");
            Check(problems, actual.Flags.Default == expected.Flags.Default, $"{where}: default {actual.Flags.Default}, expected {expected.Flags.Default}");
            Check(problems, actual.Flags.Forced == expected.Flags.Forced, $"{where}: forced {actual.Flags.Forced}, expected {expected.Flags.Forced}");
            Check(problems, actual.Flags.Enabled == expected.Flags.Enabled, $"{where}: enabled {actual.Flags.Enabled}, expected {expected.Flags.Enabled}");
        }

        if (!string.IsNullOrEmpty(spec.Title))
        {
            Check(problems, result.Title == spec.Title, $"title '{result.Title}', expected '{spec.Title}'");
        }

        var chaptersExpected = !string.IsNullOrEmpty(spec.ChaptersFile) || spec.Sources.Any(s => s.KeepChapters && s.File.HasChapters);
        Check(problems, chaptersExpected == result.HasChapters, chaptersExpected ? "chapters are missing" : "unexpected chapters");
        var attachments = spec.Sources.Where(s => s.KeepAttachments).Sum(s => s.File.AttachmentCount) + spec.Attachments.Count;
        Check(problems, result.AttachmentCount == attachments, $"attachments {result.AttachmentCount}, expected {attachments}");
        return problems;
    }

    private static void Check(List<string> problems, bool condition, string problem)
    {
        if (!condition)
        {
            problems.Add(problem);
        }
    }
}
