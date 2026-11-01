using System.Buffers.Binary;
using Universum.Core.Batch;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;
using Universum.Core.Speech;

namespace Universum.Tests;

/// <summary>Recognition of the spoken language: pieces, their vote, the bundled tools and (on Windows CI) the real thing.</summary>
public sealed class SpeechTests
{
    [Test]
    public void PiecesVote()
    {
        static SpeechPiece P(string? language, double p, bool silent = false) => new(TimeSpan.Zero, language, p, silent);

        static CzechSlovakComparison Compared(double margin, string czech = "", string slovak = "") => new(margin, czech, slovak);
        Assert.True(SpokenLanguage.WithComparison(SpokenLanguage.Decide([P("cs", 0.9), P("cs", 0.9)]), Compared(0.3)) is { Language: "sk", SlovakMargin: 0.3 }, "the certainty decides");
        var agreed = SpokenLanguage.WithComparison(SpokenLanguage.Decide([P("cs", 0.8), P("sk", 0.6), P("cs", 0.7)]), Compared(-0.3));
        Assert.True(agreed is { Language: "cs", Doubt: "úseky: čeština 2×, slovenčina 1×" }, "a clear Czech transcription removes the doubt – " + agreed.Doubt);

        // both transcriptions written in Slovak (whisper writes what it hears): Slovak, though the certainty is close
        const string slovakText = "Dobrý večer, vitajte pri dnešnom príbehu. Starý mlynár sa pozerá na rieku a hovorí, že som tu len ja, ale ešte nie je " +
                                  "neskoro, ako to tu teraz je, čo sa stalo a prečo to tak je, keď už tu je.";
        var written = SpokenLanguage.WithComparison(SpokenLanguage.Decide([P("cs", 0.85)]), Compared(0.01, slovakText, slovakText));
        Assert.True(written is { Language: "sk", Doubt: "podľa prepisu slovenčina, nie čeština; rozpoznané len z jedného úseku" }, "the written language decides – " + written.Doubt);
        // the words are Slovak though whisper spelled them with ů and ě (as it does for Slovak speech it takes for Czech)
        const string spelledCzech = "vytajte pri dnešnom príbehu. Malá dedina sa prebůdza, starý mlynář sa pozerá na řeku a hovorí, že dnes sa musí " +
                                    "opraviť koleso, inak nebůde múka. Syn sa usmeje a ide pre vodu, keď je ešte tma.";
        Assert.Equal("sk", new CzechSlovakComparison(-0.03, spelledCzech, spelledCzech).TextLanguage, "words over spelling");
        Assert.Equal("cs", new CzechSlovakComparison(0, "Malá vesnice se probouzí, mlynář se dívá na řeku a říká, že jsem tady jen já, protože když…", "").TextLanguage);
        Assert.Equal<string?>(null, new CzechSlovakComparison(0, "kapor v rybníku pláva", "").TextLanguage, "no telling words");
        var leaning = SpokenLanguage.WithComparison(SpokenLanguage.Decide([P("cs", 0.85)]), Compared(-0.03, "sa sa sa pre pre sa", "se se jsme mě"));
        Assert.True(leaning is { Language: "sk" } && leaning.Doubt!.Contains("čeština alebo slovenčina – overte", StringComparison.Ordinal) &&
                    leaning.Doubt.Contains("podľa prepisu slovenčina", StringComparison.Ordinal),
            "the words lean to Slovak (6 : 4): Slovak to check – " + leaning.Doubt);
        var disputed = SpokenLanguage.WithComparison(SpokenLanguage.Decide([P("cs", 0.9), P("cs", 0.9)]), Compared(-0.3, spelledCzech, spelledCzech));
        Assert.True(disputed is { Language: "sk" } && disputed.Doubt!.Contains("čeština alebo slovenčina – overte", StringComparison.Ordinal), "words against certainty – " + disputed.Doubt);
        Assert.True(SpokenLanguage.WithComparison(SpokenLanguage.Decide([P("cs", 0.85)]), Compared(0.01)) is { Language: "cs" } unsure &&
                    unsure.Doubt == "čeština alebo slovenčina – overte; rozpoznané len z jedného úseku", "nothing tells them apart");
        Assert.Equal<double?>(null, SpokenLanguage.MeanLogProbability("""{"transcription":[{"text":" a","tokens":[{"text":" a","p":0.5}]}]}"""), "too few words");
        Assert.Equal<double?>(null, SpokenLanguage.MeanLogProbability("not json"));

        var clear = SpokenLanguage.Decide([P("cs", 0.95), P("cs", 0.9), P("cs", 0.88)]);
        Assert.Equal("cs", clear.Language);
        Assert.Equal<string?>(null, clear.Doubt, "all agree");

        var mixed = SpokenLanguage.Decide([P("cs", 0.8), P("sk", 0.6), P("cs", 0.7)]);
        Assert.Equal("cs", mixed.Language);
        Assert.True(mixed.Doubt?.StartsWith("čeština alebo slovenčina", StringComparison.Ordinal) == true, mixed.Doubt ?? "no doubt");
        Assert.True(mixed.Doubt!.Contains("čeština 2×", StringComparison.Ordinal) && mixed.Doubt.Contains("slovenčina 1×", StringComparison.Ordinal), mixed.Doubt);

        Assert.True(SpokenLanguage.Decide([P("en", 0.97)]) is { Language: "en", Doubt: "rozpoznané len z jedného úseku" }, "one piece");
        Assert.True(SpokenLanguage.Decide([P(null, 0, true), P(null, 0, true)]) is { Language: null, Doubt: "v úsekoch nie je reč (ticho)" }, "silence");
        Assert.True(SpokenLanguage.Decide([P("en", 0.2), P("de", 0.3)]) is { Language: null, Doubt: not null }, "music: nobody is sure");
        Assert.True(SpokenLanguage.Decide([P("en", 0.8), P("de", 0.8)]) is { Language: null } tie && tie.Doubt!.StartsWith("úseky sa nezhodli", StringComparison.Ordinal), "a tie");
        Assert.True(SpokenLanguage.Decide([P("en", 0.9), P("en", 0.9), P("de", 0.5), P(null, 0, true)]) is { Language: "en", Doubt: "úseky: angličtina 2×, nemčina 1×" }, "a stray piece");
    }

