using Universum.Core.Media;
using Universum.Core.Text;

namespace Universum.Core.Batch;

/// <summary>Language from an original track name such as "CZ Dabing", "Slovensky 5.1" or "English (SDH)".</summary>
public static class LanguageGuess
{
    private static readonly (string Language, string[] Words)[] Names =
    [
        ("cs", ["cz", "cze", "ces", "czech", "cesky", "cestina", "ceske", "cesky dabing"]),
        ("sk", ["sk", "slo", "slk", "slovak", "slovensky", "slovencina", "slovenske"]),
        ("en", ["en", "eng", "english", "anglicky", "anglictina"]),
        ("de", ["de", "ger", "deu", "german", "deutsch", "nemecky", "nemcina"]),
        ("hu", ["hu", "hun", "hungarian", "magyar", "madarsky"]),
        ("pl", ["pl", "pol", "polish", "polski", "polsky"]),
        ("ru", ["ru", "rus", "russian", "rusky", "russkij"]),
        ("fr", ["fr", "fre", "fra", "french", "francais", "francuzsky"]),
        ("es", ["es", "spa", "spanish", "espanol", "spanielsky"]),
        ("it", ["it", "ita", "italian", "italiano", "taliansky"]),
    ];

    /// <summary>The language named in the track name; null when none or more than one language is named.</summary>
    public static LanguageTag? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var found = Names.Where(n => Words.ContainsAny(name, n.Words)).Select(n => n.Language).Distinct().ToList();
        return found.Count == 1 ? LanguageTag.FromCode(found[0]) : null;
    }
}
