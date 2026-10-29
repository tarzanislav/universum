using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Universum.Core.Library;
using Universum.Core.Text;

namespace Universum.Core.News;

/// <summary>
/// The TVmaze API (https://www.tvmaze.com/api – free, no key, data under CC BY-SA 4.0, credited with a link): the
/// schedule of a day by country, the schedule of streaming services, and the episodes of a series. At most two
/// requests per second (TVmaze allows 20 in 10 s); answers at most 8 MiB; nothing about the user is sent.
/// </summary>
public sealed class TvMazeClient(HttpClient http, RequestPacer? pacer = null)
{
    /// <summary>The API's address.</summary>
    public static Uri BaseAddress { get; } = new("https://api.tvmaze.com/");

    /// <summary>TVmaze asks to be credited with a link.</summary>
    public const string Attribution = "Údaje o seriáloch: TVmaze (CC BY-SA 4.0)";

    public const int MaxBytes = 8 << 20;

    /// <summary>The pacer of all TVmaze requests: 2 per second.</summary>
    public static RequestPacer Pacer { get; } = new(2);

    private readonly RequestPacer _pacer = pacer ?? Pacer;

    /// <summary>
    /// The episodes of a day: of a country's channels (US, GB …), or with <paramref name="country"/> null of all
    /// streaming services. Shows of the skipped types (news, talk shows …) and below the popularity weight are left out.
    /// </summary>
    public async Task<IReadOnlyList<SeriesEpisode>> ScheduleAsync(string? country, DateOnly day, TvMazeFilter filter, CancellationToken cancellationToken = default)
    {
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = country is null ? $"schedule/web?date={date}" : $"schedule?country={Uri.EscapeDataString(country)}&date={date}";
        using var document = await GetAsync(path, cancellationToken).ConfigureAwait(false);
        return document is null ? [] : ParseSchedule(document, filter, TimeZoneInfo.Local);
    }

    /// <summary>The air dates of a season of a series (empty when TVmaze does not know it).</summary>
    public async Task<IReadOnlyList<DateOnly>> SeasonDatesAsync(int show, int season, CancellationToken cancellationToken = default)
    {
        using var document = await GetAsync($"shows/{show.ToString(CultureInfo.InvariantCulture)}/episodes?specials=0", cancellationToken).ConfigureAwait(false);
        return document is null ? [] : ParseSeasonDates(document, season);
    }

