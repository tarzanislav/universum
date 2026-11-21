using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Universum.App.Pages;
using Universum.App.Setup;
using Universum.App.Shell;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Platform.Windows;

namespace Universum.App.SelfTesting;

// the self-test: Hromadné spracovanie and the disks
internal static partial class SelfTest
{
    private static async Task TestBatchPageAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var media = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media");
        var folder = Path.Combine(_outputDirectory, "batch");
        var season = Path.Combine(folder, "Sezóna 1");
        Directory.CreateDirectory(season);
        foreach (var name in new[] { "Seriál E2.mkv", "Seriál E3.mkv", "Seriál E10.mkv" })
        {
            File.Copy(Path.Combine(media, "en-und-cz-forced.mkv"), Path.Combine(season, name), overwrite: true);
        }

        File.Copy(Path.Combine(media, "cz-only.mkv"), Path.Combine(folder, "cz-only.mkv"), overwrite: true);
        var batch = model.Batch;
        model.SelectedTool = model.Tools.First(t => t.Page == batch);

        // owner's report (27. 10.): streams kept in the output folder were not found – only MOVIES and TVSHOWS are skipped
        var library = Path.Combine(_outputDirectory, "library");
        if (Directory.Exists(library))
        {
            var kept = Path.Combine(library, "Na spracovanie");
            Directory.CreateDirectory(kept);
            File.Copy(Path.Combine(media, "cz-only.mkv"), Path.Combine(kept, "Pôvodný film.mkv"), overwrite: true);
            File.WriteAllBytes(Path.Combine(kept, "Iný film.wmv"), []);
            batch.Folder = library;
            batch.Recursive = true;
            await batch.AnalyzeAsync();
            Check(batch.Analysis is { FileCount: 1 } found && found.Groups[0].Files[0].Path.EndsWith("Pôvodný film.mkv", StringComparison.Ordinal),
                $"batch: originals in the output folder are found, the results in MOVIES and TVSHOWS are not – {batch.Summary}");
            File.Delete(Path.Combine(kept, "Pôvodný film.mkv"));
            batch.Folder = kept;
            await batch.AnalyzeAsync();
            Check(batch.Groups.Count == 0 && batch.SummaryIsWarning && batch.Summary.Contains("nevie prečítať: 1 × WMV", StringComparison.Ordinal),
                "batch: a folder without readable video says why – " + batch.Summary);
            await Screenshot(window, "batch-no-video.png");

            // owner, 27. 10.: MP4, AVI, TS and others are processed too – as a new MKV
            foreach (var name in new[] { "en-cz.mp4", "sk-en.ts", "und.avi" })
            {
                File.Copy(Path.Combine(media, name), Path.Combine(kept, name), overwrite: true);
            }

            await batch.AnalyzeAsync();
            Check(batch.Analysis is { FileCount: 3 } && batch.Items.All(i => !i.File.IsMatroska && i.Status is Core.Batch.BatchStatus.Remux or Core.Batch.BatchStatus.NeedsReview),
                "batch: MP4, TS and AVI are analysed and planned as a new MKV – " + batch.Summary + " | " +
                string.Join(", ", batch.Items.Select(i => $"{Path.GetFileName(i.File.Path)} {i.Status}")));
            await Screenshot(window, "batch-other-formats.png");
            Directory.Delete(kept, recursive: true);

            // owner, 28. 10.: the language of audio tracks from their speech (whisper.cpp and FFmpeg installed with the
            // program); groups named by their film
            var speech = Path.Combine(library, "Reč");
            Directory.CreateDirectory(speech);
            File.Copy(Path.Combine(media, "speech-und.mkv"), Path.Combine(speech, "Zlatý kapor 2021 WEB-DL CZ 1080p.mkv"), overwrite: true);
            batch.Folder = speech;
            // owner, 7. 11. 2026: heard right after Analyzovať – no button needed (Rozpoznať jazyk audiostôp stays)
            batch.DetectAudioAfterAnalysis = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await batch.AnalyzeAsync();
            batch.DetectAudioAfterAnalysis = false;
            Check(!batch.IsAutoStep && batch.AutoStep.Length == 0, "batch: the blinking line of the steps after the analysis is gone when they end");
            Check(batch.Groups.Count == 1 && batch.Groups[0].Title == "Zlatý kapor", "batch: the group is named by its film – " + batch.Groups.FirstOrDefault()?.Title);
            Check(batch.DetectAudioCommand.CanExecute(null), "batch: speech recognition still offered by hand");
            var audio = batch.SlotRows.Where(s => s.Key.Type == TrackType.Audio).ToList();
            Write($"speech ({watch.Elapsed.TotalSeconds:0.0} s): {batch.RunState} | " + string.Join(" | ", audio.Select(a => $"{a.Slot}: {a.Suggested} – {a.Evidence}")));
            Check(audio.Count == 3 && audio.All(a => a.HasSuggestion), "batch: the language of the three audio tracks is suggested from the speech – " + batch.RunState);
            Check(audio.Count == 3 && audio[1].Suggested.Contains("(en)", StringComparison.Ordinal), "batch: English speech recognised – " + audio.ElementAtOrDefault(1)?.Suggested);
            Check(audio.Count == 3 && new[] { audio[0], audio[2] }.All(a => a.Suggested.Contains("(cs)", StringComparison.Ordinal) || a.Suggested.Contains("(sk)", StringComparison.Ordinal)),
                "batch: Czech and Slovak speech recognised as Czech or Slovak");
            await Screenshot(window, "batch-speech.png");

            // the pieces and the Czech/Slovak comparison in detail (for tuning the thresholds)
            var tools = await services.SpeechAsync();
            var synthetic = batch.Analysis!.Groups[0].Files[0];
            foreach (var (ordinal, spoken) in new[] { (0, "cs"), (1, "en"), (2, "sk") })
            {
                var timed = System.Diagnostics.Stopwatch.StartNew();
                var stages = new List<string>();
                var result = await Core.Speech.SpokenLanguage.DetectAsync(tools, services.Runner, synthetic.Path, ordinal, synthetic.Duration,
                    new Progress<Core.Speech.SpeechProgress>(p => stages.Add($"{p.Stage}@{timed.Elapsed.TotalSeconds:0.0}s")));
                Write($"speech detail ({timed.Elapsed.TotalSeconds:0.0} s; accurate model {(tools.Accurate is null ? "not installed" : "installed")}; " +
                      $"{string.Join(" ", stages.Distinct().Where((_, i) => i % 4 == 0).Take(12))}): audio {ordinal + 1} spoken {spoken} → {result.Language ?? "?"}, margin sk−cs {result.SlovakMargin?.ToString("0.000") ?? "–"}, " +
                      $"written in {result.Comparison?.TextLanguage ?? (result.Comparison?.TextLeaning is { } lean ? lean + "?" : "–")}, pieces {string.Join(" ", result.Pieces.Select(p => $"{p.Language}:{p.Probability:0.00}"))}, " +
                      $"doubt: {result.Doubt} | as cs: {result.Comparison?.CzechText} | as sk: {result.Comparison?.SlovakText}");
                if (spoken != "en" && tools.Accurate is not null)
                {
                    // the same with the quick model only (as before the accurate model) – for the comparison of speed
                    timed.Restart();
                    var quick = await Core.Speech.SpokenLanguage.DetectAsync(tools with { Accurate = null }, services.Runner, synthetic.Path, ordinal, synthetic.Duration);
                    Write($"speech detail, quick model only ({timed.Elapsed.TotalSeconds:0.0} s): audio {ordinal + 1} spoken {spoken} → {quick.Language ?? "?"}, doubt: {quick.Doubt}");

                    // the way of 0.3.17 (a run of FFmpeg and of whisper for each piece, two transcriptions with the search of
                    // alternatives) – how long it took, for the comparison
                    timed.Restart();
                    var old = Path.Combine(_outputDirectory, "rec-old");
                    Directory.CreateDirectory(old);
                    var oldPiece = Path.Combine(old, "piece-1.wav");
                    await services.Runner.RunAsync(new Core.Processes.ProcessRequest(tools.Ffmpeg, Core.Speech.SpokenLanguage.CutArguments(synthetic.Path, ordinal, TimeSpan.Zero, oldPiece)));
                    await services.Runner.RunAsync(Core.Speech.SpokenLanguage.WhisperRequest(tools, oldPiece) with { Background = false });
                    foreach (var language in new[] { "cs", "sk" })
                    {
                        await services.Runner.RunAsync(new Core.Processes.ProcessRequest(tools.Whisper,
                            ["-m", tools.Model, "-f", "-", "-l", language, "-nt", "-ojf", "-of", "old-" + language, "-t", Math.Clamp(Environment.ProcessorCount, 1, 8).ToString(CultureInfo.InvariantCulture)])
                            { WorkingDirectory = old, StandardInputFile = oldPiece });
                    }

                    Write($"speech detail, the way of 0.3.17 ({timed.Elapsed.TotalSeconds:0.0} s): audio {ordinal + 1}");
                    Directory.Delete(old, recursive: true);
                }
            }

            // where the accurate model spends its time (load, encoder, decoder) on 6 s of speech – for tuning the speed
            if (tools.Accurate is { } accurateModel)
            {
                var timing = Path.Combine(_outputDirectory, "rec-timing");
                Directory.CreateDirectory(timing);
                var piece = Path.Combine(timing, "piece.wav");
                await services.Runner.RunAsync(new Core.Processes.ProcessRequest(tools.Ffmpeg, Core.Speech.SpokenLanguage.CutArguments(synthetic.Path, 2, TimeSpan.Zero, piece)));
                var clipSeconds = Core.Speech.SpokenLanguage.SpeechClip(piece, Path.Combine(timing, "clip.wav"), Core.Speech.SpokenLanguage.ClipSeconds) ?? 6;
                var run = await services.Runner.RunAsync(new Core.Processes.ProcessRequest(tools.Whisper,
                    ["-m", accurateModel, "-f", "clip.wav", "-l", "cs", "-ac", Core.Speech.SpokenLanguage.AudioContext(clipSeconds).ToString(CultureInfo.InvariantCulture),
                     "-bs", "1", "-bo", "1", "-nf", "-nt", "-t", Math.Clamp(Environment.ProcessorCount, 1, 8).ToString(CultureInfo.InvariantCulture)])
                    { WorkingDirectory = timing, Background = true });
                Write($"speech timing of the accurate model ({run.Elapsed.TotalSeconds:0.0} s, {Environment.ProcessorCount} processors): " +
                      string.Join(" | ", run.StandardError.Split('\n').Where(l => l.Contains("time =", StringComparison.Ordinal) || l.Contains("system_info", StringComparison.Ordinal)).Select(l => l.Trim())));
                Directory.Delete(timing, recursive: true);
            }

            // owner, 28. 10.: each film of a group on its own, or only the chosen films
            File.Copy(Path.Combine(media, "speech-und.mkv"), Path.Combine(speech, "Iný kapor 2019 WEB-DL CZ 1080p.mkv"), overwrite: true);
            await batch.AnalyzeAsync();
            Check(batch.Groups.Count == 1 && batch.FileRows.Count == 2 && batch.Groups[0].Title.Contains("+ 1 ďalší", StringComparison.Ordinal),
                "batch: two films in one group – " + batch.Groups.FirstOrDefault()?.Title);
            batch.SelectFiles([batch.FileRows[1]]);
            watch.Restart();
            await batch.DetectAudioSelectedCommand.ExecuteAsync(null);
            var second = batch.FileRows[1].AudioSlots;
            Write($"speech, selected file ({watch.Elapsed.TotalSeconds:0.0} s): {batch.RunState} | " + string.Join(" | ", second.Select(a => $"{a.Slot}: {a.Hint}")));
            Check(second.All(a => a.SelectedChoice.Language is null && a.SelectedChoice.Display.StartsWith("návrh (", StringComparison.Ordinal)),
                "batch: the file's list shows what applies without a choice – " + string.Join(" | ", second.Select(a => a.SelectedChoice.Display)));
            Check(second.Count == 3 && second.All(a => a.Hint.StartsWith("návrh:", StringComparison.Ordinal)) &&
                  batch.FileRows[0].AudioSlots.All(a => !a.Hint.StartsWith("návrh:", StringComparison.Ordinal)),
                "batch: only the selected film was listened to, its own suggestions shown at it");
            watch.Restart();
            await batch.DetectAudioCommand.ExecuteAsync(null);
            Write($"speech, whole group ({watch.Elapsed.TotalSeconds:0.0} s): {batch.RunState}");
            Check(batch.FileRows[0].AudioSlots.All(a => a.Hint.StartsWith("návrh:", StringComparison.Ordinal)) &&
                  batch.RunState.Contains("3 už jazyk má", StringComparison.Ordinal),
                "batch: the group's films each on their own, the film heard before not again – " + batch.RunState);
            batch.ConfirmSuggestionsCommand.Execute(null);
            Check(batch.FileRows.All(f => f.AudioSlots[1].SelectedChoice.Language == Languages.English),
                "batch: a language chosen for the file is shown in its list");
            Check(batch.FileRows.All(f => f.AudioSlots[1].ChosenLanguage == Languages.English),
                "batch: Potvrdiť návrhy takes each film's own suggestion (English) – " + batch.RunState);
            await Screenshot(window, "batch-speech-files.png");

            // owner, 28. 10.: the name of a file may be wrong – what is not the found title's name waits, and any title
            // found can be chosen at the file (checked in HISTRA)
            var fakeTitles = new FakeTitleServices
            {
                Tmdb = """{"id":9700,"title":"The Golden Carp","original_title":"The Golden Carp","release_date":"2021-11-05"}""",
                Histra = """{"id":31,"title":"The Golden Carp","original_title":"The Golden Carp","release_date":"2021-11-05","external_ids":{"tmdb":"9700"}}""",
            };
            var titlesHttp = new System.Net.Http.HttpClient(fakeTitles);
            services.TitlesForSelfTest = new TitleSearch(
                new Core.Library.Tmdb.TmdbClient(titlesHttp, "selftest-key", pacer: new RequestPacer(100)),
                new Core.Library.Histra.HistraCatalog(new Core.Library.Histra.HistraClient(titlesHttp, "hst_selftest-0123456789", new RequestPacer(100))));
            batch.IsMovies = true;
            await batch.FindMoviesCommand.ExecuteAsync(null);
            // the film of another year waits with the list; the one of the year HISTRA's own catalog gives for the name is suggested
            Check(batch.FileRows[0] is { HasCandidates: true, IsUnverified: true } && batch.FileRows[1].NamingSuggestion.StartsWith("návrh: The Golden Carp (2021)", StringComparison.Ordinal),
                "batch: a name unlike the found title waits, with the titles found to choose from – " + batch.RunState + " | " +
                string.Join(" | ", batch.FileRows.Select(f => f.NamingSuggestion)));
            await Screenshot(window, "batch-title-candidates.png");
            var chosenFile = batch.FileRows[0];
            chosenFile.ChosenCandidate = chosenFile.Candidates[0];
            for (var wait = 0; wait < 50 && batch.Library!.TitleFor(chosenFile.Item.File.Path) is null; wait++)
            {
                await Task.Delay(100);
            }

            Check(batch.Library!.TitleFor(chosenFile.Item.File.Path)?.FolderName == "The Golden Carp (2021)" && !chosenFile.HasCandidates,
                "batch: a title chosen from the list, confirmed in HISTRA – " + batch.RunState);

            // owner, 29. 10. 2026: a film exactly by its TMDB ID typed in the field TMDB ID
            var byId = batch.FileRows[1];
            Check(batch.Library!.TitleFor(byId.Item.File.Path) is null, "batch: the second file has only a suggestion before");
            byId.TmdbId = "tmdbid-9700";
            for (var wait = 0; wait < 50 && batch.Library!.TitleFor(byId.Item.File.Path) is null; wait++)
            {
                await Task.Delay(100);
            }

            // the library names a film in Latin script without its ID (13.2); the title is the one of the ID
            Check(batch.Library!.TitleFor(byId.Item.File.Path)?.FolderName == "The Golden Carp (2021)", "batch: a film named by its TMDB ID – " + batch.RunState);

            // owner, 31. 10. 2026: Doplnok k názvu – chosen, in the name before the size class; a size class is refused
            var versioned = batch.FileRows[1];
            versioned.Addition = "Directors Cut";
            var withAddition = batch.FileRows.First(f => Core.IO.Paths.Same(f.Item.File.Path, versioned.Item.File.Path));
            Check(withAddition.Target.Contains("Directors Cut ", StringComparison.Ordinal) && withAddition.Addition == "Directors Cut",
                "batch: Doplnok k názvu in the target – " + withAddition.Target);
            withAddition.Addition = "BIG";
            Check(withAddition.Addition == "Directors Cut" && batch.RunState.Contains("Veľkostnú triedu", StringComparison.Ordinal), "batch: a size class is no addition – " + batch.RunState);
            withAddition.Addition = "";
            Check(!batch.FileRows.First(f => Core.IO.Paths.Same(f.Item.File.Path, versioned.Item.File.Path)).Target.Contains("Directors Cut", StringComparison.Ordinal),
                "batch: the addition taken away");
            services.TitlesForSelfTest = null;

            Directory.Delete(speech, recursive: true);
        }

