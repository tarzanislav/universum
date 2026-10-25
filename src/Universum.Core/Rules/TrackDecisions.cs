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

    /// <summary>Video names to reuse, so a preview does not change on every recalculation.</summary>
    public IReadOnlyDictionary<int, string> VideoNames { get; init; } = new Dictionary<int, string>();
}
