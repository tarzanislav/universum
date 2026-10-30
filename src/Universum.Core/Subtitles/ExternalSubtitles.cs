using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.Text;

namespace Universum.Core.Subtitles;

/// <summary>An external subtitle file that belongs to a video, how sure it is, and what its name says.</summary>
/// <param name="Score">100 = the name of the video; 80 = the title (and the year); 60 = the episode; 40 = the only video of the folder.</param>
/// <param name="Reason">Why it belongs to the video, in Slovak.</param>
/// <param name="Language">The language its name says ("Film.cze.srt"), or null.</param>
public sealed record SubtitleFileMatch(string Path, int Score, string Reason, LanguageTag? Language, bool Forced, bool HearingImpaired)
{
    /// <summary>Picture subtitles (PGS .sup, VobSub .idx): added as they are, their text cannot be read.</summary>
    public bool IsPicture => ExternalSubtitles.PictureExtensions.Contains(System.IO.Path.GetExtension(Path));
}

/// <summary>
/// External subtitles (owner, 30. 10. 2026, bod 5): subtitle files lying with the video they belong to. A file belongs
/// to a video when its name is the video's name (plus a language or "forced": "Film.cze.forced.srt"); otherwise when the
/// words of the title agree – dots, underscores and dashes between them do not matter – with the same year; an episode
/// needs the same season and episode number too (S01E1 = S01E01 = 1x01). A subtitle file named after another episode or
/// film never belongs; in a folder with a single video, a subtitle file without any title belongs to it.
/// </summary>
public static class ExternalSubtitles
{
    /// <summary>Picture subtitles mkvmerge adds as they are.</summary>
    public static readonly IReadOnlySet<string> PictureExtensions = new HashSet<string>([".sup", ".idx"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The extensions of subtitle files looked for next to a video (".xml" is left out – chapters use it too).</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(
        SubtitleReader.TextExtensions.Where(e => e != ".xml").Concat(PictureExtensions), StringComparer.OrdinalIgnoreCase);

    // words a subtitle file name carries after the title that say nothing about the title
    private static readonly HashSet<string> Neutral = new(
        ["titulky", "titulek", "subs", "sub", "subtitles", "subtitle", "full", "default", "plne", "uplne", "srt"], StringComparer.Ordinal);

    // language words of file names (normalised): the codes are found by Languages.Find
    private static readonly Dictionary<string, string> LanguageWords = new(StringComparer.Ordinal)
    {
        ["cz"] = "cs", ["czech"] = "cs", ["cesky"] = "cs", ["cestina"] = "cs", ["cesko"] = "cs",
        ["slovak"] = "sk", ["slovensky"] = "sk", ["slovencina"] = "sk", ["svk"] = "sk",
        ["english"] = "en", ["anglicky"] = "en", ["anglictina"] = "en",
        ["german"] = "de", ["deutsch"] = "de", ["polish"] = "pl", ["polski"] = "pl", ["hungarian"] = "hu", ["magyar"] = "hu",
        ["russian"] = "ru", ["french"] = "fr", ["spanish"] = "es", ["italian"] = "it",
    };

    private static readonly HashSet<string> HearingImpairedWords = new(["sdh", "cc", "hi", "hoh", "neslysici", "nepocujuci"], StringComparer.Ordinal);

    /// <summary>A subtitle file by its extension (".idx" of VobSub; its ".sub" picture data is not a text file).</summary>
    public static bool IsSubtitleFile(string path, IEnumerable<string>? siblings = null)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (!Extensions.Contains(extension))
        {
            return false;
        }

        // "film.sub" with "film.idx" beside it is the VobSub picture data
        return !extension.Equals(".sub", StringComparison.OrdinalIgnoreCase) ||
               !(siblings ?? []).Any(s => string.Equals(s, System.IO.Path.ChangeExtension(path, ".idx"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The subtitle files among <paramref name="candidates"/> that belong to the video, the surest first.</summary>
    /// <param name="videosInFolder">How many videos lie in the folder (a single one takes subtitle files without a title).</param>
    /// <param name="forcedKeywords">Words of forced subtitles (the profile's).</param>
    public static IReadOnlyList<SubtitleFileMatch> For(
        string videoPath, IEnumerable<string> candidates, int videosInFolder = 1, IReadOnlyCollection<string>? forcedKeywords = null)
    {
        forcedKeywords ??= ["forced", "vynucene", "vynutene", "nucene", "nutene"];
        var forced = forcedKeywords.Select(Words.Normalize).ToHashSet(StringComparer.Ordinal);
        var list = candidates.ToList();
        var video = System.IO.Path.GetFileNameWithoutExtension(videoPath);
        var videoEpisode = EpisodeParser.FromText(video);
        var videoIsEpisode = TitleGuess.NamesEpisode(videoEpisode, video) || EpisodeParser.SeasonOfPath(videoPath) is not null;
        var videoTitle = TitleGuess.FromName(video, videoIsEpisode ? MediaKind.Series : MediaKind.Movie);
        // the names of the other videos of the folder: a subtitle file named after one of them is that one's ("Film 2.srt")
        var others = list.Where(p => !string.Equals(p, videoPath, StringComparison.OrdinalIgnoreCase) &&
                                     Batch.BatchAnalyzer.VideoExtensions.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant()))
            .Select(p => Words.Normalize(System.IO.Path.GetFileNameWithoutExtension(p))).ToHashSet(StringComparer.Ordinal);
        var matches = new List<SubtitleFileMatch>();
        foreach (var path in list.Where(p => IsSubtitleFile(p, list)))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var (language, isForced, hearingImpaired, rest) = Tail(name, forced);
            if (others.Contains(Words.Normalize(rest)) || others.Contains(Words.Normalize(name)))
            {
                continue;
            }

            var match = Score(video, videoIsEpisode, videoEpisode, videoTitle, name, rest, videosInFolder);
            if (match is { } found)
            {
                matches.Add(new SubtitleFileMatch(path, found.Score, found.Reason, language, isForced, hearingImpaired));
            }
        }

        return matches.OrderByDescending(m => m.Score).ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (int Score, string Reason)? Score(
        string video, bool videoIsEpisode, EpisodeMatch? videoEpisode, NameGuess? videoTitle, string name, string rest, int videosInFolder)
    {
        // the video's own name, perhaps with a language or "forced" after it
        var videoName = Words.Normalize(video);
        if (videoName.Length > 0 && (Words.Normalize(rest) == videoName || Words.Normalize(name) == videoName))
        {
            return (100, "rovnaký názov ako video");
        }

        var episode = EpisodeParser.FromText(name);
        var isEpisode = episode is not null && TitleGuess.NamesEpisode(episode, name);
        if (videoIsEpisode)
        {
            // an episode: the same season and episode, always
            if (!isEpisode || videoEpisode is null || episode!.Number.Season != videoEpisode.Number.Season || episode.Number.Episode != videoEpisode.Number.Episode)
            {
                return null;
            }
        }
        else if (isEpisode)
        {
            return null;
        }

        // the title words of the name without its language tail – or of the whole name ("Dr.No": "No" is a title word too)
        var kind = videoIsEpisode ? MediaKind.Series : MediaKind.Movie;
        var videoWords = Words.Normalize(videoTitle?.Name);
        var titles = new[] { TitleGuess.FromName(rest, kind), TitleGuess.FromName(name, kind) }.Where(t => Meaningful(t?.Name)).ToList();
        if (titles.Count > 0 && videoWords.Length > 0)
        {
            foreach (var title in titles)
            {
                var sameYear = title!.Year is not { } year || videoTitle?.Year is not { } videoYear || year == videoYear;
                if (Words.Normalize(title.Name) == videoWords && sameYear)
                {
                    return videoIsEpisode ? (90, $"názov seriálu a epizóda {videoEpisode!.Number.Tag}") : (80, "slová názvu" + (title.Year is { } y ? $" a rok {y}" : ""));
                }
            }

            // other words or another year: another title
            return null;
        }

        // no title in the subtitle file's name ("S01E04.cze.srt", "titulky.srt")
        if (videoIsEpisode)
        {
            return (60, $"epizóda {videoEpisode!.Number.Tag}");
        }

        return videosInFolder == 1 ? (40, "jediné video v priečinku") : null;
    }

    // a title made of more than the words every subtitle file may carry ("titulky", "cze", "forced")
    private static bool Meaningful(string? title) =>
        Words.Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(w => !Neutral.Contains(w) && !LanguageWords.ContainsKey(w) && !HearingImpairedWords.Contains(w) && !(w.Length is 2 or 3 && Languages.FindCommon(w) is not null) && w != "forced");

    // the language and flag words at the end of the name ("Film.2020.cze.forced"), and the name without them
    private static (LanguageTag? Language, bool Forced, bool HearingImpaired, string Title) Tail(string name, HashSet<string> forcedWords)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(name, @"[.\s_\-\[\]()]+").Where(p => p.Length > 0).ToList();
        LanguageTag? language = null;
        bool forced = false, hearingImpaired = false;
        var cut = parts.Count;
        for (var i = parts.Count - 1; i > 0; i--)
        {
            var word = Words.Normalize(parts[i]);
            if (forcedWords.Contains(word))
            {
                forced = true;
            }
            else if (HearingImpairedWords.Contains(word))
            {
                hearingImpaired = true;
            }
            else if (LanguageWords.TryGetValue(word, out var code) || (word.Length is 2 or 3 && Languages.FindCommon(word) is { } found && (code = found.Ietf) is not null))
            {
                language ??= LanguageTag.FromCode(code!);
            }
            else if (!Neutral.Contains(word))
            {
                break;
            }

            cut = i;
        }

        // the rest of the name as it was written (only the words before the tail)
        var rest = name;
        if (cut < parts.Count)
        {
            var at = 0;
            for (var i = 0; i < cut; i++)
            {
                at = name.IndexOf(parts[i], at, StringComparison.Ordinal) + parts[i].Length;
            }

            rest = name[..at];
        }

        return (language, forced, hearingImpaired, rest);
    }
}
