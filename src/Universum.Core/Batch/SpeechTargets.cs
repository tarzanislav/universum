using Universum.Core.Media;

namespace Universum.Core.Batch;

/// <summary>One audio track to listen to: in one file for that file alone, or in a group's first file for the whole group.</summary>
public sealed record SpeechTarget(BatchGroup Group, MediaFile File, SlotKey Key, MediaTrack Track, bool ForGroup);

/// <summary>Which audio tracks to listen to, and the found language kept where it belongs (owner, 28. 10. 2026).</summary>
/// <remarks>
/// The films of one structure group may be spoken in different languages, so each film is listened to on its own;
/// a series is spoken alike, so one episode (the group's first file) speaks for its group, as does a group of one
/// file. Files the user selects are always listened to one by one. A track whose language the file, its name, the
/// user or an earlier hearing already gives is not listened to again.
/// </remarks>
public static class SpeechTargets
{
    /// <summary>Whole groups: films file by file, a series (or a group of one file) through its first file.</summary>
    public static (IReadOnlyList<SpeechTarget> Targets, int Known) ForGroups(IEnumerable<BatchGroup> groups, bool series)
    {
        var candidates = groups.SelectMany(group => series || group.Files.Count == 1
            ? Tracks(group, group.Representative, forGroup: true)
            : group.Files.SelectMany(file => Tracks(group, file, forGroup: false)));
        return Split(candidates);
    }

    /// <summary>The files the user selected, each on its own.</summary>
    public static (IReadOnlyList<SpeechTarget> Targets, int Known) ForFiles(IEnumerable<(BatchGroup Group, MediaFile File)> files) =>
        Split(files.SelectMany(f => Tracks(f.Group, f.File, forGroup: false)));

    /// <summary>Keeps a heard language as a suggestion of the group or of the one file.</summary>
    public static void Keep(SpeechTarget target, LanguageEvidence suggestion)
    {
        if (target.ForGroup)
        {
            target.Group.Suggestions[target.Key] = suggestion;
        }
        else
        {
            target.Group.SuggestForFile(target.File.Path, target.Key, suggestion);
        }
    }

    /// <summary>The track's language is already given and it is not listened to.</summary>
    public static bool IsKnown(SpeechTarget target)
    {
        var (group, file, key, track, forGroup) = target;
        return !track.Language.IsUndetermined || LanguageGuess.FromName(track.Name) is not null ||
               group.Decisions.Languages.ContainsKey(key) ||
               (forGroup
                   ? group.Suggestions.TryGetValue(key, out var suggestion) && suggestion.Source == EvidenceSource.Speech
                   : group.Decisions.FileLanguage(file.Path, key) is not null || group.FileSuggestion(file.Path, key) is not null);
    }

    // the audio tracks without a language in the file
    private static IEnumerable<SpeechTarget> Tracks(BatchGroup group, MediaFile file, bool forGroup) =>
        SlotKey.Of(file).Where(x => x.Key.Type == TrackType.Audio && x.Track.Language.IsUndetermined)
            .Select(x => new SpeechTarget(group, file, x.Key, x.Track, forGroup));

    private static (IReadOnlyList<SpeechTarget>, int) Split(IEnumerable<SpeechTarget> candidates)
    {
        var all = candidates.DistinctBy(t => (t.File.Path, t.Key)).ToList();
        var targets = all.Where(t => !IsKnown(t)).ToList();
        return (targets, all.Count - targets.Count);
    }
}
