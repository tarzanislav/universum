using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Universum.Core.Weather;

/// <summary>
/// A place for the weather: its name as found, where it lies (district, region, country – to tell places of the same name
/// apart) and its coordinates.
/// </summary>
public sealed record WeatherPlace(
    string Name, string? Region, string? Country, double Latitude, double Longitude, string? District = null, double? Elevation = null)
{
    /// <summary>Short, for the Dashboard: "Valaská, okres Brezno" (or the region, or the country).</summary>
    public string Display => string.Join(", ", new[] { Name, Known(District) ?? Known(Region) ?? Known(Country) }.OfType<string>());

    /// <summary>Where it lies: "okres Brezno, Banskobystrický kraj, Slovensko · 469 m n. m.".</summary>
    public string Details =>
        string.Join(", ", new[] { District, Region, Country }.Select(Known).OfType<string>().Distinct()) +
        (Elevation is { } meters ? string.Create(CultureInfo.GetCultureInfo("sk-SK"), $" · {meters:0} m n. m.") : "");

    /// <summary>For the choice and the settings: "Valaská – okres Brezno, Banskobystrický kraj, Slovensko · 469 m n. m.".</summary>
    public string FullDisplay => Details.Length == 0 ? Name : $"{Name} – {Details.TrimStart(' ', '·')}";

    private static string? Known(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>A day of the forecast.</summary>
public sealed record WeatherDay(DateOnly Date, int Code, double Min, double Max, int? PrecipitationChance);

/// <summary>The weather now and the next days.</summary>
public sealed record WeatherReport(
    double Temperature, double? FeelsLike, int Code, double? Wind, bool IsDay, IReadOnlyList<WeatherDay> Days, DateTime TakenUtc)
{
    public string Description => OpenMeteo.Describe(Code);
}

public sealed class WeatherException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Weather from Open-Meteo (https://open-meteo.com, free without a key, data under CC BY 4.0 – the page names the
/// source). Only the town typed in the settings is sent to find it, then its coordinates for the forecast; answers are
/// small and limited to 1 MiB.
/// </summary>
public sealed class OpenMeteo(HttpClient http)
{
    public const string Attribution = "Počasie: Open-Meteo.com (CC BY 4.0)";

    public static Uri Website { get; } = new("https://open-meteo.com/");

    private const int MaxResponseBytes = 1 << 20;

    /// <summary>How long one answer may take (headers and body).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The towns of this name as Open-Meteo finds them (at most <paramref name="count"/>, best first).</summary>
    public async Task<IReadOnlyList<WeatherPlace>> SearchAsync(string town, int count = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(town))
        {
            return [];
        }

        using var document = await GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"https://geocoding-api.open-meteo.com/v1/search?count={Math.Clamp(count, 1, 100)}&language=sk&format=json&name=") +
            Uri.EscapeDataString(town.Trim()),
            cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var places = new List<WeatherPlace>();
        foreach (var place in results.EnumerateArray())
        {
            if (Number(place, "latitude") is { } latitude && Number(place, "longitude") is { } longitude && Text(place, "name") is { } name)
            {
                places.Add(new WeatherPlace(name, Text(place, "admin1"), Text(place, "country"), latitude, longitude,
                    Text(place, "admin2"), Number(place, "elevation")));
            }
        }

        return places;
    }

    /// <summary>The first (best) town of this name; null when Open-Meteo finds none.</summary>
    public async Task<WeatherPlace?> FindAsync(string town, CancellationToken cancellationToken = default) =>
        (await SearchAsync(town, 1, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>The weather now and for three days at the coordinates.</summary>
    public async Task<WeatherReport> ForecastAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        var at = string.Create(CultureInfo.InvariantCulture, $"latitude={latitude:0.####}&longitude={longitude:0.####}");
        using var document = await GetAsync(
            "https://api.open-meteo.com/v1/forecast?" + at +
            "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,is_day" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=3",
            cancellationToken).ConfigureAwait(false);
        return Parse(document);
    }

    /// <summary>The forecast answer as a report.</summary>
    public static WeatherReport Parse(JsonDocument document)
    {
        var root = document.RootElement;
        if (!root.TryGetProperty("current", out var current) || Number(current, "temperature_2m") is not { } temperature)
        {
            throw new WeatherException("Open-Meteo neposlalo aktuálne počasie.");
        }

        var days = new List<WeatherDay>();
        if (root.TryGetProperty("daily", out var daily) && daily.TryGetProperty("time", out var times) && times.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var time in times.EnumerateArray())
            {
                if (time.ValueKind == JsonValueKind.String && DateOnly.TryParse(time.GetString(), CultureInfo.InvariantCulture, out var date) &&
                    At(daily, "temperature_2m_min", i) is { } min && At(daily, "temperature_2m_max", i) is { } max)
                {
                    days.Add(new WeatherDay(date, (int)(At(daily, "weather_code", i) ?? 0), min, max, At(daily, "precipitation_probability_max", i) is { } p ? (int)p : null));
                }

                i++;
            }
        }

        return new WeatherReport(temperature, Number(current, "apparent_temperature"), (int)(Number(current, "weather_code") ?? 0),
            Number(current, "wind_speed_10m"), Number(current, "is_day") is not 0, days, DateTime.UtcNow);
    }

    /// <summary>The WMO weather code in Slovak.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "jasno",
        1 => "prevažne jasno",
        2 => "polojasno",
        3 => "zamračené",
        45 or 48 => "hmla",
        51 or 53 or 55 => "mrholenie",
        56 or 57 => "mrznúce mrholenie",
        61 => "slabý dážď",
        63 => "dážď",
        65 => "silný dážď",
        66 or 67 => "mrznúci dážď",
        71 => "slabé sneženie",
        73 => "sneženie",
        75 => "silné sneženie",
        77 => "snehové zrná",
        80 or 81 => "prehánky",
        82 => "silné prehánky",
        85 or 86 => "snehové prehánky",
        95 => "búrka",
        96 or 99 => "búrka s krupobitím",
        _ => "",
    };

    /// <summary>A symbol for the weather code (the font draws it in one colour).</summary>
    public static string Symbol(int code, bool day = true) => code switch
    {
        0 => day ? "☀" : "☾",
        1 or 2 => "⛅",
        3 => "☁",
        45 or 48 => "≋",
        >= 51 and <= 67 or >= 80 and <= 82 => "☂",
        >= 71 and <= 77 or 85 or 86 => "❄",
        >= 95 => "⚡",
        _ => "·",
    };

    private async Task<JsonDocument> GetAsync(string address, CancellationToken cancellationToken)
    {
        // the whole answer (headers and body) within the time limit; a body is read at most to the size limit
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        byte[] body;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new WeatherException($"Služba počasia odpovedala chybou {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw new WeatherException("Odpoveď služby počasia je neprimerane veľká.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, limit.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw new WeatherException("Odpoveď služby počasia je neprimerane veľká.");
                }

                buffer.Write(chunk, 0, read);
            }

            body = buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new WeatherException("Počasie nie je dostupné: " + ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WeatherException("Služba počasia neodpovedá.", ex);
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new WeatherException("Služba počasia poslala nečitateľnú odpoveď.", ex);
        }
    }

    private static double? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static double? At(JsonElement element, string property, int index) =>
        element.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array && index < values.GetArrayLength() &&
        values[index].ValueKind == JsonValueKind.Number
            ? values[index].GetDouble()
            : null;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
