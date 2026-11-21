using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Localization;
using Universum.App.Templates;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Speech;
using Universum.Core.Templates;
using Universum.Core.Text;
using Universum.Platform.Windows;

namespace Universum.App.Pages;

// BatchPageViewModel: the kind of the folder, the titles of films and series, TMDB, the suggestions to confirm
public sealed partial class BatchPageViewModel
{
    public string OutputDescription => _services.Library?.Root ?? Loc.T("nie je nastavený – zvoľte ho tlačidlom Priečinok…");

    /// <summary>Naming decisions for the analysed folder; null without an output folder.</summary>
    public LibraryJob? Library => _library;

    public bool IsMovies
    {
        get => _library is { Mixed: true } library ? SelectedFirst is not { } first || library.KindFor(first) != MediaKind.Series : _library?.Kind != MediaKind.Series;
        set => SetKind(value, MediaKind.Movie);
    }

    public bool IsSeries
    {
        get => _library is { Mixed: true } library ? SelectedFirst is { } first && library.KindFor(first) == MediaKind.Series : _library?.Kind == MediaKind.Series;
        set => SetKind(value, MediaKind.Series);
    }

    /// <summary>A folder sorted into MOVIES and TVSHOWS (owner, 29. 10. 2026): the kind comes from the folder, per group.</summary>
    public bool IsMixed => _library?.Mixed == true;

    /// <summary>The kind can be chosen (not in a sorted folder – there the user sorts the files into MOVIES or TVSHOWS).</summary>
    public bool CanChooseKind => !IsMixed;

    /// <summary>"Spresniť názvy": films, and in a sorted folder the series of TVSHOWS too.</summary>
    public bool CanFindTitles => IsMovies || IsMixed;

    /// <summary>"Obsah:" – of the selected group in a sorted folder.</summary>
    public string KindLabel => IsMixed ? Loc.T("Obsah skupiny:") : Loc.T("Obsah:");

    // the files of the selected group
    private IReadOnlyList<string> SelectedFiles => SelectedGroup?.Group.Files.Select(f => f.Path).ToList() ?? [];

    private string? SelectedFirst => SelectedGroup?.Group.Files.FirstOrDefault()?.Path;

    // the series suggested for the selected group (sorted folder) or for the folder
    private TitleInfo? SuggestedSeries => _library is { Mixed: true } library
        ? SelectedFirst is { } first ? library.SeriesSuggestionOfFile.GetValueOrDefault(first) : null
        : _library?.SeriesSuggestion;

    /// <summary>
    /// Title of the series as in the library ("Nine Puzzles (2025) [tmdbid-227191]"); for films each file has its own. In
    /// a sorted folder the series of the selected group.
    /// </summary>
    public string SeriesTitle
    {
        get => _library is { Mixed: true } library
            ? SelectedFirst is { } first ? library.SeriesOfFile.GetValueOrDefault(first)?.FolderName ?? "" : ""
            : _library?.Series?.FolderName ?? "";
        set
        {
            if (_library is null)
            {
                return;
            }

            if (_library.Mixed)
            {
                // a series' address or ID: its title from TMDB; a film's address does not belong among the series
                if (TitleGuess.ParseTmdb(value) is { } address)
                {
                    if (address.Kind == MediaKind.Movie)
                    {
                        RunState = Loc.T("Toto je adresa filmu – v priečinku TVSHOWS má byť seriál; film presuňte do MOVIES.");
                        OnPropertyChanged();
                        return;
                    }

                    UseTmdbReference(address, value);
                    return;
                }

                _typedTmdb = null;
                var typed = TitleGuess.Typed(MediaKind.Series, value);
                foreach (var path in SeriesFiles)
                {
                    if (typed is null)
                    {
                        _library.SeriesOfFile.Remove(path);
                    }
                    else
                    {
                        _library.SeriesOfFile[path] = typed;
                    }
                }

                Replan();
                return;
            }

            // a TMDB address tells the kind itself: themoviedb.org/tv/… is a series, themoviedb.org/movie/… a film
            if (TitleGuess.ParseTmdb(value) is { Kind: not null } reference)
            {
                UseTmdbReference(reference, value);
                return;
            }

            _typedTmdb = null;
            _library.Series = TitleGuess.Typed(MediaKind.Series, value);
            Replan();
        }
    }

    /// <summary>Looking up the film of the selected files on TMDB (the row Film: at the bottom).</summary>
    public TmdbPicker FilmTmdb { get; }

    // the files the row Film: names: the selected group in a sorted folder (one film and its versions), otherwise the
    // files selected in the list, or the group's only file
    private IReadOnlyList<string> FilmFiles
    {
        get
        {
            if (_library is null || SelectedGroup is null)
            {
                return [];
            }

            var group = SelectedFiles;
            var films = group.Where(p => _library.KindFor(p) == MediaKind.Movie).ToList();
            if (_library.Mixed || films.Count == 1)
            {
                return films;
            }

            var chosen = _selectedFiles.Select(f => f.Item.File.Path).Where(films.Contains).ToList();
            return chosen;
        }
    }

