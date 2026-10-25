namespace Universum.Core.Rules;

public enum RuleIssueKind
{
    /// <summary>A track has no language and none was supplied; blocks automatic processing.</summary>
    UndeterminedLanguage,

    /// <summary>More than one video track; the file needs a human decision.</summary>
    MultipleVideoTracks,

    /// <summary>The file has no audio track; the file needs a human decision.</summary>
    NoAudio,

    /// <summary>No audio in a preferred language and no subtitles in one either: nothing is shown by default.</summary>
    NoPreferredSubtitles,

    /// <summary>The video track has no index (cues): seeking is slow or broken; a rewrite repairs it.</summary>
    MissingIndex,

    /// <summary>mkvmerge reported warnings when reading the file (damaged or unusual file).</summary>
    ContainerWarnings,
}

/// <param name="TrackId">The track concerned, null for the whole file.</param>
public sealed record RuleIssue(RuleIssueKind Kind, int? TrackId = null)
{
    /// <summary>Blocking issues stop automatic processing; the file goes to manual review.</summary>
    public bool IsBlocking => Kind is RuleIssueKind.UndeterminedLanguage or RuleIssueKind.MultipleVideoTracks or RuleIssueKind.NoAudio;
}
