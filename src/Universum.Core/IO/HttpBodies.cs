namespace Universum.Core.IO;

/// <summary>
/// Reading the body of an HTTP answer (14. 11. 2026, step 1 of the review of the code – one way instead of a copy in each
/// client): whole into memory only up to a limit – a larger body (told by its length or found while reading) is never
/// kept – or streamed into a file with the progress.
/// </summary>
public static class HttpBodies
{
    /// <summary>The body, at most <paramref name="limit"/> bytes; null when it is larger.</summary>
    public static async Task<byte[]?> ReadAsync(HttpContent content, long limit, CancellationToken cancellationToken = default)
    {
        if (content.Headers.ContentLength > limit)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The body written into a new file; the part done (0–1) told while the length of the body is known.</summary>
    public static async Task CopyToFileAsync(HttpContent content, string target, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var total = content.Headers.ContentLength ?? 0;
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(target);
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            done += read;
            if (total > 0)
            {
                progress?.Report((double)done / total);
            }
        }
    }
}
