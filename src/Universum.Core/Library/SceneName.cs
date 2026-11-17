using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.Core.Library;

/// <summary>How the files of the library are named (owner, 17. 11. 2026: switched in Nastavenia).</summary>
public enum NamingStyle
{
    /// <summary>Názvoslovie č. 1 (Jellyfin): "Názov (rok) - CZ EN FHD H264 BIG.mkv".</summary>
    Classic = 0,

    /// <summary>Názvoslovie č. 2 (the scene, dots): "Nazov.2021.1080p.BluRay.H264.DD5.1.CZ.EN-Skupina.mkv".</summary>
    Scene = 1,
}

/// <summary>
/// Názvoslovie č. 2 (owner, 17. 11. 2026 – after the usual names of the scene): Nazov.Rok.SxxEyy.Rozlisenie.Zdroj.Kodek.Zvuk-Skupina.
/// Only the name of the file; the folders stay "Názov (rok)" (Jellyfin finds the title by them). The name of the title as
/// in názvoslovie č. 1 without diacritics; the resolution, the codec of the picture, the first sound and the languages
/// from the result itself; the source (BluRay, WEB-DL, REMUX …), HDR, the edition (EXTENDED …), the marks of fixes
/// (PROPER, REPACK …) and the group taken from the original's name when they are in it, else left out.
/// </summary>
public static partial class SceneName
{
    /// <summary>"Vymysleny.Film.2021" (a film), "Vymysleny.Serial.S01E04" (an episode – the name without a year, as in č. 1).</summary>
    public static string Prefix(TitleInfo title, EpisodeNumber? episode) =>
        episode is null
            ? Dots(title.CleanName) + (title.Year is { } year ? "." + year.ToString(CultureInfo.InvariantCulture) : "")
            : Dots(title.CleanName) + "." + episode.Tag;

    /// <summary>
    /// Everything after the name: "EXTENDED.1080p.BluRay.HDR.H264.DD5.1.CZ.EN-Skupina". The parts that cannot be told are
    /// left out.
    /// </summary>
    public static string Stem(TargetState target, RuleProfile profile)
    {
        var name = Path.GetFileNameWithoutExtension(target.Source.Path);
        var group = Group(name);
        var original = Marked(Tokens(group is null ? name : name[..^(group.Length + 1)]));
        var video = target.Tracks.FirstOrDefault(t => t.Type == TrackType.Video)?.Source;
        var encoded = video?.VideoEncode is not null;
        var parts = new List<string>();
        parts.AddRange(Edition(original));
        parts.Add(Resolution(video));
        parts.Add(Source(original, encoded));
        if (!encoded)
        {
            parts.AddRange(Dynamic(original));
        }

        parts.Add(VideoCodec(target, original, encoded));
        parts.Add(Sound(target, original));
        parts.Add(Languages(target, profile));
        var stem = string.Join('.', parts.Where(p => p.Length > 0));
        return group is not null ? stem + "-" + group : stem;
    }

