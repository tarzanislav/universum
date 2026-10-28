using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using Universum.Core.Media;
using Universum.Core.Processes;

namespace Universum.Core.Speech;

/// <summary>What one piece of an audio track said: the language whisper heard and how sure it was.</summary>
/// <param name="Language">ISO 639-1 code ("cs"), or null when the piece was silent, empty or not recognised.</param>
public sealed record SpeechPiece(TimeSpan At, string? Language, double Probability, bool Silent = false);

/// <summary>The spoken language of an audio track decided from its pieces; always only a suggestion for the user.</summary>
/// <param name="Language">ISO 639-1 code of the language most pieces heard, or null when none can be told.</param>
/// <param name="Agreement">Share of the winning language in the sure pieces (0–1).</param>
/// <param name="Doubt">Why the suggestion should be checked carefully (Slovak), or null.</param>
public sealed record SpokenLanguageResult(string? Language, double Agreement, IReadOnlyList<SpeechPiece> Pieces, string? Doubt);

/// <summary>
/// Recognises the spoken language of an audio track: FFmpeg cuts short pieces from several places of the film (not
/// only the beginning, where music often plays) as 16 kHz mono WAV, silent pieces are left out, whisper.cpp tells the
/// language of each, and the pieces vote. Czech and Slovak are told apart carefully: a piece of the other one, or a
/// single piece, makes the suggestion a doubtful one. Nothing leaves the computer.
/// </summary>
public static partial class SpokenLanguage
{
    /// <summary>Pieces whisper is less sure about do not vote (music, noise, several languages).</summary>
    public const double MinimumProbability = 0.4;

    /// <summary>Quieter pieces are silence and are not given to whisper.</summary>
    public const double SilenceDecibels = -50;

    private static readonly TimeSpan PieceLength = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where the pieces start: one piece of a short file; otherwise at 20, 50 and 80 % of the film and, when they do
    /// not agree, at 35 and 65 %; without a known length at 1, 5 and 15 minutes (and 30 and 45).
    /// </summary>
    public static IReadOnlyList<TimeSpan> Positions(TimeSpan? duration)
    {
        if (duration is not { } length)
        {
            return [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45)];
        }

        if (length < TimeSpan.FromMinutes(2))
        {
            return [TimeSpan.Zero];
        }

