using Universum.Core.Library;

namespace Universum.Core.Rules;

/// <summary>
/// Settings of the UNIVERSUM rules. <see cref="Default"/> is the profile from the specification (Czech and Slovak);
/// other users can define their own languages and keywords.
/// </summary>
public sealed record RuleProfile
{
    public static RuleProfile Default { get; } = new();

    public string Name { get; init; } = "UNIVERSUM CZ/SK";

    /// <summary>
    /// Preferred languages in priority order (primary IETF subtags). Audio and subtitle tracks in these languages
    /// come first; audio in one of them means full subtitles are not shown by default; only their subtitles may be forced.
    /// </summary>
    public IReadOnlyList<string> PreferredLanguages { get; init; } = ["cs", "sk"];

    /// <summary>
    /// Track order by language (primary subtags), e.g. ["cs", "en", "sk"] from a batch template. Empty means the
    /// order of <see cref="PreferredLanguages"/>. Languages not listed follow in their original order.
    /// </summary>
    public IReadOnlyList<string> OrderLanguages { get; init; } = [];

    /// <summary>
    /// Subtitle languages kept in the result, in this order (owner, 29. 10. 2026: CZ, SK, EN, PL, HU, RU). Subtitles in
    /// another language are left out – except those in the language of one of the audio tracks, which follow the list.
    /// Empty keeps all subtitles. A track the user keeps, a template's order and a manual order win.
    /// </summary>
    public IReadOnlyList<string> SubtitleLanguages { get; init; } = ["cs", "sk", "en", "pl", "hu", "ru"];

    /// <summary>Words in the original track name that mark forced subtitles (matched without case and diacritics).</summary>
    public IReadOnlyList<string> ForcedKeywords { get; init; } = ["forced", "vynucene", "vynutene", "nucene", "nutene"];

    /// <summary>Words marking subtitles for the deaf and hard of hearing.</summary>
    public IReadOnlyList<string> HearingImpairedKeywords { get; init; } =
        ["sdh", "cc", "hoh", "pre nepocujucich", "pro neslysici", "nepocujuci", "neslysici", "hearing impaired"];

    /// <summary>Words marking commentary tracks.</summary>
    public IReadOnlyList<string> CommentaryKeywords { get; init; } = ["commentary", "comments", "komentar", "komentare"];

    /// <summary>Length of the random video track name (letters A–Z, a–z and digits).</summary>
    public int VideoNameLength { get; init; } = 8;

    /// <summary>Remove all tags (global, track and statistics tags).</summary>
    public bool RemoveTags { get; init; } = true;

    /// <summary>Rewrite files whose video track has no index (cues), so seeking works again.</summary>
    public bool RepairIndex { get; init; } = true;

    /// <summary>Naming of results in the library (size classes, subtitles word).</summary>
    public LibraryProfile Library { get; init; } = new();

    /// <summary>Position of a language in the track order (lower first); int.MaxValue for languages not listed.</summary>
    public int Priority(string primaryLanguage) => IndexOf(OrderLanguages.Count > 0 ? OrderLanguages : PreferredLanguages, primaryLanguage);

    /// <summary>Preference among <see cref="PreferredLanguages"/> (lower is better); int.MaxValue for other languages.</summary>
    public int Preference(string primaryLanguage) => IndexOf(PreferredLanguages, primaryLanguage);

    public bool IsPreferred(string primaryLanguage) => Preference(primaryLanguage) != int.MaxValue;

    /// <summary>
    /// The order of subtitle languages for a file whose audio is in <paramref name="audioLanguages"/>: the template's
    /// order, the subtitle languages, then the audio languages. Without subtitle languages the order of the audio.
    /// </summary>
    public IReadOnlyList<string> SubtitleOrder(IEnumerable<string> audioLanguages) =>
        SubtitleLanguages.Count == 0
            ? (OrderLanguages.Count > 0 ? OrderLanguages : PreferredLanguages)
            : OrderLanguages.Concat(SubtitleLanguages).Concat(audioLanguages).Select(l => l.ToLowerInvariant()).Distinct().ToList();

    /// <summary>
    /// Subtitles in this language stay: listed among the subtitle languages or in a template's order, or the language of
    /// an audio track (always when <see cref="SubtitleLanguages"/> is empty).
    /// </summary>
    public bool KeepsSubtitles(string primaryLanguage, IEnumerable<string> audioLanguages) =>
        SubtitleLanguages.Count == 0 || IndexOf(SubtitleLanguages, primaryLanguage) != int.MaxValue ||
        IndexOf(OrderLanguages, primaryLanguage) != int.MaxValue ||
        audioLanguages.Contains(primaryLanguage, StringComparer.OrdinalIgnoreCase);

    /// <summary>Position of a language in a list (lower first); int.MaxValue when it is not listed.</summary>
    public static int PositionIn(IReadOnlyList<string> languages, string primaryLanguage) => IndexOf(languages, primaryLanguage);

    private static int IndexOf(IReadOnlyList<string> languages, string primaryLanguage)
    {
        for (var i = 0; i < languages.Count; i++)
        {
            if (string.Equals(languages[i], primaryLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
