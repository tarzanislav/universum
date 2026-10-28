using Universum.Core.Library;
using Universum.Core.Library.Histra;
using Universum.Core.Library.Tmdb;
using Universum.Core.News;

namespace Universum.App.Pages;

/// <summary>
/// Opens the page of a film or series on TMDB or HISTRA when only its name and year are known (premieres, new
/// episodes): the title is looked up only when the user clicks – TMDB by name and year (its search page without a key),
/// HISTRA in its catalog (only the name is sent; nothing is written there).
/// </summary>
public sealed class TitleOpener(AppServices services)
{
    /// <summary>Opens the title on TMDB; a problem to show, or null.</summary>
    public async Task<string?> OpenTmdbAsync(MediaKind kind, int? tmdbId, string name, int? year)
    {
        if (tmdbId is > 0)
        {
            Browser.Open(TitleLinks.Tmdb(kind, tmdbId)!);
            return null;
        }

        if (services.Tmdb is { } tmdb)
        {
            try
            {
                var results = await tmdb.SearchAsync(kind, name, year);
                if (results.Count == 0 && year is not null)
                {
                    results = await tmdb.SearchAsync(kind, name, null);
                }

                var best = TmdbClient.ExactMatch(results, name, year) ??
                           results.FirstOrDefault(r => year is null || r.Year is null || Math.Abs(r.Year.Value - year.Value) <= 1);
                if (best is not null)
                {
                    Browser.Open(TitleLinks.Tmdb(kind, best.Id)!);
                    return null;
                }
            }
            catch (TitleServiceException ex)
            {
                return ex.Message;
            }
        }

        // without a key or a match: TMDB's own search
        Browser.Open(new Uri($"https://www.themoviedb.org/search/{(kind == MediaKind.Movie ? "movie" : "tv")}?query=" +
                             Uri.EscapeDataString(name + (year is { } y ? $" y:{y}" : ""))));
        return null;
    }

    /// <summary>Opens a series of the news on TMDB – directly when it was found there, else by its name.</summary>
    public async Task<string?> OpenSeriesTmdbAsync(SeriesEpisode episode) =>
        await services.SeriesLookup.FindAsync(episode) is { } match
            ? await OpenTmdbAsync(MediaKind.Series, match.TmdbId, episode.OriginalTitle, episode.Year)
            : await OpenTmdbAsync(MediaKind.Series, null, episode.OriginalTitle, episode.Year);

    /// <summary>Opens a series of the news on HISTRA (by its TMDB ID when it was found on TMDB).</summary>
    public async Task<string?> OpenSeriesHistraAsync(SeriesEpisode episode) =>
        await OpenHistraAsync(MediaKind.Series, (await services.SeriesLookup.FindAsync(episode))?.TmdbId, episode.OriginalTitle, episode.Year);

    /// <summary>Opens the title on HISTRA when its catalog has it; a problem to show, or null.</summary>
    public async Task<string?> OpenHistraAsync(MediaKind kind, int? tmdbId, string name, int? year)
    {
        if (services.Histra is not { } histra)
        {
            return "Odkaz na HISTRA potrebuje token HISTRA v Nastaveniach (a zapnuté pripájanie).";
        }

        try
        {
            var results = await new HistraCatalog(histra).SearchAsync(kind, name, year);
            var match = results.FirstOrDefault(r => tmdbId is > 0 && r.Id == tmdbId && r.HistraId is not null) ??
                        results.FirstOrDefault(r => r.HistraId is not null && r.IsNamed(name));
            if (match?.HistraId is { } id)
            {
                Browser.Open(TitleLinks.Histra(kind, id)!);
                return null;
            }

            return $"HISTRA titul „{name}“ vo svojom katalógu nemá.";
        }
        catch (TitleServiceException ex)
        {
            return ex.Message;
        }
    }
}
