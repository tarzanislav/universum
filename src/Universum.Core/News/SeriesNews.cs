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
/// Each day of each source is kept on the disk (6 hours for today and later days, 3 days for past ones), so the
/// sources are asked only for what is missing; a source that fails is reported and not asked again in this reading.
/// The same episode from two sources is shown once (the one of the page, with the Czech title and TVmaze's IDs).
/// </summary>
public sealed class SeriesNews(WebPageClient pages, TvMazeClient tvmaze, NewsCache cache)
{
    /// <summary>Days before and after today of a TVmaze source.</summary>
    public const int TvMazeDaysBack = 7;

    public const int TvMazeDaysAhead = 14;

    public async Task<SeriesSchedule> ReadAsync(IReadOnlyList<NewsSource> sources, DateOnly today, bool refresh = false,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var episodes = new List<SeriesEpisode>();
        var problems = new List<string>();
        foreach (var source in sources.Where(s => s.Enabled && s.Kind is NewsSourceKind.Page or NewsSourceKind.TvMaze))
        {
            var (back, ahead) = source.Kind == NewsSourceKind.Page && source.Rules is { } rules ? (rules.DaysBack, rules.DaysAhead) : (TvMazeDaysBack, TvMazeDaysAhead);
            var days = Enumerable.Range(-back, back + ahead + 1).Select(today.AddDays).ToList();
            try
            {
                for (var i = 0; i < days.Count; i++)
                {
                    var day = days[i];
                    var lifetime = day < today ? TimeSpan.FromDays(3) : TimeSpan.FromHours(6);
                    if (!refresh && cache.Get(source.Name, day, lifetime) is { } cached)
                    {
                        episodes.AddRange(cached);
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
    /// its ID, or the series found by its original name and year), else from the airings in the schedule.
    /// </summary>
    public async Task<IReadOnlyList<SeriesPremiereRow>> PremieresAsync(IReadOnlyList<SeriesEpisode> schedule, int maxLookups = 80,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var rows = new List<SeriesPremiereRow>();
        var premieres = schedule.Where(e => e.IsPremiere && e.Season is not null).ToList();
        var lookups = 0;
        foreach (var premiere in premieres)
        {
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

/// <summary>The days read from the sources of news, one file per source and day in a folder of this Windows account.</summary>
public sealed class NewsCache(string folder)
{
    private static readonly JsonSerializerOptions Options = new();

    public IReadOnlyList<SeriesEpisode>? Get(string source, DateOnly day, TimeSpan lifetime)
    {
        var file = File(source, day);
        try
        {
            if (!System.IO.File.Exists(file) || DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(file) > lifetime)
            {
                return null;
            }

            return JsonSerializer.Deserialize<List<SeriesEpisode>>(System.IO.File.ReadAllBytes(file), Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Put(string source, DateOnly day, IReadOnlyList<SeriesEpisode> episodes)
    {
        try
        {
            var file = File(source, day);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllBytes(file + ".part", JsonSerializer.SerializeToUtf8Bytes(episodes, Options));
            System.IO.File.Move(file + ".part", file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the cache is only an optimisation
        }
    }

    /// <summary>Removes the kept days (all sources).</summary>
    public void Clear()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // the source's name is hashed: any name is a safe folder
    private string File(string source, DateOnly day) =>
        Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16], day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".json");
}
