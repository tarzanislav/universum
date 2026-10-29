using System.Text.Json;
using System.Text.Json.Serialization;

namespace Universum.Core.News;

/// <summary>The premieres kept between starts: every film and series premiere read so far; the past ones are the history.</summary>
public sealed record PremiereArchive
{
    public List<FilmPremiere> Films { get; init; } = [];

    public List<SeriesPremiereRow> Series { get; init; } = [];

    /// <summary>When the sources were last asked.</summary>
    public DateTime? ReadAt { get; init; }

    /// <summary>
    /// The films read now added to the kept ones (one per region and film; what was read now wins). Kept films that are
    /// still to come but were not read now are given back in <paramref name="recheck"/> – the sources list only the most
    /// popular films, so they are checked by their own details.
    /// </summary>
    public static List<FilmPremiere> MergeFilms(IEnumerable<FilmPremiere> kept, IEnumerable<FilmPremiere> read, DateOnly today, out List<FilmPremiere> recheck)
    {
        var films = new Dictionary<(NewsRegion, int), FilmPremiere>();
        foreach (var film in kept)
        {
            films[(film.Region, film.TmdbId)] = film;
        }

        var fresh = new HashSet<(NewsRegion, int)>();
        foreach (var film in read)
        {
            films[(film.Region, film.TmdbId)] = film;
            fresh.Add((film.Region, film.TmdbId));
        }

        recheck = films.Where(f => !fresh.Contains(f.Key) && f.Value.Premiere >= today).Select(f => f.Value).ToList();
        return [.. films.Values];
    }

    /// <summary>
    /// The series premieres read now added to the kept ones (one per region, series and season; what was read now
    /// wins). When all sources were read (<paramref name="complete"/>), a kept premiere still to come within the days
    /// read that the schedule no longer has is dropped (it moved or was cancelled); the past ones stay as the history.
    /// </summary>
    public static List<SeriesPremiereRow> MergeSeries(IEnumerable<SeriesPremiereRow> kept, IEnumerable<SeriesPremiereRow> read, DateOnly today, DateOnly readTo, bool complete)
    {
        var rows = new Dictionary<string, SeriesPremiereRow>(StringComparer.Ordinal);
        var fresh = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in read)
        {
            rows[Key(row)] = row;
            fresh.Add(Key(row));
        }

        foreach (var row in kept)
        {
            var key = Key(row);
            var gone = complete && row.First.AirDate >= today && row.First.AirDate <= readTo;
            if (!fresh.Contains(key) && !gone)
            {
                rows[key] = row;
            }
        }

        return [.. rows.Values];
    }

    /// <summary>
    /// The kept premieres by origin (owner, 29. 10. 2026; those kept before were sorted by where they aired): the films by
    /// <see cref="FilmPremieres.Classify(IEnumerable{FilmPremiere})"/>, the series by <paramref name="classify"/>
    /// (SeriesNews.Classify – a dubbed foreign series of a Czech or Slovak channel is left out).
    /// </summary>
    public PremiereArchive Classified(Func<SeriesEpisode, SeriesEpisode?> classify) => this with
    {
        Films = FilmPremieres.Classify(Films),
        Series = Series.Select(r => classify(r.First) is { } first ? r with { First = first } : null).OfType<SeriesPremiereRow>()
            .GroupBy(Key, StringComparer.Ordinal).Select(g => g.First()).ToList(),
    };

    /// <summary>A premiere of a series: its region, series and season.</summary>
    public static string Key(SeriesPremiereRow row) => $"{row.First.Region}|{row.First.SeriesKey}|{row.First.Season}";
}

/// <summary>The kept premieres, in a file of this Windows account.</summary>
public sealed class PremiereStore(string file)
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public PremiereArchive Load()
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<PremiereArchive>(File.ReadAllBytes(file), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new();
        }
    }

    /// <summary>Keeps the premieres; false when the file cannot be written (they stay for this run).</summary>
    public bool Save(PremiereArchive archive)
    {
        try
        {
            Files.WriteAtomically(file, JsonSerializer.SerializeToUtf8Bytes(archive, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Forgets the kept premieres (the history too).</summary>
    public void Clear()
    {
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }
}

/// <summary>What the user marked on a title: chosen ("I look forward to it") and a note.</summary>
public sealed record TitleMark(bool Chosen, string Note)
{
    public static TitleMark None { get; } = new(false, "");
}

/// <summary>
/// The marks of titles in Premiéry and Novinky (chosen, a note) in a file of this Windows account – a film by its TMDB
/// ID, a series by its original title and year, so a series chosen among the premieres is chosen in Novinky too.
/// </summary>
public sealed class MarkStore(string file)
{
    private Dictionary<string, TitleMark>? _marks;

    public static string Film(int tmdbId) => "film:" + tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Series(SeriesEpisode episode) => "serial:" + episode.SeriesKey;

    /// <summary>Raised with the key of a changed mark.</summary>
    public event Action<string>? Changed;

    public TitleMark Get(string key) => Marks.TryGetValue(key, out var mark) ? mark : TitleMark.None;

    /// <summary>The keys of the chosen titles (Vybrané).</summary>
    public IReadOnlyList<string> Chosen => Marks.Where(m => m.Value.Chosen).Select(m => m.Key).ToList();

    public void Set(string key, TitleMark mark)
    {
        mark = mark with { Note = mark.Note.Trim() };
        if (Get(key) == mark)
        {
            return;
        }

        if (mark.Chosen || mark.Note.Length > 0)
        {
            Marks[key] = mark;
        }
        else
        {
            Marks.Remove(key);
        }

        try
        {
            Files.WriteAtomically(file, JsonSerializer.SerializeToUtf8Bytes(Marks));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the mark stays for this run
        }

        Changed?.Invoke(key);
    }

    private Dictionary<string, TitleMark> Marks => _marks ??= Read();

    private Dictionary<string, TitleMark> Read()
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, TitleMark>>(File.ReadAllBytes(file)) ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}

internal static class Files
{
    /// <summary>Writes a whole file or nothing (a half-written file never replaces the old one).</summary>
    public static void WriteAtomically(string file, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllBytes(file + ".part", content);
        File.Move(file + ".part", file, overwrite: true);
    }
}
