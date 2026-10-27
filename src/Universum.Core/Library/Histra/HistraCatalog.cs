using System.Globalization;
using System.Text.Json;
using Universum.Core.Library.Tmdb;
using Universum.Core.Text;

namespace Universum.Core.Library.Histra;

/// <summary>
/// Searches the HISTRA catalog for films and series by name (decided 27. 10. 2026: from HISTRA UNIVERSUM uses only
/// this search – when there is no TMDB key or TMDB finds nothing). The catalog answers in English without a language,
/// with the original title and the TMDB ID of each title, so a result names the library folder exactly as a TMDB
/// result does. Only the searched name is sent; answers are kept for the session.
/// </summary>
public sealed class HistraCatalog(HistraClient client)
{
    private readonly Dictionary<string, IReadOnlyList<TmdbTitle>> _answers = new(StringComparer.Ordinal);

    /// <summary>Raised when HISTRA asked to slow down and the client waits before trying again.</summary>
    public event Action<TimeSpan>? Waiting
    {
        add => client.Waiting += value;
        remove => client.Waiting -= value;
    }

    /// <summary>
    /// Titles matching a name, those whose title or original title is the name first; with a year only titles of that
    /// year (as TMDB searches). Titles without a TMDB ID are left out (the library needs it).
    /// </summary>
    public async Task<IReadOnlyList<TmdbTitle>> SearchAsync(MediaKind kind, string query, int? year, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var path = (kind == MediaKind.Movie ? "api/v1/catalog/movies/search?q=" : "api/v1/catalog/shows/search?q=") + Uri.EscapeDataString(query.Trim());
        IReadOnlyList<TmdbTitle>? all;
        lock (_answers)
        {
            _answers.TryGetValue(path, out all);
        }

        if (all is null)
        {
            using var document = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
            all = Parse(kind, document);
            lock (_answers)
            {
                _answers[path] = all;
            }
        }

        var wanted = Words.Normalize(query);
        return all
            .Where(t => year is null || t.Year == year)
            .OrderByDescending(t => Words.Normalize(t.Title) == wanted || Words.Normalize(t.OriginalTitle) == wanted)
            .ToList();
    }

    /// <summary>The results of a catalog search as titles (films: release_date, series: first_air_date).</summary>
    public static IReadOnlyList<TmdbTitle> Parse(MediaKind kind, JsonDocument? document)
    {
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var titles = new List<TmdbTitle>();
        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object || TmdbId(result) is not { } id)
            {
                continue;
            }

            var title = Text(result, "title");
            var original = Text(result, "original_title") ?? title;
            if (string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            var date = Text(result, kind == MediaKind.Movie ? "release_date" : "first_air_date");
            int? year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
            // "id" is the title's number in HISTRA's own catalog; 0 for a title HISTRA only passes on from TMDB
            int? histraId = result.TryGetProperty("id", out var own) && own.ValueKind == JsonValueKind.Number && own.TryGetInt32(out var number) && number > 0 ? number : null;
            titles.Add(new TmdbTitle(kind, id, string.IsNullOrWhiteSpace(title) ? original : title, original, null, year, []) { HistraId = histraId });
        }

        return titles;
    }

    // "external_ids": { "tmdb": "603" } – a string in the catalog, a number accepted too
    private static int? TmdbId(JsonElement result)
    {
        if (!result.TryGetProperty("external_ids", out var ids) || ids.ValueKind != JsonValueKind.Object || !ids.TryGetProperty("tmdb", out var tmdb))
        {
            return null;
        }

        return tmdb.ValueKind switch
        {
            JsonValueKind.Number when tmdb.TryGetInt32(out var number) && number > 0 => number,
            JsonValueKind.String when int.TryParse(tmdb.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 => number,
            _ => null,
        };
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
