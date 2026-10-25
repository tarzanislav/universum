using System.Text.RegularExpressions;

namespace Universum.Core.MkvToolNix;

public static partial class MkvToolNixVersion
{
    /// <summary>Oldest MKVToolNix version UNIVERSUM supports (confirmed by the CI self-test).</summary>
    public static Version Minimum { get; } = new(100, 0);

    /// <summary>
    /// Reads the version from "mkvmerge --version" output, e.g. "mkvmerge v103.0 ('The Darkness') 64-bit".
    /// </summary>
    public static bool TryParseToolOutput(string output, out Version version)
    {
        var match = ToolVersion().Match(output);
        if (match.Success)
        {
            return TryParse(match.Groups[1].Value, out version);
        }

        version = new Version(0, 0);
        return false;
    }

    /// <summary>Parses "103.0", "103" or "81.0.1" into a <see cref="Version"/> with at least major.minor.</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().TrimStart('v', 'V');
        if (!trimmed.Contains('.'))
        {
            trimmed += ".0";
        }

        if (!Version.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    [GeneratedRegex(@"\bv(\d+(?:\.\d+){0,2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex ToolVersion();
}
