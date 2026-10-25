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
}
