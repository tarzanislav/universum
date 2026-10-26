using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Universum.Core.MkvToolNix;

namespace Universum.Core.Updates;

/// <summary>One section of a version in NEWS.md ("New features and enhancements", "Bug fixes", "Security fixes" …).</summary>
public sealed record NewsSection(string Title, string Text)
{
    /// <summary>Security fixes are highlighted in the update window (specification, section 4).</summary>
    public bool IsSecurity =>
        Title.Contains("security", StringComparison.OrdinalIgnoreCase) || Text.Contains("CVE-", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One version of MKVToolNix as NEWS.md describes it.</summary>
public sealed record NewsVersion(Version Version, string Codename, DateOnly? Date, IReadOnlyList<NewsSection> Sections)
{
    public bool HasSecurityFixes => Sections.Any(s => s.IsSecurity);

    /// <summary>"103.0 „The Darkness“ (18. 10. 2026)".</summary>
    public string Heading =>
        MkvToolNixDownloads.Format(Version) + (Codename.Length > 0 ? $" „{Codename}“" : "") +
        (Date is { } date ? " (" + date.ToString("d. M. yyyy", CultureInfo.InvariantCulture) + ")" : "");
}

/// <summary>
/// Reads the MKVToolNix change log (https://mkvtoolnix.download/doc/NEWS.md): headings "# Version 103.0 "Name" 2026-10-18"
/// with "## …" sections. The text stays in English (no translation service is used).
/// </summary>
public static partial class MkvToolNixNews
{
    public static IReadOnlyList<NewsVersion> Parse(string markdown)
    {
        var versions = new List<NewsVersion>();
        Version? version = null;
        string codename = "";
        DateOnly? date = null;
        var sections = new List<NewsSection>();
        string? sectionTitle = null;
        var text = new StringBuilder();

        void CloseSection()
        {
            if (sectionTitle is not null)
            {
                sections.Add(new NewsSection(sectionTitle, text.ToString().Trim()));
            }

            sectionTitle = null;
            text.Clear();
        }

        void CloseVersion()
        {
            CloseSection();
            if (version is not null)
            {
                versions.Add(new NewsVersion(version, codename, date, sections.ToList()));
            }

            sections.Clear();
            version = null;
        }

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (VersionHeading().Match(line) is { Success: true } heading)
            {
                CloseVersion();
                MkvToolNixVersion.TryParse(heading.Groups["version"].Value, out var parsed);
                version = parsed;
                codename = heading.Groups["name"].Value;
                date = DateOnly.TryParseExact(heading.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                continue;
            }

            if (version is null)
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                CloseSection();
                sectionTitle = line[3..].Trim();
                continue;
            }

            if (sectionTitle is not null)
            {
                text.AppendLine(line);
            }
        }

        CloseVersion();
        return versions;
    }

    /// <summary>Versions newer than <paramref name="installed"/> up to <paramref name="latest"/>, newest first.</summary>
    public static IReadOnlyList<NewsVersion> Between(IReadOnlyList<NewsVersion> news, Version? installed, Version latest) =>
        news.Where(n => (installed is null || n.Version > installed) && n.Version <= latest).OrderByDescending(n => n.Version).ToList();

    [GeneratedRegex(@"^#\s+Version\s+(?<version>\d+(?:\.\d+){0,3})\s*(?:""(?<name>[^""]*)"")?\s*(?<date>\d{4}-\d{2}-\d{2})?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionHeading();
}
