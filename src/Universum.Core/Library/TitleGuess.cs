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
            "10bit", "8bit", "cz", "sk", "czech", "slovak", "dabing", "titulky", "multi", "eng", "en", "proper", "repack", "extended", "-",
        ],
        StringComparer.OrdinalIgnoreCase);

    // words that, followed by a number, name an episode or a season ("Díl 4", "Season 2"); normalised (no diacritics)
    private static readonly HashSet<string> NumberedWords = new(
        ["episode", "epizoda", "dil", "diel", "cast", "folge", "odcinek", "ep", "season", "seria", "serie", "series", "sezona", "rada", "staffel", "sezon"],
        StringComparer.Ordinal);

    /// <summary>
    /// "8555", "tmdbid-8555", "[tmdbid-8555]", "tmdb 8555", "https://www.themoviedb.org/movie/8555-creep",
    /// "themoviedb.org/tv/227191-nine-puzzles?language=cs".
    /// </summary>
    public static TmdbReference? ParseTmdb(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var bare) && bare > 0)
        {
            return new TmdbReference(null, bare);
        }

        if (TmdbIdOnly().Match(trimmed) is { Success: true } only && Id(only.Groups["id"].Value) is { } onlyId)
        {
            return new TmdbReference(null, onlyId);
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
    /// Unlike <see cref="FromName"/> nothing is cut away (a title may contain words like "Web"). An ID that is no valid
    /// number leaves the title without one (its problem says so).
    /// </summary>
    public static TitleInfo? Typed(MediaKind kind, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int? id = null;
        var hasId = false;
        if (IdTag().Match(text) is { Success: true } idMatch)
        {
            hasId = true;
            id = Id(idMatch.Groups["id"].Value);
            text = text.Remove(idMatch.Index, idMatch.Length);
        }

        int? year = null;
        if (TrailingYear().Match(text) is { Success: true } yearMatch)
        {
            year = int.Parse(yearMatch.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            text = text[..yearMatch.Index];
        }

        var name = LibraryNames.Clean(text);
        return name.Length == 0 ? null : new TitleInfo(kind, name, year, id, hasId);
    }

    /// <summary>
    /// Kind, title and episode of one file (header editor, chapters, multiplexer, rules page): a series when the name
    /// carries a clear episode number (S01E04, 1x04; "Díl 4" only without a film year in the name) or the file lies in
    /// a season folder; the title from the folder of the title (above a "Season 2" folder) when it has a year or a TMDB
    /// ID, otherwise from the file name.
    /// </summary>
    public static FileGuess ForFile(string path)
    {
        var episode = EpisodeParser.FromPath(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var series = NamesEpisode(episode, name) || EpisodeParser.SeasonOfPath(path) is not null;
        var kind = series ? MediaKind.Series : MediaKind.Movie;
        var directory = Path.GetDirectoryName(path);
        if (kind == MediaKind.Series && EpisodeParser.IsSeasonOnlyFolder(Path.GetFileName(directory)))
        {
            directory = Path.GetDirectoryName(directory);
        }

        var fromFolder = FromName(Path.GetFileName(directory), kind);
        var fromFile = FromName(name, kind);
        return new FileGuess(kind, Preferred(fromFolder, fromFile), kind == MediaKind.Series ? episode : null);
    }

    /// <summary>
    /// The folder's title when it is in library form (with a year or an ID) or spelled like the file names (then with
    /// its diacritics: "Kriminálka Anděl" rather than "Kriminalka Andel"); otherwise the one from the file names.
    /// </summary>
    public static NameGuess? Preferred(NameGuess? fromFolder, NameGuess? fromFiles)
    {
        if (fromFolder is { Year: not null } or { TmdbId: not null })
        {
            return fromFolder;
        }

        if (fromFolder is not null && fromFiles is not null && Words.Normalize(fromFolder.Name) == Words.Normalize(fromFiles.Name))
        {
            return fromFolder with { Year = fromFiles.Year, TmdbId = fromFiles.TmdbId };
        }

        return fromFiles ?? fromFolder;
    }

    /// <summary>True when the name carries a year in parentheses, as films in library form do: "Sága - Časť 2 (2011)".</summary>
    public static bool HasYearInParentheses(string? name) => name is not null && ParenthesizedYear().IsMatch(name);

    /// <summary>
    /// True when the number found in a name makes it an episode: S01E04 always; 1x04 or a word form ("Díl 4", "Episode 2")
    /// unless it stands before the year of a library-form name, where it belongs to a film's title ("Sága - Časť 2 (2011)",
    /// "Rally 4x10 (2015)"); after the year it is an episode ("Kriminálka Anděl (2008) Díl 1").
    /// </summary>
    public static bool NamesEpisode(EpisodeMatch? match, string name) =>
        match is not null && (match.Form == EpisodeForm.SeasonEpisode || !NumberInTitle(name));

    // the number stands in the title part of a library-form name, before "(Year)"
    private static bool NumberInTitle(string name) =>
        ParenthesizedYear().Match(name) is { Success: true, Index: > 0 } year && EpisodeParser.FromText(name[..year.Index]) is { Form: not EpisodeForm.SeasonEpisode };

    /// <summary>Title, year and TMDB ID from a file or folder name; null when no name is left.</summary>
    /// <param name="kind">
    /// For a film, "Časť 2" belongs to the title; for a series (or unknown) an episode or season ends the title:
    /// "Kriminálka Anděl Díl 4" → "Kriminálka Anděl".
    /// </param>
    /// <remarks>
    /// Library form: the title is everything before the year in parentheses ("Kolónia 2049 (2017)", "Sága - Časť 2
    /// (2011)"). Release form: the title ends at a resolution, a release word or an episode; the year is the last year-like
    /// word before that ("Mesto.1999.2004.1080p" → "Mesto 1999", 2004).
    /// </remarks>
    public static NameGuess? FromName(string? name, MediaKind? kind = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        int? tmdbId = null;
        if (IdTag().Match(name) is { Success: true } idMatch)
        {
            tmdbId = Id(idMatch.Groups["id"].Value);
        }

        // "[2021]" after the title is its year; other brackets (and a year in front of it) are tags of the release.
        // Dots and underscores separate the words when the name itself has no spaces (brackets aside).
        var spaced = Brackets().Replace(name, "").Trim().Contains(' ', StringComparison.Ordinal);
        var text = Brackets().Replace(BracketedYear().Replace(name, m => Brackets().Replace(name[..m.Index], "").Trim(' ', '.', '_', '-').Length > 0 ? " (" + m.Groups["year"].Value + ") " : m.Value), " ");
        if (!spaced)
        {
            text = text.Replace('.', ' ').Replace('_', ' ');
        }

        var film = kind == MediaKind.Movie;
        if (ParenthesizedYear().Match(text) is { Success: true } inParentheses && inParentheses.Index > 0)
        {
            var before = text[..inParentheses.Index];
            var titleWords = before.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cut = film ? titleWords.Length : EpisodeStart(titleWords);
            var title = Clean(titleWords.Take(cut));
            if (title.Length > 0)
            {
                return new NameGuess(title, int.Parse(inParentheses.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture), tmdbId);
            }
        }

        // "Nine Puzzles.S01E09": a clear episode number inside a word starts a word of its own
        // "Zlatý kapor.2021.WEB-DL.CZ": words joined by dots after a name with spaces are words of their own
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(ReleaseWords)
            .SelectMany(t => EpisodeParser.IndexOfClearNumber(t) is > 0 and var at ? [t[..at].TrimEnd('.', '_', '-'), t[at..]] : new[] { t })
            .Where(t => t.Length > 0)
            .ToArray();
        var end = tokens.Length;
        var dash = tokens.Length;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim('(', ')', ',');
            if (token == "-")
            {
                dash = Math.Min(dash, i > 0 ? i : dash);
                continue;
            }

            if ((!film && IsEpisodeAt(tokens, i)) || (i > 0 && (StopWords.Contains(token) || Resolution().IsMatch(token))))
            {
                end = i;
                break;
            }
        }

        // the year: the last year-like word of the title part that is not its first word ("Zlatý kapor - Návrat 2021
        // 1080p": the part after the dash belongs to the title); without a year a dash ends the title
        int? year = null;
        for (var i = end - 1; i > 0; i--)
        {
            if (Year().IsMatch(tokens[i].Trim('(', ')', ',')))
            {
                year = int.Parse(tokens[i].Trim('(', ')', ','), NumberStyles.None, CultureInfo.InvariantCulture);
                end = i;
                break;
            }
        }

        // "Zlatý kapor.CZ.dabing.2021.1080p": a year among the release words right after the title
        for (var i = end; year is null && i < tokens.Length; i++)
        {
            var token = tokens[i].Trim('(', ')', ',');
            if (Year().IsMatch(token))
            {
                year = int.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            else if (!StopWords.Contains(token) || (!film && IsEpisodeAt(tokens, i)))
            {
                break;
            }
        }

        if (year is null)
        {
            end = Math.Min(end, dash);
        }

        var result = Clean(tokens.Take(end));
        return result.Length == 0 ? null : new NameGuess(result, year, tmdbId);
    }

    // a word with dots or underscores is split when a part after the first is a year, a resolution or a release word
    // ("Rybka.2021.WEB-DL"); "Mr." or "H.264" stay whole
    private static IEnumerable<string> ReleaseWords(string token)
    {
        var parts = token.Split(['.', '_'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && parts.Skip(1).Any(p => Year().IsMatch(p) || Resolution().IsMatch(p) || (p != "-" && StopWords.Contains(p))) ? parts : [token];
    }

    private static string Clean(IEnumerable<string> words) => LibraryNames.Clean(string.Join(' ', words).Trim(' ', '-', '.', ','));

    // index of the first word that starts an episode or a season ("S01E04", "Díl 4", "Season 2"), or the length
    private static int EpisodeStart(string[] words)
    {
        for (var i = 0; i < words.Length; i++)
        {
            if (IsEpisodeAt(words, i))
            {
                return i;
            }
        }

        return words.Length;
    }

    private static bool IsEpisodeAt(string[] tokens, int i)
    {
        var token = tokens[i].Trim('(', ')', ',', '-');
        return EpisodeToken().IsMatch(token) || EpisodeParser.FromText(token) is { Form: not EpisodeForm.Word } ||
               (NumberedWords.Contains(Words.Normalize(token)) && i + 1 < tokens.Length && Number().IsMatch(tokens[i + 1].Trim('(', ')', ',', '.', ':')));
    }

    // a TMDB ID; an ID too long for a number is no ID
    private static int? Id(string digits) => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    [GeneratedRegex(@"^[\[{(]?tmdb(?:id)?\s*[-:=]?\s*(?<id>\d+)[\]})]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TmdbIdOnly();

    [GeneratedRegex(@"themoviedb\.org/(?:[a-z]{2}(?:-[A-Z]{2})?/)?(?<kind>movie|tv)/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TmdbUrl();

    [GeneratedRegex(@"[\[{(]tmdb(?:id)?-(?<id>\d+)[\]})]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdTag();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\[\s*(?<year>(?:18|19|20|21)\d\d)\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedYear();

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

    [GeneratedRegex(@"^(?:s\d{1,3}(?:[._-]?e\d{1,4}.*)?|e\d{1,4}|ep\d{1,4}|\d{1,2}x\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeToken();
}
