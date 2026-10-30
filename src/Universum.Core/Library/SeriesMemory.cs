using System.Text.Json;
using Universum.Core.Text;

namespace Universum.Core.Library;

/// <summary>A series the user processes regularly: the title chosen for it and the names its files and folders carry.</summary>
/// <param name="Title">The series as it goes into the library (from TMDB and HISTRA, or as the user typed it).</param>
/// <param name="Names">The names it was recognised by (normalised: small letters without diacritics).</param>
public sealed record RememberedSeries(TitleInfo Title, IReadOnlyList<string> Names, DateTime LastUsedUtc, int Uses);

/// <summary>
/// Napovedkyňa (owner, 30. 10. 2026): the title of a series chosen on TMDB and HISTRA is remembered when its episodes are
/// processed, under the names its files and folders carry, so the next episodes get it at once – no search, no new
/// confirmation. A name belongs to one series only (the last one processed under it); when two remembered series answer
/// one name, nothing is suggested. One JSON file of the Windows account, never sent anywhere.
/// </summary>
public sealed class SeriesMemory(string file)
{
    /// <summary>At most this many series are kept (the longest unused ones go first).</summary>
    public const int MaximumSeries = 1000;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private List<RememberedSeries>? _series;

    public string File { get; } = file;

    /// <summary>The remembered series, the most recently processed first.</summary>
    public IReadOnlyList<RememberedSeries> Series => Loaded().OrderByDescending(s => s.LastUsedUtc).ToList();

    /// <summary>Raised after the memory changed (a series remembered or forgotten).</summary>
    public event Action? Changed;

    /// <summary>
    /// The remembered series for the names read from the files (null names are skipped): only when every name that is
    /// known points to the same series and its year fits (a name without a year fits any; a year one off too).
    /// </summary>
    public TitleInfo? Find(IEnumerable<NameGuess?> names)
    {
        TitleInfo? found = null;
        foreach (var name in names.OfType<NameGuess>())
        {
            var key = Key(name.Name);
            if (key.Length == 0)
            {
                continue;
            }

            var matches = Loaded().Where(s => s.Names.Contains(key, StringComparer.Ordinal) && Fits(s.Title, name)).ToList();
            if (matches.Count > 1 || (matches.Count == 1 && found is not null && !Same(found, matches[0].Title)))
            {
                return null;
            }

            found ??= matches.FirstOrDefault()?.Title;
        }

        return found;
    }

    /// <summary>
    /// Remembers <paramref name="series"/> under <paramref name="names"/> (as read from the files) and its own name; the
    /// names stop pointing to any other series. Titles that cannot go into the library are not remembered.
    /// </summary>
    public void Remember(TitleInfo series, IEnumerable<string?> names, DateTime nowUtc)
    {
        if (series.Kind != MediaKind.Series || series.Problem is not null)
        {
            return;
        }

        var keys = names.Append(series.CleanName).Select(Key).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        var list = Loaded();
        var known = list.FirstOrDefault(s => Same(s.Title, series));
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(list[i], known))
            {
                continue;
            }

            var left = list[i].Names.Where(n => !keys.Contains(n)).ToList();
            if (left.Count != list[i].Names.Count)
            {
                list[i] = list[i] with { Names = left };
            }
        }

        var entry = known is null
            ? new RememberedSeries(series, keys.Order(StringComparer.Ordinal).ToList(), nowUtc, 1)
            : known with { Title = series, Names = known.Names.Union(keys).Order(StringComparer.Ordinal).ToList(), LastUsedUtc = nowUtc, Uses = known.Uses + 1 };
        if (known is not null)
        {
            list.Remove(known);
        }

        list.Add(entry);
        list.RemoveAll(s => s.Names.Count == 0);
        if (list.Count > MaximumSeries)
        {
            list.Sort((a, b) => b.LastUsedUtc.CompareTo(a.LastUsedUtc));
            list.RemoveRange(MaximumSeries, list.Count - MaximumSeries);
        }

        Save();
    }

    /// <summary>Forgets a series (the user's choice in Nastavenia).</summary>
    public void Forget(TitleInfo series)
    {
        if (Loaded().RemoveAll(s => Same(s.Title, series)) > 0)
        {
            Save();
        }
    }

    /// <summary>The form a name is remembered in: small letters without diacritics, words split by one space.</summary>
    public static string Key(string? name) => Words.Normalize(name);

    // one series: the same TMDB ID, or without one the same folder in the library
    private static bool Same(TitleInfo a, TitleInfo b) =>
        a.TmdbId is { } id ? id == b.TmdbId : b.TmdbId is null && string.Equals(a.FolderName, b.FolderName, StringComparison.OrdinalIgnoreCase);

    private static bool Fits(TitleInfo title, NameGuess name) =>
        name.TmdbId is { } id ? id == title.TmdbId : name.Year is not { } year || title.Year is not { } known || Math.Abs(year - known) <= 1;

    private List<RememberedSeries> Loaded()
    {
        if (_series is not null)
        {
            return _series;
        }

        try
        {
            _series = System.IO.File.Exists(File)
                ? (JsonSerializer.Deserialize<List<Stored>>(System.IO.File.ReadAllBytes(File), Options) ?? [])
                .Where(s => s.Name is { Length: > 0 } && s.Names is { Count: > 0 })
                .Select(s => new RememberedSeries(new TitleInfo(MediaKind.Series, s.Name!, s.Year, s.TmdbId, s.IncludeId), s.Names!, s.LastUsedUtc, s.Uses))
                .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _series = [];
        }

        return _series;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
            var temporary = File + ".part";
            var stored = Loaded().Select(s => new Stored
            {
                Name = s.Title.Name,
                Year = s.Title.Year,
                TmdbId = s.Title.TmdbId,
                IncludeId = s.Title.IncludeId,
                Names = [.. s.Names],
                LastUsedUtc = s.LastUsedUtc,
                Uses = s.Uses,
            }).ToList();
            System.IO.File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(stored, Options));
            System.IO.File.Move(temporary, File, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not saved: the next start knows what was saved before; this session keeps it
        }

        Changed?.Invoke();
    }

    private sealed class Stored
    {
        public string? Name { get; set; }

        public int? Year { get; set; }

        public int? TmdbId { get; set; }

        public bool IncludeId { get; set; }

        public List<string>? Names { get; set; }

        public DateTime LastUsedUtc { get; set; }

        public int Uses { get; set; }
    }
}
