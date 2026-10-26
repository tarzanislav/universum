using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.Tests;

/// <summary>Library names (specification, section 13): folders, file names, stream description, episodes, titles.</summary>
public sealed class LibraryTests
{
    private const long GiB = 1L << 30;

    [Test]
    public void NamesLoseForbiddenCharacters()
    {
        Assert.Equal("Kantara A Legend Chapter 1", LibraryNames.Clean("Kantara A Legend: Chapter 1"));
        Assert.Equal("ACDC Live", LibraryNames.Clean("AC/DC: Live?"));
        Assert.Equal("Mr. Nobody", LibraryNames.Clean("  Mr.  Nobody. "));
        Assert.True(LibraryNames.IsReserved("CON") && LibraryNames.IsReserved("nul.txt") && !LibraryNames.IsReserved("Console"), "reserved names");
    }

    [Test]
    public void LatinScript()
    {
        foreach (var latin in new[] { "Creep", "Pelíšky", "Amélie", "1917", "Łódź", "Ærø" })
        {
            Assert.True(LibraryNames.IsLatinScript(latin), latin);
        }

        foreach (var other in new[] { "Метро", "나인 퍼즐", "九宫格", "ಕಾಂತಾರ", "Ελληνικά", "שלום", "ナイン" })
        {
            Assert.True(!LibraryNames.IsLatinScript(other), other);
        }
    }

    [Test]
    public void TitleFolders()
    {
        Assert.Equal("Creep (2004)", TitleInfo.FromTmdb(MediaKind.Movie, 8555, "Creep", "Creep", 2004)!.FolderName, "Latin original: no ID");
        Assert.Equal("Pelíšky (1999)", TitleInfo.FromTmdb(MediaKind.Movie, 1, "Pelíšky", "Cosy Dens", 1999)!.FolderName, "original title, not English");
        Assert.Equal(
            "Kantara A Legend Chapter 1 (2025) [tmdbid-1083637]",
            TitleInfo.FromTmdb(MediaKind.Movie, 1083637, "ಕಾಂತಾರ", "Kantara A Legend: Chapter 1", 2025)!.FolderName,
            "other script: English title, colon removed, ID");
        Assert.Equal<TitleInfo?>(null, TitleInfo.FromTmdb(MediaKind.Movie, 5, "Метро", null, 2013), "no English title: the user has to type it");
        Assert.Equal("chýba názov", new TitleInfo(MediaKind.Movie, "???", 2000).Problem);
    }

    [Test]
    public void MoviePlacement()
    {
        var layout = new LibraryLayout(Path.GetFullPath("/out"));
        var target = Target(Video("1920x1080"), Audio("cs"), Audio("en"), Subtitles("cs"));
        var placement = layout.Place(target, new TitleInfo(MediaKind.Movie, "Creep", 2004), null, RuleProfile.Default);

        Assert.Equal(Path.Combine(layout.Movies, "Creep (2004)"), placement.Directory);
        Assert.Equal("Creep (2004) - CZ EN FHD H264 BIG.mkv", placement.FileName(5 * GiB));
        Assert.Equal("Creep (2004) - CZ EN FHD H264 BIG2.mkv", placement.FileName(5 * GiB, 2), "version number (K1)");
        Assert.True(placement.IsVersionName("Creep (2004) - CZ EN FHD H264 BluRay BIG3.mkv"), "any size class and number");
        Assert.True(!placement.IsVersionName("Creep (2004) - CZ EN 4K HEVC BIG.mkv") && !placement.IsVersionName("Creep (2004) - CZ EN FHD H264 BIG1.mkv"), "other streams");
        Assert.True(placement.Holds(Path.Combine(placement.Directory, "Creep (2004) - CZ EN FHD H264 BIG2.mkv"), 5 * GiB), "already in place");
        Assert.True(!placement.Holds(Path.Combine(placement.Directory, "Creep (2004) - CZ EN FHD H264 SMALL.mkv"), 5 * GiB), "wrong size class");
    }

