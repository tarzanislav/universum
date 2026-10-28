using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Universum.Core.Text;

namespace Universum.Core.News;

/// <summary>The kinds of sources of news.</summary>
public enum NewsSourceKind
{
    /// <summary>A web page read by rules the user imported (one page per day).</summary>
    Page,

    /// <summary>The TVmaze API (series of the world).</summary>
    TvMaze,

    /// <summary>TMDB – film premieres (with the user's TMDB key).</summary>
    TmdbFilms,
}

/// <summary>
/// A source of news as kept in the settings – the user adds (imports), switches off and removes sources there; the
/// addresses of web pages live only in the settings, never in the program.
/// </summary>
public sealed record NewsSource
{
    public required string Name { get; init; }

    public NewsSourceKind Kind { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>The rules of a web page (only <see cref="NewsSourceKind.Page"/>).</summary>
    public PageRules? Rules { get; init; }

    /// <summary>TVmaze: the countries whose channels are read (besides all streaming services).</summary>
    public IReadOnlyList<string> Countries { get; init; } = ["US", "GB"];

    /// <summary>TVmaze: the least popularity weight (0–100) of a show.</summary>
    public int MinWeight { get; init; } = TvMazeFilter.Default.MinWeight;

    /// <summary>TVmaze: kinds of shows left out.</summary>
    public IReadOnlyList<string> SkipTypes { get; init; } = TvMazeFilter.Default.SkipTypes;

    /// <summary>Where the source is read from.</summary>
    [JsonIgnore]
    public string Address => Kind switch
    {
        NewsSourceKind.Page => Rules?.Address ?? "",
        NewsSourceKind.TvMaze => TvMazeClient.BaseAddress.AbsoluteUri,
        _ => "https://api.themoviedb.org/3/",
    };

    [JsonIgnore]
    public string KindText => Kind switch
    {
        NewsSourceKind.Page => "webová stránka (podľa pravidiel)",
        NewsSourceKind.TvMaze => "TVmaze API – seriály vo svete",
        _ => "TMDB – filmové premiéry",
    };

    /// <summary>The sources of a new installation: TVmaze and TMDB (web pages are imported by the user).</summary>
    public static IReadOnlyList<NewsSource> Defaults { get; } =
    [
        new() { Name = "TVmaze", Kind = NewsSourceKind.TvMaze },
        new() { Name = "TMDB – filmy", Kind = NewsSourceKind.TmdbFilms },
    ];
}

/// <summary>The episodes the sources gave, and what could not be read.</summary>
public sealed record SeriesSchedule(IReadOnlyList<SeriesEpisode> Episodes, IReadOnlyList<string> Problems);

/// <summary>A series premiere for the table: its first episode, how often episodes come and how many the season has.</summary>
public sealed record SeriesPremiereRow(SeriesEpisode First, string Periodicity, int? EpisodeCount);

/// <summary>
/// The schedule of series episodes from the sources of the settings, day by day: web pages by their rules and TVmaze.
/// Each day of each source is kept on the disk: a day read after it was over is final and never asked again (the
/// history of Novinky), today is asked again after 6 hours, later days after a day; Obnoviť asks today and later days
/// at once. A source that fails is reported and not asked again in this reading. The same episode from two sources is
/// shown once (the one of the page, with the Czech title and TVmaze's IDs).
/// </summary>
public sealed class SeriesNews(WebPageClient pages, TvMazeClient tvmaze, NewsCache cache)
{
    /// <summary>Days before and after today of a TVmaze source.</summary>
    public const int TvMazeDaysBack = 7;

    public const int TvMazeDaysAhead = 14;

    /// <summary>
    /// Web pages (Czech and Slovak channels) are read at least this many days ahead (owner, 28. 10. 2026: premieres there
    /// are announced weeks before).
    /// </summary>
    public const int PageDaysAhead = 30;

