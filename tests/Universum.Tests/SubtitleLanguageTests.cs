using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;
using Universum.Core.Text;

namespace Universum.Tests;

/// <summary>The language of subtitles from their text (owner, 30. 10. 2026) – invented texts only.</summary>
public sealed class SubtitleLanguageTests
{
    [Test]
    public void CzechAndSlovakByTheirLetters()
    {
        Assert.True(SubtitleLanguage.Detect("Řekl jsem ti, že se vrátím.") is { Language: "cs", Certain: true }, "ř: Czech");
        Assert.True(SubtitleLanguage.Detect("To je můj dům.") is { Language: "cs", Certain: true }, "ů: Czech");
        Assert.True(SubtitleLanguage.Detect("Ľudia sa vrátia domov.") is { Language: "sk", Certain: true }, "ľ: Slovak");
        Assert.True(SubtitleLanguage.Detect("Môj brat príde skôr.") is { Language: "sk", Certain: true }, "ô: Slovak");
        Assert.True(SubtitleLanguage.Detect("Ide späť.") is { Language: "sk", Certain: true }, "ä: Slovak");
    }

    [Test]
    public void CzechAndSlovakByTheOwnersWords()
    {
        var czech = SubtitleLanguage.Detect("Prostě to udělej správně a ještě jednou.");
        Assert.True(czech is { Language: "cs", Certain: true } && czech.Detail.Contains("české slová", StringComparison.Ordinal), "Czech words: " + czech?.Detail);
        Assert.True(SubtitleLanguage.Detect("Najmä kvôli tomu, zatiaľ nie.") is { Language: "sk", Certain: true }, "Slovak words");
    }

    [Test]
    public void BothLanguagesDecideByTheirCount()
    {
        // Slovak subtitles with a Czech name in them stay Slovak
        var text = string.Join(" ", Enumerable.Repeat("Ľudia veľmi chcú, aby sa vrátil skôr.", 8)) + " Volá sa Jiří.";
        Assert.True(SubtitleLanguage.Detect(text) is { Language: "sk", Certain: true }, "a Czech name does not make Slovak subtitles Czech");
        Assert.True(SubtitleLanguage.Detect("Řekl to. Ľudia to vedia.") is { Certain: false }, "even signs: only the most probable one");
    }

    [Test]
    public void DzIsOnlyAWeakSign()
    {
        Assert.True(SubtitleLanguage.Detect("Na podzim.") is null, "dz alone decides nothing (Czech podzim)");
        Assert.True(SubtitleLanguage.Detect("Ako sa máš? Som tu, lebo medzi nami je to tak.") is { Language: "sk", Certain: false } result &&
                    result.Detail.Contains("overte", StringComparison.Ordinal), "short words and dz: probably Slovak, to check");
    }

    [Test]
    public void OtherLanguagesAreNotTakenForSlovak()
    {
        Assert.True(SubtitleLanguage.Detect("Mädchen, hör zu.") is null or { Language: not "sk" }, "German ä is not Slovak");
        var english = string.Join(" ", Enumerable.Repeat("I don't know what you are doing here, it's not your house and you can go.", 3));
        Assert.True(SubtitleLanguage.Detect(english) is { Language: "en" }, "English");
    }

