using System.Globalization;
using System.Text.Json.Serialization;
using Universum.Core.Text;

namespace Universum.Core.News;

/// <summary>Where a premiere or an episode belongs: Czech and Slovak broadcasters and services, or the rest of the world.</summary>
public enum NewsRegion
{
    Domestic,
    World,
}

/// <summary>What an aired episode is: the first episode of a new series, of a new season, any other one, or a new episode on a streaming service whose number the source does not give.</summary>
public enum EpisodeKind
{
    Episode,
    SeriesPremiere,
    SeasonPremiere,
    Streaming,
}

/// <summary>An episode of a series aired on a day, as a source of news gives it.</summary>
public sealed record SeriesEpisode
{
    /// <summary>The name of the source in the settings (a web page, TVmaze …).</summary>
    public required string Source { get; init; }

    public required NewsRegion Region { get; init; }

    /// <summary>The title the source shows first (a Czech title on a Czech page); the original title when there is no other.</summary>
    public required string Title { get; init; }

    public required string OriginalTitle { get; init; }

    /// <summary>The year the series started.</summary>
    public int? Year { get; init; }

    public int? Season { get; init; }

    public int? Number { get; init; }

    /// <summary>The last episode when the source gives several aired at once ("01×09 + 1 další" = E09-E10).</summary>
    public int? LastNumber { get; init; }

    /// <summary>The day of the airing in the user's time zone.</summary>
    public required DateOnly AirDate { get; init; }

    public string Channel { get; init; } = "";

    /// <summary>The country of the channel (ISO code) when the source tells it.</summary>
    public string Country { get; init; } = "";

    public string Genres { get; init; } = "";

    public EpisodeKind Kind { get; init; }

    /// <summary>The kind of show (Scripted, Reality …) when the source tells it.</summary>
    public string ShowType { get; init; } = "";

    public string? Imdb { get; init; }

    public int? Tvdb { get; init; }

    public int? TvMaze { get; init; }

    /// <summary>The page of the series at the source.</summary>
    public string? Link { get; init; }

    /// <summary>The name of the series in the library: original title and year.</summary>
    [JsonIgnore]
    public string Caption => Year is { } year ? $"{OriginalTitle} ({year.ToString(CultureInfo.InvariantCulture)})" : OriginalTitle;

    /// <summary>"S01E04" or "S01E09-E10"; empty without a number.</summary>
    [JsonIgnore]
    public string Code => Season is { } season && Number is { } number
        ? $"S{season:00}E{number:00}" + (LastNumber is { } last && last > number ? $"-E{last:00}" : "")
        : "";

    [JsonIgnore]
    public bool IsPremiere => Kind is EpisodeKind.SeriesPremiere or EpisodeKind.SeasonPremiere;

    /// <summary>The same series in another source: its original title (without diacritics and case) and year.</summary>
    [JsonIgnore]
    public string SeriesKey => Words.Normalize(OriginalTitle) + "|" + (Year?.ToString(CultureInfo.InvariantCulture) ?? "");
}
