using System.Text.Json;

namespace Universum.Core.News;

/// <summary>
/// Whether a title is Czech or Slovak (owner, 29. 10. 2026): the CZ/SK tables hold only Czech and Slovak films and series
/// (co-productions too), the world tables all the others – by origin, not by where they air.
/// </summary>
public static class HomeOrigin
{
    /// <summary>A country of origin or of the original channel (ISO code).</summary>
    public static bool IsHomeCountry(string? code) =>
        string.Equals(code, "CZ", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "SK", StringComparison.OrdinalIgnoreCase);

    /// <summary>The original language: TVmaze names it in English ("Czech"), TMDB by its ISO code ("cs").</summary>
    public static bool IsHomeLanguage(string? language) => language is not null &&
        (language.Equals("Czech", StringComparison.OrdinalIgnoreCase) || language.Equals("Slovak", StringComparison.OrdinalIgnoreCase) ||
         language.Equals("cs", StringComparison.OrdinalIgnoreCase) || language.Equals("sk", StringComparison.OrdinalIgnoreCase));
}

/// <summary>What a lookup told about a series: Czech or Slovak, or not (null: not found), and when it was asked.</summary>
public sealed record SeriesOrigin(bool? Home, DateTime Asked);

/// <summary>
/// The origins of series looked up on TVmaze (the pages of Czech and Slovak channels do not tell it), kept in a file of
/// this Windows account – or only for this run without a file. A series found is asked once; one not found is asked again
/// after <see cref="NotFoundLifetime"/>.
/// </summary>
public sealed class SeriesOrigins(string? file)
{
    public static readonly TimeSpan NotFoundLifetime = TimeSpan.FromDays(30);

    private readonly Lock _lock = new();
    private Dictionary<string, SeriesOrigin>? _origins;

    public SeriesOrigin? Get(string seriesKey)
    {
        lock (_lock)
        {
            return Origins.TryGetValue(seriesKey, out var origin) ? origin : null;
        }
    }

    /// <summary>Whether a series is to be asked (not yet, or not found long enough ago).</summary>
    public bool IsDue(string seriesKey, DateTime now) =>
        Get(seriesKey) is not { } known || known.Home is null && now - known.Asked > NotFoundLifetime;

    public void Set(string seriesKey, bool? home, DateTime now)
    {
        lock (_lock)
        {
            Origins[seriesKey] = new SeriesOrigin(home, now);
        }
    }

    /// <summary>Keeps what was learnt (nothing without a file; a file that cannot be written only loses the lookups).</summary>
    public void Save()
    {
        if (file is null)
        {
            return;
        }

        lock (_lock)
        {
            try
            {
                Files.WriteAtomically(file, JsonSerializer.SerializeToUtf8Bytes(Origins));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // asked again in a later run
            }
        }
    }

    private Dictionary<string, SeriesOrigin> Origins => _origins ??= Read();

    private Dictionary<string, SeriesOrigin> Read()
    {
        try
        {
            return file is not null && File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, SeriesOrigin>>(File.ReadAllBytes(file)) ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
