using Universum.Core.Chapters;
using Universum.Core.Media;

namespace Universum.Core.Rules;

/// <summary>Result of the rules for one file: all tracks in their final order and the file-level changes.</summary>
public sealed record TargetState(MediaFile Source, IReadOnlyList<TrackTarget> Tracks, bool RemoveTags, IReadOnlyList<RuleIssue> Issues)
{
    /// <summary>The file is rewritten to create a missing index (cues) for the video.</summary>
    public bool RepairIndex { get; init; }

    /// <summary>Tracks left out of the result (only audio and subtitles can be removed).</summary>
    public IReadOnlyList<MediaTrack> Removed { get; init; } = [];

    /// <summary>New segment title: null keeps the title of the file, "" removes it.</summary>
    public string? NewTitle { get; init; }

    /// <summary>New chapters (chapter editor): null keeps the file's chapters, an empty list removes them.</summary>
    public IReadOnlyList<Chapter>? NewChapters { get; init; }

    /// <summary>The chapters are replaced.</summary>
    public bool ChaptersChanged => NewChapters is not null;

    /// <summary>Number of chapter entries the result has.</summary>
    public int ResultChapterEntries => NewChapters?.Count ?? Source.ChapterEntries;

    /// <summary>The segment title changes (header editor).</summary>
    public bool TitleChanged => NewTitle is not null && !string.Equals(NewTitle, Source.Title ?? "", StringComparison.Ordinal);

    /// <summary>The segment title the result has.</summary>
    public string? ResultTitle => TitleChanged ? (NewTitle!.Length == 0 ? null : NewTitle) : Source.Title;

    /// <summary>True when the track order differs from the file (requires writing a new file).</summary>
    public bool OrderChanged => !Tracks.Select(t => t.TrackId).SequenceEqual(Source.Tracks.Select(t => t.Id));

    public bool TagsChanged => RemoveTags && Source.HasTags;

    /// <summary>
    /// The file is not Matroska (MP4, AVI, TS…): the result is always a new MKV written by mkvmerge – never a header edit
    /// and never the file copied as it is (owner, 27. 10. 2026: batch processing converts every video format it can).
    /// </summary>
    public bool ContainerChanged => !Source.IsMatroska;

    public bool HasChanges => ContainerChanged || OrderChanged || RepairIndex || TagsChanged || TitleChanged || ChaptersChanged || Removed.Count > 0 ||
                              Tracks.Any(t => t.HasChanges);

    /// <summary>A new file must be written (another container, order, removed tracks, index repair); otherwise a header edit is enough.</summary>
    public bool NeedsRewrite => ContainerChanged || OrderChanged || RepairIndex;

    public bool IsBlocked => Issues.Any(i => i.IsBlocking);

    public TrackTarget For(int trackId) => Tracks.First(t => t.TrackId == trackId);
}
