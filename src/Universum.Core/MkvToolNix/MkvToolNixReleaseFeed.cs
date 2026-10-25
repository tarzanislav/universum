using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Universum.Core.MkvToolNix;

/// <summary>Reads the latest released version from the MKVToolNix release feed (latest-release.xml).</summary>
public static class MkvToolNixReleaseFeed
{
    /// <summary>Decompresses the gzip feed if needed and returns its XML text.</summary>
    public static string Decode(byte[] content)
    {
        var isGzip = content.Length > 2 && content[0] == 0x1F && content[1] == 0x8B;
        if (!isGzip)
        {
            return System.Text.Encoding.UTF8.GetString(content);
        }

        using var input = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
        using var reader = new StreamReader(input, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Returns the latest stable version. The feed lists the latest source release in an element whose name starts
    /// with "latest-source"; if the layout changes, the highest version found in any "version" element is used.
    /// </summary>
    /// <exception cref="MkvToolNixException">The feed contains no version.</exception>
    public static Version ParseLatestVersion(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new MkvToolNixException($"The MKVToolNix release feed cannot be read: {ex.Message}", ex);
        }

        var versions = document.Descendants()
            .Where(e => e.Name.LocalName.Equals("version", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Element: e, Ok: MkvToolNixVersion.TryParse(e.Value, out var v), Version: v))
            .Where(x => x.Ok)
            .ToList();

        var source = versions.FirstOrDefault(x =>
            x.Element.Parent?.Name.LocalName.StartsWith("latest-source", StringComparison.OrdinalIgnoreCase) == true);
        if (source.Ok)
        {
            return source.Version;
        }

        if (versions.Count == 0)
        {
            throw new MkvToolNixException("The MKVToolNix release feed contains no version.");
        }

        return versions.Max(x => x.Version)!;
    }
}