        batch.Folder = folder;
        batch.Recursive = true;
        await batch.AnalyzeAsync();
        Check(batch.Groups.Count == 2 && batch.Groups[0].Count == 3, $"batch: 2 groups, the series first ({batch.Summary})");
        Check(batch.IsSeries, "batch: series recognised from the episode numbers");
        Check(batch.SeriesSuggestion.Contains("Seriál", StringComparison.Ordinal), "batch: series title suggested – " + batch.SeriesSuggestion);
        Check(batch.Items.Count(i => i.Status == Core.Batch.BatchStatus.NeedsReview) == 4, "batch: everything waits for the title and the subtitle language");
        Check(batch.SlotRows.First(s => s.Key == new Core.Batch.SlotKey(TrackType.Audio, 1)) is { IsNamedInTrack: true } named && named.ChosenLanguage == Languages.Czech,
            "batch: the language named in the track name (CZ Dabing) is shown as chosen");
        Check(batch.SlotRows.First(s => s.Key == new Core.Batch.SlotKey(TrackType.Subtitles, 1)).Evidence.StartsWith("NÁVRH UNIVERSUM podľa textu titulkov (isté", StringComparison.Ordinal),
            "batch: the subtitles' language read after the analysis – " + batch.SlotRows.First(s => s.Key == new Core.Batch.SlotKey(TrackType.Subtitles, 1)).Evidence);
        await Screenshot(window, "batch-analysed.png");

