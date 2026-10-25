namespace Universum.Core.Media;

/// <summary>
/// Track language as stored in Matroska: the legacy ISO 639-2 element and the IETF BCP 47 element.
/// Either may be missing in older files; <see cref="Group"/> and <see cref="Primary"/> use whichever is present.
/// </summary>
public readonly record struct LanguageTag(string Legacy, string? Ietf)
{
    public static LanguageTag Undetermined { get; } = new("und", "und");

    /// <summary>Builds a tag from one code, e.g. "cs", "cze", "ces" or "cs-CZ".</summary>
    public static LanguageTag FromCode(string code)
    {
        var primary = PrimarySubtag(code);
        var language = Languages.Find(primary);
        return language is null
            ? new LanguageTag(primary.Length == 3 ? primary.ToLowerInvariant() : "und", code)
            : new LanguageTag(language.Bibliographic, code.Contains('-') ? code : language.Ietf);
    }

    /// <summary>Primary language subtag (lower case), preferring the IETF element, e.g. "cs".</summary>
    public string Primary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Ietf) && !IsUnd(PrimarySubtag(Ietf)))
            {
                return PrimarySubtag(Ietf).ToLowerInvariant();
            }

            var legacy = Languages.Find(Legacy);
            return legacy?.Ietf ?? (string.IsNullOrWhiteSpace(Legacy) ? "und" : Legacy.Trim().ToLowerInvariant());
        }
    }

    public bool IsUndetermined => IsUnd(Primary);

    public LanguageGroup Group => Primary switch
    {
        "cs" => LanguageGroup.Czech,
        "sk" => LanguageGroup.Slovak,
        _ when IsUndetermined => LanguageGroup.Undetermined,
        _ => LanguageGroup.Other,
    };

    public bool IsCzechOrSlovak => Group is LanguageGroup.Czech or LanguageGroup.Slovak;

    public override string ToString() => Ietf is null || Ietf == Legacy ? Legacy : $"{Legacy} / {Ietf}";

    private static string PrimarySubtag(string code)
    {
        var trimmed = code.Trim();
        var dash = trimmed.IndexOfAny(['-', '_']);
        return dash < 0 ? trimmed : trimmed[..dash];
    }

    private static bool IsUnd(string code) => code.Length == 0 || code.Equals("und", StringComparison.OrdinalIgnoreCase);
}
