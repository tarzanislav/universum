using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Text;

namespace Universum.Core.News;

/// <summary>A film premiere: the library title, where and when it premieres, who made it and when it comes to VOD or TV.</summary>
public sealed record FilmPremiere
{
    public required int TmdbId { get; init; }

    public required string OriginalTitle { get; init; }

    /// <summary>The English title (the library uses it for titles not written in Latin script).</summary>
    public required string EnglishTitle { get; init; }

    /// <summary>The Czech title (TMDB's translation), when it differs.</summary>
    public string LocalTitle { get; init; } = "";

    public int? Year { get; init; }

    public required NewsRegion Region { get; init; }

    public required DateOnly Premiere { get; init; }

    /// <summary>Where it premieres ("CZ", "SK", "CZ · SK 31. 10.") or empty.</summary>
    public string PremiereNote { get; init; } = "";

    /// <summary>Countries of production and the first company ("USA · Warner Bros. Pictures").</summary>
    public string Production { get; init; } = "";

    /// <summary>When it comes to VOD and TV ("VOD 12. 12. 2026 · TV 1. 3. 2027 (USA)") or empty.</summary>
    public string Availability { get; init; } = "";

    public string? PosterPath { get; init; }

    public double Popularity { get; init; }

    /// <summary>The library title by the specification (13.2).</summary>
    [JsonIgnore]
    public TitleInfo? Title => TitleInfo.FromTmdb(MediaKind.Movie, TmdbId, OriginalTitle, EnglishTitle, Year);

    /// <summary>"Original (Year)" as the library names the folder.</summary>
    [JsonIgnore]
    public string Caption => Title?.FolderName ?? (Year is { } year ? $"{OriginalTitle} ({year})" : OriginalTitle);
}

/// <summary>
/// Film premieres from TMDB (owner, 28. 10. 2026: for films TMDB is the source – it keeps release dates per country):
/// premieres in Czech and Slovak cinemas, and the most popular premieres in the world, each with the dates of VOD and TV
/// releases. Lists are cached 12 hours, the details of a film 3 days; only the dates and the film's IDs are sent.
/// </summary>
public sealed class FilmPremieres(TmdbClient tmdb)
{
    private static readonly TimeSpan ListLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan DetailLifetime = TimeSpan.FromDays(3);
    private static readonly string[] Domestic = ["CZ", "SK"];

    /// <summary>At most this many pages of 20 films per list.</summary>
    public const int MaxPages = 5;

    /// <summary>Films in Czech or Slovak cinemas between the dates (the earlier of the two premieres).</summary>
    public async Task<IReadOnlyList<FilmPremiere>> DomesticAsync(DateOnly from, DateOnly to, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<int, (string English, double Popularity)>();
        foreach (var region in Domestic)
        {
            foreach (var film in await DiscoverAsync(
                $"discover/movie?language=en-US&include_adult=false&region={region}&release_date.gte={Iso(from)}&release_date.lte={Iso(to)}" +
                "&with_release_type=2%7C3&sort_by=popularity.desc", MaxPages, cancellationToken).ConfigureAwait(false))
            {
                found.TryAdd(film.Id, (film.English, film.Popularity));
            }
        }

        var films = new List<FilmPremiere>();
        var done = 0;
        foreach (var (id, (english, popularity)) in found)
        {
            progress?.Report($"Filmy SK/CZ: {++done} z {found.Count}");
            if (await DetailsAsync(id, cancellationToken).ConfigureAwait(false) is { } details && Here(details, english, popularity, from, to) is { } film)
            {
                films.Add(film);
            }
        }

        return films.OrderBy(f => f.Premiere).ThenByDescending(f => f.Popularity).ToList();
    }

    /// <summary>
    /// A kept premiere asked again (the lists give only the most popular films, so a kept film that dropped out of them
    /// is checked by its own details): its premiere from <paramref name="from"/> on, with fresh VOD and TV dates; null
    /// when it no longer premieres then (the date moved earlier or was dropped).
    /// </summary>
    public async Task<FilmPremiere?> RecheckAsync(FilmPremiere film, DateOnly from, CancellationToken cancellationToken = default)
    {
        if (await DetailsAsync(film.TmdbId, cancellationToken).ConfigureAwait(false) is not { } details)
        {
            return null;
        }

        return film.Region == NewsRegion.Domestic
            ? Here(details, film.EnglishTitle, film.Popularity, from, DateOnly.MaxValue)
            : details.Release is { } release && release >= from ? details.ToPremiere(film.EnglishTitle, film.Popularity, NewsRegion.World, release, "") : null;
    }

