using Universum.Core.Batch;
using Universum.Core.IO;

namespace Universum.Core.Library;

/// <summary>
/// Naming decisions for one batch (specification, section 13.5): whether the folder holds films or a series, the
/// titles and the episode numbers. Guesses from file and folder names are suggestions until the user confirms them.
/// </summary>
public sealed class LibraryJob(LibraryLayout layout)
{
    public LibraryLayout Layout { get; } = layout;

    public MediaKind Kind { get; set; } = MediaKind.Movie;

    /// <summary>Confirmed title of the series (one series per analysed folder).</summary>
    public TitleInfo? Series { get; set; }

    public TitleInfo? SeriesSuggestion { get; set; }

    /// <summary>Confirmed film titles by file path.</summary>
    public Dictionary<string, TitleInfo> Movies { get; } = new(Paths.Comparer);

    public Dictionary<string, TitleInfo> MovieSuggestions { get; } = new(Paths.Comparer);

    /// <summary>Episode numbers set by the user (they win over numbers found in the names).</summary>
    public Dictionary<string, EpisodeNumber> Episodes { get; } = new(Paths.Comparer);

    /// <summary>Numbers by order in the folder for files whose names carry none; used only after confirmation.</summary>
    public Dictionary<string, EpisodeNumber> EpisodeSuggestions { get; } = new(Paths.Comparer);

    /// <summary>
    /// Guesses everything from the names: a series when most files carry an episode number, the series title from the
    /// file names or the folder, film titles from file (or their own folder) names, episode numbers in folder order.
    /// Names in library form with a TMDB ID ("… (2025) [tmdbid-227191]") are taken as confirmed.
    /// </summary>
    public static LibraryJob Guess(LibraryLayout layout, BatchAnalysis analysis)
    {
        var job = new LibraryJob(layout);
        var files = analysis.Groups.SelectMany(g => g.Files).Select(f => f.Path).Order(NaturalComparer.Instance).ToList();
        var numbered = files.Count(f => EpisodeParser.FromPath(f) is not null);
        job.Kind = files.Count > 0 && numbered * 2 >= files.Count ? MediaKind.Series : MediaKind.Movie;

        // series: the folder of the series (above a "Season 2" folder) or the start of the file names
        var folder = analysis.Folder;
        if (EpisodeParser.SeasonOfFolder(Path.GetFileName(folder)) is not null && Path.GetDirectoryName(folder) is { } parent)
        {
            folder = parent;
        }

        var fromFolder = TitleGuess.FromName(Path.GetFileName(folder));
        // the name most episodes start with ("Nine.Puzzles.S01E04…" → "Nine Puzzles")
        var fromFile = files.Where(f => EpisodeParser.FromPath(f) is not null)
            .Select(f => TitleGuess.FromName(Path.GetFileNameWithoutExtension(f)))
            .OfType<NameGuess>()
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.First())
            .FirstOrDefault();
        if (fromFolder is { TmdbId: not null })
        {
            job.Series = Title(MediaKind.Series, fromFolder);
        }
        else if ((fromFile ?? fromFolder) is { } seriesGuess)
        {
            job.SeriesSuggestion = Title(MediaKind.Series, seriesGuess with { Year = seriesGuess.Year ?? fromFolder?.Year });
        }

        // films: each file, named by its own folder when it has one with a year, otherwise by the file name
        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file)!;
            var ownFolder = Paths.Same(directory, analysis.Folder) ? null : TitleGuess.FromName(Path.GetFileName(directory));
            var guess = ownFolder is { Year: not null } ? ownFolder : TitleGuess.FromName(Path.GetFileNameWithoutExtension(file)) ?? ownFolder;
            if (guess is { TmdbId: not null })
            {
                job.Movies[file] = Title(MediaKind.Movie, guess);
            }
            else if (guess is not null)
            {
                job.MovieSuggestions[file] = Title(MediaKind.Movie, guess);
            }
        }

        // episodes without a number in the name: the next free numbers of season 1 in folder order
        var used = files.Select(EpisodeParser.FromPath).Where(m => m is not null).Select(m => m!.Number)
            .Where(n => n.Season == 1).Select(n => n.Episode).ToHashSet();
        var next = 1;
        foreach (var file in files.Where(f => EpisodeParser.FromPath(f) is null))
        {
            while (used.Contains(next))
            {
                next++;
            }

            job.EpisodeSuggestions[file] = new EpisodeNumber(1, next);
            used.Add(next);
        }

        return job;
    }

    /// <summary>The title a file is stored under; null while it is not decided.</summary>
    public TitleInfo? TitleFor(string path) => Kind == MediaKind.Series ? Series : Movies.GetValueOrDefault(path);

    /// <summary>A suggested title waiting for confirmation, when there is no decided one.</summary>
    public TitleInfo? SuggestedTitleFor(string path) =>
        TitleFor(path) is not null ? null : Kind == MediaKind.Series ? SeriesSuggestion : MovieSuggestions.GetValueOrDefault(path);

    /// <summary>Episode of a file: set by the user, found in its name, or suggested (to be confirmed).</summary>
    public (EpisodeNumber? Number, bool Suggested) EpisodeFor(string path)
    {
        if (Episodes.TryGetValue(path, out var decided))
        {
            return (decided, false);
        }

        if (EpisodeParser.FromPath(path) is { } found)
        {
            return (found.Number, false);
        }

        return EpisodeSuggestions.TryGetValue(path, out var suggested) ? (suggested, true) : (null, false);
    }

    /// <summary>Accepts all suggested titles and episode numbers.</summary>
    public void ConfirmSuggestions()
    {
        Series ??= SeriesSuggestion;
        foreach (var (path, title) in MovieSuggestions)
        {
            Movies.TryAdd(path, title);
        }

        foreach (var (path, number) in EpisodeSuggestions)
        {
            Episodes.TryAdd(path, number);
        }
    }

    private static TitleInfo Title(MediaKind kind, NameGuess guess) => new(kind, guess.Name, guess.Year, guess.TmdbId, guess.TmdbId is not null);
}
