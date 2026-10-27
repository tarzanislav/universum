namespace Universum.Core.Media;

/// <summary>Track header flags. Defaults follow the Matroska specification (default and enabled are on).</summary>
public sealed record TrackFlags(
    bool Default = true,
    bool Enabled = true,
    bool Forced = false,
    bool HearingImpaired = false,
    bool VisualImpaired = false,
    bool Commentary = false,
    bool Original = false,
    bool TextDescriptions = false);
