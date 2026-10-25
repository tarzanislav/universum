namespace Universum.Core.Media;

/// <summary>One track of a media file as reported by an inspector.</summary>
public sealed record MediaTrack
{
    /// <summary>Track ID used by the backend's command line (mkvmerge: 0-based source track ID).</summary>
    public required int Id { get; init; }

    public required TrackType Type { get; init; }

    /// <summary>Human readable codec name, e.g. "AC-3".</summary>
    public string Codec { get; init; } = "";

    /// <summary>Container codec ID, e.g. "A_AC3".</summary>
    public string? CodecId { get; init; }

    /// <summary>Track name; null when the element is absent.</summary>
    public string? Name { get; init; }

    public LanguageTag Language { get; init; } = LanguageTag.Undetermined;

    public TrackFlags Flags { get; init; } = new();

    /// <summary>Track number in the container (Matroska: 1-based).</summary>
    public int? Number { get; init; }

    public ulong? Uid { get; init; }

    public int? AudioChannels { get; init; }

    public int? AudioSamplingFrequency { get; init; }

    /// <summary>Video size as "width x height".</summary>
    public string? PixelDimensions { get; init; }

    public bool IsTextSubtitles { get; init; }

    /// <summary>Number of frames from the track statistics tags, when present.</summary>
    public long? StatisticsFrames { get; init; }

    /// <summary>Bits per second from the track statistics tags, when present.</summary>
    public long? StatisticsBitsPerSecond { get; init; }

    /// <summary>Number of tag entries attached to this track.</summary>
    public int TagEntries { get; init; }
}
