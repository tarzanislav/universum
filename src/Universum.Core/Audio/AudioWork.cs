using Universum.Core.Subtitles;

namespace Universum.Core.Audio;

/// <summary>One sound as it was left in Úprava audiostopy (the values of the page, so they come back as they were).</summary>
public sealed record AudioTrackState
{
    /// <summary>A track of the video (its ID), or …</summary>
    public int? TrackId { get; init; }

    /// <summary>…a track (<see cref="ExternalTrack"/>, mkvmerge's ID there) of another file.</summary>
    public string? ExternalFile { get; init; }

    public int ExternalTrack { get; init; }

    public bool Include { get; init; } = true;

    /// <summary>The language code ("cs", "fil").</summary>
    public string? Language { get; init; }

    /// <summary>The track name written by the user (null: the rules' or the file's).</summary>
    public string? Name { get; init; }

    /// <summary>Zmena FPS (labels of <see cref="FrameRate.All"/>).</summary>
    public string? FpsFrom { get; init; }

    public string? FpsTo { get; init; }

    /// <summary>A speed measured by Napasovať that is no change of frame rate (numerator / denominator), or none.</summary>
    public long? MeasuredNumerator { get; init; }

    public long? MeasuredDenominator { get; init; }

    public string Delay { get; init; } = "0";

    public bool KeepPitch { get; init; } = true;

    public AudioCodec Codec { get; init; }

    public int? Bitrate { get; init; }

    public bool Stereo { get; init; }

    public bool Normalize { get; init; }

    public string Gain { get; init; } = "0";
}

/// <summary>The work on the sounds of one video (Úprava audiostopy), with the files the sounds were added from.</summary>
public sealed record AudioWork : VideoWork
{
    public List<AudioTrackState> Sounds { get; init; } = [];
}

/// <summary>Saved work of Úprava audiostopy.</summary>
public sealed class AudioWorkStore(string folder) : VideoWorkStore<AudioWork>(folder);
