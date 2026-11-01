using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Universum.Core.Text;

namespace Universum.Core.Library;

/// <summary>The ratings of a title on IMDb, Rotten Tomatoes and Metacritic as OMDb gives them ("8,7", "88 %", "73"); any may be missing.</summary>
public sealed record OtherRatings(string? Imdb, string? ImdbVotes, string? RottenTomatoes, string? Metacritic)
{
    public bool IsEmpty => Imdb is null && RottenTomatoes is null && Metacritic is null;
}

/// <summary>
/// The ratings of IMDb, Rotten Tomatoes and Metacritic from OMDb (https://www.omdbapi.com, owner 31. 10. 2026) with the
/// user's own free key: only the IMDb ID is sent; answers are kept 7 days; one request per second at most (the free key
/// allows 1 000 a day).
/// </summary>
public sealed partial class OmdbClient(HttpClient http, string key, string? cacheFile = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private static readonly RequestPacer Pacer = new(1);
    private readonly Dictionary<string, (DateTime Time, OtherRatings? Ratings)> _cache = Load(cacheFile);

    /// <summary>An OMDb key as omdbapi.com gives it (letters and digits).</summary>
    public static bool IsValidKey(string? key) => key is not null && Key().IsMatch(key.Trim());

    /// <summary>The ratings of a title by its IMDb ID; null when OMDb does not know it. Throws <see cref="TitleServiceException"/> for a bad key or an unavailable service.</summary>
    public async Task<OtherRatings?> RatingsAsync(string imdbId, CancellationToken cancellationToken = default)
    {
        if (TitleIds.ValidImdb(imdbId) is not { } id)
        {
            return null;
        }

        lock (_cache)
        {
            if (_cache.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.Time < Lifetime)
            {
                return cached.Ratings;
            }
        }

        var address = new Uri($"https://www.omdbapi.com/?i={id}&apikey={Uri.EscapeDataString(key.Trim())}");
        string body;
        try
        {
            using var response = await Pacer.RunAsync(() => http.GetAsync(address, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new OmdbException(CoreText.T("Kľúč OMDb nie je platný alebo vyčerpal denný limit – skontrolujte ho v Nastaveniach."));
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new OmdbException(CoreText.F("OMDb odpovedalo chybou {0}.", (int)response.StatusCode));
            }

            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OmdbException(CoreText.T("OMDb nie je dostupné: ") + ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OmdbException(CoreText.T("OMDb neodpovedá."));
        }

        var ratings = Parse(body);
        lock (_cache)
        {
            _cache[id] = (DateTime.UtcNow, ratings);
        }

        Save();
        return ratings;
    }

    /// <summary>The ratings in an OMDb answer; null for "Response": "False".</summary>
    public static OtherRatings? Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (Text(root, "Response") != "True")
            {
                return null;
            }

            string? Of(string source) => root.TryGetProperty("Ratings", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(r => Text(r, "Source") == source).Select(r => Text(r, "Value")).FirstOrDefault()
                : null;
            var sk = CultureInfo.GetCultureInfo("sk-SK");
            var imdb = Real(Text(root, "imdbRating")) is { } rating && double.TryParse(rating, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value.ToString("0.0", sk)
                : null;
            var tomatoes = Of("Rotten Tomatoes") is { } percent ? percent.Replace("%", " %", StringComparison.Ordinal) : null;
            var metacritic = Real(Text(root, "Metascore")) ?? Of("Metacritic")?.Split('/')[0];
            return new OtherRatings(imdb, Real(Text(root, "imdbVotes")), tomatoes, metacritic);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Real(string? value) => string.IsNullOrWhiteSpace(value) || value == "N/A" ? null : value.Trim();

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void Save()
    {
        if (cacheFile is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
            lock (_cache)
            {
                File.WriteAllText(cacheFile, JsonSerializer.Serialize(_cache.ToDictionary(e => e.Key, e => new Entry(e.Value.Time, e.Value.Ratings))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the cache is only an optimisation
        }
    }

    private static Dictionary<string, (DateTime, OtherRatings?)> Load(string? file)
    {
        try
        {
            if (file is not null && File.Exists(file))
            {
                return (JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? []).ToDictionary(e => e.Key, e => (e.Value.Time, e.Value.Ratings));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // the cache is only an optimisation
        }

        return [];
    }

    private sealed record Entry(DateTime Time, OtherRatings? Ratings);

    [GeneratedRegex("^[A-Za-z0-9]{6,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex Key();
}

public sealed class OmdbException(string message) : TitleServiceException(message);
