using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Universum.Core.Media;
using Universum.Core.Processes;
using Universum.Core.Subtitles;

namespace Universum.Core.Audio;

/// <summary>The codec of a sound written anew (Ponechať: the codec it has, when FFmpeg can write it).</summary>
public enum AudioCodec
{
    Keep,
    Ac3,
    Eac3,
    Aac,
    Opus,
    Flac,
}

/// <summary>
/// What becomes of one sound (owner, 30. 10. 2026: Úprava audiostopy): its times (the shift is written by mkvmerge, a change
/// of speed – 25 ↔ 23,976 – needs the sound written anew), the codec and its bitrate, stereo, the loudness. Anything but
/// the shift writes the sound anew (FFmpeg) into a new file; the original is never changed.
/// </summary>
/// <param name="KeepPitch">A change of speed keeps the pitch (otherwise it changes with the speed, as PAL releases do).</param>
/// <param name="Bitrate">kbit/s; null: by the codec and the channels.</param>
/// <param name="Normalize">Loudness to −23 LUFS (EBU R128), measured first.</param>
/// <param name="Gain">Louder (+) or quieter (−), in dB.</param>
public sealed record AudioRecipe(SubtitleTiming Timing, bool KeepPitch = true, AudioCodec Codec = AudioCodec.Keep, int? Bitrate = null, bool Stereo = false,
    bool Normalize = false, double Gain = 0)
{
    /// <summary>The target loudness of normalizing (EBU R128).</summary>
    public const double TargetLoudness = -23;

    public static AudioRecipe None { get; } = new(SubtitleTiming.None);

    /// <summary>The sound has to be written anew (a shift alone does not).</summary>
    public bool NeedsEncoding => Timing.Numerator != Timing.Denominator || Codec != AudioCodec.Keep || Stereo || Normalize || Gain != 0;

    /// <summary>The shift alone, as mkvmerge writes it (the speed is in the sound written anew).</summary>
    public SubtitleTiming Shift => SubtitleTiming.Delay(Timing.DelayMilliseconds);

    /// <summary>In words, for the list: "FPS 25 → 23,976 (zachová výšku tónu), AC-3 640 kbit/s, normalizovať".</summary>
    public string Describe(MediaTrack track)
    {
        var sk = CultureInfo.GetCultureInfo("sk-SK");
        var parts = new List<string>();
        if (!Timing.IsNone)
        {
            parts.Add(Timing.Describe("zvuk") + (Timing.Numerator != Timing.Denominator ? KeepPitch ? " (výška tónu ostane)" : " (výška tónu sa zmení)" : ""));
        }

        if (NeedsEncoding)
        {
            var (name, _) = Encoder(track);
            parts.Add(name is null ? "kodek sa nedá zapísať" : $"{name}{(BitrateFor(track) is { } kbps ? string.Create(sk, $" {kbps} kbit/s") : "")}");
        }

        if (Stereo)
        {
            parts.Add("stereo");
        }

        if (Normalize)
        {
            parts.Add(string.Create(sk, $"hlasitosť {TargetLoudness} LUFS"));
        }

        if (Gain != 0)
        {
            parts.Add(string.Create(sk, $"hlasitosť {Gain:+0.#;−0.#} dB"));
        }

        return parts.Count == 0 ? "bez zmeny" : string.Join(", ", parts);
    }

    /// <summary>Why the sound cannot be written (a codec FFmpeg does not write – DTS, TrueHD – kept), or null.</summary>
    public string? Problem(MediaTrack track) =>
        NeedsEncoding && Encoder(track).Encoder is null
            ? $"Kodek {track.Codec} FFmpeg nezapíše – zvoľte kodek (AC-3, E-AC-3, AAC, Opus, FLAC)."
            : Timing.Numerator != Timing.Denominator && Timing.Factor is < 0.5 or > 2 ? "Taká zmena rýchlosti sa nedá." : null;

    /// <summary>FFmpeg's encoder and the codec's name; the encoder null when FFmpeg cannot write the codec kept.</summary>
    public (string? Name, string? Encoder) Encoder(MediaTrack track) => Codec switch
    {
        AudioCodec.Ac3 => ("AC-3", "ac3"),
        AudioCodec.Eac3 => ("E-AC-3", "eac3"),
        AudioCodec.Aac => ("AAC", "aac"),
        AudioCodec.Opus => ("Opus", "libopus"),
        AudioCodec.Flac => ("FLAC", "flac"),
        _ => track.CodecId switch
        {
            "A_AC3" => ("AC-3", "ac3"),
            "A_EAC3" => ("E-AC-3", "eac3"),
            { } id when id.StartsWith("A_AAC", StringComparison.Ordinal) => ("AAC", "aac"),
            "A_OPUS" => ("Opus", "libopus"),
            "A_FLAC" => ("FLAC", "flac"),
            "A_MPEG/L3" => ("MP3", "libmp3lame"),
            "A_VORBIS" => ("Vorbis", "libvorbis"),
            { } id when id.StartsWith("A_PCM", StringComparison.Ordinal) => ("FLAC", "flac"),
            _ => (null, null),
        },
    };

    /// <summary>kbit/s written: the one chosen, or by the codec and the channels; null for FLAC (lossless).</summary>
    public int? BitrateFor(MediaTrack track)
    {
        var (_, encoder) = Encoder(track);
        if (encoder is null or "flac")
        {
            return null;
        }

        if (Bitrate is { } chosen)
        {
            return chosen;
        }

        var surround = !Stereo && track.AudioChannels is > 2;
        return encoder switch
        {
            "ac3" => surround ? 640 : 224,
            "eac3" => surround ? 768 : 256,
            "aac" => surround ? 448 : 192,
            "libopus" => surround ? 384 : 160,
            "libmp3lame" => 192,
            _ => surround ? 448 : 192,
        };
    }
}