    [Test]
    public void LanguagesWithOtherLettersAreNeitherCzechNorSlovak()
    {
        // review: French ô, Finnish ä, Spanish, Italian, Romanian, Croatian, Turkish – invented sentences
        string[] others =
        [
            "Il est trop tôt. L'hôtel est fermé, je ne sais pas où il est allé. Pourquoi tu es là? On doit partir tout de suite.",
            "Mitä sinä täällä teet? Tämä talo on minun. En tiedä, missä hän on. Hän lähti pois tänään ja sanoi, että palaa huomenna.",
            "Pensé que te habías ido. No sé si lo lograremos. Tenemos que irnos ahora, antes de que vuelva. Esta es mi casa. Se fue.",
            "Che cosa fai qui? Pensavo che fossi andato via. Non so se ce la faremo. Perché non me l'ha detto? Questa è la mia casa.",
            "Ce se întâmplă aici? Nu știu ce să fac. Se pare că a plecat. Ne vedem mâine? Nu se poate așa ceva. Ea se duce acasă.",
            "Ce se intampla aici? Nu stiu ce sa fac. Se pare ca a plecat. Ne vedem maine? Nu se poate asa ceva. Ea se duce acasa. Se face tarziu.",
            "Što se dogodilo? Ne znam, nisam bio tamo. Moramo se vratiti kući. Ne mogu više čekati. Što ćemo sada? Ne brini, sve će biti u redu.",
            "Burada ne yapıyorsun? Gittiğini sanıyordum. Başarabilir miyiz bilmiyorum. Hemen gitmeliyiz, o dönmeden önce. Neden bana söylemedi?",
            "Wir gehen nach Hause, Mädchen. Das ist ein Problem.",
        ];
        foreach (var text in others)
        {
            Assert.True(SubtitleLanguage.Detect(text) is null or { Language: not "cs" and not "sk" }, "not Czech or Slovak: " + text[..30] + " → " + SubtitleLanguage.Detect(text));
        }

        var english = string.Join(" ", Enumerable.Repeat("I don't know what you are doing here, it's not your house and you can go to the café.", 3));
        Assert.True(SubtitleLanguage.Detect(english) is { Language: "en" }, "a café in English subtitles stays English");
    }

    [Test]
    public void WithoutDiacriticsOnlyTheMostProbable()
    {
        var czech = SubtitleLanguage.Detect("Co tady delas? Myslel jsem, ze jsi odesel. Nevim, jestli to zvladneme. Jsem tady pro tebe. Chci ti neco ukazat. Co se stalo?");
        Assert.True(czech is { Language: "cs", Certain: false, CzechOrSlovak: false }, "Czech without diacritics: " + czech);
        var slovak = SubtitleLanguage.Detect("Co tu robis? Myslel som, ze si odisiel. Neviem, ci to zvladneme. Som tu pre teba. Chcem ti nieco ukazat. Co sa stalo?");
        Assert.True(slovak is { Language: "sk", Certain: false }, "Slovak without diacritics: " + slovak);
    }

    [Test]
    public void AGuessNeverReplacesAnotherLanguageOfTheFile()
    {
        var guess = new SubtitleLanguageResult("cs", false, "bez diakritiky – overte");
        Assert.True(guess.Replaces(LanguageTag.Undetermined) && guess.Replaces(LanguageTag.FromCode("sk")), "undetermined, Slovak: replaced");
        Assert.True(!guess.Replaces(LanguageTag.FromCode("slv")) && !guess.Replaces(LanguageTag.FromCode("eng")), "Slovenian, English in the file stay");
        Assert.True(new SubtitleLanguageResult("cs", true, "ř").Replaces(LanguageTag.FromCode("eng")), "certain Czech replaces English (EN that is Czech)");
        Assert.True(new SubtitleLanguageResult("sk", false, "– overte", CzechOrSlovak: true).Replaces(LanguageTag.FromCode("eng")),
            "surely Czech or Slovak replaces English even when which of the two is uncertain");
        var german = new SubtitleLanguageResult("de", true, "podľa častých slov");
        Assert.True(german.Replaces(LanguageTag.FromCode("eng")) && !german.Replaces(LanguageTag.FromCode("nld")),
            "a language the detector cannot tell (Dutch) stays as the file has it");
    }

    [Test]
    public async Task EverySubtitleTrackOfAFileIsRead()
    {
        // without FFmpeg the whole tracks are read by mkvextract; the Czech full subtitles are certainly Czech
        var installation = await TestData.RequireMkvToolNixAsync();
        var file = await new MkvMergeInspector(installation, new ProcessRunner()).InspectAsync(TestData.Media("en-und-cz-forced.mkv"));
        var found = await SubtitleText.DetectAsync(installation, new ProcessRunner(), null, file);
        var czech = file.TracksOf(TrackType.Subtitles).First(t => t.Name == "Czech full");
        Assert.True(found.TryGetValue(czech.Id, out var result) && result is { Language: "cs", Certain: true }, "Czech full: " + result);
        var evidence = SubtitleText.Evidence(result!);
        Assert.True(evidence is { IsUsable: true, Source: Universum.Core.Batch.EvidenceSource.SubtitleAnalysis } && evidence.Detail!.StartsWith("isté", StringComparison.Ordinal),
            "applied as UNIVERSUM's suggestion");
    }

