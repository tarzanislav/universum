using System.Globalization;
using Universum.Core.IO;
using Universum.Core.Rules;

namespace Universum.Core.Library;

/// <summary>
/// Where a result goes in the library and how it is named. The size class is known only after writing, so the final
/// name is chosen then: <c>{Prefix} - {Stem} {size}{n}.mkv</c>, where n = 2, 3 … only when that name is taken by
/// another stream (specification 13.3, K1).
/// </summary>
/// <param name="Directory">Folder of the title (film) or of the season (series).</param>
/// <param name="Prefix">Film: the folder name ("Creep (2004)"); episode: "Nine Puzzles S01E04".</param>
/// <param name="Stem">Languages, quality and codec ("CZ EN FHD H264").</param>
public sealed record Placement(string Directory, string Prefix, string Stem, SizeClasses Sizes, string Extension = ".mkv")
{
    /// <summary>
    /// The file keeps exactly this name (header editor: the file is edited where it is); a free name for a second
    /// file would be "name (2).mkv".
    /// </summary>
    public string? FixedName { get; init; }

    /// <summary>
    /// The file at <paramref name="path"/> is edited where it is and keeps its name. Internal: originals are never
    /// edited (owner's decision); kept for tests and a possible later, explicitly agreed use.
    /// </summary>
    internal static Placement InPlace(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return new Placement(System.IO.Path.GetDirectoryName(full)!, System.IO.Path.GetFileNameWithoutExtension(full), "", new SizeClasses(), System.IO.Path.GetExtension(full))
        {
            FixedName = System.IO.Path.GetFileName(full),
        };
    }

    /// <summary>
    /// A new file next to <paramref name="path"/> named "name (upravené).mkv" (then "name (upravené) (2).mkv" …): the
    /// original is not touched (header editor with "keep originals").
    /// </summary>
    internal static Placement Beside(string path, string suffix = " (upravené)")
    {
        var full = System.IO.Path.GetFullPath(path);
        var prefix = System.IO.Path.GetFileNameWithoutExtension(full) + suffix;
        var extension = System.IO.Path.GetExtension(full);
        return new Placement(System.IO.Path.GetDirectoryName(full)!, prefix, "", new SizeClasses(), extension) { FixedName = prefix + extension };
    }

    public string FileName(long size, int number = 1) =>
        FixedName is not null
            ? number <= 1 ? FixedName : $"{Prefix} ({number.ToString(CultureInfo.InvariantCulture)}){Extension}"
            : Prefix + " - " + (Stem.Length > 0 ? Stem + " " : "") + Sizes.Label(size) +
              (number > 1 ? number.ToString(CultureInfo.InvariantCulture) : "") + Extension;

    public string PathFor(long size, int number = 1) => Path.Combine(Directory, FileName(size, number));

    /// <summary>Start shared by every version of this stream: "Creep (2004) - CZ EN FHD H264 ".</summary>
    public string VersionStart => Prefix + " - " + (Stem.Length > 0 ? Stem + " " : "");

