using Universum.Core.Library.Histra;
using Universum.Core.Library.Tmdb;
using Universum.Core.Text;

namespace Universum.Core.Library;

/// <summary>A problem of a title service (TMDB, HISTRA) the user can act on; the message is ready to show.</summary>
public class TitleServiceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How HISTRA confirmed a title (the synchronisation of TMDB and HISTRA, decided 27. 10. 2026).</summary>
public enum TitleCheck
{
    /// <summary>HISTRA is not used or could not be asked: the title stands as TMDB gives it.</summary>
    NotChecked,

    /// <summary>HISTRA found the same title (the same TMDB ID) for the name.</summary>
    Confirmed,

    /// <summary>The title comes from the HISTRA catalog itself (no TMDB key, or TMDB found nothing).</summary>
    FromHistra,

    /// <summary>HISTRA found nothing for the name: the title waits for the user's confirmation.</summary>
    NotFound,

    /// <summary>HISTRA found another title of this name (and year): the title waits for the user's confirmation.</summary>
    Different,

    /// <summary>HISTRA is set up but could not be asked: the title is not checked and waits for the user's confirmation.</summary>
    Unavailable,
}

public static class TitleChecks
{
    /// <summary>The title waits for the user's confirmation before anything is named by it.</summary>
    public static bool NeedsConfirmation(this TitleCheck check) => check is TitleCheck.NotFound or TitleCheck.Different or TitleCheck.Unavailable;
}

/// <param name="Source">"TMDB" or "HISTRA" – where the titles come from.</param>
/// <param name="Note">Why the titles come from HISTRA, or why nothing could be searched or checked; "" when nothing to say.</param>
public sealed record TitleResults(IReadOnlyList<TmdbTitle> Titles, string Source, string Note = "")
{
    /// <summary>What HISTRA found for the same name (the check of TMDB's titles); null when HISTRA was not asked.</summary>
    public IReadOnlyList<TmdbTitle>? HistraTitles { get; init; }

    /// <summary>HISTRA is set up but could not be asked to check TMDB's titles.</summary>
    public bool HistraUnavailable { get; init; }

    /// <summary>The HISTRA catalog was searched (by name).</summary>
    public bool HistraSearched { get; init; }

    /// <summary>Why TMDB could not be used (a refused key, no answer); null when it was used or is not set up.</summary>
    public string? TmdbProblem { get; init; }

    /// <summary>The HISTRA catalog was searched as well and nothing was found anywhere.</summary>
    public bool FoundNowhere => Titles.Count == 0 && Source == "HISTRA" && HistraSearched;

    /// <summary>How HISTRA confirms a title of these results.</summary>
    public TitleCheck Check(TmdbTitle title)
    {
        if (Source == "HISTRA")
        {
            return TitleCheck.FromHistra;
        }

        if (HistraTitles is null)
        {
            return HistraUnavailable ? TitleCheck.Unavailable : TitleCheck.NotChecked;
        }

        if (HistraTitles.Any(h => h.Kind == title.Kind && h.Id == title.Id))
        {
            return TitleCheck.Confirmed;
        }

        return Alternative(title) is null ? TitleCheck.NotFound : TitleCheck.Different;
    }

    /// <summary>The title HISTRA found under the same name (and year) with another TMDB ID, its own catalog's first.</summary>
    public TmdbTitle? Alternative(TmdbTitle title)
    {
        if (HistraTitles is null)
        {
            return null;
        }

        return HistraTitles
            .Where(h => h.Id != title.Id && (title.IsNamed(h.Title) || title.IsNamed(h.OriginalTitle)) &&
                        (title.Year is null || h.Year is null || h.Year == title.Year))
            .OrderByDescending(h => h.HistraId is not null)
            .FirstOrDefault();
    }

    /// <summary>
    /// The title meant by a name (and year): the only exact match; among several exact matches the only one HISTRA
    /// confirms, or the only one in HISTRA's own catalog; with no exact match the only title of the year that HISTRA
    /// has in its own catalog. Null when it cannot be told – the user chooses.
    /// </summary>
    public TmdbTitle? Best(string name, int? year)
    {
        if (TmdbClient.ExactMatch(Titles, name, year) is { } single)
        {
            return single;
        }

        // the year in a file's name is often the year the film came out here, a year after its premiere (TMDB's year)
        if (year is { } asked && !Titles.Any(t => t.Year == asked && t.IsNamed(name)) &&
            Titles.Where(t => t.Year is { } other && Math.Abs(other - asked) == 1 && t.IsNamed(name)).ToList() is { Count: 1 } near)
        {
            return near[0];
        }

        if (HistraTitles is null && Source != "HISTRA")
        {
            return null;
        }

        var ofYear = Titles.Where(t => year is null || t.Year == year).ToList();
        var exact = ofYear.Where(t => t.IsNamed(name)).ToList();
        if (exact.Count == 1)
        {
            return null; // the exact match is of another year than asked: ExactMatch decided
        }

        var candidates = exact.Count > 1 ? exact : ofYear;
        var confirmed = Source == "HISTRA" ? candidates : candidates.Where(t => Check(t) == TitleCheck.Confirmed).ToList();
        if (exact.Count > 1 && confirmed.Count == 1)
        {
            return confirmed[0];
        }

        // HISTRA's own catalog (not only titles it passes on from TMDB) tells which one is meant
        var inCatalog = confirmed.Where(t => InHistraCatalog(t)).ToList();
        return inCatalog.Count == 1 ? inCatalog[0] : null;
    }

