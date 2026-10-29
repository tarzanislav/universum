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

    /// <summary>
    /// Where it premieres: a Czech or Slovak film "CZ", "SK", "CZ · SK 31. 10."; a film of the world in the cinemas here
    /// "v kinách CZ · SK od 6. 11."; else empty.
    /// </summary>
    public string PremiereNote { get; init; } = "";

    /// <summary>Countries of production and the first company ("USA · Warner Bros. Pictures").</summary>
    public string Production { get; init; } = "";

    /// <summary>The countries of production (ISO codes).</summary>
    public IReadOnlyList<string> Countries { get; init; } = [];

    /// <summary>The original language (ISO code) when TMDB tells it.</summary>
    public string Language { get; init; } = "";

    /// <summary>The region by origin; null in premieres kept before 29. 10. 2026 (sorted by <see cref="FilmPremieres.Classify(FilmPremiere)"/>).</summary>
    public NewsRegion? Origin { get; init; }

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
/// Czech and Slovak films in the cinemas here, and the most popular premieres in the world, each with the dates of VOD and
/// TV releases. By origin (owner, 29. 10. 2026): a Czech or Slovak film (country of production or original language) is
/// <see cref="NewsRegion.Domestic"/>, any other <see cref="NewsRegion.World"/> – a foreign film in the cinemas here with
/// its world premiere and the dates here as a note. Lists are cached 12 hours, the details of a film 3 days; only the dates
/// and the film's IDs are sent.
/// </summary>
public sealed partial class FilmPremieres(TmdbClient tmdb)
{
    private static readonly TimeSpan ListLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan DetailLifetime = TimeSpan.FromDays(3);
    private static readonly string[] Domestic = ["CZ", "SK"];

    /// <summary>At most this many pages of 20 films per list.</summary>
    public const int MaxPages = 5;