    [Test]
    public void EpisodePlacement()
    {
        var layout = new LibraryLayout(Path.GetFullPath("/out"));
        var target = Target(Video("1920x1080"), Audio("ko"), Subtitles("cs"), Subtitles("en"));
        var title = TitleInfo.FromTmdb(MediaKind.Series, 227191, "나인 퍼즐", "Nine Puzzles", 2025)!;
        var placement = layout.Place(target, title, new EpisodeNumber(1, 4), RuleProfile.Default);

        Assert.Equal(Path.Combine(layout.Shows, "Nine Puzzles (2025) [tmdbid-227191]", "Season 01"), placement.Directory);
        Assert.Equal("Nine Puzzles S01E04 - CZ titulky FHD H264 SMALL.mkv", placement.FileName(GiB));
        Assert.Equal("Season 00", LibraryNames.SeasonFolder(0), "specials");
        Assert.Equal("S01E01-E02", new EpisodeNumber(1, 1, 2).Tag);
        Assert.Equal("S02E100", new EpisodeNumber(2, 100).Tag);
    }

    [Test]
    public void SizeClasses()
    {
        var sizes = new SizeClasses();
        Assert.Sequence(
            ["SMALL", "BIG", "BIG", "BluRay", "BluRay BIG"],
            new[] { 2 * GiB, 2 * GiB + 1, 14 * GiB, 14 * GiB + 1, 31 * GiB }.Select(sizes.Label),
            "2 / 14 / 30 GB as in Windows");
    }

    [Test]
    public void StreamLanguages()
    {
        var profile = RuleProfile.Default;
        Assert.Equal("CZ EN", StreamLabel.Languages(Target(Video(), Audio("cs"), Audio("en"), Subtitles("cs")), profile));
        Assert.Equal("CZ SK EN", StreamLabel.Languages(Target(Video(), Audio("sk"), Audio("cs"), Audio("en")), profile), "final order");
        Assert.Equal("SK EN", StreamLabel.Languages(Target(Video(), Audio("sk"), Audio("en"), Subtitles("cs")), profile), "Slovak audio: subtitles not named");
        Assert.Equal("CZ titulky", StreamLabel.Languages(Target(Video(), Audio("ko"), Subtitles("sk"), Subtitles("cs")), profile), "Czech subtitles first");
        Assert.Equal("SK titulky", StreamLabel.Languages(Target(Video(), Audio("en"), Subtitles("sk")), profile));
        Assert.Equal("EN", StreamLabel.Languages(Target(Video(), Audio("en"), Subtitles("cs", "Forced")), profile), "forced subtitles do not count");
        Assert.Equal("CZ", StreamLabel.Languages(Target(Video(), Audio("cs"), Audio("en", "Commentary")), profile), "commentary does not count");
    }

    [Test]
    public void StreamQualityAndCodec()
    {
        Assert.Sequence(
            ["SD", "HD", "HD", "FHD", "FHD", "FHD", "4K"],
            new[] { "720x576", "1280x536", "960x720", "1920x800", "1440x1080", "2560x1440", "3840x1600" }.Select(d => StreamLabel.Quality(Target(Video(d)))),
            "tolerant of cropped formats");
        Assert.Equal("HEVC", StreamLabel.Codec(Target(Video(codecId: "V_MPEGH/ISO/HEVC"))));
        Assert.Equal("AV1", StreamLabel.Codec(Target(Video(codecId: "V_AV1"))));
        Assert.Equal("MPEG2", StreamLabel.Codec(Target(Video(codecId: "V_MPEG2"))));
        Assert.Equal("VC1", StreamLabel.Codec(Target(Video(codecId: "V_MS/VFW/FOURCC", codec: "VC-1"))));
    }

