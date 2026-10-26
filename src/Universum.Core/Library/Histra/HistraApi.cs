using System.Net.Http.Headers;

namespace Universum.Core.Library.Histra;

/// <summary>
/// HISTRA (histra.net – tracking of watched movies and TV shows): the user's own token goes as a Bearer header with
/// every API request ("Authorization: Bearer &lt;token&gt;"), the API lives at https://histra.net. Which requests
/// UNIVERSUM makes is decided once the endpoints are described; until then nothing is sent to HISTRA.
/// </summary>
public static class HistraApi
{
    /// <summary>Base address of the HISTRA API.</summary>
    public static Uri BaseAddress { get; } = new("https://histra.net/");

    /// <summary>The HISTRA settings page where the user generates a token and reads the API documentation.</summary>
    public static Uri TokenPage { get; } = new("https://histra.net/settings/tracking-api");

    public const int MaxTokenLength = 4096;

    /// <summary>
    /// The token as it may go into a Bearer header (RFC 6750 "token68": letters, digits and - . _ ~ + /, then '='
    /// padding); anything else – spaces, line breaks, quotes – is refused, so the token can never alter the request.
    /// </summary>
    public static bool IsValidToken(string? token)
    {
        if (token is not { Length: >= 8 and <= MaxTokenLength })
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
        IsValidToken(token) ? new AuthenticationHeaderValue("Bearer", token) : throw new ArgumentException("Neplatný token HISTRA.", nameof(token));

    /// <summary>
    /// A request to the HISTRA API with the token; the path is relative to <see cref="BaseAddress"/> and the request can
    /// only go to histra.net over HTTPS (the token is never sent anywhere else).
    /// </summary>
    public static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        if (!Uri.TryCreate(path, UriKind.Relative, out var relative) || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\'))
        {
            throw new ArgumentException("Cesta API HISTRA musí byť relatívna.", nameof(path));
        }

        var address = new Uri(BaseAddress, relative);
        if (address.Scheme != Uri.UriSchemeHttps || address.Host != BaseAddress.Host || !address.IsDefaultPort)
        {
            throw new ArgumentException("Požiadavka HISTRA môže ísť len na https://histra.net.", nameof(path));
        }

        var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = Authorization(token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static readonly System.Buffers.SearchValues<char> Token68 =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~+/");
}
