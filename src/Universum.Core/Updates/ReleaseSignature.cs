using System.Security.Cryptography;
using System.Text;
using Universum.Core.Security;

namespace Universum.Core.Updates;

/// <summary>
/// Checks that the checksum list of a UNIVERSUM release was signed with the UNIVERSUM release key (ECDSA P-256,
/// SHA-256). The public key is built into the program; the private key exists only as a secret of the release
/// workflow. Together with the SHA-256 of the installer this means a changed or replaced installer – even one
/// uploaded to the GitHub release by someone else – is never started.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>
    /// Public release key (SubjectPublicKeyInfo, Base64). Empty until the release key is created; while empty,
    /// UNIVERSUM does not install its own updates and only opens the release page.
    /// </summary>
    public const string PublicKey = "";

    public static bool IsConfigured => PublicKey.Length > 0;

    /// <summary>True when <paramref name="signature"/> (IEEE P1363 or DER) signs <paramref name="checksums"/> with the given key.</summary>
    public static bool Verify(ReadOnlySpan<byte> checksums, ReadOnlySpan<byte> signature, string publicKey = PublicKey)
    {
        if (publicKey.Length == 0)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(checksums, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation) ||
                   key.VerifyData(checksums, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The SHA-256 the installer must have, taken from a checksum list signed by the release key; null when the list is
    /// not signed by the key, is not the list of <paramref name="version"/> (its first line "# UNIVERSUM 1.2.3" is
    /// signed too, so an older signed release cannot be passed off as a newer one) or does not name the installer
    /// "UNIVERSUM_Setup_1.2.3.exe".
    /// </summary>
    public static string? ExpectedInstallerHash(byte[] checksums, byte[] signature, Version version, string publicKey = PublicKey)
    {
        if (!Verify(checksums, signature, publicKey))
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(checksums);
        var header = text.Split('\n').FirstOrDefault()?.Trim();
        return header == VersionLine(version) && Sha256Sums.Parse(text).TryGetValue(InstallerFileName(version), out var hash) ? hash : null;
    }

    /// <summary>First line of the signed checksum list of a release.</summary>
    public static string VersionLine(Version version) => "# UNIVERSUM " + version.ToString(3);

    /// <summary>The only installer name a release may have.</summary>
    public static string InstallerFileName(Version version) => $"UNIVERSUM_Setup_{version.ToString(3)}.exe";
}
