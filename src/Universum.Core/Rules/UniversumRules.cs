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
    private const string NameAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <param name="languages">Languages chosen for tracks (by track ID); they override the language in the file.</param>
    /// <param name="videoNames">Video names to reuse (by track ID), so a preview does not change on every recalculation.</param>
    public static TargetState Apply(
        MediaFile file,
        RuleProfile? profile = null,
        IReadOnlyDictionary<int, LanguageTag>? languages = null,
        IReadOnlyDictionary<int, string>? videoNames = null)
    {
        profile ??= RuleProfile.Default;
        var issues = new List<RuleIssue>();
        var language = (MediaTrack t) => languages is not null && languages.TryGetValue(t.Id, out var chosen) ? chosen : t.Language;

        var videos = file.TracksOf(TrackType.Video).ToList();
        if (videos.Count > 1)
        {
            issues.Add(new RuleIssue(RuleIssueKind.MultipleVideoTracks));
        }

        var audio = Order(file.TracksOf(TrackType.Audio), t => AudioRole(t, profile), language, profile);
        var subtitles = Order(file.TracksOf(TrackType.Subtitles), t => SubtitleRole(t, language(t), profile), language, profile);
        if (audio.Count == 0)
        {
            issues.Add(new RuleIssue(RuleIssueKind.NoAudio));
        }

        foreach (var track in audio.Concat(subtitles).Where(t => t.Language.IsUndetermined))
        {
            issues.Add(new RuleIssue(RuleIssueKind.UndeterminedLanguage, track.Track.Id));
        }

        var targets = new List<TrackTarget>();
        targets.AddRange(videos.Select(v => new TrackTarget(v, videoNames is not null && videoNames.TryGetValue(v.Id, out var name) ? name : VideoName(v, profile), v.Language, v.Flags with { Default = true, Enabled = true, Forced = false }, TrackRole.Main)));

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

        return new TargetState(file, targets, profile.RemoveTags, issues);
    }

    private static IEnumerable<TrackTarget> SubtitleTargets(
        List<Classified> audio, Classified? defaultAudio, List<Classified> subtitles, RuleProfile profile, List<RuleIssue> issues)
    {
        var preferredAudio = audio.Any(a => a.Role != TrackRole.Commentary && profile.IsPreferred(a.Language.Primary));
        Classified? defaultSubtitle;
        var defaultForced = new HashSet<Classified>();

        if (preferredAudio)
        {
            // viewers understand the audio: only forced subtitles are shown, preferably those in the audio's language
            defaultSubtitle = null;
            var forced = subtitles.Where(s => s.Role == TrackRole.Forced).ToList();
            var matching = forced.FirstOrDefault(s => defaultAudio is not null &&
                string.Equals(s.Language.Primary, defaultAudio.Language.Primary, StringComparison.OrdinalIgnoreCase));
            if ((matching ?? forced.FirstOrDefault()) is { } chosen)
            {
                defaultForced.Add(chosen);
            }
        }
        else
        {
            // foreign audio: full subtitles in the best preferred language; forced ones stay forced but not default
            defaultSubtitle = subtitles
                .Where(s => s.Role == TrackRole.Main && profile.IsPreferred(s.Language.Primary))
                .OrderBy(s => profile.Priority(s.Language.Primary))
                .FirstOrDefault();
            if (defaultSubtitle is null && subtitles.Count > 0)
            {
                issues.Add(new RuleIssue(RuleIssueKind.NoPreferredSubtitles));
            }
        }

        return subtitles.Select(s => new TrackTarget(
            s.Track, null, s.Language,
            s.Track.Flags with
            {
                Default = ReferenceEquals(s, defaultSubtitle) || defaultForced.Contains(s),
                Enabled = true,
                Forced = s.Role == TrackRole.Forced,
            },
            s.Role));
    }

    /// <summary>Preferred languages first (in profile order), then the rest in the original order; commentary last.</summary>
    private static List<Classified> Order(
        IEnumerable<MediaTrack> tracks, Func<MediaTrack, TrackRole> role, Func<MediaTrack, LanguageTag> language, RuleProfile profile) =>
        tracks
            .Select((t, index) => new Classified(t, language(t), role(t), index))
            .OrderBy(c => c.Role == TrackRole.Commentary ? 1 : 0)
            .ThenBy(c => profile.Priority(c.Language.Primary))
            .ThenBy(c => c.Role switch { TrackRole.Forced => 0, TrackRole.Main => 1, _ => 2 })
            .ThenBy(c => c.Index)
            .ToList();

    private static TrackRole AudioRole(MediaTrack track, RuleProfile profile) =>
        track.Flags.Commentary || Words.ContainsAny(track.Name, profile.CommentaryKeywords) ? TrackRole.Commentary : TrackRole.Main;

    private static TrackRole SubtitleRole(MediaTrack track, LanguageTag language, RuleProfile profile)
    {
        if (track.Flags.Commentary || Words.ContainsAny(track.Name, profile.CommentaryKeywords))
        {
            return TrackRole.Commentary;
        }

        // forced subtitles are honoured only in the preferred languages (specification: Czech and Slovak)
        if (profile.IsPreferred(language.Primary) && (track.Flags.Forced || Words.ContainsAny(track.Name, profile.ForcedKeywords)))
        {
            return TrackRole.Forced;
        }

        return track.Flags.HearingImpaired || Words.ContainsAny(track.Name, profile.HearingImpairedKeywords)
            ? TrackRole.HearingImpaired
            : TrackRole.Main;
    }

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
