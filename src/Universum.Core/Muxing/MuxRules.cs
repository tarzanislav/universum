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
    public static IReadOnlyList<CheckFinding> Check(IReadOnlyList<MuxSource> sources, IReadOnlyList<MuxTrack> tracks, RuleProfile? profile = null)
    {
        var (file, _) = Combined(sources, tracks);
        var keepsTags = sources.Any(s => s.KeepTags && s.File.HasTags);
        var target = ManualTarget.Create(file, file.Tracks.Select(t => new TrackEdit(t.Id, t.Name, t.Language, t.Flags)).ToList(), removeTags: !keepsTags);
        return TargetChecks.Review(target, profile);
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
