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

    // the posters put aside by emptying the tray (Vrátiť brings them back; the next emptying deletes them)
    private string Aside => Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-vyprazdnene";

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

    /// <summary>All posters put aside (the tray emptied); those put aside before go for good. Whether there were any.</summary>
    public bool Clear()
    {
        if (!Directory.Exists(Folder) || !Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories).Any())
        {
            return false;
        }

        if (Directory.Exists(Aside))
        {
            Directory.Delete(Aside, recursive: true);
        }

        Directory.Move(Folder, Aside);
        return true;
    }

    /// <summary>The posters put aside back (those picked since stay too).</summary>
    public void Restore()
    {
        if (!Directory.Exists(Aside))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Aside, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Folder, Path.GetRelativePath(Aside, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
            {
                File.Move(file, target);
            }
        }

        Directory.Delete(Aside, recursive: true);
    }

    private static string TitleFolder(string root, MediaKind kind, int id) =>
        Path.Combine(root, (kind == MediaKind.Series ? "tv-" : "movie-") + id.ToString(CultureInfo.InvariantCulture));

    [GeneratedRegex(@"^(?<at>\d{17})-(?<source>csfd|tmdb)-[0-9a-f]{12}\.(jpg|png|webp)$", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();
}
