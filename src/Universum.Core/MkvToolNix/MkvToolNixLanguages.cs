using Universum.Core.Media;
using Universum.Core.Processes;

namespace Universum.Core.MkvToolNix;

/// <summary>
/// The languages the installed MKVToolNix knows (owner, 30. 10. 2026: all of them in the lists, e.g. Filipino): read from
/// <c>mkvmerge --list-languages</c> – exactly what mkvmerge accepts as a track language. Nothing of the list is shipped
/// with UNIVERSUM.
/// </summary>
public static class MkvToolNixLanguages
{
    /// <summary>The languages of the installation; empty when mkvmerge cannot say them.</summary>
    public static async Task<IReadOnlyList<Language>> ReadAsync(MkvToolNixInstallation installation, IProcessRunner runner, CancellationToken cancellationToken = default)
    {
        try
        {
            // UTF-8 whatever the console's code page (names like "Abé"); without it mkvmerge stops at the first such name
            var result = await runner.RunAsync(new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvMerge), ["--output-charset", "UTF-8", "--list-languages"])
            {
                Timeout = TimeSpan.FromSeconds(30),
            }, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? Parse(result.StandardOutput) : [];
        }
        catch (Exception ex) when (ex is TimeoutException or ProcessStartException)
        {
            return [];
        }
    }

    /// <summary>
    /// The table of mkvmerge: "English name | ISO 639-3 code | ISO 639-2 code | ISO 639-1 code" (the 639-3 column holds the
    /// 639-2/B code where there is one). The code written into files is the two-letter one, else the three-letter one.
    /// </summary>
    public static IReadOnlyList<Language> Parse(string table)
    {
        var languages = new List<Language>();
        foreach (var line in table.Split('\n'))
        {
            var cells = line.Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 4 || cells[0].Length == 0 || cells[0].StartsWith("English language name", StringComparison.Ordinal) ||
                !IsCode(cells[1], 3) || (cells[2].Length > 0 && !IsCode(cells[2], 3)) || (cells[3].Length > 0 && !IsCode(cells[3], 2)))
            {
                continue;
            }

            var ietf = cells[3].Length == 2 ? cells[3] : cells[1];
            var bibliographic = cells[2].Length == 3 ? cells[2] : cells[1];
            var terminology = cells[1] != bibliographic ? cells[1] : null;
            languages.Add(new Language(ietf, bibliographic, terminology, cells[0], Languages.SlovakNameOf(ietf) ?? cells[0]));
        }

        return languages;
    }

    private static bool IsCode(string text, int length) => text.Length == length && text.All(char.IsAsciiLetterLower);
}
