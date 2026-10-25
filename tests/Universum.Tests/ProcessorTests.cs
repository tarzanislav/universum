using Universum.Core.Execution;
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

    private static async Task<(FileProcessor Processor, MkvMergeInspector Inspector)> CreateAsync(WorkFolder work, IRecycleBin? bin = null)
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var runner = new ProcessRunner();
        return (new FileProcessor(installation, runner, bin ?? new FolderRecycleBin(Path.Combine(work.Path, "bin")), Path.Combine(work.Path, "journal")),
            new MkvMergeInspector(installation, runner));
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

        public bool CanRecycle(string path) => Available;

        public void MoveToRecycleBin(string path)
        {
            Directory.CreateDirectory(folder);
            System.IO.File.Move(path, System.IO.Path.Combine(folder, System.IO.Path.GetFileName(path)));
            Recycled.Add(path);
        }
    }

    /// <summary>Progress that reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
