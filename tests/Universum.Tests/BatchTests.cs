using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;
using Universum.Core.Rules;
using Universum.Core.Templates;
using Universum.Core.Text;

namespace Universum.Tests;

public sealed class BatchTests
{
    private static readonly BatchTemplate FilmCzEn = new()
    {
        Name = "Film CZ + EN",
        Slots =
        [
            new TemplateSlot(TrackType.Audio, "cs", TrackRole.Main, Required: true),
            new TemplateSlot(TrackType.Audio, "en", TrackRole.Main, Required: false),
            new TemplateSlot(TrackType.Subtitles, "cs", TrackRole.Forced, Required: false),
            new TemplateSlot(TrackType.Subtitles, "cs", TrackRole.Main, Required: false),
        ],
    };

    [Test]
    public async Task AnalysisGroupsFilesByStructure()
    {
        using var work = await SeriesFolderAsync();
        var analysis = await AnalyzeAsync(work.Path);

        Assert.Equal(5, analysis.FileCount, "files");
        Assert.Equal(3, analysis.Groups.Count, "groups: three episodes, cz-only, en-only");
        var series = analysis.Groups[0];
        Assert.Equal("A", series.Label);
        Assert.Sequence(["Seriál E2.mkv", "Seriál E3.mkv", "Seriál E10.mkv"], series.Files.Select(f => Path.GetFileName(f.Path)), "natural order");
        Assert.True(series.Signature.Contains("A: en, und", StringComparison.Ordinal), series.Signature);
        Assert.Sequence(["Seriál E5.universum-tmp.mkv", "Seriál E6.0a1b2c3d.universum-tmp"], analysis.LeftoverTemporaryFiles.Select(Path.GetFileName)!, "left-over temporary files found, not analysed");
    }

    [Test]
    public async Task AnalysisShowsFilesAsTheyAreRead()
    {
        using var work = await SeriesFolderAsync();
        var found = new List<string>();
        var inspector = new MkvMergeInspector(await TestData.RequireMkvToolNixAsync(), new ProcessRunner());
        var analysis = await BatchAnalyzer.AnalyzeAsync(work.Path, recursive: false, inspector, found: new SyncProgress<MediaFile>(f => { lock (found) { found.Add(f.Path); } }));
        Assert.Equal(analysis.FileCount, found.Count, "every file read is reported at once");
        Assert.Equal(null, BatchAnalyzer.WhyEmpty(work.Path, recursive: false));
    }

