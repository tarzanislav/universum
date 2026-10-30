using System.Security.Cryptography;
using Universum.Core.Media;
using Universum.Core.Text;

namespace Universum.Core.Rules;

/// <summary>
/// The UNIVERSUM track rules (specification, section 6). Pure function of the file, the profile and the languages
/// supplied for undetermined tracks; the only random part is a new video track name.
/// </summary>
public static class UniversumRules
{
    /// <summary>The track name of forced subtitles (owner, 29. 10. 2026).</summary>
    public const string ForcedName = "Forced";

    private const string NameAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static TargetState Apply(MediaFile file, RuleProfile? profile = null, TrackDecisions? decisions = null)
    {
        profile ??= RuleProfile.Default;
        decisions ??= TrackDecisions.None;
        var issues = new List<RuleIssue>();
        var language = (MediaTrack t) => decisions.Languages.TryGetValue(t.Id, out var chosen) ? chosen : t.Language;
        var kept = (MediaTrack t) => t.Type is TrackType.Video or TrackType.Other || !decisions.Removed.Contains(t.Id);

        var videos = file.TracksOf(TrackType.Video).ToList();
        if (videos.Count > 1)
        {
            issues.Add(new RuleIssue(RuleIssueKind.MultipleVideoTracks));
        }

        var audio = Order(file.TracksOf(TrackType.Audio).Where(kept), t => AudioRole(t, profile), language, profile.Priority, decisions);

        // owner, 29. 10. 2026: subtitles in the profile's languages (CZ, SK, EN, PL, HU, RU) in that order, then those in
        // the language of an audio track; the others are left out unless the user keeps them (an undetermined one waits)
        var audioLanguages = audio.Where(a => !a.Language.IsUndetermined).Select(a => a.Language.Primary).ToList();
        var subtitleOrder = profile.SubtitleOrder(audioLanguages);
        // owner, 30. 10. 2026: a subtitle track without a single subtitle is removed
        var empty = file.TracksOf(TrackType.Subtitles).Where(kept).Where(ForcedSubtitles.IsEmpty).Select(t => t.Id).ToHashSet();
        var outOfList = file.TracksOf(TrackType.Subtitles).Where(kept)
            .Where(t => !empty.Contains(t.Id) && language(t) is { IsUndetermined: false } tag && !profile.KeepsSubtitles(tag.Primary, audioLanguages) &&
                        !decisions.Kept.Contains(t.Id))
            .Select(t => t.Id).ToHashSet();
        var subtitles = Order(file.TracksOf(TrackType.Subtitles).Where(t => kept(t) && !outOfList.Contains(t.Id) && !empty.Contains(t.Id)),
            t => SubtitleRole(file, t, profile, decisions), language, l => RuleProfile.PositionIn(subtitleOrder, l), decisions);
        if (audio.Count == 0)
        {
            issues.Add(new RuleIssue(RuleIssueKind.NoAudio));
        }

        foreach (var track in audio.Concat(subtitles).Where(t => t.Language.IsUndetermined))
        {
            issues.Add(new RuleIssue(RuleIssueKind.UndeterminedLanguage, track.Track.Id));
        }

        var targets = new List<TrackTarget>();
        targets.AddRange(videos.Select(v => new TrackTarget(v, decisions.VideoNames.TryGetValue(v.Id, out var name) ? name : VideoName(v, profile), v.Language, v.Flags with { Default = true, Enabled = true, Forced = false }, TrackRole.Main)));

        // audio: exactly one default – the first regular track after ordering
        var defaultAudio = audio.FirstOrDefault(a => a.Role != TrackRole.Commentary) ?? audio.FirstOrDefault();
        targets.AddRange(audio.Select(a => new TrackTarget(
            a.Track, null, a.Language,
            a.Track.Flags with { Default = ReferenceEquals(a, defaultAudio), Enabled = true, Forced = false },
            a.Role)));

        targets.AddRange(SubtitleTargets(audio, defaultAudio, subtitles, profile, issues));

        // other track types (rare) keep their position at the end and their properties
        targets.AddRange(file.Tracks.Where(t => t.Type == TrackType.Other)
            .Select(t => new TrackTarget(t, t.Name, t.Language, t.Flags, TrackRole.Main)));

        var missingIndex = videos.Any(v => v.IndexEntries == 0);
        if (missingIndex)
        {
            issues.Add(new RuleIssue(RuleIssueKind.MissingIndex));
        }

        if (file.Warnings.Count > 0)
        {
            issues.Add(new RuleIssue(RuleIssueKind.ContainerWarnings));
        }

        // owner, 29. 10. 2026: the segment title ("File title" in MKVToolNix) is always removed – it would stand for the
        // file's name in players and in MKVToolNix, the library names the file itself
        return new TargetState(file, targets, profile.RemoveTags, issues)
        {
            Removed = file.Tracks.Where(t => !kept(t) || outOfList.Contains(t.Id) || empty.Contains(t.Id)).ToList(),
            RemovedByLanguage = file.Tracks.Where(t => outOfList.Contains(t.Id)).ToList(),
            RemovedEmpty = file.Tracks.Where(t => empty.Contains(t.Id)).ToList(),
            RepairIndex = missingIndex && profile.RepairIndex,
            NewTitle = "",
        };
    }