    /// <summary>
    /// The only title of the year (a year more or less; any, without a year) when there is just one, though no name of it
    /// is the guessed one (TMDB found it by a name it does not list) – a suggestion that waits for the user's
    /// confirmation; null when there are none or several.
    /// </summary>
    public TmdbTitle? OnlyOne(int? year) =>
        Titles.Where(t => year is null || t.Year is null || Math.Abs(t.Year.Value - year.Value) <= 1).ToList() is { Count: 1 } only ? only[0] : null;

    /// <summary>The title is in HISTRA's own catalog (not only passed on from TMDB).</summary>
    public bool InHistraCatalog(TmdbTitle title) =>
        title.HistraId is not null || HistraTitles?.Any(h => h.Kind == title.Kind && h.Id == title.Id && h.HistraId is not null) == true;
}

/// <summary>
/// Looks titles up for the pages (synchronisation of TMDB and HISTRA, decided 27. 10. 2026): on TMDB with the user's
/// key; in the HISTRA catalog when there is no TMDB key, TMDB finds nothing or refuses. What TMDB finds is checked in
/// HISTRA: the same name is searched there and the TMDB ID compared (<see cref="TitleResults.Check"/>), so the right
/// title is chosen among several and a doubtful one waits for the user. Only the name is sent to HISTRA; a TMDB ID or
/// address is read only from TMDB.
/// </summary>
public sealed class TitleSearch
{
    // how many of the found titles have their other names read when none is named as asked
    private const int OtherTitlesRead = 3;

    private readonly TmdbClient? _tmdb;
    private readonly HistraCatalog? _histra;

    public TitleSearch(TmdbClient? tmdb, HistraCatalog? histra)
    {
        if (tmdb is null && histra is null)
        {
            throw new ArgumentException("TMDB or HISTRA is needed.");
        }

        _tmdb = tmdb;
        _histra = histra;
    }

    public TmdbClient? Tmdb => _tmdb;

    /// <summary>HISTRA checks TMDB's titles and is searched when TMDB is missing or finds nothing.</summary>
    public bool UsesHistra => _histra is not null;

    /// <summary>Raised when a service asked to slow down and the search waits before trying again.</summary>
    public event Action<TimeSpan>? Waiting
    {
        add
        {
            if (_tmdb is not null)
            {
                _tmdb.Waiting += value;
            }

            if (_histra is not null)
            {
                _histra.Waiting += value;
            }
        }

        remove
        {
            if (_tmdb is not null)
            {
                _tmdb.Waiting -= value;
            }

            if (_histra is not null)
            {
                _histra.Waiting -= value;
            }
        }
    }

