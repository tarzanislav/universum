using System.Globalization;
using System.Text.Json;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Text;

namespace Universum.Core.News;

/// <summary>Where a title can be watched legally: a service in a country, how (subscription, free, rent, buy) and since when it is known.</summary>
public sealed record WatchOffer(string Country, string Provider, string Kind, DateOnly Since)
{
    /// <summary>"predplatné", "zadarmo", "s reklamou", "požičanie", "kúpa".</summary>
    public string KindText => CoreText.T(Kind switch
    {
        "flatrate" => "predplatné",
        "free" => "zadarmo",
        "ads" => "s reklamou",
        "rent" => "požičanie",
        _ => "kúpa",
    });
}

/// <summary>The legal offers of one chosen title, as last checked.</summary>
public sealed record TitleAvailability(string Caption, MediaKind Kind, int TmdbId, DateTime Checked, List<WatchOffer> Offers)
{
    /// <summary>Offers to watch (subscription, free, with ads) first – renting and buying after them.</summary>
    public IEnumerable<WatchOffer> Streaming => Offers.Where(o => o.Kind is "flatrate" or "free" or "ads");

    /// <summary>"Netflix (SK) · Voyo (CZ)" – the services to watch on, or the shops when there are none.</summary>
    public string Text
    {
        get
        {
            var watch = Streaming.ToList();
            var shown = watch.Count > 0 ? watch : Offers;
            return string.Join(" · ", shown.GroupBy(o => o.Provider).Select(g => $"{g.Key} ({string.Join(", ", g.Select(o => o.Country).Distinct())})" +
                                                                                   (watch.Count == 0 ? " – " + g.First().KindText : "")));
        }
    }
}

/// <summary>
/// Legal availability of the chosen films and series on streaming services in Slovakia and Czechia, from TMDB (data
/// by JustWatch, credited); only the title's ID is sent. Kept in a file of this Windows account; an offer remembers
/// the day it was first seen, so new ones can be shown.
/// </summary>
public sealed class WatchProviders(TmdbClient tmdb)
{
    public const string Attribution = "Údaje o dostupnosti: JustWatch (cez TMDB)";

    private static readonly string[] Countries = ["SK", "CZ"];

    public async Task<IReadOnlyList<WatchOffer>> GetAsync(MediaKind kind, int tmdbId, DateOnly today, CancellationToken cancellationToken = default)
    {
        var path = (kind == MediaKind.Movie ? "movie/" : "tv/") + tmdbId.ToString(CultureInfo.InvariantCulture) + "/watch/providers?language=en-US";
        using var document = await tmdb.GetCompactAsync(path, TimeSpan.FromHours(20), Compact, cancellationToken).ConfigureAwait(false);
        if (document is null || !document.RootElement.TryGetProperty("o", out var offers) || offers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return offers.EnumerateArray()
            .Select(o => new WatchOffer(o.GetProperty("c").GetString() ?? "", WebText.Clean(o.GetProperty("p").GetString(), 60), o.GetProperty("k").GetString() ?? "", today))
            .Where(o => o.Country.Length > 0 && o.Provider.Length > 0)
            .ToList();
    }

    // only the offers of Slovakia and Czechia: country, service and kind
    private static string Compact(JsonElement root) => JsonSerializer.Serialize(new
    {
        o = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object
            ? Countries.Where(c => results.TryGetProperty(c, out _)).SelectMany(c => new[] { "flatrate", "free", "ads", "rent", "buy" }
                .SelectMany(k => results.GetProperty(c).TryGetProperty(k, out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(p => new { c, k, p = p.TryGetProperty("provider_name", out var n) ? n.GetString() : null })
                    : [])).Where(o => o.p is not null).ToList()
            : [],
    });

    /// <summary>The offers read now, each keeping the day it was first seen.</summary>
    public static List<WatchOffer> Merge(IEnumerable<WatchOffer> kept, IEnumerable<WatchOffer> read)
    {
        var since = kept.GroupBy(o => (o.Country, o.Provider, o.Kind)).ToDictionary(g => g.Key, g => g.Min(o => o.Since));
        return read.DistinctBy(o => (o.Country, o.Provider, o.Kind))
            .Select(o => since.TryGetValue((o.Country, o.Provider, o.Kind), out var first) ? o with { Since = first } : o).ToList();
    }
}

/// <summary>The kept availability of the chosen titles, by the key of their mark.</summary>
public sealed class AvailabilityStore(string file)
{
    public Dictionary<string, TitleAvailability> Load()
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, TitleAvailability>>(File.ReadAllBytes(file)) ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    public bool Save(Dictionary<string, TitleAvailability> availability)
    {
        try
        {
            Files.WriteAtomically(file, JsonSerializer.SerializeToUtf8Bytes(availability));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
