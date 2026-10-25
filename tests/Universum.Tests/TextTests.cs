using Universum.Core.Text;

namespace Universum.Tests;

public sealed class TextTests
{
    [Test]
    public void PhraseMatchingIgnoresCaseAndDiacritics()
    {
        Assert.Equal("vynutene titulky", Words.Normalize("  Vynútené – TITULKY! "));
        Assert.True(Words.ContainsPhrase("Czech (Forced)", "forced"), "in brackets");
        Assert.True(Words.ContainsPhrase("Titulky pre nepočujúcich", "pre nepocujucich"), "two words");
        Assert.True(!Words.ContainsPhrase("Unforced", "forced"), "whole words only");
        Assert.True(!Words.ContainsPhrase("CCTV", "cc"), "whole words only (cc)");
        Assert.True(!Words.ContainsPhrase(null, "forced"), "null text");
    }
}
