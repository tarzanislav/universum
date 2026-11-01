using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Universum.Core.MkvToolNix;
using Universum.Core.Security;
using Universum.Platform.Windows;
using Universum.App.Localization;

namespace Universum.App.Setup;

/// <summary>Problem with downloading, verifying or running the MKVToolNix installer; the message is for the user.</summary>
public sealed class SetupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads the official MKVToolNix installer, verifies it (SHA-256 from the official checksum list and the
/// Authenticode signature of LINET Services GmbH) and runs it. Nothing unverified is ever started.
/// </summary>
public sealed class MkvToolNixInstaller
{
    private static readonly HttpClient Http = CreateClient();

    public static async Task<Version> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        var content = await GetBytesAsync(MkvToolNixDownloads.LatestReleaseFeed, cancellationToken);
        try
        {
            return MkvToolNixReleaseFeed.ParseLatestVersion(MkvToolNixReleaseFeed.Decode(content));
        }
        catch (Exception ex) when (ex is MkvToolNixException or InvalidDataException)
        {
            throw new SetupException(Loc.T("Informácie o najnovšej verzii MKVToolNix sa nedajú prečítať."), ex);
        }
    }

    /// <summary>Downloads and verifies the 64-bit installer; returns its local path.</summary>
    public static async Task<string> DownloadAsync(Version version, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var fileName = MkvToolNixDownloads.WindowsInstallerFileName(version);
        var sums = Sha256Sums.Parse(System.Text.Encoding.UTF8.GetString(
            await GetBytesAsync(MkvToolNixDownloads.WindowsSha256Sums(version), cancellationToken)));
        if (!sums.TryGetValue(fileName, out var expectedHash))
        {
            throw new SetupException(Loc.F("Oficiálny zoznam kontrolných súčtov neobsahuje súbor {0}.", fileName));
        }

        // a new folder with a random name: nothing can be prepared there in advance
        var folder = Directory.CreateTempSubdirectory("UNIVERSUM-mkvtoolnix-").FullName;
        var target = Path.Combine(folder, fileName);
        await DownloadFileAsync(MkvToolNixDownloads.WindowsReleaseFile(version, fileName), target, progress, cancellationToken);

        var actualHash = await FileHashes.Sha256Async(target, cancellationToken);
        if (!FileHashes.Matches(expectedHash, actualHash))
        {
            File.Delete(target);
            throw new SetupException(Loc.T("Stiahnutý inštalátor MKVToolNix nemá správny kontrolný súčet SHA-256, preto sa zmazal."));
        }

        var signature = AuthenticodeVerifier.Verify(target);
        if (signature.IsSignedBy(MkvToolNixDownloads.WindowsSignerName) && !signature.RevocationChecked)
        {
            File.Delete(target);
            throw new SetupException(Loc.T("Nepodarilo sa overiť, či podpis inštalátora MKVToolNix nebol odvolaný (servery certifikátov sú nedostupné). Skúste to neskôr."));
        }

        if (!signature.IsSignedBy(MkvToolNixDownloads.WindowsSignerName))
        {
            File.Delete(target);
            throw new SetupException(
                Loc.F("Stiahnutý inštalátor MKVToolNix nemá platný podpis vydavateľa {0}, preto sa zmazal. (stav 0x{1:X8}, podpísal: {2})",
                    MkvToolNixDownloads.WindowsSignerName, signature.Status, signature.SignerName ?? Loc.T("nikto")));
        }

        return target;
    }

    /// <summary>Runs the verified installer and waits for it. Returns false when the user declined the Windows prompt.</summary>
    public static async Task<bool> RunAsync(string installerPath, bool silent, CancellationToken cancellationToken = default)
    {
        // locked against changes and checked once more, so it cannot be swapped between the check and the start
        using var locked = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (AuthenticodeVerifier.Verify(installerPath) is not { RevocationChecked: true } verified || !verified.IsSignedBy(MkvToolNixDownloads.WindowsSignerName))
        {
            throw new SetupException(Loc.T("Inštalátor MKVToolNix sa po overení zmenil – nespustil sa."));
        }

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true, // the installer requests administrator rights itself
                Arguments = silent ? "/S" : "",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // the user declined the User Account Control prompt
        }

        if (process is null)
        {
            throw new SetupException(Loc.T("Inštalátor MKVToolNix sa nepodarilo spustiť."));
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken);
        }

        return true;
    }

    private static async Task<byte[]> GetBytesAsync(Uri address, CancellationToken cancellationToken)
    {
        RequireOfficial(address);
        try
        {
            using var response = await Http.GetAsync(address, cancellationToken);
            RequireOfficial(response.RequestMessage?.RequestUri ?? address);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new SetupException(Loc.F("Stránka MKVToolNix nie je dostupná. Skontrolujte pripojenie na internet.\n({0})", ex.Message), ex);
        }
    }

    private static async Task DownloadFileAsync(Uri address, string target, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        RequireOfficial(address);
        try
        {
            using var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            RequireOfficial(response.RequestMessage?.RequestUri ?? address);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(target);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            throw new SetupException(Loc.F("Inštalátor MKVToolNix sa nepodarilo stiahnuť.\n({0})", ex.Message), ex);
        }
    }

    private static void RequireOfficial(Uri address)
    {
        if (!MkvToolNixDownloads.IsOfficial(address))
        {
            throw new SetupException(Loc.F("Adresa {0} nie je oficiálna stránka MKVToolNix, sťahovanie sa zastavilo.", address));
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 })
        {
            Timeout = TimeSpan.FromMinutes(10),
            MaxResponseContentBufferSize = 16 << 20, // feed and checksum lists; installers are streamed
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"UNIVERSUM/{typeof(MkvToolNixInstaller).Assembly.GetName().Version}");
        return client;
    }
}
