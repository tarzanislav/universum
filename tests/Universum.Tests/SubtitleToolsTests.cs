using System.Text;
using Universum.Core.Media;
using Universum.Core.Subtitles;

namespace Universum.Tests;

/// <summary>Subtitle formats, encodings, times and external subtitle files (owner, 30. 10. 2026) – invented texts and names only.</summary>
public sealed class SubtitleToolsTests
{
    private static TimeSpan T(double seconds) => TimeSpan.FromSeconds(seconds);

    [Test]
    public void EveryTextFormatIsRead()
    {
        var srt = SubtitleReader.Read("1\n0:00:01.5 --> 0:00:03,25\nPrvý riadok\n<font color=\"red\">druhý</font>\n\n2\n00:00:04,000 --> 00:00:05,000\n<i>Kurzíva</i>\n");
        Assert.True(srt is { Format: "SRT", Cues.Count: 2 } && srt.Cues[0].Start == T(1.5) && srt.Cues[0].End == T(3.25) && srt.Cues[0].Text == "Prvý riadok\ndruhý" &&
                    srt.Cues[1].Text == "<i>Kurzíva</i>", "SRT: " + string.Join(" | ", srt.Cues));

        var vtt = SubtitleReader.Read("WEBVTT\n\nNOTE poznámka\n\n00:01.000 --> 00:02.500 align:start\n<v Hlas>Ahoj</v> svet\n\nid\n01:00:00.000 --> 01:00:01.000\n<c.yellow>Koniec</c>\n");
        Assert.True(vtt is { Format: "WebVTT", Cues.Count: 2 } && vtt.Cues[0].Start == T(1) && vtt.Cues[0].End == T(2.5) && vtt.Cues[0].Text == "Ahoj svet" &&
                    vtt.Cues[1].Start == T(3600) && vtt.Cues[1].Text == "Koniec", "WebVTT: " + string.Join(" | ", vtt.Cues));

        var ass = SubtitleReader.Read("[Script Info]\nTitle: x\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
                                      "Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\\i1}Kurzíva{\\i0} a text\\Nďalší, s čiarkou\n" +
                                      "Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,{\\p1}m 0 0 l 100 0{\\p0}\n" +
                                      "Comment: 0,0:00:05.00,0:00:06.00,Default,,0,0,0,,nie\n");
        Assert.True(ass is { Format: "ASS/SSA", Cues.Count: 1 } && ass.Cues[0].Text == "<i>Kurzíva</i> a text\nďalší, s čiarkou" && ass.Cues[0].End == T(2.5),
            "ASS: italic, line break, comma in the text; drawings and comments left out – " + string.Join(" | ", ass.Cues));

        var ttml = SubtitleReader.Read("<?xml version=\"1.0\"?><tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:tts=\"http://www.w3.org/ns/ttml#styling\" xmlns:ttp=\"http://www.w3.org/ns/ttml#parameter\" ttp:tickRate=\"10000000\"><body><div>" +
                                       "<p begin=\"10000000t\" end=\"25000000t\">Jeden<br/><span tts:fontStyle=\"italic\">dva</span></p>" +
                                       "<p begin=\"00:00:03.000\" dur=\"1.5s\">Tri</p></div></body></tt>");
        Assert.True(ttml is { Format: "TTML", Cues.Count: 2 } && ttml.Cues[0].Start == T(1) && ttml.Cues[0].End == T(2.5) && ttml.Cues[0].Text == "Jeden\n<i>dva</i>" &&
                    ttml.Cues[1].End == T(4.5), "TTML: ticks, br, italic, dur – " + string.Join(" | ", ttml.Cues));

        var sami = SubtitleReader.Read("<SAMI><BODY><SYNC Start=1000><P Class=CSCC>Ahoj<br>svet<SYNC Start=2000><P Class=CSCC>&nbsp;<SYNC Start=3000><P>Ďalej</BODY></SAMI>");
        Assert.True(sami is { Format: "SAMI", Cues.Count: 2 } && sami.Cues[0].Text == "Ahoj\nsvet" && sami.Cues[0].End == T(2), "SAMI – " + string.Join(" | ", sami.Cues));

        var subViewer = SubtitleReader.Read("[INFORMATION]\n[TITLE]x\n[END INFORMATION]\n\n00:00:01.00,00:00:02.50\nRaz[br]dva\n\n00:00:03.00,00:00:04.00\nTri\n");
        Assert.True(subViewer is { Format: "SubViewer", Cues.Count: 2 } && subViewer.Cues[0].Text == "Raz\ndva", "SubViewer – " + string.Join(" | ", subViewer.Cues));

        var microDvd = SubtitleReader.Read("{1}{1}25.000\n{25}{50}Raz|{y:i}dva\n{75}{100}Tri\n");
        Assert.True(microDvd is { Format: "MicroDVD", Cues.Count: 2, FramesPerSecond: 25 } && microDvd.Cues[0].Start == T(1) && microDvd.Cues[0].End == T(2) &&
                    microDvd.Cues[0].Text == "Raz\n<i>dva</i>", "MicroDVD with its frame rate – " + string.Join(" | ", microDvd.Cues));
        var byVideo = SubtitleReader.Read("{24}{48}Raz\n{72}{96}Dva\n", framesPerSecond: 24);
        Assert.True(byVideo.Cues[0].Start == T(1), "MicroDVD by the video's frame rate");

        var mpl2 = SubtitleReader.Read("[10][25]Raz|/dva\n[30][40]Tri\n");
        Assert.True(mpl2 is { Format: "MPL2", Cues.Count: 2 } && mpl2.Cues[0].Start == T(1) && mpl2.Cues[0].End == T(2.5) && mpl2.Cues[0].Text == "Raz\n<i>dva</i>", "MPL2");

        var tmPlayer = SubtitleReader.Read("00:00:01:Raz|dva\n00:00:03:Tri\n00:01:00:Štyri\n");
        Assert.True(tmPlayer is { Format: "TMPlayer", Cues.Count: 3 } && tmPlayer.Cues[0].End == T(3) && tmPlayer.Cues[1].End == T(8), "TMPlayer: until the next, at most 5 s");

        Assert.Throws<FormatException>(() => SubtitleReader.Read("Toto nie sú titulky, len text.\nĎalší riadok."), "plain text is no subtitles");
    }

