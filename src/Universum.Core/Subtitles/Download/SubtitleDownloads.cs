using System.IO.Compression;
using System.Text;

namespace Universum.Core.Subtitles.Download;

/// <summary>
/// Subtitles from the pages that allow only a browser (owner, 30. 10. 2026: titulky.com for Czech and Slovak – its rules
/// forbid programs downloading from it): UNIVERSUM opens the search in the browser, the user downloads the subtitles,
/// and UNIVERSUM takes the downloaded file from the Downloads folder (from a ZIP archive too). Downloaded and translated
/// subtitles are saved next to the video under its name with the language ("Film.cs.srt"), so every processing finds
/// them with the video; an existing file is never overwritten.
/// </summary>
public static class SubtitleDownloads
{
    /// <summary>The search of titulky.com for a title (opened in the browser).</summary>
    public static Uri TitulkyCom(string query) => new("https://www.titulky.com/?Fulltext=" + Uri.EscapeDataString(query.Trim()));

    /// <summary>Subtitle files and archives that appeared in the folder since <paramref name="since"/> (UTC), newest first.</summary>
    public static IReadOnlyList<string> NewFiles(string folder, DateTime since)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => f.LastWriteTimeUtc >= since && (ExternalSubtitles.Extensions.Contains(f.Extension) || f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)))
                // a download still being written by the browser
                .Where(f => !f.Name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) && !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The subtitle files of a download: the file itself, or the subtitle files of a ZIP archive extracted into
    /// <paramref name="folder"/> (only their names, no folders – nothing outside it is ever written).
    /// </summary>
    public static IReadOnlyList<string> Unpack(string download, string folder)
    {
        if (!Path.GetExtension(download).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return [download];
        }

        var files = new List<string>();
        using var archive = ZipFile.OpenRead(download);
        foreach (var entry in archive.Entries.Where(e => e.Length is > 0 and < 20 * 1024 * 1024 && ExternalSubtitles.Extensions.Contains(Path.GetExtension(e.Name))))
        {
            Directory.CreateDirectory(folder);
            var target = Free(Path.Combine(folder, Path.GetFileName(entry.Name)));
            entry.ExtractToFile(target);
            files.Add(target);
        }

        return files;
    }

    /// <summary>
    /// The name subtitles get next to the video: "Film.cs.srt", "Film.en.sdh.srt", "Film.cs.forced.srt" – a free one
    /// ("Film.cs (2).srt") when it is taken.
    /// </summary>
    public static string BesideVideo(string video, string language, bool hearingImpaired = false, bool forced = false, string extension = ".srt")
    {
        // the language comes from a service too: letters only ("cs", "en", "und")
        var code = language.Length is >= 2 and <= 8 && language.All(char.IsAsciiLetterLower) ? language : "und";
        var folder = Path.GetDirectoryName(Path.GetFullPath(video))!;
        var path = Free(Path.Combine(folder, Path.GetFileNameWithoutExtension(video) + "." + code + (hearingImpaired ? ".sdh" : "") + (forced ? ".forced" : "") + extension));
        return string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), folder, StringComparison.OrdinalIgnoreCase)
            ? path
            : throw new IOException("the subtitles would be saved outside the video's folder");
    }

    /// <summary>Saves subtitles next to the video as SRT in UTF-8 (a new file; returns its path).</summary>
    public static async Task<string> SaveBesideAsync(string video, SubtitleDocument document, string language, bool hearingImpaired = false, bool forced = false,
        CancellationToken cancellationToken = default)
    {
        var path = BesideVideo(video, language, hearingImpaired, forced);
        await SrtWriter.SaveAsync(document, path, cancellationToken).ConfigureAwait(false);
        return path;
    }

    // the path itself, or with " (2)", " (3)" … when the file exists
    private static string Free(string path)
    {
        var candidate = path;
        for (var copy = 2; File.Exists(candidate); copy++)
        {
            candidate = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({copy}){Path.GetExtension(path)}");
        }

        return candidate;
    }

    /// <summary>Text of a downloaded subtitle file in any encoding (OpenSubtitles may send a ZIP or plain text).</summary>
    public static string Text(byte[] content) => SubtitleEncoding.Decode(content).Text;

    /// <summary>The bytes of an SRT text in UTF-8.</summary>
    public static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
}
