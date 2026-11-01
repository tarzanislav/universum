using Universum.Core.Execution;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;

namespace Universum.Tests;

/// <summary>End-to-end: rules → plan → real mkvmerge/mkvpropedit → verification → library, on copies of the test files.</summary>
public sealed class ProcessorTests
{
    private static readonly OutputOptions Keep = new(OriginalHandling.Keep);

    private static readonly OutputOptions Delete = new(OriginalHandling.Delete);

    private static readonly TrackDecisions CzechDub = new()
    {
        Languages = new Dictionary<int, LanguageTag> { [2] = LanguageTag.FromCode("cs"), [3] = LanguageTag.FromCode("cs") },
    };

    [Test]
    public async Task RemuxKeepingTheOriginal()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var original = work.Copy("en-und-cz-forced.mkv");
        var before = await System.IO.File.ReadAllBytesAsync(original);

        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original), decisions: CzechDub));
        Assert.Equal(WriteMethod.Remux, plan.Method);
        var progress = new List<double>();
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Keep, new SyncProgress(progress.Add));

        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.Equal(work.InMovies("Vymyslený film (2026)", "Vymyslený film (2026) - CZ EN SD H264 SMALL.mkv"), result.OutputPath);
        var after = await System.IO.File.ReadAllBytesAsync(original);
        Assert.True(before.AsSpan().SequenceEqual(after), "original untouched");
        Assert.True(progress.Count > 0 && progress[^1] >= 0.99, "progress reported");

        var written = await inspector.InspectAsync(result.OutputPath!);
        Assert.Sequence(["video", "audio cs", "audio en", "subtitles cs", "subtitles cs", "subtitles en"],
            written.Tracks.Select(t => t.Type == TrackType.Video ? "video" : $"{t.Type.ToString().ToLowerInvariant()} {t.Language.Primary}"), "result tracks");
        Assert.True(!written.HasTags, "no tags, no statistics tags");
        Assert.Equal(2, written.ChapterEntries, "chapters kept");
        Assert.Equal<string?>(null, written.Title, "the segment title removed (owner, 29. 10. 2026)");

        // idempotent: the result already matches the rules and is in its place
        var again = EditPlan.For(UniversumRules.Apply(written));
        Assert.Equal(WriteMethod.None, again.Method, "second run changes nothing");
        Assert.Equal(ProcessingStatus.Unchanged, (await processor.ProcessAsync(again, work.Movie(again), Keep)).Status, "already in its place");
        Assert.True(!Directory.EnumerateFiles(work.Path, "*", SearchOption.AllDirectories).Any(FileProcessor.IsTemporaryFile), "no temporary file left");
    }

    [Test]
    public async Task WebVttTickedIsWrittenAsSrtInsideTheResult()
    {
        // owner, 1. 11. 2026: WebVTT in the film written as SRT inside the result, the WebVTT track not kept; only when ticked
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var installation = await TestData.RequireMkvToolNixAsync();
        var vtt = System.IO.Path.Combine(work.Path, "titulky.vtt");
        await System.IO.File.WriteAllTextAsync(vtt, "WEBVTT\n\n00:00.300 --> 00:00.900 line:90%\n<c.yellow>Ahoj, toto je vymyslený titulok.</c>\n\n00:01.000 --> 00:01.800\nĎakujem, dobre.\n");
        var film = System.IO.Path.Combine(work.Path, "film-vtt.mkv");
        var made = await new ProcessRunner().RunAsync(new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvMerge),
            ["-q", "-o", film, TestData.Media("en-und-cz-forced.mkv"), "--language", "0:slk", vtt]));
        Assert.True(made.ExitCode is 0 or 1, "a film with WebVTT made: " + made.StandardError);
        var file = await inspector.InspectAsync(film);
        var webVtt = file.TracksOf(TrackType.Subtitles).Single(t => t.CodecId == "S_TEXT/WEBVTT");
        Assert.True(Universum.Core.Subtitles.SrtConversion.Offers(webVtt), "WebVTT can be written as SRT");

        var untouched = EditPlan.For(UniversumRules.Apply(file, decisions: CzechDub));
        Assert.True(!untouched.Target.Tracks.Any(t => t.ToSrt), "not ticked: kept as it is");

        var plan = EditPlan.For(UniversumRules.Apply(file, decisions: CzechDub with { ToSrt = new HashSet<int> { webVtt.Id } }));
        Assert.True(plan.Method == WriteMethod.Remux && plan.Changes.Any(c => c.Kind == ChangeKind.ToSrt && c.TrackId == webVtt.Id), "ticked: a new file, the change listed");
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Keep);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        var written = await inspector.InspectAsync(result.OutputPath!);
        var subtitles = written.TracksOf(TrackType.Subtitles).ToList();
        Assert.True(subtitles.All(t => t.CodecId == "S_TEXT/UTF8") && subtitles.Any(t => t.Language.Primary == "sk"),
            "no WebVTT left, the Slovak subtitles as SRT – " + string.Join(", ", subtitles.Select(t => $"{t.CodecId} {t.Language.Primary}")));
        var slovak = subtitles.First(t => t.Language.Primary == "sk");
        var document = await SubtitleText.ExtractDocumentAsync(installation, new ProcessRunner(), written, slovak);
        Assert.True(document is { Cues.Count: 2 } && document.Cues[0].Text.Contains("Ahoj, toto je vymyslený titulok.", StringComparison.Ordinal) &&
                    !document.Cues[0].Text.Contains("yellow", StringComparison.Ordinal), "the text kept, WebVTT styling left out");
    }

    [Test]
    public async Task OtherContainersBecomeMkv()
    {
        // owner, 27. 10. 2026: batch processing converts MP4, AVI, TS and other formats – always as a new MKV
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var cases = new (string File, TrackDecisions? Decisions, string Container, string[] Tracks)[]
        {
            ("en-cz.mp4", null, "MP4", ["video", "audio cs", "audio en", "subtitles cs"]),
            ("sk-en.ts", null, "TS", ["video", "audio sk", "audio en"]),
            ("und.avi", new TrackDecisions { Languages = new Dictionary<int, LanguageTag> { [1] = LanguageTag.FromCode("cs") } }, "AVI", ["video", "audio cs"]),
        };
        for (var i = 0; i < cases.Length; i++)
        {
            var (file, decisions, container, tracks) = cases[i];
            var original = work.Copy(file);
            var before = await System.IO.File.ReadAllBytesAsync(original);
            var source = await inspector.InspectAsync(original);
            Assert.True(!source.IsMatroska && source.IsSupported && source.ContainerName == container, $"{file}: {source.ContainerType} ({source.ContainerName})");

            // no default flags in these containers: the first video and audio are the default ones, subtitles are not
            Assert.True(source.Tracks.GroupBy(t => t.Type).All(g => g.Count(t => t.Flags.Default) == (g.Key == TrackType.Subtitles ? 0 : 1) && (g.Key == TrackType.Subtitles || g.First().Flags.Default)),
                $"{file}: default flags " + string.Join(", ", source.Tracks.Select(t => $"{t.Type} {t.Flags.Default}")));
            var target = UniversumRules.Apply(source, decisions: decisions);
            Assert.True(!target.IsBlocked, $"{file}: " + string.Join("; ", target.Issues.Select(x => x.ToString())));
            var plan = EditPlan.For(target);
            Assert.Equal(WriteMethod.Remux, plan.Method, file + ": always a new MKV, never a header edit or a copy");
            Assert.True(plan.Changes.Any(c => c is { Kind: ChangeKind.Container, After: "MKV" } && c.Before == container), file + ": the conversion is listed");

            var placement = work.Layout.Place(target, new TitleInfo(MediaKind.Movie, "Prevedený film " + (i + 1), 2026), null, RuleProfile.Default);
            var result = await processor.ProcessAsync(plan, placement, Keep);
            Assert.Equal(ProcessingStatus.Done, result.Status, $"{file}: " + string.Join("; ", result.Messages));
            Assert.True(result.OutputPath!.EndsWith(".mkv", StringComparison.Ordinal), file + ": " + result.OutputPath);
            var written = await inspector.InspectAsync(result.OutputPath);
            Assert.True(written.IsMatroska, file + ": the result is Matroska");
            Assert.Sequence(tracks, written.Tracks.Select(t => t.Type == TrackType.Video ? "video" : $"{t.Type.ToString().ToLowerInvariant()} {t.Language.Primary}"), file + ": result tracks");
            var after = await System.IO.File.ReadAllBytesAsync(original);
            Assert.True(before.AsSpan().SequenceEqual(after), file + ": original untouched");

            // the result is Matroska: a second run finds nothing to do
            Assert.Equal(WriteMethod.None, EditPlan.For(UniversumRules.Apply(written)).Method, file + ": second run changes nothing");
        }
    }

    [Test]
    public async Task RemuxReplacingTheOriginalUsesTheRecycleBin()
    {
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var (processor, inspector) = await CreateAsync(work, bin);
        var original = work.Copy("sk-cz-mixed.mkv");
        var time = new DateTime(2020, 5, 17, 10, 0, 0, DateTimeKind.Utc);
        System.IO.File.SetLastWriteTimeUtc(original, time);

        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original)));
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Delete);

        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.Equal("Vymyslený film (2026) - CZ SK EN SD H264 SMALL.mkv", Path.GetFileName(result.OutputPath), "name with the audio languages");
        Assert.Equal(1, bin.Recycled.Count, "original in the Recycle Bin");
        Assert.True(!System.IO.File.Exists(original), "original removed");
        Assert.Equal(time.AddSeconds(2), System.IO.File.GetLastWriteTimeUtc(result.OutputPath!), "modification time kept (+2 s so changes are noticed)");
        var audio = (await inspector.InspectAsync(result.OutputPath!)).TracksOf(TrackType.Audio).Select(t => t.Language.Primary);
        Assert.Sequence(["cs", "sk", "en"], audio, "audio order");
    }

    [Test]
    public async Task HeaderEditMovesTheOriginalIntoTheLibraryAndUndoMovesItBack()
    {
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var (processor, inspector) = await CreateAsync(work, bin);
        var file = work.Copy("cz-only.mkv");
        var tags = Path.Combine(work.Path, "tags.xml");
        await System.IO.File.WriteAllTextAsync(tags, "<?xml version=\"1.0\"?><Tags><Tag><Targets/><Simple><Name>ENCODER</Name><String>X</String></Simple></Tag></Tags>");
        var installation = await TestData.RequireMkvToolNixAsync();
        await new ProcessRunner().RunAsync(new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvPropEdit),
            [file, "--tags", "global:" + tags, "--edit", "track:a1", "--set", "name=Dabing CZ", "--edit", "track:s1", "--set", "flag-default=1"]));
        var before = await inspector.InspectAsync(file);

        var plan = EditPlan.For(UniversumRules.Apply(before));
        Assert.Equal(WriteMethod.HeaderEdit, plan.Method);
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Delete);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.True(result.UndoRecordPath is not null, "undo record");
        Assert.True(!System.IO.File.Exists(file) && bin.Recycled.Count == 0, "moved, not copied and recycled");
        Assert.Equal("Vymyslený film (2026) - CZ SD H264 SMALL.mkv", Path.GetFileName(result.OutputPath));

        var after = await inspector.InspectAsync(result.OutputPath!);
        Assert.True(!after.HasTags && after.TracksOf(TrackType.Audio).Single().Name is null, "edited");
        Assert.True(!after.TracksOf(TrackType.Subtitles).Single().Flags.Default, "subtitles not default");

        Assert.Equal(0, (await processor.UndoAsync(result.UndoRecordPath!)).Count, "undo succeeded");
        Assert.True(System.IO.File.Exists(file) && !System.IO.File.Exists(result.OutputPath!), "moved back");
        var restored = await inspector.InspectAsync(file);
        Assert.Equal("Dabing CZ", restored.TracksOf(TrackType.Audio).Single().Name, "name restored");
        Assert.True(restored.TracksOf(TrackType.Subtitles).Single().Flags.Default, "flag restored");
        Assert.Equal(1, restored.GlobalTagEntries, "tags restored");
    }

    [Test]
    public async Task SameStreamIsNotWrittenTwiceAnotherGetsANumber()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var original = work.Copy("cz-only.mkv");
        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original)));
        Assert.Equal(WriteMethod.None, plan.Method, "clean file: only placed");

        var first = await processor.ProcessAsync(plan, work.Movie(plan), Keep);
        Assert.Equal(ProcessingStatus.Done, first.Status, string.Join("; ", first.Messages));
        Assert.Equal("Vymyslený film (2026) - CZ SD H264 SMALL.mkv", Path.GetFileName(first.OutputPath));

        var second = await processor.ProcessAsync(plan, work.Movie(plan), Keep);
        Assert.Equal(ProcessingStatus.AlreadyInLibrary, second.Status, "the same stream again (K8)");
        Assert.Equal(first.OutputPath, second.OutputPath, "the file found in the library");

        // a different stream with the same description: the audio delayed, so the film is longer
        var installation = await TestData.RequireMkvToolNixAsync();
        var cut = Path.Combine(work.Path, "cut.mkv");
        await new ProcessRunner().RunAsync(new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvMerge),
            ["-o", cut, "--sync", "1:1500", original]));
        var other = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(cut)));
        var third = await processor.ProcessAsync(other, work.Movie(other), Keep);
        Assert.Equal(ProcessingStatus.Done, third.Status, string.Join("; ", third.Messages));
        Assert.Equal("Vymyslený film (2026) - CZ SD H264 SMALL2.mkv", Path.GetFileName(third.OutputPath), "version number (K1)");
    }

    [Test]
    public async Task EpisodeGoesIntoItsSeasonFolder()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var original = work.Copy("en-only-cz-subs.mkv");
        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original)));
        var title = new TitleInfo(MediaKind.Series, "Nine Puzzles", 2025, 227191, IncludeId: true);
        var placement = work.Layout.Place(plan.Target, title, new EpisodeNumber(1, 4), RuleProfile.Default);

        var result = await processor.ProcessAsync(plan, placement, Keep);

        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.Equal(
            Path.Combine(work.Layout.Shows, "Nine Puzzles (2025) [tmdbid-227191]", "Season 01", "Nine Puzzles S01E04 - CZ titulky SD H264 SMALL.mkv"),
            result.OutputPath);
        Assert.True(System.IO.File.Exists(original), "original kept");
        var subtitles = (await inspector.InspectAsync(result.OutputPath!)).TracksOf(TrackType.Subtitles).ToList();
        Assert.Sequence([(false, true), (true, false), (false, false)], subtitles.Select(s => (s.Flags.Default, s.Flags.Forced)), "forced CZ, full CZ default, EN");
    }

    [Test]
    public async Task FailedEditPutsTheOriginalBack()
    {
        using var work = new WorkFolder();
        var runner = new FailingRunner(new ProcessRunner());
        var (processor, inspector) = await CreateAsync(work, runner: runner);
        var file = work.Copy("cz-only.mkv");
        var installation = await TestData.RequireMkvToolNixAsync();
        await new ProcessRunner().RunAsync(new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvPropEdit), [file, "--edit", "track:a1", "--set", "name=Dabing CZ"]));
        var time = new DateTime(2021, 3, 4, 5, 6, 8, DateTimeKind.Utc);
        System.IO.File.SetLastWriteTimeUtc(file, time);
        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(file)));
        Assert.Equal(WriteMethod.HeaderEdit, plan.Method);

        runner.FailNextEdit = true;
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Delete);

        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.True(result.Messages.Contains("the original was restored"), string.Join("; ", result.Messages));
        Assert.True(System.IO.File.Exists(file), "original back in its place");
        Assert.Equal("Dabing CZ", (await inspector.InspectAsync(file)).TracksOf(TrackType.Audio).Single().Name, "header as before");
        Assert.Equal(time, System.IO.File.GetLastWriteTimeUtc(file), "modification time as before");
        Assert.True(!Directory.EnumerateFiles(work.Layout.Root, "*", SearchOption.AllDirectories).Any(), "nothing left in the library");
    }

    [Test]
    public async Task ChangedFileIsNotWritten()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var file = work.Copy("sk-cz-mixed.mkv");
        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(file)));
        var installation = await TestData.RequireMkvToolNixAsync();
        await new ProcessRunner().RunAsync(new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvPropEdit), [file, "--edit", "track:a1", "--set", "name=changed"]));

        var result = await processor.ProcessAsync(plan, work.Movie(plan), Delete);
        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.True(result.Messages.Single().Contains("changed since the analysis", StringComparison.Ordinal), result.Messages.Single());
        Assert.True(!Directory.Exists(work.Layout.Root), "nothing written");
    }

    [Test]
    public async Task ReplacingALibraryFileSendsTheOldOneToTheRecycleBin()
    {
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var (processor, inspector) = await CreateAsync(work, bin);
        var original = work.Copy("sk-cz-mixed.mkv");
        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original)));
        var first = await processor.ProcessAsync(plan, work.Movie(plan), Keep);
        Assert.Equal(ProcessingStatus.Done, first.Status, string.Join("; ", first.Messages));

        // the library file again, with English first: a new file replaces it in the same folder
        var englishFirst = RuleProfile.Default with { OrderLanguages = ["en", "cs", "sk"] };
        var again = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(first.OutputPath!), englishFirst));
        Assert.Equal(WriteMethod.Remux, again.Method);
        var placement = work.Layout.Place(again.Target, new TitleInfo(MediaKind.Movie, "Vymyslený film", 2026), null, englishFirst);
        var second = await processor.ProcessAsync(again, placement, Keep);

        Assert.Equal(ProcessingStatus.Done, second.Status, string.Join("; ", second.Messages));
        Assert.Equal("Vymyslený film (2026) - EN CZ SK SD H264 SMALL.mkv", Path.GetFileName(second.OutputPath));
        Assert.True(!System.IO.File.Exists(first.OutputPath!), "old library file replaced");
        Assert.True(bin.Recycled.Single().EndsWith(FileProcessor.ReplacedExtension, StringComparison.Ordinal), "old file in the Recycle Bin");
        Assert.True(System.IO.File.Exists(original), "the original outside the library untouched");
    }

    [Test]
    public async Task CorrectedRunReplacesTheEarlierResult()
    {
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var (processor, inspector) = await CreateAsync(work, bin);
        var original = work.Copy("sk-cz-mixed.mkv");
        var file = await inspector.InspectAsync(original);
        var first = await processor.ProcessAsync(EditPlan.For(UniversumRules.Apply(file)), work.Movie(EditPlan.For(UniversumRules.Apply(file))), Keep);
        Assert.Equal(ProcessingStatus.Done, first.Status, string.Join("; ", first.Messages));

        // the user corrects a decision (Slovak subtitles are forced): same name, different result
        var corrected = EditPlan.For(UniversumRules.Apply(file, decisions: new TrackDecisions { Forced = new Dictionary<int, bool> { [4] = true } }));
        var second = await processor.ProcessAsync(corrected, work.Movie(corrected), Keep);
        Assert.Equal(ProcessingStatus.Done, second.Status, "not skipped: " + string.Join("; ", second.Messages));
        Assert.Equal(first.OutputPath, second.OutputPath, "takes the place of the earlier result");
        Assert.Equal(1, bin.Recycled.Count, "earlier result in the Recycle Bin");
        Assert.True((await inspector.InspectAsync(second.OutputPath!)).TracksOf(TrackType.Subtitles).Any(t => t.Flags.Forced), "corrected flags written");

        var third = await processor.ProcessAsync(corrected, work.Movie(corrected), Keep);
        Assert.Equal(ProcessingStatus.AlreadyInLibrary, third.Status, "the same decisions again: nothing to do");

        // owner, 31. 10. 2026: Revízia – written again all the same; the new result verified, then the earlier one to the Recycle Bin
        var revision = await processor.ProcessAsync(corrected with { Revision = true }, work.Movie(corrected), Keep);
        Assert.Equal(ProcessingStatus.Done, revision.Status, "a revision is written: " + string.Join("; ", revision.Messages));
        Assert.Equal(second.OutputPath, revision.OutputPath, "the new version takes the place of the earlier one");
        Assert.True(bin.Recycled.Count == 2 && revision.Messages.Any(m => m.StartsWith(FileProcessor.ReplacedPreviousPrefix, StringComparison.Ordinal)), "the earlier version in the Recycle Bin");
        Assert.True(System.IO.File.Exists(original), "the original stays");
        Assert.Equal(ProcessingStatus.AlreadyInLibrary, (await processor.ProcessAsync(corrected, work.Movie(corrected), Keep)).Status, "without revision the new version is found");
    }

    [Test]
    public async Task CleanOriginalIsMovedAndUndoMovesItBack()
    {
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var (processor, inspector) = await CreateAsync(work, bin);
        var original = work.Copy("cz-only.mkv");
        var before = await System.IO.File.ReadAllBytesAsync(original);
        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original)));
        Assert.Equal(WriteMethod.None, plan.Method);

        var result = await processor.ProcessAsync(plan, work.Movie(plan), Delete);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.True(!System.IO.File.Exists(original) && bin.Recycled.Count == 0, "moved, not recycled");
        var moved = await System.IO.File.ReadAllBytesAsync(result.OutputPath!);
        Assert.True(before.SequenceEqual(moved), "content unchanged");

        Assert.Equal(0, (await processor.UndoAsync(result.UndoRecordPath!)).Count, "undo");
        var restored = await System.IO.File.ReadAllBytesAsync(original);
        Assert.True(before.SequenceEqual(restored), "back in its place, unchanged");
    }

    [Test]
    public async Task MissingVideoIndexIsRepaired()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var installation = await TestData.RequireMkvToolNixAsync();
        var file = System.IO.Path.Combine(work.Path, "no-index.mkv");
        await new ProcessRunner().RunAsync(new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvMerge),
            ["-o", file, "--cues", "0:none", TestData.Media("cz-only.mkv")]));
        var source = await inspector.InspectAsync(file);
        Assert.Equal(0, source.TracksOf(TrackType.Video).Single().IndexEntries, "test file without index");

        var plan = EditPlan.For(UniversumRules.Apply(source));
        Assert.True(plan.Target.Issues.Any(i => i.Kind == RuleIssueKind.MissingIndex), "health issue reported");
        Assert.Equal(WriteMethod.Remux, plan.Method, "index needs a new file");
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Keep);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.True((await inspector.InspectAsync(result.OutputPath!)).TracksOf(TrackType.Video).Single().IndexEntries > 0, "index created");

        var withoutRepair = EditPlan.For(UniversumRules.Apply(source, RuleProfile.Default with { RepairIndex = false }));
        Assert.Equal(WriteMethod.None, withoutRepair.Method, "repair can be switched off");
    }

    [Test]
    public async Task NoRecycleBinKeepsTheOriginal()
    {
        // owner, 30. 10. 2026: never deleted permanently – without a Recycle Bin the result is written and the original stays
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work, new FolderRecycleBin(Path.Combine(work.Path, "bin")) { Available = false });
        var original = work.Copy("sk-cz-mixed.mkv");
        var bytes = await System.IO.File.ReadAllBytesAsync(original);
        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original)));
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Delete);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.True(System.IO.File.Exists(result.OutputPath!) && (await System.IO.File.ReadAllBytesAsync(original)).SequenceEqual(bytes), "result written, original untouched");
        Assert.True(result.Messages.Single().StartsWith(FileProcessor.OriginalKeptPrefix, StringComparison.Ordinal), "and said so: " + string.Join("; ", result.Messages));
    }

    [Test]
    public async Task BatchDeleteWritesANewFileFirst()
    {
        // owner, 30. 10. 2026: Hromadné spracovanie deletes an original only after a new verified file is in the library –
        // also when only the header changes (never moved and edited) – and the original goes to the Recycle Bin untouched
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var (processor, inspector) = await CreateAsync(work, bin);
        var file = work.Copy("cz-only.mkv");
        await new ProcessRunner().RunAsync(new ProcessRequest((await TestData.RequireMkvToolNixAsync()).ToolPath(MkvToolNixTool.MkvPropEdit),
            [file, "--edit", "track:a1", "--set", "name=Dabing CZ"]));
        var bytes = await System.IO.File.ReadAllBytesAsync(file);
        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(file)));
        Assert.Equal(WriteMethod.HeaderEdit, plan.Method, "only the header changes");

        var result = await processor.ProcessAsync(plan, work.Movie(plan), new OutputOptions(OriginalHandling.Delete, MoveIntoLibrary: false));
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.True(result.UndoRecordPath is null && !System.IO.File.Exists(file) && bin.Recycled.Count == 1, "a new file, the original recycled");
        Assert.True(result.Messages.Contains(FileProcessor.OriginalRecycledMessage), "said so");
        var recycled = Path.Combine(work.Path, "bin", Path.GetFileName(file));
        Assert.True((await System.IO.File.ReadAllBytesAsync(recycled)).SequenceEqual(bytes), "the original in the Recycle Bin is untouched");
        Assert.True((await inspector.InspectAsync(result.OutputPath!)).TracksOf(TrackType.Audio).Single().Name is null, "the result is edited");

        // review, 30. 10. 2026: a result of a library (here the old one, after the output folder was changed) analysed as an
        // original is copied into the new library, never sent to the Recycle Bin
        var otherLibrary = new LibraryLayout(Path.Combine(work.Path, "OUTPUT2"));
        var again = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(result.OutputPath!)) with { RemoveTags = false });
        var placement = otherLibrary.Place(again.Target, new TitleInfo(MediaKind.Movie, "Vymyslený film", 2026), null, RuleProfile.Default);
        var copy = await processor.ProcessAsync(again with { Method = WriteMethod.Remux }, placement, new OutputOptions(OriginalHandling.Delete, MoveIntoLibrary: false));
        Assert.True(copy.Status == ProcessingStatus.Done && System.IO.File.Exists(result.OutputPath!) && bin.Recycled.Count == 1 &&
                    copy.Messages.Any(m => m.StartsWith(FileProcessor.OriginalKeptPrefix, StringComparison.Ordinal)),
            "the library file stays: " + copy.Status + " " + string.Join("; ", copy.Messages));
    }

    [Test]
    public async Task ManualCleanupRemovesOnlySafeOriginals()
    {
        // owner, 30. 10. 2026: Zmazať originály – only originals whose result is in the library and that did not change;
        // nothing in the library itself; into the Recycle Bin only
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var (processor, inspector) = await CreateAsync(work, bin);
        var results = new List<ProcessingResult>();
        foreach (var name in new[] { "sk-cz-mixed.mkv", "en-only-cz-subs.mkv", "cz-only.mkv" })
        {
            var original = work.Copy(name);
            var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original)));
            var title = new TitleInfo(MediaKind.Movie, "Vymyslený film " + results.Count, 2026);
            results.Add(await processor.ProcessAsync(plan, work.Layout.Place(plan.Target, title, null, RuleProfile.Default), Keep));
        }

        Assert.True(results.All(r => r.Status == ProcessingStatus.Done && System.IO.File.Exists(r.SourcePath)), "written, originals kept: " + string.Join("; ", results.SelectMany(r => r.Messages)));
        var originals = OriginalCleanup.Collect(results);
        Assert.Equal(3, originals.Count, "three originals with their results");

        // one changed after the run, one whose result was removed from the library
        var index = processor.Index!;
        System.IO.File.SetLastWriteTimeUtc(results[1].SourcePath, DateTime.UtcNow.AddMinutes(5));
        System.IO.File.Delete(results[2].OutputPath!);
        var (removed, kept) = await OriginalCleanup.RemoveAsync(originals, index, bin, work.Layout.HoldsResult);
        Assert.Sequence([results[0].SourcePath], removed, "only the safe original");
        Assert.True(!System.IO.File.Exists(results[0].SourcePath) && bin.Recycled.Count == 1 && System.IO.File.Exists(results[0].OutputPath!), "in the Recycle Bin, the result stays");
        Assert.True(kept.Count == 2 && kept.Any(k => k.Contains("zmenil", StringComparison.Ordinal)) && kept.Any(k => k.Contains("výsledok", StringComparison.Ordinal)),
            "the others stay with the reason: " + string.Join("; ", kept));

        // review, 30. 10. 2026: the time put back, but the library file at the result's place is not made from it
        var changed = OriginalCleanup.Collect([results[1]]).Single() with { };
        var other = work.Copy("sk-cz-mixed.mkv");
        System.IO.File.Copy(other, results[1].OutputPath!, overwrite: true);
        var (removedOther, keptOther) = await OriginalCleanup.RemoveAsync([changed], index, bin, work.Layout.HoldsResult);
        Assert.True(removedOther.Count == 0 && keptOther.Single().Contains("overiť", StringComparison.Ordinal), "not verified: " + string.Join("; ", keptOther));

        // a file of the library is never removed, nor an original that cannot go to the Recycle Bin
        var inLibrary = new WrittenOriginal(results[0].OutputPath!, new FileInfo(results[0].OutputPath!).Length, System.IO.File.GetLastWriteTimeUtc(results[0].OutputPath!), results[0].OutputPath!);
        var (removedNow, keptNow) = await OriginalCleanup.RemoveAsync([inLibrary], index, new FolderRecycleBin(Path.Combine(work.Path, "bin2")) { Available = false }, _ => false);
        Assert.True(removedNow.Count == 0 && System.IO.File.Exists(inLibrary.Source) && keptNow.Single().Contains("knižnice", StringComparison.Ordinal),
            "a library file is never removed, even when the folder is not known as the library: " + string.Join("; ", keptNow));
    }

    [Test]
    public async Task BlockedFileIsNotWritten()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var original = work.Copy("en-und-cz-forced.mkv");
        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original)));
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Delete);
        Assert.Equal(ProcessingStatus.NeedsReview, result.Status);
    }

    [Test]
    public async Task HeaderEditorEditsInPlaceAndUndoRestoresEverything()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var file = work.Copy("en-und-cz-forced.mkv");
        var before = await inspector.InspectAsync(file);
        var edits = ManualTarget.AsIs(before).Select(e => e.TrackId switch
        {
            1 => e with { Name = "Originál", Flags = e.Flags with { Default = false } },
            2 => e with { Language = LanguageTag.FromCode("cs"), Name = null, Flags = e.Flags with { Default = true } },
            _ => e,
        }).ToList();
        var plan = EditPlan.For(ManualTarget.Create(before, edits, title: "Vymyslený film"));
        Assert.Equal(WriteMethod.HeaderEdit, plan.Method, "names, languages, flags and title: header only");

        var result = await processor.ProcessAsync(plan, Placement.InPlace(file), Keep);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.Equal(Path.GetFullPath(file), result.OutputPath, "edited where it is, same name");
        var after = await inspector.InspectAsync(file);
        Assert.Equal("Vymyslený film", after.Title, "title");
        Assert.True(after.Tracks[1].Name == "Originál" && !after.Tracks[1].Flags.Default, "English audio");
        Assert.True(after.Tracks[2].Language.Primary == "cs" && after.Tracks[2].Name is null && after.Tracks[2].Flags.Default, "Czech audio");
        Assert.True(after.HasTags == before.HasTags, "tags untouched");

        Assert.Equal(0, (await processor.UndoAsync(result.UndoRecordPath!)).Count, "undo");
        var restored = await inspector.InspectAsync(file);
        Assert.Equal(before.Title, restored.Title, "title restored");
        Assert.Sequence(before.Tracks.Select(t => (t.Name, t.Language.Primary, t.Flags.Default)), restored.Tracks.Select(t => (t.Name, t.Language.Primary, t.Flags.Default)), "tracks restored");
    }

    [Test]
    public async Task HeaderEditorReordersByWritingANewFileUnderTheSameName()
    {
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var (processor, inspector) = await CreateAsync(work, bin);
        var file = work.Copy("en-und-cz-forced.mkv");
        var before = await inspector.InspectAsync(file);
        var edits = ManualTarget.AsIs(before).ToList();
        (edits[1], edits[2]) = (edits[2], edits[1]);
        var plan = EditPlan.For(ManualTarget.Create(before, edits));
        Assert.Equal(WriteMethod.Remux, plan.Method, "order: new file");

        var result = await processor.ProcessAsync(plan, Placement.InPlace(file), Keep);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.Equal(Path.GetFullPath(file), result.OutputPath, "same name");
        Assert.Equal(1, bin.Recycled.Count, "the old file went to the Recycle Bin");
        var after = await inspector.InspectAsync(file);
        Assert.Sequence([before.Tracks[2].Name, before.Tracks[1].Name], after.TracksOf(TrackType.Audio).Select(t => t.Name), "audio swapped");
        Assert.Equal(before.Title, after.Title, "title kept by mkvmerge");
        Assert.True(!Directory.EnumerateFiles(work.Path).Any(f => FileProcessor.IsTemporaryFile(f) || FileProcessor.IsInterruptedFile(f)), "nothing left over");
    }

    [Test]
    public async Task NewChaptersGoIntoTheLibraryCopyAndUndoInTheLibraryRestoresThem()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var installation = await TestData.RequireMkvToolNixAsync();
        var chaptersReader = new Universum.Core.Chapters.ChapterEditor(installation, new ProcessRunner(), Path.Combine(work.Path, "journal-ch"));
        var original = work.Copy("en-und-cz-forced.mkv");
        var bytes = await System.IO.File.ReadAllBytesAsync(original);
        var source = await inspector.InspectAsync(original);
        var czech = LanguageTag.FromCode("cs");
        var chapters = new List<Universum.Core.Chapters.Chapter> { new(TimeSpan.Zero, "Začiatok", czech), new(TimeSpan.FromMilliseconds(800), "Stred", czech), new(TimeSpan.FromSeconds(1.5), "Koniec", czech) };

        // header only (tracks as they are) + new chapters: a copy in the library, the original untouched
        var plan = EditPlan.For(ManualTarget.Create(source, ManualTarget.AsIs(source)) with { NewChapters = chapters });
        Assert.Equal(WriteMethod.HeaderEdit, plan.Method, "chapters alone: header edit");
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Keep);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        var now = await System.IO.File.ReadAllBytesAsync(original);
        Assert.True(bytes.SequenceEqual(now), "original untouched");
        Assert.Sequence(["Začiatok", "Stred", "Koniec"], (await chaptersReader.ReadAsync(result.OutputPath!)).Chapters.Select(c => c.Name), "chapters in the library copy");

        // the library file edited again in place: undo brings its chapters back
        var inLibrary = await inspector.InspectAsync(result.OutputPath!);
        var again = EditPlan.For(ManualTarget.Create(inLibrary, ManualTarget.AsIs(inLibrary)) with { NewChapters = [] });
        var edited = await processor.ProcessAsync(again, work.Movie(again), Keep);
        Assert.True(edited.Status == ProcessingStatus.Done && edited.UndoRecordPath is not null, string.Join("; ", edited.Messages));
        Assert.Equal(0, (await inspector.InspectAsync(edited.OutputPath!)).ChapterEntries, "chapters removed");
        Assert.Equal(0, (await processor.UndoAsync(edited.UndoRecordPath!)).Count, "undo");
        Assert.Equal(3, (await chaptersReader.ReadAsync(edited.OutputPath!)).Chapters.Count, "chapters back");

        // the library index follows the result through edit and undo: it is still a result and is edited where it is
        Assert.True(new LibraryIndex(Path.Combine(work.Path, "library-index.json")).IsResult(edited.OutputPath!), "still recorded as a result");
        var restored = await inspector.InspectAsync(edited.OutputPath!);
        var third = await processor.ProcessAsync(EditPlan.For(ManualTarget.Create(restored, ManualTarget.AsIs(restored)) with { NewChapters = [] }), work.Movie(again), Keep);
        Assert.True(third.Status == ProcessingStatus.Done && third.UndoRecordPath is not null && Paths.Same(third.OutputPath!, edited.OutputPath!), "edited in the library again: " + string.Join("; ", third.Messages));
        Assert.Equal(0, (await processor.UndoAsync(third.UndoRecordPath!)).Count, "undo again");

        // a new order and new chapters: mkvmerge takes the chapters from the file
        var reorder = ManualTarget.AsIs(source).ToList();
        (reorder[1], reorder[2]) = (reorder[2], reorder[1]);
        var remux = EditPlan.For(ManualTarget.Create(source, reorder) with { NewChapters = chapters.Take(1).ToList() });
        Assert.Equal(WriteMethod.Remux, remux.Method);
        var written = await processor.ProcessAsync(remux, work.Movie(remux), Keep);
        Assert.Equal(ProcessingStatus.Done, written.Status, string.Join("; ", written.Messages));
        Assert.Sequence(["Začiatok"], (await chaptersReader.ReadAsync(written.OutputPath!)).Chapters.Select(c => c.Name), "only the new chapters");
    }

    [Test]
    public async Task ResultChangedOutsideUniversumCountsAsAnOriginal()
    {
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var (processor, inspector) = await CreateAsync(work, bin);
        var original = work.Copy("en-und-cz-forced.mkv");
        var source = await inspector.InspectAsync(original);
        var first = await processor.ProcessAsync(EditPlan.For(ManualTarget.Create(source, ManualTarget.AsIs(source), title: "Prvý")), work.Movie(EditPlan.For(ManualTarget.Create(source, ManualTarget.AsIs(source)))), Keep);
        Assert.Equal(ProcessingStatus.Done, first.Status, string.Join("; ", first.Messages));
        var result = first.OutputPath!;

        // edited in the library with undo, then the file is touched by another program: undo refuses
        var inLibrary = await inspector.InspectAsync(result);
        var edit = EditPlan.For(ManualTarget.Create(inLibrary, ManualTarget.AsIs(inLibrary), title: "Druhý"));
        var edited = await processor.ProcessAsync(edit, work.Movie(edit), Keep);
        Assert.True(edited.Status == ProcessingStatus.Done && edited.UndoRecordPath is not null, "edited in place: " + string.Join("; ", edited.Messages));
        System.IO.File.SetLastWriteTimeUtc(edited.OutputPath!, System.IO.File.GetLastWriteTimeUtc(edited.OutputPath!).AddHours(1));
        var bytes = await System.IO.File.ReadAllBytesAsync(edited.OutputPath!);
        Assert.True((await processor.UndoAsync(edited.UndoRecordPath!)).Any(p => p.Contains("has changed since", StringComparison.Ordinal)), "undo refused");
        var afterUndo = await System.IO.File.ReadAllBytesAsync(edited.OutputPath!);
        Assert.True(bytes.SequenceEqual(afterUndo), "nothing restored");

        // and it is no longer reworked where it is: the next edit writes a new file, the changed one stays as it is
        var changed = await inspector.InspectAsync(edited.OutputPath!);
        var again = EditPlan.For(ManualTarget.Create(changed, ManualTarget.AsIs(changed), title: "Tretí"));
        var third = await processor.ProcessAsync(again, work.Movie(again), Keep);
        Assert.Equal(ProcessingStatus.Done, third.Status, string.Join("; ", third.Messages));
        Assert.True(third.UndoRecordPath is null && !Paths.Same(third.OutputPath!, edited.OutputPath!), "a new file");
        var afterThird = await System.IO.File.ReadAllBytesAsync(edited.OutputPath!);
        Assert.True(bytes.SequenceEqual(afterThird), "the changed file untouched");
        Assert.Equal(0, bin.Recycled.Count, "nothing in the Recycle Bin");
    }

    [Test]
    public async Task OriginalLyingInItsTitleFolderIsOnlyRead()
    {
        // an existing collection: originals already sit in MOVIES\Vymyslený film (2026)\ under their own names
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var (processor, inspector) = await CreateAsync(work, bin);
        var folder = work.InMovies("Vymyslený film (2026)");
        Directory.CreateDirectory(folder);
        // also names that look like library names (Jellyfin's own naming of versions)
        // first an original lying exactly where its result would go
        var cases = new (string File, Func<MediaFile, TargetState> Target, WriteMethod Method)[]
        {
            ("Vymyslený film (2026) - CZ SD H264 SMALL.mkv", f => UniversumRules.Apply(f), WriteMethod.None),
            ("Vymysleny.Film.2026.cz.mkv", f => UniversumRules.Apply(f), WriteMethod.None),
            ("Vymysleny.Film.2026.header.mkv", f => ManualTarget.Create(f, ManualTarget.AsIs(f), title: "Iný názov"), WriteMethod.HeaderEdit),
            ("Vymysleny.Film.2026.remux.mkv", f => UniversumRules.Apply(f, decisions: CzechDub), WriteMethod.Remux),
            ("Vymyslený film (2026) - 1080p BluRay.mkv", f => UniversumRules.Apply(f), WriteMethod.None),
            ("Vymyslený film (2026) - Bluray.mkv", f => ManualTarget.Create(f, ManualTarget.AsIs(f), title: "Iný názov 2"), WriteMethod.HeaderEdit),
            ("Vymyslený film (2026) - Think Big.mkv", f => UniversumRules.Apply(f, decisions: CzechDub) with { NewTitle = "Iný 3" }, WriteMethod.Remux),
        };
        var sources = new[] { "cz-only.mkv", "cz-only.mkv", "sk-cz-mixed.mkv", "en-und-cz-forced.mkv", "cz-only.mkv", "sk-cz-mixed.mkv", "en-und-cz-forced.mkv" };
        for (var i = 0; i < cases.Length; i++)
        {
            var original = Path.Combine(folder, cases[i].File);
            System.IO.File.Copy(TestData.Media(sources[i]), original);
            var bytes = await System.IO.File.ReadAllBytesAsync(original);
            var plan = EditPlan.For(cases[i].Target(await inspector.InspectAsync(original)));
            Assert.Equal(cases[i].Method, plan.Method, cases[i].File);
            var result = await processor.ProcessAsync(plan, work.Movie(plan), Keep);
            Assert.True(result.Status is ProcessingStatus.Done or ProcessingStatus.AlreadyInLibrary, $"{cases[i].File}: {result.Status} {string.Join("; ", result.Messages)}");
            var now = await System.IO.File.ReadAllBytesAsync(original);
            Assert.True(bytes.SequenceEqual(now), $"{cases[i].File}: the original is still there, unchanged");
            Assert.True(result.UndoRecordPath is null && !Paths.Same(result.OutputPath!, original), $"{cases[i].File}: a new file, no in-place edit");
        }

        // results of the same original replace each other (K8); an original never goes to the Recycle Bin
        Assert.True(bin.Recycled.All(r => !cases.Any(c => r.Contains(Path.GetFileNameWithoutExtension(c.File), StringComparison.Ordinal))), "no original in the Recycle Bin: " + string.Join(", ", bin.Recycled));
    }

    [Test]
    public async Task OriginalsAreKeptUnlessDeletingIsAllowed()
    {
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var installation = await TestData.RequireMkvToolNixAsync();
        var processor = new FileProcessor(installation, new ProcessRunner(), bin, Path.Combine(work.Path, "journal"));
        var inspector = new MkvMergeInspector(installation, new ProcessRunner());
        foreach (var name in new[] { "sk-cz-mixed.mkv", "cz-only.mkv" })
        {
            var original = work.Copy(name);
            var bytes = await System.IO.File.ReadAllBytesAsync(original);
            var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original)));
            var result = await processor.ProcessAsync(plan, work.Movie(plan), Delete);
            Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
            var now = await System.IO.File.ReadAllBytesAsync(original);
            Assert.True(bytes.SequenceEqual(now), $"{name}: original kept and unchanged although 'delete' was asked ({plan.Method})");
        }

        Assert.Equal(0, bin.Recycled.Count, "nothing in the Recycle Bin");
    }

    [Test]
    public async Task HeaderEditorKeepingOriginalsWritesANewFileBeside()
    {
        using var work = new WorkFolder();
        var bin = new FolderRecycleBin(Path.Combine(work.Path, "bin"));
        var (processor, inspector) = await CreateAsync(work, bin);
        var file = work.Copy("en-und-cz-forced.mkv");
        var bytes = await System.IO.File.ReadAllBytesAsync(file);
        var before = await inspector.InspectAsync(file);
        var edits = ManualTarget.AsIs(before).ToList();
        (edits[1], edits[2]) = (edits[2], edits[1]);
        var plan = EditPlan.For(ManualTarget.Create(before, edits));

        var result = await processor.ProcessAsync(plan, Placement.Beside(file), Keep);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.Equal(Path.Combine(work.Path, "en-und-cz-forced (upravené).mkv"), result.OutputPath, "new file beside the original");
        var now = await System.IO.File.ReadAllBytesAsync(file);
        Assert.True(bytes.SequenceEqual(now) && bin.Recycled.Count == 0, "original untouched, nothing recycled");
        Assert.Sequence([before.Tracks[2].Name, before.Tracks[1].Name], (await inspector.InspectAsync(result.OutputPath!)).TracksOf(TrackType.Audio).Select(t => t.Name), "new order");

        var second = await processor.ProcessAsync(EditPlan.For(ManualTarget.Create(before, edits, title: "Iný")), Placement.Beside(file), Keep);
        Assert.Equal(Path.Combine(work.Path, "en-und-cz-forced (upravené) (2).mkv"), second.OutputPath, "never overwrites an earlier result");
    }

    [Test]
    public async Task ExternalSubtitlesAndNewTimes()
    {
        // owner, 30. 10. 2026: subtitles lying with the video are added; FPS, speed and delay change the times (mkvmerge --sync)
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var original = work.Copy("en-und-cz-forced.mkv");
        var file = await inspector.InspectAsync(original);
        var srt = Path.Combine(work.Path, "en-und-cz-forced.sk.srt");
        await Universum.Core.Subtitles.SrtWriter.SaveAsync(Universum.Core.Subtitles.SubtitleReader.Read("1\n00:00:00,500 --> 00:00:01,000\nĽudia sa vrátia.\n"), srt);
        var added = new MediaTrack
        {
            Id = 1000, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8", IsTextSubtitles = true, Language = LanguageTag.FromCode("sk"), ExternalFile = srt, StatisticsFrames = 1,
        };
        var withFile = file with { Tracks = [.. file.Tracks, added] };
        var czech = file.TracksOf(TrackType.Subtitles).First(t => t.Name == "Czech full");
        var rules = UniversumRules.Apply(withFile, decisions: CzechDub);
        var target = rules with
        {
            Tracks = rules.Tracks.Select(t => t.TrackId == czech.Id ? t with { Timing = Universum.Core.Subtitles.SubtitleTiming.Delay(250) } : t).ToList(),
        };
        var plan = EditPlan.For(target);
        Assert.True(plan.Method == WriteMethod.Remux && plan.Changes.Any(c => c.Kind == ChangeKind.Added) && plan.Changes.Any(c => c.Kind == ChangeKind.Timing),
            "a new file with the added subtitles and new times: " + string.Join(", ", plan.Changes.Select(c => c.Kind)));

        var result = await processor.ProcessAsync(plan, work.Movie(plan), Keep);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        var written = await inspector.InspectAsync(result.OutputPath!);
        Assert.True(written.TracksOf(TrackType.Subtitles).Count(t => t.Language.Primary == "sk") == 1, "the Slovak subtitle file is in the result");
        var before = await MatroskaCues.ReadAsync(original);
        var after = await MatroskaCues.ReadAsync(result.OutputPath!);
        var shifted = before[czech.Number!.Value].Select(t => t + TimeSpan.FromMilliseconds(250)).ToList();
        Assert.True(after.Values.Any(times => times.SequenceEqual(shifted)), "Czech full 250 ms later: " + string.Join(" | ", after.Select(a => $"{a.Key}: {string.Join(",", a.Value)}")));
        Assert.True(after.Values.Any(times => times.SequenceEqual([TimeSpan.FromMilliseconds(500)])), "the added subtitle at 0,5 s");

        // the same again: already in the library; another subtitle file: another result
        Assert.Equal(ProcessingStatus.AlreadyInLibrary, (await processor.ProcessAsync(plan, work.Movie(plan), Keep)).Status, "the same result is not written twice");
        await File.AppendAllTextAsync(srt, "2\r\n00:00:01,200 --> 00:00:01,800\r\nĎalší.\r\n\r\n");
        Assert.True((await processor.ProcessAsync(plan, work.Movie(plan), Keep)).Status is ProcessingStatus.Done, "other subtitles in the file: written again");
        File.Delete(srt);
        var missing = await processor.ProcessAsync(plan, work.Movie(plan), Keep);
        Assert.True(missing.Status == ProcessingStatus.Failed && missing.Messages.Any(m => m.Contains("no longer exists", StringComparison.Ordinal)), "a removed subtitle file: nothing written");
    }

    [Test]
    public async Task ExternalSubtitlesWithNewTimesAndRules()
    {
        // the Titulky page: two external files (one with FPS and delay), all the rows' languages, kept, rules
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var original = work.Copy("en-und-cz-forced.mkv");
        var file = await inspector.InspectAsync(original);
        var sk = Path.Combine(work.Path, "a.srt");
        var en = Path.Combine(work.Path, "b.srt");
        await Universum.Core.Subtitles.SrtWriter.SaveAsync(Universum.Core.Subtitles.SubtitleReader.Read("1\n00:00:00,500 --> 00:00:01,200\nĽudia sa vrátia domov.\n"), sk);
        await Universum.Core.Subtitles.SrtWriter.SaveAsync(Universum.Core.Subtitles.SubtitleReader.Read("1\n00:00:00,300 --> 00:00:01,000\nPeople will come home.\n"), en);
        MediaTrack External(int id, string path, string language) => new()
        {
            Id = id, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8", Codec = "SubRip/SRT", IsTextSubtitles = true, StatisticsFrames = 1, ExternalFile = path,
            Language = LanguageTag.Undetermined, Flags = new TrackFlags(Default: false),
        };
        var withFiles = file with { Tracks = [.. file.Tracks, External(1000, sk, "sk"), External(1001, en, "en")] };
        var rows = withFiles.TracksOf(TrackType.Subtitles).ToList();
        var languages = rows.ToDictionary(t => t.Id, t => t.Id == 1000 ? LanguageTag.FromCode("sk") : t.Id == 1001 ? LanguageTag.FromCode("en") : t.Language.IsUndetermined ? LanguageTag.FromCode("cs") : t.Language);
        languages[2] = LanguageTag.FromCode("cs");
        var decisions = new TrackDecisions { Languages = languages, Kept = rows.Select(t => t.Id).ToHashSet() };
        var rules = UniversumRules.Apply(withFiles, RuleProfile.Default, decisions);
        var timing = Universum.Core.Subtitles.SubtitleTiming.Fps(Universum.Core.Subtitles.FrameRate.All[2], Universum.Core.Subtitles.FrameRate.All[0])
            .Then(Universum.Core.Subtitles.SubtitleTiming.Delay(250));
        var target = rules with { Tracks = rules.Tracks.Select(t => t.TrackId == 1000 ? t with { Timing = timing } : t).ToList() };
        var plan = EditPlan.For(target);
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Keep);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages) + " | " + string.Join(" ", MkvToolNixArguments.Remux(target, original, "out.mkv")));
        var after = await MatroskaCues.ReadAsync(result.OutputPath!);
        Assert.True(after.Values.Any(times => times.SequenceEqual([TimeSpan.FromMilliseconds(771)])), "the Slovak one 25 → 23,976 and +250 ms: " + string.Join(" | ", after.Select(a => $"{a.Key}: {string.Join(",", a.Value)}")));
    }

    [Test]
    public async Task AddedSoundsWithRules()
    {
        // Úprava audiostopy: a sound FFmpeg wrote (with its tags) and the second sound of another film, moved; the rules
        // remove every tag, the check of the result passes
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator)
            .Select(d => System.IO.Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            Assert.Skip("FFmpeg not on PATH");
        }

        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var original = work.Copy("en-und-cz-forced.mkv");
        var other = work.Copy("en-und-cz-forced.mkv", "iny film.mkv");
        var dub = System.IO.Path.Combine(work.Path, "dabing.mka");
        var made = await new ProcessRunner().RunAsync(new ProcessRequest(ffmpeg!, ["-nostdin", "-v", "error", "-i", original, "-map", "0:a:0", "-c:a", "aac", "-b:a", "96k", dub]));
        Assert.True(made.ExitCode == 0, made.StandardError);
        var file = await inspector.InspectAsync(original);
        var written = (await inspector.InspectAsync(dub)).TracksOf(TrackType.Audio).Single();
        var second = (await inspector.InspectAsync(other)).TracksOf(TrackType.Audio).ElementAt(1);
        var withSounds = file with
        {
            Tracks = [.. file.Tracks, written with { Id = 1000, ExternalFile = dub, ExternalTrack = written.Id, Flags = written.Flags with { Default = false } },
                second with { Id = 1001, ExternalFile = other, ExternalTrack = second.Id, Flags = second.Flags with { Default = false } }],
        };
        var languages = withSounds.Tracks.Where(t => t.Type != TrackType.Video).ToDictionary(t => t.Id, t => t.Language.IsUndetermined ? LanguageTag.FromCode("cs") : t.Language);
        var target = UniversumRules.Apply(withSounds, RuleProfile.Default, new TrackDecisions
        {
            Languages = languages,
            Kept = withSounds.TracksOf(TrackType.Subtitles).Select(t => t.Id).ToHashSet(),
            Names = new Dictionary<int, string> { [1000] = "Vymyslený dabing" },
            Timings = new Dictionary<int, Universum.Core.Subtitles.SubtitleTiming> { [1001] = Universum.Core.Subtitles.SubtitleTiming.Delay(-300) },
        });
        Assert.True(target.RemoveTags && target.For(1001).Timing is { DelayMilliseconds: -300 }, "the rules remove tags; the shift of a sound is kept");
        var plan = EditPlan.For(target);
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Keep);
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages) + " | " + string.Join(" ", MkvToolNixArguments.Remux(target, original, "out.mkv")));
        var after = await inspector.InspectAsync(result.OutputPath!);
        Assert.True(after.TracksOf(TrackType.Audio).Count() == 4 && !after.HasTags && after.TracksOf(TrackType.Audio).Any(t => t is { CodecId: "A_AAC", Name: "Vymyslený dabing" }),
            "four sounds, the dub with its name, no tags – " + string.Join(" | ", after.TracksOf(TrackType.Audio).Select(t => $"{t.CodecId} {t.Name} {t.TagEntries}")));
    }

    [Test]
    public async Task SoundWrittenAnewByTheProcessor()
    {
        // a sound of the video written anew (PAL → film, stereo AAC) in place of its track: FFmpeg right before mkvmerge
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator)
            .Select(d => System.IO.Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            Assert.Skip("FFmpeg not on PATH");
        }

        using var work = new WorkFolder();
        var installation = await TestData.RequireMkvToolNixAsync();
        var processor = new FileProcessor(installation, new ProcessRunner(), new FolderRecycleBin(System.IO.Path.Combine(work.Path, "bin")), System.IO.Path.Combine(work.Path, "journal"),
            new LibraryIndex(System.IO.Path.Combine(work.Path, "library-index.json"))) { FindFfmpeg = _ => Task.FromResult(ffmpeg!) };
        var inspector = new MkvMergeInspector(installation, new ProcessRunner());
        var original = work.Copy("en-und-cz-forced.mkv");
        var file = await inspector.InspectAsync(original);
        var english = file.TracksOf(TrackType.Audio).First();
        var recipe = new Universum.Core.Audio.AudioRecipe(Universum.Core.Subtitles.SubtitleTiming.Fps(Universum.Core.Subtitles.FrameRate.All[2], Universum.Core.Subtitles.FrameRate.All[0])
            with { DelayMilliseconds = 100 }, Codec: Universum.Core.Audio.AudioCodec.Aac, Stereo: true);
        var anew = new Universum.Core.Audio.SoundEncoding(original, 0, english, recipe).ToTrack(1000);
        var withSound = file with { Tracks = [.. file.Tracks, anew] };
        var languages = withSound.Tracks.Where(t => t.Type != TrackType.Video).ToDictionary(t => t.Id, t => t.Language.IsUndetermined ? LanguageTag.FromCode("cs") : t.Language);
        var target = UniversumRules.Apply(withSound, RuleProfile.Default, new TrackDecisions
        {
            Languages = languages,
            Kept = withSound.TracksOf(TrackType.Subtitles).Select(t => t.Id).ToHashSet(),
            Removed = new HashSet<int> { english.Id },
            Timings = new Dictionary<int, Universum.Core.Subtitles.SubtitleTiming> { [1000] = recipe.Shift },
        });
        var plan = EditPlan.For(target);
        var shares = new List<double>();
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Keep, new Progress<double>(shares.Add));
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        var after = await inspector.InspectAsync(result.OutputPath!);
        var audio = after.TracksOf(TrackType.Audio).ToList();
        Assert.True(audio.Count == 2 && audio.Any(t => t is { CodecId: "A_AAC", AudioChannels: 2 }) && !after.HasTags,
            "the English sound replaced by stereo AAC – " + string.Join(" | ", audio.Select(t => $"{t.CodecId} {t.AudioChannels} {t.Language.Primary}")));
        Assert.True(File.Exists(original), "the original stays");

        // the same again: the same result is known without reading the video whole
        Assert.Equal(ProcessingStatus.AlreadyInLibrary, (await processor.ProcessAsync(plan, work.Movie(plan), Keep)).Status, "already in the library");
        var none = new FileProcessor(installation, new ProcessRunner(), new FolderRecycleBin(System.IO.Path.Combine(work.Path, "bin")), System.IO.Path.Combine(work.Path, "journal2"));
        var refused = await none.ProcessAsync(plan, new LibraryLayout(System.IO.Path.Combine(work.Path, "INY")).Place(target, new Universum.Core.Library.TitleInfo(Universum.Core.Library.MediaKind.Movie, "Iný", 2026), null, RuleProfile.Default), Keep);
        Assert.True(refused.Status == ProcessingStatus.Failed && refused.Messages.Any(m => m.Contains("FFmpeg", StringComparison.Ordinal)), "without FFmpeg: said, nothing written");
    }

    private static async Task<(FileProcessor Processor, MkvMergeInspector Inspector)> CreateAsync(WorkFolder work, IRecycleBin? bin = null, IProcessRunner? runner = null)
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        runner ??= new ProcessRunner();
        var index = new LibraryIndex(Path.Combine(work.Path, "library-index.json"));
        return (new FileProcessor(installation, runner, bin ?? new FolderRecycleBin(Path.Combine(work.Path, "bin")), Path.Combine(work.Path, "journal"), index) { DeleteOriginalsAllowed = true },
            new MkvMergeInspector(installation, new ProcessRunner()));
    }

    private sealed class WorkFolder : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("universum-processor-");

        public string Path => _directory.FullName;

        public LibraryLayout Layout => new(System.IO.Path.Combine(Path, "OUTPUT"));

        public Placement Movie(EditPlan plan) =>
            Layout.Place(plan.Target, new TitleInfo(MediaKind.Movie, "Vymyslený film", 2026), null, RuleProfile.Default);

        public string InMovies(params string[] parts) => System.IO.Path.Combine([Layout.Movies, .. parts]);

        public string Copy(string testFile, string? name = null)
        {
            var target = System.IO.Path.Combine(Path, name ?? testFile);
            System.IO.File.Copy(TestData.Media(testFile), target);
            return target;
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }

    /// <summary>Test stand-in for the Windows Recycle Bin: moves files into a folder.</summary>
    private sealed class FolderRecycleBin(string folder) : IRecycleBin
    {
        public bool Available { get; init; } = true;

        public List<string> Recycled { get; } = [];

        public string? CannotRecycle(string path) => Available ? null : "no recycle bin";

        public void MoveToRecycleBin(string path)
        {
            // as the Recycle Bin of Windows: a file of the same name there before is kept
            Directory.CreateDirectory(folder);
            var target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(path));
            System.IO.File.Move(path, System.IO.File.Exists(target) ? System.IO.Path.Combine(folder, $"{Recycled.Count + 1} {System.IO.Path.GetFileName(path)}") : target);
            Recycled.Add(path);
        }
    }

    /// <summary>Runs the tools, but lets the next mkvpropedit edit (not an undo) fail once.</summary>
    private sealed class FailingRunner(IProcessRunner inner) : IProcessRunner
    {
        public bool FailNextEdit { get; set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (FailNextEdit && Path.GetFileNameWithoutExtension(request.FileName).Equals("mkvpropedit", StringComparison.OrdinalIgnoreCase))
            {
                FailNextEdit = false;
                return Task.FromResult(new ProcessResult(2, "", "Error: simulated failure", TimeSpan.Zero));
            }

            return inner.RunAsync(request, cancellationToken);
        }
    }

    /// <summary>Progress that reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
