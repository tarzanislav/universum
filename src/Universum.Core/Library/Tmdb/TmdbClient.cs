using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Universum.Core.Text;

namespace Universum.Core.Library.Tmdb;

/// <summary>A film or series as TMDB describes it (English data, original title and language).</summary>
/// <param name="Title">English title (TMDB falls back to the original when there is no English one).</param>
/// <param name="Seasons">Seasons of a series with their episode counts (only from details, not from searches).</param>
public sealed record TmdbTitle(
    MediaKind Kind, int Id, string Title, string OriginalTitle, string? OriginalLanguage, int? Year, IReadOnlyList<TmdbSeason> Seasons)
{
    /// <summary>The library title by the specification: original title in Latin script, otherwise English title + ID.</summary>
    public TitleInfo? ToTitleInfo() => TitleInfo.FromTmdb(Kind, Id, OriginalTitle, Title, Year);

    /// <summary>"Creep (2004)" or "Kantara: A Legend Chapter-1 (2025) · ಕಾಂತಾರ" for lists.</summary>
    public string Display =>
        Title + (Year is { } year ? $" ({year.ToString(CultureInfo.InvariantCulture)})" : "") +
        (string.Equals(Title, OriginalTitle, StringComparison.Ordinal) ? "" : " · " + OriginalTitle);

    /// <summary>True when the season and episode exist on TMDB (unknown seasons count as existing).</summary>
    public bool HasEpisode(EpisodeNumber episode) => TmdbSeasons.Has(Seasons, episode);
}

public sealed record TmdbSeason(int Number, int EpisodeCount);

public static class TmdbSeasons
{
    /// <summary>
    /// True when the season exists and has the episode (all of them, for a file with several); true as well when the
    /// seasons are not known or TMDB gives no episode count for the season.
    /// </summary>
    public static bool Has(IReadOnlyList<TmdbSeason> seasons, EpisodeNumber episode)
    {
        var season = seasons.FirstOrDefault(s => s.Number == episode.Season);
        return seasons.Count == 0 || (season is not null && (season.EpisodeCount == 0 || (episode.LastEpisode ?? episode.Episode) <= season.EpisodeCount));
    }
}

public sealed class TmdbException(string message, Exception? inner = null) : TitleServiceException(message, inner);

/// <summary>
/// Reads titles from the TMDB API (https://api.themoviedb.org/3) with the user's own key – an API key (v3) or an API
/// Read Access Token (sent as a bearer token). Only titles and IDs are sent. Answers are cached (details 30 days,
/// searches 7 days); the cache never contains the key. Requests go one at a time through a <see cref="RequestPacer"/>
/// (4 per second); "too many requests" (429) and passing server errors are waited out and retried.
/// </summary>
public sealed class TmdbClient
{
    /// <summary>Notice TMDB requires in applications that use its API.</summary>
    public const string Attribution = "This product uses the TMDB API but is not endorsed or certified by TMDB.";

    private static readonly Uri BaseAddress = new("https://api.themoviedb.org/3/");
    private static readonly TimeSpan DetailsLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan SearchLifetime = TimeSpan.FromDays(7);
    private const int MaxRetriesWhenLimited = 4;
    private const int MaxRetriesOnServerError = 2;

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string? _cacheFile;
    private readonly Dictionary<string, CacheEntry> _cache;
    private readonly RequestPacer _pacer;

    /// <param name="pacer">Spacing of requests; the shared <see cref="RequestPacer.Tmdb"/> by default.</param>
    public TmdbClient(HttpClient http, string key, string? cacheFile = null, RequestPacer? pacer = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A TMDB key is required.", nameof(key));
        }