    // the theatrical premieres here (types 2 and 3) within the dates, the earlier one first
    private static FilmPremiere? Here(Details details, string english, double popularity, DateOnly from, DateOnly to)
    {
        var here = details.Dates.Where(d => d.Country is "CZ" or "SK" && d.Type is 2 or 3 && d.Date >= from && d.Date <= to)
            .GroupBy(d => d.Country).Select(g => g.OrderBy(d => d.Date).First()).OrderBy(d => d.Date).ToList();
        if (here.Count == 0)
        {
            return null;
        }

        var note = here.Count == 1 || here[0].Date == here[1].Date
            ? string.Join(" · ", here.Select(d => d.Country))
            : $"{here[0].Country} · {here[1].Country} {here[1].Date.Day}. {here[1].Date.Month}.";
        return details.ToPremiere(english, popularity, NewsRegion.Domestic, here[0].Date, note);
    }

    /// <summary>The most popular films whose first release (anywhere) is between the dates.</summary>
    public async Task<IReadOnlyList<FilmPremiere>> WorldAsync(DateOnly from, DateOnly to, int pages = 3, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var found = await DiscoverAsync(
            $"discover/movie?language=en-US&include_adult=false&primary_release_date.gte={Iso(from)}&primary_release_date.lte={Iso(to)}&sort_by=popularity.desc",
            Math.Clamp(pages, 1, MaxPages), cancellationToken).ConfigureAwait(false);
        var films = new List<FilmPremiere>();
        var done = 0;
        foreach (var film in found)
        {
            progress?.Report($"Filmy vo svete: {++done} z {found.Count}");
            if (await DetailsAsync(film.Id, cancellationToken).ConfigureAwait(false) is { Release: { } release } details && release >= from && release <= to)
            {
                films.Add(details.ToPremiere(film.English, film.Popularity, NewsRegion.World, release, ""));
            }
        }

        return films.OrderBy(f => f.Premiere).ThenByDescending(f => f.Popularity).ToList();
    }

    private async Task<List<(int Id, string English, double Popularity)>> DiscoverAsync(string path, int pages, CancellationToken cancellationToken)
    {
        var films = new List<(int, string, double)>();
        for (var page = 1; page <= pages; page++)
        {
            using var document = await tmdb.GetCompactAsync($"{path}&page={page}", ListLifetime, CompactList, cancellationToken).ConfigureAwait(false);
            if (document is null || !document.RootElement.TryGetProperty("results", out var results))
            {
                break;
            }

            foreach (var film in results.EnumerateArray())
            {
                if (film.TryGetProperty("id", out var id) && id.TryGetInt32(out var number))
                {
                    films.Add((number, Text(film, "title") ?? "", film.TryGetProperty("popularity", out var p) && p.TryGetDouble(out var popularity) ? popularity : 0));
                }
            }

            if (!document.RootElement.TryGetProperty("total_pages", out var total) || !total.TryGetInt32(out var count) || page >= count)
            {
                break;
            }
        }

        return films;
    }

    private async Task<Details?> DetailsAsync(int id, CancellationToken cancellationToken)
    {
        using var document = await tmdb.GetCompactAsync(
            $"movie/{id.ToString(CultureInfo.InvariantCulture)}?language=cs-CZ&append_to_response=release_dates", DetailLifetime, CompactDetails, cancellationToken).ConfigureAwait(false);
        return document is not null && Details.Parse(document.RootElement) is { } details ? details with { Id = id } : null;
    }

