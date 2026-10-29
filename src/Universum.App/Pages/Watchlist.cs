using Universum.Core.Library;
using Universum.Core.News;

namespace Universum.App.Pages;

/// <summary>
/// Kde pozerať: where the chosen films and series (Vybrané) can be watched legally in Slovakia and Czechia – TMDB's
/// data by JustWatch, checked once a day (and on request); only the titles' IDs are sent. New offers are shown for a week.
/// </summary>
public sealed class Watchlist(AppServices services)
{
    private Dictionary<string, TitleAvailability>? _kept;

    /// <summary>Offers seen for the first time this long ago or less are "nové".</summary>
    public static readonly TimeSpan NewFor = TimeSpan.FromDays(7);

    /// <summary>Raised when the availability was checked.</summary>
    public event Action? Changed;

    private Dictionary<string, TitleAvailability> Kept => _kept ??= new AvailabilityStore(AppSettings.AvailabilityFile).Load();

    /// <summary>The kept availability of a title (by the key of its mark), or null.</summary>
    public TitleAvailability? Get(string markKey) => Kept.GetValueOrDefault(markKey);

    /// <summary>"▶ Netflix (SK)" for a table, or empty.</summary>
    public string Text(string markKey) => Get(markKey) is { Offers.Count: > 0 } title ? "▶ " + title.Text : "";

    /// <summary>The chosen titles with an offer, the newest offers first.</summary>
    public IReadOnlyList<(TitleAvailability Title, bool IsNew)> Offered()
    {
        var chosen = services.Marks.Chosen.ToHashSet(StringComparer.Ordinal);
        var since = DateOnly.FromDateTime(DateTime.Now - NewFor);
        return Kept.Where(k => chosen.Contains(k.Key) && k.Value.Offers.Count > 0)
            .Select(k => (k.Value, IsNew: k.Value.Streaming.Any(o => o.Since > since)))
            .OrderByDescending(t => t.IsNew).ThenBy(t => t.Value.Caption, StringComparer.Create(Texts.Slovak, true))
            .ToList();
    }

    /// <summary>Checks every chosen title; a problem to show, or null.</summary>
    public async Task<string?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (services.Watch is not { } watch)
        {
            return "Kde pozerať potrebuje kľúč TMDB v Nastaveniach.";
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var archive = services.PremiereStore.Load();
        var schedule = (await services.SeriesNews.ReadAsync(services.NewsSources, today, offline: true, cancellationToken: cancellationToken)).Episodes;
        var episodes = archive.Series.Select(s => s.First).Concat(schedule).GroupBy(MarkStore.Series).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var kept = Kept;
        var checkedNow = new Dictionary<string, TitleAvailability>(StringComparer.Ordinal);
        try
        {
            foreach (var key in services.Marks.Chosen)
            {
                (MediaKind Kind, int Id, string Caption)? title = null;
                if (key.StartsWith("film:", StringComparison.Ordinal) && int.TryParse(key[5..], out var film))
                {
                    var caption = archive.Films.FirstOrDefault(f => f.TmdbId == film)?.Caption ?? kept.GetValueOrDefault(key)?.Caption ?? $"film {film}";
                    title = (MediaKind.Movie, film, caption);
                }
                else if (key.StartsWith("serial:", StringComparison.Ordinal))
                {
                    if (episodes.TryGetValue(key, out var episode) && await services.SeriesLookup.FindAsync(episode) is { } match)
                    {
                        title = (MediaKind.Series, match.TmdbId, episode.Caption);
                    }
                    else if (kept.GetValueOrDefault(key) is { } known)
                    {
                        title = (MediaKind.Series, known.TmdbId, known.Caption);
                    }
                }

                if (title is not { } t)
                {
                    continue;
                }

                var offers = await watch.GetAsync(t.Kind, t.Id, today, cancellationToken);
                checkedNow[key] = new TitleAvailability(t.Caption, t.Kind, t.Id, DateTime.Now, WatchProviders.Merge(kept.GetValueOrDefault(key)?.Offers ?? [], offers));
            }
        }
        catch (TitleServiceException ex)
        {
            return "Kde pozerať: " + ex.Message;
        }
        finally
        {
            foreach (var (key, title) in checkedNow)
            {
                kept[key] = title;
            }

            new AvailabilityStore(AppSettings.AvailabilityFile).Save(kept);
            Changed?.Invoke();
        }

        return null;
    }
}

