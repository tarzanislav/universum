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

    /// <summary>The suggestion must be checked track by track ("… – overte"), not confirmed with the others at once.</summary>
    public bool NeedsOwnConfirmation => Doubt?.Contains("overte", StringComparison.Ordinal) == true;
}

/// <summary>The clearest piece transcribed as Czech and as Slovak.</summary>
/// <param name="Margin">Mean log-probability of the words of the Slovak transcription minus that of the Czech one.</param>
public sealed record CzechSlovakComparison(double Margin, string CzechText, string SlovakText)
{
    /// <summary>
    /// The language the words of both transcriptions belong to (se / sa, jsem / som, pro / pre…): whisper writes the
    /// words it hears, though its spelling leans to Czech (ů, ě also in Slovak speech); null when they do not tell.
    /// </summary>
    public string? TextLanguage => Heard is not null ? CzechOrSlovak.DecideWeighed(Heard) : CzechOrSlovak.Decide(CzechText, SlovakText);

    /// <summary>
    /// The accurate model's one transcription (owner, 1. 11. 2026: one run instead of two): its words, letters and
    /// endings decide; null for the quick model's two transcriptions.
    /// </summary>
    public string? Heard { get; init; }

    /// <summary>The language the words lean to, though not clearly (a suggestion with a doubt); null when they do not.</summary>
    public string? TextLeaning => Heard is not null ? CzechOrSlovak.DecideWeighed(Heard, 1.5) : CzechOrSlovak.Lean(CzechText, SlovakText);
}

/// <summary>What the recognition of a track does now.</summary>
public enum SpeechStage
{
    /// <summary>FFmpeg cuts the pieces of the audio track.</summary>
    Cutting,

    /// <summary>The quick model hears the language of each piece.</summary>
    Listening,

    /// <summary>The accurate model tells Czech from Slovak.</summary>
    CzechOrSlovak,

    /// <summary>Decided.</summary>
    Done,
}

/// <summary>How far the recognition of one track is: 0–1 and what it does now (owner, 1. 11. 2026: a gauge 0–100 %).</summary>
public readonly record struct SpeechProgress(double Fraction, SpeechStage Stage);

