using System.Globalization;
using Universum.Core.Text;

namespace Universum.Core.Subtitles.Download;

/// <summary>
/// How well downloaded subtitles fit the video (owner, 30. 10. 2026): the release they were made for compared with the
/// name of the video file (resolution, source, codec, the group at the end), and their frame rate with the film's – not
/// every subtitle says its frame rate.
/// </summary>
public static class ReleaseMatch
{
    // the words of a release name that tell one release of a film from another
    private static readonly HashSet<string> ReleaseWords = new(
        [
            "480p", "576p", "720p", "1080p", "1080i", "2160p", "4k", "uhd", "bluray", "bdrip", "brrip", "bdremux", "remux", "web", "webrip", "webdl", "dl",
            "hdtv", "dvdrip", "dvd", "hdrip", "x264", "x265", "h264", "h265", "hevc", "avc", "xvid", "divx", "av1", "hdr", "hdr10", "dv", "10bit", "proper",
            "repack", "extended", "imax", "amzn", "nf", "dsnp", "hmax", "atvp", "pcok", "hulu", "dd5", "ddp5", "dts", "truehd", "atmos", "aac", "ac3",
        ],
        StringComparer.Ordinal);

    /// <summary>0–1: the share of release words both names have, more when the release group is the same.</summary>
    public static double Score(string? release, string video)
    {
        if (string.IsNullOrWhiteSpace(release))
        {
            return 0;
        }

        var (words, group) = Parts(release);
        var (videoWords, videoGroup) = Parts(Path.GetFileNameWithoutExtension(video));
        var union = words.Union(videoWords).Count();
        var share = union == 0 ? 0 : (double)words.Intersect(videoWords).Count() / union;
        var sameGroup = group is not null && group == videoGroup;
        return Math.Min(1, (share * 0.7) + (sameGroup ? 0.3 : 0));
    }

    /// <summary>"FPS sedí", "FPS 25 ≠ film 23,976" or "FPS neuvedené".</summary>
    public static string FpsNote(double? subtitles, double? film)
    {
        var sk = CultureInfo.GetCultureInfo("sk-SK");
        if (subtitles is not { } fps || fps <= 0)
        {
            return "FPS neuvedené";
        }

        if (film is not { } video)
        {
            return $"FPS {fps.ToString("0.###", sk)}";
        }

        return Math.Abs(fps - video) < 0.01 ? "FPS sedí" : $"FPS {fps.ToString("0.###", sk)} ≠ film {video.ToString("0.###", sk)} – treba zmenu FPS";
    }

    /// <summary>The frame rates agree (or the subtitles do not say theirs).</summary>
    public static bool FpsFits(double? subtitles, double? film) => subtitles is not > 0 || film is null || Math.Abs(subtitles.Value - film.Value) < 0.01;

    // the release words of a name and its group (the word after the last dash: "…x264-GROUP")
    private static (HashSet<string> Words, string? Group) Parts(string name)
    {
        var dash = name.LastIndexOf('-');
        var group = dash > 0 && dash < name.Length - 1 && !name[(dash + 1)..].Contains(' ', StringComparison.Ordinal)
            ? Words.Normalize(name[(dash + 1)..]).Replace(" ", "", StringComparison.Ordinal)
            : null;
        var words = Words.Normalize(name.Replace("WEB-DL", "WEBDL", StringComparison.OrdinalIgnoreCase)).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(ReleaseWords.Contains).ToHashSet(StringComparer.Ordinal);
        return (words, group is { Length: > 0 } ? group : null);
    }
}
