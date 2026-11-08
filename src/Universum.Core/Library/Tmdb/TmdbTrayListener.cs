using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Universum.Core.Library.Tmdb;

/// <summary>
/// The door of the Zásobník TMDB for UNIVERSUM's script in the browser (owner, 8. 11. 2026: the button "Do UNIVERSUM" and
/// every film or series opened on TMDB sent by itself): a tiny HTTP receiver of UNIVERSUM's own, only on this computer
/// (127.0.0.1 – nothing from the network reaches it), that takes a page only with the secret key of this installation
/// and only when it is a film or series of TMDB. It also hands out the script with the key in it (to install it in
/// Tampermonkey or Violentmonkey). Nothing else is answered.
/// </summary>
public sealed class TmdbTrayListener : IDisposable
{
    private const int MaxRequest = 16 * 1024;
    private readonly TcpListener _listener;
    private readonly string _key;
    private readonly Func<TmdbTrayEntry, Task> _received;
    private readonly Func<string> _script;
    private readonly Func<bool> _automatic;
    private readonly CancellationTokenSource _stop = new();

    private TmdbTrayListener(TcpListener listener, string key, Func<TmdbTrayEntry, Task> received, Func<string> script, Func<bool> automatic)
    {
        _automatic = automatic;
        _listener = listener;
        _key = key;
        _received = received;
        _script = script;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>A new secret key (32 hexadecimal characters).</summary>
    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// Listens on 127.0.0.1 (port 0: any free one); throws a <see cref="SocketException"/> when the port is taken.
    /// <paramref name="automatic"/>: whether pages the script sends by itself are taken now (owner, 8. 11. 2026: the
    /// automatic gathering is switched on and off in UNIVERSUM; the button of the page is always taken).
    /// </summary>
    public static TmdbTrayListener Start(int port, string key, Func<TmdbTrayEntry, Task> received, Func<string> script, Func<bool>? automatic = null)
    {
        if (key.Length < 16)
        {
            throw new ArgumentException("the key is too short", nameof(key));
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var door = new TmdbTrayListener(listener, key, received, script, automatic ?? (() => true));
        _ = Task.Run(door.AcceptAsync);
        return door;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(client));
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                var request = await ReadAsync(stream, timeout.Token).ConfigureAwait(false);
                var (status, type, body) = request is null ? (400, "text/plain", "") : await HandleAsync(request).ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // a request that broke off or took too long: nothing to answer
            }
        }
    }

    /// <summary>What a request asks; the status, type and body of the answer.</summary>
    internal async Task<(int Status, string Type, string Body)> HandleAsync(Request request)
    {
        if (request.Method == "GET" && request.Path == "/universum.user.js")
        {
            // the script with the key, to the one who knows the key (the button in Nastavenia opens this address)
            return KeyMatches(request.Query.GetValueOrDefault("k")) ? (200, "text/javascript", _script()) : (403, "text/plain", "");
        }

        if (request.Method != "POST" || request.Path != "/tmdb")
        {
            return (404, "text/plain", "");
        }

        if (!KeyMatches(request.Headers.GetValueOrDefault("x-universum-key")))
        {
            return (403, "text/plain", "");
        }

        string? page;
        string? title;
        bool sentByItself;
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            page = json.RootElement.TryGetProperty("u", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            title = json.RootElement.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

            // "a": sent by itself (true) or by the button (false); a script of 1.0 does not say – it sent by itself
            sentByItself = !json.RootElement.TryGetProperty("a", out var a) || a.ValueKind != JsonValueKind.False;
        }
        catch (JsonException)
        {
            return (400, "text/plain", "");
        }

        if (TmdbTray.FromPage(page, title) is not { } entry)
        {
            return (422, "text/plain", "");
        }

        if (sentByItself && !_automatic())
        {
            // the automatic gathering is switched off: the page is not taken (the button of the page still adds it)
            return (202, "text/plain", "");
        }

        try
        {
            await _received(entry).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the tray could not be stored (the disk is full …): the script shows the refusal on its button
            return (500, "text/plain", "");
        }

        return (204, "text/plain", "");
    }

    private bool KeyMatches(string? key) =>
        key is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(_key));

    /// <summary>A request as UNIVERSUM's script sends it: the method, the path with its query, the headers, the body.</summary>
    internal sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers, string Body);

    // reads one request of at most 16 kB (the head and a body of Content-Length); null when it is no HTTP request
    internal static async Task<Request?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequest];
        var length = 0;
        var headEnd = -1;
        while (headEnd < 0)
        {
            if (length == buffer.Length)
            {
                return null;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
            headEnd = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        }

        var head = Encoding.ASCII.GetString(buffer, 0, headEnd).Split("\r\n");
        var first = head[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in head.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
        }

        var bodyLength = headers.TryGetValue("content-length", out var declared) && int.TryParse(declared, out var number) ? number : 0;
        if (bodyLength < 0 || headEnd + 4 + bodyLength > buffer.Length)
        {
            return null;
        }

        while (length < headEnd + 4 + bodyLength)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, headEnd + 4 + bodyLength - length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
        }

        var target = first[1];
        var question = target.IndexOf('?');
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (question >= 0)
        {
            foreach (var part in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = part.IndexOf('=');
                query[equals < 0 ? part : part[..equals]] = equals < 0 ? "" : Uri.UnescapeDataString(part[(equals + 1)..]);
            }
        }

        return new Request(first[0], question < 0 ? target : target[..question], query, headers, Encoding.UTF8.GetString(buffer, headEnd + 4, bodyLength));
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "Unprocessable Content",
    };

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _listener.Dispose();
    }
}