    [Test]
    public void EpisodeNumbersInNames()
    {
        var cases = new (string Name, string? Tag, bool Assumed)[]
        {
            ("Nine.Puzzles.S01E04.1080p.WEB-DL", "S01E04", false),
            ("show s2e10 final", "S02E10", false),
            ("Show S01.E05", "S01E05", false),
            ("Show 1x04", "S01E04", false),
            ("Show.S01E01E02", "S01E01-E02", false),
            ("Show S01E01-E02", "S01E01-E02", false),
            ("Seriál E2", "S01E02", true),
            ("Seriál - Epizóda 7", "S01E07", true),
            ("Seriál Řada 3 Díl 12", "S03E12", false),
            ("Film 1920x1080", null, false),
            ("WALL-E 2008", null, false),
        };
        foreach (var (name, tag, assumed) in cases)
        {
            var match = EpisodeParser.FromText(name);
            Assert.Equal(tag, match?.Number.Tag, name);
            Assert.Equal(assumed, match?.SeasonAssumed ?? false, name + " (season assumed)");
        }

        Assert.Equal("S02E03", EpisodeParser.FromPath(Path.Combine("x", "Sezóna 2", "Seriál E3.mkv"))?.Number.Tag, "season from the folder");
        Assert.Equal(1, EpisodeParser.SeasonOfFolder("Season 01"));
        Assert.Equal(4, EpisodeParser.SeasonOfFolder("S04"));
        Assert.Equal(new EpisodeNumber(1, 4), EpisodeNumber.TryParse("s1e4"));

        // specials are season 0 (Season 00), season packs name their season, a range of seasons names none
        Assert.Equal(0, EpisodeParser.SeasonOfFolder("Specials"));
        Assert.Equal(0, EpisodeParser.SeasonOfFolder("Špeciály"));
        Assert.Equal("S00E01", EpisodeParser.FromPath(Path.Combine("Seriál", "Specials", "Seriál E01.mkv"))?.Number.Tag, "special");
        Assert.Equal("Season 00", LibraryNames.SeasonFolder(0));
        Assert.Equal(2, EpisodeParser.SeasonOfFolder("Nine.Puzzles.S02.1080p.WEB-DL"), "season pack");
        Assert.Equal<int?>(null, EpisodeParser.SeasonOfFolder("Nine.Puzzles.S01-S03.Complete"), "several seasons");
        Assert.Equal<int?>(null, EpisodeParser.SeasonOfFolder("Special Forces (2019)"), "a title, not specials");
        Assert.True(EpisodeParser.IsSeasonOnlyFolder("Season 2") && EpisodeParser.IsSeasonOnlyFolder("Séria 2 (2019)") && EpisodeParser.IsSeasonOnlyFolder("S02") &&
                    EpisodeParser.IsSeasonOnlyFolder("Specials"), "season folders");
        Assert.True(!EpisodeParser.IsSeasonOnlyFolder("Nine.Puzzles.S02.1080p") && !EpisodeParser.IsSeasonOnlyFolder("Nine Puzzles (2025)"), "the series folder");

        // the season in the name wins, but a different season folder makes the number a suggestion
        var conflict = EpisodeParser.FromPath(Path.Combine("Seriál", "Season 2", "Seriál S01E03.mkv"))!;
        Assert.True(conflict is { Number.Tag: "S01E03", FolderSeason: 2, NeedsConfirmation: true }, "name and folder disagree");
        Assert.True(EpisodeParser.FromPath(Path.Combine("Seriál", "Season 1", "Seriál S01E03.mkv")) is { NeedsConfirmation: false }, "they agree");
        Assert.True(EpisodeParser.FromPath(Path.Combine("Seriál", "Seriál Díl 3.mkv")) is { SeasonAssumed: true, NeedsConfirmation: true }, "season assumed");
    }

