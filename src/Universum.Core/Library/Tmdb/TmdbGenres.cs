using Universum.Core.Text;

namespace Universum.Core.Library.Tmdb;

/// <summary>
/// Genres in Slovak (owner, 31. 10. 2026: a column Žáner in the news, and the genre of a title): TMDB's genre numbers –
/// the same for every language, so lists and searches that give only the numbers need no further request – and the
/// genre names of other sources (TVmaze in English, Czech web pages in Czech).
/// </summary>
public static class TmdbGenres
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [28] = "akčný", [12] = "dobrodružný", [16] = "animovaný", [35] = "komédia", [80] = "krimi", [99] = "dokumentárny",
        [18] = "dráma", [10751] = "rodinný", [14] = "fantasy", [36] = "historický", [27] = "horor", [10402] = "hudobný",
        [9648] = "mysteriózny", [10749] = "romantický", [878] = "sci-fi", [10770] = "TV film", [53] = "thriller", [10752] = "vojnový",
        [37] = "western",

        // series
        [10759] = "akčný, dobrodružný", [10762] = "pre deti", [10763] = "spravodajský", [10764] = "reality show",
        [10765] = "sci-fi, fantasy", [10766] = "telenovela", [10767] = "talk show", [10768] = "vojnový, politický",
    };

    // names of other sources (normalised: lower case, no diacritics) → Slovak
    private static readonly Dictionary<string, string> Other = new(StringComparer.Ordinal)
    {
        ["action"] = "akčný", ["adventure"] = "dobrodružný", ["animation"] = "animovaný", ["anime"] = "anime", ["comedy"] = "komédia",
        ["crime"] = "krimi", ["documentary"] = "dokumentárny", ["drama"] = "dráma", ["family"] = "rodinný", ["fantasy"] = "fantasy",
        ["history"] = "historický", ["horror"] = "horor", ["music"] = "hudobný", ["mystery"] = "mysteriózny", ["romance"] = "romantický",
        ["science-fiction"] = "sci-fi", ["science fiction"] = "sci-fi", ["sci-fi"] = "sci-fi", ["thriller"] = "thriller", ["war"] = "vojnový",
        ["western"] = "western", ["children"] = "pre deti", ["kids"] = "pre deti", ["supernatural"] = "nadprirodzený", ["legal"] = "súdny",
        ["medical"] = "lekársky", ["sports"] = "športový", ["espionage"] = "špionážny", ["food"] = "kulinársky", ["travel"] = "cestopisný",
        ["nature"] = "prírodopisný", ["diy"] = "urob si sám", ["adult"] = "pre dospelých", ["reality"] = "reality show", ["talk show"] = "talk show",
        ["game show"] = "súťažný", ["news"] = "spravodajský", ["soap"] = "telenovela",

        // Czech (web pages of the news)
        ["akcni"] = "akčný", ["dobrodruzny"] = "dobrodružný", ["animovany"] = "animovaný", ["komedie"] = "komédia", ["krimi"] = "krimi",
        ["dokumentarni"] = "dokumentárny", ["rodinny"] = "rodinný", ["historicky"] = "historický", ["horor"] = "horor",
        ["hudebni"] = "hudobný", ["mysteriozni"] = "mysteriózny", ["romanticky"] = "romantický", ["valecny"] = "vojnový",
        ["zivotopisny"] = "životopisný", ["sportovni"] = "športový", ["psychologicky"] = "psychologický", ["pohadka"] = "rozprávka",
        ["detsky"] = "pre deti", ["reality-show"] = "reality show", ["soutezni"] = "súťažný", ["telenovela"] = "telenovela",
        ["dramaticky"] = "dráma", ["kriminalni"] = "krimi", ["vedeckofantasticky"] = "sci-fi",
    };

    private static readonly Dictionary<int, string> English = new()
    {
        [28] = "action", [12] = "adventure", [16] = "animation", [35] = "comedy", [80] = "crime", [99] = "documentary", [18] = "drama",
        [10751] = "family", [14] = "fantasy", [36] = "history", [27] = "horror", [10402] = "music", [9648] = "mystery", [10749] = "romance",
        [878] = "science fiction", [10770] = "TV movie", [53] = "thriller", [10752] = "war", [37] = "western", [10759] = "action, adventure",
        [10762] = "kids", [10763] = "news", [10764] = "reality", [10765] = "science fiction, fantasy", [10766] = "soap", [10767] = "talk show",
        [10768] = "war, politics",
    };

    /// <summary>"action, science fiction" – the genres in English (a prompt for a poster drawn by AI).</summary>
    public static string JoinEnglish(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => English.GetValueOrDefault(id)).OfType<string>().Distinct(StringComparer.Ordinal));

    /// <summary>A genre number in Slovak; null for a number TMDB does not use.</summary>
    public static string? Name(int id) => Names.GetValueOrDefault(id) is { } name ? CoreText.T(name) : null;

    /// <summary>"akčný, sci-fi" from TMDB's genre numbers (each name once, in TMDB's order).</summary>
    public static string Join(IEnumerable<int> ids) =>
        string.Join(", ", ids.Select(Name).OfType<string>().SelectMany(n => n.Split(", ")).Distinct(StringComparer.Ordinal));

    /// <summary>
    /// "Drama, Science-Fiction" (TVmaze) or "Komedie / Drama" (a Czech page) in Slovak; a name not known stays as it is.
    /// </summary>
    public static string Translate(string? genres)
    {
        if (string.IsNullOrWhiteSpace(genres))
        {
            return "";
        }

        var names = genres.Split([',', '/', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => (Other.GetValueOrDefault(Key(n)) ?? Other.GetValueOrDefault(Text.Words.Normalize(n))) is { } known ? CoreText.T(known) : n.ToLowerInvariant())
            .SelectMany(n => n.Split(", "))
            .Distinct(StringComparer.Ordinal);
        return string.Join(", ", names);
    }

    // "Science-Fiction" → "science-fiction" (lower case without diacritics, hyphens kept)
    private static string Key(string name) =>
        new string(name.Normalize(System.Text.NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray())
            .ToLowerInvariant().Trim();
}
