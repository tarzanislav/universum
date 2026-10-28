using System.Buffers.Binary;
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
            Assert.True(runner.Cuts.All(c => c.Contains("0:a:1", StringComparison.Ordinal)), "the second audio track");

            var unsure = new StandIn(tools, _ => 0.3, "sk", 0.55);
            var slovak = await SpokenLanguage.DetectAsync(tools, unsure, "film.mkv", 0, TimeSpan.FromMinutes(100));
            Assert.Equal("sk", slovak.Language);
            Assert.Equal(5, unsure.Heard, "not sure enough: all five pieces");
            Assert.True(!Directory.EnumerateFiles(Path.Combine(Path.GetTempPath(), "UNIVERSUM"), "speech-*.wav").Any(), "pieces are deleted");

            var broken = new StandIn(tools, _ => 0.3, null, 0);
            await Assert.ThrowsAsync<SpeechException>(() => SpokenLanguage.DetectAsync(tools, broken, "film.mkv", 0, TimeSpan.FromMinutes(100)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
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
    public async Task ManifestGuardsTheTools()
    {
        var folder = NewFolder();
        try
        {
            Assert.Equal<SpeechTools?>(null, SpeechTools.In(folder), "nothing installed");
            var tools = FakeTools(folder);
            await tools.VerifyAsync();

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

    private static string NewFolder() => Directory.CreateTempSubdirectory("universum-speech-").FullName;

    private static SpeechTools FakeTools(string folder)
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var (path, text) in new[] { ($"ffmpeg/ffmpeg{exe}", "ffmpeg"), ($"whisper/whisper-cli{exe}", "whisper"), ("models/ggml-base.bin", "model") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, path))!);
            File.WriteAllText(Path.Combine(folder, path), text);
        }

        SpeechTools.WriteManifestAsync(folder).GetAwaiter().GetResult();
        return SpeechTools.In(folder)!;
    }

    /// <summary>FFmpeg writes a piece (silent or loud, by its position); whisper hears one language.</summary>
    private sealed class StandIn(SpeechTools tools, Func<double, double> amplitudeAt, string? language, double probability) : IProcessRunner
    {
        public List<string> Cuts { get; } = [];

        public int Heard { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName == tools.Ffmpeg)
            {
                Cuts.Add(string.Join(' ', request.Arguments));
                var at = double.Parse(request.Arguments[request.Arguments.ToList().IndexOf("-ss") + 1], System.Globalization.CultureInfo.InvariantCulture);
                WriteWave(request.Arguments[^1], amplitudeAt(at));
                return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
            }

            Heard++;
            var output = language is null ? "error: failed to process audio" : $"whisper_full_with_state: auto-detected language: {language} (p = {probability:0.000000})";
            return Task.FromResult(new ProcessResult(0, "", output.Replace(',', '.'), TimeSpan.Zero));
        }

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