    /// <summary>
    /// Films in Czech or Slovak cinemas between the dates: a Czech or Slovak film on the earlier of the two premieres, a
    /// foreign one on its world premiere with the dates here as a note.
    /// </summary>
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
            progress?.Report($"Filmy v kinách SK/CZ: {++done} z {found.Count}");
            if (await DetailsAsync(id, cancellationToken).ConfigureAwait(false) is { } details && InCinemasHere(details, english, popularity, from, to) is { } film)
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
            ? InCinemasHere(details, film.EnglishTitle, film.Popularity, from, DateOnly.MaxValue)
            : OfTheWorld(details, film.EnglishTitle, film.Popularity, from, DateOnly.MaxValue);
    }

    /// <summary>A Czech or Slovak film: a country of production or the original language.</summary>
    private static bool IsHome(Details details) => details.Countries.Any(HomeOrigin.IsHomeCountry) || HomeOrigin.IsHomeLanguage(details.Language);

    // a film of the cinemas here: a Czech or Slovak one on its premiere here, a foreign one on its world premiere
    private static FilmPremiere? InCinemasHere(Details details, string english, double popularity, DateOnly from, DateOnly to)
    {
        var here = HereDates(details, from, to);
        if (here.Count == 0)
        {
            return null;
        }

        if (IsHome(details))
        {
            var note = here.Count == 1 || here[0].Date == here[1].Date
                ? string.Join(" · ", here.Select(d => d.Country))
                : $"{here[0].Country} · {here[1].Country} {here[1].Date.Day}. {here[1].Date.Month}.";
            return details.ToPremiere(english, popularity, NewsRegion.Domestic, here[0].Date, note);
        }

        return details.ToPremiere(english, popularity, NewsRegion.World, details.Release ?? here[0].Date, CinemaNote(here));
    }

    // a film of the world: a Czech or Slovak one goes among the cinemas here; a foreign one on its first release, with the
    // dates of the cinemas here (if any are to come) as a note
    private static FilmPremiere? OfTheWorld(Details details, string english, double popularity, DateOnly from, DateOnly to)
    {
        if (IsHome(details))
        {
            return InCinemasHere(details, english, popularity, from, to);
        }

        return details.Release is { } release && release >= from && release <= to
            ? details.ToPremiere(english, popularity, NewsRegion.World, release, CinemaNote(HereDates(details, from, DateOnly.MaxValue)))
            : null;
    }

    // the theatrical premieres here (types 2 and 3) within the dates, the earlier one first
    private static List<Release> HereDates(Details details, DateOnly from, DateOnly to) =>
        details.Dates.Where(d => d.Country is "CZ" or "SK" && d.Type is 2 or 3 && d.Date >= from && d.Date <= to)
            .GroupBy(d => d.Country).Select(g => g.OrderBy(d => d.Date).First()).OrderBy(d => d.Date).ToList();

    // "v kinách CZ · SK od 6. 11.", "v kinách CZ od 6. 11. · SK od 13. 11." or empty
    private static string CinemaNote(IReadOnlyList<Release> here) =>
        here.Count == 0 ? ""
        : here.Count == 1 || here[0].Date == here[1].Date ? $"v kinách {string.Join(" · ", here.Select(d => d.Country))} od {Short(here[0].Date)}"
        : $"v kinách {here[0].Country} od {Short(here[0].Date)} · {here[1].Country} od {Short(here[1].Date)}";

    private static string Short(DateOnly day) => $"{day.Day}. {day.Month}.";

    /// <summary>
    /// The kept films by origin, one per region and film: films kept before 29. 10. 2026 were sorted by where they premiere
    /// – a foreign film of the cinemas here goes to the world (its dates here as a note), a Czech or Slovak one of the
    /// world here. A film already among the world keeps its entry there.
    /// </summary>
    public static List<FilmPremiere> Classify(IEnumerable<FilmPremiere> films) =>
        films.Select(f => (Film: Classify(f), Moved: f.Origin is null && Classify(f).Region != f.Region))
            .GroupBy(f => (f.Film.Region, f.Film.TmdbId))
            .Select(g => g.FirstOrDefault(f => !f.Moved).Film is { } stays
                ? stays.PremiereNote.Length == 0 && g.FirstOrDefault(f => f.Moved).Film is { PremiereNote.Length: > 0 } moved ? stays with { PremiereNote = moved.PremiereNote } : stays
                : g.First().Film)
            .ToList();

    /// <inheritdoc cref="Classify(IEnumerable{FilmPremiere})"/>
    public static FilmPremiere Classify(FilmPremiere film)
    {
        if (film.Origin is not null)
        {
            return film;
        }

        var home = film.Countries.Count > 0 || film.Language.Length > 0
            ? film.Countries.Any(HomeOrigin.IsHomeCountry) || HomeOrigin.IsHomeLanguage(film.Language)
            : film.Production.Split(" · ")[0].Split(", ").Any(c => c is "ČR" or "SR");
        var region = home ? NewsRegion.Domestic : NewsRegion.World;
        var note = region == film.Region ? film.PremiereNote
            : home ? ""
            : OldNote().Match(film.PremiereNote) is { Success: true } old
                ? old.Groups[3].Success
                    ? $"v kinách {old.Groups[1].Value} od {Short(film.Premiere)} · {old.Groups[2].Value} od {old.Groups[3].Value}"
                    : $"v kinách {string.Join(" · ", new[] { old.Groups[1].Value, old.Groups[2].Value }.Where(c => c.Length > 0))} od {Short(film.Premiere)}"
                : "";
        return film with { Region = region, Origin = region, PremiereNote = note };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(CZ|SK)(?: · (CZ|SK)(?: (\d{1,2}\. \d{1,2}\.))?)?$")]
    private static partial System.Text.RegularExpressions.Regex OldNote();

    /// <summary>The most popular films whose first release (anywhere) is between the dates; a Czech or Slovak one only with its premiere here.</summary>
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
            if (await DetailsAsync(film.Id, cancellationToken).ConfigureAwait(false) is { } details && OfTheWorld(details, film.English, film.Popularity, from, to) is { } premiere)
            {
                films.Add(premiere);
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
        original_language = Text(root, "original_language"),
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
        /// <summary>The original language (ISO code), when TMDB tells it.</summary>
        public string Language { get; init; } = "";

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
                    ? new Release(country, type, day) : null).OfType<Release>().ToList())
            {
                Language = WebText.Clean(Text(root, "original_language"), 10),
            };
        }

        public FilmPremiere ToPremiere(string english, double popularity, NewsRegion region, DateOnly premiere, string note) => new()
        {
            TmdbId = Id,
            OriginalTitle = Original,
            EnglishTitle = english.Length > 0 ? WebText.Clean(english, 300) : Original,
            LocalTitle = Title.Length > 0 && Title != Original ? Title : "",
            Year = Release?.Year ?? premiere.Year,
            Region = region,
            Origin = region,
            Premiere = premiere,
            PremiereNote = note,
            Countries = Countries.Where(c => c.Length is > 0 and <= 3).Take(10).ToList(),
            Language = Language,
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
