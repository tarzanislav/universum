using Universum.Core.Media;

namespace Universum.Core.Rules;

/// <summary>Desired state of one track after applying the rules.</summary>
/// <param name="Source">The track as it is in the file now.</param>
/// <param name="Name">Desired name; null means no name.</param>
public sealed record TrackTarget(MediaTrack Source, string? Name, LanguageTag Language, TrackFlags Flags, TrackRole Role)
{
    public int TrackId => Source.Id;

    public TrackType Type => Source.Type;

    public bool NameChanged => !string.Equals(Source.Name ?? "", Name ?? "", StringComparison.Ordinal);

    public bool LanguageChanged => !string.Equals(Source.Language.Primary, Language.Primary, StringComparison.OrdinalIgnoreCase);

    public bool FlagsChanged => Source.Flags != Flags;

    /// <summary>A flag other than default, enabled and forced changes (hearing impaired, original, commentary …).</summary>
    public bool OtherFlagsChanged =>
        (Source.Flags with { Default = false, Enabled = false, Forced = false }) != (Flags with { Default = false, Enabled = false, Forced = false });

    public bool HasChanges => NameChanged || LanguageChanged || FlagsChanged;
}