        var forcedSlot = batch.SlotRows.First(s => s.Key == new Core.Batch.SlotKey(TrackType.Subtitles, 0));
        forcedSlot.ChosenLanguage = Languages.Czech;

        // owner, 29. 10. 2026: the group's tracks in the order of the result, moved up or down within their type
        string Order() => string.Join(",", batch.SlotRows.Select(s => s.Slot));
        string Subtitles() => string.Join(",", batch.Items.First(i => i.Group == forcedSlot.Group).Target.Tracks
            .Where(t => t.Type == TrackType.Subtitles).Select(t => t.Language.Primary + (t.Flags.Forced ? "!" : "")));
        var before = Order();
        Check(batch.SlotRows[0].Key == new Core.Batch.SlotKey(TrackType.Audio, 1) && Subtitles() == "cs!,cs,en",
            $"batch: the tracks shown in the order of the result – {before} | {Subtitles()}");
        var english = batch.SlotRows.First(s => s.Key == new Core.Batch.SlotKey(TrackType.Subtitles, 2));
        english.MoveUpCommand.Execute(null);
        Check(Subtitles() == "cs!,en,cs" && Order() != before, $"batch: English subtitles moved up for all files – {Order()} | {Subtitles()}");
        await Screenshot(window, "batch-track-order.png");
        english.MoveDownCommand.Execute(null);
        Check(Subtitles() == "cs!,cs,en" && Order() == before, $"batch: and back – {Order()} | {Subtitles()}");

        // owner, 29. 10. 2026: a language written in the file can be corrected for the group ("EN" that is Czech)
        var written = batch.SlotRows.First(s => s.Key == new Core.Batch.SlotKey(TrackType.Audio, 0));
        Check(written.ChosenLanguage == Languages.English, "batch: the file's language is shown in the list");
        written.ChosenLanguage = Languages.Slovak;
        Check(batch.Items.Where(i => i.Group == forcedSlot.Group).All(i => i.Target.Tracks.Any(t => t.Type == TrackType.Audio && t.Language.Primary == "sk")) &&
              written.Evidence.StartsWith("vaša voľba (v súbore: angličtina", StringComparison.Ordinal),
            "batch: the corrected language is used for all files – " + written.Evidence);
        written.ChosenLanguage = Languages.English;

        // owner, 30. 10. 2026: Track name for the whole group – audio and subtitles
        Check(written.CanName && written.TrackName.Length == 0 && forcedSlot.TrackName == "Forced", $"batch: the names the result gets – „{written.TrackName}“, „{forcedSlot.TrackName}“");
        written.TrackName = "Vymyslený zvuk";
        forcedSlot.TrackName = "Forced CZ";
        Check(batch.Items.Where(i => i.Group == forcedSlot.Group).All(i => i.Target.Tracks.Any(t => t.Type == TrackType.Audio && t.Name == "Vymyslený zvuk") &&
                                                                         i.Target.Tracks.Any(t => t.Type == TrackType.Subtitles && t.Name == "Forced CZ")),
            "batch: the names written are used for all files of the group");
        written.TrackName = "";
        forcedSlot.TrackName = "Forced";
        Check(!forcedSlot.Group.Decisions.Names.ContainsKey(forcedSlot.Key) && batch.Items.Where(i => i.Group == forcedSlot.Group).All(i => i.Target.Tracks.Any(t => t.Type == TrackType.Subtitles && t.Name == "Forced")),
            "batch: the rules' name again needs no decision");
        Check(!forcedSlot.Group.Decisions.Languages.ContainsKey(written.Key), "batch: the file's own language again needs no decision");

        batch.AcceptSuggestionsCommand.Execute(null);
        batch.SeriesTitle = "Seriál (2026)";
        Check(batch.Items.Count(i => i.Status == Core.Batch.BatchStatus.Remux) == 3, "batch: group decision applies to all three episodes");
        Check(batch.Items.Count(i => i.Status == Core.Batch.BatchStatus.Place) == 1, "batch: clean file only goes into the library (suggested episode 1)");

        batch.KeepOriginal = true;
        Check(batch.RunCommand.CanExecute(null), "batch: run enabled");
        await Screenshot(window, "batch-ready.png");
        await batch.RunCommand.ExecuteAsync(null);
        Check(batch.Results.Count == 4 && batch.Results.All(r => r.Status == Core.Execution.ProcessingStatus.Done),
            "batch: four files in the library – " + string.Join("; ", batch.Results.SelectMany(r => r.Messages)));
        var shows = Path.Combine(model.Batch.Library!.Layout.Shows, "Seriál (2026)", "Season 01");
        Check(File.Exists(Path.Combine(shows, "Seriál S01E10 - CZ EN SD H264 SMALL.mkv")), "batch: episode 10 in Season 01");
        Check(File.Exists(Path.Combine(shows, "Seriál S01E01 - CZ SD H264 SMALL.mkv")), "batch: the loose file as episode 1");
        Check(batch.Finish is { Kind: BatchFinishKind.Done } finish && finish.Title.StartsWith("HOTOVO", StringComparison.Ordinal) &&
              finish.Detail.StartsWith("4 súbory v knižnici", StringComparison.Ordinal),
            "batch: the end of the run is shown boldly – " + batch.Finish?.Title + " | " + batch.Finish?.Detail);
        await Screenshot(window, "batch-done.png");

        // owner, 29. 10. 2026: the run is a job of Fronta úloh with a line for every file in Výstup úloh
        Check(services.Jobs.Jobs.OfType<Core.Jobs.BatchJob>().LastOrDefault() is { Status: Core.Jobs.JobStatus.Done } batchJob &&
              batchJob.Messages.Count(m => m.Text.Contains(" · OK: ", StringComparison.Ordinal)) == 4 && batchJob.Outcome.StartsWith("4 z 4", StringComparison.Ordinal),
            "batch: the run is in the job queue with its output – " + string.Join(" | ", services.Jobs.Jobs.OfType<Core.Jobs.BatchJob>().LastOrDefault()?.Messages.Select(m => m.Text) ?? []));
        model.SelectedTool = model.Tools.First(t => t.Page == model.Queue);
        await Screenshot(window, "batch-in-queue.png");
        model.SelectedTool = model.Tools.First(t => t.Page == batch);

        // owner, 29. 10. 2026: the originals already written are known at the next analysis ("už v knižnici")
        await batch.AnalyzeAsync();
        batch.SlotRows.First(s => s.Key == new Core.Batch.SlotKey(TrackType.Subtitles, 0)).ChosenLanguage = Languages.Czech;
        batch.AcceptSuggestionsCommand.Execute(null);
        batch.SeriesTitle = "Seriál (2026)";
        for (var wait = 0; wait < 100 && batch.Items.Count(i => i.AlreadyInLibraryAt is not null) < 3; wait++)
        {
            await Task.Delay(100);
        }

        Check(batch.Items.Count(i => i.AlreadyInLibraryAt is not null) >= 3 && batch.Items.Count(i => i.NeedsWork) <= 1,
            $"batch: the written originals are 'už v knižnici' at the analysis – {batch.Simulation}");

        // owner, 31. 10. 2026: Revízia – a file 'už v knižnici' chosen to be written again, then left as it was
        var present = batch.Items.First(i => i.AlreadyInLibraryAt is not null);
        batch.SelectedGroup = batch.Groups.First(g => g.Group == present.Group);
        var presentRow = batch.FileRows.First(f => f.Item.File.Path == present.File.Path);
        Check(presentRow.CanRevise && !presentRow.Revision, "batch: Revízia offered for a file 'už v knižnici'");
        presentRow.Revision = true;
        presentRow = batch.FileRows.First(f => f.Item.File.Path == present.File.Path);
        Check(presentRow.Revision && presentRow.Item.NeedsWork && presentRow.Status.StartsWith("revízia", StringComparison.Ordinal),
            $"batch: the revision will write it again – {presentRow.Status}");
        presentRow.Revision = false;
        Check(batch.Items.First(i => i.File.Path == present.File.Path).AlreadyInLibraryAt is not null, "batch: without the revision it is 'už v knižnici' again");

