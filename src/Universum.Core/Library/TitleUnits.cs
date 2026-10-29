using System.Globalization;
using System.Text.RegularExpressions;
using Universum.Core.Batch;
using Universum.Core.IO;
using Universum.Core.Text;

namespace Universum.Core.Library;

/// <summary>
/// One title of an analysed folder sorted by the user into MOVIES and TVSHOWS (owner, 29. 10. 2026): the episodes of
/// one series or one film (with its versions). <paramref name="Folder"/> is the title's folder (the first folder below
/// TVSHOWS or MOVIES), or the file itself when it lies directly there; <paramref name="Name"/> the title read from that
/// folder (or the file names); <paramref name="Numbers"/> episode numbers of a numbered sequence ("Name 01", "CF01",
/// "Name 1") for files whose names carry no clear episode number; <paramref name="Sorted"/> false for a file lying
/// outside MOVIES and TVSHOWS (its kind is only guessed – the user has to sort it).
/// </summary>
public sealed record TitleUnit(
    string Folder, MediaKind Kind, IReadOnlyList<string> Files, NameGuess? Name, IReadOnlyDictionary<string, int> Numbers, bool Sorted = true);

/// <summary>
/// Splits an analysed folder into its titles by the folders MOVIES and TVSHOWS the user sorts the files into (owner,
/// 29. 10. 2026: "the user has to tell films from series – 100 %"; the program creates both folders in the input folder
/// and never moves a file). Everything below TVSHOWS is a series – each folder below it one series, a file directly in
/// it an episode of the series its name gives; everything below MOVIES is a film – each folder below it one film (its
/// versions), each file directly in it one film. Without these folders the analysed folder is one title, as before.
/// </summary>
public static partial class TitleUnits
{
    /// <summary>The folder of films in the input folder.</summary>
    public const string MoviesFolder = "MOVIES";

    /// <summary>The folder of series in the input folder.</summary>
    public const string ShowsFolder = "TVSHOWS";

    /// <summary>The titles of the folder; null when nothing lies in MOVIES or TVSHOWS (the folder is one title).</summary>
    public static IReadOnlyList<TitleUnit>? Split(string root, IReadOnlyList<string> files)
    {
        var placed = files.Select(f => (File: f, Place: PlaceOf(root, f))).ToList();
        if (placed.All(p => p.Place is null))
        {
            return null;
        }

        // loose episodes directly in TVSHOWS: those of one series (by the name) together
        string Key((string File, (MediaKind Kind, string Unit)? Place) p) =>
            p.Place is { Kind: MediaKind.Series } place && Paths.Same(place.Unit, p.File) &&
            LibraryJob.SeriesNameOfFiles([p.File], root) is { } series
                ? Path.Combine(Path.GetDirectoryName(p.File)!, Words.Normalize(series.Name))
                : p.Place!.Value.Unit;

        var units = new List<TitleUnit>();
        foreach (var title in placed.Where(p => p.Place is not null).GroupBy(Key, Paths.Comparer))
        {
            var kind = title.First().Place!.Value.Kind;
            var titleFiles = title.Select(p => p.File).ToList();
            units.Add(kind == MediaKind.Series ? Series(title.Key, titleFiles, root) : Film(title.Key, titleFiles));
        }

        // outside MOVIES and TVSHOWS: guessed from the name, the user sorts the file
        foreach (var (file, _) in placed.Where(p => p.Place is null))
        {
            var episode = LibraryJob.LooksLikeEpisode(file, root);
            units.Add(new TitleUnit(file, episode ? MediaKind.Series : MediaKind.Movie, [file],
                episode ? LibraryJob.SeriesNameOfFiles([file], root) : TitleGuess.FromName(Path.GetFileNameWithoutExtension(file), MediaKind.Movie),
                new Dictionary<string, int>(), Sorted: false));
        }

        return units;
    }

