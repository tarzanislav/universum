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
    private MediaKind _kind = MediaKind.Movie;

    public LibraryLayout Layout { get; } = layout;

    /// <summary>The analysed folder (one series, or films); season folders are looked for only below it.</summary>
    public string? Root { get; private set; }

    /// <summary>
    /// The titles of a folder the user sorted into MOVIES and TVSHOWS (owner, 29. 10. 2026; <see cref="TitleUnits"/>); null
    /// when the folder is one title. Then each file has its own kind (<see cref="KindFor"/>) and series.
    /// </summary>
    public IReadOnlyList<TitleUnit>? Units { get; private set; }

    /// <summary>The folder holds several titles, each file is named by its own (<see cref="Units"/>).</summary>
    public bool Mixed => Units is not null;

    /// <summary>The kind of each file of a folder of several titles (<see cref="Units"/>).</summary>
    public Dictionary<string, MediaKind> KindOfFile { get; } = new(Paths.Comparer);

    /// <summary>Files whose kind the user confirmed (a group accepted or its kind chosen).</summary>
    public HashSet<string> ConfirmedKinds { get; } = new(Paths.Comparer);

    /// <summary>Film or episode: the file's own kind in a folder of several titles, else the kind of the folder.</summary>
    public MediaKind KindFor(string path) => KindOfFile.TryGetValue(path, out var kind) ? kind : Kind;

    /// <summary>The kind of the file is decided (the folder's kind confirmed, or the file's).</summary>
    public bool KindConfirmedFor(string path) => KindConfirmed || ConfirmedKinds.Contains(path);

    /// <summary>Files lying outside MOVIES and TVSHOWS of a sorted folder: the user has to sort them (owner, 29. 10. 2026).</summary>
    public HashSet<string> Unsorted { get; } = new(Paths.Comparer);

    /// <summary>The series suggested for each file of a folder of several titles (its folder's name), to be confirmed.</summary>
    public Dictionary<string, TitleInfo> SeriesSuggestionOfFile { get; } = new(Paths.Comparer);

    /// <summary>The title a file belongs to: its own folder below the analysed one (or the file itself); the analysed folder for one title.</summary>
    public string UnitOf(string path) =>
        Units?.FirstOrDefault(u => u.Files.Contains(path, Paths.Comparer))?.Folder ?? Root ?? "";

    /// <summary>Films or a series; setting it confirms it.</summary>
    public MediaKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            KindConfirmed = true;
        }
    }

    /// <summary>
    /// False while the kind is only guessed from the names (specification 13.5: UNIVERSUM proposes Film / Series, the
    /// user confirms it); nothing goes into the library before.
    /// </summary>
    public bool KindConfirmed { get; private set; }

    /// <summary>Confirmed title of the series (one series per analysed folder).</summary>
    public TitleInfo? Series { get; set; }

    /// <summary>
    /// Files of further series in the same batch (owner, 29. 10. 2026: episodes of several series sent from Novinky at
    /// once): each of them goes to its own series, the other files to <see cref="Series"/>.
    /// </summary>
    public Dictionary<string, TitleInfo> SeriesOfFile { get; } = new(Paths.Comparer);

    /// <summary>The series of a file: its own (<see cref="SeriesOfFile"/>), else the series of the batch.</summary>
    public TitleInfo? SeriesFor(string path) => SeriesOfFile.GetValueOrDefault(path) ?? Series;

    public TitleInfo? SeriesSuggestion { get; set; }

    /// <summary>Confirmed film titles by file path.</summary>
    public Dictionary<string, TitleInfo> Movies { get; } = new(Paths.Comparer);

    public Dictionary<string, TitleInfo> MovieSuggestions { get; } = new(Paths.Comparer);

    /// <summary>
    /// The film titles read from the names (folder or file), kept apart from the suggestions: a title service is
    /// always asked by them, never by a title it suggested before (that would confirm its own suggestion).
    /// </summary>
    public Dictionary<string, TitleInfo> NameGuesses { get; } = new(Paths.Comparer);

    /// <summary>Files whose title the user chose (a TMDB address, also one still waiting for confirmation): never searched again.</summary>
    public HashSet<string> ChosenTitles { get; } = new(Paths.Comparer);

    /// <summary>Episode numbers set by the user (they win over numbers found in the names).</summary>
    public Dictionary<string, EpisodeNumber> Episodes { get; } = new(Paths.Comparer);

    /// <summary>Numbers by order in the folder for files whose names carry none; used only after confirmation.</summary>
    public Dictionary<string, EpisodeNumber> EpisodeSuggestions { get; } = new(Paths.Comparer);

    /// <summary>
    /// Files whose season is a question – Season 00 (specials) or a regular season: only an answer for the file (its
    /// number, or "specials into Season 00") decides it, never "accept all suggestions".
    /// </summary>
    public HashSet<string> SeasonQuestions { get; } = new(Paths.Comparer);

    /// <summary>
    /// Seasons of a series as TMDB lists them (with episode counts), for the title they were read for; used only while
    /// that title is the series of this job (specification 13.5: TMDB checks that season and episode exist).
    /// </summary>
    public (TitleInfo Title, IReadOnlyList<TmdbSeason> Seasons)? SeriesOnTmdb { get; set; }

    /// <summary>
    /// False when TMDB lists the seasons of the series and does not know this episode; true when it does or nothing is
    /// known (no TMDB, another title, a season without an episode count).
    /// </summary>
    public bool TmdbKnows(EpisodeNumber episode, string? path = null) =>
        SeriesOnTmdb is not { } known || (path is null ? Series : SeriesFor(path)) is not { } series || known.Title.FolderName != series.FolderName ||
        TmdbSeasons.Has(known.Seasons, episode);

    /// <summary>
    /// Guesses everything from the names: a series when most files carry a clear episode number (S01E04, 1x04; the
    /// word forms "Díl 4", "Časť 2" only without a film year in the name) or lie in season folders; the series title
    /// from the folder (when it is in library form, with a year or an ID, or spelled like the episode names) or from the
    /// start of the file names; film titles from file (or their own folder) names; episode numbers in folder order per
    /// season. The kind is only proposed: it has to be confirmed. Names in library form with a TMDB ID
    /// ("… (2025) [tmdbid-227191]") give confirmed titles.
    /// </summary>
    /// <param name="splitTitles">
    /// A folder of several titles is split into them (<see cref="TitleUnits"/>); false for files sent as episodes of
    /// known series (Novinky).
    /// </param>
    public static LibraryJob Guess(LibraryLayout layout, BatchAnalysis analysis, bool splitTitles = true)
    {
        var job = new LibraryJob(layout) { Root = analysis.Folder };
        var root = analysis.Folder;
        var files = analysis.Groups.SelectMany(g => g.Files).Select(f => f.Path).Order(NaturalComparer.Instance).ToList();
        var content = files.Where(f => !EpisodeParser.IsExtra(f, root)).ToList();
        var parts = NumberedParts(content);
        var numbered = content.Count(f => LooksLikeEpisode(f, root) || SeasonFolderBelow(root, f) is not null || parts.Contains(f));
        job._kind = content.Count > 0 && numbered * 2 > content.Count ? MediaKind.Series : MediaKind.Movie;

        // series: the folder of the series (above a "Season 2" folder) or the start of the file names
        var folder = root;
        if (EpisodeParser.IsSeasonOnlyFolder(Path.GetFileName(folder)) && Path.GetDirectoryName(folder) is { } parent)
        {
            folder = parent;
        }

        var fromFolder = TitleGuess.FromName(Path.GetFileName(folder), MediaKind.Series);
        var fromFile = SeriesNameOfFiles(content, root);
        if (fromFolder is { TmdbId: not null })
        {
            job.Series = Title(MediaKind.Series, fromFolder);
        }
        else if (TitleGuess.Preferred(fromFolder, fromFile) is { } seriesGuess)
        {
            job.SeriesSuggestion = Title(MediaKind.Series, seriesGuess with { Year = seriesGuess.Year ?? fromFolder?.Year });
        }

        // films: each file, named by its own folder when it has one with a year, otherwise by the file name
        foreach (var file in content)
        {
            var directory = Path.GetDirectoryName(file)!;
            var ownFolder = Paths.Same(directory, root) ? null : TitleGuess.FromName(Path.GetFileName(directory), MediaKind.Movie);
            var guess = ownFolder is { Year: not null } ? ownFolder : TitleGuess.FromName(Path.GetFileNameWithoutExtension(file), MediaKind.Movie) ?? ownFolder;
            if (guess is { TmdbId: not null })
            {
                job.Movies[file] = Title(MediaKind.Movie, guess);
            }
            else if (guess is not null)
            {
                job.MovieSuggestions[file] = Title(MediaKind.Movie, guess);
                job.NameGuesses[file] = job.MovieSuggestions[file];
            }
        }

        // episodes without a number in the name: a bare number ("03.mkv") or the next free numbers of their season (from
        // the season folder, else 1) in folder order
        var used = content.Select(f => EpisodeParser.FromPath(f, root)).OfType<EpisodeMatch>()
            .SelectMany(m => Enumerable.Range(m.Number.Episode, (m.Number.LastEpisode ?? m.Number.Episode) - m.Number.Episode + 1).Select(e => (m.Number.Season, e)))
            .ToHashSet();
        // a numbered sequence ("Name 01", "CF01") gives its numbers when the folder is a series (only suggested: whether
        // the folder holds a series or versions of a film the user decides – owner, 29. 10. 2026)
        var sequence = content.Where(f => EpisodeParser.FromPath(f, root) is null)
            .GroupBy(f => Path.GetDirectoryName(f)!, Paths.Comparer)
            .SelectMany(g => TitleUnits.Sequence(g.ToList()))
            .ToDictionary(x => x.Key, x => x.Value, Paths.Comparer);
        foreach (var file in content.Where(f => EpisodeParser.FromPath(f, root) is null))
        {
            var season = SeasonFolderBelow(root, file) ?? 1;
            // under a specials folder ("Specials\Season 1\Bonus.mkv", or above the analysed folder): Season 00 or the
            // season is a question
            if (season != 0 && EpisodeParser.UnderSpecials(file))
            {
                season = 0;
                job.SeasonQuestions.Add(file);
            }

            var next = 1;
            if ((BareNumber(Path.GetFileNameWithoutExtension(file)) ?? sequence.GetValueOrDefault(file)) is > 0 and var own && !used.Contains((season, own)))
            {
                next = own;
            }

            while (used.Contains((season, next)))
            {
                next++;
            }

            job.EpisodeSuggestions[file] = new EpisodeNumber(season, next);
            used.Add((season, next));
        }

        // numbers from the names whose season is only assumed or disagrees with the season folder: to be confirmed; Season 00
        // or a regular season: a question
        foreach (var file in content)
        {
            if (EpisodeParser.FromPath(file, root) is { NeedsConfirmation: true } doubtful)
            {
                job.EpisodeSuggestions[file] = doubtful.Number;
                if (doubtful.IsSeasonQuestion)
                {
                    job.SeasonQuestions.Add(file);
                }
            }
        }

        if (splitTitles)
        {
            job.ApplyUnits(TitleUnits.Split(root, files));
        }

        return job;
    }

    /// <summary>
    /// A folder sorted into MOVIES and TVSHOWS (owner, 29. 10. 2026): each file gets the kind of its folder (certain), the
    /// episodes of a series its folder's name as the suggested series and the numbers of a numbered sequence as suggested
    /// episodes (season from a season folder, else 1); titles and numbers wait for confirmation like any guess.
    /// </summary>
    private void ApplyUnits(IReadOnlyList<TitleUnit>? units)
    {
        Units = units;
        if (units is null)
        {
            return;
        }

        foreach (var unit in units)
        {
            foreach (var file in unit.Files)
            {
                KindOfFile[file] = unit.Kind;

                // sorted by the user into MOVIES or TVSHOWS: the kind is certain; outside them the user sorts the file
                if (unit.Sorted)
                {
                    ConfirmedKinds.Add(file);
                }
                else
                {
                    Unsorted.Add(file);
                }
            }

            if (unit.Kind != MediaKind.Series)
            {
                continue;
            }

            if (unit.Name is { } name)
            {
                foreach (var file in unit.Files)
                {
                    if (name.TmdbId is not null)
                    {
                        SeriesOfFile[file] = Title(MediaKind.Series, name);
                    }
                    else
                    {
                        SeriesSuggestionOfFile[file] = Title(MediaKind.Series, name);
                    }
                }
            }

            // numbers of a sequence ("Name 01"), then the next free numbers of the title's own seasons for files without one
            var used = new HashSet<(int Season, int Episode)>();
            foreach (var match in unit.Files.Select(f => EpisodeParser.FromPath(f, Root)).OfType<EpisodeMatch>())
            {
                for (var e = match.Number.Episode; e <= (match.Number.LastEpisode ?? match.Number.Episode); e++)
                {
                    used.Add((match.Number.Season, e));
                }
            }

            foreach (var (file, number) in unit.Numbers)
            {
                var season = EpisodeParser.SeasonOfPath(file, Root) ?? 1;
                EpisodeSuggestions[file] = new EpisodeNumber(season, number);
                used.Add((season, number));
            }

            foreach (var file in unit.Files.Where(f => !EpisodeParser.IsExtra(f, Root) && !unit.Numbers.ContainsKey(f) && EpisodeParser.FromPath(f, Root) is null)
                         .Order(NaturalComparer.Instance))
            {
                var season = EpisodeParser.SeasonOfPath(file, Root) ?? 1;
                var next = 1;
                while (used.Contains((season, next)))
                {
                    next++;
                }

                EpisodeSuggestions[file] = new EpisodeNumber(season, next);
                used.Add((season, next));
            }
        }

        // the analysed folder is no series of its own
        Series = null;
        SeriesSuggestion = null;

        // the kind of the folder is the kind of most files (what the radio buttons show before a group is chosen)
        _kind = KindOfFile.Values.Count(k => k == MediaKind.Series) * 2 > KindOfFile.Count ? MediaKind.Series : MediaKind.Movie;
    }

    /// <summary>
    /// "Prijať všetko v skupine" (owner, 29. 10. 2026): the files' kind, title (also one HISTRA did not confirm) and
    /// episode number as suggested – except the questions Season 00 or a regular season.
    /// </summary>
    public void AcceptAll(IEnumerable<string> paths)
    {
        // one title per folder: its kind is the folder's
        if (!Mixed)
        {
            KindConfirmed = true;
        }

        foreach (var path in paths)
        {
            ConfirmedKinds.Add(path);
            if (KindFor(path) == MediaKind.Series)
            {
                if (Mixed)
                {
                    if (!SeriesOfFile.ContainsKey(path) && SeriesSuggestionOfFile.TryGetValue(path, out var series))
                    {
                        SeriesOfFile[path] = series;
                    }
                }
                else
                {
                    Series ??= SeriesSuggestion;
                    SeriesUnverified = false;
                }

                if (EpisodeSuggestions.TryGetValue(path, out var number) && !SeasonQuestions.Contains(path))
                {
                    Episodes.TryAdd(path, number);
                }
            }
            else if (!Movies.ContainsKey(path))
            {
                // a title the user chose stays; a suggestion (also one HISTRA did not confirm) is accepted
                ConfirmTitle(path);
            }
        }
    }

    /// <summary>
    /// The kind of some files chosen by the user (a group of a folder of several titles): a series gets its folder's
    /// name as the suggested series when it has none.
    /// </summary>
    public void SetKindOf(IReadOnlyCollection<string> paths, MediaKind kind)
    {
        foreach (var path in paths)
        {
            KindOfFile[path] = kind;
            ConfirmedKinds.Add(path);
            if (kind == MediaKind.Series && SeriesFor(path) is null && !SeriesSuggestionOfFile.ContainsKey(path) &&
                Units?.FirstOrDefault(u => u.Files.Contains(path, Paths.Comparer)) is { } unit)
            {
                var name = unit.Kind == MediaKind.Series ? unit.Name
                    : File.Exists(unit.Folder) ? SeriesNameOfFiles([path], Root) : TitleGuess.FromName(Path.GetFileName(unit.Folder), MediaKind.Series);
                if (name is not null)
                {
                    SeriesSuggestionOfFile[path] = Title(MediaKind.Series, name);
                }
            }
        }
    }

    /// <summary>
    /// The series name most episode files start with ("Nine.Puzzles.S01E04…" → "Nine Puzzles"); null when the names
    /// carry no title ("E04.mkv").
    /// </summary>
    public static NameGuess? SeriesNameOfFiles(IEnumerable<string> files, string? root = null) =>
        files.Where(f => EpisodeParser.FromPath(f, root) is not null)
            .Select(f => TitleGuess.FromName(Path.GetFileNameWithoutExtension(f), MediaKind.Series))
            .OfType<NameGuess>()
            .GroupBy(g => Words.Normalize(g.Name), StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.First())
            .FirstOrDefault();

    /// <summary>
    /// Files named alike except for a number before the same year ("Kriminálka Anděl - Díl 1 (2008)", "… Díl 2 (2008)"):
    /// episodes of one series, not films ("Sága - Časť 1 (2010)" and "Časť 2 (2011)" have different years).
    /// </summary>
    private static HashSet<string> NumberedParts(IEnumerable<string> files) =>
        files.Select(f => (File: f, Name: Path.GetFileNameWithoutExtension(f)))
            .Where(x => TitleGuess.HasYearInParentheses(x.Name) && EpisodeParser.FromText(x.Name) is { Form: not EpisodeForm.SeasonEpisode })
            .GroupBy(x => TitleGuess.FromName(x.Name, MediaKind.Series) is { } g ? Words.Normalize(g.Name) + "|" + g.Year : x.File, StringComparer.Ordinal)
            .Where(g => g.Select(x => EpisodeParser.FromText(x.Name)!.Number.Episode).Distinct().Count() > 1)
            .SelectMany(g => g.Select(x => x.File))
            .ToHashSet(Paths.Comparer);

    /// <summary>
    /// An episode by its name: S01E04 always; 1x04, "Díl 4", "E04" unless the number stands in a film's title before its
    /// year ("Sága - Časť 2 (2011)"); "Kriminálka Anděl (2008) Díl 1" is an episode.
    /// </summary>
    public static bool LooksLikeEpisode(string file, string? root) =>
        TitleGuess.NamesEpisode(EpisodeParser.FromPath(file, root), Path.GetFileNameWithoutExtension(file));

    // "03" → 3
    private static int? BareNumber(string name) =>
        name.Length is > 0 and <= 3 && name.All(char.IsAsciiDigit) && int.Parse(name, System.Globalization.CultureInfo.InvariantCulture) is > 0 and var n ? n : null;

    // season of the nearest season folder below and including the analysed folder ("Séria 2\a.mkv"), or null
    private static int? SeasonFolderBelow(string root, string file) => EpisodeParser.SeasonOfPath(file, root);

    /// <summary>The title a file is stored under; null while it is not decided.</summary>
    public TitleInfo? TitleFor(string path) => KindFor(path) == MediaKind.Series ? SeriesFor(path) : Movies.GetValueOrDefault(path);

    /// <summary>A suggested title waiting for confirmation, when there is no decided one.</summary>
    public TitleInfo? SuggestedTitleFor(string path) =>
        TitleFor(path) is not null ? null
        : KindFor(path) == MediaKind.Series ? SeriesSuggestionOfFile.GetValueOrDefault(path) ?? (Mixed ? null : SeriesSuggestion)
        : MovieSuggestions.GetValueOrDefault(path);

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

        return EpisodeParser.FromPath(path, Root) is { } found ? (found.Number, found.NeedsConfirmation) : (null, false);
    }

    /// <summary>Why the episode of a file is only suggested (Slovak), or null.</summary>
    public string? EpisodeDoubt(string path)
    {
        if (Episodes.ContainsKey(path))
        {
            return null;
        }

        return EpisodeParser.FromPath(path, Root) switch
        {
            { NameSeason: { } named } found =>
                $"OTÁZKA: súbor je v priečinku špeciálov (Season 00), ale meno uvádza sezónu {named:00} – patrí do Season 00 ({found.Number.Tag}) " +
                $"alebo do Season {named:00} ({(found.Number with { Season = named }).Tag})? Zadajte číslo epizódy",
            { FolderSeason: { } folder, Number.Season: 0 } found =>
                $"OTÁZKA: meno uvádza špeciál ({found.Number.Tag}), ale súbor je v priečinku Season {folder:00} – patrí do Season 00 " +
                $"alebo do Season {folder:00} ({(found.Number with { Season = folder }).Tag})? Zadajte číslo epizódy",
            { Number: { Episode: 0, Season: > 0 } number } =>
                $"OTÁZKA: epizóda 00 ({number.Tag}) – je to špeciál (Season 00) alebo časť sezóny {number.Season:00}? Zadajte číslo epizódy",
            { FolderSeason: { } folder } found => $"sezóna v mene ({found.Number.Season}) a v priečinku ({folder}) sa líšia",
            { SeasonAssumed: true } => "sezóna nie je v mene ani v priečinku – predpokladá sa 1",
            { RangeRejected: true } => "rozsah epizód v mene sa nedá zapísať (iná sezóna alebo opačné poradie) – berie sa prvá epizóda",
            null when SeasonQuestions.Contains(path) && EpisodeSuggestions.TryGetValue(path, out var special) =>
                $"OTÁZKA: súbor bez čísla je pod priečinkom špeciálov – patrí do Season 00 (návrh {special.Tag}) alebo do bežnej sezóny? Zadajte číslo epizódy",
            null when EpisodeSuggestions.ContainsKey(path) => "číslo nie je v mene – návrh podľa poradia",
            _ => null,
        };
    }

    /// <summary>
    /// Accepts the proposed kind and all suggested titles and episode numbers – except the questions Season 00 or a
    /// regular season, which the user answers for each file (or with <see cref="AnswerSpecials"/>).
    /// </summary>
    public void ConfirmSuggestions()
    {
        KindConfirmed = true;
        if (!SeriesUnverified)
        {
            Series ??= SeriesSuggestion;
        }

        foreach (var (path, series) in SeriesSuggestionOfFile)
        {
            SeriesOfFile.TryAdd(path, series);
        }

        foreach (var (path, title) in MovieSuggestions)
        {
            // a title HISTRA did not confirm is confirmed for its file alone (ConfirmTitle)
            if (!UnverifiedTitles.Contains(path))
            {
                Movies.TryAdd(path, title);
            }
        }

        foreach (var (path, number) in EpisodeSuggestions)
        {
            if (!SeasonQuestions.Contains(path))
            {
                Episodes.TryAdd(path, number);
            }
        }
    }

    /// <summary>
    /// Films whose suggested title TMDB found but HISTRA did not confirm (it found nothing, or another title): they wait
    /// for the user's confirmation of each file, not for "accept suggestions".
    /// </summary>
    public HashSet<string> UnverifiedTitles { get; } = new(Paths.Comparer);

    /// <summary>The suggested series HISTRA did not confirm: it waits for <see cref="ConfirmSeries"/>, not for "accept suggestions".</summary>
    public bool SeriesUnverified { get; set; }

    /// <summary>Confirms the suggested series (also one HISTRA did not confirm).</summary>
    public void ConfirmSeries()
    {
        Series ??= SeriesSuggestion;
        SeriesUnverified = false;
    }

    /// <summary>Confirms the suggested title of one film (also one HISTRA did not confirm).</summary>
    public void ConfirmTitle(string path)
    {
        if (MovieSuggestions.TryGetValue(path, out var title))
        {
            Movies[path] = title;
        }

        UnverifiedTitles.Remove(path);
    }

    /// <summary>Unanswered questions Season 00 or a regular season.</summary>
    public IEnumerable<string> OpenSeasonQuestions => SeasonQuestions.Where(p => !Episodes.ContainsKey(p));

    /// <summary>
    /// The user's answer "these are specials": every open question whose suggestion is in Season 00 (files in a specials
    /// folder, names with S00) goes into Season 00; episode 00 of a regular season stays a question. Returns how many.
    /// </summary>
    public int AnswerSpecials()
    {
        var answered = 0;
        foreach (var path in OpenSeasonQuestions.ToList())
        {
            if (EpisodeSuggestions.TryGetValue(path, out var number) && number.Season == 0)
            {
                Episodes[path] = number;
                answered++;
            }
        }

        return answered;
    }

    private static TitleInfo Title(MediaKind kind, NameGuess guess) => new(kind, guess.Name, guess.Year, guess.TmdbId, guess.TmdbId is not null);
}