    /// <summary>The series named so (TVmaze's best match); null when TVmaze has none.</summary>
    public async Task<TvMazeShow?> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        using var document = await GetAsync("singlesearch/shows?q=" + Uri.EscapeDataString(name), cancellationToken).ConfigureAwait(false);
        return document is null ? null : ParseShow(document.RootElement);
    }

    /// <summary>A series by its TVmaze ID; null when TVmaze has none.</summary>
    public async Task<TvMazeShow?> ShowAsync(int id, CancellationToken cancellationToken = default)
    {
        using var document = await GetAsync("shows/" + id.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        return document is null ? null : ParseShow(document.RootElement);
    }

    /// <summary>A show: its ID, name, the year it started, its language and the country of its channel.</summary>
    public static TvMazeShow? ParseShow(JsonElement show)
    {
        if (show.ValueKind != JsonValueKind.Object || Int(show, "id") is not { } id)
        {
            return null;
        }

        return new TvMazeShow(id, WebText.Clean(Text(show, "name"), 300), Year(Text(show, "premiered")), WebText.Clean(Text(show, "language"), 40), CountryOf(show));
    }

    // the country of the show's channel or streaming service ("" when TVmaze gives none)
    private static string CountryOf(JsonElement show)
    {
        foreach (var name in new[] { "network", "webChannel" })
        {
            if (show.TryGetProperty(name, out var channel) && channel.ValueKind == JsonValueKind.Object &&
                channel.TryGetProperty("country", out var country) && country.ValueKind == JsonValueKind.Object && Text(country, "code") is { Length: > 0 } code)
            {
                return code;
            }
        }

        return "";
    }

    public static IReadOnlyList<SeriesEpisode> ParseSchedule(JsonDocument document, TvMazeFilter filter, TimeZoneInfo zone)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var episodes = new List<SeriesEpisode>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var show = item.TryGetProperty("show", out var s) && s.ValueKind == JsonValueKind.Object ? s
                : item.TryGetProperty("_embedded", out var embedded) && embedded.ValueKind == JsonValueKind.Object &&
                  embedded.TryGetProperty("show", out var e) && e.ValueKind == JsonValueKind.Object ? e
                : default;
            if (show.ValueKind != JsonValueKind.Object || Text(show, "name") is not { Length: > 0 } name)
            {
                continue;
            }

            var type = Text(show, "type") ?? "";
            var weight = Int(show, "weight") ?? 0;
            if (weight < filter.MinWeight || filter.SkipTypes.Contains(type, StringComparer.OrdinalIgnoreCase) ||
                Text(item, "type") is "insignificant_special")
            {
                continue;
            }

            var channel = show.TryGetProperty("network", out var network) && network.ValueKind == JsonValueKind.Object ? network
                : show.TryGetProperty("webChannel", out var web) && web.ValueKind == JsonValueKind.Object ? web
                : default;
            var country = channel.ValueKind == JsonValueKind.Object && channel.TryGetProperty("country", out var c) && c.ValueKind == JsonValueKind.Object
                ? Text(c, "code") ?? "" : "";
            var day = Day(item, zone);
            if (day is null)
            {
                continue;
            }

            var season = Int(item, "season");
            var number = Int(item, "number");
            var externals = show.TryGetProperty("externals", out var x) && x.ValueKind == JsonValueKind.Object ? x : default;
            episodes.Add(new SeriesEpisode
            {
                Source = SeriesNews.TvMazeSource,
                Region = country is "CZ" or "SK" ? NewsRegion.Domestic : NewsRegion.World,
                Title = WebText.Clean(name, 300),
                OriginalTitle = WebText.Clean(name, 300),
                Year = Year(Text(show, "premiered")),
                Season = season,
                Number = number,
                AirDate = day.Value,
                Channel = channel.ValueKind == JsonValueKind.Object ? WebText.Clean(Text(channel, "name"), 100) : "",
                Country = country,
                Language = WebText.Clean(Text(show, "language"), 40),
                Genres = show.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array
                    ? WebText.Clean(string.Join(", ", genres.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString())), 200)
                    : "",
                Kind = number == 1 ? (season == 1 ? EpisodeKind.SeriesPremiere : EpisodeKind.SeasonPremiere) : EpisodeKind.Episode,
                ShowType = WebText.Clean(type, 40),
                Imdb = externals.ValueKind == JsonValueKind.Object ? Library.TitleIds.ValidImdb(Text(externals, "imdb")) : null,
                Tvdb = externals.ValueKind == JsonValueKind.Object ? Int(externals, "thetvdb") : null,
                TvMaze = Int(show, "id"),
                Link = Text(show, "url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var page) && page.Scheme == Uri.UriSchemeHttps &&
                       page.Host.EndsWith("tvmaze.com", StringComparison.OrdinalIgnoreCase) ? page.AbsoluteUri : null,
            });
        }

        return episodes;
    }

    public static IReadOnlyList<DateOnly> ParseSeasonDates(JsonDocument document, int season) =>
        document.RootElement.ValueKind != JsonValueKind.Array ? []
        : document.RootElement.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object && Int(e, "season") == season && Text(e, "type") is null or "regular")
            .Select(e => DateOnly.TryParseExact(Text(e, "airdate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null)
            .Select(d => d ?? DateOnly.MinValue)
            .ToList();

    // the day in the user's time zone (from "airstamp"), else the channel's own "airdate"
    private static DateOnly? Day(JsonElement item, TimeZoneInfo zone)
    {
        if (Text(item, "airstamp") is { Length: > 0 } stamp &&
            DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, zone).DateTime);
        }

        return DateOnly.TryParseExact(Text(item, "airdate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
    }

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress, path));
            request.Headers.UserAgent.ParseAdd(WebPageClient.UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response;
            try
            {
                response = await _pacer.RunAsync(() => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new NewsSourceException("TVmaze nie je dostupné: " + ex.Message, ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new NewsSourceException("TVmaze neodpovedá.", ex);
            }

            using (response)
            {
                if (response.RequestMessage?.RequestUri is { } final && (final.Scheme != Uri.UriSchemeHttps || final.Host != BaseAddress.Host))
                {
                    throw new NewsSourceException($"TVmaze presmerovalo na inú adresu ({final.Host}) – odpoveď sa nepoužila.");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
                {
                    var delay = RequestPacer.RetryDelay(response, attempt);
                    _pacer.PauseFor(delay);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new NewsSourceException($"TVmaze odpovedalo chybou {(int)response.StatusCode}.");
                }

                if (response.Content.Headers.ContentLength > MaxBytes)
                {
                    throw new NewsSourceException("Odpoveď TVmaze je neprimerane veľká – nepoužila sa.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxBytes)
                    {
                        throw new NewsSourceException("Odpoveď TVmaze je neprimerane veľká – nepoužila sa.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                try
                {
                    return JsonDocument.Parse(buffer.ToArray());
                }
                catch (JsonException ex)
                {
                    throw new NewsSourceException("TVmaze vrátilo nečitateľnú odpoveď.", ex);
                }
            }
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;
}

/// <summary>A show on TVmaze: its ID, name, the year it started, its original language and the country of its channel.</summary>
public sealed record TvMazeShow(int Id, string Name, int? Year, string Language, string Country)
{
    /// <summary>A Czech or Slovak series.</summary>
    public bool IsHome => HomeOrigin.IsHomeLanguage(Language) || HomeOrigin.IsHomeCountry(Country);
}

/// <summary>Which TVmaze shows are news for the user: popular enough (TVmaze's weight 0–100) and not of the skipped types.</summary>
public sealed record TvMazeFilter(int MinWeight, IReadOnlyList<string> SkipTypes)
{
    public static TvMazeFilter Default { get; } = new(70, ["News", "Talk Show", "Sports", "Game Show", "Panel Show", "Variety", "Award Show"]);
}
