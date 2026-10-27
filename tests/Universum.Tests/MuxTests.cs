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
    public void AppendedFilesAndPartsGetTheirOwnFileIds()
    {
        var film = Film("a.mkv");
        var second = Film("b.mkv");
        var other = Film("c.mkv");
        var spec = new MuxSpec(
            [new MuxSource(film, KeepChapters: false) { Appended = [second], AdditionalParts = ["a2.mkv"] }, new MuxSource(other)],
            [
                new MuxTrack(0, 0, null, LanguageTag.Undetermined, new TrackFlags()),
                new MuxTrack(1, 2, null, LanguageTag.FromCode("cs"), new TrackFlags(HearingImpaired: true, Commentary: true))
                {
                    Options = new MuxTrackOptions { DelayMs = -120, DefaultDuration = "25p" },
                },
            ],
            "out.mkv");
        var arguments = MuxArguments.Build(spec, "tmp.mkv").ToList();
        var text = string.Join(" ", arguments);
        Assert.Sequence([0, 2], spec.FileIds(), "the appended file takes ID 1");
        Assert.True(text.Contains("( a.mkv a2.mkv )", StringComparison.Ordinal), "parts read as one: " + text);
        Assert.True(text.Contains("--no-chapters --no-attachments --no-global-tags --no-track-tags + b.mkv", StringComparison.Ordinal), "appended: " + text);
        Assert.True(text.Contains("--hearing-impaired-flag 2:1", StringComparison.Ordinal) && text.Contains("--commentary-flag 2:1", StringComparison.Ordinal) &&
                    text.Contains("--sync 2:-120", StringComparison.Ordinal) && text.Contains("--default-duration 2:25p", StringComparison.Ordinal), "flags and options");
        Assert.True(text.EndsWith("--track-order 0:0,2:2", StringComparison.Ordinal), "order with file IDs: " + text);

        var broken = new MuxTrack(0, 0, null, LanguageTag.Undetermined, new TrackFlags()) { Options = new MuxTrackOptions { DefaultDuration = "fast", Cropping = "1,2" } };
        Assert.True(new MuxSpec([new MuxSource(film)], [broken], "x.mkv").Validate().Count(p => p.StartsWith("Stopa 1", StringComparison.Ordinal)) == 2, "options checked");
        var audioOnly = new MediaFile { Path = "d.mka", Tracks = [new MediaTrack { Id = 0, Type = TrackType.Audio, CodecId = "A_AC3" }] };
        Assert.True(new MuxSpec([new MuxSource(film) { Appended = [audioOnly] }], [new MuxTrack(0, 0, null, LanguageTag.Undetermined, new TrackFlags())], "x.mkv")
            .Validate().Any(p => p.Contains("nedá pripojiť", StringComparison.Ordinal)), "an appended file must continue the same tracks");
    }

    [Test]
    public async Task AppendedFileContinuesTheTracksAndFlagsAreWritten()
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var work = Directory.CreateTempSubdirectory("universum-mux-append-");
        try
        {
            var inspector = new MkvMergeInspector(installation, new ProcessRunner());
            var first = Path.Combine(work.FullName, "Časť 1.mkv");
            var second = Path.Combine(work.FullName, "Časť 2.mkv");
            File.Copy(TestData.Media("cz-only.mkv"), first);
            File.Copy(TestData.Media("cz-only.mkv"), second);
            var a = await inspector.InspectAsync(first);
            var b = await inspector.InspectAsync(second);
            var output = Path.Combine(work.FullName, "Celé.mkv");
            var tracks = a.Tracks.Select(t => new MuxTrack(0, t.Id, null, t.Language, t.Flags with
            {
                HearingImpaired = t.Type == TrackType.Subtitles,
                Original = t.Type == TrackType.Audio,
                TextDescriptions = t.Type == TrackType.Subtitles,
            })).ToList();
            var spec = new MuxSpec([new MuxSource(a) { Appended = [b] }], tracks, output);
            var result = await new Multiplexer(installation, new ProcessRunner()).MuxAsync(spec);
            Assert.Equal(MuxStatus.Done, result.Status, string.Join("; ", result.Messages));
            var written = await inspector.InspectAsync(output);
            Assert.Equal(a.Tracks.Count, written.Tracks.Count, "same tracks");
            Assert.True(written.Duration > a.Duration * 1.9, $"twice as long: {written.Duration} vs {a.Duration}");
            Assert.True(written.Tracks.Single(t => t.Type == TrackType.Subtitles).Flags is { HearingImpaired: true, TextDescriptions: true }, "subtitle flags");
            Assert.True(written.Tracks.Single(t => t.Type == TrackType.Audio).Flags.Original, "original language flag");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    [Test]
    public async Task AppendedSubtitlesAreReadWithTheirCharacterSet()
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var work = Directory.CreateTempSubdirectory("universum-mux-charset-");
        try
        {
            // invented Slovak subtitles in cp1250 ("Žltý kôň"); without the character set for the appended part
            // mkvmerge drops its entries and the result is half as long
            byte[] Cp1250(string start, string end, string tail) =>
                [.. System.Text.Encoding.ASCII.GetBytes($"1\r\n{start} --> {end}\r\n"), 0x8E, (byte)'l', (byte)'t', 0xFD, (byte)' ', (byte)'k', 0xF4, 0xF2,
                 .. System.Text.Encoding.ASCII.GetBytes(tail + "\r\n\r\n")];
            var first = Path.Combine(work.FullName, "CD1.srt");
            var second = Path.Combine(work.FullName, "CD2.srt");
            File.WriteAllBytes(first, Cp1250("00:00:01,000", "00:00:02,000", ""));
            File.WriteAllBytes(second, Cp1250("00:00:01,000", "00:00:03,000", " 2"));
            var inspector = new MkvMergeInspector(installation, new ProcessRunner());
            var a = await inspector.InspectAsync(first);
            var b = await inspector.InspectAsync(second);
            var output = Path.Combine(work.FullName, "Titulky.mkv");
            var track = new MuxTrack(0, 0, null, LanguageTag.FromCode("sk"), new TrackFlags()) { Options = new MuxTrackOptions { SubtitleCharset = "cp1250" } };
            var spec = new MuxSpec([new MuxSource(a) { Appended = [b] }], [track], output);
            var arguments = MuxArguments.Build(spec, output);
            Assert.Equal(2, arguments.Count(x => x == "--sub-charset"), "the character set is given for both parts: " + string.Join(" ", arguments));
            var result = await new Multiplexer(installation, new ProcessRunner()).MuxAsync(spec);
            Assert.Equal(MuxStatus.Done, result.Status, string.Join("; ", result.Messages));
            var written = await inspector.InspectAsync(output);
            Assert.True(written.Duration > TimeSpan.FromSeconds(3.5), $"both parts' entries are kept: {written.Duration}");
        }
        finally
        {
            work.Delete(recursive: true);
        }
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
