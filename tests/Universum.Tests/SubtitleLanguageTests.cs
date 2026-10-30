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
}
