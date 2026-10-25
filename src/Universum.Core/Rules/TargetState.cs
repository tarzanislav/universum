using Universum.Core.Media;

namespace Universum.Core.Rules;

/// <summary>Result of the rules for one file: all tracks in their final order and the file-level changes.</summary>
public sealed record TargetState(MediaFile Source, IReadOnlyList<TrackTarget> Tracks, bool RemoveTags, IReadOnlyList<RuleIssue> Issues)
{
    /// <summary>The file is rewritten to create a missing index (cues) for the video.</summary>
    public bool RepairIndex { get; init; }

    /// <summary>Tracks left out of the result (only audio and subtitles can be removed).</summary>
    public IReadOnlyList<MediaTrack> Removed { get; init; } = [];

    /// <summary>True when the track order differs from the file (requires writing a new file).</summary>
    public bool OrderChanged => !Tracks.Select(t => t.TrackId).SequenceEqual(Source.Tracks.Select(t => t.Id));

    public bool TagsChanged => RemoveTags && Source.HasTags;

    public bool HasChanges => OrderChanged || RepairIndex || TagsChanged || Removed.Count > 0 || Tracks.Any(t => t.HasChanges);

    /// <summary>A new file must be written (order, removed tracks, index repair); otherwise a header edit is enough.</summary>
    public bool NeedsRewrite => OrderChanged || RepairIndex;

    public bool IsBlocked => Issues.Any(i => i.IsBlocking);

    public TrackTarget For(int trackId) => Tracks.First(t => t.TrackId == trackId);
}