    /// <summary>What the user typed into a title field: a TMDB ID or address, or "Name (Year)".</summary>
    public async Task<TitleResults> FindAsync(MediaKind kind, string? text, CancellationToken cancellationToken = default)
    {
        var typed = TitleGuess.ParseTmdb(text) is null ? TitleGuess.Typed(kind, text) : null;
        TitleServiceException? tmdbProblem = null;
        if (_tmdb is not null)
        {
            try
            {
                var found = await _tmdb.FindAsync(kind, text, cancellationToken).ConfigureAwait(false);
                if (typed is not null)
                {
                    found = await WithOtherTitlesAsync(found, typed.Name, typed.Year, cancellationToken).ConfigureAwait(false);
                }

                if (found.Count > 0 || _histra is null)
                {
                    // an ID or address: HISTRA is asked by the title's name
                    return await CheckedAsync(new TitleResults(found, "TMDB"), typed?.Name ?? found.FirstOrDefault()?.Title, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TmdbException ex) when (_histra is not null)
            {
                tmdbProblem = ex;
            }
        }

        if (TitleGuess.ParseTmdb(text) is not null)
        {
            return new TitleResults([], "HISTRA", tmdbProblem?.Message ??
                (_tmdb is null ? "ID alebo adresu TMDB načíta len TMDB (kľúč v Nastaveniach) – v katalógu HISTRA hľadajte Názov (rok)." : "TMDB taký titul nepozná."))
            {
                TmdbProblem = tmdbProblem?.Message,
            };
        }

        if (typed is null)
        {
            return new TitleResults([], "HISTRA");
        }

        var results = await SearchHistraAsync(kind, typed.Name, typed.Year, cancellationToken).ConfigureAwait(false);

        // "Name (Year) [tmdbid-123]": that title, when the catalog has it
        if (typed.TmdbId is { } id && results.Where(r => r.Id == id).ToList() is { Count: > 0 } byId)
        {
            results = byId;
        }

        return new TitleResults(results, "HISTRA", Why(tmdbProblem)) { HistraSearched = true, TmdbProblem = tmdbProblem?.Message };
    }

    /// <summary>Titles matching a guessed name and year (the batch refines its suggestions with them), checked in HISTRA.</summary>
    public async Task<TitleResults> SearchAsync(MediaKind kind, string name, int? year, CancellationToken cancellationToken = default)
    {
        TitleServiceException? tmdbProblem = null;
        if (_tmdb is not null)
        {
            try
            {
                var found = await WithOtherTitlesAsync(await _tmdb.SearchAsync(kind, name, year, cancellationToken).ConfigureAwait(false), name, year, cancellationToken)
                    .ConfigureAwait(false);
                if (found.Count > 0 || _histra is null)
                {
                    return await CheckedAsync(new TitleResults(found, "TMDB"), name, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TmdbException ex) when (_histra is not null)
            {
                tmdbProblem = ex;
            }
        }

        return new TitleResults(await _histra!.SearchAsync(kind, name, year, cancellationToken).ConfigureAwait(false), "HISTRA", Why(tmdbProblem))
        {
            HistraSearched = true,
            TmdbProblem = tmdbProblem?.Message,
        };
    }

    /// <summary>
    /// How HISTRA confirms one TMDB title (read by its ID or address): its name is searched in HISTRA and the TMDB ID
    /// compared; <see cref="TitleCheck.NotChecked"/> without HISTRA.
    /// </summary>
    public async Task<TitleResults> CheckAsync(TmdbTitle title, CancellationToken cancellationToken = default) =>
        await CheckedAsync(new TitleResults([title], "TMDB"), title.Title, cancellationToken).ConfigureAwait(false);

    /// <summary>Saves the TMDB cache (HISTRA answers are kept only for the session).</summary>
    public void SaveCache() => _tmdb?.SaveCache();

    // TMDB answers in English: a name in another language (a file named by its Czech title) is matched by the other
    // names of the likely titles (of the year, a year more or less), read best first until one of them carries it
    private async Task<IReadOnlyList<TmdbTitle>> WithOtherTitlesAsync(IReadOnlyList<TmdbTitle> found, string name, int? year, CancellationToken cancellationToken)
    {
        bool OfYear(TmdbTitle t) => year is null || t.Year is null || Math.Abs(t.Year.Value - year.Value) <= 1;
        if (_tmdb is null || found.Count == 0 || string.IsNullOrWhiteSpace(name) || found.Any(t => OfYear(t) && t.IsNamed(name)))
        {
            return found;
        }

        var titles = found.ToList();
        var read = 0;
        for (var i = 0; i < titles.Count && read < OtherTitlesRead; i++)
        {
            if (!OfYear(titles[i]))
            {
                continue;
            }

            read++;
            try
            {
                titles[i] = titles[i] with { OtherTitles = await _tmdb.OtherTitlesAsync(titles[i].Kind, titles[i].Id, cancellationToken).ConfigureAwait(false) };
            }
            catch (TmdbException)
            {
                return titles; // the names could not be read: the titles stand as found
            }

            if (titles[i].IsNamed(name))
            {
                break;
            }
        }

        return titles;
    }

    // TMDB's titles with what HISTRA finds for the same name (all years: the TMDB ID decides), in the catalog of the
    // titles' kind (a film's address typed where a series is expected is checked among films)
    private async Task<TitleResults> CheckedAsync(TitleResults found, string? name, CancellationToken cancellationToken)
    {
        if (_histra is null || found.Titles.Count == 0)
        {
            return found;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return found with { HistraUnavailable = true, Note = "Titul nemá názov na overenie v HISTRA." };
        }

        try
        {
            return found with { HistraTitles = await _histra.SearchAsync(found.Titles[0].Kind, name, null, cancellationToken).ConfigureAwait(false) };
        }
        catch (HistraException ex)
        {
            return found with { HistraUnavailable = true, Note = $"HISTRA sa nedala opýtať ({ex.Message}) – titul nie je overený." };
        }
    }

    private async Task<IReadOnlyList<TmdbTitle>> SearchHistraAsync(MediaKind kind, string name, int? year, CancellationToken cancellationToken)
    {
        var results = await _histra!.SearchAsync(kind, name, year, cancellationToken).ConfigureAwait(false);
        return results.Count == 0 && year is not null
            ? await _histra.SearchAsync(kind, name, null, cancellationToken).ConfigureAwait(false)
            : results;
    }

    private string Why(TitleServiceException? tmdbProblem) =>
        tmdbProblem is not null ? tmdbProblem.Message + " Výsledky sú z katalógu HISTRA."
        : _tmdb is not null ? "TMDB nič nenašlo – výsledky sú z katalógu HISTRA."
        : "";
}
