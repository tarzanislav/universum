using System.Globalization;
using System.Text.RegularExpressions;

namespace Universum.Core.Library;

/// <summary>A TMDB reference typed or pasted by the user; Kind is null for a bare number.</summary>
public sealed record TmdbReference(MediaKind? Kind, int Id);

/// <summary>A title read from a file or folder name; only a suggestion until the user confirms it.</summary>
public sealed record NameGuess(string Name, int? Year, int? TmdbId);

/// <summary>Reads titles from names in library form ("Creep (2004)") or release form ("Creep.2004.1080p.BluRay").</summary>
public static partial class TitleGuess
{
    private static readonly HashSet<string> StopWords = new(
        [
            "4k", "uhd", "hdr", "hdr10", "dv", "sdr", "bluray", "blu-ray", "bdrip", "brrip", "bdremux", "remux", "web", "webrip", "web-dl",
            "webdl", "hdtv", "dvdrip", "dvd", "hdrip", "x264", "x265", "h264", "h.264", "h265", "h.265", "hevc", "avc", "xvid", "divx", "av1",
            "10bit", "8bit", "cz", "sk", "czech", "slovak", "dabing", "titulky", "multi", "eng", "en", "proper", "repack", "extended",
            "season", "sezona", "sezóna", "séria", "seria", "serie", "série", "complete", "-",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>"8555", "https://www.themoviedb.org/movie/8555-creep", "themoviedb.org/tv/227191-nine-puzzles?language=cs".</summary>
    public static TmdbReference? ParseTmdb(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var bare) && bare > 0)
        {
            return new TmdbReference(null, bare);
        }

        var match = TmdbUrl().Match(trimmed);
        if (!match.Success || !int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return null;
        }

        return new TmdbReference(match.Groups["kind"].Value.Equals("tv", StringComparison.OrdinalIgnoreCase) ? MediaKind.Series : MediaKind.Movie, id);
    }

    /// <summary>
    /// A title typed by the user in library form: "Creep (2004)", "Nine Puzzles (2025) [tmdbid-227191]" or just a name.
    /// Unlike <see cref="FromName"/> nothing is cut away (a title may contain words like "Web").
    /// </summary>
    public static TitleInfo? Typed(MediaKind kind, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int? id = null;
        if (IdTag().Match(text) is { Success: true } idMatch)
        {
            id = int.Parse(idMatch.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            text = text.Remove(idMatch.Index, idMatch.Length);
        }

        int? year = null;
        if (TrailingYear().Match(text) is { Success: true } yearMatch)
        {
            year = int.Parse(yearMatch.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            text = text[..yearMatch.Index];
        }

        var name = LibraryNames.Clean(text);
        return name.Length == 0 ? null : new TitleInfo(kind, name, year, id, id is not null);
    }

    /// <summary>Title, year and TMDB ID from a file or folder name; null when no name is left.</summary>
    public static NameGuess? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        int? tmdbId = null;
        if (IdTag().Match(name) is { Success: true } idMatch)
        {
            tmdbId = int.Parse(idMatch.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        var text = Brackets().Replace(name, " ");
        if (!text.Contains(' ', StringComparison.Ordinal))
        {
            text = text.Replace('.', ' ').Replace('_', ' ');
        }

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = new List<string>();
        int? year = null;
        foreach (var raw in tokens)
        {
            var token = raw.Trim('(', ')', ',');
            if (words.Count > 0 && Year().IsMatch(token))
            {
                year = int.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture);
                break;
            }

            if (words.Count > 0 && (StopWords.Contains(token) || Resolution().IsMatch(token) || EpisodeToken().IsMatch(token)))
            {
                break;
            }

            words.Add(raw);
        }

        var title = LibraryNames.Clean(string.Join(' ', words).Trim(' ', '-', '.', ','));
        return title.Length == 0 ? null : new NameGuess(title, year, tmdbId);
    }

    [GeneratedRegex(@"themoviedb\.org/(?:[a-z]{2}(?:-[A-Z]{2})?/)?(?<kind>movie|tv)/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TmdbUrl();

    [GeneratedRegex(@"[\[{(]tmdb(?:id)?-(?<id>\d+)[\]})]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdTag();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\(\s*(?<year>(?:18|19|20|21)\d\d)\s*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"^(?:19|20)\d\d$", RegexOptions.CultureInvariant)]
    private static partial Regex Year();

    [GeneratedRegex(@"^\d{3,4}[pi]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Resolution();

    [GeneratedRegex(@"^(?:s\d{1,3}(?:e\d{1,4}.*)?|e\d{1,4}|ep\d{1,4}|\d{1,2}x\d{2,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeToken();
}
