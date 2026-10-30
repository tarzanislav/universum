namespace Universum.Core.Media;

/// <summary>
/// The languages UNIVERSUM offers: the common ones (Slovak names, always at the top of every list – owner, 30. 10. 2026)
/// and every other language MKVToolNix knows (<c>mkvmerge --list-languages</c>: ISO 639-1, 639-2 and 639-3, e.g.
/// Filipino "fil"), added by <see cref="Extend"/> once the installed MKVToolNix is found. Lookups that must work without
/// MKVToolNix use the common ones.
/// </summary>
public static class Languages
{
    public static Language Czech { get; } = new("cs", "cze", "ces", "Czech", "čeština");

    public static Language Slovak { get; } = new("sk", "slo", "slk", "Slovak", "slovenčina");

    public static Language English { get; } = new("en", "eng", null, "English", "angličtina");

    public static Language Undetermined { get; } = new("und", "und", null, "Undetermined", "neurčený");

    /// <summary>The common languages in the owner's order (the top of every list).</summary>
    public static IReadOnlyList<Language> Common { get; } =
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

    // Slovak names of further languages (by the code UNIVERSUM writes); the others keep their English name
    private static readonly Dictionary<string, string> SlovakNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["af"] = "afrikánčina", ["sq"] = "albánčina", ["am"] = "amharčina", ["hy"] = "arménčina", ["az"] = "azerbajdžančina",
        ["eu"] = "baskičtina", ["be"] = "bieloruština", ["bn"] = "bengálčina", ["bs"] = "bosniačtina", ["br"] = "bretónčina",
        ["my"] = "barmčina", ["cy"] = "waleština", ["eo"] = "esperanto", ["fo"] = "faerčina", ["fil"] = "filipínčina",
        ["fy"] = "frízština", ["gl"] = "galícijčina", ["ka"] = "gruzínčina", ["gu"] = "gudžarátčina", ["ht"] = "haitská kreolčina",
        ["ha"] = "hauština", ["haw"] = "havajčina", ["id"] = "indonézština", ["ga"] = "írčina", ["is"] = "islandčina",
        ["jv"] = "jávčina", ["yi"] = "jidiš", ["yo"] = "jorubčina", ["kn"] = "kannadčina", ["yue"] = "kantončina",
        ["kk"] = "kazaština", ["qu"] = "kečuánčina", ["km"] = "khmérčina", ["ky"] = "kirgizština", ["co"] = "korzičtina",
        ["ku"] = "kurdčina", ["lo"] = "laoština", ["la"] = "latinčina", ["lb"] = "luxemburčina", ["mk"] = "macedónčina",
        ["ms"] = "malajčina", ["ml"] = "malajálamčina", ["mg"] = "malgaština", ["mt"] = "maltčina", ["mi"] = "maorijčina",
        ["mr"] = "maráthčina", ["mn"] = "mongolčina", ["ne"] = "nepálčina", ["nb"] = "nórčina (bokmål)", ["nn"] = "nórčina (nynorsk)",
        ["oc"] = "okcitánčina", ["pa"] = "pandžábčina", ["ps"] = "paštčina", ["fa"] = "perzština", ["rm"] = "rétorománčina",
        ["sa"] = "sanskrit", ["gd"] = "škótska gaelčina", ["si"] = "sinhalčina", ["so"] = "somálčina", ["sw"] = "swahilčina",
        ["tg"] = "tadžičtina", ["tl"] = "tagalčina", ["ta"] = "tamilčina", ["tt"] = "tatárčina", ["te"] = "telugčina",
        ["bo"] = "tibetčina", ["tk"] = "turkménčina", ["ug"] = "ujgurčina", ["ur"] = "urdčina", ["uz"] = "uzbečtina",
        ["xh"] = "xhoština", ["zu"] = "zuluština", ["hsb"] = "hornolužická srbčina", ["dsb"] = "dolnolužická srbčina",
        ["mul"] = "viac jazykov", ["zxx"] = "bez jazyka",
    };

    private static readonly Dictionary<string, Language> CommonByCode = Index(Common);
    private static volatile IReadOnlyList<Language> _all = Common;
    private static volatile Dictionary<string, Language> _byCode = CommonByCode;

    /// <summary>
    /// Every language of the lists: the common ones first, then the others MKVToolNix knows, by name (until MKVToolNix is
    /// found, the common ones only).
    /// </summary>
    public static IReadOnlyList<Language> All => _all;

    /// <summary>Finds a language by ISO 639-1, 639-2/B, 639-2/T or 639-3 code (case-insensitive).</summary>
    public static Language? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : _byCode.GetValueOrDefault(code.Trim());

    /// <summary>
    /// Finds one of the common languages only – for words of file names ("…cz.srt", "…eng.srt"), where the thousands of
    /// three-letter codes of other languages would take ordinary words.
    /// </summary>
    public static Language? FindCommon(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : CommonByCode.GetValueOrDefault(code.Trim());

    /// <summary>The Slovak name of a further language, when UNIVERSUM has one.</summary>
    public static string? SlovakNameOf(string code) => SlovakNames.GetValueOrDefault(code);

    /// <summary>
    /// Adds every language of <paramref name="languages"/> that is not a common one, sorted by its (Slovak or English)
    /// name below the common ones.
    /// </summary>
    public static void Extend(IEnumerable<Language> languages)
    {
        var slovak = System.Globalization.CultureInfo.GetCultureInfo("sk-SK").CompareInfo;
        var others = languages.Where(l => FindCommon(l.Ietf) is null && FindCommon(l.Bibliographic) is null)
            .DistinctBy(l => l.Ietf, StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l.SlovakName, Comparer<string>.Create((a, b) => slovak.Compare(a, b, System.Globalization.CompareOptions.IgnoreCase)))
            .ToList();
        if (others.Count == 0)
        {
            return;
        }

        IReadOnlyList<Language> all = [.. Common, .. others];
        _byCode = Index(all);
        _all = all;
    }

    private static Dictionary<string, Language> Index(IEnumerable<Language> languages)
    {
        var index = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in languages)
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