    /// <summary>A text written with dots: no diacritics, letters and digits only ("Vymyslená sága - Časť 2" → "Vymyslena.Saga.Cast.2").</summary>
    public static string Dots(string text)
    {
        var plain = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’' or '`')
            {
                continue;
            }

            plain.Append(c switch
            {
                'ß' => "ss", 'Æ' => "AE", 'æ' => "ae", 'Œ' => "OE", 'œ' => "oe", 'Ø' => "O", 'ø' => "o", 'Ł' => "L", 'ł' => "l",
                'Đ' => "D", 'đ' => "d", 'Þ' => "Th", 'þ' => "th", 'ı' => "i", '&' => " and ",
                _ => char.IsAsciiLetterOrDigit(c) ? c.ToString() : " ",
            });
        }

        return string.Join('.', plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>2160p, 1080p, 720p, 576p or 480p by the picture (a cropped wide picture by its width: 1920×800 is 1080p).</summary>
    public static string Resolution(MediaTrack? video)
    {
        var parts = video?.PixelDimensions?.Split('x', 'X');
        if (parts is not [var w, var h] ||
            !int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            return "";
        }

        return width >= 3200 || height >= 1800 ? "2160p"
            : width >= 1600 || height >= 1000 ? "1080p"
            : width >= 1100 || height >= 650 ? "720p"
            : height >= 540 ? "576p"
            : "480p";
    }

    /// <summary>
    /// The first sound of the result: "DD5.1", "DDP5.1", "TrueHD.7.1.Atmos", "DTS-HD.MA.5.1", "AAC2.0"; Atmos also when the
    /// original's name says so.
    /// </summary>
    public static string Sound(TargetState target, IReadOnlyList<string>? original = null)
    {
        var audio = target.Tracks.FirstOrDefault(t => t.Type == TrackType.Audio)?.Source;
        if (audio is null)
        {
            return "";
        }

        var id = audio.CodecId ?? "";
        var name = audio.Codec;
        var codec = id switch
        {
            "A_AC3" => "DD",
            "A_EAC3" => "DDP",
            "A_TRUEHD" or "A_MLP" => "TrueHD",
            "A_DTS" when name.Contains("Master Audio", StringComparison.OrdinalIgnoreCase) || name.Contains("DTS-HD MA", StringComparison.OrdinalIgnoreCase) => "DTS-HD.MA",
            "A_DTS" when name.Contains("High Resolution", StringComparison.OrdinalIgnoreCase) || name.Contains("DTS-HD HR", StringComparison.OrdinalIgnoreCase) => "DTS-HD.HRA",
            "A_DTS" when name.Contains(":X", StringComparison.OrdinalIgnoreCase) => "DTS-X",
            "A_DTS" => "DTS",
            "A_OPUS" => "Opus",
            "A_FLAC" => "FLAC",
            "A_VORBIS" => "Vorbis",
            "A_MPEG/L3" => "MP3",
            "A_MPEG/L2" => "MP2",
            _ when id.StartsWith("A_AAC", StringComparison.Ordinal) => "AAC",
            _ when id.StartsWith("A_PCM", StringComparison.Ordinal) => "LPCM",
            _ when name.Contains("E-AC-3", StringComparison.OrdinalIgnoreCase) => "DDP",
            _ when name.Contains("AC-3", StringComparison.OrdinalIgnoreCase) => "DD",
            _ when name.Contains("TrueHD", StringComparison.OrdinalIgnoreCase) => "TrueHD",
            _ when name.Contains("DTS", StringComparison.OrdinalIgnoreCase) => "DTS",
            _ when name.Contains("AAC", StringComparison.OrdinalIgnoreCase) => "AAC",
            _ => new string(name.Where(char.IsAsciiLetterOrDigit).ToArray()),
        };
        var channels = audio.AudioChannels switch
        {
            1 => "1.0",
            2 => "2.0",
            3 => "2.1",
            6 => "5.1",
            7 => "6.1",
            8 => "7.1",
            { } n and > 0 => n.ToString(CultureInfo.InvariantCulture) + ".0",
            _ => "",
        };

        // a number right after a short name ("DD5.1", "AAC2.0"), after a dotted one with a dot ("TrueHD.7.1", "DTS-HD.MA.5.1")
        var sound = channels.Length == 0 ? codec : codec is "DD" or "DDP" or "AAC" or "DTS" or "Opus" or "FLAC" or "LPCM" or "MP3" ? codec + channels : codec + "." + channels;
        var atmos = name.Contains("Atmos", StringComparison.OrdinalIgnoreCase) ||
                    (codec is "TrueHD" or "DDP" && (original ?? []).Any(t => t.Equals("Atmos", StringComparison.OrdinalIgnoreCase)));
        return atmos && codec.Length > 0 ? sound + ".Atmos" : sound;
    }

    /// <summary>
    /// The languages of the sound in their order without commentaries ("CZ.SK.EN"); without sound in a preferred language
    /// the full subtitles of the first preferred language that has them are added ("EN.CZ.Sub").
    /// </summary>
    public static string Languages(TargetState target, RuleProfile profile)
    {
        var audio = target.Tracks
            .Where(t => t.Type == TrackType.Audio && t.Role != TrackRole.Commentary && !t.Language.IsUndetermined)
            .Select(t => t.Language.Primary)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var codes = audio.Select(StreamLabel.Code).ToList();
        if (!audio.Any(profile.IsPreferred))
        {
            foreach (var preferred in profile.PreferredLanguages)
            {
                if (target.Tracks.Any(t => t.Type == TrackType.Subtitles && t.Role is TrackRole.Main or TrackRole.HearingImpaired &&
                                           string.Equals(t.Language.Primary, preferred, StringComparison.OrdinalIgnoreCase)))
                {
                    codes.Add(StreamLabel.Code(preferred) + ".Sub");
                    break;
                }
            }
        }

        return string.Join('.', codes);
    }

    // the codec of the picture: as the original's name writes it when that is the codec of the result (x264, x265, H.264 …),
    // else as in názvoslovie č. 1 (H264, HEVC, AV1 …)
    private static string VideoCodec(TargetState target, IReadOnlyList<string> original, bool encoded)
    {
        var codec = StreamLabel.Codec(target);
        if (!encoded)
        {
            for (var i = 0; i < original.Count; i++)
            {
                var token = original[i];
                var two = i + 1 < original.Count ? token + "." + original[i + 1] : "";
                var written = (codec, token.ToUpperInvariant(), two.ToUpperInvariant()) switch
                {
                    ("H264", "X264", _) => "x264",
                    ("H264", "AVC" or "H264", _) => token.ToUpperInvariant(),
                    ("H264", _, "H.264") => "H.264",
                    ("HEVC", "X265", _) => "x265",
                    ("HEVC", "HEVC" or "H265", _) => token.ToUpperInvariant(),
                    ("HEVC", _, "H.265") => "H.265",
                    _ => null,
                };
                if (written is not null)
                {
                    return written;
                }
            }
        }

        return codec;
    }

    // UHD, BluRay/BDRip/BRRip/WEB-DL/WEBRip/HDTV/DVDRip …, REMUX – from the original's name (REMUX not after a new picture)
    private static string Source(IReadOnlyList<string> original, bool encoded)
    {
        string? Find(params string[] names)
        {
            for (var i = 0; i < original.Count; i++)
            {
                foreach (var name in names)
                {
                    var parts = name.Split('.');
                    if (i + parts.Length <= original.Count && parts.Select((p, k) => original[i + k].Equals(p, StringComparison.OrdinalIgnoreCase)).All(x => x))
                    {
                        return name;
                    }
                }
            }

            return null;
        }

        var uhd = Find("UHD");
        var source = Find("BluRay", "Blu-Ray", "BDRip", "BRRip", "WEB-DL", "WEBDL", "WEBRip", "WEB", "HDTV", "DVDRip", "DVD", "HDRip", "HDCAM", "CAM", "TS", "TELESYNC");
        source = source switch
        {
            null => null,
            "Blu-Ray" => "BluRay",
            "WEBDL" => "WEB-DL",
            "TELESYNC" => "TS",
            _ => source,
        };
        var remux = !encoded ? Find("REMUX") : null;
        return string.Join('.', new[] { uhd is not null && source is not null ? "UHD" : null, source, remux }.OfType<string>());
    }

    // HDR10+, HDR10, HDR, DV (Dolby Vision), 10bit – from the original's name
    private static IEnumerable<string> Dynamic(IReadOnlyList<string> original)
    {
        var dv = original.Any(t => t.Equals("DV", StringComparison.OrdinalIgnoreCase) || t.Equals("DoVi", StringComparison.OrdinalIgnoreCase) ||
                                    t.Equals("DolbyVision", StringComparison.OrdinalIgnoreCase));
        var hdr = original.FirstOrDefault(t => t.Equals("HDR10+", StringComparison.OrdinalIgnoreCase) || t.Equals("HDR10Plus", StringComparison.OrdinalIgnoreCase))
            is not null ? "HDR10+"
            : original.Any(t => t.Equals("HDR10", StringComparison.OrdinalIgnoreCase)) ? "HDR10"
            : original.Any(t => t.Equals("HDR", StringComparison.OrdinalIgnoreCase)) ? "HDR"
            : null;
        if (dv)
        {
            yield return "DV";
        }

        if (hdr is not null)
        {
            yield return hdr;
        }

        if (original.Any(t => t.Equals("10bit", StringComparison.OrdinalIgnoreCase) || t.Equals("10-bit", StringComparison.OrdinalIgnoreCase)))
        {
            yield return "10bit";
        }
    }

    // EXTENDED, DIRECTORS.CUT, UNRATED, REMASTERED …; PROPER, REPACK, INTERNAL – from the original's name, in this order
    private static IEnumerable<string> Edition(IReadOnlyList<string> original)
    {
        string[] single = ["EXTENDED", "UNRATED", "UNCUT", "REMASTERED", "THEATRICAL", "IMAX", "CRITERION"];
        foreach (var name in single)
        {
            if (original.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                yield return name;
            }
        }

        for (var i = 0; i + 1 < original.Count; i++)
        {
            if (original[i].Equals("DIRECTORS", StringComparison.OrdinalIgnoreCase) && original[i + 1].Equals("CUT", StringComparison.OrdinalIgnoreCase))
            {
                yield return "DIRECTORS.CUT";
                break;
            }
        }

        foreach (var name in new[] { "PROPER", "REPACK", "INTERNAL" })
        {
            if (original.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                yield return name;
            }
        }
    }

    // the group after the last "-" of a name of the scene ("…x264-Skupina"): letters and digits only; the name must have the
    // dots of the scene and a sure mark of the scene before the group (not "Film - CZ" of názvoslovie č. 1, not "WEB-DL")
    private static string? Group(string name)
    {
        if (!name.Contains('.', StringComparison.Ordinal) || name.Contains(" - ", StringComparison.Ordinal) || GroupAtEnd().Match(name) is not { Success: true } match)
        {
            return null;
        }

        var group = match.Groups[1].Value;
        return Tokens(name[..match.Index]).Any(Sure) && group.ToUpperInvariant() is not ("DL" or "HD" or "RAY" or "RIP") && !Sure(group) ? group : null;
    }

    // the marks of the scene that a title hardly has (not WEB, DV, TS, HDR – "Charlottes.Web.2006" is a title)
    private static readonly HashSet<string> SureMarks = new(StringComparer.OrdinalIgnoreCase)
    {
        "2160p", "1080p", "1080i", "720p", "576p", "480p", "4K", "x264", "x265", "H264", "H265", "HEVC", "AVC", "AV1", "BluRay", "BDRip", "BRRip",
        "WEB-DL", "WEBDL", "WEBRip", "HDTV", "DVDRip", "REMUX", "10bit", "HDR10", "TrueHD", "DDP5", "DD5",
    };

    private static bool Sure(string token) => SureMarks.Contains(token) || EpisodeMark().IsMatch(token);

    // the part of a name after the title: from the year before the first sure mark ("Film.2021.EXTENDED.1080p…" → EXTENDED,
    // 1080p …); nothing when the name has no sure mark (a name of the user's own: its words are a title, not marks)
    private static List<string> Marked(List<string> tokens)
    {
        var first = tokens.FindIndex(Sure);
        if (first < 0)
        {
            return [];
        }

        var year = tokens.FindLastIndex(first, t => YearMark().IsMatch(t));
        return tokens[(year >= 0 ? year + 1 : first)..];
    }

    // the parts of a name split at dots, spaces and underscores ("WEB-DL", "DTS-HD" and "H.264" stay readable by pairs)
    private static List<string> Tokens(string name) =>
        [.. Separators().Split(name).Where(t => t.Length > 0)];

    [GeneratedRegex(@"[.\s_\[\]()]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    [GeneratedRegex(@"-([A-Za-z0-9]{2,20})$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupAtEnd();

    [GeneratedRegex(@"^S\d{1,2}(E\d{1,3})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeMark();

    [GeneratedRegex(@"^(19|20)\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex YearMark();
}
