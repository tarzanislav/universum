using System.Globalization;
using System.Text.RegularExpressions;
using Universum.Core.Media;
using Universum.Core.Processes;

namespace Universum.Core.Video;

/// <summary>The codec of a picture written anew (Ponechať: the codec it has – H.264 for any other).</summary>
public enum VideoCodec
{
    Keep,
    H264,
    Hevc,
}

/// <summary>
/// What becomes of the picture of a film (owner, 30. 10. 2026: Úprava videostopy): turned, of another height, in another
/// codec. Any of them writes the picture anew (FFmpeg with the encoders of Windows – the FFmpeg of UNIVERSUM is an LGPL
/// build); the original is never changed.
/// </summary>
/// <param name="Rotation">Degrees clockwise: 0, 90, 180, 270.</param>
/// <param name="Quality">The quality of the encoders of Windows, 1–100 (more = better and larger).</param>
/// <param name="Height">Lines of the picture written (null: as it is); the width follows the picture's proportions.</param>
public sealed record VideoRecipe(int Rotation = 0, VideoCodec Codec = VideoCodec.Keep, int Quality = 75, int? Height = null)
{
    public static VideoRecipe None { get; } = new();

    /// <summary>The picture has to be written anew.</summary>
    public bool NeedsEncoding => Rotation % 360 != 0 || Codec != VideoCodec.Keep || Height is not null;

    /// <summary>FFmpeg's encoder (the encoders of Windows: Media Foundation) and the codec's name.</summary>
    public (string Name, string Encoder) Encoder(MediaTrack track) => Codec switch
    {
        VideoCodec.Hevc => ("H.265", "hevc_mf"),
        VideoCodec.H264 => ("H.264", "h264_mf"),
        _ => track.CodecId == "V_MPEGH/ISO/HEVC" ? ("H.265", "hevc_mf") : ("H.264", "h264_mf"),
    };

    /// <summary>The Matroska codec ID of the picture written anew.</summary>
    public string WrittenCodecId(MediaTrack track) => Encoder(track).Encoder == "hevc_mf" ? "V_MPEGH/ISO/HEVC" : "V_MPEG4/ISO/AVC";

    /// <summary>Why it cannot be written, or null.</summary>
    public string? Problem => Rotation is not (0 or 90 or 180 or 270) ? "Otočiť sa dá o 90, 180 alebo 270°."
        : Quality is < 1 or > 100 ? "Kvalita je od 1 do 100."
        : Height is < 120 or > 4320 ? "Výška obrazu od 120 do 4320 riadkov."
        : null;

    /// <summary>In words: "otočiť o 90°, 720 riadkov, H.264 kvalita 75".</summary>
    public string Describe(MediaTrack track)
    {
        var parts = new List<string>();
        if (Rotation % 360 != 0)
        {
            parts.Add($"otočiť o {Rotation}°");
        }

        if (Height is { } height)
        {
            parts.Add($"{height} riadkov");
        }

        if (NeedsEncoding)
        {
            parts.Add($"{Encoder(track).Name} kvalita {Quality}");
        }

        return parts.Count == 0 ? "bez zmeny" : string.Join(", ", parts);
    }

    /// <summary>The size of the picture written ("1280x720") from the size it has; null when that is not known.</summary>
    public string? Dimensions(string? pixels)
    {
        if (pixels?.Split('x') is not [var w, var h] || !int.TryParse(w, CultureInfo.InvariantCulture, out var width) || !int.TryParse(h, CultureInfo.InvariantCulture, out var height) ||
            width <= 0 || height <= 0)
        {
            return null;
        }

        if (Rotation % 180 == 90)
        {
            (width, height) = (height, width);
        }

        if (Height is { } lines)
        {
            // as FFmpeg's "scale=-2:height" (rounded to the nearest even width)
            width = (int)Math.Round(width * (double)lines / height / 2, MidpointRounding.AwayFromZero) * 2;
            height = lines;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");
    }

    /// <summary>FFmpeg's filters: turned, then of the height, in the encoders' pixel format.</summary>
    public IReadOnlyList<string> Filters()
    {
        var filters = new List<string>();
        switch (Rotation % 360)
        {
            case 90:
                filters.Add("transpose=clock");
                break;
            case 180:
                filters.Add("hflip,vflip");
                break;
            case 270:
                filters.Add("transpose=cclock");
                break;
        }

        if (Height is { } height)
        {
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"scale=-2:{height}:flags=lanczos"));
        }

