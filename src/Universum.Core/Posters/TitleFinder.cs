using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Text;

namespace Universum.Core.Posters;

/// <summary>A film or series found on TMDB (the Czech title when TMDB has one).</summary>
public sealed record FoundTitle(MediaKind Kind, int Id, string Title, string OriginalTitle, int? Year, string? PosterPath, double Popularity)
{
    /// <summary>"Original (Year)", and the Czech title when it differs.</summary>
    public string Caption => (Year is { } year ? $"{OriginalTitle} ({year.ToString(CultureInfo.InvariantCulture)})" : OriginalTitle) +
                             (Words.Normalize(Title) != Words.Normalize(OriginalTitle) ? " · " + Title : "");

    public string KindText => Kind == MediaKind.Movie ? "film" : "seriál";
}

/// <summary>
/// Finds films and series on TMDB as the user writes them (owner, 31. 10. 2026): a TMDB ID or address, an IMDb ID, or a
/// name – original, Czech, Slovak or English, in any script – with or without the year; a name written with a typo is
/// found by its words and by the similarity of the names. Each search is one request (a typo a few more), kept 7 days.
/// </summary>
public sealed partial class TitleFinder(TmdbClient tmdb)
{
    /// <summary>Results given at most.</summary>
    public const int MaxResults = 20;

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Titles for the text, the best first; <paramref name="kind"/> null: films and series.</summary>
    public async Task<IReadOnlyList<FoundTitle>> FindAsync(string? text, MediaKind? kind, CancellationToken cancellationToken = default)
    {
        var typed = text?.Trim() ?? "";
        if (typed.Length == 0)
        {
            return [];
        }

        // an IMDb ID ("tt0133093", also in an address)
        if (ImdbId().Match(typed) is { Success: true } imdb)
        {
            return Ranked(await GetListAsync($"find/{imdb.Value}?external_source=imdb_id&language=cs-CZ", null, cancellationToken).ConfigureAwait(false), kind, "", null);
        }

        // a TMDB ID or address: the film and the series of that number, unless the address says which; a bare number may
        // also be a name ("1917"), so it is searched as well
        if (TitleGuess.ParseTmdb(typed) is { } reference)
        {
            var found = new List<FoundTitle>();
            foreach (var k in reference.Kind is { } known ? [known] : kind is { } wanted ? [wanted] : new[] { MediaKind.Movie, MediaKind.Series })
            {
                found.AddRange(await GetListAsync($"{(k == MediaKind.Movie ? "movie" : "tv")}/{reference.Id.ToString(CultureInfo.InvariantCulture)}?language=cs-CZ", k, cancellationToken)
                    .ConfigureAwait(false));
            }

            if (!typed.All(char.IsAsciiDigit))
            {
                return found;
            }

            found.AddRange(Ranked(await SearchAsync(typed, null, kind, cancellationToken).ConfigureAwait(false), kind, typed, null));
            return found.GroupBy(f => (f.Kind, f.Id)).Select(g => g.First()).Take(MaxResults).ToList();
        }

        var (name, year) = NameAndYear(typed);
        var results = await SearchAsync(name, year, kind, cancellationToken).ConfigureAwait(false);
        var ranked = Ranked(results, kind, name, year);
        if (ranked.Any(r => Similarity(name, r) >= 0.5))
        {
            return ranked;
        }

        // a typo: the name without diacritics, its words one by one, and the beginning of a single word – each title then by
        // how similar its name is
        var more = new List<FoundTitle>(results);
        var words = Words.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).OrderByDescending(w => w.Length).Take(2).ToList();
        var variants = new List<string> { Words.Normalize(name) };
        variants.AddRange(words.Count > 1 ? words : words.Select(w => w[..3]));
        foreach (var variant in variants.Where(v => v.Length > 0 && v != name).Distinct(StringComparer.Ordinal).Take(3))
        {
            more.AddRange(await SearchAsync(variant, null, kind, cancellationToken).ConfigureAwait(false));
        }