    /// <summary>
    /// The episodes of the days of the enabled sources; <paramref name="offline"/> gives only what is kept, without asking
    /// the sources (shown at once when a page opens).
    /// </summary>
    public async Task<SeriesSchedule> ReadAsync(IReadOnlyList<NewsSource> sources, DateOnly today, bool refresh = false,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default, bool offline = false)
    {
        var episodes = new List<SeriesEpisode>();
        var problems = new List<string>();
        foreach (var source in sources.Where(s => s.Enabled && s.Kind is NewsSourceKind.Page or NewsSourceKind.TvMaze))
        {
            var (back, ahead) = Window(source);
            var days = Enumerable.Range(-back, back + ahead + 1).Select(today.AddDays).ToList();
            try
            {
                for (var i = 0; i < days.Count; i++)
                {
                    var day = days[i];
                    if (cache.Get(source.Name, day, today, refresh, any: offline) is { } cached)
                    {
                        episodes.AddRange(cached);
                        continue;
                    }

                    if (offline)
                    {
                        continue;
                    }

                    progress?.Report($"{source.Name}: {day.Day}. {day.Month}. ({i + 1}/{days.Count})");
                    var read = await ReadDayAsync(source, day, cancellationToken).ConfigureAwait(false);
                    cache.Put(source.Name, day, read);
                    episodes.AddRange(read);
                }
            }
            catch (NewsSourceException ex)
            {
                problems.Add($"{source.Name}: {ex.Message}");
            }
            catch (FormatException ex)
            {
                problems.Add($"{source.Name}: stránka sa nedá prečítať podľa pravidiel ({ex.Message}).");
            }
        }

        return new SeriesSchedule(Merge(episodes), problems);
    }

    /// <summary>The days before and after today a source is read.</summary>
    public static (int Back, int Ahead) Window(NewsSource source) =>
        source.Kind == NewsSourceKind.Page && source.Rules is { } rules
            ? (rules.DaysBack, Math.Max(rules.DaysAhead, PageDaysAhead))
            : (TvMazeDaysBack, TvMazeDaysAhead);

    /// <summary>The last day read of the enabled sources of series.</summary>
    public static DateOnly ReadTo(IReadOnlyList<NewsSource> sources, DateOnly today) =>
        today.AddDays(sources.Where(s => s.Enabled && s.Kind is NewsSourceKind.Page or NewsSourceKind.TvMaze).Select(s => Window(s).Ahead).DefaultIfEmpty(0).Max());

    /// <summary>All kept episodes aired before a day (the history of Novinky), one entry per episode.</summary>
    public IReadOnlyList<SeriesEpisode> History(DateOnly before) => Merge(cache.Before(before));

    private async Task<IReadOnlyList<SeriesEpisode>> ReadDayAsync(NewsSource source, DateOnly day, CancellationToken cancellationToken)
    {
        if (source.Kind == NewsSourceKind.Page)
        {
            var rules = source.Rules ?? throw new NewsSourceException("zdroj nemá pravidlá – importujte ho znova.");
            var html = await pages.GetAsync(rules.PageAddress(day), cancellationToken).ConfigureAwait(false);
            return rules.Read(html, day, source.Name);
        }

        var filter = new TvMazeFilter(source.MinWeight, source.SkipTypes);
        var all = new List<SeriesEpisode>();
        foreach (var country in source.Countries.Select(c => (string?)c).Append(null))
        {
            all.AddRange(await tvmaze.ScheduleAsync(country, day, filter, cancellationToken).ConfigureAwait(false));
        }

        // the days are in the user's time zone (an evening in America is the next day here); the same episode read on two
        // days is merged later
        return all;
    }

    /// <summary>
    /// One entry per episode: the same series, season and episode from several sources or days becomes one (the
    /// earliest airing; the entry of a web page wins, it gets TVmaze's IDs).
    /// </summary>
    public static IReadOnlyList<SeriesEpisode> Merge(IEnumerable<SeriesEpisode> episodes)
    {
        var merged = new Dictionary<string, SeriesEpisode>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var episode in episodes.OrderBy(e => e.AirDate).ThenBy(e => e.Source == "TVmaze" ? 1 : 0))
        {
            var key = episode.Season is null
                ? $"{episode.SeriesKey}|{episode.AirDate.DayNumber}|{episode.Channel}"
                : $"{Words.Normalize(episode.OriginalTitle)}|{episode.Season}|{episode.Number}";
            if (!merged.TryGetValue(key, out var known))
            {
                merged[key] = episode;
                order.Add(key);
                continue;
            }

            if (known.Source != "TVmaze" && episode.Source == "TVmaze")
            {
                merged[key] = known with
                {
                    Imdb = known.Imdb ?? episode.Imdb,
                    Tvdb = known.Tvdb ?? episode.Tvdb,
                    TvMaze = known.TvMaze ?? episode.TvMaze,
                    Country = known.Country.Length > 0 ? known.Country : episode.Country,
                    ShowType = known.ShowType.Length > 0 ? known.ShowType : episode.ShowType,
                };
            }
        }

        return order.Select(k => merged[k]).ToList();
    }

