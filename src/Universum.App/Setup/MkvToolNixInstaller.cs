using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Universum.Core.MkvToolNix;
using Universum.Core.Security;
using Universum.Platform.Windows;

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
            throw new SetupException("Informácie o najnovšej verzii MKVToolNix sa nedajú prečítať.", ex);
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
            throw new SetupException($"Oficiálny zoznam kontrolných súčtov neobsahuje súbor {fileName}.");
        }

        var folder = Path.Combine(Path.GetTempPath(), "UNIVERSUM", "mkvtoolnix-" + MkvToolNixDownloads.Format(version));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, fileName);
        await DownloadFileAsync(MkvToolNixDownloads.WindowsReleaseFile(version, fileName), target, progress, cancellationToken);

        var actualHash = await FileHashes.Sha256Async(target, cancellationToken);
        if (!FileHashes.Matches(expectedHash, actualHash))
        {
            File.Delete(target);
            throw new SetupException("Stiahnutý inštalátor MKVToolNix nemá správny kontrolný súčet SHA-256, preto sa zmazal.");
        }

        var signature = AuthenticodeVerifier.Verify(target);
        if (!signature.IsSignedBy(MkvToolNixDownloads.WindowsSignerName))
        {
            File.Delete(target);
            throw new SetupException(
                $"Stiahnutý inštalátor MKVToolNix nemá platný podpis vydavateľa {MkvToolNixDownloads.WindowsSignerName}, preto sa zmazal. " +
                $"(stav 0x{signature.Status:X8}, podpísal: {signature.SignerName ?? "nikto"})");
        }

        return target;
    }

    /// <summary>Runs the verified installer and waits for it. Returns false when the user declined the Windows prompt.</summary>
    public static async Task<bool> RunAsync(string installerPath, bool silent, CancellationToken cancellationToken = default)
    {
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
            throw new SetupException("Inštalátor MKVToolNix sa nepodarilo spustiť.");
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
            throw new SetupException($"Stránka MKVToolNix nie je dostupná. Skontrolujte pripojenie na internet.\n({ex.Message})", ex);
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
            throw new SetupException($"Inštalátor MKVToolNix sa nepodarilo stiahnuť.\n({ex.Message})", ex);
        }
    }

    private static void RequireOfficial(Uri address)
    {
        if (!MkvToolNixDownloads.IsOfficial(address))
        {
            throw new SetupException($"Adresa {address} nie je oficiálna stránka MKVToolNix, sťahovanie sa zastavilo.");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"UNIVERSUM/{typeof(MkvToolNixInstaller).Assembly.GetName().Version}");
        return client;
    }
}
