using System.Text.RegularExpressions;

namespace Universum.Core.Library;

/// <summary>The identifiers of a film or series in the databases UNIVERSUM links to; any of them may be missing.</summary>
public sealed partial record TitleIds(int? Histra = null, int? Tmdb = null, string? Imdb = null, int? Trakt = null, int? Tvdb = null)
{
    /// <summary>An IMDb ID as IMDb writes it ("tt" and digits); anything else is not used.</summary>
    public static string? ValidImdb(string? id) => id is not null && ImdbId().IsMatch(id) ? id : null;

    [GeneratedRegex("^tt[0-9]{5,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImdbId();
}

/// <summary>
/// Addresses of a title's pages, opened in the user's browser: HISTRA, TMDB and IMDb by ID; ČSFD has no ID in the
/// data, so its search is opened. Only addresses built here (HTTPS, known sites, encoded values) are ever opened.
/// </summary>
public static class TitleLinks
{
    /// <summary>The title on histra.net (its own number in HISTRA).</summary>
    public static Uri? Histra(MediaKind kind, int? id) =>
        id is > 0 ? new Uri($"https://histra.net/{(kind == MediaKind.Movie ? "movie" : "show")}/{id}") : null;

    /// <summary>The title on themoviedb.org.</summary>
    public static Uri? Tmdb(MediaKind kind, int? id) =>
        id is > 0 ? new Uri($"https://www.themoviedb.org/{(kind == MediaKind.Movie ? "movie" : "tv")}/{id}") : null;

    /// <summary>The title on imdb.com.</summary>
    public static Uri? Imdb(string? id) => TitleIds.ValidImdb(id) is { } valid ? new Uri($"https://www.imdb.com/title/{valid}/") : null;

    /// <summary>A search on csfd.cz for the title (ČSFD IDs are not in the data UNIVERSUM reads).</summary>
    public static Uri? CsfdSearch(string? title) =>
        string.IsNullOrWhiteSpace(title) ? null : new Uri("https://www.csfd.cz/hledat/?q=" + Uri.EscapeDataString(title.Trim()));
}
