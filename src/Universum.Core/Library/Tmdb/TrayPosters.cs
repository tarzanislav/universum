using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Universum.Core.Library.Tmdb;

/// <summary>Where a poster of the Zásobník TMDB was picked.</summary>
public enum TrayPosterSource
{
    Csfd,
    Tmdb,
}

/// <summary>A film or series page of ČSFD (csfd.cz, csfd.sk): its ČSFD ID, its IMDb ID when the page links IMDb, and its tab title.</summary>
public sealed record CsfdPage(int CsfdId, string? Imdb, TabTitle? Title)
{
    /// <summary>"Vymyslený film (2026)" or "ČSFD 12345".</summary>
    public string Display => Title?.Display ?? "ČSFD " + CsfdId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The original name as the page gives it (owner, 22. 11. 2026: for a title only on ČSFD); null when not sent.</summary>
    public string? Original { get; init; }

    /// <summary>The genres as the page gives them ("Komedie / Drama").</summary>
    public string? Genres { get; init; }

    /// <summary>The countries of origin ("Česko, Slovensko").</summary>
    public string? Countries { get; init; }

    /// <summary>The plot as the page gives it (the first one).</summary>
    public string? Plot { get; init; }

    /// <summary>The page of the title on ČSFD.</summary>
    public Uri Link => new($"https://www.csfd.cz/film/{CsfdId.ToString(CultureInfo.InvariantCulture)}/");

    /// <summary>
    /// The title as a detail for the poster page (owner, 22. 11. 2026: a title not on TMDB, its poster drawn by AI from what
    /// ČSFD says): no TMDB ID (0); the folder named by the original name when it is in Latin script, else by the name of the
    /// page. Null without a name and year.
    /// </summary>
    public TmdbDetails? AsDetails()
    {
        if (Title is not { } title)
        {
            return null;
        }

        var original = Original is { Length: > 0 } o && LibraryNames.IsLatinScript(o) && LibraryNames.Clean(o).Length > 0 ? o : title.Name;
        return new TmdbDetails
        {
            Kind = title.Kind,
            Id = 0,
            OriginalTitle = original,
            EnglishTitle = original,
            CzechTitle = title.Name,
            Year = title.Year,
            Genres = TmdbGenres.Ids(Genres),
            Overview = Plot ?? "",
            Imdb = Imdb,
        };
    }
}

/// <summary>
/// A poster the script sends (owner, 8. 11. 2026: "či by skript nevedel zbierať aj plagáty z ČSFD"): where it was picked,
/// the addresses to download it from (the largest first), and the title – a TMDB page says it exactly, a ČSFD page by its
/// link to IMDb or by its name and year.
/// </summary>
public sealed record TrayPosterRequest(TrayPosterSource Source, IReadOnlyList<Uri> Images, TmdbTrayEntry? Entry, CsfdPage? Csfd, string? TmdbPath);

/// <summary>A poster kept in the tray.</summary>
public sealed record TrayPoster(string Path, TrayPosterSource Source, DateTime Added)
{
    public string SourceName => Source == TrayPosterSource.Csfd ? "ČSFD" : "TMDB";
}

/// <summary>
/// The posters of the Zásobník TMDB (owner, 8. 11. 2026): picked in the browser on ČSFD or TMDB, kept per title in a
/// folder of UNIVERSUM's own (only on this computer), offered first on the page TMDB poster. Emptying the tray puts them
/// aside, Vrátiť brings them back; only pictures are kept (JPEG, PNG, WebP by their first bytes).
/// </summary>
public sealed partial class TrayPosters(string folder)
{
    /// <summary>The largest poster accepted.</summary>
    public const int MaxBytes = 15 << 20;

    public string Folder { get; } = folder;

    // the posters put aside by emptying a note of the tray (Vrátiť brings them back; the next emptying deletes them): the
    // films where all posters went before the tray was split (14. 11. 2026), the series beside them
    private string Aside(MediaKind kind) =>
        Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + (kind == MediaKind.Series ? "-vyprazdnene-serialy" : "-vyprazdnene");