    // the list keeps the IDs, English titles and popularity
    private static string CompactList(JsonElement root) => JsonSerializer.Serialize(new
    {
        total_pages = root.TryGetProperty("total_pages", out var t) && t.TryGetInt32(out var pages) ? pages : 1,
        results = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(r => new
            {
                id = r.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) ? number : 0,
                title = Text(r, "title"),
                popularity = r.TryGetProperty("popularity", out var p) && p.TryGetDouble(out var popularity) ? popularity : 0,
            }).ToList()
            : [],
    });

    // the details keep titles, poster, production and the release dates of CZ, SK and the USA
    private static string CompactDetails(JsonElement root) => JsonSerializer.Serialize(new
    {
        title = Text(root, "title"),
        original_title = Text(root, "original_title"),
        release_date = Text(root, "release_date"),
        poster_path = Text(root, "poster_path"),
        countries = Array(root, "production_countries").Select(c => Text(c, "iso_3166_1")).OfType<string>().ToList(),
        companies = Array(root, "production_companies").Select(c => Text(c, "name")).OfType<string>().Take(2).ToList(),
        dates = root.TryGetProperty("release_dates", out var releases)
            ? Array(releases, "results").Where(r => Text(r, "iso_3166_1") is "CZ" or "SK" or "US")
                .SelectMany(r => Array(r, "release_dates").Select(d => new
                {
                    c = Text(r, "iso_3166_1"),
                    t = d.TryGetProperty("type", out var type) && type.TryGetInt32(out var number) ? number : 0,
                    d = Text(d, "release_date"),
                })).ToList()
            : [],
    });

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? Date(string? text) =>
        text is { Length: >= 10 } && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private sealed record Release(string Country, int Type, DateOnly Date);

    private sealed record Details(int Id, string Title, string Original, DateOnly? Release, string? Poster, IReadOnlyList<string> Countries, IReadOnlyList<string> Companies, IReadOnlyList<Release> Dates)
    {
        public static Details? Parse(JsonElement root)
        {
            var original = WebText.Clean(Text(root, "original_title"), 300);
            if (original.Length == 0)
            {
                return null;
            }

            return new Details(0, WebText.Clean(Text(root, "title"), 300), original, Date(Text(root, "release_date")),
                PosterCache.IsValidPath(Text(root, "poster_path")) ? Text(root, "poster_path") : null,
                root.TryGetProperty("countries", out var countries) && countries.ValueKind == JsonValueKind.Array
                    ? countries.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                    : [],
                root.TryGetProperty("companies", out var companies) && companies.ValueKind == JsonValueKind.Array
                    ? companies.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => WebText.Clean(e.GetString(), 100)).ToList()
                    : [],
                Array(root, "dates").Select(d => Date(Text(d, "d")) is { } day && Text(d, "c") is { } country && d.TryGetProperty("t", out var t) && t.TryGetInt32(out var type)
                    ? new Release(country, type, day) : null).OfType<Release>().ToList());
        }

        public FilmPremiere ToPremiere(string english, double popularity, NewsRegion region, DateOnly premiere, string note) => new()
        {
            TmdbId = Id,
            OriginalTitle = Original,
            EnglishTitle = english.Length > 0 ? WebText.Clean(english, 300) : Original,
            LocalTitle = Title.Length > 0 && Title != Original ? Title : "",
            Year = Release?.Year ?? premiere.Year,
            Region = region,
            Premiere = premiere,
            PremiereNote = note,
            Production = string.Join(" · ", new[] { string.Join(", ", Countries.Take(3).Select(CountryNames.Name)), Companies.FirstOrDefault() ?? "" }.Where(p => p.Length > 0)),
            Availability = Availability(),
            PosterPath = Poster,
            Popularity = popularity,
        };

        // VOD (digital, type 4) and TV (type 6): Czech, else Slovak, else the USA (marked)
        private string Availability()
        {
            string? One(int type, string label)
            {
                foreach (var country in new[] { "CZ", "SK", "US" })
                {
                    if (Dates.Where(d => d.Country == country && d.Type == type).OrderBy(d => d.Date).FirstOrDefault() is { } release)
                    {
                        return $"{label} {release.Date.Day}. {release.Date.Month}. {release.Date.Year}" + (country == "US" ? " (USA)" : country == "SK" ? " (SK)" : "");
                    }
                }

                return null;
            }

            return string.Join(" · ", new[] { One(4, "VOD"), One(6, "TV") }.OfType<string>());
        }
    }
}

/// <summary>Short Slovak names of the countries films most often come from (the ISO code for the others).</summary>
public static class CountryNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "USA", ["GB"] = "VB", ["CZ"] = "ČR", ["SK"] = "SR", ["FR"] = "Francúzsko", ["DE"] = "Nemecko", ["IT"] = "Taliansko",
        ["ES"] = "Španielsko", ["CA"] = "Kanada", ["AU"] = "Austrália", ["NZ"] = "Nový Zéland", ["IE"] = "Írsko", ["BE"] = "Belgicko",
        ["NL"] = "Holandsko", ["DK"] = "Dánsko", ["SE"] = "Švédsko", ["NO"] = "Nórsko", ["FI"] = "Fínsko", ["PL"] = "Poľsko",
        ["HU"] = "Maďarsko", ["AT"] = "Rakúsko", ["CH"] = "Švajčiarsko", ["RU"] = "Rusko", ["UA"] = "Ukrajina", ["JP"] = "Japonsko",
        ["KR"] = "Južná Kórea", ["CN"] = "Čína", ["HK"] = "Hongkong", ["TW"] = "Taiwan", ["IN"] = "India", ["MX"] = "Mexiko",
        ["BR"] = "Brazília", ["AR"] = "Argentína", ["TR"] = "Turecko", ["IL"] = "Izrael", ["ZA"] = "JAR", ["IS"] = "Island",
        ["PT"] = "Portugalsko", ["GR"] = "Grécko", ["RO"] = "Rumunsko", ["TH"] = "Thajsko",
    };

    public static string Name(string code) => Names.TryGetValue(code, out var name) ? name : code.ToUpperInvariant();
}
