using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using Universum.Core.Media;
using Universum.Core.Processes;
using Universum.Core.Text;

namespace Universum.Core.Speech;

/// <summary>What one piece of an audio track said: the language whisper heard and how sure it was.</summary>
/// <param name="Language">ISO 639-1 code ("cs"), or null when the piece was silent, empty or not recognised.</param>
public sealed record SpeechPiece(TimeSpan At, string? Language, double Probability, bool Silent = false);

/// <summary>The spoken language of an audio track decided from its pieces; always only a suggestion for the user.</summary>
/// <param name="Language">ISO 639-1 code of the language most pieces heard, or null when none can be told.</param>
/// <param name="Agreement">Share of the winning language in the sure pieces (0–1).</param>
/// <param name="Doubt">Why the suggestion should be checked carefully (Slovak), or null.</param>
public sealed record SpokenLanguageResult(string? Language, double Agreement, IReadOnlyList<SpeechPiece> Pieces, string? Doubt)
{
    /// <summary>Czech against Slovak by transcription of the clearest piece; null when not compared.</summary>
    public CzechSlovakComparison? Comparison { get; init; }

    /// <summary>How much surer whisper was of the Slovak than of the Czech transcription (negative = Czech).</summary>
    public double? SlovakMargin => Comparison?.Margin;
}

