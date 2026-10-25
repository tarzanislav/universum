namespace Universum.Core.Media;

/// <summary>
/// Built-in table of common languages. The complete list comes from the installed MKVToolNix
/// (<c>mkvmerge --list-languages</c>); this table covers lookups that must work without it.
/// </summary>
public static class Languages
{
    public static Language Czech { get; } = new("cs", "cze", "ces", "Czech", "čeština");

    public static Language Slovak { get; } = new("sk", "slo", "slk", "Slovak", "slovenčina");

    public static Language English { get; } = new("en", "eng", null, "English", "angličtina");

    public static Language Undetermined { get; } = new("und", "und", null, "Undetermined", "neurčený");

    public static IReadOnlyList<Language> All { get; } =
    [
        Czech,
        Slovak,
        English,
        new("de", "ger", "deu", "German", "nemčina"),
        new("fr", "fre", "fra", "French", "francúzština"),
        new("es", "spa", null, "Spanish", "španielčina"),
        new("it", "ita", null, "Italian", "taliančina"),
        new("pl", "pol", null, "Polish", "poľština"),
        new("hu", "hun", null, "Hungarian", "maďarčina"),
        new("ru", "rus", null, "Russian", "ruština"),
        new("uk", "ukr", null, "Ukrainian", "ukrajinčina"),
        new("pt", "por", null, "Portuguese", "portugalčina"),
        new("nl", "dut", "nld", "Dutch", "holandčina"),
        new("sv", "swe", null, "Swedish", "švédčina"),
        new("da", "dan", null, "Danish", "dánčina"),
        new("no", "nor", null, "Norwegian", "nórčina"),
        new("fi", "fin", null, "Finnish", "fínčina"),
        new("hr", "hrv", null, "Croatian", "chorvátčina"),
        new("sr", "srp", null, "Serbian", "srbčina"),
        new("sl", "slv", null, "Slovenian", "slovinčina"),
        new("bg", "bul", null, "Bulgarian", "bulharčina"),
        new("ro", "rum", "ron", "Romanian", "rumunčina"),
        new("el", "gre", "ell", "Greek", "gréčtina"),
        new("tr", "tur", null, "Turkish", "turečtina"),
        new("he", "heb", null, "Hebrew", "hebrejčina"),
        new("ar", "ara", null, "Arabic", "arabčina"),
        new("hi", "hin", null, "Hindi", "hindčina"),
        new("ja", "jpn", null, "Japanese", "japončina"),
        new("ko", "kor", null, "Korean", "kórejčina"),
        new("zh", "chi", "zho", "Chinese", "čínština"),
        new("th", "tha", null, "Thai", "thajčina"),
        new("vi", "vie", null, "Vietnamese", "vietnamčina"),
        new("lt", "lit", null, "Lithuanian", "litovčina"),
        new("lv", "lav", null, "Latvian", "lotyština"),
        new("et", "est", null, "Estonian", "estónčina"),
        new("ca", "cat", null, "Catalan", "katalánčina"),
        Undetermined,
    ];

    private static readonly Dictionary<string, Language> ByCode = BuildIndex();

    /// <summary>Finds a language by ISO 639-1, 639-2/B or 639-2/T code (case-insensitive).</summary>
    public static Language? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return ByCode.TryGetValue(code.Trim(), out var language) ? language : null;
    }

    private static Dictionary<string, Language> BuildIndex()
    {
        var index = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in All)
        {
            index.TryAdd(language.Ietf, language);
            index.TryAdd(language.Bibliographic, language);
            if (language.Terminology is not null)
            {
                index.TryAdd(language.Terminology, language);
            }
        }

        return index;
    }
}
