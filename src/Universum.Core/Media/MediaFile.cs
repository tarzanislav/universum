namespace Universum.Core.Media;

/// <summary>Structure of a media file: container data, tracks, chapters, tags and attachments.</summary>
public sealed record MediaFile
{
    public required string Path { get; init; }

    public string ContainerType { get; init; } = "";

    public bool IsRecognized { get; init; }

    public bool IsSupported { get; init; }

    /// <summary>Segment title; null when absent.</summary>
    public string? Title { get; init; }

    public TimeSpan? Duration { get; init; }

    public string? WritingApplication { get; init; }

    public IReadOnlyList<MediaTrack> Tracks { get; init; } = [];

    public int ChapterEntries { get; init; }

    public int GlobalTagEntries { get; init; }

    public int AttachmentCount { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Version of the backend's identification format (mkvmerge JSON schema).</summary>
    public int? IdentificationFormatVersion { get; init; }

    public bool HasChapters => ChapterEntries > 0;

    public bool HasTags => GlobalTagEntries > 0 || Tracks.Any(t => t.TagEntries > 0);

    public IEnumerable<MediaTrack> TracksOf(TrackType type) => Tracks.Where(t => t.Type == type);
}