    [Test]
    public void EmptyFolderSaysWhy()
    {
        using var work = new Work();
        void Touch(params string[] parts)
        {
            var path = Path.Combine([work.Path, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }

        Assert.True(BatchAnalyzer.WhyEmpty(work.Path, true)!.Contains("žiadne video súbory", StringComparison.Ordinal), "an empty folder");
        Touch("Film A.wmv");
        Touch("Film B.wmv");
        Assert.True(BatchAnalyzer.WhyEmpty(work.Path, true) is { } unsupported && unsupported.Contains("nevie prečítať: 2 × WMV", StringComparison.Ordinal),
            "formats mkvmerge cannot read: " + BatchAnalyzer.WhyEmpty(work.Path, true));
        Touch("DVD", "VTS_01_1.VOB");
        Assert.True(BatchAnalyzer.WhyEmpty(work.Path, true)!.Contains("súbory DVD (VOB)", StringComparison.Ordinal), "DVD parts: " + BatchAnalyzer.WhyEmpty(work.Path, true));
        Touch("Kamera", "PRIVATE", "AVCHD", "BDMV", "STREAM", "00000.MTS");
        Assert.Sequence(["00000.MTS"], BatchAnalyzer.FindFiles(work.Path, true).Files.Select(Path.GetFileName)!, "camcorder clips are read");
        File.Delete(Path.Combine(work.Path, "Kamera", "PRIVATE", "AVCHD", "BDMV", "STREAM", "00000.MTS"));
        Touch("Disk", "BDMV", "STREAM", "00001.m2ts");
        Assert.True(BatchAnalyzer.WhyEmpty(work.Path, true)!.Contains("kópia disku", StringComparison.Ordinal), "a disc structure is not split into pieces");
        Assert.Equal(0, BatchAnalyzer.FindFiles(work.Path, true).Files.Count, "nothing of the disc is analysed");
        Touch("Sezóna 1", "Epizóda 1.mp4");
        Assert.True(BatchAnalyzer.WhyEmpty(work.Path, false)!.Contains("zaškrtnite „aj podpriečinky“", StringComparison.Ordinal), "only in subfolders");
        Assert.Equal(null, BatchAnalyzer.WhyEmpty(work.Path, true));
        Assert.Sequence(["Epizóda 1.mp4"], BatchAnalyzer.FindFiles(work.Path, true).Files.Select(Path.GetFileName)!, "MP4 is analysed");
        var results = Path.Combine(work.Path, "Sezóna 1");
        Assert.True(BatchAnalyzer.WhyEmpty(work.Path, true, [results])!.Contains("knižnici s výsledkami", StringComparison.Ordinal), "all video among the results");
        Assert.True(BatchAnalyzer.WhyEmpty(results, true, [results])!.StartsWith("Tento priečinok je v knižnici s výsledkami", StringComparison.Ordinal), "the folder is among the results");
        Assert.Equal(null, BatchAnalyzer.WhyEmpty(work.Path, true, [Path.Combine(work.Path, "Sezóna")]));
        Assert.True(BatchAnalyzer.WhyEmpty(Path.Combine(work.Path, "nie je"), true)!.StartsWith("Priečinok neexistuje", StringComparison.Ordinal), "a missing folder");
    }

    [Test]
    public async Task OtherFormatsAreAnalysed()
    {
        using var work = new Work();
        foreach (var name in new[] { "en-cz.mp4", "sk-en.ts", "und.avi", "cz-only.mkv" })
        {
            File.Copy(TestData.Media(name), Path.Combine(work.Path, name));
        }

        File.WriteAllBytes(Path.Combine(work.Path, "poškodený.mp4"), [1, 2, 3]);
        var analysis = await AnalyzeAsync(work.Path);
        Assert.Equal(4, analysis.FileCount, "MKV, MP4, TS and AVI are read: " + string.Join("; ", analysis.Failed.Select(f => f.Path + " " + f.Error)));
        Assert.True(analysis.Failed.Count == 1 && analysis.Failed[0].Path.EndsWith("poškodený.mp4", StringComparison.Ordinal), "a broken file is named");
        var items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, RuleProfile.Default);
        Assert.True(items.Where(i => !i.File.IsMatroska).All(i => i.Status is BatchStatus.Remux or BatchStatus.NeedsReview),
            "other containers are always written as a new MKV: " + string.Join(", ", items.Select(i => $"{Path.GetFileName(i.File.Path)} {i.Status}")));
    }

    [Test]
    public async Task GroupDecisionResolvesUndeterminedTracks()
    {
        using var work = await SeriesFolderAsync();
        var analysis = await AnalyzeAsync(work.Path);
        var series = analysis.Groups[0];

        var items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly);
        var episode = items.First(i => i.Group == series);
        Assert.Equal(BatchStatus.NeedsReview, episode.Status, "forced subtitles without a language");
        Assert.Equal(EvidenceSource.TrackName, episode.Evidence[new SlotKey(TrackType.Audio, 1)].Source, "'CZ Dabing' resolved from the name");
        Assert.Equal(new SlotKey(TrackType.Subtitles, 0), episode.Reasons.Single().Slot, "only the forced subtitles are open");

        series.Decisions.Languages[new SlotKey(TrackType.Subtitles, 0)] = LanguageTag.FromCode("cs");
        items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly);
        Assert.True(items.Where(i => i.Group == series).All(i => i.Status == BatchStatus.Remux), "all three episodes ready");
        Assert.True(items.Single(i => i.File.Path.EndsWith("cz-only.mkv", StringComparison.Ordinal)).Status == BatchStatus.Unchanged, "clean file unchanged");
    }

    [Test]
    public async Task TemplateToleratesMissingOptionalTracks()
    {
        using var work = await SeriesFolderAsync();
        var analysis = await AnalyzeAsync(work.Path);
        analysis.Groups[0].Decisions.Languages[new SlotKey(TrackType.Subtitles, 0)] = LanguageTag.FromCode("cs");
        var library = Library(work.Path, analysis);
        var items = BatchPlanner.Plan(analysis, FilmCzEn, library: library);

        var czOnly = items.Single(i => i.File.Path.EndsWith("cz-only.mkv", StringComparison.Ordinal));
        Assert.True(czOnly.Match.IsAcceptable && czOnly.Match.MissingOptional.Count == 2, "no EN audio, no forced: still matches");
        Assert.Equal(BatchStatus.Place, czOnly.Status, "nothing to change, only into the library");

        var english = items.Single(i => i.File.Path.EndsWith("en-only.mkv", StringComparison.Ordinal));
        Assert.Equal(ReviewReasonKind.MissingRequiredSlot, english.Reasons.Single().Kind, "required Czech audio missing");
        english.Group.Decisions.ProcessAnyway.Add(english.File.Path);
        Assert.True(BatchPlanner.Plan(analysis, FilmCzEn, library: library).Single(i => i.File.Path == english.File.Path).NeedsWork, "processed anyway on request");
    }

    [Test]
    public async Task TemplateOrderAndRemovalOfUnlistedTracks()
    {
        using var work = new Work();
        File.Copy(TestData.Media("sk-cz-mixed.mkv"), Path.Combine(work.Path, "mix.mkv"));
        var analysis = await AnalyzeAsync(work.Path);
        var template = new BatchTemplate
        {
            Name = "CZ + EN",
            Slots = [new(TrackType.Audio, "en", TrackRole.Main, false), new(TrackType.Audio, "cs", TrackRole.Main, true), new(TrackType.Subtitles, "cs", TrackRole.Main, false)],
            Unlisted = UnlistedTracks.Remove,
        };
        var item = BatchPlanner.Plan(analysis, template, library: Library(work.Path, analysis)).Single();
        Assert.Sequence(
            ["video", "audio en", "audio cs", "subtitles cs"],
            item.Target.Tracks.Select(t => t.Type == TrackType.Video ? "video" : $"{t.Type.ToString().ToLowerInvariant()} {t.Language.Primary}"),
            "template order, Slovak tracks removed");
        Assert.Equal(2, item.Target.Removed.Count, "removed tracks");
        Assert.True(item.Target.For(item.Target.Tracks[1].TrackId).Flags.Default, "first audio by template is default");

        var results = await RunAsync(work.Path, [item]);
        Assert.Equal(ProcessingStatus.Done, results.Single().Status, string.Join("; ", results.Single().Messages));
        var written = await (await InspectorAsync()).InspectAsync(results.Single().OutputPath!);
        Assert.Equal(4, written.Tracks.Count, "written tracks");
    }

    [Test]
    public async Task SeriesIsGuessedFromTheNames()
    {
        using var work = await SeriesFolderAsync();
        var analysis = await AnalyzeAsync(work.Path);
        var job = LibraryJob.Guess(new LibraryLayout(Path.Combine(work.Path, "OUTPUT")), analysis);

        Assert.Equal(MediaKind.Series, job.Kind, "most files carry an episode number");
        Assert.Equal<TitleInfo?>(null, job.Series, "title only suggested");
        Assert.Equal("Seriál", job.SeriesSuggestion?.Name, "series title from the file names");
        var czOnly = analysis.Groups.SelectMany(g => g.Files).Single(f => f.Path.EndsWith("cz-only.mkv", StringComparison.Ordinal)).Path;
        Assert.Equal((new EpisodeNumber(1, 1), true), job.EpisodeFor(czOnly), "free number suggested in folder order");
        var episode = analysis.Groups[0].Files[0].Path;
        Assert.Equal((new EpisodeNumber(1, 2), false), job.EpisodeFor(episode), "number from the name");

        analysis.Groups[0].Decisions.Languages[new SlotKey(TrackType.Subtitles, 0)] = LanguageTag.FromCode("cs");
        var items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, library: job);
        Assert.True(items.All(i => i.Reasons.Any(r => r.Kind == ReviewReasonKind.MissingTitle)), "nothing without a confirmed title");

        job.ConfirmSuggestions();
        items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, library: job);
        Assert.True(items.All(i => i.Reasons.Single() is { Kind: ReviewReasonKind.MissingTitle, Detail: "chýba rok" }), "the folder is \"Name (Year)\": the year is required");

        job.Series = job.Series! with { Year = 2026 };
        items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, library: job);
        Assert.True(items.All(i => i.Reasons.Count == 0), string.Join("; ", items.SelectMany(i => i.Reasons).Select(r => r.Kind)));
    }

    [Test]
    public void SeriesFolderIsCheckedEpisodeByEpisode()
    {
        // one analysed folder = one series; season folders, specials, files without numbers, a name/folder conflict,
        // the same episode twice and a file of another series
        using var work = new Work();
        var root = Path.Combine(work.Path, "Vymyslený seriál (2024)");
        string P(params string[] parts)
        {
            var path = Path.GetFullPath(Path.Combine([root, .. parts]));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, []);
            }

            return path;
        }

        var analysis = Synthetic(root,
            P("Season 1", "Vymysleny.Serial.S01E01.mkv"),
            P("Season 1", "Vymysleny.Serial.S01E02-E03.mkv"),
            P("Season 1", "Vymysleny.Serial.S01E03.v2.mkv"),
            P("Season 2", "prvá.mkv"),
            P("Season 2", "druhá.mkv"),
            P("Season 2", "Vymysleny.Serial.S01E04.mkv"),
            P("Specials", "Vymysleny Serial E01.mkv"),
            P("Season 1", "Iny.Serial.S01E05.mkv"),
            P("Season 3", "07.mkv"),
            P("Season 2", "Extras", "Vymysleny Serial E09.mkv"));
        var output = Path.Combine(work.Path, "OUTPUT");
        var job = LibraryJob.Guess(new LibraryLayout(output), analysis);

        Assert.True(job is { Kind: MediaKind.Series, KindConfirmed: false }, "season folders: a series, to be confirmed");
        Assert.Equal("Vymyslený seriál (2024)", job.SeriesSuggestion?.FolderName, "the folder in library form wins over the file names");
        Assert.Equal((new EpisodeNumber(2, 1), true), job.EpisodeFor(P("Season 2", "druhá.mkv")), "folder order, season of the folder");
        Assert.Equal((new EpisodeNumber(2, 2), true), job.EpisodeFor(P("Season 2", "prvá.mkv")));
        Assert.Equal((new EpisodeNumber(1, 4), true), job.EpisodeFor(P("Season 2", "Vymysleny.Serial.S01E04.mkv")), "name and folder disagree: to confirm");
        Assert.Equal((new EpisodeNumber(0, 1), false), job.EpisodeFor(P("Specials", "Vymysleny Serial E01.mkv")), "specials: season 0");
        Assert.Equal((new EpisodeNumber(3, 7), true), job.EpisodeFor(P("Season 3", "07.mkv")), "a bare number is suggested as the episode");

        job.Series = new TitleInfo(MediaKind.Series, "Vymyslený seriál", 2024);
        var items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, library: job);
        Assert.True(items.All(i => i.Reasons.Any(r => r is { Kind: ReviewReasonKind.KindNotConfirmed, Detail: "seriál" })), "the kind waits for confirmation");
        job.Kind = MediaKind.Series;
        items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, library: job);
        ReviewReasonKind[] Kinds(string path) => items.Single(i => Paths.Same(i.File.Path, path)).Reasons
            .Select(r => r.Kind).Where(k => k is not (ReviewReasonKind.NoAudio or ReviewReasonKind.MultipleVideoTracks or ReviewReasonKind.UndeterminedLanguage)).ToArray();
        Assert.Sequence([ReviewReasonKind.MissingEpisode], Kinds(P("Season 2", "Vymysleny.Serial.S01E04.mkv")), "conflict");
        Assert.Sequence([ReviewReasonKind.DuplicateEpisode], Kinds(P("Season 1", "Vymysleny.Serial.S01E02-E03.mkv")), "episode 3 twice");
        Assert.Sequence([ReviewReasonKind.DuplicateEpisode], Kinds(P("Season 1", "Vymysleny.Serial.S01E03.v2.mkv")));
        Assert.Sequence([ReviewReasonKind.OtherSeries], Kinds(P("Season 1", "Iny.Serial.S01E05.mkv")), "another series");
        Assert.Sequence([], Kinds(P("Season 1", "Vymysleny.Serial.S01E01.mkv")));
        Assert.Sequence([ReviewReasonKind.ExtraFile], Kinds(P("Season 2", "Extras", "Vymysleny Serial E09.mkv")), "an extra is no episode");
        foreach (var item in items.Where(i => i.PlannedPath is not null))
        {
            var season = Path.GetFileName(Path.GetDirectoryName(item.PlannedPath))!;
            Assert.True(season is "Season 00" or "Season 01" or "Season 02" or "Season 03", "two-digit season folders: " + item.PlannedPath);
        }

        Assert.Equal(
            Path.Combine(output, "TVSHOWS", "Vymyslený seriál (2024)", "Season 00", "Vymyslený seriál S00E01 - SMALL.mkv"),
            items.Single(i => Paths.Same(i.File.Path, P("Specials", "Vymysleny Serial E01.mkv"))).PlannedPath, "Season 00 (no tracks: no languages, quality or codec)");

        // a file in Specials named S01: Season 00 or Season 01 is asked; accepting all suggestions does not answer it
        var question = P("Specials", "Vymysleny.Serial.S01E02.mkv");
        analysis.Groups[0].Files.Add(new MediaFile { Path = question });
        job = LibraryJob.Guess(new LibraryLayout(output), analysis);
        job.Kind = MediaKind.Series;
        job.Series = new TitleInfo(MediaKind.Series, "Vymyslený seriál", 2024);
        job.ConfirmSuggestions();
        items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, library: job);
        Assert.Sequence([ReviewReasonKind.SeasonQuestion], Kinds(question), "asked, not accepted");
        Assert.True(items.Single(i => Paths.Same(i.File.Path, question)).Reasons.Single(r => r.Kind == ReviewReasonKind.SeasonQuestion).Detail?.Contains("Season 00", StringComparison.Ordinal) == true, "the question names both seasons");
        var bonus = P("Specials", "Season 1", "Bonus noc.mkv");
        analysis.Groups[0].Files.Add(new MediaFile { Path = bonus });
        job = LibraryJob.Guess(new LibraryLayout(output), analysis);
        job.Kind = MediaKind.Series;
        job.Series = new TitleInfo(MediaKind.Series, "Vymyslený seriál", 2024);
        job.ConfirmSuggestions();
        items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, library: job);
        Assert.Sequence([ReviewReasonKind.SeasonQuestion], Kinds(bonus), "a file without a number under Specials is asked too");
        Assert.True(job.EpisodeFor(bonus) is ({ Season: 0 }, true), "suggested in Season 00");
        Assert.Equal(2, job.AnswerSpecials(), "answer: specials");
        analysis.Groups[0].Files.RemoveAll(f => Paths.Same(f.Path, bonus));
        Assert.Equal((new EpisodeNumber(0, 2), false), job.EpisodeFor(question));
        analysis.Groups[0].Files.RemoveAll(f => Paths.Same(f.Path, question));

        // TMDB knows four episodes of season 1 and the specials without a count
        job.ConfirmSuggestions();
        job.SeriesOnTmdb = (job.Series, [new TmdbSeason(0, 0), new TmdbSeason(1, 4), new TmdbSeason(2, 1)]);
        analysis.Groups[0].Decisions.ProcessAnyway.Add(P("Season 1", "Vymysleny.Serial.S01E03.v2.mkv"));
        items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, library: job);
        Assert.Sequence([], Kinds(P("Season 1", "Vymysleny.Serial.S01E03.v2.mkv")), "processed anyway: a second version (K1)");
        Assert.Sequence([ReviewReasonKind.UnknownEpisode], Kinds(P("Season 2", "prvá.mkv")), "S02E02: TMDB has one episode in season 2");
        Assert.Sequence([], Kinds(P("Season 2", "druhá.mkv")));
        Assert.Sequence([], Kinds(P("Specials", "Vymysleny Serial E01.mkv")), "no count: not checked");
        Assert.Sequence([ReviewReasonKind.UnknownEpisode, ReviewReasonKind.OtherSeries], Kinds(P("Season 1", "Iny.Serial.S01E05.mkv")));

        // a further series sent together from Novinky (owner, 29. 10. 2026): its file goes to its own series
        var other = P("Season 1", "Iny.Serial.S01E05.mkv");
        job.SeriesOfFile[other] = new TitleInfo(MediaKind.Series, "Iný seriál", 2021);
        items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, library: job);
        Assert.Sequence([], Kinds(other), "no other series, no TMDB check of the first series");
        Assert.Equal(Path.Combine(output, "TVSHOWS", "Iný seriál (2021)", "Season 01"), Path.GetDirectoryName(items.Single(i => Paths.Same(i.File.Path, other)).PlannedPath),
            "into its own series");
        Assert.True(items.Where(i => i.PlannedPath is not null && !Paths.Same(i.File.Path, other)).All(i => i.PlannedPath!.Contains("Vymyslený seriál (2024)", StringComparison.Ordinal)),
            "the other files stay in the series of the batch");
        job.SeriesOfFile.Clear();

        job.Series = new TitleInfo(MediaKind.Series, "Iný názov", 2024);
        Assert.True(job.TmdbKnows(new EpisodeNumber(9, 9)), "the seasons belong to the other title");
    }

    [Test]
    public void FolderSortedIntoMoviesAndShowsIsSplit()
    {
        // owner, 29. 10. 2026: the user sorts films into MOVIES and series into TVSHOWS (each series in its own folder,
        // numbered without S01E01: "Name 1", "Name 01", "KM01"); versions of a film stay a film (all names invented)
        using var work = new Work();
        var root = Path.Combine(work.Path, "Vstup");
        string P(params string[] parts)
        {
            var path = Path.GetFullPath(Path.Combine([root, .. parts]));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            return path;
        }

        var river = new[] { P("TVSHOWS", "Vymyslená rieka (2021)", "Vymyslená rieka 1.mp4"), P("TVSHOWS", "Vymyslená rieka (2021)", "Vymyslená rieka 2.mp4"),
            P("TVSHOWS", "Vymyslená rieka (2021)", "Vymyslená rieka 10.mp4") };
        var cross = new[] { P("tvshows", "Krížová cesta", "Krížová cesta 01.avi"), P("tvshows", "Krížová cesta", "Krížová cesta 02.avi"),
            P("tvshows", "Krížová cesta", "Krížová cesta 12.avi") };
        var code = new[] { P("TVSHOWS", "Kódové meno (2019)", "KM01.mkv"), P("TVSHOWS", "Kódové meno (2019)", "KM02.mkv"), P("TVSHOWS", "Kódové meno (2019)", "KM10.mkv") };
        var loose = new[] { P("TVSHOWS", "Vymysleny Zradca (2024) S01E05.mkv"), P("TVSHOWS", "Vymysleny Zradca (2024) S01E06.mkv") };
        var versions = new[] { P("MOVIES", "Hraničný film (2018)", "Hranicny film 1.mkv"), P("MOVIES", "Hraničný film (2018)", "Hranicny film 2.mkv"),
            P("MOVIES", "Hraničný film (2018)", "Hranicny film 3.mkv") };
        var found = P("MOVIES", "Najdeny film (2019).mkv");
        var unsorted = P("Nezaradeny 01.mkv");
        var analysis = Synthetic(root, [.. river, .. cross, .. code, .. loose, .. versions, found, unsorted]);

        Assert.Sequence([1, 2, 10], TitleUnits.Sequence(river).Values.Order(), "Name 1, 2, 10");
        Assert.Sequence([1, 2, 10], TitleUnits.Sequence(code).Values.Order(), "KM01, KM02, KM10");
        Assert.Equal(0, TitleUnits.Sequence([P("x", "Abc.mkv"), P("x", "Abd.mkv")]).Count, "no numbers");

        var output = Path.Combine(work.Path, "OUTPUT");
        var job = LibraryJob.Guess(new LibraryLayout(output), analysis);
        Assert.True(job.Mixed && job.Units!.Count == 7, $"seven titles ({job.Units?.Count})");
        string Kinds(params string[] paths) => string.Join(",", paths.Select(p => job.KindFor(p) == MediaKind.Series ? "S" : "F"));
        Assert.Equal("S,S,S,S,S,S,S,S,S,S,S", Kinds([.. river, .. cross, .. code, .. loose]), "everything in TVSHOWS is a series");
        Assert.Equal("F,F,F,F", Kinds([.. versions, found]), "everything in MOVIES is a film – numbered versions too");
        Assert.True(job.KindConfirmedFor(river[0]) && job.KindConfirmedFor(found) && !job.KindConfirmedFor(unsorted) && job.Unsorted.Contains(unsorted),
            "the kind of a sorted file is certain; an unsorted one waits");
        Assert.Equal("Vymyslená rieka (2021)", job.SuggestedTitleFor(river[2])?.FolderName, "the series named by its folder");
        Assert.Equal("Kódové meno (2019)", job.SuggestedTitleFor(code[0])?.FolderName, "also when the files carry only a code");
        Assert.Equal((new EpisodeNumber(1, 10), true), job.EpisodeFor(river[2]), "the number of the sequence, to confirm");
        Assert.Equal((new EpisodeNumber(1, 12), true), job.EpisodeFor(cross[2]));
        Assert.Equal((new EpisodeNumber(1, 10), true), job.EpisodeFor(code[2]));
        Assert.Equal((new EpisodeNumber(1, 6), false), job.EpisodeFor(loose[1]), "S01E06 as written");
        Assert.True(job.UnitOf(loose[0]) == job.UnitOf(loose[1]) && job.SuggestedTitleFor(loose[0])?.Name == "Vymysleny Zradca", "loose episodes of one series together");
        Assert.Equal("Hraničný film (2018)", job.SuggestedTitleFor(versions[2])?.FolderName, "a version named by its film's folder");

        var split = analysis.SplitBy(job.UnitOf);
        Assert.Equal(7, split.Groups.Count, "one group per title");
        Assert.True(split.Groups.All(g => g.Files.Select(f => job.UnitOf(f.Path)).Distinct().Count() == 1), "no group mixes titles");

        var items = BatchPlanner.Plan(split, BatchTemplate.RulesOnly, library: job);
        ReviewReasonKind[] Reasons(string path) => [.. items.Single(i => Paths.Same(i.File.Path, path)).Reasons.Select(r => r.Kind)];
        Assert.True(Reasons(unsorted).Contains(ReviewReasonKind.NotSorted) && !Reasons(river[0]).Contains(ReviewReasonKind.NotSorted), "an unsorted file waits to be sorted");
        Assert.True(items.All(i => !i.Reasons.Any(r => r.Kind == ReviewReasonKind.KindNotConfirmed) || Paths.Same(i.File.Path, unsorted)),
            "the sorted kinds need no confirmation");

        var riverGroup = split.Groups.First(g => g.Files.Any(f => Paths.Same(f.Path, river[0])));
        job.AcceptAll(riverGroup.Files.Select(f => f.Path).ToList());
        items = BatchPlanner.Plan(split, BatchTemplate.RulesOnly, library: job);
        string? Planned(string path) => items.Single(i => Paths.Same(i.File.Path, path)).PlannedPath;
        Assert.Equal(Path.Combine(output, "TVSHOWS", "Vymyslená rieka (2021)", "Season 01"), Path.GetDirectoryName(Planned(river[2])), "a group accepted: into its series");
        Assert.True(Planned(code[0]) is null && Reasons(code[0]).Contains(ReviewReasonKind.MissingTitle), "the other titles still wait");

        job.ConfirmSuggestions();
        items = BatchPlanner.Plan(split, BatchTemplate.RulesOnly, library: job);
        Assert.True(Path.GetFileName(Planned(code[2]))?.StartsWith("Kódové meno S01E10", StringComparison.Ordinal) == true,
            "named by the series and the number: " + Planned(code[2]) + " | " + string.Join(", ", Reasons(code[2])));
        Assert.True(Planned(found)?.StartsWith(Path.Combine(output, "MOVIES", "Najdeny film (2019)"), StringComparison.Ordinal) == true,
            "a film into the films: " + Planned(found) + " | " + string.Join(", ", Reasons(found)));
        Assert.True(Planned(loose[0])?.StartsWith(Path.Combine(output, "TVSHOWS", "Vymysleny Zradca (2024)", "Season 01"), StringComparison.Ordinal) == true,
            "loose episodes into their series: " + Planned(loose[0]) + " | " + string.Join(", ", Reasons(loose[0])));
        Assert.True(Reasons(cross[0]).Contains(ReviewReasonKind.MissingTitle), "a series without a year waits for it (TMDB or typed)");
        Assert.True(versions.All(v => Planned(v)?.StartsWith(Path.Combine(output, "MOVIES", "Hraničný film (2018)"), StringComparison.Ordinal) == true),
            "the versions of a film into one film");
        Assert.True(items.All(i => i.Reasons.All(r => r.Kind is not (ReviewReasonKind.OtherSeries or ReviewReasonKind.EpisodeAmongFilms))),
            "the titles do not disturb each other – " + string.Join("; ", items.SelectMany(i => i.Reasons).Select(r => r.Kind).Distinct()));

        // without MOVIES and TVSHOWS the folder is one title, as before
        var classic = Synthetic(root, P("Season 1", "a.mkv"), P("Season 2", "b.mkv"));
        Assert.True(!LibraryJob.Guess(new LibraryLayout(output), classic).Mixed, "season folders: one series");
        Assert.True(!LibraryJob.Guess(new LibraryLayout(output), Synthetic(Path.Combine(root, "tvshows", "Krížová cesta"), cross)).Mixed,
            "a series folder below TVSHOWS analysed on its own: one series");
    }

    [Test]
    public void FilmsAndEpisodesAreToldApart()
    {
        using var work = new Work();
        string F(string folder, string name)
        {
            var path = Path.Combine(work.Path, folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            return path;
        }

        // "Name - Díl N (Year)" with the same year: episodes too
        var parts = Synthetic(Path.Combine(work.Path, "c"), F("c", "Kriminálka Anděl - Díl 1 (2008).mkv"), F("c", "Kriminálka Anděl - Díl 2 (2008).mkv"), F("c", "Kriminálka Anděl - Díl 3 (2008).mkv"));
        Assert.Equal(MediaKind.Series, LibraryJob.Guess(new LibraryLayout(Path.Combine(work.Path, "OUTPUT")), parts).Kind, "numbered parts of one year");
        var saga = Synthetic(Path.Combine(work.Path, "d"), F("d", "Sága - Časť 1 (2010).mkv"), F("d", "Sága - Časť 2 (2011).mkv"));
        Assert.Equal(MediaKind.Movie, LibraryJob.Guess(new LibraryLayout(Path.Combine(work.Path, "OUTPUT")), saga).Kind, "parts of different years: films");

        // "Name (Year) Díl N" is a series
        var series = Synthetic(Path.Combine(work.Path, "a"), F("a", "Kriminálka Anděl (2008) Díl 1.mkv"), F("a", "Kriminálka Anděl (2008) Díl 2.mkv"));
        var job = LibraryJob.Guess(new LibraryLayout(Path.Combine(work.Path, "OUTPUT")), series);
        Assert.True(job is { Kind: MediaKind.Series, SeriesSuggestion.FolderName: "Kriminálka Anděl (2008)" }, "a series with its year");

        // films with numbers in their titles stay films; episodes among them and different films under one title go for review
        var films = Synthetic(Path.Combine(work.Path, "b"),
            F("b", "Sága - Časť 2 (2011).mkv"), F("b", "Rally 4x10 (2015).mkv"), F("b", "Okno (2014).mkv"), F("b", "Nine Puzzles S01E01.mkv"));
        job = LibraryJob.Guess(new LibraryLayout(Path.Combine(work.Path, "OUTPUT")), films);
        Assert.Equal(MediaKind.Movie, job.Kind);
        job.ConfirmSuggestions();
        foreach (var (path, title) in job.Movies.ToList())
        {
            job.Movies[path] = title with { Year = title.Year ?? 2025 };
        }

        ReviewReasonKind[] Kinds(IReadOnlyList<BatchItem> items, string name) => items.Single(i => Path.GetFileName(i.File.Path) == name).Reasons
            .Select(r => r.Kind).Where(k => k is not (ReviewReasonKind.NoAudio or ReviewReasonKind.MultipleVideoTracks or ReviewReasonKind.UndeterminedLanguage)).ToArray();
        var planned = BatchPlanner.Plan(films, BatchTemplate.RulesOnly, library: job);
        Assert.Equal("Sága - Časť 2 (2011)", planned.Single(i => i.File.Path.EndsWith("Sága - Časť 2 (2011).mkv", StringComparison.Ordinal)).Title?.FolderName);
        Assert.Sequence([], Kinds(planned, "Rally 4x10 (2015).mkv"), "a film");
        Assert.Sequence([ReviewReasonKind.EpisodeAmongFilms], Kinds(planned, "Nine Puzzles S01E01.mkv"), "an episode among films");

        foreach (var path in job.Movies.Keys.ToList())
        {
            job.Movies[path] = new TitleInfo(MediaKind.Movie, "Okno", 2014);
        }

        planned = BatchPlanner.Plan(films, BatchTemplate.RulesOnly, library: job);
        Assert.Sequence([], Kinds(planned, "Okno (2014).mkv"), "its own title");
        Assert.Sequence([ReviewReasonKind.OtherSeries], Kinds(planned, "Rally 4x10 (2015).mkv"), "another film is no version of Okno");
    }

    [Test]
    public void FilmTitlesFromNames()
    {
        var analysis = Synthetic("/filmy",
            "/filmy/Creep.2004.1080p.BluRay.x264-GRP.mkv",
            "/filmy/Kantara A Legend Chapter 1 (2025) [tmdbid-1083637]/kantara-uhd.mkv",
            "/filmy/Pelíšky (1999)/pelisky.mkv");
        var job = LibraryJob.Guess(new LibraryLayout("/out"), analysis);

        Assert.Equal(MediaKind.Movie, job.Kind);
        Assert.Equal(new TitleInfo(MediaKind.Movie, "Creep", 2004), job.MovieSuggestions[Path.GetFullPath("/filmy/Creep.2004.1080p.BluRay.x264-GRP.mkv")]);
        Assert.Equal(new TitleInfo(MediaKind.Movie, "Pelíšky", 1999), job.MovieSuggestions[Path.GetFullPath("/filmy/Pelíšky (1999)/pelisky.mkv")], "named by its folder");
        Assert.Equal(
            "Kantara A Legend Chapter 1 (2025) [tmdbid-1083637]",
            job.Movies[Path.GetFullPath("/filmy/Kantara A Legend Chapter 1 (2025) [tmdbid-1083637]/kantara-uhd.mkv")].FolderName,
            "library name with an ID: confirmed");
    }

    [Test]
    public async Task RunWritesSeriesIntoTheLibrary()
    {
        using var work = await SeriesFolderAsync();
        var analysis = await AnalyzeAsync(work.Path);
        analysis.Groups[0].Decisions.Languages[new SlotKey(TrackType.Subtitles, 0)] = LanguageTag.FromCode("cs");
        var library = LibraryJob.Guess(new LibraryLayout(Path.Combine(work.Path, "OUTPUT")), analysis);
        library.ConfirmSuggestions();
        library.Series = new TitleInfo(MediaKind.Series, "Seriál", 2026);
        var items = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, library: library);
        var progress = new List<BatchProgress>();

        var results = await RunAsync(work.Path, items, progress);

        Assert.Equal(5, results.Count, "every file goes into the library");
        Assert.True(results.All(r => r.Status == ProcessingStatus.Done), string.Join("; ", results.SelectMany(r => r.Messages)));
        var season = Path.Combine(library.Layout.Shows, "Seriál (2026)", "Season 01");
        Assert.True(File.Exists(Path.Combine(season, "Seriál S01E02 - CZ EN SD H264 SMALL.mkv")), string.Join(", ", Directory.EnumerateFiles(season).Select(Path.GetFileName)));
        Assert.True(File.Exists(Path.Combine(season, "Seriál S01E10 - CZ EN SD H264 SMALL.mkv")), "episode 10");
        Assert.True(File.Exists(Path.Combine(season, "Seriál S01E01 - CZ SD H264 SMALL.mkv")), "confirmed suggestion for the loose file");
        Assert.Equal(5, progress.Count(p => p.Result is not null), "a result per file");

        var inLibrary = await AnalyzeAsync(Path.Combine(library.Layout.Shows, "Seriál (2026)"));
        var again = LibraryJob.Guess(library.Layout, inLibrary);
        Assert.True(again is { Kind: MediaKind.Series, KindConfirmed: false }, "the kind is only proposed");
        again.Kind = MediaKind.Series;
        again.Series = library.Series;
        var plan = BatchPlanner.Plan(inLibrary, BatchTemplate.RulesOnly, library: again);
        Assert.True(plan.All(i => i.Status == BatchStatus.Unchanged), string.Join("; ", plan.Select(i => i.Status + " " + Path.GetFileName(i.File.Path))));
    }

    [Test]
    public async Task StopAfterCurrentFile()
    {
        using var work = await SeriesFolderAsync();
        var analysis = await AnalyzeAsync(work.Path);
        analysis.Groups[0].Decisions.Languages[new SlotKey(TrackType.Subtitles, 0)] = LanguageTag.FromCode("cs");
        var library = LibraryJob.Guess(new LibraryLayout(Path.Combine(work.Path, "OUTPUT")), analysis);
        library.ConfirmSuggestions();
        library.Series = new TitleInfo(MediaKind.Series, "Seriál", 2026);
        var control = new BatchControl();
        var results = await RunAsync(work.Path, BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly, library: library),
            new SyncProgress<BatchProgress>(p => { if (p.Result is not null) { control.StopAfterCurrent = true; } }), control);
        Assert.Equal(1, results.Count, "stopped after the first file");
    }

    [Test]
    public async Task SimilarTrackSuggestsLanguage()
    {
        using var work = new Work();
        // episode 1: Czech dub tagged; episode 2: same dub without language and name
        var tagged = Path.Combine(work.Path, "E1.mkv");
        var untagged = Path.Combine(work.Path, "E2.mkv");
        File.Copy(TestData.Media("en-und-cz-forced.mkv"), tagged);
        File.Copy(TestData.Media("en-und-cz-forced.mkv"), untagged);
        await PropEditAsync(tagged, "--edit", "track:a2", "--set", "language=cs", "--delete", "name");
        await PropEditAsync(untagged, "--edit", "track:a2", "--delete", "name");
        var analysis = await AnalyzeAsync(work.Path);
        Assert.Equal(2, analysis.Groups.Count, "different structure");

        BatchPlanner.Suggest(analysis);
        var group = analysis.GroupOf(Path.GetFullPath(untagged))!;
        var suggestion = group.Suggestions[new SlotKey(TrackType.Audio, 1)];
        Assert.Equal(("cs", Confidence.Medium, EvidenceSource.Similarity), (suggestion.Language.Primary, suggestion.Confidence, suggestion.Source));
        var item = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly).Single(i => i.Group == group);
        Assert.Equal(BatchStatus.NeedsReview, item.Status, "a suggestion is not used before confirmation");
    }

    [Test]
    public async Task TargetFolderInsideIsNotAnalysed()
    {
        using var work = await SeriesFolderAsync();
        var output = Directory.CreateDirectory(Path.Combine(work.Path, "_výsledok", "Sezóna 1")).FullName;
        File.Copy(TestData.Media("cz-only.mkv"), Path.Combine(output, "x.mkv"));
        var analysis = await BatchAnalyzer.AnalyzeAsync(work.Path, recursive: true, await InspectorAsync(), excludeDirectories: [Path.Combine(work.Path, "_výsledok")]);
        Assert.Equal(5, analysis.FileCount, "results in the target folder are skipped");
    }

    [Test]
    public async Task OriginalsInTheOutputFolderAreFound()
    {
        // the owner keeps the streams in the output folder (27. 10. 2026): only MOVIES and TVSHOWS hold results
        using var work = await SeriesFolderAsync();
        var layout = new LibraryLayout(work.Path);
        layout.EnsureCreated();
        File.Copy(TestData.Media("cz-only.mkv"), Path.Combine(layout.Movies, "Výsledok (2026).mkv"));
        File.Copy(TestData.Media("cz-only.mkv"), Path.Combine(layout.Shows, "Výsledok S01E01.mkv"));
        var analysis = await BatchAnalyzer.AnalyzeAsync(work.Path, recursive: true, await InspectorAsync(), excludeDirectories: layout.ResultFolders);
        Assert.Equal(5, analysis.FileCount, "the originals beside MOVIES and TVSHOWS are found, the results are not");
        Assert.True(BatchAnalyzer.FindFiles(layout.Root, true, layout.ResultFolders).Files.All(f => !f.Contains("Výsledok", StringComparison.Ordinal)), "results skipped");
    }

    [Test]
    public async Task DecisionInOneGroupSuggestsAnother()
    {
        using var work = new Work();
        var full = Path.Combine(work.Path, "E1.mkv");
        var shorter = Path.Combine(work.Path, "E2.mkv");
        File.Copy(TestData.Media("en-und-cz-forced.mkv"), full);
        await MergeAsync(shorter, "--audio-tracks", "2", TestData.Media("en-und-cz-forced.mkv"));
        await PropEditAsync(full, "--edit", "track:a2", "--delete", "name");
        await PropEditAsync(shorter, "--edit", "track:a1", "--delete", "name");
        var analysis = await AnalyzeAsync(work.Path);
        var first = analysis.GroupOf(Path.GetFullPath(full))!;
        var second = analysis.GroupOf(Path.GetFullPath(shorter))!;
        BatchPlanner.Suggest(analysis);
        Assert.True(!second.Suggestions.ContainsKey(new SlotKey(TrackType.Audio, 0)), "nothing known yet");

        first.Decisions.Languages[new SlotKey(TrackType.Audio, 1)] = LanguageTag.FromCode("cs");
        BatchPlanner.Suggest(analysis);
        Assert.Equal("cs", second.Suggestions[new SlotKey(TrackType.Audio, 0)].Language.Primary, "the same dub in the shorter file");
    }

    [Test]
    public void ForcedSuggestedFromSubtitleCounts()
    {
        var file = new MediaFile
        {
            Path = "x.mkv",
            Tracks =
            [
                new MediaTrack { Id = 0, Type = TrackType.Video },
                new MediaTrack { Id = 1, Type = TrackType.Audio, Language = new LanguageTag("eng", "en") },
                new MediaTrack { Id = 2, Type = TrackType.Subtitles, Language = new LanguageTag("cze", "cs"), StatisticsFrames = 1200 },
                new MediaTrack { Id = 3, Type = TrackType.Subtitles, Language = new LanguageTag("cze", "cs"), StatisticsFrames = 37 },
            ],
        };
        var group = new BatchGroup(StructureSignature.Of(file), 0);
        group.Files.Add(file);
        var analysis = new BatchAnalysis("/", false, [group], [], []);

        BatchPlanner.Suggest(analysis);
        Assert.Sequence([new SlotKey(TrackType.Subtitles, 1)], group.ForcedSuggestions, "37 of 1200 subtitles: forced");

        group.Decisions.Forced[new SlotKey(TrackType.Subtitles, 1)] = true;
        var item = BatchPlanner.Plan(analysis, BatchTemplate.RulesOnly).Single();
        Assert.True(item.Target.For(3).Flags.Forced && item.Target.For(2).Flags.Default, "confirmed forced; full Czech default");
    }

    [Test]
    public void TemplateJsonRoundTrip()
    {
        var copy = BatchTemplate.FromJson(FilmCzEn.ToJson());
        Assert.Equal(FilmCzEn.Name, copy.Name);
        Assert.Sequence(FilmCzEn.Slots, copy.Slots, "slots");
        Assert.True(FilmCzEn.ToJson().Contains("\"Forced\"", StringComparison.Ordinal), "enums as text");
    }

    [Test]
    public async Task CacheAnswersUnchangedFiles()
    {
        using var work = new Work();
        var file = Path.Combine(work.Path, "a.mkv");
        File.Copy(TestData.Media("en-und-cz-forced.mkv"), file);
        var cacheFile = Path.Combine(work.Path, "cache.json");
        var first = new CachingInspector(await InspectorAsync(), cacheFile);
        var original = await first.InspectAsync(file);
        first.Save();

        var second = new CachingInspector(await InspectorAsync(), cacheFile);
        var cached = await second.InspectAsync(file);
        Assert.Equal(1, second.Hits, "answered from the cache");
        Assert.Sequence(original.Tracks.Select(t => (t.Id, t.Language, t.Flags, t.Name)), cached.Tracks.Select(t => (t.Id, t.Language, t.Flags, t.Name)), "same tracks");

        await PropEditAsync(file, "--edit", "track:a1", "--set", "name=changed");
        Assert.Equal("changed", (await second.InspectAsync(file)).TracksOf(TrackType.Audio).First().Name, "changed file read again");
    }

    [Test]
    public async Task SubtitleTextIsExtracted()
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var file = await (await InspectorAsync()).InspectAsync(TestData.Media("en-und-cz-forced.mkv"));
        var czech = file.TracksOf(TrackType.Subtitles).Single(t => t.Name == "Czech full");
        var text = await SubtitleText.ExtractAsync(installation, new ProcessRunner(), file, czech);
        Assert.Equal("Dobrý den, jak se máte?\nDěkuji, mám se dobře.", text);
    }

    [Test]
    public void TextLanguages()
    {
        const string czech = "Ahoj, jak se máš? Já jsem v pořádku, děkuji. Nevím, co mám dělat, ale musím to udělat ještě dnes. " +
                             "Proč jsi mi to neřekl dřív? Když to nevím, tak se nemůžu rozhodnout. Tohle je opravdu těžké, jo.";
        const string slovak = "Ahoj, ako sa máš? Ja som v poriadku, ďakujem. Neviem, čo mám robiť, ale musím to urobiť ešte dnes. " +
                              "Prečo si mi to nepovedal skôr? Keď to neviem, tak sa nemôžem rozhodnúť. Toto je naozaj ťažké, hej.";
        const string english = "Hello, how are you? I am fine, thank you. I don't know what to do, but I have to do it today. " +
                               "Why didn't you tell me before? If I do not know it, I can't decide. This is really hard, you know.";
        Assert.Equal("cs", TextLanguageDetector.Detect(czech));
        Assert.Equal("sk", TextLanguageDetector.Detect(slovak));
        Assert.Equal("en", TextLanguageDetector.Detect(english));
        Assert.Equal<string?>(null, TextLanguageDetector.Detect("Ahoj, ako sa máš?"), "too short");
        Assert.Equal("ru", TextLanguageDetector.Detect(string.Concat(Enumerable.Repeat("Привет, как дела? Я не знаю, что делать. ", 6))));
    }

    [Test]
    public void SubtitleCleaning() =>
        Assert.Equal(
            "Ahoj\nAko sa máš?",
            SubtitleText.Clean("[Script Info]\nTitle: x\nFormat: Layer, Start, End, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\i1}Ahoj{\\i0}\n\n1\n00:00:03,000 --> 00:00:04,000\n<i>Ako sa máš?</i>\n"));

    [Test]
    public void NaturalSort() =>
        Assert.Sequence(["E1.mkv", "E2.mkv", "e3.mkv", "E10.mkv", "E10a.mkv"], new[] { "E10.mkv", "e3.mkv", "E2.mkv", "E10a.mkv", "E1.mkv" }.Order(NaturalComparer.Instance), "order");

    /// <summary>Season folder with three episodes of one structure, a clean Czech file, a foreign-only file and a left-over temporary file.</summary>
    private static async Task<Work> SeriesFolderAsync()
    {
        var work = new Work();
        var season = Directory.CreateDirectory(Path.Combine(work.Path, "Sezóna 1")).FullName;
        foreach (var name in new[] { "Seriál E2.mkv", "Seriál E10.mkv", "Seriál E3.mkv" })
        {
            File.Copy(TestData.Media("en-und-cz-forced.mkv"), Path.Combine(season, name));
        }

        File.Copy(TestData.Media("en-und-cz-forced.mkv"), Path.Combine(season, "Seriál E5.universum-tmp.mkv"));
        File.Copy(TestData.Media("en-und-cz-forced.mkv"), Path.Combine(season, "Seriál E6.0a1b2c3d.universum-tmp"));
        File.Copy(TestData.Media("cz-only.mkv"), Path.Combine(work.Path, "cz-only.mkv"));
        var english = Path.Combine(work.Path, "en-only.mkv");
        await MergeAsync(english, "--audio-tracks", "1", "--subtitle-tracks", "5", TestData.Media("en-und-cz-forced.mkv"));
        return work;
    }

    private static async Task<BatchAnalysis> AnalyzeAsync(string folder) =>
        await BatchAnalyzer.AnalyzeAsync(folder, recursive: true, await InspectorAsync());

    private static async Task<IMediaInspector> InspectorAsync() => new MkvMergeInspector(await TestData.RequireMkvToolNixAsync(), new ProcessRunner());

    /// <summary>Films: every file confirmed under its own (guessed) title.</summary>
    private static LibraryJob Library(string root, BatchAnalysis analysis)
    {
        var job = LibraryJob.Guess(new LibraryLayout(Path.Combine(root, "OUTPUT")), analysis);
        job.Kind = MediaKind.Movie;
        job.ConfirmSuggestions();
        foreach (var (path, title) in job.Movies.ToList())
        {
            job.Movies[path] = title with { Year = title.Year ?? 2026 };
        }

        return job;
    }

    private static BatchAnalysis Synthetic(string folder, params string[] paths)
    {
        var group = new BatchGroup("x", 0);
        group.Files.AddRange(paths.Select(p => new MediaFile { Path = Path.GetFullPath(p) }));
        return new BatchAnalysis(Path.GetFullPath(folder), true, [group], [], []);
    }

    private static Task<IReadOnlyList<ProcessingResult>> RunAsync(string root, IReadOnlyList<BatchItem> items, ICollection<BatchProgress>? progress = null) =>
        RunAsync(root, items, progress is null ? null : new SyncProgress<BatchProgress>(progress.Add), null);

    private static async Task<IReadOnlyList<ProcessingResult>> RunAsync(string root, IReadOnlyList<BatchItem> items, IProgress<BatchProgress>? progress, BatchControl? control)
    {
        var processor = new FileProcessor(await TestData.RequireMkvToolNixAsync(), new ProcessRunner(), new NoRecycleBin(), Path.Combine(root, "_journal"));
        return await BatchRunner.RunAsync(items, processor, new OutputOptions(OriginalHandling.Keep), progress, control);
    }

    private static async Task PropEditAsync(string file, params string[] arguments) =>
        await new ProcessRunner().RunAsync(new ProcessRequest((await TestData.RequireMkvToolNixAsync()).ToolPath(MkvToolNixTool.MkvPropEdit), [file, .. arguments]));

    private static async Task MergeAsync(string output, params string[] arguments) =>
        await new ProcessRunner().RunAsync(new ProcessRequest((await TestData.RequireMkvToolNixAsync()).ToolPath(MkvToolNixTool.MkvMerge), ["-o", output, .. arguments]));

    private sealed class Work : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("universum-batch-");

        // the analysed folder's own name counts (a "Season 2" folder may be analysed): never the random temporary name
        // ("universum-batch-s54k…" read as season 54 made a test fail now and then)
        public Work() => Directory.CreateDirectory(Path);

        public string Path => System.IO.Path.Combine(_directory.FullName, "Práca");

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class NoRecycleBin : IRecycleBin
    {
        public string? CannotRecycle(string path) => "no recycle bin in tests";

        public void MoveToRecycleBin(string path) => throw new IOException("no recycle bin in tests");
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
