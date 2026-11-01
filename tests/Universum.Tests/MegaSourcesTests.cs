using Universum.Core.Execution;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.Mega;
using Universum.Core.MkvToolNix;
using Universum.Core.Muxing;
using Universum.Core.Processes;
using Universum.Core.Rules;

namespace Universum.Tests;

/// <summary>
/// MEGA spracovanie takes files of Single spracovanie and results of Zlučovanie too (owner, 1. 11. 2026); Zlučovanie writes
/// previews into its own folder. Invented names only.
/// </summary>
public sealed class MegaSourcesTests
{
    private static readonly TrackDecisions CzechDub = new()
    {
        Languages = new Dictionary<int, LanguageTag> { [2] = LanguageTag.FromCode("cs"), [3] = LanguageTag.FromCode("cs") },
    };

    [Test]
    public void PreviewOfPartsOfAFilm()
    {
        Assert.Equal("parts:00:00:00.000-00:02:00.000,+00:50:00.000-00:52:00.000,+01:30:00.000-01:32:00.000", MuxPreview.Parts(TimeSpan.FromMinutes(100)),
            "two minutes at the start, in the middle and at 90 %");
        Assert.Equal("parts:00:00:00.000-00:02:00.000,+00:05:00.000-00:07:00.000,+00:08:00.000-00:10:00.000", MuxPreview.Parts(TimeSpan.FromMinutes(10)),
            "the last piece ends with the film");
        Assert.True(MuxPreview.Parts(TimeSpan.FromMinutes(8)) is null && MuxPreview.Parts(null) is null, "a short film (or one of unknown length) whole");
        var name = MuxPreview.FileName("Vymyslený: film?", 2, new DateTime(2026, 11, 1, 14, 5, 9));
        Assert.Equal("Vymyslený- film- – náhľad 2 – 1. 11. 14-05-09.mkv", name, "a file name Windows takes");
        var spec = new MuxSpec([], [], "x.mkv") { Placement = new Placement("D:\\OUTPUT", "Vymyslený", "Vymyslený", new SizeClasses()) };
        var preview = MuxPreview.Of(spec, "C:\\náhľady\\" + name, PreviewRange.Whole);
        Assert.True(preview.Placement is null && preview.OutputPath.EndsWith(name, StringComparison.Ordinal) && preview.PreviewParts is null, "never into the library");
    }

