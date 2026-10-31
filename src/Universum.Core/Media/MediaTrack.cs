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

    /// <summary>The size the video is shown at ("1024x576" for anamorphic 720 × 576), or null.</summary>
    public string? DisplayDimensions { get; init; }

    /// <summary>Width ÷ height as the video is shown (by its display size, else its pixels), or null.</summary>
    public double? DisplayAspect => Size(DisplayDimensions) ?? Size(PixelDimensions);

    private static double? Size(string? dimensions) =>
        dimensions?.Split('x') is [var w, var h] && int.TryParse(w, System.Globalization.CultureInfo.InvariantCulture, out var width) &&
        int.TryParse(h, System.Globalization.CultureInfo.InvariantCulture, out var height) && width > 0 && height > 0
            ? (double)width / height
            : null;

    public bool IsTextSubtitles { get; init; }

    /// <summary>Number of frames from the track statistics tags, when present.</summary>
    public long? StatisticsFrames { get; init; }

    /// <summary>Number of bytes from the track statistics tags, when present.</summary>
    public long? StatisticsBytes { get; init; }

    /// <summary>Bits per second from the track statistics tags, when present.</summary>
    public long? StatisticsBitsPerSecond { get; init; }

    /// <summary>The duration of one frame in nanoseconds (video: 41 708 333 = 23,976 frames a second), when the file says it.</summary>
    public long? DefaultDurationNanoseconds { get; init; }

    /// <summary>Frames a second of a video track (from <see cref="DefaultDurationNanoseconds"/>), or null.</summary>
    public double? FramesPerSecond => DefaultDurationNanoseconds is > 0 and var duration ? 1_000_000_000.0 / duration : null;

    /// <summary>Number of index (cue) entries; 0 for a video track means seeking will be slow or broken.</summary>
    public int? IndexEntries { get; init; }

    /// <summary>Number of tag entries attached to this track.</summary>
    public int TagEntries { get; init; }

    /// <summary>
    /// The external file the track comes from – a subtitle file or a sound added to the video (owner, 30. 10. 2026); null
    /// for the file's own tracks. Its ID is UNIVERSUM's own (not one of the file's); the track is
    /// <see cref="ExternalTrack"/> of that file.
    /// </summary>
    public string? ExternalFile { get; init; }

    /// <summary>The track's ID in <see cref="ExternalFile"/> (mkvmerge's; 0 for a file of one track – a subtitle file, a sound).</summary>
    public int ExternalTrack { get; init; }

    /// <summary>
    /// A sound written anew (Úprava audiostopy: a change of speed, codec, channels, loudness) from a track of
    /// <see cref="ExternalFile"/> – the video itself or another file; it is written right before mkvmerge reads it.
    /// </summary>
    public Audio.SoundEncoding? Encode { get; init; }

    /// <summary>The picture written anew (Úprava videostopy: turned, of another height or codec, cut exactly) from a video track of <see cref="ExternalFile"/>.</summary>
    public Video.VideoEncoding? VideoEncode { get; init; }
}
