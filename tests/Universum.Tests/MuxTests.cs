using Universum.Core.Jobs;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Muxing;
using Universum.Core.Processes;
using Universum.Core.Rules;

namespace Universum.Tests;

/// <summary>Multiplexer: arguments, checks, the UNIVERSUM rules over several inputs, real mkvmerge.</summary>
public sealed class MuxTests
{
    [Test]
    public void ArgumentsSelectTracksPerFileAndOrderThem()
    {
        var film = Film("film.mkv");
        var subs = new MediaFile { Path = "sub.srt", ContainerType = "SRT subtitles", Tracks = [new MediaTrack { Id = 0, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8" }] };
        var spec = new MuxSpec(
            [new MuxSource(film, KeepChapters: false, KeepTags: false), new MuxSource(subs)],
            [
                new MuxTrack(0, 0, null, LanguageTag.Undetermined, new TrackFlags()),
                new MuxTrack(0, 2, "", LanguageTag.FromCode("cs"), new TrackFlags()),
                new MuxTrack(1, 0, "Slovenské", LanguageTag.FromCode("sk"), new TrackFlags(Default: false)),
            ],
            "out.mkv") { Title = "Film" };

        var arguments = MuxArguments.Build(spec, "tmp.mkv").ToList();
        var text = string.Join(" ", arguments);
        Assert.True(text.StartsWith("--gui-mode --output tmp.mkv --title Film", StringComparison.Ordinal), text);
        Assert.True(text.Contains("--audio-tracks 2 --no-subtitles --no-chapters --no-global-tags --no-track-tags", StringComparison.Ordinal), "first file: one audio, no subtitles, no chapters, no tags – " + text);
        Assert.True(text.Contains("--track-name 0:Slovenské --language 0:sk --default-track-flag 0:0", StringComparison.Ordinal), "second file's track");
        Assert.True(text.EndsWith("--track-order 0:0,0:2,1:0", StringComparison.Ordinal), "order across files");
        Assert.True(arguments.IndexOf("film.mkv") < arguments.IndexOf("sub.srt"), "files in order");
    }

    [Test]
    public void SpecProblemsAreReportedBeforeAnythingRuns()
    {
        var film = Film(Path.Combine(Path.GetTempPath(), "film.mkv"));
        var track = new MuxTrack(0, 0, null, LanguageTag.Undetermined, new TrackFlags());
        Assert.True(new MuxSpec([], [], "").Validate().Count >= 2, "no input, no track, no output");
        Assert.True(new MuxSpec([new MuxSource(film)], [track], film.Path).Validate().Any(p => p.Contains("vstupných", StringComparison.Ordinal)), "output = input");
        Assert.True(new MuxSpec([new MuxSource(film)], [track], Path.Combine(Path.GetTempPath(), "out.avi")).Validate().Any(p => p.Contains("príponu", StringComparison.Ordinal)), "extension");
        Assert.True(new MuxSpec([new MuxSource(film)], [track, track], Path.Combine(Path.GetTempPath(), "x-universum-test.mkv")).Validate().Any(p => p.Contains("dvakrát", StringComparison.Ordinal)), "duplicate");
        Assert.True(new MuxSpec([new MuxSource(film)], [track with { TrackId = 9 }], Path.Combine(Path.GetTempPath(), "x-universum-test.mkv")).Validate().Any(p => p.Contains("nie je", StringComparison.Ordinal)), "unknown track");
    }

    [Test]
    public void RulesWorkAcrossInputs()
    {
        var film = Film("film.mkv");
        var subs = new MediaFile { Path = "sub.srt", Tracks = [new MediaTrack { Id = 0, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8" }] };
        var sources = new[] { new MuxSource(film), new MuxSource(subs) };
        var tracks = new List<MuxTrack>
        {
            new(0, 0, "x264", LanguageTag.Undetermined, new TrackFlags()),
            new(0, 1, "English", LanguageTag.FromCode("en"), new TrackFlags()),
            new(0, 2, "CZ", LanguageTag.FromCode("cs"), new TrackFlags()),
            new(1, 0, null, LanguageTag.FromCode("sk"), new TrackFlags()),
        };
        Assert.True(MuxRules.Check(sources, tracks).Any(f => f.Kind == CheckKind.SeveralDefaults), "two default audio tracks");

        var ruled = MuxRules.Apply(sources, tracks);
        Assert.Sequence([(0, 0), (0, 2), (0, 1), (1, 0)], ruled.Select(t => (t.Source, t.TrackId)), "Czech audio first");
        Assert.True(ruled[1].Flags.Default && !ruled[2].Flags.Default && ruled[1].Name is null, "rules' flags and names");
        Assert.Equal(0, MuxRules.Check(sources, ruled).Count, "nothing to check after the rules");
    }

    [Test]
    public async Task MuxWritesVerifiesAndNeverOverwrites()
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var work = Directory.CreateTempSubdirectory("universum-mux-");
        try
        {
            var inspector = new MkvMergeInspector(installation, new ProcessRunner());
            var film = await inspector.InspectAsync(TestData.Media("en-und-cz-forced.mkv"));
            var srt = Path.Combine(work.FullName, "titulky.srt");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,500 --> 00:00:01,500\nAhoj, toto je vymyslený titulok.\n\n");
            var subtitles = await inspector.InspectAsync(srt);
            var font = Path.Combine(work.FullName, "poznamka.txt");
            await File.WriteAllTextAsync(font, "vymyslená príloha");
            var output = Path.Combine(work.FullName, "Zlúčený film.mkv");
            var spec = new MuxSpec(
                [new MuxSource(film, KeepTags: false), new MuxSource(subtitles)],
                [
                    new MuxTrack(0, 0, null, LanguageTag.Undetermined, new TrackFlags()),
                    new MuxTrack(0, 2, null, LanguageTag.FromCode("cs"), new TrackFlags(Default: true)),
                    new MuxTrack(1, 0, "Slovenské", LanguageTag.FromCode("sk"), new TrackFlags(Default: false)),
                    new MuxTrack(0, 4, null, LanguageTag.FromCode("cs"), new TrackFlags(Default: false)),
                ],
                output)
            {
                Title = "Vymyslený film",
                Attachments = [new MuxAttachment(font, "poznámka")],
            };

            var job = new MuxJob("Zlučovanie: test", new Multiplexer(installation, new ProcessRunner()), spec);
            var queue = new JobQueue();
            queue.Add(job, start: true);
            await queue.Start();
            Assert.Equal(JobStatus.Done, job.Status, string.Join("; ", job.Messages.Select(m => m.Text)));
            Assert.True(job.Progress == 1, "progress");

            var written = await inspector.InspectAsync(output);
            Assert.Sequence(["video", "audio cs", "subtitles sk", "subtitles cs"],
                written.Tracks.Select(t => t.Type == TrackType.Video ? "video" : $"{t.Type.ToString().ToLowerInvariant()} {t.Language.Primary}"), "tracks");
            Assert.Equal("Slovenské", written.Tracks[2].Name, "name from the second file");
            Assert.Equal("Vymyslený film", written.Title, "title");
            Assert.True(written.HasChapters && written.AttachmentCount == 1 && !written.HasTags, "chapters kept, attachment added, tags left out");
            Assert.True(!Directory.EnumerateFiles(work.FullName).Any(f => f.EndsWith(".universum-tmp", StringComparison.Ordinal)), "no temporary file left");

            var size = new FileInfo(output).Length;
            var again = await new Multiplexer(installation, new ProcessRunner()).MuxAsync(spec);
            Assert.True(again.Status == MuxStatus.Failed && again.Messages.Any(m => m.Contains("existuje", StringComparison.Ordinal)), "existing output refused");
            Assert.Equal(size, new FileInfo(output).Length, "existing output untouched");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    [Test]
    public async Task MuxIntoTheLibraryIsNamedByTheRules()
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var work = Directory.CreateTempSubdirectory("universum-mux-library-");
        try
        {
            var inspector = new MkvMergeInspector(installation, new ProcessRunner());
            var film = await inspector.InspectAsync(TestData.Media("en-und-cz-forced.mkv"));
            var sources = new[] { new MuxSource(film, KeepTags: false) };
            var tracks = new List<MuxTrack>
            {
                new(0, 0, null, LanguageTag.Undetermined, new TrackFlags()),
                new(0, 2, null, LanguageTag.FromCode("cs"), new TrackFlags(Default: true)),
                new(0, 1, null, LanguageTag.FromCode("en"), new TrackFlags(Default: false)),
            };
            var layout = new Universum.Core.Library.LibraryLayout(Path.Combine(work.FullName, "OUTPUT"));
            var title = new Universum.Core.Library.TitleInfo(Universum.Core.Library.MediaKind.Movie, "Vymyslený film", 2026);
            var placement = layout.Place(MuxRules.Target(sources, tracks), title, null, RuleProfile.Default);
            var spec = new MuxSpec(sources, tracks, "") { Placement = placement };
            Assert.Equal(0, spec.Validate().Count, string.Join("; ", spec.Validate()));

            var multiplexer = new Multiplexer(installation, new ProcessRunner());
            var first = await multiplexer.MuxAsync(spec);
            Assert.Equal(MuxStatus.Done, first.Status, string.Join("; ", first.Messages));
            Assert.Equal(Path.Combine(layout.Movies, "Vymyslený film (2026)", "Vymyslený film (2026) - CZ EN SD H264 SMALL.mkv"), first.OutputPath, "name by the rules");
            var second = await multiplexer.MuxAsync(spec);
            Assert.Equal(Path.Combine(layout.Movies, "Vymyslený film (2026)", "Vymyslený film (2026) - CZ EN SD H264 SMALL2.mkv"), second.OutputPath, "a second version gets a number, nothing overwritten");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static MediaFile Film(string path) => new()
    {
        Path = path,
        ContainerType = "Matroska",
        Tracks =
        [
            new MediaTrack { Id = 0, Number = 1, Type = TrackType.Video, CodecId = "V_MPEG4/ISO/AVC" },
            new MediaTrack { Id = 1, Number = 2, Type = TrackType.Audio, CodecId = "A_AAC", Language = LanguageTag.FromCode("en") },
            new MediaTrack { Id = 2, Number = 3, Type = TrackType.Audio, CodecId = "A_AC3", Language = LanguageTag.FromCode("cs") },
            new MediaTrack { Id = 3, Number = 4, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8", Language = LanguageTag.FromCode("cs") },
        ],
    };
}
