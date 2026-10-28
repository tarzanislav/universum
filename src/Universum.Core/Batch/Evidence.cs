using Universum.Core.Media;

namespace Universum.Core.Batch;

/// <summary>How sure UNIVERSUM is about a track language (specification, section 7.3).</summary>
public enum Confidence
{
    /// <summary>A suggestion that must be confirmed by the user before it is used.</summary>
    Medium,

    High,

    /// <summary>Chosen or confirmed by the user.</summary>
    Confirmed,
}

public enum EvidenceSource
{
    /// <summary>Language stored in the file.</summary>
    File,

    /// <summary>Language named in the original track name.</summary>
    TrackName,

    /// <summary>Decision of the user for the whole group.</summary>
    GroupDecision,

    /// <summary>Same kind of track (codec, channels, sampling rate, name) with a known language in another file.</summary>
    Similarity,

    /// <summary>Language recognised from the text of the subtitles.</summary>
    SubtitleText,

    /// <summary>Language recognised from the speech of an audio track (whisper.cpp).</summary>
    Speech,
}

public sealed record LanguageEvidence(LanguageTag Language, Confidence Confidence, EvidenceSource Source)
{
    /// <summary>What the user should know about the suggestion ("čeština alebo slovenčina – overte"), or null.</summary>
    public string? Detail { get; init; }

    /// <summary>A doubtful suggestion: confirmed only by choosing the language at the track, never with the others at once.</summary>
    public bool NeedsOwnConfirmation { get; init; }

    /// <summary>Only high and confirmed evidence is used for writing; medium evidence is a suggestion.</summary>
    public bool IsUsable => Confidence >= Confidence.High;
}
