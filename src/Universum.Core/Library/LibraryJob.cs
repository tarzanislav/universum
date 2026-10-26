using Universum.Core.Batch;
using Universum.Core.IO;
using Universum.Core.Library.Tmdb;
using Universum.Core.Text;

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
    /// Seasons of a series as TMDB lists them (with episode counts), for the title they were read for; used only while
    /// that title is the series of this job (specification 13.5: TMDB checks that season and episode exist).
    /// </summary>
    public (TitleInfo Title, IReadOnlyList<TmdbSeason> Seasons)? SeriesOnTmdb { get; set; }

    /// <summary>
    /// False when TMDB lists the seasons of the series and does not know this episode; true when it does or nothing is
    /// known (no TMDB, another title, a season without an episode count).
    /// </summary>
    public bool TmdbKnows(EpisodeNumber episode) =>
        SeriesOnTmdb is not { } known || Series is null || known.Title.FolderName != Series.FolderName || TmdbSeasons.Has(known.Seasons, episode);

    /// <summary>
    /// Guesses everything from the names: a series when most files carry an episode number or lie in season folders,
    /// the series title from the folder (when it is in library form, with a year or an ID) or from the start of the
    /// file names, film titles from file (or their own folder) names, episode numbers in folder order per season.
    /// Names in library form with a TMDB ID ("… (2025) [tmdbid-227191]") are taken as confirmed.
    /// </summary>
    public static LibraryJob Guess(LibraryLayout layout, BatchAnalysis analysis)
    {
        var job = new LibraryJob(layout);
        var files = analysis.Groups.SelectMany(g => g.Files).Select(f => f.Path).Order(NaturalComparer.Instance).ToList();
        // one analysed folder is one series: its season folders count even for files without a number in the name
        var numbered = files.Count(f => EpisodeParser.FromPath(f) is not null || SeasonFolderBelow(analysis.Folder, f) is not null);
        job.Kind = files.Count > 0 && numbered * 2 >= files.Count ? MediaKind.Series : MediaKind.Movie;

        // series: the folder of the series (above a "Season 2" folder) or the start of the file names
        var folder = analysis.Folder;
        if (EpisodeParser.IsSeasonOnlyFolder(Path.GetFileName(folder)) && Path.GetDirectoryName(folder) is { } parent)
        {
            folder = parent;
        }

        var fromFolder = TitleGuess.FromName(Path.GetFileName(folder));
        var fromFile = SeriesNameOfFiles(files);
        if (fromFolder is { TmdbId: not null })
        {
            job.Series = Title(MediaKind.Series, fromFolder);
        }
        else if (TitleGuess.Preferred(fromFolder, fromFile) is { } seriesGuess)
        {
            job.SeriesSuggestion = Title(MediaKind.Series, seriesGuess with { Year = seriesGuess.Year ?? fromFolder?.Year });
        }

        // films: each file, named by its own folder when it has one with a year, otherwise by the file name
        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file)!;
            var ownFolder = Paths.Same(directory, analysis.Folder) ? null : TitleGuess.FromName(Path.GetFileName(directory), MediaKind.Movie);
            var guess = ownFolder is { Year: not null } ? ownFolder : TitleGuess.FromName(Path.GetFileNameWithoutExtension(file), MediaKind.Movie) ?? ownFolder;
            if (guess is { TmdbId: not null })
            {
                job.Movies[file] = Title(MediaKind.Movie, guess);
            }
            else if (guess is not null)
            {
                job.MovieSuggestions[file] = Title(MediaKind.Movie, guess);
            }
        }

        // episodes without a number in the name: the next free numbers of their season (from the season folder, else 1)
        // in folder order
        var used = files.Select(EpisodeParser.FromPath).OfType<EpisodeMatch>()
            .SelectMany(m => Enumerable.Range(m.Number.Episode, (m.Number.LastEpisode ?? m.Number.Episode) - m.Number.Episode + 1).Select(e => (m.Number.Season, e)))
            .ToHashSet();
        foreach (var file in files.Where(f => EpisodeParser.FromPath(f) is null))
        {
            var season = SeasonFolderBelow(analysis.Folder, file) ?? EpisodeParser.SeasonOfPath(file) ?? 1;
            var next = 1;
            while (used.Contains((season, next)))
            {
                next++;
            }

            job.EpisodeSuggestions[file] = new EpisodeNumber(season, next);
            used.Add((season, next));
        }

        // numbers from the names whose season is only assumed or disagrees with the season folder: to be confirmed
        foreach (var file in files)
        {
            if (EpisodeParser.FromPath(file) is { NeedsConfirmation: true } doubtful)
            {
                job.EpisodeSuggestions[file] = doubtful.Number;
            }
        }

        return job;
    }

    /// <summary>
    /// The series name most episode files start with ("Nine.Puzzles.S01E04…" → "Nine Puzzles"); null when the names
    /// carry no title ("E04.mkv").
    /// </summary>
    public static NameGuess? SeriesNameOfFiles(IEnumerable<string> files) =>
        files.Where(f => EpisodeParser.FromPath(f) is not null)
            .Select(f => TitleGuess.FromName(Path.GetFileNameWithoutExtension(f)))
            .OfType<NameGuess>()
            .GroupBy(g => Words.Normalize(g.Name), StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.First())
            .FirstOrDefault();

    // season of a season folder between the analysed folder and the file ("Séria 2\a.mkv"), or null
    private static int? SeasonFolderBelow(string root, string file)
    {
        var folder = Path.GetDirectoryName(file);
        while (!string.IsNullOrEmpty(folder) && !Paths.Same(folder, root) && Paths.IsIn(folder, root))
        {
            if (EpisodeParser.SeasonOfFolder(Path.GetFileName(folder)) is { } season)
            {
                return season;
            }

            folder = Path.GetDirectoryName(folder);
        }

        return null;
    }

    /// <summary>The title a file is stored under; null while it is not decided.</summary>
    public TitleInfo? TitleFor(string path) => Kind == MediaKind.Series ? Series : Movies.GetValueOrDefault(path);

    /// <summary>A suggested title waiting for confirmation, when there is no decided one.</summary>
    public TitleInfo? SuggestedTitleFor(string path) =>
        TitleFor(path) is not null ? null : Kind == MediaKind.Series ? SeriesSuggestion : MovieSuggestions.GetValueOrDefault(path);

    /// <summary>
    /// Episode of a file: set by the user, found in its name, or suggested (to be confirmed). A number from the name is
    /// only a suggestion when its season was assumed or the season folder says otherwise.
    /// </summary>
    public (EpisodeNumber? Number, bool Suggested) EpisodeFor(string path)
    {
        if (Episodes.TryGetValue(path, out var decided))
        {
            return (decided, false);
        }

        if (EpisodeSuggestions.TryGetValue(path, out var suggested))
        {
            return (suggested, true);
        }

        return EpisodeParser.FromPath(path) is { } found ? (found.Number, found.NeedsConfirmation) : (null, false);
    }

    /// <summary>Why the episode of a file is only suggested (Slovak), or null.</summary>
    public string? EpisodeDoubt(string path)
    {
        if (Episodes.ContainsKey(path))
        {
            return null;
        }

        return EpisodeParser.FromPath(path) switch
        {
            { FolderSeason: { } folder } found => $"sezóna v mene ({found.Number.Season}) a v priečinku ({folder}) sa líšia",
            { SeasonAssumed: true } => "sezóna nie je v mene ani v priečinku – predpokladá sa 1",
            null when EpisodeSuggestions.ContainsKey(path) => "číslo nie je v mene – návrh podľa poradia",
            _ => null,
        };
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
