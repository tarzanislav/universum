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
}

/// <param name="Source">"TMDB" or "HISTRA" – where the titles come from.</param>
/// <param name="Note">Why the titles come from HISTRA, or why nothing could be searched or checked; "" when nothing to say.</param>
public sealed record TitleResults(IReadOnlyList<TmdbTitle> Titles, string Source, string Note = "")
{
    /// <summary>What HISTRA found for the same name (the check of TMDB's titles); null when HISTRA was not asked.</summary>
    public IReadOnlyList<TmdbTitle>? HistraTitles { get; init; }

    /// <summary>HISTRA was searched as well and nothing was found anywhere.</summary>
    public bool FoundNowhere => Titles.Count == 0 && Source == "HISTRA";

    /// <summary>How HISTRA confirms a title of these results.</summary>
    public TitleCheck Check(TmdbTitle title)
    {
        if (Source == "HISTRA")
        {
            return TitleCheck.FromHistra;
        }

        if (HistraTitles is null)
        {
            return TitleCheck.NotChecked;
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

        var names = new[] { Words.Normalize(title.Title), Words.Normalize(title.OriginalTitle) };
        return HistraTitles
            .Where(h => h.Id != title.Id && (names.Contains(Words.Normalize(h.Title)) || names.Contains(Words.Normalize(h.OriginalTitle))) &&
                        (title.Year is null || h.Year is null || h.Year == title.Year))
            .OrderByDescending(h => h.HistraId is not null)
            .FirstOrDefault();
    }

    /// <summary>
    /// The title meant by a name (and year): the only exact match; among several exact matches the only one HISTRA
    /// confirms, or the only one in HISTRA's own catalog. Null when it cannot be told – the user chooses.
    /// </summary>
    public TmdbTitle? Best(string name, int? year)
    {
        if (TmdbClient.ExactMatch(Titles, name, year) is { } single)
        {
            return single;
        }

        if (HistraTitles is null && Source != "HISTRA")
        {
            return null;
        }

        var wanted = Words.Normalize(name);
        var exact = Titles.Where(t => (Words.Normalize(t.Title) == wanted || Words.Normalize(t.OriginalTitle) == wanted) && (year is null || t.Year == year)).ToList();
        if (exact.Count < 2)
        {
            return null;
        }

        var confirmed = Source == "HISTRA" ? exact : exact.Where(t => Check(t) == TitleCheck.Confirmed).ToList();
        if (confirmed.Count == 1)
        {
            return confirmed[0];
        }

        var inCatalog = confirmed.Where(t => (Source == "HISTRA" ? t : HistraTitles!.First(h => h.Kind == t.Kind && h.Id == t.Id)).HistraId is not null).ToList();
        return inCatalog.Count == 1 ? inCatalog[0] : null;
    }
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
                if (found.Count > 0 || _histra is null)
                {
                    // an ID or address: HISTRA is asked by the title's name
                    return await CheckedAsync(new TitleResults(found, "TMDB"), kind, typed?.Name ?? found.FirstOrDefault()?.Title, cancellationToken).ConfigureAwait(false);
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
                (_tmdb is null ? "ID alebo adresu TMDB načíta len TMDB (kľúč v Nastaveniach) – v katalógu HISTRA hľadajte Názov (rok)." : "TMDB taký titul nepozná."));
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

        return new TitleResults(results, "HISTRA", Why(tmdbProblem));
    }

    /// <summary>Titles matching a guessed name and year (the batch refines its suggestions with them), checked in HISTRA.</summary>
    public async Task<TitleResults> SearchAsync(MediaKind kind, string name, int? year, CancellationToken cancellationToken = default)
    {
        TitleServiceException? tmdbProblem = null;
        if (_tmdb is not null)
        {
            try
            {
                var found = await _tmdb.SearchAsync(kind, name, year, cancellationToken).ConfigureAwait(false);
                if (found.Count > 0 || _histra is null)
                {
                    return await CheckedAsync(new TitleResults(found, "TMDB"), kind, name, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TmdbException ex) when (_histra is not null)
            {
                tmdbProblem = ex;
            }
        }

        return new TitleResults(await _histra!.SearchAsync(kind, name, year, cancellationToken).ConfigureAwait(false), "HISTRA", Why(tmdbProblem));
    }

    /// <summary>Saves the TMDB cache (HISTRA answers are kept only for the session).</summary>
    public void SaveCache() => _tmdb?.SaveCache();

    // TMDB's titles with what HISTRA finds for the same name (all years: the TMDB ID decides)
    private async Task<TitleResults> CheckedAsync(TitleResults found, MediaKind kind, string? name, CancellationToken cancellationToken)
    {
        if (_histra is null || found.Titles.Count == 0 || string.IsNullOrWhiteSpace(name))
        {
            return found;
        }

        try
        {
            return found with { HistraTitles = await _histra.SearchAsync(kind, name, null, cancellationToken).ConfigureAwait(false) };
        }
        catch (HistraException ex)
        {
            return found with { Note = $"HISTRA sa nedala opýtať ({ex.Message}) – titul nie je overený." };
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
