using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.Core.Templates;

/// <summary>
/// One expected group of tracks in a batch template, e.g. "audio, Czech, regular, required" or
/// "subtitles, Czech, forced, optional". A slot holds all tracks of that kind, so two Czech dubs both match.
/// </summary>
/// <param name="Language">Primary language subtag, e.g. "cs".</param>
public sealed record TemplateSlot(TrackType Type, string Language, TrackRole Role, bool Required)
{
    public bool Matches(TrackTarget track) =>
        track.Type == Type && track.Role == Role && string.Equals(track.Language.Primary, Language, StringComparison.OrdinalIgnoreCase);
}
