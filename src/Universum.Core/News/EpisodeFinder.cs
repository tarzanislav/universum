using Universum.Core.Batch;
using Universum.Core.Library;
using Universum.Core.Text;

namespace Universum.Core.News;

/// <summary>An episode chosen in Novinky and the video files of the input folder that are it (none when not there yet).</summary>
public sealed record EpisodeFiles(SeriesEpisode Episode, IReadOnlyList<string> Files);

/// <summary>
/// Finds the files of chosen episodes in the input folder (with its subfolders; the library and disc copies are left
/// out): a file is an episode when its name (or its folders) gives the same season and episode and its title is the
/// series' original or local title – without case, diacritics and punctuation; a year in the name must agree (±1).
/// Files are only read, never touched.
/// </summary>
public static class EpisodeFinder
{
    public static IReadOnlyList<EpisodeFiles> Find(IReadOnlyList<SeriesEpisode> episodes, string folder, IEnumerable<string>? excludeDirectories = null,
        CancellationToken cancellationToken = default)
    {
        var (files, _) = BatchAnalyzer.FindFiles(folder, recursive: true, excludeDirectories, cancellationToken);
        var guesses = files.Select(f => (Path: f, Guess: TitleGuess.ForFile(f))).Where(g => g.Guess.Episode is not null).ToList();
        return episodes.Select(e => new EpisodeFiles(e, guesses.Where(g => Matches(e, g.Guess)).Select(g => g.Path).ToList())).ToList();
    }

    /// <summary>True when a file's guess is the episode.</summary>
    public static bool Matches(SeriesEpisode episode, FileGuess guess)
    {
        if (guess.Episode?.Number is not { } number || episode.Season is not { } season || episode.Number is not { } first || number.Season != season)
        {
            return false;
        }

        // the file's episodes must lie within the aired ones (E09-E10 aired: E09, E10 or E09-E10)
        var last = episode.LastNumber ?? first;
        if (number.Episode < first || (number.LastEpisode ?? number.Episode) > last)
        {
            return false;
        }

        if (guess.Title is not { } title || (title.Year is { } year && episode.Year is { } started && Math.Abs(year - started) > 1))
        {
            return false;
        }

        var name = Words.Normalize(title.Name);
        return name.Length > 0 && new[] { episode.OriginalTitle, episode.Title }.Select(Words.Normalize).Any(t => t.Length > 0 && SameTitle(name, t));
    }

    // the same title, or one starting the other at a word ("Invented Show US" for "Invented Show") when it is long enough
    private static bool SameTitle(string a, string b) =>
        a == b || (Math.Min(a.Length, b.Length) >= 6 && (a.StartsWith(b + " ", StringComparison.Ordinal) || b.StartsWith(a + " ", StringComparison.Ordinal)));
}

/// <summary>A film chosen by the user and the video files of the input folder that are it.</summary>
public sealed record FilmFiles(FilmPremiere Film, IReadOnlyList<string> Files);

/// <summary>
/// Finds chosen films in the input folder: a file (not an episode) whose title is the film's original, English or
/// Czech title – without case, diacritics and punctuation – and whose year, when the name has one, agrees (±1).
/// Files are only read.
/// </summary>
public static class FilmFinder
{
    public static IReadOnlyList<FilmFiles> Find(IReadOnlyList<FilmPremiere> films, string folder, IEnumerable<string>? excludeDirectories = null,
        CancellationToken cancellationToken = default)
    {
        var (files, _) = BatchAnalyzer.FindFiles(folder, recursive: true, excludeDirectories, cancellationToken);
        var guesses = files.Select(f => (Path: f, Guess: TitleGuess.ForFile(f))).Where(g => g.Guess.Episode is null && g.Guess.Title is not null).ToList();
        return films.Select(f => new FilmFiles(f, guesses.Where(g => Matches(f, g.Guess)).Select(g => g.Path).ToList())).ToList();
    }

    public static bool Matches(FilmPremiere film, FileGuess guess)
    {
        if (guess.Episode is not null || guess.Title is not { } title || (title.Year is { } year && film.Year is { } released && Math.Abs(year - released) > 1))
        {
            return false;
        }

        var name = Words.Normalize(title.Name);
        return name.Length > 0 && new[] { film.OriginalTitle, film.EnglishTitle, film.LocalTitle }.Select(Words.Normalize).Any(t => t.Length > 0 && t == name);
    }
}
