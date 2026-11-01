using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Universum.Core.Processes;
using Universum.Core.Text;

namespace Universum.Core.Speech;

/// <summary>
/// Hlasové poznámky (owner, 31. 10. 2026 – POZNÁMKY): the voice recorded from a microphone by FFmpeg (installed with
/// UNIVERSUM) as raw 16 kHz mono samples – so a recording stopped at any moment is whole – then made a WAV file and written
/// down by whisper.cpp in the language chosen. Everything happens in the computer; nothing is sent anywhere.
/// </summary>
public static partial class VoiceNotes
{
    /// <summary>The samples a second (whisper.cpp hears 16 kHz).</summary>
    public const int SampleRate = 16000;

    /// <summary>A recording is stopped after this long at the latest.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(30);

    /// <summary>FFmpeg lists the sound devices of DirectShow (in its error output).</summary>
    public static ProcessRequest DevicesRequest(SpeechTools tools) =>
        new(tools.Ffmpeg, ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"]) { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The microphones in FFmpeg's list of devices ("Mikrofón (Vymyslená karta)").</summary>
    public static IReadOnlyList<string> Microphones(string ffmpegOutput) =>
        [.. AudioDevice().Matches(ffmpegOutput).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>FFmpeg records the microphone into raw samples (16 kHz, mono, 16 bits) until it is stopped or <see cref="Longest"/>.</summary>
    public static ProcessRequest RecordRequest(SpeechTools tools, string microphone, string samples) =>
        new(tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "dshow", "-i", "audio=" + microphone, "-t", ((int)Longest.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", samples])
        {
            Timeout = Longest + TimeSpan.FromMinutes(1),
        };

    /// <summary>FFmpeg takes an audio track of a file as such samples (the self-test and a recording from a file).</summary>
    public static ProcessRequest SamplesFromFileRequest(SpeechTools tools, string file, int audioOrdinal, TimeSpan from, TimeSpan length, string samples) =>
        new(tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-ss", from.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-t",
            length.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", file, "-map", $"0:a:{audioOrdinal}", "-ac", "1", "-ar",
            SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", samples])
        {
            Timeout = TimeSpan.FromMinutes(3),
        };

    /// <summary>Raw samples (16 kHz, mono, 16 bits) made a WAV file; how long it is.</summary>
    public static TimeSpan Wave(string samples, string wave)
    {
        var length = new FileInfo(samples).Length & ~1L;
        using (var output = new FileStream(wave, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(output, Encoding.ASCII))
        {
            writer.Write("RIFF"u8);
            writer.Write((uint)Math.Min(uint.MaxValue, 36 + length));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16u);                      // the size of the format
            writer.Write((ushort)1);                // PCM
            writer.Write((ushort)1);                // mono
            writer.Write((uint)SampleRate);
            writer.Write((uint)(SampleRate * 2));   // bytes a second
            writer.Write((ushort)2);                // bytes a sample
            writer.Write((ushort)16);               // bits a sample
            writer.Write("data"u8);
            writer.Write((uint)Math.Min(uint.MaxValue, length));
            using var input = File.OpenRead(samples);
            var buffer = new byte[81920];
            var left = length;
            while (left > 0 && input.Read(buffer, 0, (int)Math.Min(buffer.Length, left)) is var read and > 0)
            {
                output.Write(buffer, 0, read);
                left -= read;
            }
        }

        return TimeSpan.FromSeconds(length / 2.0 / SampleRate);
    }

    /// <summary>
    /// whisper.cpp writes down the recording in the language ("sk", "cs", "en"); the text, or null when it heard nothing
    /// or cannot run (the message of the problem in <paramref name="problem"/>).
    /// </summary>
    public static async Task<(string? Text, string? Problem)> TranscribeAsync(SpeechTools tools, IProcessRunner runner, string wave, string language, CancellationToken cancellationToken = default)
    {
        if (!tools.Writer.All(char.IsAscii))
        {
            return (null, CoreText.T("Prepis nejde: priečinok modelu reči má v ceste písmená s diakritikou."));
        }

        var name = Path.GetFileNameWithoutExtension(wave) + "-prepis";
        var json = Path.Combine(Path.GetDirectoryName(wave)!, name + ".json");
        try
        {
            var result = await runner.RunAsync(SpokenLanguage.TranscriptionRequest(tools, wave, language, name, tools.Writer) with { Timeout = TimeSpan.FromMinutes(60) }, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(json))
            {
                return (null, CoreText.F("Prepis sa nepodaril (whisper.cpp skončil s kódom {0}).", result.ExitCode.ToString(CultureInfo.InvariantCulture)));
            }

            var text = Text(await File.ReadAllTextAsync(json, cancellationToken).ConfigureAwait(false));
            return text.Length > 0 ? (text, null) : (null, CoreText.T("V nahrávke nebolo počuť reč."));
        }
        finally
        {
            File.Delete(json);
        }
    }

    /// <summary>The text of a whisper.cpp JSON transcription: its segments joined, without the marks of silence and noise.</summary>
    public static string Text(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var text = new StringBuilder();
            foreach (var segment in document.RootElement.GetProperty("transcription").EnumerateArray())
            {
                var said = segment.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                said = Noise().Replace(said, " ").Trim();
                if (said.Length > 0)
                {
                    text.Append(text.Length > 0 ? " " : "").Append(said);
                }
            }

            return text.ToString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "";
        }
    }

    // "Mikrofón (Vymyslená karta)" (audio)
    [GeneratedRegex("\"([^\"]+)\"\\s*\\(audio\\)", RegexOptions.CultureInvariant)]
    private static partial Regex AudioDevice();

    // [BLANK_AUDIO], [hudba], (smiech), ♪
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|♪", RegexOptions.CultureInvariant)]
    private static partial Regex Noise();
}
