using System.Globalization;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.Core.Library;

/// <summary>Size classes of results; 1 GB = 1024³ bytes, as Windows shows sizes (specification 13.4, K5).</summary>
public sealed record SizeClasses
{
    public double SmallMaxGb { get; init; } = 2;

    public double BigMaxGb { get; init; } = 14;

    public double BluRayMaxGb { get; init; } = 30;

    public string Label(long bytes)
    {
        var gb = bytes / (double)(1L << 30);
        return gb <= SmallMaxGb ? "SMALL" : gb <= BigMaxGb ? "BIG" : gb <= BluRayMaxGb ? "BluRay" : "BluRay BIG";
    }

    /// <summary>All labels, longest first (for recognising names).</summary>
    public static IReadOnlyList<string> AllLabels { get; } = ["BluRay BIG", "BluRay", "SMALL", "BIG"];
}

/// <summary>Library naming settings, part of the rules profile.</summary>
public sealed record LibraryProfile
{
    public SizeClasses Movies { get; init; } = new();

    public SizeClasses Series { get; init; } = new();

    /// <summary>Word after the language code when only subtitles are in a preferred language ("CZ titulky").</summary>
    public string SubtitlesWord { get; init; } = "titulky";

    public SizeClasses SizesFor(MediaKind kind) => kind == MediaKind.Movie ? Movies : Series;
}

/// <summary>
/// Description of a stream in its file name: "{languages} {quality} {codec} {size}", e.g. "CZ EN FHD H264 SMALL"
/// (specification, section 13.4).
/// </summary>
public static class StreamLabel
{
    /// <summary>Everything except the size class, which is known only after writing.</summary>
    public static string Stem(TargetState target, RuleProfile profile) =>
        string.Join(" ", new[] { Languages(target, profile), Quality(target), Codec(target) }.Where(p => p.Length > 0));

    /// <summary>
    /// Audio languages in the final order without commentaries ("CZ EN"). Without audio in a preferred language,
    /// full subtitles in the first preferred language that has them ("CZ titulky"); without those, the audio languages.
    /// </summary>
    public static string Languages(TargetState target, RuleProfile profile)
    {
        var audio = target.Tracks
            .Where(t => t.Type == TrackType.Audio && t.Role != TrackRole.Commentary && !t.Language.IsUndetermined)
            .Select(t => t.Language.Primary)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (audio.Any(profile.IsPreferred))
        {
            return string.Join(" ", audio.Select(Code));
        }

        foreach (var preferred in profile.PreferredLanguages)
        {
            if (target.Tracks.Any(t => t.Type == TrackType.Subtitles && t.Role is TrackRole.Main or TrackRole.HearingImpaired &&
                                       string.Equals(t.Language.Primary, preferred, StringComparison.OrdinalIgnoreCase)))
            {
                return Code(preferred) + " " + profile.Library.SubtitlesWord;
            }
        }

        return string.Join(" ", audio.Select(Code));
    }

    /// <summary>Language code in names: Czech "CZ", otherwise the language subtag in capitals ("SK", "EN", "KO").</summary>
    public static string Code(string primaryLanguage) =>
        primaryLanguage.Equals("cs", StringComparison.OrdinalIgnoreCase) ? "CZ" : primaryLanguage.ToUpperInvariant();

    /// <summary>SD, HD, FHD or 4K by the video resolution, tolerant of cropped wide formats (1920×800 is FHD).</summary>
    public static string Quality(TargetState target)
    {
        var video = target.Tracks.FirstOrDefault(t => t.Type == TrackType.Video)?.Source;
        if (video?.PixelDimensions is not { } dimensions)
        {
            return "";
        }

        var parts = dimensions.Split('x', 'X');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            return "";
        }

        return width >= 3200 || height >= 1800 ? "4K"
            : width >= 1600 || height >= 1000 ? "FHD"
            : width >= 1100 || height >= 650 ? "HD"
            : "SD";
    }

    /// <summary>Video codec: H264, HEVC, AV1; older codecs by their own names (MPEG2, VC1, MPEG4, VP9 …).</summary>
    public static string Codec(TargetState target)
    {
        var video = target.Tracks.FirstOrDefault(t => t.Type == TrackType.Video)?.Source;
        if (video is null)
        {
            return "";
        }

        var id = video.CodecId ?? "";
        var name = video.Codec.ToUpperInvariant();
        return id switch
        {
            "V_MPEG4/ISO/AVC" => "H264",
            "V_MPEGH/ISO/HEVC" => "HEVC",
            "V_AV1" => "AV1",
            "V_VP9" => "VP9",
            "V_VP8" => "VP8",
            "V_MPEG2" => "MPEG2",
            "V_MPEG1" => "MPEG1",
            "V_THEORA" => "THEORA",
            _ when id.StartsWith("V_MPEG4/ISO/", StringComparison.Ordinal) => "MPEG4",
            _ when id.StartsWith("V_REAL/", StringComparison.Ordinal) => "REAL",
            _ when name.Contains("AVC", StringComparison.Ordinal) || name.Contains("H.264", StringComparison.Ordinal) => "H264",
            _ when name.Contains("HEVC", StringComparison.Ordinal) || name.Contains("H.265", StringComparison.Ordinal) => "HEVC",
            _ when name.Contains("VC-1", StringComparison.Ordinal) || name.Contains("VC1", StringComparison.Ordinal) => "VC1",
            _ when name.Contains("MPEG-4", StringComparison.Ordinal) || name.Contains("XVID", StringComparison.Ordinal) || name.Contains("DIVX", StringComparison.Ordinal) => "MPEG4",
            _ => new string(name.Where(char.IsAsciiLetterOrDigit).ToArray()),
        };
    }
}
