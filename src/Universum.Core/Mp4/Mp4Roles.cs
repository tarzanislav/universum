using Universum.Core.Media;

namespace Universum.Core.Mp4;

/// <summary>
/// What a track is for, in an MP4 (owner, 8. 11. 2026: forced subtitles, subtitles for the hearing impaired and
/// commentary kept in an MP4 that stays MP4): 'kind' boxes in the track's 'udta' with the roles of MPEG-DASH
/// (urn:mpeg:dash:role:2011) – the way FFmpeg writes and reads them, so ffprobe and the media servers built on it (Jellyfin)
/// see them. MKVToolNix does not read them; UNIVERSUM adds them to what it reads of an MP4 (<see cref="Mp4Reading"/>).
/// </summary>
public static class Mp4Roles
{
    public const string Scheme = "urn:mpeg:dash:role:2011";

    public const string Forced = "forced-subtitle";

    /// <summary>Subtitles for the hearing impaired (FFmpeg: hearing impaired and captions).</summary>
    public const string Caption = "caption";

    public const string Commentary = "commentary";

    /// <summary>For the visually impaired (FFmpeg: visual impaired and descriptions).</summary>
    public const string Description = "description";

    /// <summary>The roles UNIVERSUM sets and takes away; other roles of the scheme ("dub", "main" …) stay as they are.</summary>
    public static readonly IReadOnlySet<string> Managed = new HashSet<string>(StringComparer.Ordinal) { Forced, Caption, Commentary, Description };

    /// <summary>The roles UNIVERSUM manages that a track has, in the fixed order.</summary>
    public static IReadOnlyList<string> ManagedOf(IEnumerable<string> roles)
    {
        var set = roles.ToHashSet(StringComparer.Ordinal);
        return [.. new[] { Forced, Caption, Commentary, Description }.Where(set.Contains)];
    }

    /// <summary>The roles of flags, in a fixed order (empty: none).</summary>
    public static IReadOnlyList<string> Of(TrackFlags flags) =>
    [
        .. flags.Forced ? [Forced] : Array.Empty<string>(),
        .. flags.HearingImpaired ? [Caption] : Array.Empty<string>(),
        .. flags.Commentary ? [Commentary] : Array.Empty<string>(),
        .. flags.VisualImpaired ? [Description] : Array.Empty<string>(),
    ];

    /// <summary>The flags with what the roles say (the others as they are).</summary>
    public static TrackFlags Apply(TrackFlags flags, IReadOnlyCollection<string> roles) => flags with
    {
        Forced = roles.Contains(Forced),
        HearingImpaired = roles.Contains(Caption),
        Commentary = roles.Contains(Commentary),
        VisualImpaired = roles.Contains(Description),
    };

    /// <summary>
    /// The flags an MP4 can hold besides the default one: forced, hearing impaired, commentary, visual impaired. The text
    /// descriptions and original flags it cannot.
    /// </summary>
    public static bool CanHold(TrackFlags before, TrackFlags after) => before.TextDescriptions == after.TextDescriptions && before.Original == after.Original;
}
