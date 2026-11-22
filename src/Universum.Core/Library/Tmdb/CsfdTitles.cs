using System.Text.Json;

namespace Universum.Core.Library.Tmdb;

/// <summary>
/// The titles of ČSFD not found on TMDB (owner, 22. 11. 2026: a poster drawn by AI for a film or series only on ČSFD): what
/// their pages said – the name, year, original name, genres, countries and plot – kept in one file, the newest first, at
/// most <see cref="Most"/>. Only on this computer.
/// </summary>
public sealed class CsfdTitles(string path)
{
    /// <summary>Titles kept at most.</summary>
    public const int Most = 40;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The titles kept, the newest first.</summary>
    public IReadOnlyList<CsfdPage> All()
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var stored = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(path)) ?? [];
            return [.. stored.Select(s => s.Page()).OfType<CsfdPage>()];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Keeps a title (the same page again replaces its older copy and moves to the top).</summary>
    public void Add(CsfdPage page)
    {
        if (page.Title is null)
        {
            return;
        }

        var titles = All().Where(p => p.CsfdId != page.CsfdId).Prepend(page).Take(Most).Select(Stored.Of).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(titles, Options));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Forgets a title.</summary>
    public void Remove(int csfdId)
    {
        var titles = All();
        if (titles.All(p => p.CsfdId != csfdId))
        {
            return;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(titles.Where(p => p.CsfdId != csfdId).Select(Stored.Of).ToList(), Options));
    }

    private sealed record Stored(int Id, string? Imdb, string? Name, int Year, bool Series, string? Original, string? Genres, string? Countries, string? Plot)
    {
        public static Stored Of(CsfdPage p) =>
            new(p.CsfdId, p.Imdb, p.Title?.Name, p.Title?.Year ?? 0, p.Title?.Kind == MediaKind.Series, p.Original, p.Genres, p.Countries, p.Plot);

        public CsfdPage? Page() =>
            Id <= 0 || string.IsNullOrWhiteSpace(Name) || Year <= 0
                ? null
                : new CsfdPage(Id, Imdb, new TabTitle(Name, Year, Series ? MediaKind.Series : MediaKind.Movie))
                {
                    Original = Original, Genres = Genres, Countries = Countries, Plot = Plot,
                };
    }
}
