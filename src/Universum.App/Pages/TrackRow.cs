using Universum.App.Localization;
using Universum.Core.Media;

namespace Universum.App.Pages;

/// <summary>Track as shown in a table.</summary>
public sealed class TrackRow(MediaTrack track)
{
    public MediaTrack Track { get; } = track;

    public int Id => Track.Id;

    public string Type => Track.Type switch
    {
        TrackType.Video => Loc.T("Video"),
        TrackType.Audio => Loc.T("Zvuk"),
        TrackType.Subtitles => Loc.T("Titulky"),
        _ => Loc.T("Iné"),
    };

    public string Codec => Track.Codec;

    public string Language
    {
        get
        {
            var language = Languages.Find(Track.Language.Primary);
            var name = Track.Language.IsUndetermined ? Loc.T("neurčený (und)") : language?.SlovakName ?? Track.Language.Primary;
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
        Track.Flags.HearingImpaired ? Loc.T("nepočujúci") : null,
        Track.Flags.VisualImpaired ? Loc.T("nevidiaci") : null,
        Track.Flags.TextDescriptions ? Loc.T("popis textom") : null,
        Track.Flags.Original ? Loc.T("pôvodný") : null,
        Track.Flags.Commentary ? Loc.T("komentár") : null,
    }.OfType<string>());

    public string Properties => Track.Type switch
    {
        TrackType.Video => Track.PixelDimensions ?? "",
        TrackType.Audio => Loc.F("{0} kan., {1} Hz", Track.AudioChannels, Track.AudioSamplingFrequency),
        TrackType.Subtitles => Track.IsTextSubtitles ? Loc.T("textové") : Loc.T("obrázkové"),
        _ => "",
    };
}