/// <summary>A title found in the input folder (an episode of a series of Novinky, or a chosen film): what it is and its files.</summary>
public sealed record FoundInFolder(string Title, string Detail, IReadOnlyList<string> Files, bool IsSeries, string Episode, IReadOnlyList<SeriesEpisode> Episodes);

/// <summary>
/// Watches the input folder: the aired episodes (the last month) of every series of Novinky – chosen or not (owner,
/// 29. 10. 2026) – and the chosen films (Vybrané); checked at the start, when Novinky or the Dashboard is shown and when
/// the folder changes.
/// Files are only read.
/// </summary>
public sealed class InputWatch(AppServices services)
{
    private IReadOnlyList<FoundInFolder> _found = [];
    private HashSet<string> _episodes = new(StringComparer.Ordinal);

    /// <summary>Episodes of the last month kept in view.</summary>
    public const int DaysBack = 30;

    public IReadOnlyList<FoundInFolder> Found => _found;

    /// <summary>The episode is in the input folder (as last checked).</summary>
    public bool Contains(SeriesEpisode episode) => _episodes.Contains(Key(episode));

    private static string Key(SeriesEpisode e) => $"{e.SeriesKey}|{e.Season}|{e.Number}";

    public event Action? Changed;

    public async Task ScanAsync()
    {
        if (services.InputDirectory is not { } input || !System.IO.Directory.Exists(input))
        {
            Set([], []);
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var chosen = services.Marks.Chosen.ToHashSet(StringComparer.Ordinal);
        var schedule = (await services.SeriesNews.ReadAsync(services.NewsSources, today, offline: true)).Episodes;
        var history = services.SeriesNews.History(today.AddDays(-SeriesNewsPageViewModel.RecentDays)).Where(e => e.AirDate >= today.AddDays(-DaysBack));
        var episodes = SeriesNews.Merge(schedule.Concat(history))
            .Where(e => e.AirDate <= today && e.AirDate >= today.AddDays(-DaysBack) && e.Code.Length > 0).ToList();
        var films = services.PremiereStore.Load().Films.Where(f => chosen.Contains(MarkStore.Film(f.TmdbId))).DistinctBy(f => f.TmdbId).ToList();
        var excluded = services.Library?.ResultFolders;
        try
        {
            var (foundEpisodes, foundFilms) = await Task.Run(() => (EpisodeFinder.Find(episodes, input, excluded), FilmFinder.Find(films, input, excluded)));
            var found = foundEpisodes.Where(f => f.Files.Count > 0).GroupBy(f => f.Episode.SeriesKey)
                .Select(g => new FoundInFolder(g.First().Episode.Caption,
                    string.Join(", ", g.Select(f => f.Episode.Code)) + " · " + Texts.Count(g.Sum(f => f.Files.Count), "súbor", "súbory", "súborov"),
                    g.SelectMany(f => f.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), true, g.First().Episode.Code, g.Select(f => f.Episode).ToList()))
                .Concat(foundFilms.Where(f => f.Files.Count > 0).Select(f => new FoundInFolder(f.Film.Caption, "film · " + Texts.Count(f.Files.Count, "súbor", "súbory", "súborov"),
                    f.Files, false, "", [])))
                .OrderBy(f => f.Title, StringComparer.Create(Texts.Slovak, true)).ToList();
            Set(found, foundEpisodes.Where(f => f.Files.Count > 0).Select(f => f.Episode));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Set([], []);
        }
    }

    private void Set(IReadOnlyList<FoundInFolder> found, IEnumerable<SeriesEpisode> episodes)
    {
        _found = found;
        _episodes = episodes.Select(Key).ToHashSet(StringComparer.Ordinal);
        Changed?.Invoke();
    }
}
