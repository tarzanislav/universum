using Universum.Core.Library.Histra;

namespace Universum.Tests;

/// <summary>HISTRA token: only what can safely go into a Bearer header is accepted.</summary>
public sealed class HistraTests
{
    [Test]
    public void TokenGoesIntoABearerHeader()
    {
        const string token = "hst_9fK2-x.Q~p+/abc==";
        Assert.True(HistraApi.IsValidToken(token), "token68 characters and padding");
        var header = HistraApi.Authorization(token);
        Assert.Equal("Bearer", header.Scheme);
        Assert.Equal(token, header.Parameter);
        Assert.Equal("Bearer " + token, header.ToString());
    }

    [Test]
    public void UnsafeTokensAreRefused()
    {
        foreach (var bad in new[] { null, "", "short", "abc def ghi", "abcdefgh\r\nX-Evil: 1", "\"abcdefghij\"", "========", "abc=defghij", new string('a', HistraApi.MaxTokenLength + 1) })
        {
            Assert.True(!HistraApi.IsValidToken(bad), $"refused: {bad?.Replace("\r\n", "\\r\\n")}");
        }

        Assert.True(HistraApi.TokenPage.Scheme == "https" && HistraApi.TokenPage.Host == "histra.net", "token page on histra.net over HTTPS");
    }

    [Test]
    public void RequestsGoOnlyToHistra()
    {
        using var request = HistraApi.Request(HttpMethod.Get, "api/v1/me", "hst_0123456789");
        Assert.Equal("https://histra.net/api/v1/me", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer hst_0123456789", request.Headers.Authorization!.ToString());

        foreach (var foreign in new[] { "https://evil.example/x", "//evil.example/x", "http://histra.net/x", "\\\\evil\\x" })
        {
            var refused = false;
            try
            {
                HistraApi.Request(HttpMethod.Get, foreign, "hst_0123456789").Dispose();
            }
            catch (ArgumentException)
            {
                refused = true;
            }

            Assert.True(refused, "token never sent elsewhere: " + foreign);
        }
    }
}
