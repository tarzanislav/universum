using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Universum.Platform.Windows;

/// <summary>
/// Protects small secrets (the TMDB key) with the Windows data protection API for the current user: the stored text
/// can be read back only by the same Windows account on the same computer.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class UserSecret
{
    private const int UiForbidden = 0x1;

    private static readonly byte[] Entropy = "UNIVERSUM secret v1"u8.ToArray();

    public static string Protect(string secret) =>
        Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(secret), protect: true));

    /// <summary>The secret, or null when the text cannot be read (another account, damaged settings).</summary>
    public static string? Unprotect(string protectedText)
    {
        try
        {
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedText), protect: false));
        }
        catch (Exception ex) when (ex is FormatException or Win32Exception)
        {
            return null;
        }
    }

    private static unsafe byte[] Transform(byte[] data, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = data.Length, Data = (nint)input };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = (nint)entropy };
            var outBlob = default(DataBlob);
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, 0, 0, UiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, 0, ref entropyBlob, 0, 0, UiForbidden, ref outBlob);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }
}