    [Test]
    public async Task SingleAndMuxResultsInAMegaBatch()
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var work = Directory.CreateTempSubdirectory("universum-mega-sources-");
        try
        {
            var runner = new ProcessRunner();
            var inspector = new MkvMergeInspector(installation, runner);
            var library = new LibraryLayout(Path.Combine(work.FullName, "OUTPUT"));

            // Single spracovanie: a film with its decisions, sent to a batch that deletes originals – it keeps its own
            var single = Path.Combine(work.FullName, "Vymyslený film.mkv");
            File.Copy(TestData.Media("en-und-cz-forced.mkv"), single);
            var film = await inspector.InspectAsync(single);
            var target = UniversumRules.Apply(film, RuleProfile.Default, CzechDub);
            var title = new TitleInfo(MediaKind.Movie, "Vymyslený film", 2026);
            var singleFile = MegaFile.FromSingle(film, CzechDub, title, null, null, false, RuleProfile.Default, library.Place(target, title, null, RuleProfile.Default).Prefix);

            // Zlučovanie: a video and invented subtitles, twice – the second one's subtitles change before the run
            var source = Path.Combine(work.FullName, "Zdroj.mkv");
            File.Copy(TestData.Media("cz-only.mkv"), source);
            var subtitles = Path.Combine(work.FullName, "Vymyslené titulky.srt");
            File.WriteAllText(subtitles, "1\r\n00:00:01,000 --> 00:00:02,000\r\nŽltý kôň úpel.\r\n\r\n");
            var other = Path.Combine(work.FullName, "Iné titulky.srt");
            File.WriteAllText(other, "1\r\n00:00:01,000 --> 00:00:02,000\r\nIná veta.\r\n\r\n");
            var video = await inspector.InspectAsync(source);
            MuxSpec Spec(MediaFile srt, string output) => new(
                [new MuxSource(video), new MuxSource(srt)],
                [.. video.Tracks.Select(t => new MuxTrack(0, t.Id, null, t.Language, t.Flags)), new MuxTrack(1, 0, "Slovenské", LanguageTag.FromCode("sk"), new TrackFlags())],
                output);
            var merged = Path.Combine(work.FullName, "OUTPUT", "Zlúčený film.mkv");
            var mux = MegaFile.FromMux(Spec(await inspector.InspectAsync(subtitles), merged) with { PreviewParts = "parts:00:00:00-00:00:01" },
                new TitleInfo(MediaKind.Movie, "Zlúčený film", 2026), null, merged);
            var changedMux = MegaFile.FromMux(Spec(await inspector.InspectAsync(other), Path.Combine(work.FullName, "OUTPUT", "Iný.mkv")),
                new TitleInfo(MediaKind.Movie, "Iný", 2026), null, "Iný.mkv");
            Assert.True(mux.Mux!.Spec.PreviewParts is null && mux.Mux.Inputs.Count == 2 && mux.KeepOriginal && mux.Source == MegaSource.Mux, "the whole result, both files it reads");

            var file = Path.Combine(work.FullName, "mega.json");
            var batch = new MegaStore(file).Add("Vymyslená dávka", library.Root, RuleProfile.Default, OriginalsMode.DeleteAutomatically, [singleFile, mux, changedMux])!;
            Assert.True(batch.Files.Count == 3, "all three in the batch");
            var store = new MegaStore(file);
            Assert.True(store.Reserved().Count == 1 && store.Reserved().ContainsKey(Path.GetFullPath(single)), "a source of Zlučovanie is not reserved (it may give several results)");
            var stored = store.Batches.Single();
            var storedMux = stored.Files.Single(f => f.Mux is not null && f.Title.Name == "Zlúčený film");
            Assert.True(storedMux.Mux!.Spec.Tracks.Count == video.Tracks.Count + 1 && storedMux.Mux.Spec.Sources[0].File.Tracks.Count == video.Tracks.Count &&
                        storedMux.Mux.Spec.Tracks[^1].Name == "Slovenské" && stored.Files.Single(f => f.Source == MegaSource.Single).KeepOriginal,
                "kept and read again: the recipe with its files, the file of Single keeping its original");
            Assert.True(store.Put(stored, [MegaFile.FromMux(Spec(await inspector.InspectAsync(subtitles), merged), title, null, "")]) == (1, 0), "a result of Zlučovanie is always added");
            stored.Files.RemoveAt(stored.Files.Count - 1);

            File.AppendAllText(other, "2\r\n00:00:03,000 --> 00:00:04,000\r\nZmena.\r\n\r\n");
            var bin = new Bin(Path.Combine(work.FullName, "bin"));
            var deleting = new FileProcessor(installation, runner, bin, Path.Combine(work.FullName, "_journal")) { DeleteOriginalsAllowed = true };
            var keeping = new FileProcessor(installation, runner, bin, Path.Combine(work.FullName, "_journal"));
            var multiplexer = new Multiplexer(installation, runner);
            var job = new MegaJob(stored, deleting, (path, token) => inspector.InspectAsync(path, token), keeping: keeping, mux: multiplexer.MuxAsync);
            var queue = new Universum.Core.Jobs.JobQueue();
            queue.Add(job);
            await queue.Start();

            string Messages() => string.Join(" | ", job.Messages.Select(m => m.Text));
            var states = stored.Files.ToDictionary(f => f.Title.Name, f => f);
            Assert.True(states["Vymyslený film"] is { State: MegaFileState.Done, OriginalRemoved: false } && File.Exists(single) && File.Exists(states["Vymyslený film"].ResultPath!) && bin.Recycled.Count == 0,
                "Single: written into the library, the original kept although the batch deletes – " + Messages());
            Assert.True(states["Zlúčený film"] is { State: MegaFileState.Done } && File.Exists(merged) && (await inspector.InspectAsync(merged)).Tracks.Count == video.Tracks.Count + 1,
                "Zlučovanie: the whole result written from its recipe – " + Messages());
            Assert.True(states["Iný"].State == MegaFileState.Skipped && states["Iný"].Note.Contains("Iné titulky.srt", StringComparison.Ordinal) && !File.Exists(Path.Combine(work.FullName, "OUTPUT", "Iný.mkv")),
                "a result whose file changed is not written – " + states["Iný"].Note);
            Assert.Equal("2 z 3 súborov v knižnici, 1 preskočených pri kontrole.", job.Outcome, "outcome");
            Assert.True(job.LoggedFiles.Count == 3 && job.LoggedFiles.Any(l => l.Headline == "Hotovo – nový súbor"), "all three in the log of processing");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    [Test]
    public async Task PreviewOfPartsIsShort()
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var work = Directory.CreateTempSubdirectory("universum-preview-");
        try
        {
            var inspector = new MkvMergeInspector(installation, new ProcessRunner());
            var video = await inspector.InspectAsync(TestData.Media("en-und-cz-forced.mkv"));
            var spec = new MuxSpec([new MuxSource(video)], [.. video.Tracks.Select(t => new MuxTrack(0, t.Id, t.Name, t.Language, t.Flags))], Path.Combine(work.FullName, "x.mkv"));
            var output = Path.Combine(work.FullName, "náhľady", MuxPreview.FileName("Vymyslený film", 1, DateTime.Now));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var preview = MuxPreview.Of(spec, output, PreviewRange.Whole) with { PreviewParts = "parts:00:00:00-00:00:01,+00:00:02-00:00:03" };
            Assert.True(MuxArguments.Build(preview, output).SkipWhile(a => a != "--split").Skip(1).FirstOrDefault() == preview.PreviewParts, "the parts given to mkvmerge");
            var result = await new Multiplexer(installation, new ProcessRunner()).MuxAsync(preview);
            Assert.Equal(MuxStatus.Done, result.Status, string.Join(" | ", result.Messages));
            var written = await inspector.InspectAsync(result.OutputPath!);
            // the test film has one key frame: mkvmerge keeps a part to the next key frame, so the length is not compared here
            Assert.True(result.OutputPath == output && written.Tracks.Count == video.Tracks.Count && written.Duration <= video.Duration,
                $"one file with every track – {written.Duration} of {video.Duration}");
            Assert.True(MuxPreview.Old(Path.GetDirectoryName(output)!, TimeSpan.FromDays(1), DateTime.Now).Count == 0 &&
                        MuxPreview.Old(Path.GetDirectoryName(output)!, TimeSpan.FromDays(1), DateTime.Now.AddDays(2)).Single() == output, "old previews found to be cleared");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private sealed class Bin(string folder) : IRecycleBin
    {
        public List<string> Recycled { get; } = [];

        public string? CannotRecycle(string path) => null;

        public void MoveToRecycleBin(string path)
        {
            Directory.CreateDirectory(folder);
            File.Move(path, Path.Combine(folder, Recycled.Count + "-" + Path.GetFileName(path)));
            Recycled.Add(path);
        }
    }
}
