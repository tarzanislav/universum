using System.Globalization;
using System.Text.RegularExpressions;
using Universum.Core.Text;

namespace Universum.Core.Library;

/// <summary>A TMDB reference typed or pasted by the user; Kind is null for a bare number.</summary>
public sealed record TmdbReference(MediaKind? Kind, int Id);

/// <summary>A title read from a file or folder name; only a suggestion until the user confirms it.</summary>
public sealed record NameGuess(string Name, int? Year, int? TmdbId);

/// <summary>What one file is, guessed from its name and folders; only a suggestion until the user confirms it.</summary>
/// <param name="Episode">Episode found in the name (with the season from the folders), or null.</param>
public sealed record FileGuess(MediaKind Kind, NameGuess? Title, EpisodeMatch? Episode)
{
    /// <summary>The title in library form ("Creep (2004)"), or "".</summary>
    public string TitleText => Title is null ? "" : new TitleInfo(Kind, Title.Name, Title.Year, Title.TmdbId, Title.TmdbId is not null).FolderName;
}

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

    // words that, followed by a number, name an episode or a season ("Díl 4", "Season 2"); normalised (no diacritics)
    private static readonly HashSet<string> NumberedWords = new(
        ["episode", "epizoda", "dil", "diel", "cast", "folge", "odcinek", "ep", "season", "seria", "serie", "series", "sezona", "rada", "staffel", "sezon"],
        StringComparer.Ordinal);

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

    /// <summary>
    /// Kind, title and episode of one file (header editor, chapters, multiplexer, rules page): a series when the name
    /// carries an episode number or the file lies in a season folder; the title from the folder of the title (above a
    /// "Season 2" folder) when it has a year or a TMDB ID, otherwise from the file name.
    /// </summary>
    public static FileGuess ForFile(string path)
    {
        var episode = EpisodeParser.FromPath(path);
        var kind = episode is not null || EpisodeParser.SeasonOfPath(path) is not null ? MediaKind.Series : MediaKind.Movie;
        var directory = Path.GetDirectoryName(path);
        if (kind == MediaKind.Series && EpisodeParser.IsSeasonOnlyFolder(Path.GetFileName(directory)))
        {
            directory = Path.GetDirectoryName(directory);
        }

        var fromFolder = FromName(Path.GetFileName(directory), kind);
        var fromFile = FromName(Path.GetFileNameWithoutExtension(path), kind);
        return new FileGuess(kind, Preferred(fromFolder, fromFile), episode);
    }

    /// <summary>The folder's title when it is in library form (with a year or an ID), otherwise the one from the file names.</summary>
    public static NameGuess? Preferred(NameGuess? fromFolder, NameGuess? fromFiles) =>
        fromFolder is { Year: not null } or { TmdbId: not null } ? fromFolder : fromFiles ?? fromFolder;

    /// <summary>Title, year and TMDB ID from a file or folder name; null when no name is left.</summary>
    /// <param name="kind">
    /// For a film, "Časť 2" or " - " before a year in parentheses belong to the title ("Sága - Časť 2 (2011)"); otherwise an
    /// episode or season ends the title.
    /// </param>
    public static NameGuess? FromName(string? name, MediaKind? kind = null)
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
        var film = kind == MediaKind.Movie;
        // "Title - Part 2 (2011)": in library form the title ends at the year in parentheses
        var yearInParentheses = ParenthesizedYear().Match(text);
        for (var i = 0; i < tokens.Length; i++)
        {
            var raw = tokens[i];
            var token = raw.Trim('(', ')', ',');
            if (words.Count > 0 && Year().IsMatch(token))
            {
                year = int.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture);
                break;
            }

            // an episode or season ends the title: "Show S01E04", "Kriminálka Anděl Díl 4", "Show Season 2"; a name that
            // starts with one ("E04", "Diel 3") has no title
            if (!film && (EpisodeToken().IsMatch(token) ||
                (NumberedWords.Contains(Words.Normalize(token)) && i + 1 < tokens.Length && Number().IsMatch(tokens[i + 1].Trim('(', ')', ',', '.', ':')))))
            {
                break;
            }

            if (words.Count > 0 && (Resolution().IsMatch(token) ||
                (StopWords.Contains(token) && !(film && token == "-" && yearInParentheses.Success && yearInParentheses.Index > text.IndexOf(raw, StringComparison.Ordinal)))))
            {
                break;
            }

            words.Add(raw);
        }

        if (year is null && yearInParentheses.Success && words.Count > 0)
        {
            year = int.Parse(yearInParentheses.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
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

    [GeneratedRegex(@"^\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"\(\s*(?<year>(?:18|19|20|21)\d\d)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedYear();

    [GeneratedRegex(@"^(?:s\d{1,3}(?:e\d{1,4}.*)?|e\d{1,4}|ep\d{1,4}|\d{1,2}x\d{2,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeToken();
}
