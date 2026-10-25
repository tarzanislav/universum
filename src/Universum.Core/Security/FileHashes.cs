using System.Security.Cryptography;

namespace Universum.Core.Security;

public static class FileHashes
{
    /// <summary>SHA-256 of a file as upper-case hexadecimal text.</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static bool Matches(string expectedHex, string actualHex) =>
        expectedHex.Length == 64 && string.Equals(expectedHex, actualHex, StringComparison.OrdinalIgnoreCase);
}
