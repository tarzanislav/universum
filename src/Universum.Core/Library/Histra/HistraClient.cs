using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Universum.Core.Library.Histra;

/// <summary>A HISTRA problem the user can act on; the message is ready to show.</summary>
public sealed class HistraException(string message, Exception? inner = null) : TitleServiceException(message, inner);

/// <summary>
/// Talks to the HISTRA API (https://histra.net) with the user's token as a Bearer header. Requests go only to
/// histra.net over HTTPS, one at a time through <see cref="RequestPacer.Histra"/> (2 per second); "too many requests"
/// (429) and passing server errors are waited out and retried as with TMDB; answers are limited to
/// <see cref="MaxResponseBytes"/>. UNIVERSUM uses HISTRA only to search its catalog (<see cref="HistraCatalog"/>).
/// </summary>
public sealed class HistraClient
{
    /// <summary>The largest answer accepted (a protection, HISTRA answers are small JSON documents).</summary>
    public const int MaxResponseBytes = 4 << 20;

    private const int MaxRetriesWhenLimited = 4;
    private const int MaxRetriesOnServerError = 2;

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly RequestPacer _pacer;

    /// <param name="pacer">Spacing of requests; the shared <see cref="RequestPacer.Histra"/> by default.</param>
    public HistraClient(HttpClient http, string token, RequestPacer? pacer = null)
    {
        if (!HistraApi.IsValidToken(token?.Trim()))
        {
            throw new ArgumentException("Neplatný token HISTRA.", nameof(token));
        }

        _http = http;
        _token = token!.Trim();
        _pacer = pacer ?? RequestPacer.Histra;
    }

    /// <summary>Raised when HISTRA asked to slow down and the client waits before trying again.</summary>
    public event Action<TimeSpan>? Waiting;

    /// <summary>GET of an API path (relative to https://histra.net); null when HISTRA has nothing there (404).</summary>
    public Task<JsonDocument?> GetAsync(string path, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, path, null, cancellationToken);

    /// <summary>
    /// A request with an optional JSON body; returns the JSON answer, or null for 404 and answers without content.
    /// </summary>
    public async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken = default)
    {
        var json = body is null ? null : JsonSerializer.Serialize(body);
        for (var attempt = 0; ; attempt++)
        {
            using var request = HistraApi.Request(method, path, _token);
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await _pacer.RunAsync(
                    () => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new HistraException("HISTRA nie je dostupná: " + ex.Message, ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HistraException("HISTRA neodpovedá.", ex);
            }

            using (response)
            {
                // a redirect may not lead the answer elsewhere (the token itself is never sent on after a redirect)
                if (response.RequestMessage?.RequestUri is { } final &&
                    (final.Scheme != Uri.UriSchemeHttps || final.Host != HistraApi.BaseAddress.Host))
                {
                    throw new HistraException($"HISTRA presmerovala na inú adresu ({final.Host}) – odpoveď sa nepoužila.");
                }

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway
                    or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError)
                {
                    var limited = response.StatusCode == HttpStatusCode.TooManyRequests;
                    if (attempt >= (limited ? MaxRetriesWhenLimited : MaxRetriesOnServerError))
                    {
                        throw new HistraException(limited
                            ? "HISTRA obmedzila počet požiadaviek a ani po čakaní ich neprijíma – skúste to o niekoľko minút."
                            : $"HISTRA má problém ({(int)response.StatusCode}) – skúste to neskôr.");
                    }

                    var delay = RequestPacer.RetryDelay(response, attempt);
                    _pacer.PauseFor(delay);
                    Waiting?.Invoke(delay);
                    continue;
                }

                switch (response.StatusCode)
                {
                    case HttpStatusCode.NotFound or HttpStatusCode.NoContent:
                        return null;
                    case HttpStatusCode.Unauthorized:
                        throw new HistraException("Token HISTRA nie je platný – skontrolujte ho v Nastaveniach.");
                    case HttpStatusCode.Forbidden:
                        throw new HistraException("Token HISTRA na túto akciu nemá oprávnenie.");
                    case >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous:
                        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                    default:
                        throw new HistraException($"HISTRA odpovedala chybou {(int)response.StatusCode}.");
                }
            }
        }
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new HistraException("Odpoveď HISTRA je neprimerane veľká – nepoužila sa.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new HistraException("Odpoveď HISTRA je neprimerane veľká – nepoužila sa.");
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (JsonException ex)
        {
            throw new HistraException("HISTRA vrátila nečitateľnú odpoveď.", ex);
        }
    }
}