        filters.Add("format=nv12");
        return filters;
    }
}

/// <summary>
/// The picture of <paramref name="Path"/> (<paramref name="Source"/>: its video track) to be written anew by
/// <paramref name="Recipe"/>, with key frames at <paramref name="Keyframes"/> (a cut there is exact). <paramref name="Matroska"/>:
/// the film's own timestamps are kept (mkvmerge keeps those of a Matroska file; another one it counts from its first one).
/// </summary>
public sealed record VideoEncoding(string Path, MediaTrack Source, VideoRecipe Recipe, IReadOnlyList<TimeSpan> Keyframes, bool Matroska = true)
{
    /// <summary>The track that comes of it (UNIVERSUM's <paramref name="id"/>).</summary>
    public MediaTrack ToTrack(int id) => Source with
    {
        Id = id, ExternalFile = Path, ExternalTrack = 0, VideoEncode = this, Codec = Recipe.Encoder(Source).Name, CodecId = Recipe.WrittenCodecId(Source),
        PixelDimensions = Recipe.Dimensions(Source.PixelDimensions), DisplayDimensions = null, Number = null, Uid = null, StatisticsFrames = null, StatisticsBytes = null,
        StatisticsBitsPerSecond = null, TagEntries = 0, IndexEntries = null,
    };
}

/// <summary>Writes the picture of a film anew (FFmpeg as a separate tool) into a new Matroska file of one video track.</summary>
public static partial class VideoEncoder
{
    /// <summary>
    /// The picture written into <paramref name="output"/>; FFmpeg's message when it fails. <paramref name="encoder"/>
    /// replaces the encoder of Windows (tests on other systems: "libx264").
    /// </summary>
    public static async Task<string?> EncodeAsync(IProcessRunner runner, string ffmpeg, VideoEncoding encoding, string output, TimeSpan? duration,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default, string? encoder = null)
    {
        if (encoding.Recipe.Problem is { } problem)
        {
            return problem;
        }

        var invariant = CultureInfo.InvariantCulture;
        var chosen = encoder ?? encoding.Recipe.Encoder(encoding.Source).Encoder;
        // on the time mkvmerge gives the film's sound: a Matroska file's own timestamps (-copyts, also when it does not start
        // at 0), another file's from its first one (FFmpeg's own way) – the picture stays with the sound, the key frames forced
        // land on the marks
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error" };
        if (encoding.Matroska)
        {
            arguments.Add("-copyts");
        }

        arguments.AddRange(["-i", encoding.Path, "-map", "0:v:0", "-an", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
            "-vf", string.Join(",", encoding.Recipe.Filters()), "-c:v", chosen]);
        arguments.AddRange(chosen switch
        {
            // H.264 High (Media Foundation starts from Baseline; FFmpeg's generic option takes the number only: 100); the
            // HEVC encoder of Windows is a hardware one
            "h264_mf" => ["-rate_control", "quality", "-quality", encoding.Recipe.Quality.ToString(invariant), "-profile:v", "100"],
            "hevc_mf" => ["-rate_control", "quality", "-quality", encoding.Recipe.Quality.ToString(invariant), "-hw_encoding", "1"],
            _ => ["-crf", Math.Clamp(51 - (encoding.Recipe.Quality / 2), 0, 51).ToString(invariant)],
        });
        if (encoding.Keyframes.Count > 0)
        {
            arguments.AddRange(["-force_key_frames", string.Join(",", encoding.Keyframes.Select(k => k.TotalSeconds.ToString("0.###", invariant)))]);
        }

        arguments.AddRange(["-progress", "pipe:1", "-nostats", "-f", "matroska", output]);
        var result = await runner.RunAsync(new ProcessRequest(ffmpeg, arguments)
        {
            Timeout = TimeSpan.FromHours(24),
            OnOutputLine = line =>
            {
                if (progress is not null && duration is { TotalSeconds: > 0 } length && ProgressTime().Match(line) is { Success: true } match &&
                    long.TryParse(match.Groups[1].Value, NumberStyles.Integer, invariant, out var microseconds))
                {
                    progress.Report(Math.Clamp(microseconds / 1e6 / length.TotalSeconds, 0, 1));
                }
            },
        }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
        {
            return "FFmpeg: " + (result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"skončil s kódom {result.ExitCode}");
        }

        progress?.Report(1);
        return null;
    }

    [GeneratedRegex("^out_time_us=(\\d+)")]
    private static partial Regex ProgressTime();
}
