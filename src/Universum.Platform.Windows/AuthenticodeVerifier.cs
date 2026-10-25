using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Universum.Platform.Windows;

/// <summary>Result of an Authenticode check of a downloaded program.</summary>
/// <param name="IsValid">Windows trusts the signature (WinVerifyTrust succeeded).</param>
/// <param name="SignerName">Common name of the signing certificate, e.g. "LINET Services GmbH".</param>
/// <param name="Status">WinVerifyTrust result code (0 = valid).</param>
/// <param name="RevocationChecked">False when the revocation servers could not be reached.</param>
public sealed record AuthenticodeResult(bool IsValid, string? SignerName, int Status, bool RevocationChecked)
{
    public bool IsSignedBy(string signerName) =>
        IsValid && string.Equals(SignerName, signerName, StringComparison.Ordinal);
}

/// <summary>Checks Authenticode signatures with the Windows trust provider (wintrust.dll).</summary>
[SupportedOSPlatform("windows")]
public static partial class AuthenticodeVerifier
{
    private const int CryptERevocationOffline = unchecked((int)0x80092013);
    private const int CertERevocationFailure = unchecked((int)0x800B010E);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static AuthenticodeResult Verify(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var status = WinVerifyTrust(fullPath, checkRevocation: true);
        var revocationChecked = true;
        if (status is CryptERevocationOffline or CertERevocationFailure)
        {
            // revocation servers unreachable: the signature itself is still checked
            status = WinVerifyTrust(fullPath, checkRevocation: false);
            revocationChecked = false;
        }

        return new AuthenticodeResult(status == 0, status == 0 ? SignerName(fullPath) : null, status, revocationChecked);
    }

    private static string? SignerName(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // the signer certificate of a signed file has no replacement API
            using var certificate = X509CertificateLoader.LoadCertificate(X509Certificate.CreateFromSignedFile(path).GetRawCertData());
#pragma warning restore SYSLIB0057
            return certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static unsafe int WinVerifyTrust(string path, bool checkRevocation)
    {
        fixed (char* filePath = path)
        {
            var fileInfo = new WintrustFileInfo
            {
                CbStruct = (uint)sizeof(WintrustFileInfo),
                FilePath = (IntPtr)filePath,
            };
            var data = new WintrustData
            {
                CbStruct = (uint)sizeof(WintrustData),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = checkRevocation ? 1u : 0u, // WTD_REVOKE_WHOLECHAIN / WTD_REVOKE_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                File = (IntPtr)(&fileInfo),
                StateAction = 1, // WTD_STATEACTION_VERIFY
                ProvFlags = checkRevocation ? 0x80u : 0x40u, // CHAIN_EXCLUDE_ROOT / WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrustNative(IntPtr.Zero, ref action, ref data);

            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrustNative(IntPtr.Zero, ref action, ref data);
            return result;
        }
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial int WinVerifyTrustNative(IntPtr hwnd, ref Guid action, ref WintrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint CbStruct;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint CbStruct;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
