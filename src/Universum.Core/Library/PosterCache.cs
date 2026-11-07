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
/// form "/name.jpg" (or .png, .webp) are accepted, only from the configured HTTPS server (no redirect elsewhere), at most
/// <see cref="MaxBytes"/>, and only a real JPEG, PNG or WebP picture is stored (kept as it came; what it is, is told by its content). A poster that cannot be had is asked for again
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

        // one download per poster, however many rows ask for it – not cancelled with the first one who asked (the others
        // still wait for it); each caller stops waiting with its own token
        return _running.GetOrAdd(file, _ => DownloadAsync(Address(path, size), file, size == PosterSize.Original ? MaxOriginalBytes : MaxBytes, size != PosterSize.Original))
            .WaitAsync(cancellationToken);
    }

    // remember: a poster that cannot be had is not asked for again in this run (an original is – a passing network error
    // must not keep folder.jpg from being written)
    private async Task<string?> DownloadAsync(Uri address, string file, long limit, bool remember)
    {
        var cancellationToken = CancellationToken.None;
        await _parallel.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // JPEG asked for first: a server that can give the same picture as WebP gives the JPEG (a WebP is read too)
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.ParseAdd("image/jpeg");
            request.Headers.Accept.ParseAdd("image/png;q=0.9");
            request.Headers.Accept.ParseAdd("image/webp;q=0.8");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Host != Server.Host ||
                response.Content.Headers.ContentLength > limit)
            {
                Fail(file, remember);
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
                    Fail(file, remember);
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            if (!IsPicture(bytes))
            {
                Fail(file, remember);
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
                Fail(file, remember);
            }

            return null;
        }
        finally
        {
            _parallel.Release();
            _running.TryRemove(file, out _);
        }
    }

    private void Fail(string file, bool remember)
    {
        if (remember)
        {
            _failed[file] = true;
        }
    }

    /// <summary>
    /// The bytes are a JPEG, PNG or WebP picture (checked by their signature, not by the name: a WebP can come under a name
    /// ending .jpg – it is converted, never only renamed, when it is written into a library).
    /// </summary>
    public static bool IsPicture(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 8 && Pictures.PictureFormats.Of(bytes) is Pictures.PictureKind.Jpeg or Pictures.PictureKind.Png or Pictures.PictureKind.WebP;

    private static string SizeFolder(PosterSize size) => size switch
    {
        PosterSize.Large => "w342",
        PosterSize.Original => "original",
        _ => "w154",
    };

    [GeneratedRegex("^/[A-Za-z0-9_-]{1,100}\\.(jpg|jpeg|png|webp)$", RegexOptions.CultureInvariant)]
    private static partial Regex PosterPath();
}
