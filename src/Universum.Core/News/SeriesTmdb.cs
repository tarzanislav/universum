using System.Globalization;
using System.Text.Json;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Text;

namespace Universum.Core.News;

/// <summary>A series found on TMDB: its ID (for a direct link), its poster, and its origin when TMDB tells it.</summary>
public sealed record SeriesMatch(int TmdbId, string? PosterPath)
{
    /// <summary>The countries of origin (ISO codes).</summary>
    public IReadOnlyList<string> Countries { get; init; } = [];

    /// <summary>The original language (ISO code).</summary>
    public string Language { get; init; } = "";

    /// <summary>TMDB's genre numbers (empty in answers kept before 31. 10. 2026).</summary>
    public IReadOnlyList<int> Genres { get; init; } = [];

    /// <summary>A Czech or Slovak series; null when TMDB tells neither the countries nor the language.</summary>
    public bool? IsHome => Countries.Count == 0 && Language.Length == 0 ? null
        : Countries.Any(HomeOrigin.IsHomeCountry) || HomeOrigin.IsHomeLanguage(Language);
}

/// <summary>
/// Finds the series of the news on TMDB by the original title (and the year it started, ±1) – the sources of series give
/// no TMDB ID, poster or origin. Only the title and the year are sent; the answers are kept 30 days, so each series is
/// asked once.
/// </summary>
public sealed class SeriesTmdb(TmdbClient tmdb)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public async Task<SeriesMatch?> FindAsync(string originalTitle, int? year, CancellationToken cancellationToken = default)
    {
        var wanted = Words.Normalize(originalTitle);
        if (wanted.Length == 0)
        {
            return null;
        }

        foreach (var withYear in year is null ? [false] : new[] { true, false })
        {
            // (the order of the parameters names the cache entry: answers kept before 31. 10. 2026 had no genres)
            var path = "search/tv?include_adult=false&language=en-US&query=" + Uri.EscapeDataString(originalTitle.Trim()) +
                       (withYear ? "&first_air_date_year=" + year!.Value.ToString(CultureInfo.InvariantCulture) : "");
            using var document = await tmdb.GetCompactAsync(path, Lifetime, Compact, cancellationToken).ConfigureAwait(false);
            if (document is null || !document.RootElement.TryGetProperty("r", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var found = results.EnumerateArray().Select(Parse).OfType<Found>().ToList();
            var named = found.Where(f => (f.Name == wanted || f.Original == wanted) && (year is null || f.Year is null || Math.Abs(f.Year.Value - year.Value) <= 1)).ToList();
            var match = named.FirstOrDefault() ?? (withYear && found.Count == 1 ? found[0] : null);
            if (match is not null)
            {
                return new SeriesMatch(match.Id, match.Poster) { Countries = match.Countries, Language = match.Language, Genres = match.Genres };
            }
        }

        return null;
    }

    private sealed record Found(int Id, string Name, string Original, int? Year, string? Poster, IReadOnlyList<string> Countries, string Language)
    {
        public IReadOnlyList<int> Genres { get; init; } = [];
    }

    private static Found? Parse(JsonElement r) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) && number > 0
            ? new Found(number, Words.Normalize(Text(r, "n")), Words.Normalize(Text(r, "o")),
                Text(r, "y") is { Length: 4 } y && int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null,
                PosterCache.IsValidPath(Text(r, "p")) ? Text(r, "p") : null,
                r.TryGetProperty("c", out var countries) && countries.ValueKind == JsonValueKind.Array
                    ? countries.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).Where(c => c.Length is > 0 and <= 3).Take(5).ToList()
                    : [],
                WebText.Clean(Text(r, "l"), 10))
            {
                Genres = r.TryGetProperty("g", out var genres) && genres.ValueKind == JsonValueKind.Array
                    ? genres.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Number && g.TryGetInt32(out _)).Select(g => g.GetInt32()).Take(8).ToList()
                    : [],
            }
            : null;

    // a search keeps only the IDs, names, years, posters and origins of its first ten results
    private static string Compact(JsonElement root) => JsonSerializer.Serialize(new
    {
        r = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Take(10).Select(e => new
            {
                id = e.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) ? number : 0,
                n = Text(e, "name"),
                o = Text(e, "original_name"),
                y = Text(e, "first_air_date") is { Length: >= 4 } date ? date[..4] : null,
                p = Text(e, "poster_path"),
                c = e.TryGetProperty("origin_country", out var origin) && origin.ValueKind == JsonValueKind.Array
                    ? origin.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()).ToList()
                    : [],
                l = Text(e, "original_language"),
                g = e.TryGetProperty("genre_ids", out var genres) && genres.ValueKind == JsonValueKind.Array
                    ? genres.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Number).Select(g => g.GetInt32()).ToList()
                    : [],
            }).ToList()
            : [],
    });

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
