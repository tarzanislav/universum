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
}