    /// <summary>
    /// True when <paramref name="fileName"/> is a name this placement gives (any size class, any number); used to
    /// recognise a file that is already in its place and versions of the same stream.
    /// </summary>
    public bool IsVersionName(string fileName)
    {
        if (FixedName is not null)
        {
            return string.Equals(fileName, FixedName, Paths.Comparison);
        }

        if (!fileName.StartsWith(VersionStart, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = fileName[VersionStart.Length..^Extension.Length];
        foreach (var label in SizeClasses.AllLabels)
        {
            if (rest.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                var number = rest[label.Length..];
                return number.Length == 0 || (number.All(char.IsAsciiDigit) && number[0] != '0' && number != "1");
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="fileName"/> is a name the library gives a result of this title (or episode), whatever
    /// its stream description: "{Prefix} - … {size}{n}.mkv". Files with other names in the folder (an existing
    /// collection, originals put there by hand) are never treated as library results.
    /// </summary>
    public bool IsLibraryName(string fileName)
    {
        if (FixedName is not null)
        {
            return string.Equals(fileName, FixedName, Paths.Comparison);
        }

        var start = Prefix + " - ";
        if (fileName.Length < start.Length + Extension.Length ||
            !fileName.StartsWith(start, StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // the name ends with a size class and maybe a version number: "… BIG", "… BluRay BIG2"
        var rest = fileName[start.Length..^Extension.Length].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return SizeClasses.AllLabels.Any(label =>
            rest.EndsWith(label, StringComparison.OrdinalIgnoreCase) && (rest.Length == label.Length || rest[^(label.Length + 1)] == ' '));
    }

    /// <summary>
    /// True when the file at <paramref name="path"/> already is in its place: in the folder, named by this placement
    /// with the size class of <paramref name="size"/> (with or without a version number).
    /// </summary>
    public bool Holds(string path, long size)
    {
        var name = Path.GetFileName(path);
        if (!Paths.IsIn(path, Directory) || !IsVersionName(name))
        {
            return false;
        }

        if (FixedName is not null)
        {
            return true;
        }

        var label = name[VersionStart.Length..^Extension.Length].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return string.Equals(label, Sizes.Label(size), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Length of the full path of a result of the given size, with room for a version number.</summary>
    public int EstimatedPathLength(long size) => PathFor(size, 2).Length;
}

/// <summary>The output folder with its MOVIES and TVSHOWS folders (specification, section 13.1).</summary>
public sealed record LibraryLayout(string Root)
{
    public string Movies => Path.Combine(Root, LibraryNames.MoviesFolder);

    public string Shows => Path.Combine(Root, LibraryNames.ShowsFolder);

    /// <summary>
    /// Where results are written (MOVIES and TVSHOWS): batch processing skips them. Other folders of the output folder
    /// may hold originals (owner's report, 27. 10. 2026: streams kept in the output folder were not found).
    /// </summary>
    public IReadOnlyList<string> ResultFolders => [Movies, Shows];

    /// <summary>Creates MOVIES and TVSHOWS. UNIVERSUM never deletes folders in the output folder.</summary>
    public void EnsureCreated()
    {
        System.IO.Directory.CreateDirectory(Movies);
        System.IO.Directory.CreateDirectory(Shows);
    }

    /// <summary>Folder of a title: MOVIES\Creep (2004) or TVSHOWS\Nine Puzzles (2025) [tmdbid-227191].</summary>
    public string TitleDirectory(TitleInfo title) =>
        Path.Combine(title.Kind == MediaKind.Movie ? Movies : Shows, title.FolderName);

    /// <summary>Place of a result: films in their title folder, episodes in the season folder.</summary>
    public Placement Place(TargetState target, TitleInfo title, EpisodeNumber? episode, RuleProfile profile)
    {
        var stem = StreamLabel.Stem(target, profile);
        var sizes = profile.Library.SizesFor(title.Kind);
        if (title.Kind == MediaKind.Movie)
        {
            return new Placement(TitleDirectory(title), title.FolderName, stem, sizes);
        }

        if (episode is null)
        {
            throw new ArgumentException("A series file needs an episode number.", nameof(episode));
        }

        return new Placement(
            Path.Combine(TitleDirectory(title), LibraryNames.SeasonFolder(episode.Season)),
            title.CleanName + " " + episode.Tag,
            stem,
            sizes);
    }

    /// <summary>True when <paramref name="path"/> lies in MOVIES or TVSHOWS (where results are written).</summary>
    public bool HoldsResult(string path) =>
        ResultFolders.Any(folder => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, Paths.Comparison));

    /// <summary>True when <paramref name="path"/> lies inside the output folder.</summary>
    public bool Contains(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, Paths.Comparison);
    }

    /// <summary>Unfinished results (temporary copies) left in the library after an interruption; safe to remove.</summary>
    public IReadOnlyList<string> FindLeftovers() => Find(Execution.FileProcessor.IsTemporaryFile);

    /// <summary>
    /// Originals or replaced library files left half-way by an interruption (".universum-moving", ".universum-old"):
    /// possibly the only copy, never removed automatically.
    /// </summary>
    public IReadOnlyList<string> FindInterrupted() => Find(Execution.FileProcessor.IsInterruptedFile);

    private List<string> Find(Func<string, bool> predicate) =>
        System.IO.Directory.Exists(Root)
            ? System.IO.Directory.EnumerateFiles(Root, "*.universum-*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(predicate).ToList()
            : [];
}