    /// <summary>The row Film: has files to name (otherwise: select the files in the list).</summary>
    public bool CanNameFilm => FilmFiles.Count > 0;

    public string FilmHint => CanNameFilm
        ? FilmFiles.Count > 1 ? Loc.F("pre {0} súbory (verzie jedného filmu)", FilmFiles.Count) : ""
        : Loc.T("skupina má viac filmov – vyberte súbory v zozname (Ctrl / Shift) alebo zadajte názov pri súbore");

    /// <summary>
    /// The film of the selected files (owner, 30. 10. 2026: as the row Seriál) – "Názov (rok)", for a title in another
    /// script "Anglický názov (rok) [tmdbid-ID]", or a TMDB ID or address.
    /// </summary>
    public string FilmTitle
    {
        get
        {
            var files = FilmFiles;
            var titles = files.Select(p => _library?.Movies.GetValueOrDefault(p)?.FolderName).Distinct().ToList();
            return files.Count > 0 && titles.Count == 1 ? titles[0] ?? "" : "";
        }
        set
        {
            var files = FilmFiles;
            if (_library is null || files.Count == 0)
            {
                return;
            }

            if (TitleGuess.ParseTmdb(value) is { } reference)
            {
                BackgroundWork.Run(NameFilmsFromTmdbAsync(files, reference));
                return;
            }

            var typed = TitleGuess.Typed(MediaKind.Movie, value);
            foreach (var path in files)
            {
                if (typed is null)
                {
                    _library.Movies.Remove(path);
                }
                else
                {
                    _library.Movies[path] = typed;
                    _library.ChosenTitles.Add(path);
                    _library.UnverifiedTitles.Remove(path);
                    _unverifiedNotes.Remove(path);
                }
            }

            Replan();
            OnPropertyChanged(nameof(FilmSuggestion));
        }
    }

    /// <summary>The suggested film of the selected files, waiting for confirmation.</summary>
    public string FilmSuggestion => _library is { } library && FilmTitle.Length == 0 &&
                                    FilmFiles.Select(p => library.SuggestedTitleFor(p)?.FolderName).FirstOrDefault(t => t is not null) is { } suggestion
        ? Loc.T("návrh: ") + suggestion
        : "";

    /// <summary>A TMDB ID or address for the selected files' film (the field clears after it is applied).</summary>
    public string FilmTmdbId
    {
        get => "";
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && FilmFiles is { Count: > 0 } files)
            {
                if (TitleGuess.ParseTmdb(value) is { } reference)
                {
                    BackgroundWork.Run(NameFilmsFromTmdbAsync(files, reference));
                }
                else
                {
                    RunState = Loc.T("TMDB ID je číslo filmu (napr. 12345) alebo adresa z themoviedb.org – minisériál vložte ako adresu …/tv/….");
                }
            }

