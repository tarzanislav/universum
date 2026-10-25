using Universum.Core.Execution;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;

namespace Universum.Tests;

/// <summary>End-to-end: rules → plan → real mkvmerge/mkvpropedit → verification, on copies of the test files.</summary>
public sealed class ProcessorTests
{
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
        var result = await processor.ProcessAsync(plan, new OutputOptions(OriginalHandling.Keep), new SyncProgress(progress.Add));

        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.Equal(Path.Combine(work.Path, "en-und-cz-forced [UNIVERSUM].mkv"), result.OutputPath);
        var after = await System.IO.File.ReadAllBytesAsync(original);
        Assert.True(before.AsSpan().SequenceEqual(after), "original untouched");
        Assert.True(progress.Count > 0 && progress[^1] >= 0.99, "progress reported");

        var written = await inspector.InspectAsync(result.OutputPath!);
        Assert.Sequence(["video", "audio cs", "audio en", "subtitles cs", "subtitles cs", "subtitles en"],
            written.Tracks.Select(t => t.Type == TrackType.Video ? "video" : $"{t.Type.ToString().ToLowerInvariant()} {t.Language.Primary}"), "result tracks");
        Assert.True(!written.HasTags, "no tags, no statistics tags");
        Assert.Equal(2, written.ChapterEntries, "chapters kept");
        Assert.Equal("Invented.Film.2026.1080p", written.Title, "title untouched");

        // idempotent: the result already matches the rules
        Assert.Equal(WriteMethod.None, EditPlan.For(UniversumRules.Apply(written)).Method, "second run changes nothing");
        Assert.True(!Directory.EnumerateFiles(work.Path).Any(FileProcessor.IsTemporaryFile), "no temporary file left");
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

        var result = await processor.ProcessAsync(EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original))), new OutputOptions(OriginalHandling.Delete));

        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.Equal(original, result.OutputPath, "result replaces the original");
        Assert.Equal(1, bin.Recycled.Count, "original in the Recycle Bin");
        Assert.Equal(time.AddSeconds(2), System.IO.File.GetLastWriteTimeUtc(original), "modification time kept (+2 s so changes are noticed)");
        var audio = (await inspector.InspectAsync(original)).TracksOf(TrackType.Audio).Select(t => t.Language.Primary);
        Assert.Sequence(["cs", "sk", "en"], audio, "audio order");
    }

    [Test]
    public async Task HeaderEditInPlaceAndUndo()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var file = work.Copy("cz-only.mkv");
        var tags = Path.Combine(work.Path, "tags.xml");
        await System.IO.File.WriteAllTextAsync(tags, "<?xml version=\"1.0\"?><Tags><Tag><Targets/><Simple><Name>ENCODER</Name><String>X</String></Simple></Tag></Tags>");
        var installation = await TestData.RequireMkvToolNixAsync();
        await new ProcessRunner().RunAsync(new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvPropEdit),
            [file, "--tags", "global:" + tags, "--edit", "track:a1", "--set", "name=Dabing CZ", "--edit", "track:s1", "--set", "flag-default=1"]));
        var before = await inspector.InspectAsync(file);

        var plan = EditPlan.For(UniversumRules.Apply(before));
        Assert.Equal(WriteMethod.HeaderEdit, plan.Method);
        var result = await processor.ProcessAsync(plan, new OutputOptions(OriginalHandling.Delete));
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.True(result.UndoRecordPath is not null, "undo record");

        var after = await inspector.InspectAsync(file);
        Assert.True(!after.HasTags && after.TracksOf(TrackType.Audio).Single().Name is null, "edited");
        Assert.True(!after.TracksOf(TrackType.Subtitles).Single().Flags.Default, "subtitles not default");

        Assert.Equal(0, (await processor.UndoAsync(result.UndoRecordPath!)).Count, "undo succeeded");
        var restored = await inspector.InspectAsync(file);
        Assert.Equal("Dabing CZ", restored.TracksOf(TrackType.Audio).Single().Name, "name restored");
        Assert.True(restored.TracksOf(TrackType.Subtitles).Single().Flags.Default, "flag restored");
        Assert.Equal(1, restored.GlobalTagEntries, "tags restored");
    }

    [Test]
    public async Task NoRecycleBinMeansNothingIsWritten()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work, new FolderRecycleBin(Path.Combine(work.Path, "bin")) { Available = false });
        var original = work.Copy("sk-cz-mixed.mkv");
        var result = await processor.ProcessAsync(EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original))), new OutputOptions(OriginalHandling.Delete));
        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.Equal(1, Directory.EnumerateFiles(work.Path).Count(), "only the untouched original");
    }

    [Test]
    public async Task BlockedFileIsNotWritten()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var original = work.Copy("en-und-cz-forced.mkv");
        var result = await processor.ProcessAsync(EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original))), new OutputOptions(OriginalHandling.Delete));
        Assert.Equal(ProcessingStatus.NeedsReview, result.Status);
    }

    [Test]
    public async Task TargetFolderKeepsTheOriginal()
    {
        using var work = new WorkFolder();
        var (processor, inspector) = await CreateAsync(work);
        var original = work.Copy("en-only-cz-subs.mkv");
        var output = Path.Combine(work.Path, "out");
        var result = await processor.ProcessAsync(EditPlan.For(UniversumRules.Apply(await inspector.InspectAsync(original))), new OutputOptions(OriginalHandling.Keep, output));
        Assert.Equal(ProcessingStatus.Done, result.Status, string.Join("; ", result.Messages));
        Assert.Equal(Path.Combine(output, "en-only-cz-subs.mkv"), result.OutputPath);
        Assert.True(System.IO.File.Exists(original), "original kept");
        var subtitles = (await inspector.InspectAsync(result.OutputPath!)).TracksOf(TrackType.Subtitles).ToList();
        Assert.Sequence([(false, true), (true, false), (false, false)], subtitles.Select(s => (s.Flags.Default, s.Flags.Forced)), "forced CZ, full CZ default, EN");
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