/// <summary>
/// Writes a sound anew by its <see cref="AudioRecipe"/> (FFmpeg as a separate tool): the speed (atempo keeps the pitch,
/// asetrate changes it), stereo, the loudness (normalizing measures the whole sound first), the codec – into a new
/// Matroska audio file. The shift is not in it (mkvmerge writes it), nor anything but the sound.
/// </summary>
public static partial class AudioEncoder
{
    private const string Loudnorm = "loudnorm=I=-23:TP=-1:LRA=20";

    /// <summary>
    /// The audio track <paramref name="ordinal"/> of <paramref name="path"/> written into <paramref name="output"/> (a new
    /// file); <paramref name="progress"/> gets the share done (0–1). The message of FFmpeg when it fails.
    /// </summary>
    public static async Task<string?> EncodeAsync(IProcessRunner runner, string ffmpeg, string path, int ordinal, MediaTrack track, AudioRecipe recipe, string output,
        TimeSpan? duration, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (recipe.Problem(track) is { } problem)
        {
            return problem;
        }

        var rate = track.AudioSamplingFrequency is > 0 and var known ? known : 48000;
        var filters = SpeedFilters(recipe, rate);
        var stretched = duration is { } length ? TimeSpan.FromTicks((long)(length.Ticks * recipe.Timing.Factor)) : (TimeSpan?)null;
        var input = new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", path, "-map", "0:a:" + ordinal.ToString(CultureInfo.InvariantCulture), "-vn", "-sn", "-dn" };
        if (recipe.Stereo)
        {
            filters.Add("aformat=channel_layouts=stereo");
        }

        if (recipe.Normalize)
        {
            // the whole sound measured first (the first half of the work), then written to the loudness
            var measured = await MeasureAsync(runner, ffmpeg, input, filters, stretched, progress, cancellationToken).ConfigureAwait(false);
            if (measured is null)
            {
                return "Hlasitosť zvuku sa nepodarilo zmerať.";
            }

            filters.Add(measured);
            // loudnorm works at 192 kHz: back to the sound's rate
            filters.Add("aresample=" + rate.ToString(CultureInfo.InvariantCulture));
        }

        if (recipe.Gain != 0)
        {
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"volume={recipe.Gain:0.##}dB"));
        }

        var (_, encoder) = recipe.Encoder(track);
        var arguments = new List<string>(input) { "-map_metadata", "-1", "-map_chapters", "-1" };
        if (filters.Count > 0)
        {
            arguments.AddRange(["-af", string.Join(",", filters)]);
        }

        arguments.AddRange(["-c:a", encoder!]);
        if (recipe.BitrateFor(track) is { } kbps)
        {
            arguments.AddRange(["-b:a", kbps.ToString(CultureInfo.InvariantCulture) + "k"]);
        }

        // AC-3 and E-AC-3 carry at most 5.1
        if (!recipe.Stereo && encoder is "ac3" or "eac3" && track.AudioChannels is > 6)
        {
            arguments.AddRange(["-ac", "6"]);
        }

        arguments.AddRange(["-progress", "pipe:1", "-nostats", "-f", "matroska", output]);
        var share = recipe.Normalize ? (0.5, 0.5) : (0.0, 1.0);
        var result = await runner.RunAsync(new ProcessRequest(ffmpeg, arguments)
        {
            Timeout = TimeSpan.FromHours(6),
            OnOutputLine = line => Report(line, stretched, progress, share),
        }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
        {
            return "FFmpeg: " + (result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"skončil s kódom {result.ExitCode}");
        }

        progress?.Report(1);
        return null;
    }

    /// <summary>The filters of the speed: the times × factor (the sound as many times longer).</summary>
    public static List<string> SpeedFilters(AudioRecipe recipe, int rate)
    {
        var filters = new List<string>();
        if (recipe.Timing.Numerator == recipe.Timing.Denominator)
        {
            return filters;
        }

        var invariant = CultureInfo.InvariantCulture;
        var tempo = (double)recipe.Timing.Denominator / recipe.Timing.Numerator;
        filters.Add(recipe.KeepPitch
            ? string.Create(invariant, $"atempo={tempo:0.#########}")
            : string.Create(invariant, $"asetrate={rate * tempo:0.#####},aresample={rate}"));
        return filters;
    }

    // the loudness of the whole sound (loudnorm's first pass) → the filter of the second pass
    private static async Task<string?> MeasureAsync(IProcessRunner runner, string ffmpeg, string[] input, List<string> filters, TimeSpan? duration,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var arguments = new List<string>(input)
        {
            "-af", string.Join(",", filters.Append(Loudnorm + ":print_format=json")),
            "-progress", "pipe:1", "-nostats", "-f", "null", "-",
        };
        arguments[Array.IndexOf(input, "-loglevel") + 1] = "info";
        var result = await runner.RunAsync(new ProcessRequest(ffmpeg, arguments)
        {
            Timeout = TimeSpan.FromHours(6),
            OnOutputLine = line => Report(line, duration, progress, (0, 0.5)),
        }, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? SecondPass(result.StandardError) : null;
    }

    /// <summary>The filter of loudnorm's second pass from the first pass's report (null when it has none).</summary>
    public static string? SecondPass(string report)
    {
        var start = report.LastIndexOf('{');
        var end = report.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(report[start..(end + 1)]);
            string Value(string name) => json.RootElement.TryGetProperty(name, out var value) && value.GetString() is { } text &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                ? number.ToString("0.##", CultureInfo.InvariantCulture)
                : throw new FormatException(name);
            return $"{Loudnorm}:measured_I={Value("input_i")}:measured_TP={Value("input_tp")}:measured_LRA={Value("input_lra")}" +
                   $":measured_thresh={Value("input_thresh")}:offset={Value("target_offset")}:linear=true";
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            // silence (−inf) or no report: nothing to normalize by
            return null;
        }
    }

    private static void Report(string line, TimeSpan? duration, IProgress<double>? progress, (double From, double Share) part)
    {
        if (progress is null || duration is not { TotalSeconds: > 0 } length || ProgressTime().Match(line) is not { Success: true } match ||
            !long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
        {
            return;
        }

        progress.Report(part.From + (part.Share * Math.Clamp(microseconds / 1e6 / length.TotalSeconds, 0, 1)));
    }

    [GeneratedRegex("^out_time_us=(\\d+)")]
    private static partial Regex ProgressTime();
}