/// <summary>The clearest piece transcribed as Czech and as Slovak.</summary>
/// <param name="Margin">Mean log-probability of the words of the Slovak transcription minus that of the Czech one.</param>
public sealed record CzechSlovakComparison(double Margin, string CzechText, string SlovakText)
{
    /// <summary>
    /// The language both transcriptions are written in, by their words and letters (whisper writes much of what it
    /// hears: "som", "sa", ä/ô/ľ or "jsem", "se", ř/ě/ů) – cs or sk when they agree, otherwise null.
    /// </summary>
    public string? TextLanguage =>
        TextLanguageDetector.Detect(CzechText) is { } czech && czech == TextLanguageDetector.Detect(SlovakText) && czech is "cs" or "sk" ? czech : null;
}

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

    /// <summary>Czech and Slovak transcriptions closer than this (mean log-probability) leave the choice to the user.</summary>
    public const double ClearMargin = 0.08;

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
        (string Wave, double Probability)? clearest = null; // the surest Czech or Slovak piece, kept for the comparison
        try
        {
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

                    var heard = await runner.RunAsync(WhisperRequest(tools, wave), cancellationToken).ConfigureAwait(false);
                    if (ParseDetected(heard.StandardError + "\n" + heard.StandardOutput) is { } detected)
                    {
                        pieces.Add(new SpeechPiece(at, detected.Language, detected.Probability));
                        if (detected.Language is "cs" or "sk" && detected.Probability > (clearest?.Probability ?? 0))
                        {
                            if (clearest is { } previous)
                            {
                                File.Delete(previous.Wave);
                            }

                            clearest = (wave, detected.Probability);
                        }
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
                    if (clearest?.Wave != wave)
                    {
                        File.Delete(wave);
                    }

                    pieceDone?.Report(pieces.Count);
                }
            }

            if (pieces.All(p => p is { Language: null, Silent: false }) && problem is not null)
            {
                throw new SpeechException("Reč sa nepodarilo spracovať: " + problem);
            }

            var decided = Decide(pieces);
            if (decided.Language is "cs" or "sk" && clearest is { } piece)
            {
                try
                {
                    if (await CompareCzechSlovakAsync(tools, runner, piece.Wave, cancellationToken).ConfigureAwait(false) is { } comparison)
                    {
                        decided = WithComparison(decided, comparison);
                    }
                }
                catch (ProcessStartException ex)
                {
                    throw new SpeechException("Nástroj rozpoznávania reči sa nedá spustiť: " + ex.Message, ex);
                }
            }

            return decided;
        }
        finally
        {
            if (clearest is { } kept)
            {
                File.Delete(kept.Wave);
            }
        }
    }

    /// <summary>
    /// The comparison of the Czech and the Slovak transcription decides between the two: the language both are written
    /// in, or else a clear difference in whisper's certainty, sets the language (and removes the Czech-or-Slovak doubt;
    /// a changed language is noted); otherwise the vote's language stays with a doubt.
    /// </summary>
    public static SpokenLanguageResult WithComparison(SpokenLanguageResult decided, CzechSlovakComparison comparison)
    {
        const string czechOrSlovak = "čeština alebo slovenčina – overte";
        var rest = Without(decided.Doubt, czechOrSlovak + "; ") is { } r && r != czechOrSlovak ? r : null;
        var language = comparison.TextLanguage ?? (Math.Abs(comparison.Margin) >= ClearMargin ? (comparison.Margin > 0 ? "sk" : "cs") : null);
        if (language is null)
        {
            return decided with { Comparison = comparison, Doubt = Join(czechOrSlovak, rest) };
        }

        if (language != decided.Language)
        {
            rest = Join($"podľa prepisu {(language == "sk" ? "slovenčina, nie čeština" : "čeština, nie slovenčina")}", rest);
        }

        return decided with { Language = language, Comparison = comparison, Doubt = rest };
    }

    /// <summary>
    /// Czech or Slovak: the piece is transcribed as Czech and as Slovak, with whisper's certainty of each (mean
    /// log-probability of its words). Null when it cannot be compared – also when the model's folder has letters
    /// outside ASCII (whisper reads the model path as UTF-8, Windows passes the code page).
    /// </summary>
    public static async Task<CzechSlovakComparison?> CompareCzechSlovakAsync(SpeechTools tools, IProcessRunner runner, string wave, CancellationToken cancellationToken = default)
    {
        if (!tools.Model.All(char.IsAscii))
        {
            return null;
        }

        var transcripts = new Dictionary<string, (double Score, string Text)>();
        foreach (var language in new[] { "cs", "sk" })
        {
            // the transcription is written next to the piece, named relatively (the piece's folder is the working folder)
            var name = Path.GetFileNameWithoutExtension(wave) + "-" + language;
            var json = Path.Combine(Path.GetDirectoryName(wave)!, name + ".json");
            try
            {
                var result = await runner.RunAsync(TranscriptionRequest(tools, wave, language, name), cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0 || !File.Exists(json) || Transcript(await File.ReadAllTextAsync(json, cancellationToken).ConfigureAwait(false)) is not { } transcript)
                {
                    return null;
                }

                transcripts[language] = transcript;
            }
            finally
            {
                File.Delete(json);
            }
        }

        return new CzechSlovakComparison(transcripts["sk"].Score - transcripts["cs"].Score, transcripts["cs"].Text, transcripts["sk"].Text);
    }

    /// <summary>whisper.cpp: the piece transcribed in one language, with the probability of every word (JSON next to the piece).</summary>
    public static ProcessRequest TranscriptionRequest(SpeechTools tools, string wave, string language, string outputName) =>
        new(tools.Whisper, ["-m", tools.Model, "-f", "-", "-l", language, "-nt", "-ojf", "-of", outputName, "-t", Threads.ToString(CultureInfo.InvariantCulture)])
        {
            WorkingDirectory = Path.GetDirectoryName(wave),
            StandardInputFile = wave,
            Timeout = TimeSpan.FromMinutes(5),
        };

    /// <summary>Mean natural logarithm of the probability of the words of a whisper.cpp JSON (special tokens left out); null below 5 words.</summary>
    public static double? MeanLogProbability(string json) => Transcript(json)?.Score;

    /// <summary>
    /// The text of a whisper.cpp JSON transcription and the mean natural logarithm of the probability of its words
    /// (special tokens left out); null below 5 words or when it cannot be read.
    /// </summary>
    public static (double Score, string Text)? Transcript(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var logs = new List<double>();
            var text = new System.Text.StringBuilder();
            foreach (var segment in document.RootElement.GetProperty("transcription").EnumerateArray())
            {
                text.Append(segment.TryGetProperty("text", out var said) ? said.GetString() : "");
                if (!segment.TryGetProperty("tokens", out var tokens))
                {
                    continue;
                }

                foreach (var token in tokens.EnumerateArray())
                {
                    var piece = token.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (piece.StartsWith("[_", StringComparison.Ordinal) || !token.TryGetProperty("p", out var p))
                    {
                        continue;
                    }

                    logs.Add(Math.Log(Math.Max(p.GetDouble(), 1e-6)));
                }
            }

            return logs.Count >= 5 ? (logs.Average(), text.ToString().Trim()) : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Without(string? text, string part) =>
        text is null ? null : text.Replace(part, "", StringComparison.Ordinal) is { Length: > 0 } rest ? rest : null;

    private static string Join(string first, string? rest) => rest is null ? first : first + "; " + rest;

    /// <summary>FFmpeg: 30 seconds of the audio track from the position, mono 16 kHz WAV (what whisper reads).</summary>
    public static IReadOnlyList<string> CutArguments(string path, int audioOrdinal, TimeSpan at, string wave) =>
    [
        "-nostdin", "-hide_banner", "-loglevel", "error",
        "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
        "-map", "0:a:" + audioOrdinal.ToString(CultureInfo.InvariantCulture),
        "-t", PieceLength.TotalSeconds.ToString(CultureInfo.InvariantCulture),
        "-vn", "-sn", "-dn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", "-y", wave,
    ];

    /// <summary>
    /// whisper.cpp: only the language of the piece. On Windows it reads its arguments in the system code page and the
    /// model path as UTF-8, so no path goes in an argument (a user folder "Žltý kôň" would break it): the model is named
    /// in its own folder (the working folder) and the piece comes on the standard input.
    /// </summary>
    public static ProcessRequest WhisperRequest(SpeechTools tools, string wave) =>
        new(tools.Whisper, ["-m", Path.GetFileName(tools.Model), "-f", "-", "-dl", "-t", Threads.ToString(CultureInfo.InvariantCulture)])
        {
            WorkingDirectory = Path.GetDirectoryName(tools.Model),
            StandardInputFile = wave,
            Timeout = TimeSpan.FromMinutes(5),
        };

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
