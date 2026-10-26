using System.Text.Json;

namespace Universum.Core.Updates;

/// <summary>A published UNIVERSUM release on GitHub (https://github.com/tarzanislav/universum/releases).</summary>
/// <param name="Notes">What is new (Slovak, written in the release).</param>
/// <param name="Installer">The setup program ("UNIVERSUM_Setup_0.2.0.exe").</param>
/// <param name="Checksums">SHA256SUMS.txt of the release.</param>
/// <param name="ChecksumsSignature">SHA256SUMS.txt.sig – signature of the checksum list by the UNIVERSUM release key.</param>
public sealed record UniversumRelease(Version Version, string Notes, Uri Page, Uri? Installer, Uri? Checksums, Uri? ChecksumsSignature)
{
    public const string Repository = "tarzanislav/universum";

    /// <summary>GitHub API address of the latest release (pre-releases and drafts are not "latest").</summary>
    public static Uri LatestApi { get; } = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>All files of a release must come from GitHub over HTTPS.</summary>
    public static bool IsOfficial(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps &&
        (address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         address.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
         address.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         address.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         address.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the GitHub API answer; null when it is not a usable release (no version tag, draft, pre-release).</summary>
    public static UniversumRelease? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease") ||
            Text(root, "tag_name") is not { } tag || !Version.TryParse(tag.TrimStart('v', 'V'), out var version) ||
            Text(root, "html_url") is not { } page)
        {
            return null;
        }

        Uri? installer = null, sums = null, signature = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (Text(asset, "name") is not { } name || Text(asset, "browser_download_url") is not { } url ||
                    !Uri.TryCreate(url, UriKind.Absolute, out var address) || !IsOfficial(address))
                {
                    continue;
                }

                if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sums = address;
                }
                else if (name.Equals("SHA256SUMS.txt.sig", StringComparison.OrdinalIgnoreCase))
                {
                    signature = address;
                }
                else if (name.StartsWith("UNIVERSUM", StringComparison.OrdinalIgnoreCase) && name.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                         name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    installer = address;
                }
            }
        }

        return new UniversumRelease(version, Text(root, "body")?.Trim() ?? "", new Uri(page), installer, sums, signature);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