        var last = length - PieceLength;
        return new[] { 0.2, 0.5, 0.8, 0.35, 0.65 }.Select(f => TimeSpan.FromSeconds(Math.Min(last.TotalSeconds, length.TotalSeconds * f))).ToList();
    }

    /// <summary>The language of the audio track with this number among the file's audio tracks (0 = the first).</summary>
    /// <param name="pieceDone">Reports each finished piece (1, 2, …).</param>
    public static async Task<SpokenLanguageResult> DetectAsync(
        SpeechTools tools, IProcessRunner runner, string path, int audioOrdinal, TimeSpan? duration,
        IProgress<int>? pieceDone = null, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "UNIVERSUM");
        Directory.CreateDirectory(folder);
        var pieces = new List<SpeechPiece>();
        string? problem = null;
        foreach (var at in Positions(duration))
        {
            // three pieces that agree are enough
            if (pieces.Count >= 3 && IsClear(pieces))
            {
                break;
            }

            var wave = Path.Combine(folder, $"speech-{Guid.NewGuid():N}.wav");
            try
            {
                if (!File.Exists(tools.Ffmpeg) || !File.Exists(tools.Whisper))
                {
                    throw new SpeechException("Nástroje rozpoznávania reči chýbajú – preinštalujte UNIVERSUM.");
                }

                var cut = await runner.RunAsync(new ProcessRequest(tools.Ffmpeg, CutArguments(path, audioOrdinal, at, wave)) { Timeout = TimeSpan.FromMinutes(3) },
                    cancellationToken).ConfigureAwait(false);
                if (cut.ExitCode != 0 || !File.Exists(wave))
                {
                    problem ??= LastLine(cut.StandardError);
                    pieces.Add(new SpeechPiece(at, null, 0));
                    continue;
                }

                if (Loudness(wave) is not { } decibels || decibels < SilenceDecibels)
                {
                    pieces.Add(new SpeechPiece(at, null, 0, Silent: true)); // silence, or nothing after the end of a shorter film
                    continue;
                }

                var heard = await runner.RunAsync(
                    new ProcessRequest(tools.Whisper, ["-m", tools.Model, "-f", wave, "-dl", "-t", Threads.ToString(CultureInfo.InvariantCulture)])
                    {
                        Timeout = TimeSpan.FromMinutes(5),
                    },
                    cancellationToken).ConfigureAwait(false);
                if (ParseDetected(heard.StandardError + "\n" + heard.StandardOutput) is { } detected)
                {
                    pieces.Add(new SpeechPiece(at, detected.Language, detected.Probability));
                }
                else
                {
                    problem ??= LastLine(heard.StandardError);
                    pieces.Add(new SpeechPiece(at, null, 0));
                }
            }
            catch (ProcessStartException ex)
            {
                throw new SpeechException("Nástroj rozpoznávania reči sa nedá spustiť: " + ex.Message, ex);
            }
            finally
            {
                File.Delete(wave);
                pieceDone?.Report(pieces.Count);
            }
        }

        if (pieces.All(p => p is { Language: null, Silent: false }) && problem is not null)
        {
            throw new SpeechException("Reč sa nepodarilo spracovať: " + problem);
        }

        return Decide(pieces);
    }

    /// <summary>FFmpeg: 30 seconds of the audio track from the position, mono 16 kHz WAV (what whisper reads).</summary>
    public static IReadOnlyList<string> CutArguments(string path, int audioOrdinal, TimeSpan at, string wave) =>
    [
        "-nostdin", "-hide_banner", "-loglevel", "error",
        "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
        "-map", "0:a:" + audioOrdinal.ToString(CultureInfo.InvariantCulture),
        "-t", PieceLength.TotalSeconds.ToString(CultureInfo.InvariantCulture),
        "-vn", "-sn", "-dn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", "-y", wave,
    ];

    /// <summary>"whisper_full_with_state: auto-detected language: cs (p = 0.912345)" → (cs, 0.91).</summary>
    public static (string Language, double Probability)? ParseDetected(string output)
    {
        var match = Detected().Match(output);
        return match.Success && double.TryParse(match.Groups["p"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var probability)
            ? (match.Groups["language"].Value, probability)
            : null;
    }

    /// <summary>
    /// The vote of the pieces: the sure ones (<see cref="MinimumProbability"/>) count with their probability; the
    /// language needs more than half of it. A doubt is noted when the pieces disagree, when Czech and Slovak both
    /// appear, or when only one piece could be heard.
    /// </summary>
    public static SpokenLanguageResult Decide(IReadOnlyList<SpeechPiece> pieces)
    {
        var sure = pieces.Where(p => p.Language is not null && p.Probability >= MinimumProbability).ToList();
        if (sure.Count == 0)
        {
            var why = pieces.Count > 0 && pieces.All(p => p.Silent) ? "v úsekoch nie je reč (ticho)" : "reč sa nedala spoľahlivo rozpoznať (hudba alebo šum?)";
            return new SpokenLanguageResult(null, 0, pieces, why);
        }

        var scores = sure.GroupBy(p => p.Language!).Select(g => (Language: g.Key, Score: g.Sum(p => p.Probability), Count: g.Count()))
            .OrderByDescending(s => s.Score).ToList();
        var total = scores.Sum(s => s.Score);
        var winner = scores[0];
        var agreement = winner.Score / total;
        if (agreement <= 0.5)
        {
            return new SpokenLanguageResult(null, agreement, pieces, "úseky sa nezhodli: " + Counts(scores));
        }

        string? doubt = null;
        if (scores.Count > 1)
        {
            doubt = (IsCzechOrSlovak(winner.Language) && scores.Any(s => s.Language != winner.Language && IsCzechOrSlovak(s.Language))
                ? "čeština alebo slovenčina – overte; "
                : "") + "úseky: " + Counts(scores);
        }
        else if (sure.Count == 1)
        {
            doubt = "rozpoznané len z jedného úseku";
        }

        return new SpokenLanguageResult(winner.Language, agreement, pieces, doubt);
    }

    /// <summary>Loudness of a 16-bit PCM WAV in dBFS (RMS); null when it holds less than a second of sound.</summary>
    public static double? Loudness(string wave)
    {
        var bytes = File.ReadAllBytes(wave);
        var data = DataChunk(bytes);
        if (data.Length < 16000 * 2)
        {
            return null;
        }

        double sum = 0;
        var count = data.Length / 2;
        for (var i = 0; i < count; i++)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
            sum += sample * sample;
        }

        var rms = Math.Sqrt(sum / count);
        return rms <= 0 ? double.NegativeInfinity : 20 * Math.Log10(rms / 32768);
    }

    // the "data" chunk of a RIFF/WAVE file (FFmpeg writes "LIST" before it)
    private static ReadOnlySpan<byte> DataChunk(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return [];
        }

        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at + 4, 4)), (uint)(bytes.Length - at - 8));
            if (bytes.Slice(at, 4).SequenceEqual("data"u8))
            {
                return bytes.Slice(at + 8, size & ~1);
            }

            at += 8 + size + (size & 1);
        }

        return [];
    }

    private static bool IsClear(List<SpeechPiece> pieces)
    {
        var sure = pieces.Where(p => p.Language is not null && p.Probability >= MinimumProbability).ToList();
        return sure.Count >= 2 && sure.All(p => p.Language == sure[0].Language && p.Probability >= 0.7);
    }

    private static bool IsCzechOrSlovak(string language) => language is "cs" or "sk";

    private static string Counts(IEnumerable<(string Language, double Score, int Count)> scores) =>
        string.Join(", ", scores.Select(s => $"{Languages.Find(s.Language)?.SlovakName ?? s.Language} {s.Count}×"));

    private static int Threads => Math.Clamp(Environment.ProcessorCount, 1, 8);

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "neznáma chyba";

    [GeneratedRegex(@"auto-detected language:\s*(?<language>[a-z]{2,3})\s*\(p\s*=\s*(?<p>[0-9.]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Detected();
}
