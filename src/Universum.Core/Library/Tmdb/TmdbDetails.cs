using System.Globalization;
using System.Text.Json;
using Universum.Core.Posters;

namespace Universum.Core.Library.Tmdb;

/// <summary>A film of a collection (TMDB "collection"), for the offer of the other films of the series of films.</summary>
public sealed record TmdbCollectionPart(int Id, string Title, string OriginalTitle, int? Year, string? PosterPath)
{
    /// <summary>"Original (Year)" – as the library names it.</summary>
    public string Caption => Year is { } year ? $"{OriginalTitle} ({year.ToString(CultureInfo.InvariantCulture)})" : OriginalTitle;
}

/// <summary>The poster of a season of a series (TMDB's main one).</summary>
public sealed record TmdbSeasonPoster(int Number, string PosterPath);

/// <summary>The still of an episode (a picture from it).</summary>
public sealed record TmdbStill(int Season, int Episode, string Path);

/// <summary>The pictures of a title: posters, backgrounds (backdrops) and title logos, each with its language, size and rating.</summary>
public sealed record TmdbPictures(IReadOnlyList<TmdbPicture> Posters, IReadOnlyList<TmdbPicture> Backdrops, IReadOnlyList<TmdbPicture> Logos);

/// <summary>A collection of films (TMDB: "belongs_to_collection") with its films in the order of their years.</summary>
public sealed record TmdbCollection(int Id, string Name, IReadOnlyList<TmdbCollectionPart> Parts);

/// <summary>
/// The detail of a film or series for the title card (owner, 31. 10. 2026): the names (original, English, Czech,
/// Slovak), length, rating, genres, directors (series: creators), actors, IDs, the collection and the posters.
/// </summary>
public sealed record TmdbDetails
{
    public required MediaKind Kind { get; init; }

    public required int Id { get; init; }

    public required string OriginalTitle { get; init; }

    /// <summary>The English title (TMDB gives the original when there is no English one).</summary>
    public required string EnglishTitle { get; init; }

    public string CzechTitle { get; init; } = "";

    public string SlovakTitle { get; init; } = "";

    /// <summary>The original language (ISO 639-1).</summary>
    public string OriginalLanguage { get; init; } = "";

    public int? Year { get; init; }

    /// <summary>The length of the film, or of an episode of the series, in minutes.</summary>
    public int? Runtime { get; init; }

    public int? Seasons { get; init; }

    public int? Episodes { get; init; }

    /// <summary>TMDB's rating 0–10 and the number of its votes.</summary>
    public double? Rating { get; init; }

    public int RatingVotes { get; init; }

    /// <summary>TMDB's genre numbers (<see cref="TmdbGenres"/>).</summary>
    public IReadOnlyList<int> Genres { get; init; } = [];

    /// <summary>Directors of a film; creators of a series.</summary>
    public IReadOnlyList<string> Directors { get; init; } = [];

    /// <summary>The first actors as TMDB orders them.</summary>
    public IReadOnlyList<string> Cast { get; init; } = [];

    /// <summary>The plot in Czech, else Slovak, else English.</summary>
    public string Overview { get; init; } = "";

    /// <summary>The plot in English (for a poster drawn by AI).</summary>
    public string EnglishOverview { get; init; } = "";

    public string? Imdb { get; init; }

    public int? Tvdb { get; init; }

    public string? PosterPath { get; init; }

    /// <summary>The collection a film belongs to (its ID and name; the films are read by <see cref="TmdbTitleDetails.CollectionAsync"/>).</summary>
    public int? CollectionId { get; init; }

    public string CollectionName { get; init; } = "";

    /// <summary>The posters of the seasons of a series (without the specials).</summary>
    public IReadOnlyList<TmdbSeasonPoster> SeasonPosters { get; init; } = [];

    /// <summary>The library title by the specification (original title in Latin script, else English title and ID).</summary>
    public TitleInfo? Title => TitleInfo.FromTmdb(Kind, Id, OriginalTitle, EnglishTitle, Year);

