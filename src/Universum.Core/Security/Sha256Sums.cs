namespace Universum.Core.Security;

/// <summary>Parses checksum lists in the "sha256sum" format: "&lt;hash&gt;  &lt;file&gt;" or "&lt;hash&gt; *&lt;file&gt;".</summary>
public static class Sha256Sums
{
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var space = line.IndexOfAny([' ', '\t']);
            if (space != 64 || !IsHex(line.AsSpan(0, 64)))
            {
                continue;
            }

            var fileName = line[space..].Trim().TrimStart('*');
            fileName = Path.GetFileName(fileName.Replace('\\', '/'));
            if (fileName.Length > 0)
            {
                result[fileName] = line[..64].ToUpperInvariant();
            }
        }

        return result;
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
