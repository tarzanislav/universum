using Universum.Core.Media;

namespace Universum.App.Pages;

/// <summary>Track as shown in a table.</summary>
public sealed class TrackRow(MediaTrack track)
{
    public MediaTrack Track { get; } = track;

    public int Id => Track.Id;

    public string Type => Track.Type switch
    {
        TrackType.Video => "Video",
        TrackType.Audio => "Zvuk",
        TrackType.Subtitles => "Titulky",
        _ => "Iné",
    };

    public string Codec => Track.Codec;

    public string Language
    {
        get
        {
            var language = Languages.Find(Track.Language.Primary);
            var name = Track.Language.IsUndetermined ? "neurčený (und)" : language?.SlovakName ?? Track.Language.Primary;
            return $"{name} – {Track.Language}";
        }
    }

    public string Name => Track.Name ?? "";

    public bool IsDefault => Track.Flags.Default;

    public bool IsEnabled => Track.Flags.Enabled;

    public bool IsForced => Track.Flags.Forced;

    /// <summary>For the colours of the track types.</summary>
    public TrackType Kind => Track.Type;

    /// <summary>The less common flags: "nepočujúci, komentár".</summary>
    public string FlagMarks => string.Join(", ", new[]
    {
        Track.Flags.HearingImpaired ? "nepočujúci" : null,
        Track.Flags.VisualImpaired ? "nevidiaci" : null,
        Track.Flags.TextDescriptions ? "popis textom" : null,
        Track.Flags.Original ? "pôvodný" : null,
        Track.Flags.Commentary ? "komentár" : null,
    }.OfType<string>());

    public string Properties => Track.Type switch
    {
        TrackType.Video => Track.PixelDimensions ?? "",
        TrackType.Audio => $"{Track.AudioChannels} kan., {Track.AudioSamplingFrequency} Hz",
        TrackType.Subtitles => Track.IsTextSubtitles ? "textové" : "obrázkové",
        _ => "",
    };
}
