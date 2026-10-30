using Universum.Core.Media;

namespace Universum.Tests;

public sealed class LanguageTests
{
    [Test]
    public void CzechCodesAreRecognised()
    {
        foreach (var code in new[] { "cs", "cze", "ces", "CS", "cs-CZ" })
        {
            var tag = LanguageTag.FromCode(code);
            Assert.Equal(LanguageGroup.Czech, tag.Group, code);
            Assert.Equal("cze", tag.Legacy, code + " legacy");
        }
    }

    [Test]
    public void SlovakCodesAreRecognised()
    {
        foreach (var code in new[] { "sk", "slo", "slk", "sk-SK" })
        {
            Assert.Equal(LanguageGroup.Slovak, LanguageTag.FromCode(code).Group, code);
        }
    }

    [Test]
    public void LegacyElementAloneIsEnough()
    {
        Assert.Equal("cs", new LanguageTag("cze", null).Primary);
        Assert.Equal("sk", new LanguageTag("slo", null).Primary);
        Assert.Equal(LanguageGroup.Other, new LanguageTag("eng", null).Group);
    }

    [Test]
    public void IetfElementWinsOverLegacy()
    {
        // files written by newer tools may contain a precise IETF tag with a generic legacy value
        Assert.Equal(LanguageGroup.Czech, new LanguageTag("und", "cs").Group);
    }

    [Test]
    public void UndeterminedIsDetected()
    {
        Assert.True(LanguageTag.Undetermined.IsUndetermined, "und");
        Assert.True(new LanguageTag("und", null).IsUndetermined, "legacy und");
        Assert.True(new LanguageTag("", null).IsUndetermined, "empty");
        Assert.True(!new LanguageTag("zxx", null).IsUndetermined, "zxx means no linguistic content, not unknown");
    }

    [Test]
    public void LookupByAnyCode()
    {
        Assert.Equal(Languages.Czech, Languages.Find("ces"));
        Assert.Equal(Languages.Slovak, Languages.Find("SLO"));
        Assert.Equal("nemčina", Languages.Find("deu")?.SlovakName);
        Assert.Equal<Language?>(null, Languages.Find("xx"));
    }

    [Test]
    public async Task EveryLanguageOfMkvToolNix()
    {
        // owner, 30. 10. 2026: a film in Filipino – all languages of MKVToolNix in the lists, the common ones on top
        const string table = """
            English language name                                      | ISO 639-3 code | ISO 639-2 code | ISO 639-1 code
            -----------------------------------------------------------+----------------+----------------+---------------
            Abé                                                        | aba            |                |
            Czech                                                      | cze            | cze            | cs
            Filipino                                                   | fil            | fil            |
            Germanic languages                                         | gem            | gem            |
            Icelandic                                                  | ice            | ice            | is
            """;
        var parsed = Universum.Core.MkvToolNix.MkvToolNixLanguages.Parse(table.Replace("\r", "", StringComparison.Ordinal));
        Assert.Equal("aba cs fil gem is", string.Join(" ", parsed.Select(l => l.Ietf)), "the code written: two letters, else three");
        Assert.True(parsed.Single(l => l.Ietf == "fil") is { Bibliographic: "fil", SlovakName: "filipínčina" } && parsed[0].SlovakName == "Abé",
            "Slovak name when UNIVERSUM has one, else the English one");

        Languages.Extend(parsed);
        Assert.True(Languages.All.Take(Languages.Common.Count).SequenceEqual(Languages.Common) && Languages.All.Contains(Languages.Find("fil")!),
            "the common languages stay on top, the others below");
        Assert.True(Languages.Find("ice")?.Ietf == "is" && LanguageTag.FromCode("fil").Primary == "fil" && new LanguageTag("fil", null).Primary == "fil",
            "found by any of their codes");
        Assert.True(Languages.FindCommon("fil") is null && Languages.FindCommon("cze") == Languages.Czech, "words of file names: the common languages only");

        var installation = await TestData.RequireMkvToolNixAsync();
        var all = await Universum.Core.MkvToolNix.MkvToolNixLanguages.ReadAsync(installation, new Universum.Core.Processes.ProcessRunner());
        Assert.True(all.Count > 7000 && all.Any(l => l.Ietf == "fil") && all.Any(l => l.Ietf == "cs"), $"the installed MKVToolNix: {all.Count} languages");
    }
}