    /// <summary>
    /// The premieres of the schedule with how often the episodes come and how many the season has – from TVmaze (by
    /// its ID, or the series found by its original name and year), else from the airings in the schedule. A premiere
    /// <paramref name="known"/> with its episode count from TVmaze already is not asked again.
    /// </summary>
    public async Task<IReadOnlyList<SeriesPremiereRow>> PremieresAsync(IReadOnlyList<SeriesEpisode> schedule, int maxLookups = 80,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default, Func<SeriesEpisode, SeriesPremiereRow?>? known = null)
    {
        var rows = new List<SeriesPremiereRow>();
        var premieres = schedule.Where(e => e.IsPremiere && e.Season is not null).ToList();
        var lookups = 0;
        foreach (var premiere in premieres)
        {
            if (known?.Invoke(premiere) is { EpisodeCount: not null } kept && kept.First.AirDate == premiere.AirDate)
            {
                rows.Add(kept with { First = premiere });
                continue;
            }

            IReadOnlyList<DateOnly> dates = [];
            try
            {
                var show = premiere.TvMaze;
                if (show is null && lookups++ < maxLookups)
                {
                    progress?.Report($"Seriály: {rows.Count + 1} z {premieres.Count}");
                    if (await tvmaze.FindAsync(premiere.OriginalTitle, cancellationToken).ConfigureAwait(false) is { } found &&
                        (premiere.Year is null || found.Year is null || Math.Abs(found.Year.Value - premiere.Year.Value) <= 1))
                    {
                        show = found.Id;
                    }
                }

                if (show is { } id && lookups++ < maxLookups * 2)
                {
                    dates = await tvmaze.SeasonDatesAsync(id, premiere.Season!.Value, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (NewsSourceException)
            {
                // without TVmaze the schedule itself tells what it can
            }

            if (dates.Count == 0)
            {
                dates = schedule.Where(e => e.SeriesKey == premiere.SeriesKey && e.Season == premiere.Season).Select(e => e.AirDate).ToList();
                rows.Add(new SeriesPremiereRow(premiere, Periodicity.Describe(dates), null));
            }
            else
            {
                rows.Add(new SeriesPremiereRow(premiere, Periodicity.Describe(dates), dates.Count));
            }
        }

        return rows;
    }
}

/// <summary>
/// The days read from the sources of news, one file per source and day in a folder of this Windows account. A day read
/// after it was over is final: it is kept for good (the history) and never asked again.
/// </summary>
public sealed class NewsCache(string folder)
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>The next week is asked again after a day, later days after three (schedules far ahead change little).</summary>
    public static TimeSpan Lifetime(DateOnly day, DateOnly today) =>
        day <= today ? TimeSpan.FromHours(6) : day <= today.AddDays(7) ? TimeSpan.FromDays(1) : TimeSpan.FromDays(3);

    /// <summary>
    /// A kept day, or null when it is to be asked: not kept, a past day read before it was over, today or a later day
    /// read too long ago or when <paramref name="refresh"/> asks for them again. <paramref name="any"/> gives whatever
    /// is kept (shown at once, before the sources are asked).
    /// </summary>
    public IReadOnlyList<SeriesEpisode>? Get(string source, DateOnly day, DateOnly today, bool refresh = false, bool any = false)
    {
        var file = File(source, day);
        try
        {
            if (!System.IO.File.Exists(file))
            {
                return null;
            }

            var written = System.IO.File.GetLastWriteTime(file);
            var final = DateOnly.FromDateTime(written) > day;
            if (!any && !final && (day < today || refresh || DateTime.Now - written > Lifetime(day, today)))
            {
                return null;
            }

            return Read(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Put(string source, DateOnly day, IReadOnlyList<SeriesEpisode> episodes)
    {
        try
        {
            Files.WriteAtomically(File(source, day), JsonSerializer.SerializeToUtf8Bytes(episodes, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the cache is only an optimisation
        }
    }

    /// <summary>The episodes of all kept days before a day, of all sources ever read.</summary>
    public IEnumerable<SeriesEpisode> Before(DateOnly day)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var kept) && kept < day && Read(file) is { } episodes)
            {
                foreach (var episode in episodes)
                {
                    yield return episode;
                }
            }
        }
    }

    /// <summary>Removes the kept days (all sources, the history too).</summary>
    public void Clear()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static List<SeriesEpisode>? Read(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SeriesEpisode>>(System.IO.File.ReadAllBytes(file), Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // the source's name is hashed: any name is a safe folder
    private string File(string source, DateOnly day) =>
        Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16], day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".json");
}