        var similar = Ranked(more, kind, name, year).Where(r => Similarity(name, r) >= 0.5).ToList();
        return similar.Count > 0 ? similar : ranked;
    }

    /// <summary>"Kapr návrat (2021)" or "Kapr návrat 2021" → the name and the year.</summary>
    public static (string Name, int? Year) NameAndYear(string text)
    {
        var match = TrailingYear().Match(text);
        return match.Success && match.Groups["name"].Value.Trim().Length > 0
            ? (match.Groups["name"].Value.Trim(), int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture))
            : (text.Trim(), null);
    }

    /// <summary>How similar the typed name is to a title's names 0–1 (1 the same, ignoring case, diacritics and punctuation).</summary>
    public static double Similarity(string typed, FoundTitle title) =>
        Math.Max(Similarity(typed, title.Title), Similarity(typed, title.OriginalTitle));

    /// <inheritdoc cref="Similarity(string, FoundTitle)"/>
    public static double Similarity(string typed, string name)
    {
        var a = Words.Normalize(typed);
        var b = Words.Normalize(name);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a == b)
        {
            return 1;
        }

        // the whole name, and its beginning as long as what was typed ("matirx" ~ "matrix reloaded")
        var whole = 1 - (Distance(a, b) / (double)Math.Max(a.Length, b.Length));
        var start = b.Length > a.Length ? 1 - (Distance(a, b[..a.Length]) / (double)a.Length) - 0.1 : 0;
        return Math.Max(whole, start);
    }

    // Damerau–Levenshtein (optimal string alignment): a swapped pair of letters is one typo
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    // films and series (multi) or one kind; with a year also the search of that year (multi has none)
    private async Task<List<FoundTitle>> SearchAsync(string name, int? year, MediaKind? kind, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString(name);
        var found = new List<FoundTitle>();
        if (kind is null)
        {
            found.AddRange(await GetListAsync($"search/multi?language=cs-CZ&include_adult=false&query={query}", null, cancellationToken).ConfigureAwait(false));
            if (year is not { } y || found.Any(f => f.Year == y))
            {
                return found;
            }
        }

        var y2 = year?.ToString(CultureInfo.InvariantCulture);
        foreach (var k in kind is { } only ? [only] : new[] { MediaKind.Movie, MediaKind.Series })
        {
            var path = k == MediaKind.Movie
                ? $"search/movie?language=cs-CZ&include_adult=false&query={query}" + (y2 is null ? "" : "&year=" + y2)
                : $"search/tv?language=cs-CZ&include_adult=false&query={query}" + (y2 is null ? "" : "&first_air_date_year=" + y2);
            found.AddRange(await GetListAsync(path, k, cancellationToken).ConfigureAwait(false));
        }

        return found;
    }

    // the titles in a TMDB answer: a list ("results"; find: "movie_results", "tv_results") or one title
    private async Task<List<FoundTitle>> GetListAsync(string path, MediaKind? kind, CancellationToken cancellationToken)
    {
        using var document = await tmdb.GetCompactAsync(path, Lifetime, root => Compact(root, kind), cancellationToken).ConfigureAwait(false);
        if (document is null || !document.RootElement.TryGetProperty("r", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.EnumerateArray().Select(r => new FoundTitle(
                r.GetProperty("k").GetString() == "tv" ? MediaKind.Series : MediaKind.Movie,
                r.GetProperty("i").GetInt32(),
                r.TryGetProperty("t", out var t) ? t.GetString() ?? "" : "",
                r.TryGetProperty("o", out var o) ? o.GetString() ?? "" : "",
                r.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null,
                r.TryGetProperty("p", out var p) && PosterCache.IsValidPath(p.GetString()) ? p.GetString() : null,
                r.TryGetProperty("pop", out var pop) && pop.TryGetDouble(out var popularity) ? popularity : 0))
            .Where(f => f.Id > 0 && (f.OriginalTitle.Length > 0 || f.Title.Length > 0))
            .Select(f => f.OriginalTitle.Length == 0 ? f with { OriginalTitle = f.Title } : f.Title.Length == 0 ? f with { Title = f.OriginalTitle } : f)
            .ToList();
    }

    // results by kind, the year (±1) and how well the name matches, then by popularity; each title once
    private static List<FoundTitle> Ranked(IEnumerable<FoundTitle> found, MediaKind? kind, string name, int? year) =>
        found.Where(f => kind is null || f.Kind == kind)
            .GroupBy(f => (f.Kind, f.Id)).Select(g => g.First())
            .Where(f => year is null || f.Year is null || Math.Abs(f.Year.Value - year.Value) <= 1)
            .OrderByDescending(f => (name.Length == 0 ? 0 : Similarity(name, f)) + (year is { } y && f.Year == y ? 0.3 : 0) + (Math.Log10(1 + f.Popularity) / 10))
            .Take(MaxResults)
            .ToList();

    internal static string Compact(JsonElement root, MediaKind? kind)
    {
        IEnumerable<(JsonElement Item, string Kind)> items;
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            items = results.EnumerateArray().Select(r => (r, kind is { } k ? (k == MediaKind.Movie ? "movie" : "tv") : Text(r, "media_type") ?? ""));
        }
        else if (root.TryGetProperty("movie_results", out _) || root.TryGetProperty("tv_results", out _))
        {
            items = Items(root, "movie_results").Select(r => (r, "movie")).Concat(Items(root, "tv_results").Select(r => (r, "tv")));
        }
        else
        {
            items = root.TryGetProperty("id", out _) ? [(root, kind == MediaKind.Series ? "tv" : "movie")] : [];
        }

        return JsonSerializer.Serialize(new
        {
            r = items.Where(x => x.Kind is "movie" or "tv" && x.Item.ValueKind == JsonValueKind.Object).Take(MaxResults).Select(x =>
            {
                var movie = x.Kind == "movie";
                var date = Text(x.Item, movie ? "release_date" : "first_air_date");
                return new
                {
                    k = x.Kind,
                    i = x.Item.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) ? number : 0,
                    t = Text(x.Item, movie ? "title" : "name"),
                    o = Text(x.Item, movie ? "original_title" : "original_name"),
                    y = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : (int?)null,
                    p = Text(x.Item, "poster_path"),
                    pop = x.Item.TryGetProperty("popularity", out var p) && p.TryGetDouble(out var popularity) ? popularity : 0,
                };
            }).ToList(),
        });
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string property) =>
        root.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"tt[0-9]{5,10}", RegexOptions.CultureInvariant)]
    private static partial Regex ImdbId();

    [GeneratedRegex(@"^(?<name>.+?)[\s.(\[]+(?<year>(?:18|19|20)[0-9]{2})[)\]]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYear();
}
