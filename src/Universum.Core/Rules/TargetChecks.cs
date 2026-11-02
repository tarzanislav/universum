using Universum.Core.Media;
using Universum.Core.Text;

namespace Universum.Core.Rules;

public enum CheckKind
{
    /// <summary>An audio or subtitle track has no language (und).</summary>
    UndeterminedLanguage,

    /// <summary>More than one track of one kind is marked default; players choose unpredictably.</summary>
    SeveralDefaults,

    /// <summary>A kind of track has tracks but none of them is enabled.</summary>
    NoneEnabled,

    /// <summary>Forced subtitles that are not default while no subtitles are default: most players never show them.</summary>
    ForcedWithoutDefault,

    /// <summary>The result still has tags (titles, statistics, encoder notes).</summary>
    TagsRemain,

    /// <summary>Audio or subtitles are not in the order of the preferred languages (CZ → SK → others; commentary last).</summary>
    OrderNotPreferred,

    /// <summary>
    /// No audio in Czech or Slovak, but the Czech (else Slovak) subtitles are not Default and Enabled (owner, 2. 11. 2026:
    /// always so with foreign audio).
    /// </summary>
    ForeignAudioWithoutSubtitles,
}

/// <param name="TrackId">Track concerned; null for the whole file.</param>
/// <param name="Type">Kind of tracks concerned (several defaults, none enabled, order).</param>
public sealed record CheckFinding(CheckKind Kind, int? TrackId = null, TrackType? Type = null);

/// <summary>
/// The Check panel (specification, section 5, point 2): what is worth a second look in a target state. Findings are
/// advice, never blocking.
/// </summary>
public static class TargetChecks
{
    public static IReadOnlyList<CheckFinding> Review(TargetState target, RuleProfile? profile = null)
    {
        profile ??= RuleProfile.Default;
        var findings = new List<CheckFinding>();
        foreach (var track in target.Tracks.Where(t => t.Type is TrackType.Audio or TrackType.Subtitles && t.Language.IsUndetermined))
        {
            findings.Add(new CheckFinding(CheckKind.UndeterminedLanguage, track.TrackId));
        }

        foreach (var type in new[] { TrackType.Video, TrackType.Audio, TrackType.Subtitles })
        {
            var ofType = target.Tracks.Where(t => t.Type == type).ToList();
            if (ofType.Count(t => t.Flags.Default) > 1)
            {
                findings.Add(new CheckFinding(CheckKind.SeveralDefaults, Type: type));
            }

            if (type != TrackType.Subtitles && ofType.Count > 0 && !ofType.Any(t => t.Flags.Enabled))
            {
                findings.Add(new CheckFinding(CheckKind.NoneEnabled, Type: type));
            }

            if (type != TrackType.Video && !InPreferredOrder(ofType, profile))
            {
                findings.Add(new CheckFinding(CheckKind.OrderNotPreferred, Type: type));
            }
        }

        // owner, 2. 11. 2026: with foreign audio only, the Czech subtitles – else the Slovak ones – are Default and Enabled
        if (ForeignAudioSubtitles(target, profile) is { } wanted && !(wanted.Flags.Default && wanted.Flags.Enabled))
        {
            findings.Add(new CheckFinding(CheckKind.ForeignAudioWithoutSubtitles, wanted.TrackId));
        }

        // forced subtitles that are not default show only when another subtitle track is (foreign audio: full subtitles)
        var anyDefaultSubtitle = target.Tracks.Any(t => t.Type == TrackType.Subtitles && t.Flags.Default);
        foreach (var track in target.Tracks.Where(t => t.Type == TrackType.Subtitles && t.Flags.Forced && !t.Flags.Default && !anyDefaultSubtitle))
        {
            findings.Add(new CheckFinding(CheckKind.ForcedWithoutDefault, track.TrackId));
        }

        if (target.Source.HasTags && !target.RemoveTags)
        {
            findings.Add(new CheckFinding(CheckKind.TagsRemain));
        }

        return findings;
    }

    /// <summary>
    /// With no audio in a preferred language (commentary aside): the subtitles that must be Default and Enabled – the full
    /// ones in the best preferred language (Czech, else Slovak), else those for the hearing impaired; null otherwise.
    /// </summary>
    public static TrackTarget? ForeignAudioSubtitles(TargetState target, RuleProfile? profile = null)
    {
        profile ??= RuleProfile.Default;
        var audio = target.Tracks.Where(t => t.Type == TrackType.Audio && !IsCommentary(t, profile)).ToList();
        if (audio.Count == 0 || audio.Any(a => profile.IsPreferred(a.Language.Primary)))
        {
            return null;
        }

        return target.Tracks
            .Where(t => t.Type == TrackType.Subtitles && profile.IsPreferred(t.Language.Primary) && !t.Flags.Forced && !IsCommentary(t, profile))
            .OrderBy(t => profile.Preference(t.Language.Primary))
            .ThenBy(t => t.Flags.HearingImpaired ? 1 : 0)
            .FirstOrDefault();
    }

    /// <summary>Preferred languages first in profile order, the rest after them; commentary tracks last.</summary>
    private static bool InPreferredOrder(List<TrackTarget> tracks, RuleProfile profile)
    {
        var keys = tracks.Select(t => (Commentary: IsCommentary(t, profile) ? 1 : 0, Priority: profile.Priority(t.Language.Primary))).ToList();
        for (var i = 1; i < keys.Count; i++)
        {
            if (keys[i].CompareTo(keys[i - 1]) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCommentary(TrackTarget track, RuleProfile profile) =>
        track.Role == TrackRole.Commentary || track.Source.Flags.Commentary || track.Flags.Commentary ||
        Words.ContainsAny(track.Name ?? track.Source.Name, profile.CommentaryKeywords);
}
