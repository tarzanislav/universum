using System.Text.Json;
using System.Text.RegularExpressions;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Text;

namespace Universum.Core.Posters;

/// <summary>
/// Where a poster goes (owner, 31. 10. 2026): "folder.jpg" always in the main folder of the title (never in a season
/// folder) – in POSTER, named exactly as UNIVERSUM names the folder in MOVIES or TVSHOWS, so finding a poster alone never
/// mixes with the films processed; with "Zlúčiť poster s filmom/seriálom" in the folder of the title in MOVIES or TVSHOWS, or –
/// when the title is only in the input folder – once UNIVERSUM has processed it. An existing folder.jpg is kept under
/// another name (owner, 31. 10. 2026: "folder-povodny.jpg") and said; nothing is deleted.
/// </summary>
public sealed partial class PosterPlaces(LibraryLayout library, string? inputDirectory)
{
    /// <summary>The name of the poster in the folder of a title (Kodi, Jellyfin, Plex and Windows read it).</summary>
    public const string FileName = "folder.jpg";

    /// <summary>Files and folders of the input folder looked through at most when looking for a title.</summary>
    public const int MaxInputEntries = 50_000;

    private static readonly HashSet<string> Videos = new([".mkv", ".mp4", ".m4v", ".avi", ".ts", ".m2ts", ".mov", ".webm", ".wmv", ".mpg", ".iso"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The file is a video (by its extension) – as the input folder is searched.</summary>
    public static bool IsVideo(string file) => Videos.Contains(Path.GetExtension(file));

    public LibraryLayout Library { get; } = library;

    /// <summary>POSTER\Názov (rok) – the folder of a poster found alone.</summary>
    public string PosterFolder(TitleInfo title) => Path.Combine(Library.Posters, title.FolderName);

    /// <summary>
    /// The main folder of the title in MOVIES or TVSHOWS, when it is there: named as UNIVERSUM names it, or with the
    /// title's TMDB ID, or beginning with its name and year.
    /// </summary>
    public string? LibraryFolder(TitleInfo title, int tmdbId)
    {
        var exact = Library.TitleDirectory(title);
        if (Directory.Exists(exact))
        {
            return exact;
        }

        var root = title.Kind == MediaKind.Movie ? Library.Movies : Library.Shows;
        if (!Directory.Exists(root))
        {
            return null;
        }

        var tag = $"[tmdbid-{tmdbId}]";
        var wanted = Words.Normalize(title.Year is { } year ? $"{title.CleanName} ({year})" : title.CleanName);
        return Directory.EnumerateDirectories(root).FirstOrDefault(d =>
        {
            var name = Path.GetFileName(d);
            return name.Contains(tag, StringComparison.OrdinalIgnoreCase) || Words.Normalize(name) == wanted;
        });
    }

    /// <summary>
    /// A file or folder of the input folder named as the title (any of its names, with its year when the name has one;
    /// an episode of a series by its name and number); null when there is none or no input folder.
    /// </summary>
    public string? InInput(MediaKind kind, IReadOnlyList<string> names, int? year)
    {
        if (inputDirectory is null || !Directory.Exists(inputDirectory))
        {
            return null;
        }

        var wanted = names.Select(Words.Normalize).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var entry in Directory.EnumerateFileSystemEntries(inputDirectory, "*", options).Take(MaxInputEntries))
        {
            var isFile = File.Exists(entry);
            if (isFile && !Videos.Contains(Path.GetExtension(entry)))
            {
                continue;
            }

            var name = Words.Normalize(isFile ? Path.GetFileNameWithoutExtension(entry) : Path.GetFileName(entry));
            if (wanted.Any(w => Names(name, w, kind, year)))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The result of writing a poster: done (and an older poster kept under which name), or the problem.</summary>
    public sealed record Saved(bool Done, string? KeptAs, string? Problem);

    /// <summary>
    /// Writes the poster as folder.jpg (or another picture of the title as <paramref name="name"/>) into a folder (created
    /// when missing). One already there is renamed first ("folder-povodny.jpg", "folder-povodny-2.jpg" …) – never deleted
    /// or overwritten.
    /// </summary>
    public static Saved Save(string folder, byte[] jpeg, string name = FileName)
    {
        var target = Path.Combine(folder, name);
        var temporary = Path.Combine(folder, name + ".universum-part");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(temporary, jpeg);
            var kept = KeepOld(folder, name);
            File.Move(temporary, target, overwrite: false);
            return new Saved(true, kept, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // a leftover part of UNIVERSUM's own write
            }

            return new Saved(false, null, $"{name} sa nedá zapísať do {folder}: {ex.Message}");
        }
    }

    /// <summary>The picture of a folder (folder.jpg) renamed to the first free "folder-povodny(-N).jpg"; its new name, or null when there was none.</summary>
    public static string? KeepOld(string folder, string fileName = FileName)
    {
        var current = Path.Combine(folder, fileName);
        if (!File.Exists(current))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var number = 1; ; number++)
        {
            var name = number == 1 ? $"{stem}-povodny{extension}" : $"{stem}-povodny-{number}{extension}";
            var kept = Path.Combine(folder, name);
            if (!File.Exists(kept))
            {
                File.Move(current, kept, overwrite: false);
                return name;
            }
        }
    }

    // "vymysleny film 2026 1080p" names "vymysleny film" of 2026; "vymysleny serial s01e02" the series
    private static bool Names(string name, string wanted, MediaKind kind, int? year)
    {
        if (name == wanted)
        {
            return true;
        }

        if (!name.StartsWith(wanted + " ", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = name[(wanted.Length + 1)..];
        if (Year().Match(rest) is { Success: true } found)
        {
            return year is null || Math.Abs(int.Parse(found.Value, System.Globalization.CultureInfo.InvariantCulture) - year.Value) <= 1;
        }

        return kind == MediaKind.Series ? Episode().IsMatch(rest) : Release().IsMatch(rest);
    }

    [GeneratedRegex(@"^(18|19|20)[0-9]{2}(?= |$)", RegexOptions.CultureInvariant)]
    private static partial Regex Year();

    [GeneratedRegex(@"^(s[0-9]{1,2}( ?e[0-9]{1,3})?|season [0-9]+|serie [0-9]+|[0-9]{1,2}x[0-9]{2})( |$)", RegexOptions.CultureInvariant)]
    private static partial Regex Episode();

    [GeneratedRegex(@"^(2160p|1080p|720p|576p|480p|4k|uhd|bluray|blu ray|bdrip|brrip|web|webrip|web dl|dvdrip|hdtv|remux|x264|x265|h264|h265|hevc|cz|sk|dabing|titulky)( |$)", RegexOptions.CultureInvariant)]
    private static partial Regex Release();
}

/// <summary>A poster waiting for its title to be processed (the title is only in the input folder).</summary>
/// <param name="File">The poster (folder.jpg) kept meanwhile; null or empty when only the other pictures wait.</param>
/// <param name="Extras">Other pictures of the main folder waiting with it (fanart.jpg, clearlogo.png, season posters): name → file.</param>
public sealed record WaitingPoster(MediaKind Kind, int TmdbId, string FolderName, string? File, DateTimeOffset Since)
{
    public Dictionary<string, string> Extras { get; init; } = [];
}

/// <summary>
/// Posters waiting for their titles (Zlúčiť poster s filmom/seriálom, the title only in the input folder): kept in UNIVERSUM's
/// own folder; when UNIVERSUM writes a result into the title's folder in MOVIES or TVSHOWS, the poster goes into the main
/// folder of the title as folder.jpg (an existing one renamed to folder-povodny.jpg) and stops waiting.
/// </summary>
/// <param name="libraryRoot">The output folder now (null: none) – a poster goes only into its MOVIES or TVSHOWS, never next to originals.</param>
public sealed partial class PendingPosters(string folder, Func<string?> libraryRoot)
{
    private static readonly object Gate = new();

    private string IndexFile => Path.Combine(folder, "cakajuce.json");

    /// <summary>The posters waiting.</summary>
    public IReadOnlyList<WaitingPoster> All
    {
        get
        {
            lock (Gate)
            {
                return Load();
            }
        }
    }

    /// <summary>
    /// A poster and/or other pictures of the main folder wait for the title (owner, 31. 10. 2026: the poster and the other
    /// pictures are processed by their own buttons): one entry per title – a newer poster replaces the older one, the
    /// other pictures are added to those already waiting (a newer one of the same name replaces the older).
    /// </summary>
    public void Add(TitleInfo title, int tmdbId, byte[]? jpeg, IReadOnlyDictionary<string, byte[]>? extras = null)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(folder);
            var prefix = $"{(title.Kind == MediaKind.Movie ? "film" : "serial")}-{tmdbId}";
            var all = Load();
            var old = all.FirstOrDefault(w => w.Kind == title.Kind && w.TmdbId == tmdbId);
            var file = old?.File is { Length: > 0 } kept && System.IO.File.Exists(kept) ? kept : null;
            if (jpeg is not null)
            {
                file = Path.Combine(folder, prefix + ".jpg");
                System.IO.File.WriteAllBytes(file, jpeg);
            }

            var pictures = new Dictionary<string, string>(old?.Extras.Where(e => System.IO.File.Exists(e.Value)) ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var (name, bytes) in extras ?? new Dictionary<string, byte[]>())
            {
                var extra = Path.Combine(folder, $"{prefix}-{Path.GetFileName(name)}");
                System.IO.File.WriteAllBytes(extra, bytes);
                pictures[Path.GetFileName(name)] = extra;
            }

            if (file is null && pictures.Count == 0)
            {
                return;
            }

            all.RemoveAll(w => w.Kind == title.Kind && w.TmdbId == tmdbId);
            all.Add(new WaitingPoster(title.Kind, tmdbId, title.FolderName, file, DateTimeOffset.Now) { Extras = pictures });
            Save(all);
        }
    }

    /// <summary>
    /// After a result was written into <paramref name="resultDirectory"/> (a title folder, or its season folder): the poster
    /// waiting for that title as folder.jpg in its main folder. What was done in words; null when no poster waits for it.
    /// Never throws – the result is written either way.
    /// </summary>
    public string? Deliver(string resultDirectory)
    {
        try
        {
            return DeliverOrThrow(resultDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return "čakajúci plagát sa nedal pridať: " + ex.Message;
        }
    }

    private string? DeliverOrThrow(string resultDirectory)
    {
        var main = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resultDirectory));
        if (SeasonFolder().IsMatch(Path.GetFileName(main)))
        {
            main = Path.GetDirectoryName(main) ?? main;
        }

        var kind = Path.GetFileName(Path.GetDirectoryName(main)) switch
        {
            LibraryNames.MoviesFolder => MediaKind.Movie,
            LibraryNames.ShowsFolder => MediaKind.Series,
            _ => (MediaKind?)null,
        };
        // only into the library of the output folder (MOVIES, TVSHOWS), never into a folder of originals named so
        var root = libraryRoot();
        if (kind is null || root is null || !Paths.Same(Path.GetDirectoryName(Path.GetDirectoryName(main)) ?? "", Path.GetFullPath(root)))
        {
            return null;
        }

        lock (Gate)
        {
            var all = Load();
            var name = Path.GetFileName(main);
            var waiting = all.FirstOrDefault(w => w.Kind == kind &&
                (string.Equals(w.FolderName, name, StringComparison.OrdinalIgnoreCase) || name.Contains($"[tmdbid-{w.TmdbId}]", StringComparison.OrdinalIgnoreCase)));
            if (waiting is null)
            {
                return null;
            }

            // the list first, then the files: a poster is never given twice, and a list that cannot be written changes nothing
            Save(all.Where(w => w != waiting).ToList());
            string? kept = null;
            var poster = waiting.File is { Length: > 0 } waitingFile && File.Exists(waitingFile);
            if (poster)
            {
                kept = PosterPlaces.KeepOld(main);
                File.Copy(waiting.File!, Path.Combine(main, PosterPlaces.FileName), overwrite: false);
                File.Delete(waiting.File!);
            }

            var others = new List<string>();
            foreach (var (extraName, file) in waiting.Extras)
            {
                if (File.Exists(file))
                {
                    var old = PosterPlaces.KeepOld(main, extraName);
                    File.Copy(file, Path.Combine(main, extraName), overwrite: false);
                    File.Delete(file);
                    others.Add(old is null ? extraName : $"{extraName} (pôvodný premenovaný na {old})");
                }
            }

            var pictures = others.Count > 0 ? "ďalšie obrázky: " + string.Join(", ", others) : "";
            return !poster ? $"{pictures} pridané do {main}"
                : $"plagát {PosterPlaces.FileName} pridaný do {main}" + (kept is null ? "" : $" (pôvodný {PosterPlaces.FileName} tam už bol – premenovaný na {kept})") +
                  (pictures.Length > 0 ? "; " + pictures : "");
        }
    }

    private List<WaitingPoster> Load()
    {
        try
        {
            return File.Exists(IndexFile) ? JsonSerializer.Deserialize<List<WaitingPoster>>(File.ReadAllText(IndexFile)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(List<WaitingPoster> all)
    {
        Directory.CreateDirectory(folder);
        var temporary = IndexFile + ".part";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all));
        File.Move(temporary, IndexFile, overwrite: true);
    }

    [GeneratedRegex(@"^(Season [0-9]+|Specials)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();
}
