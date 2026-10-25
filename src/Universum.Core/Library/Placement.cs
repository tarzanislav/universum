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
    public string FileName(long size, int number = 1) =>
        Prefix + " - " + (Stem.Length > 0 ? Stem + " " : "") + Sizes.Label(size) +
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
        if (!fileName.StartsWith(VersionStart, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = fileName[VersionStart.Length..^Extension.Length];
        foreach (var label in SizeClasses.AllLabels)
        {
            if (rest.StartsWith(label, StringComparison.Ordinal))
            {
                var number = rest[label.Length..];
                return number.Length == 0 || (number.All(char.IsAsciiDigit) && number[0] != '0' && number != "1");
            }
        }

        return false;
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

        var label = name[VersionStart.Length..^Extension.Length].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return string.Equals(label, Sizes.Label(size), StringComparison.Ordinal);
    }

    /// <summary>Length of the full path of a result of the given size, with room for a version number.</summary>
    public int EstimatedPathLength(long size) => PathFor(size, 2).Length;
}

/// <summary>The output folder with its MOVIES and TVSHOWS folders (specification, section 13.1).</summary>
public sealed record LibraryLayout(string Root)
{
    public string Movies => Path.Combine(Root, LibraryNames.MoviesFolder);

    public string Shows => Path.Combine(Root, LibraryNames.ShowsFolder);

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

    /// <summary>True when <paramref name="path"/> lies inside the output folder.</summary>
    public bool Contains(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, Paths.Comparison);
    }

    /// <summary>Unfinished results (temporary files) left in the library after an interruption.</summary>
    public IReadOnlyList<string> FindLeftovers() =>
        System.IO.Directory.Exists(Root)
            ? System.IO.Directory.EnumerateFiles(Root, "*" + Execution.FileProcessor.TemporaryExtension, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList()
            : [];
}
