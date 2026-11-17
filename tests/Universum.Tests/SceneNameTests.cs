using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.Tests;

/// <summary>Názvoslovie č. 2 (owner, 17. 11. 2026): the names of the scene with dots. Invented titles and names only.</summary>
public sealed class SceneNameTests
{
    private static readonly RuleProfile Scene = RuleProfile.Default with { Library = RuleProfile.Default.Library with { Naming = NamingStyle.Scene } };

    private static TargetState Target(string originalName, params MediaTrack[] tracks)
    {
        var file = new MediaFile { Path = Path.Combine("vstup", originalName), Tracks = tracks.Select((t, i) => t with { Id = i }).ToList() };
        return UniversumRules.Apply(file, decisions: new TrackDecisions());
    }

    private static MediaTrack Video(string dimensions = "1920x1080", string codecId = "V_MPEG4/ISO/AVC") =>
        new() { Id = 0, Type = TrackType.Video, PixelDimensions = dimensions, CodecId = codecId, Codec = codecId == "V_MPEGH/ISO/HEVC" ? "HEVC/H.265/MPEG-H" : "AVC/H.264/MPEG-4p10", IndexEntries = 10 };

    private static MediaTrack Audio(string language, string codecId = "A_AC3", string codec = "AC-3", int channels = 6, string? name = null) =>
        new() { Id = 0, Type = TrackType.Audio, Language = LanguageTag.FromCode(language), CodecId = codecId, Codec = codec, AudioChannels = channels, Name = name };

    private static MediaTrack Subtitles(string language) =>
        new() { Id = 0, Type = TrackType.Subtitles, Language = LanguageTag.FromCode(language), CodecId = "S_TEXT/UTF8", Codec = "SubRip/SRT", IsTextSubtitles = true };

    [Test]
    public void AFilmIsNamedAsOnTheScene()
    {
        var target = Target("Vymysleny.Film.2021.EXTENDED.1080p.BluRay.x264.DTS-HD.MA.5.1-Skupina.mkv",
            Video(), Audio("cs"), Audio("en", "A_DTS", "DTS-HD Master Audio"), Subtitles("cs"));
        var layout = new LibraryLayout("lib");
        var placement = layout.Place(target, new TitleInfo(MediaKind.Movie, "Vymyslený film: Návrat", 2021), null, Scene);
        Assert.Equal("Vymysleny.film.Navrat.2021.EXTENDED.1080p.BluRay.x264.DD5.1.CZ.EN-Skupina.mkv", placement.FileName(5L << 30),
            "title as in č. 1 without diacritics (its letters as they are), the edition, source, codec and group from the original, the first sound and the languages of the result");
        Assert.Equal(Path.Combine("lib", "MOVIES", "Vymyslený film Návrat (2021)"), placement.Directory, "the folder stays as in názvoslovie č. 1");
        Assert.Equal("Vymysleny.film.Navrat.2021.EXTENDED.1080p.BluRay.x264.DD5.1.CZ.EN-Skupina.2.mkv", placement.FileName(1, 2), "another stream: .2");
        Assert.True(placement.IsVersionName(placement.FileName(1)) && placement.IsVersionName(placement.FileName(1, 3)) && placement.Holds(placement.PathFor(1), 99L << 30),
            "recognised in any size – the name has no size class");
        Assert.True(placement.IsLibraryName("Vymyslený film Návrat (2021) - CZ EN FHD H264 BIG.mkv"), "a result of názvoslovie č. 1 is still the library's");
        Assert.True(!placement.IsLibraryName("Iny.Film.2021.1080p.mkv") && !placement.IsLibraryName("Vymysleny.film.Navrat.2021.mkv"), "other names are not");
    }

    [Test]
    public void WhatTheOriginalsNameDoesNotTellIsLeftOut()
    {
        // a name of the user's own: its words are a title, not marks (no WEB from "Web", no group from "- CZ")
        var plain = Target("Charlottin web 2006 - CZ dabing.mkv", Video("1280x720"), Audio("cs", "A_AAC", "AAC", 2));
        Assert.Equal("720p.H264.AAC2.0.CZ", SceneName.Stem(plain, Scene), "resolution, codec, sound and languages only");

        // 4K with HDR and Atmos, a new name of the source; English sound with Czech subtitles
        var uhd = Target("Vymysleny.Serial.S01E04.2160p.UHD.BluRay.REMUX.DV.HDR10.HEVC.TrueHD.7.1.Atmos-Skupina2.mkv",
            Video("3840x1600", "V_MPEGH/ISO/HEVC"), Audio("en", "A_TRUEHD", "TrueHD Atmos", 8), Subtitles("cs"));
        Assert.Equal("2160p.UHD.BluRay.REMUX.DV.HDR10.HEVC.TrueHD.7.1.Atmos.EN.CZ.Sub-Skupina2", SceneName.Stem(uhd, Scene), "every mark in its place");
        var episode = new LibraryLayout("lib").Place(uhd, new TitleInfo(MediaKind.Series, "Vymyslený seriál", 2019), new EpisodeNumber(1, 4), Scene);
        Assert.Equal("Vymysleny.serial.S01E04.2160p.UHD.BluRay.REMUX.DV.HDR10.HEVC.TrueHD.7.1.Atmos.EN.CZ.Sub-Skupina2.mkv", episode.FileName(1),
            "an episode: the name without a year, as in č. 1");

        // a web source with the group, the sound E-AC-3 and the Slovak dub first
        var web = Target("Vymysleny.Film.2023.1080p.WEB-DL.DDP5.1.H.264-Skupina3.mkv", Video(), Audio("sk", "A_EAC3", "E-AC-3", 6), Audio("cs"));
        Assert.Equal("1080p.WEB-DL.H.264.DD5.1.CZ.SK-Skupina3", SceneName.Stem(web, Scene), "WEB-DL is no group, H.264 kept as written; the sound and languages in the order of the result (Czech first)");

        Assert.Equal("Vymyslena.saga.Cast.2.2011", SceneName.Prefix(new TitleInfo(MediaKind.Movie, "Vymyslená sága - Časť 2", 2011), null), "dots, no diacritics");
        Assert.Equal("Lode.and.Strom", SceneName.Dots("Lóde & Strom"), "&");
    }

    [Test]
    public void TheClassicNamesStayAsTheyWere()
    {
        var target = Target("Vymysleny.Film.2021.1080p.BluRay.x264-Skupina.mkv", Video(), Audio("cs"), Audio("en"));
        var placement = new LibraryLayout("lib").Place(target, new TitleInfo(MediaKind.Movie, "Vymyslený film", 2021), null, RuleProfile.Default);
        Assert.Equal("Vymyslený film (2021) - CZ EN FHD H264 BIG.mkv", placement.FileName(5L << 30), "názvoslovie č. 1 unchanged");
        Assert.True(placement.IsLibraryName("Vymysleny.film.2021.1080p.BluRay.x264.DD5.1.CZ.EN-Skupina.mkv"), "a result of č. 2 is the library's too");
        Assert.Equal("Vymyslený film (2021) - CZ EN FHD H264 Directors Cut BIG.mkv", placement.WithAddition("Directors Cut").FileName(5L << 30), "the addition as before");
        var scene = new LibraryLayout("lib").Place(target, new TitleInfo(MediaKind.Movie, "Vymyslený film", 2021), null, Scene).WithAddition("Directors Cut");
        Assert.Equal("Vymysleny.film.2021.Directors.Cut.1080p.BluRay.x264.DD5.1.CZ.EN-Skupina.mkv", scene.FileName(1), "č. 2: the addition right after the year");
    }
}