        // owner, 30. 10. 2026: Upraviť v štúdiu – a file of the batch in Úprava titulkostopy, back with Uložiť do úlohy
        var studioItem = batch.Items.First(i => i.Target.Tracks.Any(t => t.Type == TrackType.Subtitles && !t.IsAdded && t.Source.IsTextSubtitles));
        var studioTrack = studioItem.Target.Tracks.First(t => t.Type == TrackType.Subtitles && !t.IsAdded && t.Source.IsTextSubtitles).TrackId;
        batch.SelectedGroup = batch.Groups.First(g => g.Group == studioItem.Group);
        batch.SelectedFile = batch.FileRows.First(f => f.Item.File.Path == studioItem.File.Path);
        batch.EditInStudioCommand.Execute(null);
        await WaitFor(() => model.Subtitles.IsTask && model.SelectedTool.Page == model.Subtitles && model.Subtitles.Rows.Any(r => !r.IsExternal && r.Track.Id == studioTrack));
        model.Subtitles.Rows.First(r => !r.IsExternal && r.Track.Id == studioTrack).DelayText = "1500";
        await Screenshot(window, "studio-task.png");
        model.Subtitles.SaveToTaskCommand.Execute(null);
        var studied = batch.Items.First(i => i.File.Path == studioItem.File.Path);
        Check(model.SelectedTool.Page == batch && studied.Target.For(studioTrack).Timing == Core.Subtitles.SubtitleTiming.Delay(1500) &&
              batch.RunState.Contains("úpravy zo štúdií v úlohe", StringComparison.Ordinal),
            $"batch: Upraviť v štúdiu → Uložiť do úlohy – the new times in the task ({studied.Target.For(studioTrack).Timing?.MkvMergeValue}) – {batch.RunState}");
        // owner, 30. 10. 2026: Zvuk v štúdiu – the file's sound shifted, back with Uložiť do úlohy; the subtitles' edits stay
        batch.SelectedGroup = batch.Groups.First(g => g.Group == studioItem.Group);
        batch.SelectedFile = batch.FileRows.First(f => f.Item.File.Path == studioItem.File.Path);
        batch.EditSoundInStudioCommand.Execute(null);
        await WaitFor(() => model.Audio.IsTask && model.SelectedTool.Page == model.Audio && model.Audio.Rows.Count > 0);
        var studioSound = model.Audio.Rows.FirstOrDefault(r => !r.IsExternal);
        Check(studioSound is not null, "batch: Zvuk v štúdiu opens the file's sounds – " + model.Audio.Summary);
        if (studioSound is not null)
        {
            studioSound.DelayText = "250";
            studioSound.Name = "Vymyslený zvuk v úlohe";
            await Screenshot(window, "studio-audio-task.png");
            model.Audio.SaveToTaskCommand.Execute(null);
            var both = batch.Items.First(i => i.File.Path == studioItem.File.Path);
            Check(model.SelectedTool.Page == batch && both.Target.For(studioSound.Track.Id) is { Timing.DelayMilliseconds: 250, Name: "Vymyslený zvuk v úlohe" } &&
                  both.Target.For(studioTrack).Timing == Core.Subtitles.SubtitleTiming.Delay(1500),
                $"batch: Zvuk v štúdiu → Uložiť do úlohy – the sound's shift and name in the task, the subtitles' times stay – {both.Target.For(studioSound.Track.Id).Timing?.MkvMergeValue} {both.Target.For(studioSound.Track.Id).Name}");
        }