    /// <summary>".jpg", ".png" or ".webp" by the first bytes of a picture; null for anything else.</summary>
    public static string? Extension(ReadOnlySpan<byte> picture) =>
        picture.Length >= 4 && picture[0] == 0xFF && picture[1] == 0xD8 && picture[2] == 0xFF ? ".jpg"
        : picture.Length >= 8 && picture[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? ".png"
        : picture.Length >= 12 && picture[..4].SequenceEqual("RIFF"u8) && picture.Slice(8, 4).SequenceEqual("WEBP"u8) ? ".webp"
        : null;

    /// <summary>Keeps a poster of a title; the same picture again is kept once. Its file, or null when it is no picture.</summary>
    public string? Add(MediaKind kind, int id, byte[] picture, TrayPosterSource source, DateTime? now = null)
    {
        if (picture.Length > MaxBytes || Extension(picture) is not { } extension)
        {
            return null;
        }

        var folder = TitleFolder(Folder, kind, id);
        var hash = Convert.ToHexString(SHA256.HashData(picture))[..12].ToLowerInvariant();
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*-" + hash + ".*").FirstOrDefault() is { } existing)
        {
            return existing;
        }

        Directory.CreateDirectory(folder);
        var at = (now ?? DateTime.UtcNow).ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var file = Path.Combine(folder, $"{at}-{(source == TrayPosterSource.Csfd ? "csfd" : "tmdb")}-{hash}{extension}");
        File.WriteAllBytes(file, picture);
        return file;
    }

    /// <summary>The posters of a title, the newest first.</summary>
    public IReadOnlyList<TrayPoster> Of(MediaKind kind, int id)
    {
        var folder = TitleFolder(Folder, kind, id);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var posters = new List<TrayPoster>();
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (FileName().Match(Path.GetFileName(file)) is { Success: true } match &&
                DateTime.TryParseExact(match.Groups["at"].Value, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var added))
            {
                posters.Add(new TrayPoster(file, match.Groups["source"].Value == "csfd" ? TrayPosterSource.Csfd : TrayPosterSource.Tmdb, added));
            }
        }

        return [.. posters.OrderByDescending(p => p.Added).ThenByDescending(p => p.Path, StringComparer.Ordinal)];
    }

    /// <summary>Removes one poster.</summary>
    public void Remove(TrayPoster poster)
    {
        if (Path.GetDirectoryName(Path.GetDirectoryName(poster.Path)) is { } root &&
            string.Equals(Path.GetFullPath(root), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(poster.Path);
        }
    }

    /// <summary>
    /// The posters of a kind put aside (its note of the tray emptied); those of the kind put aside before go for good.
    /// Whether there were any.
    /// </summary>
    public bool Clear(MediaKind kind)
    {
        var titles = Directory.Exists(Folder)
            ? Directory.EnumerateDirectories(Folder, Prefix(kind) + "*").Where(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any()).ToList()
            : [];
        if (titles.Count == 0)
        {
            return false;
        }

        var aside = Aside(kind);
        if (Directory.Exists(aside))
        {
            Directory.Delete(aside, recursive: true);
        }

        Directory.CreateDirectory(aside);
        foreach (var title in titles)
        {
            Directory.Move(title, Path.Combine(aside, Path.GetFileName(title)));
        }

        return true;
    }

    /// <summary>The posters of a kind put aside back (those picked since stay too).</summary>
    public void Restore(MediaKind kind)
    {
        var aside = Aside(kind);
        if (!Directory.Exists(aside))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(aside, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Folder, Path.GetRelativePath(aside, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
            {
                File.Move(file, target);
            }
        }

        Directory.Delete(aside, recursive: true);
    }

    private static string Prefix(MediaKind kind) => kind == MediaKind.Series ? "tv-" : "movie-";

    private static string TitleFolder(string root, MediaKind kind, int id) =>
        Path.Combine(root, Prefix(kind) + id.ToString(CultureInfo.InvariantCulture));

    [GeneratedRegex(@"^(?<at>\d{17})-(?<source>csfd|tmdb)-[0-9a-f]{12}\.(jpg|png|webp)$", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();
}
