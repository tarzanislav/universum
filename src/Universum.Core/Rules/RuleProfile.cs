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

    /// <summary>Position of a language in the track order (lower first); int.MaxValue for languages not listed.</summary>
    public int Priority(string primaryLanguage) => IndexOf(OrderLanguages.Count > 0 ? OrderLanguages : PreferredLanguages, primaryLanguage);

    /// <summary>Preference among <see cref="PreferredLanguages"/> (lower is better); int.MaxValue for other languages.</summary>
    public int Preference(string primaryLanguage) => IndexOf(PreferredLanguages, primaryLanguage);

    public bool IsPreferred(string primaryLanguage) => Preference(primaryLanguage) != int.MaxValue;

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
