using System.Net.Http.Headers;

namespace Universum.Core.Library.Histra;

/// <summary>
/// The user's own token for HISTRA (histra.net – tracking of watched movies and TV shows). HISTRA takes it as a Bearer
/// header with every API request ("Authorization: Bearer &lt;token&gt;"). Only the token is kept here: which requests
/// UNIVERSUM makes is decided once the HISTRA API is described; until then nothing is sent to HISTRA.
/// </summary>
public static class HistraToken
{
    /// <summary>The HISTRA settings page where the user generates a token and reads the API documentation.</summary>
    public static Uri TokenPage { get; } = new("https://histra.net/settings/tracking-api");

    public const int MaxLength = 4096;

    /// <summary>
    /// The token as it may go into a Bearer header (RFC 6750 "token68": letters, digits and - . _ ~ + /, then '='
    /// padding); anything else – spaces, line breaks, quotes – is refused, so the token can never alter the request.
    /// </summary>
    public static bool IsValid(string? token)
    {
        if (token is not { Length: >= 8 and <= MaxLength })
        {
            return false;
        }

        var end = token.Length;
        while (end > 0 && token[end - 1] == '=')
        {
            end--;
        }

        return end > 0 && token.AsSpan(0, end).IndexOfAnyExcept(Token68) < 0;
    }

    /// <summary>The header for a HISTRA request.</summary>
    public static AuthenticationHeaderValue Authorization(string token) =>
        IsValid(token) ? new AuthenticationHeaderValue("Bearer", token) : throw new ArgumentException("Neplatný token HISTRA.", nameof(token));

    private static readonly System.Buffers.SearchValues<char> Token68 =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~+/");
}
