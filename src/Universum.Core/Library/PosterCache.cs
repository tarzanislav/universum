using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Universum.Core.Library;

/// <summary>How large a poster is downloaded: a small one for lists, a larger one for the detail of a title.</summary>
public enum PosterSize
{
    Small,
    Large,

    /// <summary>The poster as it was uploaded (TMDB: up to 2000 × 3000) – for folder.jpg.</summary>
    Original,
}

/// <summary>
/// Posters of films and series, downloaded from the poster server in the settings (TMDB's image server by default –
/// HISTRA gives poster paths in TMDB's form) and kept on the disk, so each poster is downloaded once. Only paths of the
/// form "/name.jpg" (or .png) are accepted, only from the configured HTTPS server (no redirect elsewhere), at most
/// <see cref="MaxBytes"/>, and only a real JPEG or PNG picture is stored. A poster that cannot be had is asked for again
/// only in the next run.
/// </summary>
public sealed partial class PosterCache
{
    /// <summary>TMDB's image server (posters in sizes w154 and w342).</summary>
    public static readonly Uri DefaultServer = new("https://image.tmdb.org/t/p/");

    /// <summary>The largest poster accepted.</summary>
    public const int MaxBytes = 2 << 20;

    /// <summary>The largest original poster accepted.</summary>
    public const int MaxOriginalBytes = 40 << 20;

    private readonly HttpClient _http;
    private readonly string _folder;
    private readonly SemaphoreSlim _parallel = new(4);
    private readonly ConcurrentDictionary<string, Task<string?>> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _failed = new(StringComparer.Ordinal);

    public PosterCache(HttpClient http, string folder, Uri server)
    {
        if (!IsValidServer(server))
        {
            throw new ArgumentException("Server plagátov musí byť adresa https:// bez prihlasovacích údajov.", nameof(server));
        }

        _http = http;
        _folder = folder;
        Server = server;
    }

    /// <summary>The server the posters come from (ends with '/').</summary>
    public Uri Server { get; }

    /// <summary>A server address from the settings: HTTPS, no user name, query or fragment; '/' is added at the end.</summary>
    public static Uri? ParseServer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!uri.AbsoluteUri.EndsWith('/'))
        {
            uri = new Uri(uri.AbsoluteUri + "/");
        }

        return IsValidServer(uri) ? uri : null;
    }

    private static bool IsValidServer(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsoluteUri.EndsWith('/');

    /// <summary>A poster path as the services give it ("/abc123.jpg").</summary>
    public static bool IsValidPath(string? path) => path is not null && PosterPath().IsMatch(path);

    /// <summary>The address a poster is downloaded from.</summary>
    public Uri Address(string path, PosterSize size) => new(Server, SizeFolder(size) + path);

    /// <summary>The poster as a file on the disk, downloaded when it is not there yet; null when it cannot be had.</summary>
    public Task<string?> GetAsync(string? path, PosterSize size, CancellationToken cancellationToken = default)
    {
        if (!IsValidPath(path))
        {
            return Task.FromResult<string?>(null);
        }

        var file = Path.Combine(_folder, SizeFolder(size), path!.TrimStart('/'));
        if (File.Exists(file))
        {
            return Task.FromResult<string?>(file);
        }

        if (_failed.ContainsKey(file))
        {
            return Task.FromResult<string?>(null);
        }

        // one download per poster, however many rows ask for it
        return _running.GetOrAdd(file, _ => DownloadAsync(Address(path, size), file, size == PosterSize.Original ? MaxOriginalBytes : MaxBytes, cancellationToken));
    }

    private async Task<string?> DownloadAsync(Uri address, string file, long limit, CancellationToken cancellationToken)
    {
        await _parallel.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Host != Server.Host ||
                response.Content.Headers.ContentLength > limit)
            {
                _failed[file] = true;
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    _failed[file] = true;
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            if (!IsPicture(bytes))
            {
                _failed[file] = true;
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".part";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, file, overwrite: true);
            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _failed[file] = true;
            }

            return null;
        }
        finally
        {
            _parallel.Release();
            _running.TryRemove(file, out _);
        }
    }

    /// <summary>The bytes are a JPEG or PNG picture (checked by their signature, not by the name).</summary>
    public static bool IsPicture(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 8 && ((bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) ||
                             bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));

    private static string SizeFolder(PosterSize size) => size switch
    {
        PosterSize.Large => "w342",
        PosterSize.Original => "original",
        _ => "w154",
    };

    [GeneratedRegex("^/[A-Za-z0-9_-]{1,100}\\.(jpg|jpeg|png)$", RegexOptions.CultureInvariant)]
    private static partial Regex PosterPath();
}