        // owner, 2. 11. 2026: Obraz v štúdiu – a part kept and the proportions, the picture written anew (exact cut); the
        // subtitles' and the sound's edits stay
        batch.SelectedGroup = batch.Groups.First(g => g.Group == studioItem.Group);
        batch.SelectedFile = batch.FileRows.First(f => f.Item.File.Path == studioItem.File.Path);
        batch.EditPictureInStudioCommand.Execute(null);
        await WaitFor(() => model.Video.IsTask && model.SelectedTool.Page == model.Video && model.Video.IsVideo);
        model.Video.ExactCut = true;
        model.Video.AddPart(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        model.Video.Aspect = model.Video.Aspects.First(a => a.Value == "4/3");
        await Screenshot(window, "studio-video-task.png");
        model.Video.SaveToTaskCommand.Execute(null);
        var pictured = batch.Items.First(i => i.File.Path == studioItem.File.Path);
        Check(model.SelectedTool.Page == batch && pictured.Target.Cut is { Count: 1 } &&
              pictured.Target.Tracks.Single(t => t.Type == TrackType.Video) is { AspectRatio: "4/3", Source.VideoEncode: not null } &&
              pictured.Target.For(studioTrack).Timing == Core.Subtitles.SubtitleTiming.Delay(1500),
            $"batch: Obraz v štúdiu → Uložiť do úlohy – the cut, the proportions and the picture anew in the task, the subtitles' times stay – {batch.RunState}");
        await Screenshot(window, "studio-video-back.png");

        batch.SaveStudio(studioItem.File.Path, new Core.Batch.StudioEdits());
        Check(batch.Items.First(i => i.File.Path == studioItem.File.Path).Target.For(studioTrack).Timing is null, "batch: the edits of the studio forgotten again");

        var editor = new Templates.TemplateEditorWindow(new Templates.TemplateEditorViewModel(
            Core.Templates.BatchTemplate.FromTarget("Podľa skupiny A", batch.Items.First().Target))) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        editor.Show();
        await Screenshot(editor, "template-editor.png");
        editor.Close();

        // owner, 29. 10. 2026: the user sorts the input folder into MOVIES and TVSHOWS – each series folder, numbered
        // without S01E01, is a series of its own, each film a film (invented names)
        var sorted = Path.Combine(_outputDirectory, "zoradene");
        var show = Path.Combine(sorted, "TVSHOWS", "Vymyslený seriál (2024)");
        var film = Path.Combine(sorted, "MOVIES", "Vymyslený film (2020)");
        Directory.CreateDirectory(show);
        Directory.CreateDirectory(film);
        File.Copy(Path.Combine(media, "cz-only.mkv"), Path.Combine(show, "Vymysleny serial 01.mkv"), overwrite: true);
        File.Copy(Path.Combine(media, "cz-only.mkv"), Path.Combine(show, "Vymysleny serial 02.mkv"), overwrite: true);
        File.Copy(Path.Combine(media, "cz-only.mkv"), Path.Combine(film, "Vymyslený film 1080p.mkv"), overwrite: true);
        File.Copy(Path.Combine(media, "cz-only.mkv"), Path.Combine(film, "Vymyslený film 720p.mkv"), overwrite: true);
        batch.Folder = sorted;
        batch.Recursive = true;
        await batch.AnalyzeAsync();
        Check(batch.IsMixed && batch.Groups.Count == 2, $"batch: a sorted folder – one group per title ({batch.Groups.Count}) – {batch.Summary}");
        batch.SelectedGroup = batch.Groups.First(g => g.Group.Files.Any(f => f.Path.Contains("TVSHOWS", StringComparison.Ordinal)));
        Check(batch.IsSeries && !batch.CanChooseKind && batch.SeriesSuggestion.StartsWith("návrh: Vymyslený seriál (2024)", StringComparison.Ordinal) &&
              batch.FileRows.All(f => f.NamingSuggestion.StartsWith("návrh: S01E0", StringComparison.Ordinal)),
            $"batch: TVSHOWS – a series named by its folder, episodes by the numbers – {batch.SeriesSuggestion} | {string.Join(", ", batch.FileRows.Select(f => f.NamingSuggestion))}");
        batch.AcceptSuggestionsCommand.Execute(null);
        Check(batch.SeriesTitle == "Vymyslený seriál (2024)" && batch.FileRows.Select(f => f.Naming).Order().SequenceEqual(["S01E01", "S01E02"]) &&
              batch.Items.Where(i => i.File.Path.StartsWith(show, StringComparison.Ordinal))
                  .All(i => i.PlannedPath?.Contains(Path.Combine("TVSHOWS", "Vymyslený seriál (2024)", "Season 01"), StringComparison.Ordinal) == true),
            "batch: Prijať návrhy – the series and its episodes accepted – " + batch.RunState);
        await Screenshot(window, "batch-sorted-series.png");
        batch.SelectedGroup = batch.Groups.First(g => g.Group.Files.Any(f => f.Path.Contains("MOVIES", StringComparison.Ordinal)));
        Check(batch.IsMovies && batch.FileRows.All(f => f.IsFilm) && batch.Items.Where(i => i.File.Path.StartsWith(film, StringComparison.Ordinal))
                  .All(i => i.Reasons.All(r => r.Kind != Core.Batch.ReviewReasonKind.KindNotConfirmed)),
            "batch: MOVIES – the numbered versions stay a film, its kind certain");

        // owner, 30. 10. 2026: films have the row Film: like a series – the title for the film and its versions
        var versions = batch.Items.Where(i => i.File.Path.StartsWith(film, StringComparison.Ordinal)).Select(i => i.File.Path).ToList();
        Check(batch.CanNameFilm && batch.FilmHint.Contains(versions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            $"batch: the row Film: names the group's {versions.Count} versions – {batch.FilmHint}");
        batch.FilmTitle = "Vymyslený film na skúšku (2020)";
        Check(batch.FilmTitle == "Vymyslený film na skúšku (2020)" &&
              batch.Items.Where(i => versions.Contains(i.File.Path)).All(i => i.PlannedPath?.Contains(Path.Combine("MOVIES", "Vymyslený film na skúšku (2020)"), StringComparison.Ordinal) == true),
            "batch: the film typed in the row Film: names all its versions – " + string.Join(" | ", batch.Items.Select(i => i.PlannedPath)));
        await Screenshot(window, "batch-film-row.png");

        // owner, 21. 11. 2026: Do Hromadného – a new film joins the batch on top (only it is read); the film named before keeps its name
        var addedFilm = Path.Combine(sorted, "MOVIES", "Vymyslený nový film (2025)", "Vymysleny novy film.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(addedFilm)!);
        File.Copy(Path.Combine(media, "cz-only.mkv"), addedFilm, overwrite: true);
        var groupsBefore = batch.Groups.Count;
        var addedToBatch = await batch.AddChosenAsync([addedFilm]);
        Check(addedToBatch == true && batch.Groups.Count == groupsBefore + 1 && batch.Groups[0].IsNew && batch.Groups.Skip(1).All(g => !g.IsNew) &&
              batch.Groups[0].Group.Files.Any(f => Core.IO.Paths.Same(f.Path, addedFilm)) && batch.FilmGroups.FirstOrDefault() == batch.Groups[0] &&
              batch.Items.Where(i => versions.Contains(i.File.Path)).All(i => i.PlannedPath?.Contains(Path.Combine("MOVIES", "Vymyslený film na skúšku (2020)"), StringComparison.Ordinal) == true) &&
              batch.Groups[0].Title.StartsWith("Vymyslený nový film", StringComparison.Ordinal),
            "batch: Do Hromadného – the new film on top, marked; the film named before keeps its name – " + batch.Summary + " | " +
            string.Join(" | ", batch.Groups.Select(g => $"{g.Title} {(g.IsNew ? "NEW" : "")}")));
        Check(await batch.AddChosenAsync([addedFilm]) == true && batch.Groups.Count == groupsBefore + 1, "batch: a file in the batch is not added twice – " + batch.RunState);
        await Screenshot(window, "batch-added.png");

        // owner, 29. 10. 2026: MEGA spracovanie – the prepared files go into a batch written later, shown "v dávke" here
        var batches = model.Mega.Rows.Count;
        var ready = batch.Items.Where(i => i.NeedsWork).Select(i => i.File.Path).ToList();
        Check(batch.SendToMegaCommand.CanExecute(null) && ready.Count > 0, $"batch: Odoslať do MEGA enabled ({ready.Count} ready)");
        batch.SendToMegaCommand.Execute(null);
        Check(model.SelectedTool.Page == model.Mega && model.Mega.Rows.Count == batches + 1 && model.Mega.Selected?.Batch.Files.Count == ready.Count,
            $"mega: a batch with the {ready.Count} prepared files – {batch.RunState}");
        Check(batch.Items.Where(i => ready.Contains(i.File.Path)).All(i => !i.NeedsWork && i.Reasons.Any(r => r.Kind == Core.Batch.ReviewReasonKind.InMegaBatch)),
            "batch: its files are shown in the batch, not written here");
        var megaBatch = model.Mega.Selected!.Batch;
        model.Mega.Selected.Start = Core.Mega.MegaStart.At;
        Check(megaBatch.StartAt is { } at && at > DateTime.Now && model.Mega.Selected.State.StartsWith("čaká na ", StringComparison.Ordinal),
            "mega: a time to start – " + model.Mega.Selected.State);
        // the batch scheduled for a day and an hour is in Kalendár (the layer UNIVERSUM) and opens MEGA spracovanie
        model.Calendar.Select(DateOnly.FromDateTime(megaBatch.StartAt!.Value));
        var scheduled = model.Calendar.DetailSections.SelectMany(s => s.Entries).FirstOrDefault(e => e.Layer == CalendarLayer.Mega);
        Check(scheduled is not null && scheduled.Title.StartsWith(megaBatch.Label, StringComparison.Ordinal) && scheduled.TimeText == megaBatch.StartAt.Value.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            "calendar: the scheduled batch of MEGA on its day and hour – " + scheduled?.TimeText + " " + scheduled?.Title + " · " + scheduled?.Detail);
        model.Open(model.Calendar);
        scheduled!.OpenCommand!.Execute(null);
        Check(model.SelectedTool.Page == model.Mega && model.Mega.Selected?.Batch == megaBatch, "calendar: the scheduled batch opens in MEGA spracovanie");
        await Screenshot(window, "mega.png");

        // owner, 29. 10. 2026: the batch opens in Hromadné spracovanie with every decision, and is saved back
        var planned = megaBatch.Files.ToDictionary(f => f.Path, f => f.PlannedPath);
        model.Mega.EditCommand.Execute(null);
        for (var wait = 0; wait < 300 && (!batch.IsEditingMega || batch.Busy || batch.IsAnalyzing || batch.Items.Count == 0); wait++)
        {
            await Task.Delay(100);
        }

        Check(model.SelectedTool.Page == batch && batch.IsEditingMega && megaBatch.IsBeingEdited && batch.SendToMegaText == "Uložiť do " + megaBatch.Label,
            "mega: the batch is open in Hromadné spracovanie – " + batch.Summary);
        Check(batch.Items.Count == planned.Count && batch.Items.All(i => i.NeedsWork && planned.TryGetValue(i.File.Path, out var place) && place == i.PlannedPath),
            "mega: opened with every decision – the same places in the library: " + string.Join(" | ", batch.Items.Select(i => $"{i.Status} {i.PlannedPath}")));
        await Screenshot(window, "mega-edit.png");
        batch.SendToMegaCommand.Execute(null);
        Check(!batch.IsEditingMega && !megaBatch.IsBeingEdited && megaBatch.Files.Count == planned.Count && model.SelectedTool.Page == model.Mega &&
              batch.Items.All(i => i.Reasons.Any(r => r.Kind == Core.Batch.ReviewReasonKind.InMegaBatch)),
            "mega: saved back into the batch – " + batch.RunState);
        model.Mega.Selected = model.Mega.Rows.First(r => r.Batch == megaBatch);
        model.Mega.Selected.Start = Core.Mega.MegaStart.Manual;
        await model.Mega.StartAsync(megaBatch);
        for (var wait = 0; wait < 600 && model.Mega.LastJob is { IsFinished: false }; wait++)
        {
            await Task.Delay(100);
        }

        // two versions of the test film are the same file: the second one is the same stream – already in the library
        Check(megaBatch.State == Core.Mega.MegaBatchState.Done &&
              megaBatch.Files.All(f => f.State is Core.Mega.MegaFileState.Done or Core.Mega.MegaFileState.AlreadyInLibrary) &&
              megaBatch.Files.All(f => File.Exists(f.Note)),
            "mega: the batch written into the library after the check – " + megaBatch.Outcome + " | " +
            string.Join(" | ", model.Mega.LastJob?.Messages.Select(m => m.Text) ?? []));
        Check(services.Jobs.Jobs.Contains(model.Mega.LastJob!), "mega: the batch ran as a job of the job queue");
        await Screenshot(window, "mega-done.png");
        model.Mega.ClearFinishedCommand.Execute(null);
        Check(!services.Mega.Batches.Contains(megaBatch), "mega: finished batches cleared");

        // owner, 30. 10. 2026: Zmazať originály – the originals of the page's runs to the Recycle Bin after the run; the
        // library is never touched (on a drive without a Recycle Bin they stay and the page says why)
        Check(!batch.CanDeleteOriginals && !batch.DeleteOriginalsCommand.CanExecute(null), "batch: originals kept – nothing to delete");
        batch.DeleteManually = true;
        Check(batch.CanDeleteOriginals && batch.DeleteOriginalsCommand.CanExecute(null) && batch.DeleteOriginalsText.StartsWith("Zmazať originály (", StringComparison.Ordinal),
            "batch: Zmazať originály offered after the runs – " + batch.DeleteOriginalsText);
        await Screenshot(window, "batch-delete-originals.png");
        await batch.DeleteOriginalsCommand.ExecuteAsync(null);
        var (removedOriginals, keptOriginals) = batch.LastCleanup;
        Check(removedOriginals.Count >= 4 && removedOriginals.All(f => !File.Exists(f)) &&
              File.Exists(Path.Combine(shows, "Seriál S01E10 - CZ EN SD H264 SMALL.mkv")) && File.Exists(Path.Combine(shows, "Seriál S01E01 - CZ SD H264 SMALL.mkv")),
            $"batch: originals to the Recycle Bin ({removedOriginals.Count}; kept {keptOriginals.Count}: {string.Join("; ", keptOriginals)}), the library intact – {batch.RunState}");
        batch.KeepOriginal = true;
        Check(services.Settings.BatchOriginals == Core.Execution.OriginalsMode.Keep && !batch.CanDeleteOriginals, "batch: back to keeping the originals");

        // owner, 31. 10. 2026: files put into the input folder since the analysis before – their group at the end, on a pastel background
        var growing = Path.Combine(_outputDirectory, "pribudajuce");
        Directory.CreateDirectory(growing);
        var testMedia = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media");
        File.Copy(Path.Combine(testMedia, "cz-only.mkv"), Path.Combine(growing, "Prvý.Vymyslený.Film.2026.mkv"), overwrite: true);
        File.Copy(Path.Combine(testMedia, "en-cz.mp4"), Path.Combine(growing, "Druhý.Vymyslený.Film.2026.mp4"), overwrite: true);
        batch.Folder = growing;
        await batch.AnalyzeAsync();
        var firstOrder = batch.Groups.Select(g => g.Signature).ToList();
        Check(firstOrder.Count == 2 && batch.Groups.All(g => !g.IsNew), "batch: the first analysis of a folder – nothing marked new");
        var added = Path.Combine(growing, "Tretí.Vymyslený.Film.2027.mkv");
        File.Copy(Path.Combine(testMedia, "sk-cz-mixed.mkv"), added, overwrite: true);
        await batch.AnalyzeAsync();
        Check(batch.Groups.Count == 3 && batch.Groups.Take(2).Select(g => g.Signature).SequenceEqual(firstOrder) && !batch.Groups[0].IsNew && !batch.Groups[1].IsNew &&
              batch.Groups[2].IsNew && batch.Groups[2].Group.Files.Any(f => f.Path == added),
            "batch: the new file's group at the end, marked new – " + string.Join(" | ", batch.Groups.Select(g => $"{g.Title} {(g.IsNew ? "NEW" : "")}")));
        batch.SelectedFilmGroup = batch.FilmGroups.LastOrDefault();
        Check(batch.FilmGroups.Count == 3 && batch.SeriesGroups.Count == 0 && batch.SelectedGroup == batch.Groups[2] &&
              batch.SelectedFilmGroup == batch.Groups[2] && batch.SelectedSeriesGroup is null,
            $"batch: films in Skupina Film, chosen there – films {batch.FilmGroups.Count}, series {batch.SeriesGroups.Count}");
        await Screenshot(window, "batch-new-groups.png");
        await batch.AnalyzeAsync();
        Check(batch.Groups.Count == 3 && batch.Groups.All(g => !g.IsNew), "batch: analysed again with nothing new – no group marked");

        // owner, 7. 11. 2026: episodes in folders not named after their series – the groups marked together get the series
        // typed once (or found on TMDB, or by its TMDB ID) – invented names
        var scattered = Path.Combine(_outputDirectory, "roztrusene", "TVSHOWS");
        foreach (var name in new[] { "Priecinok A", "Priecinok B", "Priecinok C" })
        {
            Directory.CreateDirectory(Path.Combine(scattered, name));
            File.Copy(Path.Combine(testMedia, "cz-only.mkv"), Path.Combine(scattered, name, name + " 01.mkv"), overwrite: true);
        }

        batch.Folder = Path.GetDirectoryName(scattered)!;
        batch.Recursive = true;
        await batch.AnalyzeAsync();
        Check(batch.IsMixed && batch.SeriesGroups.Count == 3, $"batch: three series groups – {batch.SeriesGroups.Count}");
        batch.SelectedSeriesGroup = batch.SeriesGroups[0];
        batch.SelectSeriesGroups(batch.SeriesGroups.Take(2).ToList());
        Check(batch.SeriesTargetNote.Contains('2', StringComparison.Ordinal), "batch: the row Seriál: names the 2 groups marked – " + batch.SeriesTargetNote);
        batch.SeriesTitle = "Vymyslený spoločný seriál (2025) [tmdbid-424242]";
        // the episodes wait for their numbers – the series of each group is read from the row Seriál: of that group
        var seriesOfGroups = batch.SeriesGroups.Select(g => { batch.SelectedSeriesGroup = g; return batch.SeriesTitle; }).ToList();
        Check(seriesOfGroups.Take(2).All(t => t == "Vymyslený spoločný seriál (2025) [tmdbid-424242]") && seriesOfGroups[2] != seriesOfGroups[0],
            "batch: both groups marked got the series, the third did not – " + string.Join(" | ", seriesOfGroups));
        batch.SelectedSeriesGroup = batch.SeriesGroups[0];
        await Screenshot(window, "batch-series-groups.png");
        batch.SelectSeriesGroups([]);
    }

    // owner, 1. 11. 2026: Zlučovanie recognises the language of audio from its speech and of subtitles from their text,
    // with forced subtitles told by the file's name (invented speech and text)
    // owner, 2. 11. 2026 – práca na rôznych diskoch: what Windows says of the disks, and a file on another disk written into
    // the output folder on that disk (no copying between disks); invented names
    private static async Task TestDisksAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var main = services.Library!.Root;
        foreach (var path in new[] { main, Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.Windows) }.Distinct())
        {
            var disk = Core.IO.Paths.Disk(path);
            Write($"disks: {path} – {disk.Detail} ({(disk.SolidState ? "SSD" : "not SSD or unknown")}, key {disk.Key})");
        }

        await TestSuperFastAsync(services, main);

        var other = new[] { Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"C:\" }
            .FirstOrDefault(p => p.Length > 0 && Directory.Exists(p) && !Core.IO.Paths.SameVolume(p, main));
        if (other is null)
        {
            Write("disks: one volume only – the output folder on another disk is not tried");
            return;
        }

        var folder = Path.Combine(other, "UNIVERSUM-selftest-disk-" + Guid.NewGuid().ToString("N")[..8]);
        var output = Path.Combine(folder, "VYSTUP");
        var source = Path.Combine(folder, "Stiahnute", "Vymyslený film na druhom disku.mkv");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"), source);
            services.AddOutputDirectory(output);
            Check(services.LibraryFor(source)?.Root == Path.GetFullPath(output) && services.LibraryFor(Path.Combine(main, "x.mkv"))?.Root == main &&
                  Directory.Exists(Path.Combine(output, "MOVIES")),
                $"disks: a file on the other disk goes into the output folder there ({services.LibraryFor(source)?.Root})");
            model.Open(model.Rules);
            await model.Rules.LoadAsync(source);
            for (var wait = 0; wait < 600 && model.Rules.IsReadingSubtitles; wait++)
            {
                await Task.Delay(100);
            }

            model.Rules.ChooseLanguage(2, "cs");
            model.Rules.ChooseLanguage(3, "cs");
            model.Rules.KeepOriginal = true;
            model.Rules.IsMovie = true;
            model.Rules.TitleText = "Vymyslený film na druhom disku (2026)";
            Check(model.Rules.Target.StartsWith(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase), "disks: Single shows the output folder of the other disk – " + model.Rules.Target);
            var timed = System.Diagnostics.Stopwatch.StartNew();
            await model.Rules.ApplyCommand.ExecuteAsync(null);
            var written = Directory.EnumerateFiles(Path.Combine(output, "MOVIES"), "*.mkv", SearchOption.AllDirectories).ToList();
            Check(model.Rules.Result.StartsWith("Hotovo", StringComparison.Ordinal) && written.Count == 1 && File.Exists(source),
                $"disks: written on the same disk in {timed.Elapsed.TotalSeconds:0.0} s, original kept – {model.Rules.Result}");
            await Screenshot(window, "disks-single.png");

            // owner, 8. 11. 2026: the default output is never replaced without a question; a folder on another disk is offered
            // as a further output; the input folder or a folder of films is no output; Hromadné says when a disk has none
            var settings = model.Settings;
            var batch = model.Batch;
            var batchFolder = batch.Folder;
            batch.Folder = Path.GetDirectoryName(source);
            Check(!batch.HasDiskHint, "disks: Hromadné – the disk has its output folder, nothing said");
            services.RemoveOutputDirectory(output);
            Check(batch.HasDiskHint && batch.DiskHint.Contains(main, StringComparison.OrdinalIgnoreCase), "disks: Hromadné – a disk without an output folder said – " + batch.DiskHint);
            var asked = new List<string>();
            var askBefore = settings.Ask;
            settings.Ask = (question, three) =>
            {
                asked.Add(question);
                return null;
            };
            settings.ChooseOutput(output);
            Check(services.Library!.Root == main && services.Settings.OtherOutputDirectories.Count == 0 && asked.Count == 1 && settings.OutputMessage.Length > 0,
                "disks: Priečinok… on another disk asks first – cancelled, nothing changed – " + settings.OutputMessage);
            settings.Ask = (question, three) => true;
            settings.ChooseOutput(output);
            Check(services.Library!.Root == main && services.Settings.OtherOutputDirectories.Any(f => Core.IO.Paths.Same(f, output)) && !batch.HasDiskHint,
                "disks: … Áno – added as the output of the other disk, the default one kept – " + settings.OutputMessage);
            services.RemoveOutputDirectory(output);
            settings.Ask = (question, three) => false;
            settings.AddOtherOutput(Path.GetDirectoryName(source)!);
            Check(services.Settings.OtherOutputDirectories.Count == 0 && settings.OutputMessage.Length > 0,
                "disks: a folder with films is no output unless confirmed – " + settings.OutputMessage);
            if (services.InputDirectory is { Length: > 0 } input)
            {
                settings.AddOtherOutput(input);
                Check(services.Settings.OtherOutputDirectories.Count == 0 && settings.OutputMessage.Contains(input, StringComparison.OrdinalIgnoreCase),
                    "disks: the input folder is no output – " + settings.OutputMessage);
            }

            settings.Ask = (question, three) => false;
            settings.ChooseOutput(output);
            Check(Core.IO.Paths.Same(services.Library!.Root, output) && settings.OutputMessage.Contains(main, StringComparison.OrdinalIgnoreCase),
                "disks: … Nie – the default output replaced, the old one named – " + settings.OutputMessage);
            services.SetOutputDirectory(main);
            Check(Core.IO.Paths.Same(services.Library!.Root, main), "disks: the default output back");
            services.Settings.OtherOutputDirectories.Add(Path.Combine(main, "druhy-na-tom-istom-disku"));
            Check(settings.HasOutputWarning, "disks: two outputs on one disk said – " + settings.OutputWarning);
            services.Settings.OtherOutputDirectories.Clear();
            batch.Folder = batchFolder;

            // owner, 8. 11. 2026 – Priečinky na diskoch: the source folder of the other disk saved among the outputs (as the
            // owner did) is told and becomes the input of its disk; its output made with one click beside it; ★ moved and back
            var disks = settings.Disks;
            var stiahnute = Path.GetDirectoryName(source)!;
            services.AddOutputDirectory(stiahnute);
            var wrong = disks.Rows.Single(r => !r.IsMain && Core.IO.Paths.Same(r.Output!, stiahnute));
            Check(wrong.OutputLooksLikeSource && !wrong.HasInput && disks.Rows.Single(r => r.IsMain).Output == main && wrong.Disk.Length == 2 && wrong.Disk[1] == ':' &&
                  !batch.DiskHint.Contains("Volume{", StringComparison.Ordinal),
                $"disks: a source folder among the outputs is told – {wrong.Disk} {wrong.OutputText}");
            disks.OutputToInput(wrong.Row);
            var row = disks.Rows.Single(r => !r.IsMain);
            Check(row.HasInput && Core.IO.Paths.Same(row.Input!, stiahnute) && !row.HasOutput && row.NeedsOutput && row.Flow.Contains(main, StringComparison.OrdinalIgnoreCase) &&
                  services.InputDirectories.Any(i => Core.IO.Paths.Same(i, stiahnute)) && services.Settings.OtherOutputDirectories.Count == 0,
                "disks: … it is the input of its disk now, the disk says its films go to the main output – " + disks.Message + " | " + row.Flow);
            disks.CreateOutput(row.Row);
            row = disks.Rows.Single(r => !r.IsMain);
            Check(row.HasOutput && Core.IO.Paths.Same(row.Output!, output) && services.LibraryFor(source)?.Root == Path.GetFullPath(output) && !row.NeedsOutput &&
                  row.Flow.Contains(stiahnute, StringComparison.OrdinalIgnoreCase) && row.Flow.Contains(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase) &&
                  disks.Rows.Single(r => r.IsMain).MainCaption.Contains('★') && row.DiskTitle.StartsWith("DISK ", StringComparison.Ordinal) && row.DiskTitle.EndsWith(':'),
                $"disks: Vytvoriť výstup – {row.OutputText} beside the input, the film of that disk goes there – " + disks.Message);
            // owner, 9. 11. 2026: Hromadné analyses every input folder at once – the film on the other disk is found even when
            // the input of the main disk holds nothing of it; its group says its disk
            // the main disk gets an (empty) input folder of its own for the check when the self-test has none
            var inputBefore = services.Settings.InputDirectory;
            if (services.InputDirectories.Count <= 1 && !Core.IO.Paths.SameVolume(main, stiahnute))
            {
                var empty = Path.Combine(Path.GetDirectoryName(main)!, "vstup-hlavneho-disku");
                Directory.CreateDirectory(empty);
                services.Settings.OtherInputDirectories.Remove(stiahnute);
                services.SetInputDirectory(empty);
                services.SetInputFolder(stiahnute, null);
            }

            if (services.InputDirectories.Count > 1)
            {
                var batchBefore = (batch.Folder, services.Settings.BatchAllInputs);
                batch.FolderChoice = batch.FolderChoices.First(c => c.Folder is null);
                Check(batch.AllInputs && batch.FolderChoices[0].Label.Contains(Path.GetPathRoot(stiahnute)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                    "disks: Hromadné – all inputs chosen – " + string.Join(" | ", batch.FolderChoices.Select(c => c.Label)));
                await batch.AnalyzeAsync();
                var foundThere = batch.Analysis?.Groups.SelectMany(g => g.Files).Any(f => Core.IO.Paths.Same(f.Path, source)) == true;
                Check(foundThere && batch.Analysis!.Folders.Count == services.InputDirectories.Count &&
                      batch.Groups.Any(g => g.Title.EndsWith(" · " + Path.GetPathRoot(stiahnute)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)),
                    $"disks: Hromadné – the film of the other disk found among all inputs – {batch.Summary} | {string.Join(" | ", batch.Groups.Select(g => g.Title))}");
                batch.FolderChoice = batch.FolderChoices.First(c => c.Folder is { } f && Core.IO.Paths.Same(f, stiahnute));
                Check(!batch.AllInputs && Core.IO.Paths.Same(batch.Folder!, stiahnute), "disks: Hromadné – the input of one disk chosen");
                services.Settings.BatchAllInputs = batchBefore.BatchAllInputs;
                batch.Folder = batchBefore.Folder;
            }
            else
            {
                Write("disks: Hromadné – all inputs not checked (one input folder only)");
            }

            if (!Equals(services.Settings.InputDirectory, inputBefore))
            {
                var otherInputs = services.InputDirectories.Where(i => !Core.IO.Paths.Same(i, services.Settings.InputDirectory!)).ToList();
                services.Settings.OtherInputDirectories.Clear();
                services.Settings.InputDirectory = inputBefore;
                services.Settings.OtherInputDirectories.AddRange(otherInputs.Where(i => inputBefore is null || !Core.IO.Paths.Same(i, inputBefore)));
                services.Settings.Save();
            }

            disks.SetInput(row.Row, output);
            Check(disks.Rows.Single(r => !r.IsMain).Input is { } stays && Core.IO.Paths.Same(stays, stiahnute) && disks.Message.Contains(output, StringComparison.OrdinalIgnoreCase),
                "disks: an output is no input – " + disks.Message);
            disks.AddInput(Path.Combine(folder, "druhy-zdroj"));
            Check(services.InputDirectories.Count(i => Core.IO.Paths.SameVolume(i, folder)) == 1, "disks: one input per disk – " + disks.Message);
            disks.MakeMain(row.Row);
            Check(Core.IO.Paths.Same(services.Library!.Root, output) && services.Settings.OtherOutputDirectories.Any(o => Core.IO.Paths.Same(o, main)) && disks.Rows[0].IsMain && disks.Rows[0].Output == services.Library.Root,
                "disks: ★ moved to the other disk – " + disks.Message);
            disks.MakeMain(disks.Rows.Single(r => r.Output is { } o && Core.IO.Paths.Same(o, main)).Row);
            Check(Core.IO.Paths.Same(services.Library!.Root, main), "disks: ★ back – " + disks.Message);
            disks.Remove(disks.Rows.Single(r => r.IsMain).Row);
            Check(Core.IO.Paths.Same(services.Library!.Root, main) && disks.Message.Contains("★", StringComparison.Ordinal), "disks: the disk of ★ cannot be removed – " + disks.Message);
            model.Open(settings);
            await Task.Delay(300);
            await Screenshot(window, "disks-settings.png");
            disks.Remove(disks.Rows.Single(r => !r.IsMain).Row);
            Check(!services.InputDirectories.Any(i => Core.IO.Paths.SameVolume(i, folder)) && services.Settings.OtherOutputDirectories.Count == 0 && File.Exists(source),
                "disks: the disk removed, nothing in its folders touched – " + disks.Message);
            settings.Ask = askBefore;
        }
        finally
        {
            if (!Core.IO.Paths.Same(services.Library!.Root, main))
            {
                services.SetOutputDirectory(main);
            }

            services.RemoveOutputDirectory(output);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Write("disks: the test folder stays – " + ex.Message);
            }
        }
    }