/// <summary>An audio track to be heard: the file, the number of the track among its audio tracks (0 = the first), its length.</summary>
public sealed record SpeechTrack(string Path, int AudioOrdinal, TimeSpan? Duration);

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
    /// <param name="progress">How far it is (0–1) and what it does, reported often.</param>
    /// <exception cref="SpeechException">A tool is missing, cannot start, does not finish in time, or a piece cannot be read.</exception>
    public static async Task<SpokenLanguageResult> DetectAsync(
        SpeechTools tools, IProcessRunner runner, string path, int audioOrdinal, TimeSpan? duration,
        IProgress<SpeechProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tools.Ffmpeg) || !File.Exists(tools.Whisper))
        {
            throw new SpeechException(CoreText.T("Nástroje rozpoznávania reči chýbajú – preinštalujte UNIVERSUM."));
        }

        // a new folder with a random name for the pieces and transcriptions: it is whisper's working folder when it
        // transcribes, and whisper also looks there for its libraries – nothing can be prepared in it in advance
        string folder;
        try
        {
            folder = Directory.CreateTempSubdirectory("UNIVERSUM-speech-").FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechException(CoreText.T("Dočasný priečinok pre úseky zvuku sa nedá vytvoriť: ") + ex.Message, ex);
        }

        try
        {
            var result = await DetectInAsync(folder, tools, runner, path, audioOrdinal, duration, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new SpeechProgress(1, SpeechStage.Done));
            return result;
        }
        catch (ProcessStartException ex)
        {
            throw new SpeechException(CoreText.T("Nástroj rozpoznávania reči sa nedá spustiť: ") + ex.Message, ex);
        }
        catch (TimeoutException ex)
        {
            throw new SpeechException(CoreText.T("Nástroj rozpoznávania reči neskončil včas (súbor je možno na pomalom disku alebo v sieti) – skúste to znova."), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechException(CoreText.T("Úsek zvuku sa nedá zapísať alebo prečítať: ") + ex.Message, ex);
        }
        finally
        {
            await DeleteFolderAsync(folder).ConfigureAwait(false);
        }
    }

    // a piece may still be held for a moment (a tool that was just stopped, an antivirus reading the new file): tried
    // again for a second; what stays is only in the temporary files
    private static async Task DeleteFolderAsync(string folder)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    // the share of the listening each part takes: the first three pieces cut and heard, the two more when they do not
    // agree (all of it when they do)
    private static readonly (double Cut, double Hear)[] Rounds = [(0.2, 0.6), (0.05, 0.15)];

    // the part of the gauge of one track the listening takes; the rest is Czech against Slovak when it is needed
    private const double ListeningShare = 0.3;

    // the accurate model weighs about this many times more than the listening of a track (for the gauge of many tracks)
    private const double AccurateWeight = 2.5;

    private static async Task<SpokenLanguageResult> DetectInAsync(
        string folder, SpeechTools tools, IProcessRunner runner, string path, int audioOrdinal, TimeSpan? duration,
        IProgress<SpeechProgress>? progress, CancellationToken cancellationToken)
    {
        var heard = await ListenAsync(folder, "", tools, runner, new SpeechTrack(path, audioOrdinal, duration), null,
            (fraction, stage) => progress?.Report(new SpeechProgress(ListeningShare * fraction, stage)), cancellationToken).ConfigureAwait(false);
        var decided = Settle(heard);
        if (NeedsComparison(heard, decided) is not { } wave)
        {
            return decided;
        }

        // Czech or Slovak – also when the pieces split between them – is compared by transcription: by the accurate model
        // (a few seconds of the clearest speech), by the quick one when the accurate one is not installed
        progress?.Report(new SpeechProgress(ListeningShare, SpeechStage.CzechOrSlovak));
        var comparison = tools.Accurate is not null
            ? await CompareAccurateAsync(tools, runner, wave, f => progress?.Report(new SpeechProgress(ListeningShare + ((1 - ListeningShare) * f), SpeechStage.CzechOrSlovak)), cancellationToken).ConfigureAwait(false)
            : await CompareCzechSlovakAsync(tools, runner, wave, cancellationToken).ConfigureAwait(false);
        return comparison is null ? decided : WithComparison(decided, comparison);
    }

    /// <summary>
    /// The languages of many audio tracks (owner, 1. 11. 2026: as fast as possible): the next track is cut while the
    /// last one is heard, and Czech against Slovak is told for all of them in one run of the accurate model at the end (it
    /// is loaded once). <paramref name="decided"/> gets each track's result as soon as it is final (the Czech or Slovak
    /// ones at the end); a track that cannot be heard gets a result without a language and with the reason.
    /// </summary>
    /// <param name="progress">The track worked on and how far the whole is (0–1), reported often.</param>
    /// <exception cref="SpeechException">A tool is missing, cannot start or does not finish in time.</exception>
    public static async Task<IReadOnlyList<SpokenLanguageResult>> DetectManyAsync(
        SpeechTools tools, IProcessRunner runner, IReadOnlyList<SpeechTrack> tracks, Action<int, SpokenLanguageResult>? decided = null,
        IProgress<(int Track, SpeechProgress Step)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tools.Ffmpeg) || !File.Exists(tools.Whisper))
        {
            throw new SpeechException(CoreText.T("Nástroje rozpoznávania reči chýbajú – preinštalujte UNIVERSUM."));
        }

        string folder;
        try
        {
            folder = Directory.CreateTempSubdirectory("UNIVERSUM-speech-").FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechException(CoreText.T("Dočasný priečinok pre úseky zvuku sa nedá vytvoriť: ") + ex.Message, ex);
        }

        var results = new SpokenLanguageResult[tracks.Count];
        Task<(string?[] Waves, string? Problem)>? next = null;
        try
        {
            // the gauge: every track's listening, then the accurate model for each track that needs it (at first counted
            // for every track; the second part shrinks to the tracks that need it)
            var units = tracks.Count * (1 + AccurateWeight);
            void Report(int track, double done, SpeechStage stage) => progress?.Report((track, new SpeechProgress(Math.Min(1, done / units), stage)));

            var waiting = new List<(int Track, string Wave, SpokenLanguageResult Vote)>();
            next = tracks.Count > 0 ? FirstCut(folder, tools, runner, tracks[0], 0, cancellationToken) : null;
            for (var t = 0; t < tracks.Count; t++)
            {
                var cut = next!;
                next = t + 1 < tracks.Count ? FirstCut(folder, tools, runner, tracks[t + 1], t + 1, cancellationToken) : null;
                var track = t;
                SpokenLanguageResult result;
                try
                {
                    var heard = await ListenAsync(folder, Prefix(t), tools, runner, tracks[t], cut,
                        (fraction, stage) => Report(track, track + fraction, stage), cancellationToken).ConfigureAwait(false);
                    result = Settle(heard);
                    if (NeedsComparison(heard, result) is { } wave)
                    {
                        if (tools.Accurate is not null)
                        {
                            // told with the others at the end
                            waiting.Add((t, wave, result));
                            continue;
                        }

                        Report(t, t + 1, SpeechStage.CzechOrSlovak);
                        if (await CompareCzechSlovakAsync(tools, runner, wave, cancellationToken).ConfigureAwait(false) is { } comparison)
                        {
                            result = WithComparison(result, comparison);
                        }
                    }
                }
                catch (SpeechException ex)
                {
                    result = new SpokenLanguageResult(null, 0, [], ex.Message);
                }

                results[t] = result;
                decided?.Invoke(t, result);
            }

            if (waiting.Count > 0)
            {
                var start = (double)tracks.Count;
                units = tracks.Count + (waiting.Count * AccurateWeight);
                Report(waiting[0].Track, start, SpeechStage.CzechOrSlovak);
                var heard = await HearAccuratelyAsync(tools, runner, folder, waiting.Select(w => w.Wave).ToList(),
                    (index, fraction) => Report(waiting[Math.Min(index, waiting.Count - 1)].Track, start + (AccurateWeight * fraction), SpeechStage.CzechOrSlovak),
                    cancellationToken).ConfigureAwait(false);
                for (var w = 0; w < waiting.Count; w++)
                {
                    var (track, _, vote) = waiting[w];
                    var result = heard[w] is { } text ? WithComparison(vote, new CzechSlovakComparison(0, text, text) { Heard = text }) : vote;
                    results[track] = result;
                    decided?.Invoke(track, result);
                }
            }

            progress?.Report((Math.Max(0, tracks.Count - 1), new SpeechProgress(1, SpeechStage.Done)));
            return results;
        }
        catch (ProcessStartException ex)
        {
            throw new SpeechException(CoreText.T("Nástroj rozpoznávania reči sa nedá spustiť: ") + ex.Message, ex);
        }
        catch (TimeoutException ex)
        {
            throw new SpeechException(CoreText.T("Nástroj rozpoznávania reči neskončil včas (súbor je možno na pomalom disku alebo v sieti) – skúste to znova."), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechException(CoreText.T("Úsek zvuku sa nedá zapísať alebo prečítať: ") + ex.Message, ex);
        }
        finally
        {
            // a cut started ahead finishes (or stops) before its folder goes
            if (next is not null)
            {
                try
                {
                    await next.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ProcessStartException or TimeoutException or IOException or UnauthorizedAccessException)
                {
                    // the track was not going to be heard
                }
            }

            await DeleteFolderAsync(folder).ConfigureAwait(false);
        }
    }

    // the pieces of a track named apart from the others in the one folder
    private static string Prefix(int track) => $"t{track + 1}-";

    // the first three pieces of a track, cut ahead (while the track before is heard)
    private static Task<(string?[] Waves, string? Problem)> FirstCut(string folder, SpeechTools tools, IProcessRunner runner, SpeechTrack track, int index, CancellationToken cancellationToken) =>
        CutAsync(folder, Prefix(index), tools, runner, track.Path, track.AudioOrdinal, Positions(track.Duration).Take(3).ToList(), 0, cancellationToken);

    // what the pieces of a track heard: the pieces, the first complaint of a tool, the clearest Czech or Slovak piece
    private sealed record Heard(List<SpeechPiece> Pieces, string? Problem, string? Clearest);

    // the pieces of one track cut (the first three may be cut already) and heard by the quick model; the fraction of the
    // listening reported (0–1)
    private static async Task<Heard> ListenAsync(
        string folder, string prefix, SpeechTools tools, IProcessRunner runner, SpeechTrack track, Task<(string?[] Waves, string? Problem)>? firstCut,
        Action<double, SpeechStage> report, CancellationToken cancellationToken)
    {
        var positions = Positions(track.Duration);
        var pieces = new List<SpeechPiece>();
        string? problem = null;
        (string Wave, double Probability)? clearest = null; // the surest Czech or Slovak piece, kept for the comparison
        var gauge = 0.0;
        for (var round = 0; round < Rounds.Length; round++)
        {
            // the first three pieces at once; the two more only when three do not agree clearly
            var batch = (round == 0 ? positions.Take(3) : positions.Skip(3)).ToList();
            if (batch.Count == 0 || (round > 0 && IsClear(pieces)))
            {
                break;
            }

            var (cutShare, hearShare) = Rounds[round];
            report(gauge, SpeechStage.Cutting);
            var (waves, cutProblem) = round == 0 && firstCut is not null
                ? await firstCut.ConfigureAwait(false)
                : await CutAsync(folder, prefix, tools, runner, track.Path, track.AudioOrdinal, batch, pieces.Count, cancellationToken).ConfigureAwait(false);
            problem ??= cutProblem;
            gauge += cutShare;
            report(gauge, SpeechStage.Listening);

            // silence (or nothing after the end of a shorter film) is not given to whisper
            var loud = new List<string>();
            var silent = new HashSet<string>(StringComparer.Ordinal);
            foreach (var wave in waves.OfType<string>())
            {
                if (Loudness(wave) is { } decibels && decibels >= SilenceDecibels)
                {
                    loud.Add(wave);
                }
                else
                {
                    silent.Add(wave);
                }
            }

            var start = gauge;
            var (heard, hearProblem) = await HearAsync(tools, runner, folder, loud,
                count => report(start + (hearShare * count / Math.Max(1, loud.Count)), SpeechStage.Listening), cancellationToken).ConfigureAwait(false);
            problem ??= hearProblem;
            gauge = start + hearShare;
            for (var i = 0; i < batch.Count; i++)
            {
                var wave = waves[i];
                if (wave is null)
                {
                    pieces.Add(new SpeechPiece(batch[i], null, 0));
                }
                else if (silent.Contains(wave))
                {
                    pieces.Add(new SpeechPiece(batch[i], null, 0, Silent: true));
                }
                else if (heard.TryGetValue(wave, out var detected))
                {
                    pieces.Add(new SpeechPiece(batch[i], detected.Language, detected.Probability));
                    if (detected.Language is "cs" or "sk" && detected.Probability > (clearest?.Probability ?? 0))
                    {
                        clearest = (wave, detected.Probability);
                    }
                }
                else
                {
                    pieces.Add(new SpeechPiece(batch[i], null, 0));
                }
            }

            report(gauge, SpeechStage.Listening);
        }

        report(1, SpeechStage.Listening);
        return new Heard(pieces, problem, clearest?.Wave);
    }

    // the vote of the pieces; nothing heard because of a tool: its complaint
    private static SpokenLanguageResult Settle(Heard heard) =>
        heard.Pieces.All(p => p is { Language: null, Silent: false }) && heard.Problem is not null
            ? throw new SpeechException(CoreText.T("Reč sa nepodarilo spracovať: ") + heard.Problem)
            : Decide(heard.Pieces);

    // Czech or Slovak – also when the pieces split between them: the piece to compare them on
    private static string? NeedsComparison(Heard heard, SpokenLanguageResult decided) =>
        heard.Clearest is { } wave && (decided.Language is "cs" or "sk" || CzechAndSlovakShare(heard.Pieces) > 0.5) ? wave : null;

    /// <summary>
    /// The accurate model writes down the clearest seconds of speech of many pieces in one run (loaded once), as Czech
    /// with the encoder looking only at those seconds: the transcription of each piece (null when it could not be done).
    /// </summary>
    private static async Task<string?[]> HearAccuratelyAsync(
        SpeechTools tools, IProcessRunner runner, string folder, IReadOnlyList<string> waves, Action<int, double> report, CancellationToken cancellationToken)
    {
        var texts = new string?[waves.Count];
        if (tools.Accurate is not { } model || !model.All(char.IsAscii))
        {
            // the model's path cannot be given to whisper: one by one by the quick model
            for (var i = 0; i < waves.Count; i++)
            {
                report(i, (double)i / waves.Count);
                texts[i] = await CompareCzechSlovakAsync(tools, runner, waves[i], cancellationToken).ConfigureAwait(false) is { } comparison
                    ? comparison.CzechText + " " + comparison.SlovakText
                    : null;
            }

            return texts;
        }

        var clips = new List<(int Index, string Clip, double Seconds)>();
        for (var i = 0; i < waves.Count; i++)
        {
            var clip = Path.Combine(folder, Path.GetFileNameWithoutExtension(waves[i]) + "-speech.wav");
            if (SpeechClip(waves[i], clip, ClipSeconds) is { } seconds)
            {
                clips.Add((i, clip, seconds));
            }
        }

        if (clips.Count == 0)
        {
            return texts;
        }

        var arguments = new List<string>
        {
            "-m", model, "-l", "cs", "-ac", AudioContext(clips.Max(c => c.Seconds)).ToString(CultureInfo.InvariantCulture),
            "-bs", "1", "-bo", "1", "-nf", "-nt", "-ojf", "-t", Threads.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (_, clip, _) in clips)
        {
            arguments.AddRange(["-f", Path.GetFileName(clip), "-of", Path.GetFileNameWithoutExtension(clip) + "-heard"]);
        }

        // each clip reported as whisper starts it; between them by the time one takes
        var current = 0;
        var request = new ProcessRequest(tools.Whisper, arguments)
        {
            WorkingDirectory = folder,
            Timeout = TimeSpan.FromMinutes(2) * clips.Count,
            Background = true,
            OnErrorLine = line =>
            {
                if (Processing().Match(line) is { Success: true } file && clips.FindIndex(c => Path.GetFileName(c.Clip) == file.Groups["file"].Value) is var at and >= 0)
                {
                    current = at;
                }
            },
        };
        await Timed(runner, request, f => report(clips[Math.Min(current, clips.Count - 1)].Index, Math.Min(f, (current + 1.0) / clips.Count)), cancellationToken, clips.Count).ConfigureAwait(false);
        foreach (var (index, clip, _) in clips)
        {
            var json = Path.Combine(folder, Path.GetFileNameWithoutExtension(clip) + "-heard.json");
            if (File.Exists(json) && Transcript(await File.ReadAllTextAsync(json, cancellationToken).ConfigureAwait(false)) is { } transcript)
            {
                texts[index] = transcript.Text;
            }
        }

        return texts;
    }

    /// <summary>
    /// FFmpeg cuts the pieces of the track at the positions in one run (the file opened once for each of them): their
    /// waves in the order of the positions, null for a piece that was not written, and FFmpeg's complaint.
    /// </summary>
    private static async Task<(string?[] Waves, string? Problem)> CutAsync(
        string folder, string prefix, SpeechTools tools, IProcessRunner runner, string path, int audioOrdinal, IReadOnlyList<TimeSpan> positions, int first,
        CancellationToken cancellationToken)
    {
        var waves = positions.Select((_, i) => Path.Combine(folder, $"{prefix}piece-{first + i + 1}.wav")).ToList();
        var cut = await runner.RunAsync(new ProcessRequest(tools.Ffmpeg, CutArguments(path, audioOrdinal, positions, waves))
        {
            Timeout = TimeSpan.FromMinutes(3) * positions.Count,
            Background = true,
        }, cancellationToken).ConfigureAwait(false);
        var written = waves.Select(w => File.Exists(w) ? w : null).ToArray();
        return (written, cut.ExitCode != 0 || written.Any(w => w is null) ? LastLine(cut.StandardError) : null);
    }

    /// <summary>
    /// The quick model hears the language of the pieces – all of them in one run of whisper (the model loaded once),
    /// each reported as it is heard; one run for each piece when the model's path cannot be given to whisper (letters
    /// outside ASCII).
    /// </summary>
    private static async Task<(Dictionary<string, (string Language, double Probability)> Heard, string? Problem)> HearAsync(
        SpeechTools tools, IProcessRunner runner, string folder, IReadOnlyList<string> waves, Action<int> heardCount, CancellationToken cancellationToken)
    {
        var heard = new Dictionary<string, (string Language, double Probability)>(StringComparer.Ordinal);
        if (waves.Count == 0)
        {
            return (heard, null);
        }

        if (tools.Model.All(char.IsAscii))
        {
            string? current = null;
            var request = ListenRequest(tools, folder, waves.Select(Path.GetFileName).OfType<string>().ToList()) with
            {
                OnErrorLine = line =>
                {
                    if (Processing().Match(line) is { Success: true } file)
                    {
                        current = file.Groups["file"].Value;
                    }
                    else if (current is not null && ParseDetected(line) is { } detected)
                    {
                        lock (heard)
                        {
                            heard[Path.Combine(folder, current)] = detected;
                            heardCount(heard.Count);
                        }
                    }
                },
            };
            var result = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            return (heard, heard.Count < waves.Count ? LastLine(result.StandardError) : null);
        }

        string? problem = null;
        foreach (var wave in waves)
        {
            var result = await runner.RunAsync(WhisperRequest(tools, wave), cancellationToken).ConfigureAwait(false);
            if (ParseDetected(result.StandardError + "\n" + result.StandardOutput) is { } detected)
            {
                heard[wave] = detected;
            }
            else
            {
                problem ??= LastLine(result.StandardError);
            }

            heardCount(heard.Count);
        }

        return (heard, problem);
    }

    /// <summary>whisper.cpp: the language of each piece, all in one run; the pieces named in their folder (the working folder).</summary>
    public static ProcessRequest ListenRequest(SpeechTools tools, string folder, IReadOnlyList<string> names) =>
        new(tools.Whisper, ["-m", tools.Model, "-dl", "-t", Threads.ToString(CultureInfo.InvariantCulture), .. names.SelectMany(n => new[] { "-f", n })])
        {
            WorkingDirectory = folder,
            Timeout = TimeSpan.FromMinutes(2) * Math.Max(1, names.Count),
            Background = true,
        };

    /// <summary>
    /// Czech against Slovak by the accurate model: the few seconds of the piece with the most speech, written down once
    /// (as Czech – the accurate model keeps the words it hears, Slovak ones too, while a Slovak hint pulls Czech speech to
    /// Slovak words) with the encoder looking only at those seconds; the words, letters and endings of the transcription
    /// decide (<see cref="CzechOrSlovak.Weigh"/>). Null when it cannot be compared.
    /// </summary>
    public static async Task<CzechSlovakComparison?> CompareAccurateAsync(
        SpeechTools tools, IProcessRunner runner, string wave, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (tools.Accurate is not { } model || !model.All(char.IsAscii))
        {
            return await CompareCzechSlovakAsync(tools, runner, wave, cancellationToken).ConfigureAwait(false);
        }

        var folder = Path.GetDirectoryName(wave)!;
        var clip = Path.Combine(folder, Path.GetFileNameWithoutExtension(wave) + "-speech.wav");
        if (SpeechClip(wave, clip, ClipSeconds) is not { } seconds)
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(clip) + "-heard";
        var json = Path.Combine(folder, name + ".json");
        try
        {
            var request = new ProcessRequest(tools.Whisper,
            [
                "-m", model, "-f", Path.GetFileName(clip), "-l", "cs", "-ac", AudioContext(seconds).ToString(CultureInfo.InvariantCulture),
                "-bs", "1", "-bo", "1", "-nf", "-nt", "-ojf", "-of", name, "-t", Threads.ToString(CultureInfo.InvariantCulture),
            ])
            {
                WorkingDirectory = folder,
                Timeout = TimeSpan.FromMinutes(5),
                Background = true,
            };
            var result = await Timed(runner, request, f => progress?.Invoke(f), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(json) || Transcript(await File.ReadAllTextAsync(json, cancellationToken).ConfigureAwait(false)) is not { } transcript)
            {
                return null;
            }

            return new CzechSlovakComparison(0, transcript.Text, transcript.Text) { Heard = transcript.Text };
        }
        finally
        {
            File.Delete(json);
        }
    }

    /// <summary>The seconds of speech the accurate model hears (enough words to tell Czech from Slovak, little work).</summary>
    public const double ClipSeconds = 6;

    /// <summary>
    /// The encoder's window for so many seconds of sound (50 positions a second, a little more for its edge; at most the
    /// whole 30-second window of 1500).
    /// </summary>
    public static int AudioContext(double seconds) => Math.Clamp((int)Math.Ceiling(seconds * 50) + 24, 64, 1500);

    // how long a run of the accurate model takes on this computer (measured, for the gauge between its reports)
    private static double _accurateSeconds = 6;

    // a run reported on the gauge while it lasts: by the time the last run took, never past 95 % before it ends
    private static async Task<ProcessResult> Timed(IProcessRunner runner, ProcessRequest request, Action<double> fraction, CancellationToken cancellationToken, int clips = 1)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var stop = new CancellationTokenSource();
        var ticking = Task.Run(async () =>
        {
            try
            {
                while (!stop.Token.IsCancellationRequested)
                {
                    fraction(Math.Min(0.95, watch.Elapsed.TotalSeconds / Math.Max(0.5, _accurateSeconds * clips)));
                    await Task.Delay(200, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // the run ended
            }
        }, CancellationToken.None);
        try
        {
            var result = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                _accurateSeconds = (0.5 * _accurateSeconds) + (0.5 * watch.Elapsed.TotalSeconds / clips);
            }

            return result;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await ticking.ConfigureAwait(false);
            fraction(1);
        }
    }

    /// <summary>
    /// The window of the piece with the most speech – the most tenths of a second loud against the piece's loudest (not
    /// silence, not a quiet background), written as a WAV of its own; its length in seconds, null when the piece is
    /// shorter than a second.
    /// </summary>
    public static double? SpeechClip(string wave, string clip, double seconds)
    {
        var bytes = File.ReadAllBytes(wave);
        var data = DataChunk(bytes);
        const int rate = 16000;
        const int frame = rate / 10;
        var frames = data.Length / 2 / frame;
        if (frames < 10)
        {
            return null;
        }

        var energy = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var i = 0; i < frame; i++)
            {
                double sample = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(((f * frame) + i) * 2, 2));
                sum += sample * sample;
            }

            energy[f] = sum / frame;
        }

        // speech: within 25 dB of the loudest tenth, and not silence
        var loudest = energy.Max();
        var threshold = Math.Max(loudest / 316.0, 32768.0 * 32768.0 * Math.Pow(10, SilenceDecibels / 10));
        var length = Math.Min(frames, (int)Math.Round(seconds * 10));
        var spoken = energy.Select(e => e >= threshold ? 1 : 0).ToArray();
        var count = spoken.Take(length).Sum();
        var (best, bestCount) = (0, count);
        for (var start = 1; start + length <= frames; start++)
        {
            count += spoken[start + length - 1] - spoken[start - 1];
            if (count > bestCount)
            {
                (best, bestCount) = (start, count);
            }
        }

        var samples = data.Slice(best * frame * 2, length * frame * 2);
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + samples.Length);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), samples.Length);
        using (var output = File.Create(clip))
        {
            output.Write(header);
            output.Write(samples);
        }

        return length / 10.0;
    }

    /// <summary>Share of Czech and Slovak together in the vote of the sure pieces (0–1).</summary>
    public static double CzechAndSlovakShare(IReadOnlyList<SpeechPiece> pieces)
    {
        var sure = pieces.Where(p => p.Language is not null && p.Probability >= MinimumProbability).ToList();
        var total = sure.Sum(p => p.Probability);
        return total <= 0 ? 0 : sure.Where(p => p.Language is "cs" or "sk").Sum(p => p.Probability) / total;
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
        var byCertainty = Math.Abs(comparison.Margin) >= ClearMargin ? (comparison.Margin > 0 ? "sk" : "cs") : null;
        var language = comparison.TextLanguage ?? byCertainty ?? comparison.TextLeaning;
        if (language is null)
        {
            return decided with { Comparison = comparison, Doubt = Join(czechOrSlovak, rest) };
        }

        // the words say one, whisper's certainty clearly the other – or the words only lean, or the pieces split between
        // Czech and Slovak: the user checks
        if ((byCertainty is not null && byCertainty != language) || (comparison.TextLanguage is null && byCertainty is null) || decided.Language is null)
        {
            rest = Join(czechOrSlovak, rest);
        }

        if (decided.Language is not null && language != decided.Language)
        {
            rest = Join(CoreText.T(language == "sk" ? "podľa prepisu slovenčina, nie čeština" : "podľa prepisu čeština, nie slovenčina"), rest);
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

    /// <summary>
    /// whisper.cpp: the piece transcribed in one language, with the probability of every word (JSON next to the piece) –
    /// by the quick model, or by <paramref name="model"/>; the surest words one by one (no search of alternatives, no
    /// second try at a higher temperature: several times less work).
    /// </summary>
    public static ProcessRequest TranscriptionRequest(SpeechTools tools, string wave, string language, string outputName, string? model = null) =>
        new(tools.Whisper,
        [
            "-m", model ?? tools.Model, "-f", "-", "-l", language, "-bs", "1", "-bo", "1", "-nf", "-nt", "-ojf", "-of", outputName,
            "-t", Threads.ToString(CultureInfo.InvariantCulture),
        ])
        {
            WorkingDirectory = Path.GetDirectoryName(wave),
            StandardInputFile = wave,
            Timeout = TimeSpan.FromMinutes(5),
            Background = true,
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
        CutArguments(path, audioOrdinal, [at], [wave]);

    /// <summary>
    /// FFmpeg: 30 seconds of the audio track from each position, each a mono 16 kHz WAV – in one run (the file opened
    /// once for each position, read only around it).
    /// </summary>
    public static IReadOnlyList<string> CutArguments(string path, int audioOrdinal, IReadOnlyList<TimeSpan> positions, IReadOnlyList<string> waves)
    {
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error" };
        foreach (var at in positions)
        {
            arguments.AddRange(["-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", path]);
        }

        for (var i = 0; i < positions.Count; i++)
        {
            arguments.AddRange(
            [
                "-map", i.ToString(CultureInfo.InvariantCulture) + ":a:" + audioOrdinal.ToString(CultureInfo.InvariantCulture),
                "-t", PieceLength.TotalSeconds.ToString(CultureInfo.InvariantCulture),
                "-vn", "-sn", "-dn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", "-y", waves[i],
            ]);
        }

        return arguments;
    }

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
            Background = true,
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
            var why = pieces.Count > 0 && pieces.All(p => p.Silent) ? CoreText.T("v úsekoch nie je reč (ticho)") : CoreText.T("reč sa nedala spoľahlivo rozpoznať (hudba alebo šum?)");
            return new SpokenLanguageResult(null, 0, pieces, why);
        }

        var scores = sure.GroupBy(p => p.Language!).Select(g => (Language: g.Key, Score: g.Sum(p => p.Probability), Count: g.Count()))
            .OrderByDescending(s => s.Score).ToList();
        var total = scores.Sum(s => s.Score);
        var winner = scores[0];
        var agreement = winner.Score / total;
        if (agreement <= 0.5)
        {
            return new SpokenLanguageResult(null, agreement, pieces, CoreText.T("úseky sa nezhodli: ") + Counts(scores));
        }

        string? doubt = null;
        if (scores.Count > 1)
        {
            doubt = (IsCzechOrSlovak(winner.Language) && scores.Any(s => s.Language != winner.Language && IsCzechOrSlovak(s.Language))
                ? "čeština alebo slovenčina – overte; "
                : "") + CoreText.T("úseky: ") + Counts(scores);
        }
        else if (sure.Count == 1)
        {
            doubt = CoreText.T("rozpoznané len z jedného úseku");
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
        string.Join(", ", scores.Select(s => $"{Languages.Find(s.Language)?.Name ?? s.Language} {s.Count}×"));

    // whisper's threads: every processor up to eight (more do not help it) – below normal priority, so whatever the user
    // does (UNIVERSUM's menus too) gets the processors first and nothing freezes (owner, 1. 11. 2026)
    private static int Threads => Math.Clamp(Environment.ProcessorCount, 1, 8);

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? CoreText.T("neznáma chyba");

    // "main: processing 'piece-2.wav' (480000 samples, 30.0 sec), …": the file whisper hears now
    [GeneratedRegex(@"processing '(?<file>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex Processing();

    [GeneratedRegex(@"auto-detected language:\s*(?<language>[a-z]{2,3})\s*\(p\s*=\s*(?<p>[0-9.]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Detected();
}
