using Universum.Core.Library.Histra;

namespace Universum.Tests;

/// <summary>HISTRA token: only what can safely go into a Bearer header is accepted.</summary>
public sealed class HistraTests
{
    [Test]
    public void TokenGoesIntoABearerHeader()
    {
        const string token = "hst_9fK2-x.Q~p+/abc==";
        Assert.True(HistraToken.IsValid(token), "token68 characters and padding");
        var header = HistraToken.Authorization(token);
        Assert.Equal("Bearer", header.Scheme);
        Assert.Equal(token, header.Parameter);
        Assert.Equal("Bearer " + token, header.ToString());
    }

    [Test]
    public void UnsafeTokensAreRefused()
    {
        foreach (var bad in new[] { null, "", "short", "abc def ghi", "abcdefgh\r\nX-Evil: 1", "\"abcdefghij\"", "========", "abc=defghij", new string('a', HistraToken.MaxLength + 1) })
        {
            Assert.True(!HistraToken.IsValid(bad), $"refused: {bad?.Replace("\r\n", "\\r\\n")}");
        }

        Assert.True(HistraToken.TokenPage.Scheme == "https" && HistraToken.TokenPage.Host == "histra.net", "token page on histra.net over HTTPS");
    }
}