    [Test]
    public void OldCzechAndSlovakEncodingsAreRead()
    {
        const string text = "1\r\n00:00:01,000 --> 00:00:02,000\r\nŽluťoučký kůň úpěl ďábelské ódy, ľahko šťastný Ľubo päť.\r\n";
        foreach (var (page, name) in new[] { (1250, "Windows-1250"), (28592, "ISO-8859-2") })
        {
            var (decoded, encoding) = SubtitleEncoding.Decode(CodePage(page).GetBytes(text));
            Assert.True(decoded == text && encoding == name, $"{name}: {encoding} {decoded}");
        }

        Assert.True(SubtitleEncoding.Decode(Encoding.UTF8.GetBytes(text)) is ("1\r\n00:00:01,000 --> 00:00:02,000\r\nŽluťoučký kůň úpěl ďábelské ódy, ľahko šťastný Ľubo päť.\r\n", "UTF-8"), "UTF-8 without a mark");
        Assert.True(SubtitleEncoding.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)]).Text == text, "UTF-16 with a mark");
    }

    private static Encoding CodePage(int page)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(page);
    }

    [Test]
    public void SrtIsWrittenAndReadBack()
    {
        var document = new SubtitleDocument([new SubtitleCue(T(3661.5), T(3663), "<i>Ahoj</i>\nsvet"), new SubtitleCue(T(1), T(2), "Prvý")], "WebVTT");
        var written = SrtWriter.Write(document.With(document.Cues));
        Assert.True(written.StartsWith("1\r\n00:00:01,000 --> 00:00:02,000\r\nPrvý\r\n\r\n2\r\n01:01:01,500 --> 01:01:03,000\r\n<i>Ahoj</i>\r\nsvet", StringComparison.Ordinal), written);
        Assert.Sequence(document.With(document.Cues).Cues, SubtitleReader.Read(written).Cues, "read back the same");
    }

    [Test]
    public void FpsSpeedDelayAndTwoPoints()
    {
        var pal = FrameRate.All.Single(r => r.Label == "25");
        var film = FrameRate.All.Single(r => r.Label == "23,976");
        Assert.Sequence(["23,976", "24", "25", "29,97", "30", "50", "23,978 (špeciálne)"], FrameRate.All.Select(r => r.Label), "the owner's list, in his order");

        // subtitles of a 25 fps video with a 23,976 fps one: the same frame comes 4,27 % later
        var fps = SubtitleTiming.Fps(pal, film);
        Assert.True(fps.MkvMergeValue == "0,1001/960" && fps.Apply(T(3600)) == T(3753.75), "25 → 23,976: " + fps.MkvMergeValue + " " + fps.Apply(T(3600)));
        Assert.True(SubtitleTiming.Fps(film, pal).Apply(fps.Apply(T(1234))) == T(1234), "and back");

        Assert.True(SubtitleTiming.Speed(100)!.Apply(T(100)) == T(50), "+100 %: twice as fast");
        Assert.True(SubtitleTiming.Speed(-50)!.Apply(T(100)) == T(200), "−50 %: half the speed");
        Assert.True(SubtitleTiming.Speed(-100) is null && SubtitleTiming.Speed(0)!.IsNone, "−100 % is not possible, 0 % is no change");
        Assert.True(SubtitleTiming.Speed(4.27m)!.Describe().Contains("4,27 % rýchlejšie", StringComparison.Ordinal), SubtitleTiming.Speed(4.27m)!.Describe());

        var delay = SubtitleTiming.Delay(-1500);
        Assert.True(delay.Apply(T(10)) == T(8.5) && delay.MkvMergeValue == "-1500,1/1" && delay.Describe().Replace('\u00A0', ' ') == "posun −1 500 ms", delay.Describe());
        Assert.True(fps.Then(SubtitleTiming.Delay(1000)).Apply(T(3600)) == T(3754.75), "FPS and then a delay");

        // the first subtitle belongs 2 s later, the last one (at 1:00:00) 1 min later: speed and shift between them
        var sync = SubtitleTiming.TwoPoints(T(60), T(62), T(3600), T(3660))!;
        Assert.True(Math.Abs((sync.Apply(T(60)) - T(62)).TotalMilliseconds) <= 1 && Math.Abs((sync.Apply(T(3600)) - T(3660)).TotalMilliseconds) <= 1, "two points: " + sync.MkvMergeValue);
        Assert.True(SubtitleTiming.TwoPoints(T(10), T(10), T(10), T(20)) is null, "the same time twice: no sync");

        var document = new SubtitleDocument([new SubtitleCue(T(0.2), T(0.8), "preč"), new SubtitleCue(T(1), T(3), "Ostane")], "SRT");
        var moved = SubtitleTiming.Delay(-1000).Apply(document);
        Assert.True(moved.Cues is [{ Start.Ticks: 0, Text: "Ostane" }] && moved.Cues[0].End == T(2), "before the start of the film: left out or from 0 – " + string.Join(" | ", moved.Cues));
    }

    [Test]
    public void HearingImpairedFormattingAndTimes()
    {
        var sdh = new SubtitleDocument(
        [
            new SubtitleCue(T(1), T(2), "[HUDBA HRÁ]"),
            new SubtitleCue(T(3), T(4), "- JANO: Poď sem.\n- (smiech) Idem."),
            new SubtitleCue(T(5), T(6), "<i>MUŽ 2: Kto je tam?</i>"),
            new SubtitleCue(T(7), T(8), "♪ La la la ♪"),
        ], "SRT");
        var plain = SubtitleCleanup.RemoveHearingImpaired(sdh);
        Assert.Sequence(["- Poď sem.\n- Idem.", "<i>Kto je tam?</i>", "La la la"], plain.Cues.Select(c => c.Text), "sounds, speakers and music signs left out");
        Assert.Sequence(["Kurzíva"], SubtitleCleanup.RemoveFormatting(new SubtitleDocument([new SubtitleCue(T(1), T(2), "<i>Kurzíva</i>")], "SRT")).Cues.Select(c => c.Text), "formatting");

        var overlapping = new SubtitleDocument([new SubtitleCue(T(1), T(5), "A"), new SubtitleCue(T(4), T(4.1), "B"), new SubtitleCue(T(10), T(10.2), "C")], "SRT");
        Assert.True(SubtitleCleanup.Overlaps(overlapping) == 1, "one overlap");
        var fixedTimes = SubtitleCleanup.FixTimes(overlapping);
        Assert.True(fixedTimes.Cues[0].End == T(3.999) && fixedTimes.Cues[1].End == T(4.7) && fixedTimes.Cues[2].End == T(10.7) && SubtitleCleanup.Overlaps(fixedTimes) == 0,
            "ends before the next, at least 0,7 s – " + string.Join(" | ", fixedTimes.Cues));
    }

    [Test]
    public void ExternalSubtitlesBelongToTheirVideo()
    {
        // owner, 30. 10. 2026, bod 5: the same name; the words of the title with dots and underscores; the episode
        var film = Path.Combine("D:", "Filmy", "Vymyslený.Film.2020.1080p.WEB-DL.x264-SKUPINA.mkv");
        string[] files =
        [
            Path.Combine("D:", "Filmy", "Vymyslený.Film.2020.1080p.WEB-DL.x264-SKUPINA.cze.srt"),
            Path.Combine("D:", "Filmy", "Vymysleny_Film_2020_SK_forced.srt"),
            Path.Combine("D:", "Filmy", "vymysleny film (2020) eng sdh.vtt"),
            Path.Combine("D:", "Filmy", "Iný.Film.2020.cze.srt"),
            Path.Combine("D:", "Filmy", "Vymyslený.Film.2019.srt"),
            Path.Combine("D:", "Filmy", "Vymyslený.Film.2020.nfo"),
        ];
        var matches = ExternalSubtitles.For(film, files, videosInFolder: 3);
        Assert.Sequence([files[0], files[1], files[2]], matches.Select(m => m.Path).OrderBy(p => Array.IndexOf(files, p)), "only the film's own: " + string.Join(" | ", matches.Select(m => $"{Path.GetFileName(m.Path)} {m.Score}")));
        Assert.True(matches[0] is { Score: 100, Language.Primary: "cs", Forced: false }, "the same name first, Czech: " + matches[0]);
        Assert.True(matches.Single(m => m.Path == files[1]) is { Score: 80, Language.Primary: "sk", Forced: true }, "the title words with underscores, Slovak forced");
        Assert.True(matches.Single(m => m.Path == files[2]) is { Language.Primary: "en", HearingImpaired: true }, "English SDH");

        var episode = Path.Combine("D:", "Serialy", "Vymyslený.Seriál.S01E05.720p.mkv");
        string[] subtitles =
        [
            Path.Combine("D:", "Serialy", "vymysleny.serial.s01e5.sk.srt"),
            Path.Combine("D:", "Serialy", "Vymyslený.Seriál.S01E06.srt"),
            Path.Combine("D:", "Serialy", "S01E05.en.sdh.srt"),
            Path.Combine("D:", "Serialy", "Vymyslený.Seriál.S02E05.cze.srt"),
        ];
        var forEpisode = ExternalSubtitles.For(episode, subtitles, videosInFolder: 10);
        Assert.Sequence([subtitles[0], subtitles[2]], forEpisode.Select(m => m.Path), "the same season and episode only: " + string.Join(" | ", forEpisode.Select(m => m.Path)));
        Assert.True(forEpisode[0] is { Score: 90, Language.Primary: "sk" } && forEpisode[1] is { Score: 60, Language.Primary: "en", HearingImpaired: true }, "S01E5 = S01E05");

        var alone = Path.Combine("D:", "Film", "Vymyslený film (2021).mkv");
        string[] loose = [Path.Combine("D:", "Film", "titulky.srt"), Path.Combine("D:", "Film", "titulky.cze.idx"), Path.Combine("D:", "Film", "titulky.cze.sub"), Path.Combine("D:", "Film", "Iný.Film.srt")];
        Assert.Sequence([loose[0], loose[1]], ExternalSubtitles.For(alone, loose, videosInFolder: 1).Select(m => m.Path).OrderBy(p => Array.IndexOf(loose, p)),
            "a single video takes subtitle files without a title (not another title); VobSub by its .idx");
        Assert.True(ExternalSubtitles.For(alone, loose, videosInFolder: 2).Count == 0, "with two videos not");
        Assert.True(ExternalSubtitles.For(alone, loose, videosInFolder: 1).Single(m => m.Path == loose[1]).IsPicture, "VobSub is pictures");
    }
}
