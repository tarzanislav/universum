using Universum.Core.Execution;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;
using Universum.Core.Video;

namespace Universum.Tests;

/// <summary>Úprava videostopy (owner, 30. 10. 2026): cut, proportions, turned and written anew – an invented film.</summary>
public sealed class VideoTests
{
    private static TimeSpan T(double seconds) => TimeSpan.FromSeconds(seconds);

    [Test]
    public void PartsMovedToKeyFrames()
    {
        var keyframes = new[] { 0.0, 2, 4, 6, 8, 10, 12, 14, 16, 18 }.Select(T).ToList();
        var snapped = Cut.Snap([new CutPart(T(3), T(6.5)), new CutPart(T(12), T(19)), new CutPart(T(5), T(5))], keyframes, T(20));
        Assert.True(snapped.SequenceEqual([new CutPart(T(4), T(8)), new CutPart(T(12), T(20))]), "to the next key frame, the last part to the end – " + string.Join(" ", snapped));
        Assert.True(Cut.Normalized([new CutPart(T(5), T(9)), new CutPart(T(1), T(6)), new CutPart(T(10), T(11))]).SequenceEqual([new CutPart(T(1), T(9)), new CutPart(T(10), T(11))]),
            "overlapping parts joined, in order");
        Assert.Equal("parts:00:00:04.000-00:00:08.000,+00:20:00.500-01:00:00.000", Cut.MkvMergeValue([new CutPart(T(4), T(8)), new CutPart(T(1200.5), T(3600))]), "mkvmerge's value");
        var turned = new VideoRecipe(Rotation: 90, Height: 720);
        Assert.True(turned.NeedsEncoding && string.Join(",", turned.Filters()) == "transpose=clock,scale=-2:720:flags=lanczos,format=nv12" &&
                    new VideoRecipe(Rotation: 45).Problem is { Length: > 0 } && !VideoRecipe.None.NeedsEncoding, "the filters of a picture turned and smaller");
        Assert.True(turned.Dimensions("1920x1080") == "406x720" && new VideoRecipe(Rotation: 180).Dimensions("720x576") == "720x576" && turned.Dimensions(null) is null,
            "the size written: turned, then of the height (an even width, as FFmpeg)");
    }

