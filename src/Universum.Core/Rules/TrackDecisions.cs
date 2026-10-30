using Universum.Core.Media;

namespace Universum.Core.Rules;

/// <summary>
/// Decisions about individual tracks that the file itself does not contain: languages of undetermined tracks,
/// confirmed forced subtitles, tracks to remove and video names to reuse. Keys are track IDs.
/// </summary>
public sealed record TrackDecisions
{
    public static TrackDecisions None { get; } = new();

    public IReadOnlyDictionary<int, LanguageTag> Languages { get; init; } = new Dictionary<int, LanguageTag>();

    /// <summary>Confirmed forced state of subtitle tracks (true = forced, false = not forced), overriding flags and names.</summary>
    public IReadOnlyDictionary<int, bool> Forced { get; init; } = new Dictionary<int, bool>();

    /// <summary>Audio and subtitle tracks left out of the result.</summary>
    public IReadOnlySet<int> Removed { get; init; } = new HashSet<int>();

    /// <summary>
    /// Subtitle tracks kept although their language is not among the profile's subtitle languages (the user's choice).
    /// </summary>
    public IReadOnlySet<int> Kept { get; init; } = new HashSet<int>();

    /// <summary>
    /// The order the user gave the tracks (owner, 29. 10. 2026: tracks dragged up or down); tracks not listed follow in
    /// the rules' order. Audio stays before subtitles.
    /// </summary>
    public IReadOnlyList<int> Order { get; init; } = [];

    /// <summary>New times of subtitle tracks (Úprava titulkostopy: Zmena FPS, rýchlosti, Posun – owner, 30. 10. 2026).</summary>
    public IReadOnlyDictionary<int, Subtitles.SubtitleTiming> Timings { get; init; } = new Dictionary<int, Subtitles.SubtitleTiming>();

    /// <summary>Video names to reuse, so a preview does not change on every recalculation.</summary>
    public IReadOnlyDictionary<int, string> VideoNames { get; init; } = new Dictionary<int, string>();
}
