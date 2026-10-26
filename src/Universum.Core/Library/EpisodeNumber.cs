using System.Globalization;
using System.Text.RegularExpressions;
using Universum.Core.IO;
using Universum.Core.Text;

namespace Universum.Core.Library;

/// <summary>Season and episode of a series file; <see cref="LastEpisode"/> for files with several episodes.</summary>
public sealed record EpisodeNumber(int Season, int Episode, int? LastEpisode = null)
{
    /// <summary>"S01E04", "S01E01-E02", "S01E100".</summary>
    public string Tag =>
        "S" + Season.ToString("00", CultureInfo.InvariantCulture) + "E" + Episode.ToString("00", CultureInfo.InvariantCulture) +
        (LastEpisode is { } last && last > Episode ? "-E" + last.ToString("00", CultureInfo.InvariantCulture) : "");

    public override string ToString() => Tag;

    /// <summary>Parses a tag as typed by the user: "S01E04", "s1e4", "1x04", "S01E01-E02".</summary>
    public static EpisodeNumber? TryParse(string? text) => EpisodeParser.FromText(text) is { SeasonAssumed: false } match ? match.Number : null;
}

/// <summary>How an episode number was written: the kind film/series is guessed from the clear forms only.</summary>
public enum EpisodeForm
{
    /// <summary>S01E04, s1e4, S01.E04.</summary>
    SeasonEpisode,

    /// <summary>1x04.</summary>
    Crossed,

    /// <summary>E04, Ep04, "Epizóda 4", "Díl 4", "Diel 4", "Časť 4" – also used in film titles ("Časť 2").</summary>
    Word,
}

/// <summary>An episode number found in a name.</summary>
/// <param name="SeasonAssumed">No season was found in the name or in the folders; season 1 is assumed.</param>
/// <param name="FolderSeason">
/// Season of the nearest season folder when it differs from the season in the name ("Season 2\Show S01E03.mkv"):
/// one of them is wrong.
/// </param>
public sealed record EpisodeMatch(EpisodeNumber Number, bool SeasonAssumed, int? FolderSeason = null, EpisodeForm Form = EpisodeForm.SeasonEpisode)
{
    /// <summary>The number is a guess the user has to confirm: the season was assumed or the folder disagrees.</summary>
    public bool NeedsConfirmation => SeasonAssumed || FolderSeason is not null;
}

/// <summary>Finds season and episode numbers in file and folder names (specification, section 13.5).</summary>
public static partial class EpisodeParser
{
    /// <summary>
    /// Episode of a file from its name and, for the season, the names of its folders: the nearest season folder below
    /// <paramref name="root"/> (the analysed folder, which is one series) or, without a root, at most two levels up. A
    /// season in the name that differs from the folder is kept but marked for confirmation.
    /// </summary>
    public static EpisodeMatch? FromPath(string path, string? root = null)
    {
        var match = FromText(Path.GetFileNameWithoutExtension(path));
        if (match is null)
        {
            return null;
        }

        var folderSeason = SeasonOfPath(path, root);
        if (match.SeasonAssumed)
        {
            return folderSeason is { } season ? match with { Number = match.Number with { Season = season }, SeasonAssumed = false } : match;
        }

        return folderSeason is { } other && other != match.Number.Season ? match with { FolderSeason = other } : match;
    }

    /// <summary>
    /// Season of the nearest season folder of a file: below and including <paramref name="root"/>, or without a root at
    /// most two levels up; null when there is none.
    /// </summary>
    public static int? SeasonOfPath(string path, string? root = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        for (var level = 0; !string.IsNullOrEmpty(folder); level++)
        {
            if (root is null ? level >= 2 : !Paths.IsUnder(folder, root))
            {
                break;
            }

            if (SeasonOfFolder(Path.GetFileName(folder)) is { } season)
            {
                return season;
            }

            folder = Path.GetDirectoryName(folder);
        }

        return null;
    }

    public static EpisodeMatch? FromText(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (SeasonEpisode().Match(name) is { Success: true } se)
        {
            var season = Number(se.Groups["s"]);
            var episode = Number(se.Groups["e"]);
            int? last = null;
            if (se.Groups["l"].Success && se.Groups["ls"].Captures.All(c => Number(c.Value) == season))
            {
                last = Number(se.Groups["l"]);
            }
            else if (se.Groups["b"].Success && Number(se.Groups["b"]) - episode <= 10)
            {
                last = Number(se.Groups["b"]);
            }

            return new EpisodeMatch(new EpisodeNumber(season, episode, last is { } l && l > episode && l - episode <= 200 ? l : null), false);
        }

        if (Crossed().Match(name) is { Success: true } x)
        {
            var episode = Number(x.Groups["e"]);
            int? last = x.Groups["l"].Success && Number(x.Groups["l"]) is var l && l > episode ? l : null;
            return new EpisodeMatch(new EpisodeNumber(Number(x.Groups["s"]), episode, last), false, Form: EpisodeForm.Crossed);
        }

        var words = Words.Normalize(name);
        var episodeWord = EpisodeWord().Match(words);
        if (!episodeWord.Success)
        {
            return null;
        }

        var seasonWord = SeasonWord().Match(words);
        return seasonWord.Success
            ? new EpisodeMatch(new EpisodeNumber(Number(seasonWord.Groups["s"]), Number(episodeWord.Groups["e"])), false, Form: EpisodeForm.Word)
            : new EpisodeMatch(new EpisodeNumber(1, Number(episodeWord.Groups["e"])), true, Form: EpisodeForm.Word);
    }

