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

    /// <summary>New times of subtitles (Zmena FPS, rýchlosti, Posun – owner, 30. 10. 2026); null keeps them.</summary>
    public Subtitles.SubtitleTiming? Timing { get; init; }

    /// <summary>The times of the subtitles change.</summary>
    public bool TimingChanged => Timing is { IsNone: false };

    /// <summary>The track is added from an external file (subtitles lying with the video).</summary>
    public bool IsAdded => Source.ExternalFile is not null;

    /// <summary>The proportions the picture is shown at ("16/9"; Úprava videostopy – owner, 30. 10. 2026); null keeps them.</summary>
    public string? AspectRatio { get; init; }

    /// <summary>Text subtitles of another format written as SRT (owner, 1. 11. 2026); the original format is not kept.</summary>
    public bool ToSrt { get; init; }

    public bool HasChanges => NameChanged || LanguageChanged || FlagsChanged || TimingChanged || IsAdded || AspectRatio is not null || ToSrt;
}
