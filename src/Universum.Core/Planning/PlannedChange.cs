namespace Universum.Core.Planning;

public enum ChangeKind
{
    Name,
    Language,
    Default,
    Enabled,
    Forced,
    Position,
    Removed,
    Tags,
}

/// <summary>One visible difference between the file and the target, for previews and reports.</summary>
/// <param name="TrackId">Track concerned; null for file-level changes (tags).</param>
public sealed record PlannedChange(int? TrackId, ChangeKind Kind, string? Before, string? After);