    [Test]
    public async Task CutWithoutWritingThePicture()
    {
        if (Ffmpeg() is not { } ffmpeg)
        {
            Assert.Skip("FFmpeg not on PATH");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("universum-video-").FullName;
        try
        {
            var (installation, inspector, film) = await FilmAsync(ffmpeg, folder, subtitles: true);
            var ffprobe = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe")).First(File.Exists);
            var frames = await Cut.KeyframesAsync(new ProcessRunner(), ffprobe, film, matroska: true);
            var keyframes = frames.Times;
            Assert.True(keyframes.Count == 10 && Math.Abs(keyframes[1].TotalSeconds - 2) < 0.1 && frames.End is { } end && Math.Abs(end.TotalSeconds - 20) < 0.2,
                $"a key frame every two seconds, twenty seconds – {string.Join(" ", keyframes.Select(k => k.TotalSeconds))} | {frames.End}");
            var file = await inspector.InspectAsync(film);
            var dataEnd = await Universum.Core.Media.MediaEnd.LastAsync(new ProcessRunner(), ffprobe, film);
            Assert.True(dataEnd is { } last && Math.Abs(last.TotalSeconds - 20) < 0.2, $"the real end of the data – {dataEnd}");
            var parts = Cut.Snap([new CutPart(T(3), T(6.5)), new CutPart(T(12), T(14))], keyframes, frames.End!.Value);
            var rules = UniversumRules.Apply(file, RuleProfile.Default, new TrackDecisions { Languages = new Dictionary<int, LanguageTag> { [1] = LanguageTag.FromCode("cs") } });
            var target = rules with
            {
                Cut = parts,
                Tracks = rules.Tracks.Select(t => t.Type == TrackType.Video ? t with { AspectRatio = "4/3" } : t).ToList(),
            };
            var result = await Processor(installation, folder).ProcessAsync(EditPlan.For(target), Place(folder, target), new OutputOptions(OriginalHandling.Keep));
            Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
            var written = await inspector.InspectAsync(result.OutputPath!);
            Assert.True(written.Duration is { } length && Math.Abs(length.TotalSeconds - 6) < 0.3 && written.TracksOf(TrackType.Video).Single().DisplayAspect is { } aspect &&
                        Math.Abs(aspect - (4 / 3.0)) < 0.02, $"4 + 2 s kept (key frames), shown 4:3 – {written.Duration} {written.TracksOf(TrackType.Video).Single().DisplayDimensions}");

            // the subtitles within the parts kept, moved with them (one shown across the cut: in both parts, shortened)
            var subtitles = await SubtitleText.ExtractDocumentAsync(installation, new ProcessRunner(), written, written.TracksOf(TrackType.Subtitles).Single());
            var cues = subtitles!.Cues.Select(c => (c.Start.TotalSeconds, c.End.TotalSeconds, c.Text)).ToList();
            Assert.True(cues.Count == 3 && Near(cues[0], 0, 1, "Prvý") && Near(cues[1], 3, 4, "Druhý") && Near(cues[2], 4, 5, "Druhý"),
                "subtitles cut with the film – " + string.Join(" | ", cues));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task PictureWrittenAnewAndCutExactly()
    {
        if (Ffmpeg() is not { } ffmpeg)
        {
            Assert.Skip("FFmpeg not on PATH");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("universum-video-").FullName;
        try
        {
            var (installation, inspector, film) = await FilmAsync(ffmpeg, folder);
            var file = await inspector.InspectAsync(film);
            var video = file.TracksOf(TrackType.Video).Single();
            // turned by 90° and smaller; key frames where the parts begin and end, so the cut is exact
            var parts = new List<CutPart> { new(T(3), T(6.5)), new(T(12.5), T(14)) };
            var anew = new VideoEncoding(film, video, new VideoRecipe(Rotation: 90, Codec: VideoCodec.H264, Height: 160), parts.SelectMany(p => new[] { p.Start, p.End }).ToList()).ToTrack(1000);
            Assert.Equal("120x160", anew.PixelDimensions, "the size the picture will have");
            var augmented = file with { Tracks = [.. file.Tracks, anew] };
            var rules = UniversumRules.Apply(augmented, RuleProfile.Default, new TrackDecisions
            {
                Languages = new Dictionary<int, LanguageTag> { [1] = LanguageTag.FromCode("cs") },
                Removed = new HashSet<int> { video.Id },
            });
            Assert.True(rules.Tracks.Count(t => t.Type == TrackType.Video) == 1 && !rules.IsBlocked && rules.Removed.Any(t => t.Id == video.Id), "one picture: the one written anew");
            var target = rules with { Cut = parts };
            var result = await Processor(installation, folder).ProcessAsync(EditPlan.For(target), Place(folder, target), new OutputOptions(OriginalHandling.Keep));
            Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
            var written = await inspector.InspectAsync(result.OutputPath!);
            var picture = written.TracksOf(TrackType.Video).Single();
            Assert.True(picture is { CodecId: "V_MPEG4/ISO/AVC", PixelDimensions: "120x160" } && written.Duration is { } length && Math.Abs(length.TotalSeconds - 5) < 0.2,
                $"H.264 turned (portrait), 3,5 + 1,5 s exactly – {picture.PixelDimensions} {written.Duration}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static bool Near((double Start, double End, string Text) cue, double start, double end, string text) =>
        Math.Abs(cue.Start - start) < 0.06 && Math.Abs(cue.End - end) < 0.06 && cue.Text.Contains(text, StringComparison.Ordinal);

    private static string? Ffmpeg() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);

    // twenty seconds, 4:3 320 × 240, a key frame every two seconds, a tone (and invented Czech subtitles: one across the
    // start of the first part kept, one from it into the second, two in what is cut out)
    private static async Task<(MkvToolNixInstallation, MkvMergeInspector, string)> FilmAsync(string ffmpeg, string folder, bool subtitles = false)
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var mp4 = Path.Combine(folder, "film.mp4");
        var made = await new ProcessRunner().RunAsync(new ProcessRequest(ffmpeg,
        [
            "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=20", "-f", "lavfi", "-i", "sine=f=440:d=20",
            "-c:v", "libx264", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-c:a", "aac", mp4,
        ]));
        Assert.True(made.ExitCode == 0, made.StandardError);
        var film = Path.Combine(folder, "Vymyslený strihaný film.mkv");
        var srt = Path.Combine(folder, "titulky.srt");
        await File.WriteAllTextAsync(srt, """
            1
            00:00:03,000 --> 00:00:05,000
            Prvý vymyslený titulok

            2
            00:00:07,000 --> 00:00:13,000
            Druhý vymyslený titulok

            3
            00:00:09,000 --> 00:00:10,000
            Vystrihnutý

            4
            00:00:15,000 --> 00:00:16,000
            Tiež vystrihnutý

            """);
        await new ProcessRunner().RunAsync(new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvMerge),
            subtitles ? ["-q", "-o", film, mp4, "--language", "0:cs", srt] : ["-q", "-o", film, mp4]));
        return (installation, new MkvMergeInspector(installation, new ProcessRunner()), film);
    }

    private static FileProcessor Processor(MkvToolNixInstallation installation, string folder) =>
        new(installation, new ProcessRunner(), new NoRecycleBin(), Path.Combine(folder, "journal"))
        {
            FindFfmpeg = _ => Task.FromResult(Ffmpeg()!),
            VideoEncoder = "libx264",
        };

    private static Placement Place(string folder, TargetState target) =>
        new LibraryLayout(Path.Combine(folder, "OUTPUT")).Place(target, new TitleInfo(MediaKind.Movie, "Strih", 2026), null, RuleProfile.Default);

    private sealed class NoRecycleBin : IRecycleBin
    {
        public string? CannotRecycle(string path) => "no recycle bin in tests";

        public void MoveToRecycleBin(string path) => throw new IOException("no recycle bin in tests");
    }
}