    [Test]
    public void WhisperAndPositions()
    {
        Assert.Equal(("cs", 0.912345), SpokenLanguage.ParseDetected("whisper_init: ...\nwhisper_full_with_state: auto-detected language: cs (p = 0.912345)\n"));
        Assert.Equal<(string, double)?>(null, SpokenLanguage.ParseDetected("error: failed to read audio"));

        Assert.Sequence([TimeSpan.Zero], SpokenLanguage.Positions(TimeSpan.FromSeconds(29)), "a short file: one piece");
        Assert.Sequence(
            [TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(80), TimeSpan.FromMinutes(35), TimeSpan.FromMinutes(65)],
            SpokenLanguage.Positions(TimeSpan.FromMinutes(100)), "across the film");
        Assert.Equal(TimeSpan.FromMinutes(1), SpokenLanguage.Positions(null)[0], "unknown length");
        Assert.True(SpokenLanguage.Positions(TimeSpan.FromMinutes(2)).All(p => p <= TimeSpan.FromSeconds(90)), "a piece never starts in the last 30 s");

        var arguments = SpokenLanguage.CutArguments("film.mkv", 1, TimeSpan.FromSeconds(754.5), "piece.wav");
        Assert.True(string.Join(' ', arguments).Contains("-ss 754.5 -i film.mkv -map 0:a:1 -t 30", StringComparison.Ordinal), string.Join(' ', arguments));

        // no path in whisper's arguments (Windows reads them in the system code page): the model in its folder, the piece on the input
        var tools = new SpeechTools(Path.Combine("C:", "Používatelia", "Žltý kôň"), "ffmpeg.exe", "whisper-cli.exe", Path.Combine("C:", "Žltý kôň", "models", "ggml-base.bin"));
        var whisper = SpokenLanguage.WhisperRequest(tools, Path.Combine("C:", "Žltý kôň", "piece.wav"));
        Assert.True(whisper.Arguments.All(a => a.All(char.IsAscii)), string.Join(' ', whisper.Arguments));
        Assert.Equal(Path.Combine("C:", "Žltý kôň", "models"), whisper.WorkingDirectory);
        Assert.Equal(Path.Combine("C:", "Žltý kôň", "piece.wav"), whisper.StandardInputFile);
    }

