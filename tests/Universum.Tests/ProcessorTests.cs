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
        Assert.Equal("Invented.Film.2026.1080p", written.Title, "title untouched");

        // idempotent: the result already matches the rules and is in its place
        var again = EditPlan.For(UniversumRules.Apply(written));
        Assert.Equal(WriteMethod.None, again.Method, "second run changes nothing");
        Assert.Equal(ProcessingStatus.Unchanged, (await processor.ProcessAsync(again, work.Movie(again), Keep)).Status, "already in its place");
        Assert.True(!Directory.EnumerateFiles(work.Path, "*", SearchOption.AllDirectories).Any(FileProcessor.IsTemporaryFile), "no temporary file left");
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
    public async Task NoRecycleBinMeansNothingIsWritten()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work, new FolderRecycleBin(Path.Combine(work.Path, "bin")) { Available = false });
        var original = work.Copy("sk-cz-mixed.mkv");
        var plan = EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original)));
        var result = await processor.ProcessAsync(plan, work.Movie(plan), Delete);
        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.Equal(1, Directory.EnumerateFiles(work.Path, "*", SearchOption.AllDirectories).Count(), "only the untouched original");
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

        public string Copy(string testFile)
        {
            var target = System.IO.Path.Combine(Path, testFile);
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
            Directory.CreateDirectory(folder);
            System.IO.File.Move(path, System.IO.Path.Combine(folder, System.IO.Path.GetFileName(path)));
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