            OnPropertyChanged();
        }
    }

    /// <summary>The choice from the Zásobník TMDB (in the rows of the film and of the series).</summary>
    public TmdbTrayChoice Tray { get; }

    /// <summary>A TMDB ID or address of the series (owner, 30. 10. 2026: the same row for films and series).</summary>
    public string SeriesTmdbId
    {
        get => "";
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (TitleGuess.ParseTmdb(value) is { } reference)
                {
                    BackgroundWork.Run(NameSeriesFromTmdbAsync(reference));
                }
                else
                {
                    RunState = Loc.T("TMDB ID je číslo seriálu (napr. 12345) alebo adresa z themoviedb.org (…/tv/…).");
                }
            }

            OnPropertyChanged();
        }
    }

    // the series by its TMDB ID (a bare number is a series here)
    private async Task NameSeriesFromTmdbAsync(TmdbReference reference)
    {
        if ((_services.Titles?.Tmdb ?? _services.Tmdb) is not { } tmdb)
        {
            RunState = Loc.T("TMDB ID: na načítanie názvu treba kľúč TMDB v Nastaveniach – alebo napíšte Názov (rok) [tmdbid-ID].");
            return;
        }

        if (reference.Kind == MediaKind.Movie)
        {
            RunState = Loc.T("Toto je adresa filmu (/movie/) – seriál má adresu …/tv/….");
            return;
        }

        try
        {
            if (await tmdb.GetAsync(MediaKind.Series, reference.Id) is { } show && _library is not null)
            {
                TmdbPicker.TrySaveCache(tmdb);
                ChooseTmdbSeries(show);
            }
            else
            {
                RunState = Loc.F("TMDB nepozná seriál s ID {0} – skontrolujte číslo alebo vložte adresu z themoviedb.org.", reference.Id);
            }
        }
        catch (TitleServiceException ex)
        {
            RunState = ex.Message;
        }
    }

    // one film for several files (its versions): read once, applied to all
    private async Task NameFilmsFromTmdbAsync(IReadOnlyList<string> paths, TmdbReference reference)
    {
        if ((_services.Titles?.Tmdb ?? _services.Tmdb) is not { } tmdb)
        {
            RunState = Loc.T("TMDB ID: na načítanie názvu treba kľúč TMDB v Nastaveniach – alebo napíšte Názov (rok).");
            return;
        }

        try
        {
            if (await tmdb.GetAsync(reference.Kind ?? MediaKind.Movie, reference.Id) is { } film && _library is not null)
            {
                TmdbPicker.TrySaveCache(tmdb);
                await ApplyFilmAsync(paths, film);
            }
            else
            {
                RunState = reference.Kind == MediaKind.Series
                    ? Loc.F("TMDB nepozná seriál s ID {0} – skontrolujte číslo alebo vložte adresu z themoviedb.org.", reference.Id)
                    : Loc.F("TMDB nepozná film s ID {0} – skontrolujte číslo alebo vložte adresu z themoviedb.org.", reference.Id);
            }
        }
        catch (TitleServiceException ex)
        {
            RunState = ex.Message;
        }
    }

    public string SeriesSuggestion => _library is { Mixed: true } mixed
        ? SeriesTitle.Length == 0 && SuggestedSeries is { } groupSuggestion
            ? Loc.T("návrh: ") + groupSuggestion.FolderName + (groupSuggestion.Problem is { } problem ? Loc.F(" ⚠ {0} – nájdite ho cez TMDB alebo Spresniť názvy", problem) : "")
            : ""
        : _library is { Series: null, SeriesSuggestion: { } suggestion } library
            ? Loc.T("návrh: ") + suggestion.FolderName + (library.SeriesUnverified ? " ⚠ " + _seriesNote : "")
            : "";

    /// <summary>The suggested series waits for its own confirmation (HISTRA did not confirm it; in a sorted folder any suggestion).</summary>
    public bool SeriesNeedsConfirmation => _library is { Mixed: true }
        ? SeriesTitle.Length == 0 && SuggestedSeries is { Problem: null }
        : _library is { Series: null, SeriesUnverified: true, SeriesSuggestion: not null };

    /// <summary>
    /// "Prijať návrhy" (owner, 21. 11. 2026 – one button instead of Prijať návrhy názvov and Prijať všetko v skupine): for
    /// all groups at once the kind, the titles of films and series, the episode numbers and the languages of the tracks as
    /// suggested. Left to the user: the titles HISTRA did not confirm (confirmed at the file), the suggestions with a
    /// warning, the questions Season 00 or a regular season – the status line tells what is left.
    /// </summary>
    private void AcceptSuggestions()
    {
        if (_library is null || _analysis is null)
        {
            return;
        }

        _library.ConfirmSuggestions();
        var languages = _analysis.Groups.Sum(BatchPlanner.AcceptLanguageSuggestions);
        OnPropertyChanged(nameof(SeriesTitle));
        OnPropertyChanged(nameof(SeriesSuggestion));
        Replan();

        var left = new List<string>();
        var unverified = _library.UnverifiedTitles.Count(p => _library.TitleFor(p) is null);
        if (unverified > 0)
        {
            left.Add(Loc.F("názvy, ktoré HISTRA nepotvrdila: {0}", unverified));
        }

        var doubtful = _analysis.Groups.Sum(g => SlotKey.Of(g.Representative).Count(x =>
            !g.Decisions.Languages.ContainsKey(x.Key) && BatchPlanner.Evidence(x.Track, x.Key, g) is { NeedsOwnConfirmation: true }));
        if (doubtful > 0)
        {
            left.Add(Loc.F("jazyky s upozornením: {0}", doubtful));
        }

        if (_library.OpenSeasonQuestions.Any())
        {
            left.Add(Loc.T("otázky Season 00 / bežná sezóna"));
        }

        var waiting = _items.Count(i => i.Reasons.Count > 0);
        RunState = Loc.F("Návrhy prijaté (jazykov: {0}).", languages) +
                   (left.Count > 0 ? " " + Loc.F("Na vás ostali: {0}.", string.Join(", ", left)) : "") +
                   (waiting > 0 ? " " + Loc.F("Súborov na posúdenie: {0}.", waiting) : "");
    }

    /// <summary>Confirms the suggested series HISTRA did not confirm.</summary>
    public RelayCommand ConfirmSeriesCommand { get; }

    /// <summary>A film named by its TMDB ID or address typed in the field TMDB ID at the file (also used by the self-test).</summary>
    public void UseTmdbId(string path, string text)
    {
        if (_library is null || _library.KindFor(path) != MediaKind.Movie)
        {
            return;
        }

        if (TitleGuess.ParseTmdb(text) is { } reference)
        {
            BackgroundWork.Run(NameFilmFromTmdbAsync(path, reference));
        }
        else
        {
            RunState = Loc.T("TMDB ID je číslo filmu (napr. 12345) alebo adresa z themoviedb.org – minisériál vložte ako adresu …/tv/….");
        }
    }

    /// <summary>Sets the naming of one file: a film title ("Creep (2004)") or an episode ("S01E04"); also used by the self-test.</summary>
    public void SetNaming(string path, string text)
    {
        if (_library is null)
        {
            return;
        }

        // a film's TMDB address or ID: its title from TMDB (the address says film or series)
        if (_library.KindFor(path) == MediaKind.Movie && TitleGuess.ParseTmdb(text) is { } reference)
        {
            BackgroundWork.Run(NameFilmFromTmdbAsync(path, reference));
            return;
        }

        if (_library.KindFor(path) == MediaKind.Series)
        {
            if (EpisodeNumber.TryParse(text) is { } number)
            {
                _library.Episodes[path] = number;
            }
            else
            {
                _library.Episodes.Remove(path);
            }
        }
        else if (TitleGuess.Typed(MediaKind.Movie, text) is { } title)
        {
            _library.Movies[path] = title;
        }
        else
        {
            _library.Movies.Remove(path);
        }

        Replan();
    }

    /// <summary>
    /// A series named by its folder with a TMDB ID ("… (2025) [tmdbid-227191]"): its seasons are read from TMDB so every
    /// episode is checked (specification 13.5). Without TMDB nothing is checked.
    /// </summary>
    private async Task LoadSeriesSeasonsAsync()
    {
        if (_library is not { Kind: MediaKind.Series, Series: { TmdbId: { } id } series } library || _services.Tmdb is not { } tmdb ||
            library.SeriesOnTmdb is { } known && known.Title.FolderName == series.FolderName)
        {
            return;
        }

        try
        {
            if (await tmdb.GetAsync(MediaKind.Series, id) is { Seasons.Count: > 0 } details && ReferenceEquals(_library, library) && library.Series == series)
            {
                library.SeriesOnTmdb = (series, details.Seasons);
                TmdbPicker.TrySaveCache(tmdb);
                Replan();
            }
        }
        catch (TmdbException ex)
        {
            RunState = Loc.T("TMDB: epizódy sa nedajú overiť – ") + ex.Message;
        }
    }

    private void AnswerSpecials()
    {
        if (_library is not null)
        {
            RunState = Loc.F("Do Season 00: {0} súborov.", _library.AnswerSpecials());
            Replan();
        }
    }

    private void ConfirmKind()
    {
        if (_library is not null)
        {
            _library.Kind = _library.Kind;
            OnLibraryChanged();
        }
    }

    private void SetKind(bool selected, MediaKind kind)
    {
        // in a sorted folder the folder decides (the user moves the file)
        if (!selected || _library is null || _library.Mixed || _library.Kind == kind)
        {
            return;
        }

        _library.Kind = kind;
        OnLibraryChanged();
    }

    private void UseTmdbReference(TmdbReference reference, string text)
    {
        if (_library is null)
        {
            return;
        }

        if (!_library.Mixed && reference.Kind is { } kind && (kind != _library.Kind || !_library.KindConfirmed))
        {
            _library.Kind = kind;
            OnLibraryChanged();
        }

        if (_services.Tmdb is null)
        {
            RunState = reference.Kind == MediaKind.Movie
                ? Loc.T("Adresa TMDB: na načítanie názvu treba kľúč TMDB v Nastaveniach – alebo napíšte Názov (rok) pri súboroch.")
                : Loc.F("Adresa TMDB: na načítanie názvu treba kľúč TMDB v Nastaveniach – alebo napíšte Názov (rok) [tmdbid-{0}].", reference.Id);
            return;
        }

        _typedTmdb = text;
        BackgroundWork.Run(SearchTypedAsync());
    }

    private async Task SearchTypedAsync()
    {
        try
        {
            await Tmdb.SearchAsync();
        }
        finally
        {
            _typedTmdb = null;
        }
    }

    private Task NameFilmFromTmdbAsync(string path, TmdbReference reference) => NameFilmsFromTmdbAsync([path], reference);

    // a series of TMDB as a film of the library: its name and year, no ID (Jellyfin would take a series' ID for a film's)
    private static TitleInfo? AsFilm(TmdbTitle show) =>
        show.ToTitleInfo() is { } info ? new TitleInfo(MediaKind.Movie, info.Name, info.Year) : null;

    // owner, 29. 10. 2026: the user is told it is a miniseries, the name has no season and episode, it cannot be a series
    private static string MiniseriesNote(TmdbTitle show, TitleInfo asFilm) =>
        Loc.F("POZOR: {0} je na TMDB minisériál alebo TV seriál (TMDB {1}), nie film. V mene súboru nie je číslo série a epizódy, " +
              "preto sa nedá spracovať ako seriál – uloží sa medzi filmy ako „{2}“ bez TMDB ID. Ak súhlasíte, potvrďte ho pri súbore; " +
              "inak zvoľte druh seriál a doplňte čísla epizód.", show.Display, show.Id, asFilm.FolderName);

    /// <summary>A film chosen by the user for a file (its TMDB address, or one of the titles found for it).</summary>
    public async Task ChooseCandidateAsync(string path, TmdbTitle film)
    {
        try
        {
            await ApplyFilmAsync(path, film);
        }
        catch (TitleServiceException ex)
        {
            RunState = ex.Message;
        }
    }

    /// <summary>The titles TMDB (or HISTRA) found for the file at the last "Spresniť názvy", best first; empty when none.</summary>
    public IReadOnlyList<TmdbTitle> CandidatesFor(string path) => _candidates.GetValueOrDefault(path) ?? [];

    // the film becomes the file's title – checked in HISTRA as everywhere: a title it does not confirm waits at the file
    private Task ApplyFilmAsync(string path, TmdbTitle film) => ApplyFilmAsync([path], film);

    // the film becomes the title of the files (one film and its versions) – checked in HISTRA once
    private async Task ApplyFilmAsync(IReadOnlyList<string> paths, TmdbTitle film)
    {
        if (_library is null || paths.Count == 0)
        {
            return;
        }

        // a TV film or miniseries (a series on TMDB) chosen for a film: among the films without an ID, after confirmation
        if (film.Kind == MediaKind.Series)
        {
            if (AsFilm(film) is { } asFilm)
            {
                foreach (var path in paths)
                {
                    _library.ChosenTitles.Add(path);
                    _library.Movies.Remove(path);
                    _library.MovieSuggestions[path] = asFilm;
                    _library.UnverifiedTitles.Add(path);
                    _unverifiedNotes[path] = MiniseriesNote(film, asFilm);
                }

                RunState = MiniseriesNote(film, asFilm);
            }

            Replan();
            return;
        }

        var (info, note) = LibraryTitle(film, paths[0]);
        if (info is not null)
        {
            var check = _services.Titles is { UsesHistra: true } titles ? await titles.CheckAsync(film) : null;
            var unverified = check is not null && check.Check(film).NeedsConfirmation();
            foreach (var path in paths)
            {
                _library.ChosenTitles.Add(path);
                if (unverified)
                {
                    _library.Movies.Remove(path);
                    _library.MovieSuggestions[path] = info;
                    _library.UnverifiedTitles.Add(path);
                    _unverifiedNotes[path] = TmdbPicker.Describe(check!, film);
                }
                else
                {
                    _library.Movies[path] = info;
                    _library.UnverifiedTitles.Remove(path);
                    _unverifiedNotes.Remove(path);
                }
            }

            if (unverified)
            {
                note = (note.Length > 0 ? note + " " : "") + "⚠ " + TmdbPicker.Describe(check!, film);
            }
        }

        RunState = note.Length > 0 ? note : $"{(paths.Count == 1 ? Path.GetFileName(paths[0]) : Loc.F("{0} súbory", paths.Count))}: {film.Display}";
        Replan();
        OnPropertyChanged(nameof(FilmTitle));
        OnPropertyChanged(nameof(FilmSuggestion));
    }

    /// <summary>
    /// The library title of a TMDB title: the original in Latin script, otherwise the English title with the TMDB ID. When
    /// TMDB has no English title (owner, 29. 10. 2026: a film in another script was not named by its ID), another Latin
    /// name of the title or the name from the file is used with the ID – the ID says which title it is.
    /// </summary>
    internal static (TitleInfo? Title, string Note) LibraryTitle(TmdbTitle title, string? path)
    {
        var (text, note) = TmdbPicker.TitleText(title);
        if (TitleGuess.Typed(title.Kind, text) is { } info)
        {
            return (info, note);
        }

        if (title.ToLibraryTitle(path is null ? null : TitleGuess.ForFile(path).Title?.Name) is not { } fallback ||
            TitleGuess.Typed(title.Kind, fallback.FolderName) is not { } named)
        {
            return (null, Loc.F("TMDB nemá názov latinkou – napíšte ho pred rok, napr. Anglický názov {0}.", text));
        }

        return (named, Loc.F("TMDB nemá anglický názov – použitý je „{0}“ s TMDB ID {1}; ak treba, opravte názov (ID ostane).", named.Name, title.Id));
    }

    private void ChooseTmdbSeries(TmdbTitle title)
    {
        if (_library is null)
        {
            return;
        }

        // a sorted folder: the series of the selected group
        if (_library.Mixed)
        {
            var (groupSeries, seriesNote) = title.Kind == MediaKind.Series ? LibraryTitle(title, SelectedFirst) : (null, "");
            if (groupSeries is not null)
            {
                var groups = SeriesGroupsNamed;
                foreach (var path in SeriesFiles)
                {
                    _library.SeriesOfFile[path] = groupSeries;
                }

                RunState = seriesNote.Length > 0 ? seriesNote
                    : groups > 1 ? Loc.F("{0} označených skupín: {1}", groups, title.Display)
                    : Loc.F("Skupina {0}: {1}", SelectedGroup?.Label, title.Display);
            }
            else
            {
                RunState = Loc.T("Toto je film – v priečinku TVSHOWS má byť seriál; film presuňte do MOVIES.");
            }

            OnPropertyChanged(nameof(SeriesTitle));
            OnPropertyChanged(nameof(SeriesSuggestion));
            Replan();
            return;
        }

        var (text, note) = TmdbPicker.TitleText(title);
        if (title.Kind == MediaKind.Movie)
        {
            // a film's address in the series field: the folder holds this film (all files are its versions); a title HISTRA
            // did not confirm is only suggested and waits at each file for confirmation
            _library.Kind = MediaKind.Movie;
            if (TitleGuess.Typed(MediaKind.Movie, text) is { } film && _analysis is not null)
            {
                foreach (var file in _analysis.Groups.SelectMany(g => g.Files))
                {
                    _library.ChosenTitles.Add(file.Path);
                    if (Tmdb.NeedsConfirmation)
                    {
                        _library.Movies.Remove(file.Path);
                        _library.MovieSuggestions[file.Path] = film;
                        _library.UnverifiedTitles.Add(file.Path);
                        _unverifiedNotes[file.Path] = Tmdb.Warning;
                    }
                    else
                    {
                        _library.Movies[file.Path] = film;
                    }
                }
            }

            RunState = (note.Length > 0 ? note + " " : "") + (Tmdb.NeedsConfirmation ? "⚠ " + Tmdb.Warning + " " : "") +
                       Loc.T("Adresa TMDB je film (/movie/): druh je filmy a všetky súbory dostali tento názov (verzie jedného filmu) – pri súboroch ho môžete zmeniť.");
            OnLibraryChanged();
            return;
        }

        _library.Kind = MediaKind.Series;
        var (chosen, seriesNote2) = LibraryTitle(title, SelectedFirst);
        if (seriesNote2.Length > 0)
        {
            note = seriesNote2;
        }

        if (Tmdb.NeedsConfirmation && chosen is not null)
        {
            // HISTRA did not confirm TMDB's series: it is only suggested and waits for "Potvrdiť seriál"
            _library.Series = null;
            _library.SeriesSuggestion = chosen;
            _library.SeriesUnverified = true;
            _seriesNote = Tmdb.Warning;
            _library.SeriesOnTmdb = title.Seasons.Count > 0 ? (chosen, title.Seasons) : null;
            RunState = (note.Length > 0 ? note + " " : "") + "⚠ " + Tmdb.Warning + Loc.T(" Ak je správny, dajte Potvrdiť seriál.");
            OnLibraryChanged();
            return;
        }

        _library.SeriesUnverified = false;
        _library.Series = chosen;
        // seasons and episode counts: every episode is checked against them (specification 13.5)
        _library.SeriesOnTmdb = _library.Series is { } series && title.Seasons.Count > 0 ? (series, title.Seasons) : null;
        RunState = note;
        OnLibraryChanged();
    }

    /// <summary>
    /// Refines the suggested film titles on TMDB – without a TMDB key, or when TMDB finds nothing, in the HISTRA catalog –
    /// one request at a time (TMDB at most 4 per second, HISTRA 2, see RequestPacer); the same name and year is asked
    /// only once (cache). Stop cancels; titles found so far stay.
    /// </summary>
    private async Task FindMoviesAsync(bool automatic)
    {
        if (_library is null || _analysis is null || _services.Titles is not { } titles)
        {
            return;
        }

        var service = titles.Tmdb is null ? "HISTRA" : titles.UsesHistra ? "TMDB + HISTRA" : "TMDB";

        // asked by the title read from the name, never by an earlier suggestion of the service (owner's review 28. 10.:
        // a second run would otherwise confirm a title the name never matched)
        var pending = _analysis.Groups.SelectMany(g => g.Files)
            .Where(f => _library.KindFor(f.Path) == MediaKind.Movie && !_library.Unsorted.Contains(f.Path) &&
                        !_library.Movies.ContainsKey(f.Path) && _library.MovieSuggestions.ContainsKey(f.Path) && !_library.ChosenTitles.Contains(f.Path))
            .Select(f => (f.Path, Guess: _library.NameGuesses.GetValueOrDefault(f.Path) ?? _library.MovieSuggestions[f.Path]))
            .ToList();
        var questions = pending.Select(p => (p.Guess.Name.ToLowerInvariant(), p.Guess.Year)).Distinct().Count();
        if (automatic && pending.Count == 0 && !(_library.Units?.Any(u => u is { Kind: MediaKind.Series, Sorted: true, Name: not null } && !u.Files.All(_library.SeriesOfFile.ContainsKey)) ?? false))
        {
            // after the analysis: nothing to look up, the summary of the analysis stays
            return;
        }

        Busy = true;
        _tmdbCancellation = new CancellationTokenSource();
        StopCommand.NotifyCanExecuteChanged();
        void OnWaiting(TimeSpan delay) => RunState = Loc.F("{0} žiada spomaliť – čakám {1:0} s a pokračujem…", service, Math.Ceiling(delay.TotalSeconds));
        titles.Waiting += OnWaiting;
        int asked = 0, found = 0, unverified = 0, ambiguous = 0, nowhere = 0;
        var problem = "";
        try
        {
            foreach (var (path, guess) in pending)
            {
                asked++;
                RunState = Loc.F("{0} {1} / {2} ({3} rôznych názvov)…", service, asked, pending.Count, questions);
                // the name of a file may be wrong (owner, 28. 10.): searched also without the year or shortened, matched
                // also by a similar name; whatever is not the name and year as read waits for confirmation at the file
                var results = await titles.SearchTolerantAsync(MediaKind.Movie, guess.Name, guess.Year, _tmdbCancellation.Token);
                _candidates[path] = results.Titles.Take(10).ToList();

                // owner, 29. 10. 2026: no film of that name – a TV film or miniseries is looked for too (TMDB keeps them as
                // series); it goes among the films without an ID, after the user confirms it at the file
                if (results.Titles.Count == 0 && titles.Tmdb is not null &&
                    await titles.SearchAsync(MediaKind.Series, guess.Name, guess.Year, _tmdbCancellation.Token) is { Titles.Count: > 0 } tv &&
                    (tv.Best(guess.Name, guess.Year) ?? tv.OnlyOne(guess.Year)) is { } show && AsFilm(show) is { } asFilm)
                {
                    _candidates[path] = tv.Titles.Take(10).ToList();
                    _library.MovieSuggestions[path] = asFilm;
                    _library.UnverifiedTitles.Add(path);
                    _unverifiedNotes[path] = MiniseriesNote(show, asFilm);
                    found++;
                    unverified++;
                    continue;
                }
                // the title and what speaks against it are decided in the core (TitleResults.Match, tested on Linux)
                var (match, doubts) = results.Match(guess.Name, guess.Year);
                if (match?.ToTitleInfo() is { } info)
                {
                    _library.MovieSuggestions[path] = info;
                    found++;

                    // a loosened match, or HISTRA found nothing, another title, or could not be asked: the file waits for
                    // its own confirmation (or another title chosen from the list at it)
                    if (doubts.Count > 0 || results.Check(match).NeedsConfirmation())
                    {
                        if (results.Check(match) == TitleCheck.Unavailable && problem.Length == 0)
                        {
                            problem = results.Note;
                        }

                        _library.UnverifiedTitles.Add(path);
                        _unverifiedNotes[path] = (doubts.Count > 0
                                                     ? Loc.F("{0} navrhuje {1}: {2} – skontrolujte a potvrďte, alebo vyberte iný titul. ", results.Source, match.Display, string.Join("; ", doubts))
                                                     : "") + TmdbPicker.Describe(results, match);
                        unverified++;
                    }
                    else
                    {
                        _library.UnverifiedTitles.Remove(path);
                        _unverifiedNotes.Remove(path);
                    }
                }
                else if (results.Titles.Count > 0)
                {
                    // several titles and none named as the file: the suggestion from the name waits for a choice at the file
                    _library.UnverifiedTitles.Add(path);
                    var listed = string.Join(", ", results.Titles.Take(3).Select(t => t.Display)) + (results.Titles.Count > 3 ? ", …" : "");
                    _unverifiedNotes[path] = (results.Titles.Count == 1
                                                 ? Loc.F("{0} našlo len {1} – iný rok, než má súbor", results.Source, listed)
                                                 : Loc.F("{0} našlo viac titulov ({1}) a žiadny sa nevolá ako súbor", results.Source, listed)) +
                                             Loc.T(" – vyberte správny zo zoznamu pri súbore, napíšte Názov (rok), vložte TMDB ID (číslo alebo adresu z themoviedb.org) alebo potvrďte návrh z mena súboru.");
                    ambiguous++;
                }
                else if (results.FoundNowhere)
                {
                    // found nowhere: the suggestion from the file's name stays and waits at the file for confirmation
                    _library.UnverifiedTitles.Add(path);
                    _unverifiedNotes[path] = (results.TmdbProblem is { } refused ? refused + " " : "") +
                                             (titles.Tmdb is null ? Loc.T("Katalóg HISTRA titul nenašiel") : Loc.T("TMDB ani HISTRA titul nenašli")) +
                                             Loc.T(" – návrh je z mena súboru. Ak poznáte TMDB ID, vložte ho do poľa s názvom pri súbore (číslo alebo adresu z themoviedb.org).");
                    nowhere++;
                }
            }

            // a sorted folder (owner, 29. 10. 2026): each series of TVSHOWS by its folder's name, as a series on TMDB
            var seriesFound = 0;
            var seriesAsked = 0;
            foreach (var unit in _library.Units?.Where(u => u is { Kind: MediaKind.Series, Sorted: true, Name: not null } &&
                                                            !u.Files.All(_library.SeriesOfFile.ContainsKey)) ?? [])
            {
                seriesAsked++;
                RunState = Loc.F("{0}: seriál {1}…", service, unit.Name!.Name);
                var results = await titles.SearchTolerantAsync(MediaKind.Series, unit.Name.Name, unit.Name.Year, _tmdbCancellation.Token);
                if ((results.Best(unit.Name.Name, results.Loosened is null ? unit.Name.Year : null) ?? results.OnlyOne(unit.Name.Year)) is { } show &&
                    TitleGuess.Typed(MediaKind.Series, TmdbPicker.TitleText(show).Text) is { } info)
                {
                    foreach (var file in unit.Files)
                    {
                        _library.SeriesSuggestionOfFile[file] = info;
                    }

                    seriesFound++;
                }
            }

            RunState = (seriesAsked > 0 ? Loc.F("Seriály: nájdené {0} z {1} – návrh potvrďte pri skupine (Potvrdiť seriál) alebo dajte Prijať návrhy. ", seriesFound, seriesAsked) : "") +
                       Loc.F("Spresnené názvy ({0}): {1} z {2} navrhnutých", service, found, pending.Count) +
                       (unverified > 0 ? Loc.F(", {0} čaká na potvrdenie (⚠ pri súbore – názov sa líšil od mena súboru alebo HISTRA titul nepotvrdila)", unverified) : "") +
                       (ambiguous > 0 ? Loc.F(", {0} majú viac možných titulov (⚠ pri súbore – vyberte správny)", ambiguous) : "") +
                       (nowhere > 0 ? Loc.F(", {0} sa nenašli nikde (ostáva návrh z mena súboru – potvrďte ho pri súbore)", nowhere) : "") +
                       Loc.T(" – skontrolujte ich a dajte Prijať návrhy.") + (problem.Length > 0 ? " " + problem : "");
        }
        catch (OperationCanceledException)
        {
            RunState = Loc.F("Zastavené – spresnené {0} z {1} doteraz prejdených názvov.", found, asked);
        }
        catch (TitleServiceException ex)
        {
            RunState = ex.Message + Loc.F(" (spresnené doteraz: {0})", found);
        }
        finally
        {
            titles.Waiting -= OnWaiting;
            TmdbPicker.TrySaveCache(titles);
            _tmdbCancellation.Dispose();
            _tmdbCancellation = null;
            Busy = false;
            Replan();
        }
    }

    /// <summary>Confirms the suggested title of one film HISTRA did not confirm (the ⚠ button at the file).</summary>
    public void ConfirmTitle(string path)
    {
        if (_library is null)
        {
            return;
        }

        _library.ConfirmTitle(path);
        _unverifiedNotes.Remove(path);
        Replan();
    }

    /// <summary>Why HISTRA did not confirm a file's suggested title; null when it did (or was not asked).</summary>
    public string? UnverifiedNote(string path) =>
        _library?.UnverifiedTitles.Contains(path) == true ? _unverifiedNotes.GetValueOrDefault(path) ?? Loc.T("HISTRA titul nepotvrdila.") : null;

    private void ConfirmSuggestions()
    {
        var own = SlotRows.Where(s => s.NeedsOwnConfirmation).Select(s => s.Slot).ToList();
        own.AddRange(FileRows.SelectMany(f => f.AudioSlots)
            .Where(s => s.Group.FileSuggestion(s.Path, s.Key) is { NeedsOwnConfirmation: true } && s.ChosenLanguage is null)
            .Select(s => $"{Path.GetFileName(s.Path)} {s.Slot}"));
        foreach (var slot in SlotRows)
        {
            slot.ConfirmSuggestion();
        }

        // each file's own suggestions (heard in that file), except the doubtful ones
        foreach (var slot in FileRows.SelectMany(f => f.AudioSlots))
        {
            slot.ConfirmSuggestion();
        }

        Replan();
        if (own.Count > 0)
        {
            RunState = Loc.F("Návrhy s upozornením ostali na vašu voľbu ({0}) – jazyk vyberte pri stope.", string.Join(", ", own));
        }
    }
}
