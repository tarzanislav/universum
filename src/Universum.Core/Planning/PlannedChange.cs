namespace Universum.Core.Planning;

public enum ChangeKind
{
    Name,
    Language,
    Default,
    Enabled,
    Forced,

    /// <summary>Hearing impaired, visual impaired, text descriptions, original or commentary flag.</summary>
    OtherFlags,
    Position,
    Removed,
    Index,
    Tags,
    Title,
    Chapters,

    /// <summary>MP4, AVI, TS… become MKV.</summary>
    Container,
}

/// <summary>One visible difference between the file and the target, for previews and reports.</summary>
/// <param name="TrackId">Track concerned; null for file-level changes (tags).</param>
public sealed record PlannedChange(int? TrackId, ChangeKind Kind, string? Before, string? After);
