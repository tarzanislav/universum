using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Universum.Core.Security;
using Universum.Core.Updates;

namespace Universum.App.Setup;

/// <summary>
/// Downloads a UNIVERSUM release and starts its installer only when the checksum list is signed by the UNIVERSUM release
/// key and the installer has the SHA-256 the list names. Nothing unverified is ever started.
/// </summary>
public static class UniversumInstaller
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromMinutes(10) };

    static UniversumInstaller() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("UNIVERSUM-updater");

    /// <summary>A downloaded installer and the signed SHA-256 it must still have when it is started.</summary>
    public sealed record VerifiedInstaller(string Path, string Sha256);

    public static async Task<VerifiedInstaller> DownloadAsync(UniversumRelease release, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (release.Installer is null || release.Checksums is null || release.ChecksumsSignature is null || !ReleaseSignature.IsConfigured)
        {
            throw new SetupException("Toto vydanie UNIVERSUM nie je podpísané – inštalujte ho ručne zo stránky vydania.");
        }

        var name = Path.GetFileName(release.Installer.AbsolutePath);
        var expected = ReleaseSignature.ExpectedInstallerHash(
            await GetBytesAsync(release.Checksums, cancellationToken), await GetBytesAsync(release.ChecksumsSignature, cancellationToken), name)
            ?? throw new SetupException("Podpis vydania UNIVERSUM nesedí – inštalátor sa nestiahol ani nespustil.");

        var folder = Path.Combine(Path.GetTempPath(), "UNIVERSUM", "update-" + release.Version);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        await DownloadFileAsync(release.Installer, target, progress, cancellationToken);
        if (!FileHashes.Matches(expected, await FileHashes.Sha256Async(target, cancellationToken)))
        {
            File.Delete(target);
            throw new SetupException("Stiahnutý inštalátor UNIVERSUM nemá podpísaný kontrolný súčet, preto sa zmazal.");
        }

        return new VerifiedInstaller(target, expected);
    }

    /// <summary>
    /// Starts the installer (it restarts UNIVERSUM afterwards); false when the user declined. The file is locked against
    /// changes and checked once more while it is started, so it cannot be swapped between the check and the start.
    /// </summary>
    public static bool Start(VerifiedInstaller installer)
    {
        using var locked = new FileStream(installer.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!FileHashes.Matches(installer.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(locked))))
        {
            throw new SetupException("Inštalátor UNIVERSUM sa po overení zmenil – nespustil sa.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(installer.Path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /SPUSTIT=1") { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // the user declined the User Account Control prompt
        }
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
            throw new SetupException($"Vydanie UNIVERSUM sa nepodarilo stiahnuť.\n({ex.Message})", ex);
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
            throw new SetupException($"Inštalátor UNIVERSUM sa nepodarilo stiahnuť.\n({ex.Message})", ex);
        }
    }

    private static void RequireOfficial(Uri address)
    {
        if (!UniversumRelease.IsOfficial(address))
        {
            throw new SetupException($"Adresa {address} nie je stránka vydaní UNIVERSUM na GitHube, sťahovanie sa zastavilo.");
        }
    }
}