    [Test]
    public async Task ASubtitleFileOnItsOwnIsReadWhole()
    {
        // owner, 1. 11. 2026: an SRT added in Zlučovanie (in Windows-1250, as old Czech files are) – its language from its text
        var installation = await TestData.RequireMkvToolNixAsync();
        var work = Directory.CreateTempSubdirectory("universum-srt-");
        try
        {
            var srt = Path.Combine(work.FullName, "Vymyslený film.srt");
            var text = string.Join("\n\n", Enumerable.Range(0, 12).Select(i =>
                $"{i + 1}\n00:00:{i * 2:00},000 --> 00:00:{(i * 2) + 1:00},500\nŘekl jsem ti, že tady budu. Co ještě chceš, když je teď všechno takové, jaké to má být?")) + "\n";
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            await File.WriteAllBytesAsync(srt, System.Text.Encoding.GetEncoding(1250).GetBytes(text));
            var file = await new MkvMergeInspector(installation, new ProcessRunner()).InspectAsync(srt);
            Assert.True(SubtitleText.StandaloneText(file) is { } whole && whole.Contains("Řekl jsem ti", StringComparison.Ordinal), "read whole in its encoding");
            var found = await SubtitleText.DetectAsync(installation, new ProcessRunner(), null, file);
            Assert.True(found.Values.SingleOrDefault() is { Language: "cs" }, "Czech: " + string.Join(", ", found.Values));
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    [Test]
    public async Task TheIndexOfTheFileTellsWhereTheSubtitlesAre()
    {
        // mkvmerge indexes every subtitle: only the index is read, never the film
        await TestData.RequireMkvToolNixAsync();
        var cues = await MatroskaCues.ReadAsync(TestData.Media("en-und-cz-forced.mkv"));
        Assert.True(cues.TryGetValue(4, out var forced) && forced.Count == 1, "Forced (track number 4): one subtitle – " + string.Join(", ", cues.Select(c => $"{c.Key}:{c.Value.Count}")));
        Assert.True(cues.TryGetValue(5, out var full) && full.Count == 2 && full[0] <= full[1], "Czech full (5): two, in order");
        Assert.True(MatroskaCues.Read(new MemoryStream([1, 2, 3, 4])).Count == 0, "not Matroska: no index");
        Assert.True((await MatroskaCues.ReadAsync(TestData.Media("en-cz.mp4"))).Count == 0, "MP4: no index");
    }

    [Test]
    public void OnlyAFewPartsOfTheFilmAreRead()
    {
        // owner, 30. 10. 2026: not all the subtitles – a few parts, like the audio
        var full = Track(2, 3, 1_200);
        var forced = Track(3, 4, 30);
        var noIndex = Track(4, 5, 900);
        var sparseNoIndex = Track(5, 6, 20);
        var film = new MediaFile { Path = "Vymyslený film (2024).mkv", ContainerType = "Matroska", Duration = TimeSpan.FromMinutes(100), Tracks = [full, forced, noIndex, sparseNoIndex] };
        var cues = new Dictionary<int, IReadOnlyList<TimeSpan>>
        {
            [3] = Enumerable.Range(0, 1_200).Select(i => TimeSpan.FromSeconds(30 + (i * 5))).ToList(),
            [4] = Enumerable.Range(0, 30).Select(i => TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(i * 20)).ToList(),
        };

        var parts = SubtitleText.Plan(film, film.Tracks, cues);
        var common = parts.Where(p => p.Reads.Any(r => r.Track == full)).ToList();
        Assert.Sequence([20, 35, 50, 65, 80], common.Select(p => (int)p.Start.TotalMinutes), "five parts at 20–80 % of the film");
        Assert.True(common.All(p => p.Reads.Single(r => r.Track == full).Lines is > 0 and <= SubtitleText.LinesPerPart), "the full track: up to 25 subtitles a part");
        Assert.True(parts.Where(p => p.Reads.Any(r => r.Track == forced)).All(p => p.Start < TimeSpan.FromMinutes(18)) &&
                    parts.Count(p => p.Reads.Any(r => r.Track == forced)) is > 0 and <= SubtitleText.ExtraParts,
            "forced: a few parts where the index has them – " + string.Join(" | ", parts.Select(p => p.Start)));
        Assert.True(common.All(p => p.Reads.Single(r => r.Track == noIndex).Lines is null), "a full track without the index: to the end of the part");
        Assert.True(parts.All(p => p.Reads.All(r => r.Track != sparseNoIndex)), "few subtitles without the index: not read (FFmpeg would read the whole file)");
        Assert.True(parts.Sum(p => SubtitleText.PartLength.TotalMinutes) <= 8 * SubtitleText.PartLength.TotalMinutes, "only a few parts");

        var trailer = film with { Duration = TimeSpan.FromMinutes(3) };
        Assert.Sequence([TimeSpan.Zero], SubtitleText.Plan(trailer, [noIndex], new Dictionary<int, IReadOnlyList<TimeSpan>>()).Select(p => p.Start), "a short film: one part from the start");
    }

    [Test]
    public async Task FFmpegReadsThePartsAndStops()
    {
        // FFmpeg is asked for each part: from its time, so many subtitles (then it stops) – never the whole file
        var installation = await TestData.RequireMkvToolNixAsync();
        var file = await new MkvMergeInspector(installation, new ProcessRunner()).InspectAsync(TestData.Media("en-und-cz-forced.mkv"));
        var ffmpeg = Path.Combine(Path.GetTempPath(), "UNIVERSUM-tests", "ffmpeg-fake");
        Directory.CreateDirectory(Path.GetDirectoryName(ffmpeg)!);
        await File.WriteAllTextAsync(ffmpeg, "");
        var runner = new PartRunner();
        var found = await SubtitleText.DetectAsync(installation, runner, ffmpeg, file);
        Assert.True(runner.Calls.Count > 0 && runner.Calls.All(c => c.FileName == ffmpeg), "only FFmpeg, no mkvextract");
        Assert.True(runner.Calls.All(c => c.Arguments.Contains("-ss") && c.Arguments.Contains("-frames:s")), "a part: from a time, a number of subtitles");
        var czech = file.TracksOf(TrackType.Subtitles).First(t => t.Name == "Czech full");
        Assert.True(found.TryGetValue(czech.Id, out var result) && result is { Language: "cs", Certain: true }, "the text of the parts is analysed: " + result);
    }

    [Test]
    public async Task RealFFmpegReadsTheParts()
    {
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            Assert.Skip("FFmpeg not on PATH");
        }

        var installation = await TestData.RequireMkvToolNixAsync();
        var file = await new MkvMergeInspector(installation, new ProcessRunner()).InspectAsync(TestData.Media("en-und-cz-forced.mkv"));
        var found = await SubtitleText.DetectAsync(installation, new ProcessRunner(), ffmpeg, file);
        var czech = file.TracksOf(TrackType.Subtitles).First(t => t.Name == "Czech full");
        Assert.True(found.TryGetValue(czech.Id, out var result) && result is { Language: "cs" }, "Czech full read by FFmpeg: " + result);
    }

    private static MediaTrack Track(int id, int number, long frames) => new()
    {
        Id = id, Number = number, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8", IsTextSubtitles = true, StatisticsFrames = frames, StatisticsBytes = frames * 40,
    };

    // writes Czech subtitles where FFmpeg would write the part of each track
    private sealed class PartRunner : IProcessRunner
    {
        public List<ProcessRequest> Calls { get; } = [];

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            for (var i = 0; i + 2 < request.Arguments.Count; i++)
            {
                if (request.Arguments[i] == "-f" && request.Arguments[i + 1] == "srt")
                {
                    await File.WriteAllTextAsync(request.Arguments[i + 2], "1\n00:00:01,000 --> 00:00:02,000\nŘekl jsem ti, že se vrátím.\n\n", cancellationToken);
                }
            }

            return new ProcessResult(0, "", "", TimeSpan.Zero);
        }
    }
}
