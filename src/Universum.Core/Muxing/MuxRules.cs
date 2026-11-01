using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.Core.Muxing;

/// <summary>
/// The UNIVERSUM rules and the Check panel for the multiplexer: the chosen tracks of all inputs are looked at as one
/// file (with the languages, names and flags set so far), and the result is turned back into mux tracks.
/// </summary>
public static class MuxRules
{
    /// <summary>The tracks in the order and with the values the rules give.</summary>
    public static IReadOnlyList<MuxTrack> Apply(IReadOnlyList<MuxSource> sources, IReadOnlyList<MuxTrack> tracks, RuleProfile? profile = null)
    {
        var (file, map) = Combined(sources, tracks);
        var target = UniversumRules.Apply(file, profile);
        return target.Tracks.Select(t => map[t.TrackId] with { Name = t.Name, Language = t.Language, Flags = t.Flags }).ToList();
    }

    /// <summary>The Check panel's findings; track IDs are positions in <paramref name="tracks"/>.</summary>
    public static IReadOnlyList<CheckFinding> Check(IReadOnlyList<MuxSource> sources, IReadOnlyList<MuxTrack> tracks, RuleProfile? profile = null) =>
        TargetChecks.Review(Target(sources, tracks), profile);

    /// <summary>The output as one target state (languages, quality, codec: the name in the library).</summary>
    public static TargetState Target(IReadOnlyList<MuxSource> sources, IReadOnlyList<MuxTrack> tracks)
    {
        var (file, _) = Combined(sources, tracks);
        var keepsTags = sources.Any(s => s.KeepTags && s.File.HasTags);
        return ManualTarget.Create(file, file.Tracks.Select(t => new TrackEdit(t.Id, t.Name, t.Language, t.Flags)).ToList(), removeTags: !keepsTags);
    }

    /// <summary>
    /// The tracks of one type in the order of the rules (owner, 1. 11. 2026: in Zlučovanie the order follows a language as
    /// soon as it is set – CZ at the top, SK under it): the profile's languages first in its order (subtitles: its order of
    /// subtitles, then the languages of the audio), forced before the full ones of a language, commentary last, otherwise
    /// as they are. The positions in <paramref name="tracks"/> of the tracks of the type, in their new order.
    /// </summary>
    public static IReadOnlyList<int> OrderOfType(IReadOnlyList<MuxSource> sources, IReadOnlyList<MuxTrack> tracks, TrackType type, RuleProfile? profile = null)
    {
        profile ??= RuleProfile.Default;
        var kinds = tracks.Select(t => sources[t.Source].File.Tracks.FirstOrDefault(s => s.Id == t.TrackId)).ToList();
        var audio = tracks.Where((_, i) => kinds[i]?.Type == TrackType.Audio && !tracks[i].Language.IsUndetermined).Select(t => t.Language.Primary).ToList();
        var subtitleOrder = profile.SubtitleOrder(audio);
        int Priority(MuxTrack track) => track.Language.IsUndetermined
            ? int.MaxValue
            : type == TrackType.Subtitles ? RuleProfile.PositionIn(subtitleOrder, track.Language.Primary) : profile.Priority(track.Language.Primary);
        bool Commentary(int i) => tracks[i].Flags.Commentary || Text.Words.ContainsAny(tracks[i].Name ?? kinds[i]?.Name, profile.CommentaryKeywords);

        return Enumerable.Range(0, tracks.Count)
            .Where(i => kinds[i]?.Type == type)
            .OrderBy(i => Commentary(i) ? 1 : 0)
            .ThenBy(i => Priority(tracks[i]))
            .ThenBy(i => tracks[i].Flags.Forced ? 0 : 1)
            .ThenBy(i => i)
            .ToList();
    }

    /// <summary>One file made of the chosen tracks: ID = position, values = the ones chosen so far.</summary>
    private static (MediaFile File, Dictionary<int, MuxTrack> Map) Combined(IReadOnlyList<MuxSource> sources, IReadOnlyList<MuxTrack> tracks)
    {
        var map = new Dictionary<int, MuxTrack>();
        var combined = new List<MediaTrack>();
        for (var i = 0; i < tracks.Count; i++)
        {
            var chosen = tracks[i];
            var source = sources[chosen.Source].File.Tracks.First(t => t.Id == chosen.TrackId);
            map[i] = chosen;
            combined.Add(source with
            {
                Id = i,
                Number = i + 1,
                Name = string.IsNullOrEmpty(chosen.Name) ? null : chosen.Name,
                Language = chosen.Language,
                Flags = chosen.Flags,
                TagEntries = 0,
            });
        }

        var file = new MediaFile
        {
            Path = sources.Count > 0 ? sources[0].File.Path : "",
            ContainerType = "Matroska",
            Tracks = combined,
            GlobalTagEntries = sources.Any(s => s.KeepTags && s.File.HasTags) ? 1 : 0,
        };
        return (file, map);
    }
}