    /// <summary>
    /// The flags of the subtitles (owner, 29. and 30. 10. 2026):
    /// <list type="bullet">
    /// <item>Forced subtitles are active only in Czech or Slovak – the Czech ones when both exist, else the Slovak ones:
    /// Default, Enabled and Forced on, every other flag off. Forced subtitles of other languages (and the Slovak ones
    /// beside Czech) are named "Forced" too, but Default and Forced off.</item>
    /// <item>With audio in Czech or Slovak the active forced subtitles are the default ones; with foreign audio only the
    /// full subtitles in the best preferred language are (Default and Enabled on, every other flag off) and the active
    /// forced ones keep Forced on, not Default – unless there are no full ones, then the forced ones are default.</item>
    /// <item>Every other subtitle track: Default off, Enabled on, Forced off; subtitles for the hearing impaired keep
    /// their flag on.</item>
    /// </list>
    /// </summary>
    private static IEnumerable<TrackTarget> SubtitleTargets(
        List<Classified> audio, Classified? defaultAudio, List<Classified> subtitles, RuleProfile profile, List<RuleIssue> issues)
    {
        var preferredAudio = audio.Any(a => a.Role != TrackRole.Commentary && profile.IsPreferred(a.Language.Primary));
        var activeForced = subtitles.Where(s => s.Role == TrackRole.Forced && profile.IsPreferred(s.Language.Primary))
            .OrderBy(s => profile.Preference(s.Language.Primary)).ThenBy(s => s.Index).FirstOrDefault();
        Classified? defaultSubtitle = null;
        if (!preferredAudio)
        {
            // foreign audio: full subtitles in the best preferred language
            defaultSubtitle = subtitles
                .Where(s => s.Role == TrackRole.Main && profile.IsPreferred(s.Language.Primary))
                .OrderBy(s => profile.Preference(s.Language.Primary))
                .FirstOrDefault();
            if (defaultSubtitle is null && activeForced is null && subtitles.Count > 0)
            {
                issues.Add(new RuleIssue(RuleIssueKind.NoPreferredSubtitles));
            }
        }

        var forcedIsDefault = defaultSubtitle is null;
        return subtitles.Select(s => new TrackTarget(
            s.Track, s.Role == TrackRole.Forced ? ForcedName : null, s.Language,
            ReferenceEquals(s, defaultSubtitle) ? new TrackFlags(Default: true, Enabled: true)
            : ReferenceEquals(s, activeForced) ? new TrackFlags(Default: forcedIsDefault, Enabled: true, Forced: true)
            : s.Track.Flags with
            {
                Default = false,
                Enabled = true,
                Forced = false,
                HearingImpaired = s.Role == TrackRole.HearingImpaired || (s.Role != TrackRole.Forced && s.Track.Flags.HearingImpaired),
            },
            s.Role));
    }

    /// <summary>
    /// Listed languages first (in the list's order), then the rest in the original order; commentary last. An order the
    /// user gave wins (the tracks it does not name follow in the rules' order).
    /// </summary>
    private static List<Classified> Order(
        IEnumerable<MediaTrack> tracks, Func<MediaTrack, TrackRole> role, Func<MediaTrack, LanguageTag> language,
        Func<string, int> priority, TrackDecisions decisions)
    {
        var ordered = tracks
            .Select((t, index) => new Classified(t, language(t), role(t), index))
            .OrderBy(c => c.Role == TrackRole.Commentary ? 1 : 0)
            .ThenBy(c => priority(c.Language.Primary))
            .ThenBy(c => c.Role switch { TrackRole.Forced => 0, TrackRole.Main => 1, _ => 2 })
            .ThenBy(c => c.Index)
            .ToList();
        if (decisions.Order.Count == 0)
        {
            return ordered;
        }

        // a stable sort: the tracks the user placed by his order, the others after them as the rules have them
        var position = decisions.Order.Select((id, at) => (id, at)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().at);
        return ordered.OrderBy(c => position.TryGetValue(c.Track.Id, out var at) ? at : int.MaxValue).ToList();
    }

    private static TrackRole AudioRole(MediaTrack track, RuleProfile profile) =>
        track.Flags.Commentary || Words.ContainsAny(track.Name, profile.CommentaryKeywords) ? TrackRole.Commentary : TrackRole.Main;

    private static TrackRole SubtitleRole(MediaFile file, MediaTrack track, RuleProfile profile, TrackDecisions decisions)
    {
        if (track.Flags.Commentary || Words.ContainsAny(track.Name, profile.CommentaryKeywords))
        {
            return TrackRole.Commentary;
        }

        // forced subtitles of every language are recognised – by the user's choice, the flag, the name, or their small
        // amount (owner, 30. 10. 2026); which of them are active decides SubtitleTargets
        if (IsForced(file, track, profile, decisions))
        {
            return TrackRole.Forced;
        }

        return track.Flags.HearingImpaired || Words.ContainsAny(track.Name, profile.HearingImpairedKeywords)
            ? TrackRole.HearingImpaired
            : TrackRole.Main;
    }

    /// <summary>The subtitles are forced: the user's choice, else the flag, the name or the small amount (in this order).</summary>
    public static bool IsForced(MediaFile file, MediaTrack track, RuleProfile profile, TrackDecisions? decisions = null) =>
        decisions?.Forced.TryGetValue(track.Id, out var confirmed) == true
            ? confirmed
            : track.Flags.Forced || Words.ContainsAny(track.Name, profile.ForcedKeywords) || ForcedSubtitles.ByAmount(file, track) is not null;

    /// <summary>A name already made of the required random characters is kept, so the rules are idempotent.</summary>
    private static string VideoName(MediaTrack video, RuleProfile profile)
    {
        var current = video.Name;
        if (current is not null && current.Length == profile.VideoNameLength && current.All(c => NameAlphabet.Contains(c, StringComparison.Ordinal)))
        {
            return current;
        }

        return new string(RandomNumberGenerator.GetItems<char>(NameAlphabet, profile.VideoNameLength));
    }

    private sealed record Classified(MediaTrack Track, LanguageTag Language, TrackRole Role, int Index);
}
