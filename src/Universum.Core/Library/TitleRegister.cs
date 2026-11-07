using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Universum.Core.Batch;
using Universum.Core.Text;

namespace Universum.Core.Library;

/// <summary>A title UNIVERSUM wrote into a library: its name as the library has it, the episodes, when.</summary>
public sealed record RegisteredTitle
{
    public MediaKind Kind { get; init; }

    public string Name { get; init; } = "";

    public int? Year { get; init; }

    public int? TmdbId { get; init; }

    /// <summary>Episodes written ("S01E04", "S01E01-E02"), sorted; empty for a film.</summary>
    public List<string> Episodes { get; init; } = [];

    /// <summary>Files written (each version and each episode once).</summary>
    public int Files { get; init; }

    public DateTimeOffset First { get; init; }

    public DateTimeOffset Last { get; init; }

    /// <summary>"Name (Year) [tmdbid-ID]" – as the folder of the library.</summary>
    [JsonIgnore]
    public string Display => Name + (Year is { } year ? $" ({year})" : "") + (TmdbId is { } id ? $" [tmdbid-{id}]" : "");

    /// <summary>The seasons and episodes in short: "S01: 10, S02: 3".</summary>
    [JsonIgnore]
    public string Seasons => string.Join(", ", Episodes.GroupBy(e => e.Length >= 3 ? e[..3] : e).Select(g => $"{g.Key}: {g.Count()}"));

    // one title: by its TMDB ID, else by its name and year
    internal string Key => $"{Kind}|" + (TmdbId is { } id ? "id:" + id.ToString(CultureInfo.InvariantCulture) : Name.ToUpperInvariant() + "|" + Year);
}

