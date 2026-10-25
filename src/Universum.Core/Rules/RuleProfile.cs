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

    /// <summary>Priority of a language: its index in <see cref="PreferredLanguages"/>, or int.MaxValue for others.</summary>
    public int Priority(string primaryLanguage)
    {
        for (var i = 0; i < PreferredLanguages.Count; i++)
        {
            if (string.Equals(PreferredLanguages[i], primaryLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    public bool IsPreferred(string primaryLanguage) => Priority(primaryLanguage) != int.MaxValue;
}