    [Test]
    public void TitlesFromNames()
    {
        Assert.Equal(new NameGuess("Creep", 2004, null), TitleGuess.FromName("Creep.2004.1080p.BluRay.x264-GRP"));
        Assert.Equal(new NameGuess("Kantara A Legend Chapter 1", 2025, 1083637), TitleGuess.FromName("Kantara A Legend Chapter 1 (2025) [tmdbid-1083637]"));
        Assert.Equal(new NameGuess("Nine Puzzles", null, null), TitleGuess.FromName("Nine.Puzzles.S01E04.1080p.WEB"));
        Assert.Equal(new NameGuess("2001 A Space Odyssey", 1968, null), TitleGuess.FromName("2001 A Space Odyssey (1968)"));
        Assert.Equal(new NameGuess("Creep", 2004, null), TitleGuess.FromName("Creep (2004) - CZ EN FHD H264 BIG"), "library file name");

        // an episode or season ends a series title; a name that starts with one has no title
        Assert.Equal(new NameGuess("Kriminálka Vymyslená", null, null), TitleGuess.FromName("Kriminálka Vymyslená Díl 4"));
        Assert.Equal(new NameGuess("Iný seriál", null, null), TitleGuess.FromName("Iný seriál - Epizóda 1"));
        Assert.Equal(new NameGuess("Seriál", null, null), TitleGuess.FromName("Seriál Řada 2"));
        Assert.Equal<NameGuess?>(null, TitleGuess.FromName("Diel 3"));
        Assert.Equal<NameGuess?>(null, TitleGuess.FromName("E04"));
        // a film keeps its part number and the year in parentheses
        Assert.Equal(new NameGuess("Vymyslená sága - Časť 2", 2011, null), TitleGuess.FromName("Vymyslená sága - Časť 2 (2011)", MediaKind.Movie));
        Assert.Equal(new NameGuess("Vymyslená sága", 2011, null), TitleGuess.FromName("Vymyslená sága - Časť 2 (2011)"), "as an episode name");

        // one file: kind, title from the title folder (above a season folder) when it is in library form
        Assert.Equal(new FileGuess(MediaKind.Series, new NameGuess("Vymyslený seriál", 2024, null), new EpisodeMatch(new EpisodeNumber(2, 3), false)),
            TitleGuess.ForFile(Path.Combine("x", "Vymyslený seriál (2024)", "Season 2", "Vymysleny.Serial.S02E03.mkv")));
        Assert.Equal("Nine Puzzles", TitleGuess.ForFile(Path.Combine("x", "Nine.Puzzles.S02.1080p", "E03.mkv")) is { Kind: MediaKind.Series, Episode.Number.Tag: "S02E03" } pack ? pack.Title?.Name : "not a series", "season pack");
        Assert.Equal(MediaKind.Series, TitleGuess.ForFile(Path.Combine("x", "Seriál (2024)", "Season 2", "prvá.mkv")).Kind, "season folder without a number");
        Assert.Equal(new FileGuess(MediaKind.Movie, new NameGuess("Creep", 2004, null), null), TitleGuess.ForFile(Path.Combine("x", "Filmy", "Creep.2004.1080p.mkv")));
        Assert.Equal("chýba rok", new TitleInfo(MediaKind.Series, "Seriál", null).Problem, "the folder is \"Name (Year)\"");
        Assert.Equal<string?>(null, new TitleInfo(MediaKind.Series, "Seriál", 2024).Problem);

        Assert.Equal(new TitleInfo(MediaKind.Movie, "Charlotte's Web", 2006), TitleGuess.Typed(MediaKind.Movie, "Charlotte's Web (2006)"), "typed: nothing cut away");
        Assert.Equal(new TitleInfo(MediaKind.Series, "Nine Puzzles", 2025, 227191, true), TitleGuess.Typed(MediaKind.Series, " Nine Puzzles (2025) [tmdbid-227191] "));
        Assert.Equal(new TitleInfo(MediaKind.Movie, "Creep", null), TitleGuess.Typed(MediaKind.Movie, "Creep"));

        Assert.Equal(new TmdbReference(MediaKind.Movie, 8555), TitleGuess.ParseTmdb("https://www.themoviedb.org/movie/8555-creep"));
        Assert.Equal(new TmdbReference(MediaKind.Series, 227191), TitleGuess.ParseTmdb("themoviedb.org/tv/227191-nine-puzzles?language=cs-CZ"));
        Assert.Equal(new TmdbReference(null, 1083637), TitleGuess.ParseTmdb(" 1083637 "));
        Assert.Equal<TmdbReference?>(null, TitleGuess.ParseTmdb("https://example.com/movie/1"));
    }

    private static TargetState Target(params MediaTrack[] tracks)
    {
        var file = new MediaFile { Path = "x.mkv", Tracks = tracks.Select((t, i) => t with { Id = i }).ToList() };
        return UniversumRules.Apply(file, decisions: new TrackDecisions());
    }

    private static MediaTrack Video(string dimensions = "1920x1080", string codecId = "V_MPEG4/ISO/AVC", string codec = "AVC/H.264/MPEG-4p10") =>
        new() { Id = 0, Type = TrackType.Video, PixelDimensions = dimensions, CodecId = codecId, Codec = codec, IndexEntries = 10 };

    private static MediaTrack Audio(string language, string? name = null) =>
        new() { Id = 0, Type = TrackType.Audio, Language = LanguageTag.FromCode(language), Name = name };

    private static MediaTrack Subtitles(string language, string? name = null) =>
        new() { Id = 0, Type = TrackType.Subtitles, Language = LanguageTag.FromCode(language), Name = name };
}
