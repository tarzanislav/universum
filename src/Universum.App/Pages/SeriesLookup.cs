using Universum.Core.Library;
using Universum.Core.News;

namespace Universum.App.Pages;

/// <summary>
/// The series of Premiéry and Novinky found on TMDB – their ID for a direct link and the poster of the whole series –
/// each series asked once per run (TMDB's answers are kept on the disk for a month).
/// </summary>
public sealed class SeriesLookup
{
    private readonly AppServices _services;
    private readonly Dictionary<string, Task<SeriesMatch?>> _found = new(StringComparer.Ordinal);

    public SeriesLookup(AppServices services)
    {
        _services = services;
        services.TmdbChanged += _found.Clear;
    }

    /// <summary>The series on TMDB, or null (not found, TMDB unavailable or without a key).</summary>
    public Task<SeriesMatch?> FindAsync(SeriesEpisode episode)
    {
        if (!_found.TryGetValue(episode.SeriesKey, out var found))
        {
            _found[episode.SeriesKey] = found = SearchAsync(episode);
        }

        return found;
    }

    /// <summary>The poster path of the series, or null.</summary>
    public async Task<string?> PosterAsync(SeriesEpisode episode) => (await FindAsync(episode))?.PosterPath;

    private async Task<SeriesMatch?> SearchAsync(SeriesEpisode episode)
    {
        if (_services.SeriesTmdb is not { } tmdb)
        {
            return null;
        }

        try
        {
            return await tmdb.FindAsync(episode.OriginalTitle, episode.Year);
        }
        catch (TitleServiceException)
        {
            // asked again in the next run
            return null;
        }
    }
}
