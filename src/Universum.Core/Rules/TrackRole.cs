namespace Universum.Core.Rules;

/// <summary>What a track is for, as recognised by the rules.</summary>
public enum TrackRole
{
    /// <summary>Video, regular audio or full subtitles.</summary>
    Main,

    /// <summary>Subtitles shown only for foreign-language passages.</summary>
    Forced,

    /// <summary>Subtitles for the deaf and hard of hearing (SDH, CC).</summary>
    HearingImpaired,

    /// <summary>Commentary audio or subtitles; never default, placed last.</summary>
    Commentary,
}