    // owner, 2. 11. 2026: Superrýchle – a header-only file on the disk of the output folder is moved into the library and
    // edited there; one with a hard link (a torrent's seed) is written anew; Vrátiť puts the moved one back
    private static async Task TestSuperFastAsync(AppServices services, string main)
    {
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(main).TrimEnd('\\'))!, "UNIVERSUM-selftest-fast-" + Guid.NewGuid().ToString("N")[..8]);
        var library = new Core.Library.LibraryLayout(Path.Combine(folder, "VYSTUP"));
        try
        {
            Directory.CreateDirectory(folder);
            var film = Path.Combine(folder, "Vymyslený rýchly film.mkv");
            File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "cz-only.mkv"), film);
            Check(Core.IO.Paths.Links(film) == 1, "superfast: a file of one name – " + Core.IO.Paths.Links(film));
            var linked = Path.Combine(folder, "druhé meno.mkv");
            var made = await services.Runner.RunAsync(new Core.Processes.ProcessRequest("cmd.exe", ["/c", "mklink", "/H", linked, film]));
            Check(made.ExitCode == 0 && Core.IO.Paths.Links(film) == 2, $"superfast: a hard link is seen – {Core.IO.Paths.Links(film)} {made.StandardOutput.Trim()} {made.StandardError.Trim()}");

            var installation = services.CurrentMkvToolNix!;
            var processor = services.Processor(installation, deleteOriginals: true);
            var inspector = services.Inspector(installation);
            async Task<Core.Execution.ProcessingResult> Run(string path, string title)
            {
                var plan = Core.Planning.EditPlan.For(Core.Rules.UniversumRules.Apply(await inspector.InspectAsync(path)) with { NewTitle = title });
                var place = library.Place(plan.Target, new Core.Library.TitleInfo(MediaKind.Movie, title, 2026), null, Core.Rules.RuleProfile.Default);
                return await processor.ProcessAsync(plan, place, Core.Execution.OriginalsModes.Output(Core.Execution.OriginalsMode.MoveIntoLibrary));
            }

            var copied = await Run(film, "Vymyslený zdieľaný film");
            Check(copied.Status == Core.Execution.ProcessingStatus.Done && !copied.Messages.Contains(Core.Execution.FileProcessor.MovedIntoLibraryMessage) && File.Exists(linked),
                "superfast: a file with a hard link is written anew, never edited in place – " + string.Join("; ", copied.Messages));

            var single = Path.Combine(folder, "Vymyslený samostatný film.mkv");
            File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "cz-only.mkv"), single);
            var timed = System.Diagnostics.Stopwatch.StartNew();
            var moved = await Run(single, "Vymyslený samostatný film");
            Check(moved is { Status: Core.Execution.ProcessingStatus.Done, UndoRecordPath: not null } && moved.Messages.Contains(Core.Execution.FileProcessor.MovedIntoLibraryMessage) && !File.Exists(single),
                $"superfast: moved into the library and edited there in {timed.Elapsed.TotalSeconds:0.00} s – " + string.Join("; ", moved.Messages));
            // owner, 7. 11. 2026: Spracované tituly – the film written into the library is registered, with its name and year
            Check(services.TitleRegister.Titles.Any(t => t is { Name: "Vymyslený samostatný film", Year: 2026, Kind: MediaKind.Movie }),
                "titles: the film written into the library is in Spracované tituly – " + string.Join(" | ", services.TitleRegister.Titles.Select(t => t.Display)));

            // owner, 17. 11. 2026: názvoslovie č. 2 – the name of the scene, the folder as in č. 1
            var sceneOriginal = Path.Combine(folder, "Vymysleny.Scenovy.Film.2026.EXTENDED.1080p.BluRay.x264-Skupina.mkv");
            File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "cz-only.mkv"), sceneOriginal);
            var scenePlan = Core.Planning.EditPlan.For(Core.Rules.UniversumRules.Apply(await inspector.InspectAsync(sceneOriginal)) with { NewTitle = "Vymyslený scénový film" });
            var sceneProfile = Core.Rules.RuleProfile.Default with { Library = Core.Rules.RuleProfile.Default.Library with { Naming = Core.Library.NamingStyle.Scene } };
            var scenePlace = library.Place(scenePlan.Target, new Core.Library.TitleInfo(MediaKind.Movie, "Vymyslený scénový film", 2026), null, sceneProfile);
            var sceneResult = await processor.ProcessAsync(scenePlan, scenePlace, Core.Execution.OriginalsModes.Output(Core.Execution.OriginalsMode.Keep));
            var sceneName = Path.GetFileName(sceneResult.OutputPath ?? "");
            Check(sceneResult.Status == Core.Execution.ProcessingStatus.Done && sceneName.StartsWith("Vymysleny.scenovy.film.2026.EXTENDED.", StringComparison.Ordinal) &&
                  sceneName.Contains(".BluRay.", StringComparison.Ordinal) && sceneName.EndsWith(".CZ-Skupina.mkv", StringComparison.Ordinal) &&
                  Path.GetFileName(Path.GetDirectoryName(sceneResult.OutputPath)) == "Vymyslený scénový film (2026)",
                "naming no. 2: the name of the scene in the folder of no. 1 – " + sceneResult.OutputPath + " " + string.Join("; ", sceneResult.Messages));
            var problems = await processor.UndoAsync(moved.UndoRecordPath!);
            Check(problems.Count == 0 && File.Exists(single) && !File.Exists(moved.OutputPath!), "superfast: Vrátiť originály puts it back – " + string.Join("; ", problems));

            // owner, 7. 11. 2026: Ponechať MP4 – an MP4 moved into the library, its header edited by UNIVERSUM itself (checked
            // by its reader and ffprobe), Vrátiť originály puts it back byte for byte
            var mp4 = Path.Combine(folder, "Vymyslený film v MP4.mp4");
            File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-cz.mp4"), mp4);
            var mp4Bytes = File.ReadAllBytes(mp4);
            var keepMp4 = services.KeepMp4;
            services.KeepMp4 = true;
            try
            {
                Check(services.Profile.KeepMp4, "MP4: Ponechať MP4 is a part of the profile");
                var mp4Plan = Core.Planning.EditPlan.For(Core.Rules.UniversumRules.Apply(await inspector.InspectAsync(mp4), services.Profile) with { NewTitle = "Vymyslený film v MP4" });
                Check(mp4Plan.Target.StaysMp4 && mp4Plan.Method == Core.Planning.WriteMethod.HeaderEdit, $"MP4: stays MP4, a header edit – {mp4Plan.Method} {mp4Plan.Target.Mp4Obstacle}");
                var mp4Place = library.Place(mp4Plan.Target, new Core.Library.TitleInfo(MediaKind.Movie, "Vymyslený film v MP4", 2026), null, services.Profile);
                var mp4Timed = System.Diagnostics.Stopwatch.StartNew();
                var edited = await processor.ProcessAsync(mp4Plan, mp4Place, Core.Execution.OriginalsModes.Output(Core.Execution.OriginalsMode.MoveIntoLibrary));
                Check(edited is { Status: Core.Execution.ProcessingStatus.Done, UndoRecordPath: not null } && edited.OutputPath!.EndsWith(".mp4", StringComparison.Ordinal) &&
                      edited.Messages.Contains(Core.Execution.FileProcessor.MovedIntoLibraryMessage) && !File.Exists(mp4),
                    $"MP4: moved into the library and its header edited in {mp4Timed.Elapsed.TotalSeconds:0.00} s (ffprobe: {edited.Messages.Contains(Core.Execution.FileProcessor.Mp4ProbedMessage)}) – " + string.Join("; ", edited.Messages));
                var read = Core.Mp4.Mp4Reader.Read(edited.OutputPath!);
                Check(read.Title == "Vymyslený film v MP4" && read.Tracks.Count(t => t.Kind == Core.Mp4.Mp4TrackKind.Audio && t.Enabled) == 1 &&
                      read.Tracks.First(t => t.Kind == Core.Mp4.Mp4TrackKind.Audio) is { Language: "ces", Enabled: true },
                    "MP4: the title and the Czech sound first and default – " + string.Join(", ", read.Tracks.Select(t => $"{t.Id} {t.Language} {t.Enabled}")));
                var undone = await processor.UndoAsync(edited.UndoRecordPath!);
                Check(undone.Count == 0 && File.Exists(mp4) && File.ReadAllBytes(mp4).AsSpan().SequenceEqual(mp4Bytes) && !File.Exists(edited.OutputPath!),
                    "MP4: Vrátiť originály puts it back byte for byte – " + string.Join("; ", undone));
            }
            finally
            {
                services.KeepMp4 = keepMp4;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Write("superfast: the test folder stays – " + ex.Message);
            }
        }
    }
}