/// <summary>
/// Spracované tituly (owner, 7. 11. 2026): every film and series UNIVERSUM wrote into a library, filled in the background
/// with each result and from the output folders on request; exported (JSON, CSV for Excel) and imported (JSON, merged).
/// It is the user's own data – kept in the settings folder, never in the program's sources.
/// </summary>
public sealed class TitleRegister
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _file;
    private readonly Lock _gate = new();
    private Dictionary<string, RegisteredTitle>? _titles;
    private HashSet<string>? _files;

    public TitleRegister(string file) => _file = file;

    /// <summary>Raised after a change (on the thread that made it).</summary>
    public event Action? Changed;

    /// <summary>The titles, series and films by name.</summary>
    public IReadOnlyList<RegisteredTitle> Titles
    {
        get
        {
            lock (_gate)
            {
                return [.. Loaded().Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Year)];
            }
        }
    }

    /// <summary>
    /// The title, kind and episode of a file in a library: MOVIES\Name (Year)\… or TVSHOWS\Name (Year)\Season 01\…
    /// S01E04 …; null for a file elsewhere.
    /// </summary>
    public static (TitleInfo Title, EpisodeNumber? Episode)? Read(string path)
    {
        var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = parts.Length - 3; i >= 0; i--)
        {
            var kind = parts[i].Equals(LibraryNames.MoviesFolder, StringComparison.OrdinalIgnoreCase) ? MediaKind.Movie
                : parts[i].Equals(LibraryNames.ShowsFolder, StringComparison.OrdinalIgnoreCase) ? MediaKind.Series
                : (MediaKind?)null;
            if (kind is null || TitleGuess.Typed(kind.Value, parts[i + 1]) is not { } title)
            {
                continue;
            }

            if (kind == MediaKind.Movie)
            {
                return i + 3 == parts.Length ? (title, null) : null;
            }

            return EpisodeNumber.TryParse(Path.GetFileNameWithoutExtension(parts[^1])) is { } episode ? (title, episode) : null;
        }

        return null;
    }

    /// <summary>
    /// Records a file written into a library (a file elsewhere is passed by); <paramref name="known"/> – the title as it was
    /// placed – gives its TMDB ID also when the folder's name carries none (owner, 7. 11. 2026: the ID tells a title
    /// apart for sure). True when the register changed.
    /// </summary>
    public bool Record(string path, TitleInfo? known = null, DateTimeOffset? when = null)
    {
        if (Read(path) is not var (title, episode))
        {
            return false;
        }

        title = WithId(title, known);

        bool changed;
        lock (_gate)
        {
            changed = Add(title, episode, Path.GetFullPath(path), when ?? DateTimeOffset.Now);
            if (changed)
            {
                Save();
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    /// <summary>
    /// Reads the films and series already in the output folders (MOVIES, TVSHOWS); <paramref name="identify"/> gives a
    /// title's TMDB ID when its folder carries none (remembered titles). Returns how many files were new.
    /// </summary>
    public int Scan(IEnumerable<string> resultFolders, Func<TitleInfo, TitleInfo?>? identify = null, CancellationToken cancellationToken = default)
    {
        var found = new List<(string Path, DateTimeOffset Time)>();
        foreach (var folder in resultFolders.Where(Directory.Exists))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (BatchAnalyzer.VideoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    found.Add((file, new DateTimeOffset(File.GetLastWriteTime(file))));
                }
            }
        }

        var added = 0;
        lock (_gate)
        {
            foreach (var (path, time) in found)
            {
                if (Read(path) is var (title, episode) && Add(WithId(title, title.TmdbId is null ? identify?.Invoke(title) : null), episode, Path.GetFullPath(path), time))
                {
                    added++;
                }
            }

            if (added > 0)
            {
                Save();
            }
        }

        if (added > 0)
        {
            Changed?.Invoke();
        }

        return added;
    }

    /// <summary>Writes the register: JSON (whole, to be imported) or CSV (for Excel) by the file's extension.</summary>
    public void Export(string path)
    {
        var titles = Titles;
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = new StringBuilder();
            csv.AppendLine(CoreText.T("Druh;Názov;Rok;TMDB ID;Priečinok;Epizódy (počet);Série;Epizódy;Súbory;Prvé spracovanie;Posledné spracovanie"));
            foreach (var t in titles)
            {
                csv.AppendLine(string.Join(";",
                    t.Kind == MediaKind.Series ? CoreText.T("seriál") : CoreText.T("film"), Cell(t.Name), t.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
                    t.TmdbId?.ToString(CultureInfo.InvariantCulture) ?? "", Cell(t.Display), t.Episodes.Count.ToString(CultureInfo.InvariantCulture), Cell(t.Seasons),
                    Cell(string.Join(" ", t.Episodes)), t.Files.ToString(CultureInfo.InvariantCulture),
                    t.First.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), t.Last.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            }

            Write(path, csv.ToString(), withMark: true);
            return;
        }

        Write(path, JsonSerializer.Serialize(new Exported(1, titles.ToList()), Json), withMark: false);
    }

    /// <summary>Merges an exported register (JSON) into this one: episodes joined, the earliest and latest times kept; returns the titles new here.</summary>
    public int Import(string path)
    {
        var imported = JsonSerializer.Deserialize<Exported>(File.ReadAllText(path), Json)?.Titles ?? [];
        var added = 0;
        lock (_gate)
        {
            var titles = Loaded();
            foreach (var title in imported.Where(t => t.Name.Length > 0))
            {
                if (titles.TryGetValue(title.Key, out var known))
                {
                    titles[title.Key] = known with
                    {
                        Episodes = [.. known.Episodes.Union(title.Episodes, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
                        Files = Math.Max(known.Files, title.Files),
                        First = known.First < title.First ? known.First : title.First,
                        Last = known.Last > title.Last ? known.Last : title.Last,
                    };
                }
                else
                {
                    titles[title.Key] = title with { Episodes = [.. title.Episodes.Order(StringComparer.OrdinalIgnoreCase)] };
                    added++;
                }
            }

            Save();
        }

        Changed?.Invoke();
        return added;
    }

    // the title read from the folder with the TMDB ID of the same title as placed (its kind, name and year agree)
    private static TitleInfo WithId(TitleInfo read, TitleInfo? known) =>
        read.TmdbId is null && known is { TmdbId: { } id } && known.Kind == read.Kind && known.Year == read.Year &&
        string.Equals(known.FolderName, read.FolderName, StringComparison.OrdinalIgnoreCase)
            ? read with { TmdbId = id }
            : read;

    // a file once: the same file written again (a revision) changes only the time
    private bool Add(TitleInfo title, EpisodeNumber? episode, string path, DateTimeOffset when)
    {
        var titles = Loaded();
        var entry = new RegisteredTitle { Kind = title.Kind, Name = title.Name, Year = title.Year, TmdbId = title.TmdbId, First = when, Last = when };
        if (entry.TmdbId is not null && titles.Remove((entry with { TmdbId = null }).Key, out var withoutId))
        {
            // the title known so far without its ID now has it: one entry, under the ID
            titles[entry.Key] = withoutId with { TmdbId = entry.TmdbId };
        }

        var known = titles.GetValueOrDefault(entry.Key);
        var newFile = _files!.Add(path.ToUpperInvariant());
        var episodes = known?.Episodes ?? [];
        var newEpisode = episode is not null && !episodes.Contains(episode.Tag, StringComparer.OrdinalIgnoreCase);
        if (known is not null && !newFile && !newEpisode && when <= known.Last)
        {
            return false;
        }

        titles[entry.Key] = (known ?? entry) with
        {
            Episodes = newEpisode ? [.. episodes.Append(episode!.Tag).Order(StringComparer.OrdinalIgnoreCase)] : episodes,
            Files = (known?.Files ?? 0) + (newFile ? 1 : 0),
            First = known is null || when < known.First ? when : known.First,
            Last = known is null || when > known.Last ? when : known.Last,
        };
        return true;
    }

    private Dictionary<string, RegisteredTitle> Loaded()
    {
        if (_titles is not null)
        {
            return _titles;
        }

        _titles = [];
        _files = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file), Json) is { } stored)
            {
                foreach (var title in stored.Titles.Where(t => t.Name.Length > 0))
                {
                    _titles[title.Key] = title;
                }

                _files.UnionWith(stored.Files);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // a damaged register starts anew (the films are untouched); it is rebuilt from the output folders on request
        }

        return _titles;
    }

    private void Save()
    {
        try
        {
            Write(_file, JsonSerializer.Serialize(new Stored(_titles!.Values.ToList(), [.. _files!]), Json), withMark: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory; written with the next change
        }
    }

    private static void Write(string path, string text, bool withMark)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(withMark));
        File.Move(temporary, path, overwrite: true);
    }

    private static string Cell(string text) => text.Contains(';') || text.Contains('"') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    private sealed record Stored(List<RegisteredTitle> Titles, List<string> Files);

    private sealed record Exported(int Version, List<RegisteredTitle> Titles);
}