    /// <summary>The names UNIVERSUM recognises the title by.</summary>
    public IReadOnlyList<string> Names => new[] { OriginalTitle, EnglishTitle, CzechTitle, SlovakTitle }.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Reads the detail of a title, its posters and its collection from TMDB – each one request, kept in the cache (details
/// and collections 30 days, the posters 3 days: new ones come), compacted to what UNIVERSUM shows. Only IDs are sent.
/// </summary>
public sealed class TmdbTitleDetails(TmdbClient tmdb)
{
    /// <summary>Actors kept and shown at most.</summary>
    public const int MaxCast = 12;

    private static readonly TimeSpan DetailLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan PictureLifetime = TimeSpan.FromDays(3);

    /// <summary>The detail of a title; null when TMDB has no title with this ID.</summary>
    public async Task<TmdbDetails?> GetAsync(MediaKind kind, int id, CancellationToken cancellationToken = default)
    {
        // (the order of the parameters names the cache entry: details kept before the season posters had none)
        var path = $"{Kind(kind)}/{id.ToString(CultureInfo.InvariantCulture)}?append_to_response=credits,external_ids,translations&language=en-US";
        using var document = await tmdb.GetCompactAsync(path, DetailLifetime, root => CompactDetails(kind, root), cancellationToken).ConfigureAwait(false);
        return document is null ? null : ParseDetails(kind, id, document.RootElement);
    }

    /// <summary>
    /// The posters of a title in Czech, Slovak, English, its original language and without text (TMDB's list of images,
    /// each with its language, size and rating).
    /// </summary>
    public async Task<IReadOnlyList<TmdbPicture>> PostersAsync(MediaKind kind, int id, string? originalLanguage, CancellationToken cancellationToken = default) =>
        (await PicturesAsync(kind, id, originalLanguage, cancellationToken).ConfigureAwait(false)).Posters;

    /// <summary>The posters, backgrounds and title logos of a title (one request; the languages as of the posters).</summary>
    public async Task<TmdbPictures> PicturesAsync(MediaKind kind, int id, string? originalLanguage, CancellationToken cancellationToken = default)
    {
        // (null first: the order names the cache entry – pictures kept before had no backgrounds and logos)
        var languages = new[] { "null", "cs", "sk", "en", originalLanguage?.ToLowerInvariant() }
            .Where(l => l is { Length: 2 } || l == "null").Distinct(StringComparer.Ordinal);
        var path = $"{Kind(kind)}/{id.ToString(CultureInfo.InvariantCulture)}/images?include_image_language={string.Join(",", languages)}";
        using var document = await tmdb.GetCompactAsync(path, PictureLifetime, CompactPictures, cancellationToken).ConfigureAwait(false);
        List<TmdbPicture> Of(string property) =>
            document is not null && document.RootElement.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(p => new TmdbPicture(
                    Text(p, "f") ?? "", Text(p, "l"), Int(p, "w") ?? 0, Int(p, "h") ?? 0,
                    p.TryGetProperty("v", out var v) && v.TryGetDouble(out var vote) ? vote : 0, Int(p, "c") ?? 0)).Where(p => p.Path.Length > 0).ToList()
                : [];
        return new TmdbPictures(Of("p"), Of("b"), Of("o"));
    }

    /// <summary>The stills of the episodes of a season (one request, kept 30 days).</summary>
    public async Task<IReadOnlyList<TmdbStill>> StillsAsync(int seriesId, int season, CancellationToken cancellationToken = default)
    {
        var path = $"tv/{seriesId.ToString(CultureInfo.InvariantCulture)}/season/{season.ToString(CultureInfo.InvariantCulture)}?language=en-US";
        using var document = await tmdb.GetCompactAsync(path, DetailLifetime, CompactStills, cancellationToken).ConfigureAwait(false);
        return document is not null && document.RootElement.TryGetProperty("e", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => new TmdbStill(season, Int(e, "n") ?? 0, Text(e, "s") ?? ""))
                .Where(s => s.Episode > 0 && PosterCache.IsValidPath(s.Path)).ToList()
            : [];
    }

    /// <summary>The films of a collection, the earliest first; null when TMDB does not know it.</summary>
    public async Task<TmdbCollection?> CollectionAsync(int id, CancellationToken cancellationToken = default)
    {
        using var document = await tmdb.GetCompactAsync($"collection/{id.ToString(CultureInfo.InvariantCulture)}?language=en-US", DetailLifetime, CompactCollection, cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        var parts = root.TryGetProperty("p", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(p => new TmdbCollectionPart(Int(p, "i") ?? 0, Text(p, "t") ?? "", Text(p, "o") ?? Text(p, "t") ?? "", Int(p, "y"),
                    PosterCache.IsValidPath(Text(p, "p")) ? Text(p, "p") : null))
                .Where(p => p.Id > 0 && p.OriginalTitle.Length > 0).OrderBy(p => p.Year ?? int.MaxValue).ToList()
            : [];
        return new TmdbCollection(id, Text(root, "n") ?? "", parts);
    }

    private static string Kind(MediaKind kind) => kind == MediaKind.Movie ? "movie" : "tv";

    private static TmdbDetails? ParseDetails(MediaKind kind, int id, JsonElement root)
    {
        var original = Text(root, "o");
        if (string.IsNullOrWhiteSpace(original))
        {
            return null;
        }

        return new TmdbDetails
        {
            Kind = kind,
            Id = id,
            OriginalTitle = original,
            EnglishTitle = Text(root, "t") ?? original,
            CzechTitle = Text(root, "cs") ?? "",
            SlovakTitle = Text(root, "sk") ?? "",
            OriginalLanguage = Text(root, "l") ?? "",
            Year = Int(root, "y"),
            Runtime = Int(root, "r"),
            Seasons = Int(root, "s"),
            Episodes = Int(root, "e"),
            Rating = root.TryGetProperty("v", out var v) && v.TryGetDouble(out var vote) && vote > 0 ? vote : null,
            RatingVotes = Int(root, "vc") ?? 0,
            Genres = Ints(root, "g"),
            Directors = Strings(root, "d"),
            Cast = Strings(root, "a"),
            Overview = Text(root, "ov") ?? "",
            EnglishOverview = Text(root, "oe") ?? "",
            Imdb = TitleIds.ValidImdb(Text(root, "imdb")),
            Tvdb = Int(root, "tvdb") is > 0 and var tvdb ? tvdb : null,
            PosterPath = PosterCache.IsValidPath(Text(root, "p")) ? Text(root, "p") : null,
            CollectionId = Int(root, "ci") is > 0 and var collection ? collection : null,
            CollectionName = Text(root, "cn") ?? "",
            SeasonPosters = Array(root, "sp").Select(s => new TmdbSeasonPoster(Int(s, "n") ?? 0, Text(s, "p") ?? ""))
                .Where(s => s.Number > 0 && PosterCache.IsValidPath(s.PosterPath)).ToList(),
        };
    }

    // a detail keeps the names, years, length, rating, genres, people, IDs and the collection
    internal static string CompactDetails(MediaKind kind, JsonElement root)
    {
        var movie = kind == MediaKind.Movie;
        string? Translated(string language, string field) => Array(Object(root, "translations"), "translations")
            .Where(t => Text(t, "iso_639_1") == language)
            .OrderByDescending(t => Text(t, "iso_3166_1") == (language == "cs" ? "CZ" : language == "sk" ? "SK" : "US"))
            .Select(t => Text(Object(t, "data"), field)).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim();
        var date = Text(root, movie ? "release_date" : "first_air_date");
        var credits = Object(root, "credits");
        var directors = movie
            ? Array(credits, "crew").Where(c => Text(c, "job") == "Director").Select(c => Text(c, "name"))
            : Array(root, "created_by").Select(c => Text(c, "name"));
        var runtime = movie ? Int(root, "runtime")
            : Array(root, "episode_run_time").Any() ? Int(root, "episode_run_time", first: true)
            : Int(Object(root, "last_episode_to_air"), "runtime");
        var collection = Object(root, "belongs_to_collection");
        var ids = Object(root, "external_ids");
        return JsonSerializer.Serialize(new
        {
            o = Text(root, movie ? "original_title" : "original_name"),
            t = Text(root, movie ? "title" : "name"),
            cs = Translated("cs", movie ? "title" : "name"),
            sk = Translated("sk", movie ? "title" : "name"),
            l = Text(root, "original_language"),
            y = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : (int?)null,
            r = runtime is > 0 ? runtime : null,
            s = movie ? null : Int(root, "number_of_seasons"),
            e = movie ? null : Int(root, "number_of_episodes"),
            v = root.TryGetProperty("vote_average", out var vote) && vote.TryGetDouble(out var average) ? average : 0,
            vc = Int(root, "vote_count") ?? 0,
            g = Array(root, "genres").Select(g => Int(g, "id")).OfType<int>().ToList(),
            d = directors.OfType<string>().Distinct(StringComparer.Ordinal).Take(6).ToList(),
            a = Array(credits, "cast").Select(c => Text(c, "name")).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxCast).ToList(),
            ov = Translated("cs", "overview") ?? Translated("sk", "overview") ?? Text(root, "overview"),
            oe = Text(root, "overview"),
            imdb = Text(ids, "imdb_id") ?? Text(root, "imdb_id"),
            tvdb = Int(ids, "tvdb_id"),
            p = Text(root, "poster_path"),
            ci = Int(collection, "id"),
            cn = Text(collection, "name"),
            sp = Array(root, "seasons").Select(s => new { n = Int(s, "season_number"), p = Text(s, "poster_path") }).Where(s => s.n > 0 && s.p is not null).ToList(),
        });
    }

    // the posters, backgrounds and logos keep their file, language, size and rating
    internal static string CompactPictures(JsonElement root)
    {
        object Of(string property, int most) => Array(root, property).Select(p => new
        {
            f = Text(p, "file_path"),
            l = Text(p, "iso_639_1"),
            w = Int(p, "width"),
            h = Int(p, "height"),
            v = p.TryGetProperty("vote_average", out var v) && v.TryGetDouble(out var vote) ? vote : 0,
            c = Int(p, "vote_count"),
        }).Where(p => PosterCache.IsValidPath(p.f)).Take(most).ToList();
        return JsonSerializer.Serialize(new { p = Of("posters", 300), b = Of("backdrops", 100), o = Of("logos", 60) });
    }

    // a season keeps the stills of its episodes
    internal static string CompactStills(JsonElement root) => JsonSerializer.Serialize(new
    {
        e = Array(root, "episodes").Select(e => new { n = Int(e, "episode_number"), s = Text(e, "still_path") }).Where(e => e.s is not null).ToList(),
    });

    // a collection keeps the names, years and posters of its films
    internal static string CompactCollection(JsonElement root) => JsonSerializer.Serialize(new
    {
        n = Text(root, "name"),
        p = Array(root, "parts").Select(p => new
        {
            i = Int(p, "id"),
            t = Text(p, "title"),
            o = Text(p, "original_title"),
            y = Text(p, "release_date") is { Length: >= 4 } date && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : (int?)null,
            p = Text(p, "poster_path"),
        }).ToList(),
    });

    private static JsonElement Object(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string property, bool first = false)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (first && value.ValueKind == JsonValueKind.Array)
        {
            value = value.EnumerateArray().FirstOrDefault();
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    private static List<int> Ints(JsonElement element, string property) => Array(element, property).Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _)).Select(e => e.GetInt32()).ToList();

    private static List<string> Strings(JsonElement element, string property) =>
        Array(element, property).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0).ToList();
}
