namespace Universum.Core.MkvToolNix;

/// <summary>Official MKVToolNix download addresses (verified on mkvtoolnix.download, 25 October 2026).</summary>
public static class MkvToolNixDownloads
{
    public const string Host = "mkvtoolnix.download";

    /// <summary>Same feed the MKVToolNix GUI uses for its daily update check (gzip-compressed XML).</summary>
    public static Uri LatestReleaseFeed { get; } = new("https://mkvtoolnix.download/latest-release.xml.gz");

    /// <summary>Release notes for all versions.</summary>
    public static Uri News { get; } = new("https://mkvtoolnix.download/doc/NEWS.md");

    /// <summary>The Authenticode signer of the official Windows binaries since version 82.0.</summary>
    public const string WindowsSignerName = "LINET Services GmbH";

    public static string WindowsInstallerFileName(Version version) => $"mkvtoolnix-64-bit-{Format(version)}-setup.exe";

    public static string WindowsPortableFileName(Version version) => $"mkvtoolnix-64-bit-{Format(version)}.7z";

    public static Uri WindowsReleaseFile(Version version, string fileName) =>
        new($"https://mkvtoolnix.download/windows/releases/{Format(version)}/{Uri.EscapeDataString(fileName)}");

    public static Uri WindowsSha256Sums(Version version) => WindowsReleaseFile(version, "sha256sums.txt");

    /// <summary>Only HTTPS addresses on the official host are accepted for downloads.</summary>
    public static bool IsOfficial(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps && address.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Version text as used in MKVToolNix file names: "103.0", "81.0.1".</summary>
    public static string Format(Version version) =>
        version.Build > 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{Math.Max(0, version.Minor)}";
}