    /// <summary>
    /// The episode numbers of a numbered sequence (owner, 29. 10. 2026: "Name 01", "Name 1", "CF01" …) – used only for
    /// files the user put among the series: the names share their beginning and continue with a number of at most
    /// three digits; at least two files, all numbered, the numbers different. Empty when the names are no sequence.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Sequence(IReadOnlyList<string> files)
    {
        var none = new Dictionary<string, int>();
        if (files.Count < 2)
        {
            return none;
        }

        var names = files.Select(f => Path.GetFileNameWithoutExtension(f).Normalize(System.Text.NormalizationForm.FormC)).ToList();
        var prefix = names.Aggregate(CommonPrefix).TrimEnd("0123456789".ToCharArray());
        var numbers = new Dictionary<string, int>(Paths.Comparer);
        for (var i = 0; i < files.Count; i++)
        {
            if (Numbered().Match(names[i][prefix.Length..]) is not { Success: true } match)
            {
                return none;
            }

            numbers[files[i]] = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        }

        return numbers.Values.Distinct().Count() == numbers.Count && numbers.Values.All(n => n > 0) ? numbers : none;
    }

    /// <summary>A folder named MOVIES or TVSHOWS: what it holds.</summary>
    public static MediaKind? KindOfFolder(string? name) =>
        string.Equals(name, ShowsFolder, StringComparison.OrdinalIgnoreCase) ? MediaKind.Series
        : string.Equals(name, MoviesFolder, StringComparison.OrdinalIgnoreCase) ? MediaKind.Movie
        : null;

    // one series below TVSHOWS: named by its folder (a file directly in TVSHOWS by its name); the numbers of a sequence
    // are read among the files of one folder (season folders keep their own numbering)
    private static TitleUnit Series(string unit, List<string> files, string root)
    {
        // a real folder below TVSHOWS names the series; loose files are named by their names
        var isFolder = Directory.Exists(unit);
        var name = isFolder ? TitleGuess.FromName(Path.GetFileName(unit), MediaKind.Series) ?? LibraryJob.SeriesNameOfFiles(files, root)
            : LibraryJob.SeriesNameOfFiles(files, root);
        var numbers = files.Where(f => !EpisodeParser.IsExtra(f, unit) && EpisodeParser.FromPath(f, root) is null)
            .GroupBy(f => Path.GetDirectoryName(f)!, Paths.Comparer)
            .SelectMany(g => Sequence(g.ToList()))
            .ToDictionary(x => x.Key, x => x.Value, Paths.Comparer);
        return new TitleUnit(unit, MediaKind.Series, files, name, numbers);
    }

    private static TitleUnit Film(string unit, List<string> files) =>
        new(unit, MediaKind.Movie, files,
            TitleGuess.FromName(files.Any(f => Paths.Same(f, unit)) ? Path.GetFileNameWithoutExtension(unit) : Path.GetFileName(unit), MediaKind.Movie),
            new Dictionary<string, int>());

    // the kind of the nearest MOVIES or TVSHOWS folder between the file and the analysed folder (both included), and the
    // title: the first folder below it, or the file when it lies directly in it
    private static (MediaKind Kind, string Unit)? PlaceOf(string root, string file)
    {
        var full = Path.GetFullPath(file);
        var top = Path.GetFullPath(root);
        string? below = full;
        for (var folder = Path.GetDirectoryName(full); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            if (KindOfFolder(Path.GetFileName(folder)) is { } kind)
            {
                return (kind, below!);
            }

            if (Paths.Same(folder, top))
            {
                return null;
            }

            below = folder;
        }

        return null;
    }

    private static string CommonPrefix(string a, string b)
    {
        var length = 0;
        while (length < a.Length && length < b.Length && char.ToLowerInvariant(a[length]) == char.ToLowerInvariant(b[length]))
        {
            length++;
        }

        return a[..length];
    }

    // after the common beginning: separators, a word for an episode, then the number (at most three digits)
    [GeneratedRegex(@"^[\s._\-#]*(?:(?:e|ep|epizoda|epizóda|díl|dil|diel|časť|cast|part|folge)[\s._\-#]*)?(?<n>\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Numbered();
}
