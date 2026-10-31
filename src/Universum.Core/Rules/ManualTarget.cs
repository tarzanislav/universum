using Universum.Core.Media;

namespace Universum.Core.Rules;

/// <summary>Values the user chose for one track in the header editor.</summary>
/// <param name="Name">Track name; null or empty means no name.</param>
public sealed record TrackEdit(int TrackId, string? Name, LanguageTag Language, TrackFlags Flags);

/// <summary>
/// The target state the user put together by hand (header editor): every track with its name, language and flags, in
/// the chosen order, tracks to leave out, tags and the segment title. Nothing here is blocking – an undetermined
/// language is allowed, as in MKVToolNix; <see cref="TargetChecks"/> points out what is worth a second look.
/// </summary>
public static class ManualTarget
{
    /// <param name="tracks">All kept tracks in their new order.</param>
    /// <param name="removed">Audio and subtitle tracks left out of the result.</param>
    /// <param name="title">New segment title; null keeps it, "" removes it.</param>
    public static TargetState Create(
        MediaFile file, IReadOnlyList<TrackEdit> tracks, IReadOnlySet<int>? removed = null, bool removeTags = false, string? title = null)
    {
        removed ??= new HashSet<int>();
        var byId = file.Tracks.ToDictionary(t => t.Id);
        var listed = tracks.Select(t => t.TrackId).Concat(removed).ToList();
        if (listed.Count != file.Tracks.Count || listed.Distinct().Count() != listed.Count || listed.Any(id => !byId.ContainsKey(id)))
        {
            throw new ArgumentException("Every track of the file must be either kept (once) or removed.", nameof(tracks));
        }

        // a video track only when its picture is written anew in a track of its own (Úprava videostopy)
        if (removed.Any(id => byId[id].Type is not (TrackType.Audio or TrackType.Subtitles) &&
                              !(byId[id].Type == TrackType.Video && file.Tracks.Any(t => t.VideoEncode?.Source.Id == id))))
        {
            throw new ArgumentException("Only audio and subtitle tracks can be left out.", nameof(removed));
        }

        var targets = tracks.Select(edit =>
        {
            var source = byId[edit.TrackId];
            var name = string.IsNullOrEmpty(edit.Name) ? null : edit.Name;
            return new TrackTarget(source, name, edit.Language, edit.Flags, TrackRole.Main);
        }).ToList();
        return new TargetState(file, targets, removeTags, [])
        {
            Removed = file.Tracks.Where(t => removed.Contains(t.Id)).ToList(),
            NewTitle = title,
        };
    }

    /// <summary>The file as it is: every track unchanged, in its order.</summary>
    public static IReadOnlyList<TrackEdit> AsIs(MediaFile file) =>
        file.Tracks.Select(t => new TrackEdit(t.Id, t.Name, t.Language, t.Flags)).ToList();

    /// <summary>The values the UNIVERSUM rules give (one click in the header editor), to be edited further.</summary>
    public static IReadOnlyList<TrackEdit> From(TargetState target) =>
        target.Tracks.Select(t => new TrackEdit(t.TrackId, t.Name, t.Language, t.Flags)).ToList();
}
