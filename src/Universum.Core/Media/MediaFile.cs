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

    /// <summary>
    /// Matroska (also WebM): its header can be edited in place. Any other container (MP4, AVI, TS…) is always written
    /// as a new MKV by mkvmerge.
    /// </summary>
    public bool IsMatroska => ContainerType == "Matroska";

    /// <summary>
    /// "MKV", "MP4", "AVI", "TS", "M2TS" – the container as people call it (for messages). Taken from the extension: the
    /// container type mkvmerge reports changes between versions ("QuickTime/MP4" in 82, "MP4/QuickTime" in 103).
    /// </summary>
    public string ContainerName => IsMatroska
        ? "MKV"
        : System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } extension ? extension : ContainerType;

    public bool HasChapters => ChapterEntries > 0;

    public bool HasTags => GlobalTagEntries > 0 || Tracks.Any(t => t.TagEntries > 0);

    public IEnumerable<MediaTrack> TracksOf(TrackType type) => Tracks.Where(t => t.Type == type);
}