    /// <summary>
    /// Season of a folder named "Season 2", "Séria 2", "Sezóna 2", "Řada 2", "S02", "Specials" / "Špeciály" (season 0),
    /// or of a season pack named "Nine.Puzzles.S02.1080p.WEB-DL" (exactly one season in the name).
    /// </summary>
    public static int? SeasonOfFolder(string? folderName)
    {
        var words = Words.Normalize(folderName);
        if (SeasonFolder().Match(words) is { Success: true } exact)
        {
            return Number(exact.Groups["s"]);
        }

        if (SpecialsFolder().IsMatch(words))
        {
            return 0;
        }

        if (SeasonWord().Match(words) is { Success: true } named)
        {
            return Number(named.Groups["s"]);
        }

        var packs = SeasonToken().Matches(words);
        return packs.Count == 1 ? Number(packs[0].Groups["s"]) : null;
    }

    /// <summary>
    /// True for a folder that only names a season ("Season 2", "Séria 2 (2019)", "S02", "Specials"): the series is the
    /// folder above it. A season pack with a title ("Nine.Puzzles.S02.1080p") names the series itself.
    /// </summary>
    public static bool IsSeasonOnlyFolder(string? folderName)
    {
        var words = Words.Normalize(folderName);
        return SeasonFolder().IsMatch(words) || SpecialsFolder().IsMatch(words) || SeasonOnly().IsMatch(words);
    }

    /// <summary>
    /// True for a file that Jellyfin treats as an extra, not as a film or an episode: in a folder such as "Extras",
    /// "Trailers", "Featurettes", "Behind the Scenes", "Deleted Scenes", "Interviews", "Samples", "Shorts", or named
    /// "… - trailer", "…-sample".
    /// </summary>
    public static bool IsExtra(string path, string? root = null)
    {
        if (ExtraSuffix().IsMatch(Words.Normalize(Path.GetFileNameWithoutExtension(path))))
        {
            return true;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        for (var level = 0; !string.IsNullOrEmpty(folder) && (root is null ? level < 2 : Paths.IsUnder(folder, root) && !Paths.Same(folder, root)); level++)
        {
            if (ExtrasFolder().IsMatch(Words.Normalize(Path.GetFileName(folder))))
            {
                return true;
            }

            folder = Path.GetDirectoryName(folder);
        }

        return false;
    }

    private static int Number(Group group) => Number(group.Value);

    private static int Number(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    // S01E04, S01.E04, S01 E04; ranges S01E01E02, S01E20E21E22, S01E01-E02, S01E18-S01E19, S01E01-02 (bare: at most 10)
    // (an E-range ends before anything but a separator, "e" or "s": "S01E01E1080p" is no range)
    [GeneratedRegex(@"(?<![a-z0-9])s(?<s>\d{1,3})[ ._-]?e(?<e>\d{1,4})(?:(?:[ ._-]?-?[ ._-]?(?:s(?<ls>\d{1,3})[ ._-]?)?e(?<l>\d{1,4})(?![0-9a-df-rt-z]))+|-(?<b>\d{1,3})(?![0-9a-z]))?(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisode();

    // 1x04, 1x15-16, 1x15-1x16 (not 1920x1080 nor "4x100": the season has at most two digits, the episode two)
    [GeneratedRegex(@"(?<![a-z0-9])(?<s>\d{1,2})x(?<e>\d{2})(?:-(?:\d{1,2}x)?(?<l>\d{2}))?(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Crossed();

    // on normalised words (lower case, no diacritics): "epizoda 4", "dil 4", "diel 4", "cast 4", "episode 4", "ep 4", "e04"
    // ("e" only directly before the number, so "WALL-E 2008" is not episode 2008)
    [GeneratedRegex(@"\b(?:episode|epizoda|dil|diel|cast|folge|odcinek)\s?(?<e>\d{1,4})\b|\bep\s?(?<e>\d{1,3})\b|\be(?<e>\d{1,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeWord();

    [GeneratedRegex(@"\b(?:season|seria|serie|series|sezona|rada|staffel|sezon)\s?(?<s>\d{1,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonWord();

    [GeneratedRegex(@"^s(?<s>\d{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonFolder();

    // "specials", "special", "špeciály" / "speciály" → "specialy"
    [GeneratedRegex(@"^(?:specials?|specialy)(?: (?:19|20)\d\d)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SpecialsFolder();

    // "season 2", "seria 02 2019": only the season (and maybe its year)
    [GeneratedRegex(@"^(?:season|seria|serie|series|sezona|rada|staffel|sezon)\s?\d{1,3}(?: (?:19|20)\d\d)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonOnly();

    // a season in a release name: "nine puzzles s02 1080p"
    [GeneratedRegex(@"\bs(?<s>\d{1,2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonToken();

    // Jellyfin's extras folders (normalised)
    [GeneratedRegex(@"^(?:extras?|trailers?|featurettes?|behind the scenes|deleted scenes|interviews?|scenes|samples?|shorts|clips|other|bonus|bonusy)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtrasFolder();

    [GeneratedRegex(@"(?:^| )(?:trailer|sample|featurette)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtraSuffix();
}