        _http = http;
        _key = key.Trim();
        _cacheFile = cacheFile;
        _cache = LoadCache(cacheFile);
        _pacer = pacer ?? RequestPacer.Tmdb;
    }

    /// <summary>Raised when TMDB asked to slow down and the client waits before trying again.</summary>
    public event Action<TimeSpan>? Waiting;

    /// <summary>True for an API Read Access Token (a JWT), false for a v3 API key.</summary>
    public bool UsesBearerToken => _key.Count(c => c == '.') == 2 && _key.Length > 60;

    /// <summary>Details of a film or series; null when TMDB has no title with this ID.</summary>
    public async Task<TmdbTitle?> GetAsync(MediaKind kind, int id, CancellationToken cancellationToken = default)
    {
        var path = (kind == MediaKind.Movie ? "movie/" : "tv/") + id.ToString(CultureInfo.InvariantCulture) + "?language=en-US";
        using var document = await GetJsonAsync(path, DetailsLifetime, cancellationToken).ConfigureAwait(false);
        return document is null ? null : Parse(kind, document.RootElement, withSeasons: true);
    }

    /// <summary>Titles matching a name (and year, when given), best first as TMDB orders them.</summary>
    public async Task<IReadOnlyList<TmdbTitle>> SearchAsync(MediaKind kind, string query, int? year, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var path = (kind == MediaKind.Movie ? "search/movie" : "search/tv") + "?language=en-US&include_adult=false&query=" + Uri.EscapeDataString(query.Trim());
        if (year is { } y)
        {
            path += (kind == MediaKind.Movie ? "&year=" : "&first_air_date_year=") + y.ToString(CultureInfo.InvariantCulture);
        }

        using var document = await GetJsonAsync(path, SearchLifetime, cancellationToken).ConfigureAwait(false);
        if (document is null || !document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return results.EnumerateArray().Select(r => Parse(kind, r, withSeasons: false)).OfType<TmdbTitle>().ToList();
    }

    /// <summary>
    /// What the user typed into a title field: a TMDB ID or address gives that title; otherwise the text is searched as
    /// "Name (Year)".
    /// </summary>
    public async Task<IReadOnlyList<TmdbTitle>> FindAsync(MediaKind kind, string? text, CancellationToken cancellationToken = default)
    {
        if (TitleGuess.ParseTmdb(text) is { } reference)
        {
            var title = await GetAsync(reference.Kind ?? kind, reference.Id, cancellationToken).ConfigureAwait(false);
            return title is null ? [] : [title];
        }

        if (TitleGuess.Typed(kind, text) is not { } typed)
        {
            return [];
        }

        if (typed.TmdbId is { } id && await GetAsync(kind, id, cancellationToken).ConfigureAwait(false) is { } byId)
        {
            return [byId];
        }

        var results = await SearchAsync(kind, typed.Name, typed.Year, cancellationToken).ConfigureAwait(false);
        return results.Count == 0 && typed.Year is not null
            ? await SearchAsync(kind, typed.Name, null, cancellationToken).ConfigureAwait(false)
            : results;
    }

    /// <summary>
    /// The one result that matches a guessed name exactly (title or original title, ignoring case and diacritics) and,
    /// when the guess has a year, the year; null when there is none or more than one.
    /// </summary>
    public static TmdbTitle? ExactMatch(IReadOnlyList<TmdbTitle> results, string name, int? year)
    {
        var wanted = Words.Normalize(name);
        var matches = results.Where(r =>
                (Words.Normalize(r.Title) == wanted || Words.Normalize(r.OriginalTitle) == wanted) &&
                (year is null || r.Year == year))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Saves the cache, when a cache file was given.</summary>
    public void SaveCache()
    {
        if (_cacheFile is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cacheFile))!);
        lock (_cache)
        {
            File.WriteAllText(_cacheFile, JsonSerializer.Serialize(_cache));
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(path, out var cached) && DateTime.UtcNow - cached.TimeUtc < lifetime)
            {
                return cached.Body is null ? null : JsonDocument.Parse(cached.Body);
            }
        }

        for (var attempt = 0; ; attempt++)
        {
            using var request = Request(path);
            HttpResponseMessage response;
            try
            {
                response = await _pacer.RunAsync(() => _http.SendAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new TmdbException("TMDB nie je dostupné: " + ex.Message, ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TmdbException("TMDB neodpovedá.", ex);
            }

            using (response)
            {
                // too many requests or a passing server problem: wait (as long as TMDB asks) and try again
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway
                    or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError)
                {
                    var limited = response.StatusCode == HttpStatusCode.TooManyRequests;
                    if (attempt >= (limited ? MaxRetriesWhenLimited : MaxRetriesOnServerError))
                    {
                        throw new TmdbException(limited
                            ? "TMDB obmedzilo počet otázok a ani po čakaní ich neprijíma – skúste to o niekoľko minút."
                            : $"TMDB má problém ({(int)response.StatusCode}) – skúste to neskôr.");
                    }

                    var delay = RequestPacer.RetryDelay(response, attempt);
                    _pacer.PauseFor(delay);
                    Waiting?.Invoke(delay);
                    continue;
                }

                string? body = null;
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case HttpStatusCode.NotFound:
                        break;
                    case HttpStatusCode.Unauthorized:
                        throw new TmdbException("Kľúč TMDB nie je platný – skontrolujte ho v Nastaveniach.");
                    default:
                        throw new TmdbException($"TMDB odpovedalo chybou {(int)response.StatusCode}.");
                }

                JsonDocument? document;
                try
                {
                    document = body is null ? null : JsonDocument.Parse(body);
                }
                catch (JsonException ex)
                {
                    throw new TmdbException("TMDB vrátilo nečitateľnú odpoveď.", ex);
                }

                lock (_cache)
                {
                    _cache[path] = new CacheEntry(DateTime.UtcNow, body);
                }

                return document;
            }
        }
    }

    private HttpRequestMessage Request(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress, UsesBearerToken ? path : path + "&api_key=" + Uri.EscapeDataString(_key)));
        if (UsesBearerToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static TmdbTitle? Parse(MediaKind kind, JsonElement element, bool withSeasons)
    {
        if (!element.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
        {
            return null;
        }

        var movie = kind == MediaKind.Movie;
        var title = Text(element, movie ? "title" : "name");
        var original = Text(element, movie ? "original_title" : "original_name") ?? title;
        if (string.IsNullOrWhiteSpace(original))
        {
            return null;
        }

        var date = Text(element, movie ? "release_date" : "first_air_date");
        int? year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
        var seasons = new List<TmdbSeason>();
        if (withSeasons && element.TryGetProperty("seasons", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var season in list.EnumerateArray())
            {
                if (season.TryGetProperty("season_number", out var number) && number.TryGetInt32(out var n))
                {
                    seasons.Add(new TmdbSeason(n, season.TryGetProperty("episode_count", out var count) && count.TryGetInt32(out var c) ? c : 0));
                }
            }
        }

        return new TmdbTitle(kind, id, title ?? original, original, Text(element, "original_language"), year, seasons);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Dictionary<string, CacheEntry> LoadCache(string? file)
    {
        try
        {
            if (file is not null && File.Exists(file))
            {
                return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(file)) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // the cache is only an optimisation
        }

        return [];
    }

    private sealed record CacheEntry(DateTime TimeUtc, string? Body);
}
