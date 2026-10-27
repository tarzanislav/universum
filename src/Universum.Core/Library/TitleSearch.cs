using Universum.Core.Library.Histra;
using Universum.Core.Library.Tmdb;

namespace Universum.Core.Library;

/// <summary>A problem of a title service (TMDB, HISTRA) the user can act on; the message is ready to show.</summary>
public class TitleServiceException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="Source">"TMDB" or "HISTRA" – where the titles come from.</param>
/// <param name="Note">Why the titles come from HISTRA, or why nothing could be searched; "" when nothing to say.</param>
public sealed record TitleResults(IReadOnlyList<TmdbTitle> Titles, string Source, string Note = "");

/// <summary>
/// Looks titles up for the pages: on TMDB with the user's key; in the HISTRA catalog when there is no TMDB key, TMDB finds
/// nothing or cannot be reached. A TMDB ID or address is read only from TMDB (HISTRA is only searched by name).
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

    /// <summary>The HISTRA catalog is searched when TMDB is missing or finds nothing.</summary>
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
        TitleServiceException? tmdbProblem = null;
        if (_tmdb is not null)
        {
            try
            {
                var found = await _tmdb.FindAsync(kind, text, cancellationToken).ConfigureAwait(false);
                if (found.Count > 0 || _histra is null)
                {
                    return new TitleResults(found, "TMDB");
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

        if (TitleGuess.Typed(kind, text) is not { } typed)
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

    /// <summary>Titles matching a guessed name and year (the batch refines its suggestions with them).</summary>
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
                    return new TitleResults(found, "TMDB");
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