    [Test]
    public async Task PiecesAreCutHeardAndCounted()
    {
        var folder = NewFolder();
        try
        {
            var tools = FakeTools(folder);
            var runner = new StandIn(tools, at => at < 1500 ? 0.0 : 0.3, "cs", 0.93); // the first piece (20 %) is silent
            var result = await SpokenLanguage.DetectAsync(tools, runner, "film.mkv", 1, TimeSpan.FromMinutes(100));
            Assert.Equal("cs", result.Language);
            Assert.Equal(1, result.Pieces.Count(p => p.Silent), "the silent piece");
            Assert.Equal(2, runner.Heard, "silence is not given to whisper; after three pieces two that agree are enough");
            Assert.True(runner.Runs == 1 && runner.Cuts.Count == 1, $"the three pieces cut in one run of FFmpeg and heard in one run of whisper ({runner.Cuts.Count}, {runner.Runs})");
            Assert.True(runner.Cuts.All(c => c.Contains("0:a:1", StringComparison.Ordinal)), "the second audio track");

            var unsure = new StandIn(tools, _ => 0.3, "sk", 0.55);
            var slovak = await SpokenLanguage.DetectAsync(tools, unsure, "film.mkv", 0, TimeSpan.FromMinutes(100));
            Assert.Equal("sk", slovak.Language);
            Assert.Equal(5, unsure.Heard, "not sure enough: all five pieces");
            Assert.True(unsure.Runs == 2 && unsure.Cuts.Count == 2, "two more pieces in a second run");
            Assert.True(!Directory.EnumerateDirectories(Path.GetTempPath(), "UNIVERSUM-speech-*").Any(), "the folder of the pieces is deleted");

            // Slovak heard as Czech: the Slovak transcription is surer of its words – Slovak, with a note
            var dubbed = new StandIn(tools, _ => 0.3, "cs", 0.62, l => l == "sk" ? 0.8 : 0.6);
            var fixedUp = await SpokenLanguage.DetectAsync(tools, dubbed, "film.mkv", 0, TimeSpan.FromMinutes(100));
            Assert.Equal("sk", fixedUp.Language);
            Assert.Sequence(["cs", "sk"], dubbed.Transcribed, "the clearest piece transcribed in both");
            Assert.True(fixedUp.SlovakMargin > 0.2 && fixedUp.Doubt?.StartsWith("podľa prepisu slovenčina, nie čeština", StringComparison.Ordinal) == true, fixedUp.Doubt ?? "no doubt");
            Assert.True(!Directory.EnumerateDirectories(Path.GetTempPath(), "UNIVERSUM-speech-*").Any(), "the kept piece and the transcriptions are deleted");

            // the pieces split between Czech and Slovak: the transcriptions are compared all the same – a suggestion to check
            var split = new StandIn(tools, _ => 0.3, null, 0, l => l == "sk" ? 0.8 : 0.6) { Languages = ["cs", "sk", "cs", "sk", "en"] };
            var splitResult = await SpokenLanguage.DetectAsync(tools, split, "film.mkv", 0, TimeSpan.FromMinutes(100));
            Assert.True(splitResult is { Language: "sk", NeedsOwnConfirmation: true } && split.Transcribed.Count == 2, $"split vote: {splitResult.Language} {splitResult.Doubt}");
            var close = await SpokenLanguage.DetectAsync(tools, new StandIn(tools, _ => 0.3, "cs", 0.9, l => l == "sk" ? 0.71 : 0.7), "film.mkv", 0, TimeSpan.FromMinutes(100));
            Assert.True(close is { Language: "cs", Doubt: not null } && close.Doubt.StartsWith("čeština alebo slovenčina – overte", StringComparison.Ordinal), "too close: " + close.Doubt);
            var english = new StandIn(tools, _ => 0.3, "en", 0.9, _ => 0.9);
            await SpokenLanguage.DetectAsync(tools, english, "film.mkv", 0, TimeSpan.FromMinutes(100));
            Assert.Equal(0, english.Transcribed.Count, "only Czech or Slovak is transcribed");

            var broken = new StandIn(tools, _ => 0.3, null, 0);
            await Assert.ThrowsAsync<SpeechException>(() => SpokenLanguage.DetectAsync(tools, broken, "film.mkv", 0, TimeSpan.FromMinutes(100)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Test]
    public async Task TheAccurateModelTellsCzechFromSlovakOnAFewSeconds()
    {
        var folder = NewFolder();
        try
        {
            var tools = FakeTools(folder, accurate: true);
            Assert.True(tools.Accurate is not null && tools.Writer == tools.Accurate, "the accurate model installed – it also writes down dictation");
            var dubbed = new StandIn(tools, _ => 0.3, "cs", 0.62, l => l == "sk" ? 0.8 : 0.6);
            var steps = new List<SpeechProgress>();
            var result = await SpokenLanguage.DetectAsync(tools, dubbed, "film.mkv", 0, TimeSpan.FromMinutes(100), new Gauge(steps.Add));
            Assert.Equal("sk", result.Language);
            Assert.True(dubbed.Accurate == 2 && dubbed.Transcribed.SequenceEqual(["cs", "sk"]), "the clip transcribed by the accurate model in both languages");
            Assert.True(steps.Count > 3 && steps.Zip(steps.Skip(1)).All(p => p.Second.Fraction >= p.First.Fraction - 1e-9) && steps[^1] is { Fraction: 1, Stage: SpeechStage.Done },
                "the gauge only goes forward, up to 100 %: " + string.Join(" ", steps.Select(p => $"{p.Fraction:0.00}{p.Stage.ToString()[0]}")));
            Assert.True(steps.Any(p => p.Stage == SpeechStage.CzechOrSlovak), "the Czech-or-Slovak stage reported");
            Assert.True(!Directory.EnumerateDirectories(Path.GetTempPath(), "UNIVERSUM-speech-*").Any(), "the pieces and the clip deleted");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Test]
    public void TheClipOfSpeechAndTheEncodersWindow()
    {
        var folder = NewFolder();
        try
        {
            // 12 s of silence, 6 s of loud sound, 12 s of silence: the clip of 9 s holds all the sound
            var wave = Path.Combine(folder, "piece.wav");
            var samples = new short[16000 * 30];
            for (var i = 16000 * 12; i < 16000 * 18; i++)
            {
                samples[i] = (short)(Math.Sin(i * 0.05) * 9000);
            }

            var bytes = new byte[44 + (samples.Length * 2)];
            "RIFF"u8.CopyTo(bytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
            "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 16000);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 32000);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
            "data"u8.CopyTo(bytes.AsSpan(36));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples.Length * 2);
            Buffer.BlockCopy(samples, 0, bytes, 44, samples.Length * 2);
            File.WriteAllBytes(wave, bytes);

            var clip = Path.Combine(folder, "clip.wav");
            Assert.Equal<double?>(9, SpokenLanguage.SpeechClip(wave, clip, 9), "nine seconds");
            var loudness = SpokenLanguage.Loudness(clip);
            Assert.True(loudness > -20, $"the clip is the sound, not the silence ({loudness:0.0} dB)");
            Assert.True(new FileInfo(clip).Length == 44 + (9 * 32000), "a WAV of nine seconds");

            Assert.Equal(474, SpokenLanguage.AudioContext(9), "50 positions a second and an edge");
            Assert.Equal(1500, SpokenLanguage.AudioContext(40), "never more than the whole window");
            var listen = SpokenLanguage.ListenRequest(new SpeechTools(folder, "ffmpeg", "whisper", "/models/ggml-base.bin"), folder, ["piece-1.wav", "piece-2.wav"]);
            Assert.True(string.Join(' ', listen.Arguments).EndsWith("-f piece-1.wav -f piece-2.wav", StringComparison.Ordinal) && listen.Arguments.Contains("-dl") &&
                        listen.WorkingDirectory == folder && listen.Background, string.Join(' ', listen.Arguments));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private sealed class Gauge(Action<SpeechProgress> report) : IProgress<SpeechProgress>
    {
        public void Report(SpeechProgress value) => report(value);
    }

    [Test]
    public async Task AFileGoesToTheStandardInput()
    {
        var file = Path.Combine(Path.GetTempPath(), $"universum-input-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "kapor v rybníku\n");
        try
        {
            var request = OperatingSystem.IsWindows()
                ? new ProcessRequest("cmd.exe", ["/c", "more"]) { StandardInputFile = file, Timeout = TimeSpan.FromSeconds(30) }
                : new ProcessRequest("cat", []) { StandardInputFile = file, Timeout = TimeSpan.FromSeconds(30) };
            var result = await new ProcessRunner().RunAsync(request);
            Assert.True(result.ExitCode == 0 && result.StandardOutput.Contains("kapor v rybn", StringComparison.Ordinal), result.StandardOutput + result.StandardError);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task AMissingInputFileDoesNotHang()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"universum-missing-{Guid.NewGuid():N}.wav");
        var request = OperatingSystem.IsWindows()
            ? new ProcessRequest("cmd.exe", ["/c", "more"]) { StandardInputFile = missing, Timeout = TimeSpan.FromSeconds(20) }
            : new ProcessRequest("cat", []) { StandardInputFile = missing, Timeout = TimeSpan.FromSeconds(20) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await new ProcessRunner().RunAsync(request);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10) && result.StandardOutput.Trim().Length == 0, $"the input is closed at once ({watch.Elapsed.TotalSeconds:0.0} s)");
    }

    [Test]
    public async Task ManifestGuardsTheTools()
    {
        var folder = NewFolder();
        try
        {
            Assert.Equal<SpeechTools?>(null, SpeechTools.In(folder), "nothing installed");
            var tools = FakeTools(folder);
            await tools.VerifyAsync();

            File.WriteAllText(Path.Combine(folder, "whisper", "ggml-cpu-extra.dll"), "not listed");
            var stray = await Assert.ThrowsAsync<SpeechException>(() => tools.VerifyAsync());
            Assert.True(stray.Message.Contains("neznámy súbor whisper/ggml-cpu-extra.dll", StringComparison.Ordinal), stray.Message);
            File.Delete(Path.Combine(folder, "whisper", "ggml-cpu-extra.dll"));
            await (SpeechTools.In(folder + Path.DirectorySeparatorChar) ?? throw new InvalidOperationException("folder with a separator")).VerifyAsync();

            File.AppendAllText(tools.Whisper, "changed");
            var error = await Assert.ThrowsAsync<SpeechException>(() => tools.VerifyAsync());
            Assert.True(error.Message.Contains("whisper", StringComparison.Ordinal) && error.Message.Contains("zmenený", StringComparison.Ordinal), error.Message);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>
    /// The real tools (Windows CI puts them in UNIVERSUM_SPEECH_DIR) on invented synthetic speech: Czech, English
    /// and Slovak audio tracks without a language.
    /// </summary>
    [Test]
    public async Task RealSpeechIsRecognised()
    {
        var folder = Environment.GetEnvironmentVariable("UNIVERSUM_SPEECH_DIR");
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Skip("UNIVERSUM_SPEECH_DIR not set");
        }

        var tools = SpeechTools.In(folder!) ?? throw new InvalidOperationException("speech tools missing in " + folder);
        await tools.VerifyAsync();
        var installation = await TestData.RequireMkvToolNixAsync();
        var file = await new MkvMergeInspector(installation, new ProcessRunner()).InspectAsync(TestData.Media("speech-und.mkv"));
        var heard = new List<string?>();
        for (var ordinal = 0; ordinal < 3; ordinal++)
        {
            var result = await SpokenLanguage.DetectAsync(tools, new ProcessRunner(), file.Path, ordinal, file.Duration);
            Console.WriteLine($"      audio {ordinal + 1}: {result.Language ?? "?"} ({string.Join(", ", result.Pieces.Select(p => $"{p.Language}:{p.Probability:0.00}"))}) {result.Doubt}");
            heard.Add(result.Language);
        }

        Assert.True(heard[0] is "cs" or "sk", "Czech speech heard as " + heard[0]);
        Assert.Equal("en", heard[1], "English speech");
        Assert.True(heard[2] is "cs" or "sk", "Slovak speech heard as " + heard[2]);
    }

    [Test]
    public void EachFilmIsHeardOnItsOwn()
    {
        static MediaTrack Audio(int id, string? name = null) =>
            new() { Id = id, Number = id + 1, Type = TrackType.Audio, CodecId = "A_AC3", Name = name, Language = LanguageTag.Undetermined };
        static MediaFile Film(string path) => new()
        {
            Path = path, ContainerType = "Matroska",
            Tracks = [new MediaTrack { Id = 0, Number = 1, Type = TrackType.Video, CodecId = "V_MPEG4/ISO/AVC", Language = LanguageTag.Undetermined }, Audio(1), Audio(2, "CZ Dabing")],
        };

        var films = new BatchGroup("A", 0);
        films.Files.AddRange([Film("a.mkv"), Film("b.mkv"), Film("c.mkv")]);
        var key = new SlotKey(TrackType.Audio, 0);

        // films: every file on its own; the track named "CZ Dabing" is known
        var (targets, known) = SpeechTargets.ForGroups([films], series: false);
        Assert.Equal(3, targets.Count, "the unnamed track of each film");
        Assert.Equal(3, known, "the named track of each film");
        Assert.True(targets.All(t => !t.ForGroup && t.Key == key), "each for its own file");

        // a series: one episode speaks for the group
        var (episodes, _) = SpeechTargets.ForGroups([films], series: true);
        Assert.True(episodes is [{ ForGroup: true, File.Path: "a.mkv" }], "one episode");

        // a film heard (or decided) is not heard again; its suggestion wins over the group's for that file only
        SpeechTargets.Keep(targets[1], new LanguageEvidence(LanguageTag.FromCode("en"), Confidence.Medium, EvidenceSource.Speech));
        films.Suggestions[key] = new LanguageEvidence(LanguageTag.FromCode("cs"), Confidence.Medium, EvidenceSource.Speech);
        films.Decisions.SetFileLanguage("c.mkv", key, LanguageTag.FromCode("de"));
        Assert.True(SpeechTargets.ForFiles(films.Files.Select(f => (films, f))).Targets is [{ File.Path: "a.mkv" }], "only the film not heard or decided yet");
        var track = films.Files[0].Tracks[1];
        Assert.Equal("cs", BatchPlanner.Evidence(track, key, films, "a.mkv")?.Language.Primary, "the group's suggestion");
        Assert.Equal("en", BatchPlanner.Evidence(track, key, films, "b.mkv")?.Language.Primary, "the film's own suggestion");
        Assert.True(BatchPlanner.Evidence(track, key, films, "c.mkv") is { Source: EvidenceSource.FileDecision, IsUsable: true } chosen && chosen.Language.Primary == "de",
            "the choice for the film");
        Assert.Equal("cs", BatchPlanner.Evidence(track, key, films)?.Language.Primary, "the group as a whole");
    }

    private static string NewFolder() => Directory.CreateTempSubdirectory("universum-tools-test-").FullName;

    private static SpeechTools FakeTools(string folder, bool accurate = false)
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var files = new List<(string, string)> { ($"ffmpeg/ffmpeg{exe}", "ffmpeg"), ($"whisper/whisper-cli{exe}", "whisper"), ($"models/{SpeechTools.ModelName}", "model") };
        if (accurate)
        {
            files.Add(($"models/{SpeechTools.AccurateName}", "accurate model"));
        }

        foreach (var (path, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, path))!);
            File.WriteAllText(Path.Combine(folder, path), text);
        }

        SpeechTools.WriteManifestAsync(folder).GetAwaiter().GetResult();
        return SpeechTools.In(folder)!;
    }

    /// <summary>
    /// FFmpeg writes a piece (silent or loud, by its position); whisper hears one language and, asked to transcribe as
    /// Czech or Slovak, is as sure of the words as <paramref name="sureOf"/> says (no transcription without it).
    /// </summary>
    private sealed class StandIn(SpeechTools tools, Func<double, double> amplitudeAt, string? language, double probability, Func<string, double>? sureOf = null)
        : IProcessRunner
    {
        public List<string> Cuts { get; } = [];

        public int Heard { get; private set; }

        public List<string> Transcribed { get; } = [];

        /// <summary>The language heard in each piece in turn (instead of the one language).</summary>
        public string[]? Languages { get; init; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Assert.True(request.Background, "speech runs below normal priority");
            if (request.FileName == tools.Ffmpeg)
            {
                // several pieces in one run: the positions in the order of the inputs, the waves after each "-y"
                Cuts.Add(string.Join(' ', request.Arguments));
                var arguments_ = request.Arguments.ToList();
                var positions = arguments_.Select((a, i) => (a, i)).Where(x => x.a == "-ss")
                    .Select(x => double.Parse(arguments_[x.i + 1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
                var waves = arguments_.Select((a, i) => (a, i)).Where(x => x.a == "-y").Select(x => arguments_[x.i + 1]).ToList();
                Assert.True(positions.Count == waves.Count, "a wave for every position");
                for (var i = 0; i < waves.Count; i++)
                {
                    WriteWave(waves[i], amplitudeAt(positions[i]));
                }

                return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
            }

            var arguments = request.Arguments.ToList();
            if (arguments.Contains("-ojf"))
            {
                var as_ = arguments[arguments.IndexOf("-l") + 1];
                Transcribed.Add(as_);
                var input = arguments[arguments.IndexOf("-f") + 1] == "-" ? request.StandardInputFile : Path.Combine(request.WorkingDirectory!, arguments[arguments.IndexOf("-f") + 1]);
                Assert.True(File.Exists(input) && request.WorkingDirectory == Path.GetDirectoryName(input), "the piece is still there, the output next to it");
                Assert.True(arguments.Contains("-bs") && arguments.Contains("-nf"), "the surest words one by one, no second try");
                if (arguments.Contains("-ac"))
                {
                    Accurate++;
                }
                if (sureOf is not null)
                {
                    var p = sureOf(as_).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var tokens = string.Join(",", Enumerable.Range(0, 8).Select(i => $$"""{"text":" slovo{{i}}","id":{{100 + i}},"p":{{p}}}""").Prepend("""{"text":"[_BEG_]","id":50365,"p":0.01}"""));
                    File.WriteAllText(Path.Combine(request.WorkingDirectory!, arguments[arguments.IndexOf("-of") + 1] + ".json"),
                        $$"""{"result":{"language":"{{as_}}"},"transcription":[{"text":" ...","tokens":[{{tokens}}]}]}""");
                }

                return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
            }

            // the language of each piece – several in one run, each reported on its own line as whisper.cpp writes it
            var files = arguments.Select((a, i) => (a, i)).Where(x => x.a == "-f").Select(x => arguments[x.i + 1]).ToList();
            var error = new System.Text.StringBuilder();
            foreach (var file in files)
            {
                Heard++;
                var (heardLanguage, p2) = Languages is { } each ? (each[(Heard - 1) % each.Length], 0.9) : (language, probability);
                var name = file == "-" ? "-" : file;
                foreach (var line in new[]
                         {
                             $"main: processing '{name}' (480000 samples, 30.0 sec), 4 threads, 1 processors, 5 beams + best of 5, lang = auto, task = transcribe, timestamps = 1 ...",
                             heardLanguage is null ? "error: failed to process audio" : $"whisper_full_with_state: auto-detected language: {heardLanguage} (p = {p2.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)})",
                         })
                {
                    error.AppendLine(line);
                    request.OnErrorLine?.Invoke(line);
                }
            }

            Runs++;
            return Task.FromResult(new ProcessResult(0, "", error.ToString(), TimeSpan.Zero));
        }

        /// <summary>How many times whisper ran to hear languages.</summary>
        public int Runs { get; private set; }

        /// <summary>Transcriptions by the accurate model (the encoder on a few seconds).</summary>
        public int Accurate { get; private set; }

        private static void WriteWave(string path, double amplitude)
        {
            const int rate = 16000;
            var samples = rate * 5;
            var bytes = new byte[44 + samples * 2];
            "RIFF"u8.CopyTo(bytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
            "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), rate);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), rate * 2);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
            "data"u8.CopyTo(bytes.AsSpan(36));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples * 2);
            for (var i = 0; i < samples; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + i * 2), (short)(amplitude * 32767 * Math.Sin(i * 0.05)));
            }

            File.WriteAllBytes(path, bytes);
        }
    }
}
