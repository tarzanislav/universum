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

// the self-test: Editácia hlavičky, MEGA, Zlučovanie, kapitoly, SRT, the processing logs
internal static partial class SelfTest
{
    /// <summary>Batch page: three episodes of one structure, a clean file; group decision, series title, run into the library.</summary>
    /// <summary>
    /// Header editor: check panel; the result is a new file in the library named by the rules and the original stays
    /// byte for byte; a library file edited again can be undone; rules with one click; moving tracks; the queue.
    /// </summary>
    private static async Task TestHeaderEditorAsync(MainViewModel model, MainWindow window, AppServices services, string work)
    {
        var copy = Path.Combine(work, "header.mkv");
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"), copy, overwrite: true);
        var originalBytes = File.ReadAllBytes(copy);
        var inspector = new MkvMergeInspector(services.CurrentMkvToolNix!, services.Runner);
        var editor = model.HeaderEditor;
        model.SelectedTool = model.Tools.First(t => t.Page == editor);
        await editor.LoadAsync(copy);
        editor.Library.IsMovie = true;
        editor.Library.TitleText = "Hlavička test (2026)";
        Check(editor.Rows.Count == 6, $"header editor: 6 tracks ({editor.Rows.Count})");
        Check(editor.Findings.Any(f => f.Contains("neurčený", StringComparison.Ordinal)), "header editor: check panel shows the undetermined language");
        // owner, 29. 10. 2026: the File title of the file is removed by default – that alone is a change to save
        Check(editor.Title.Length == 0 && editor.SaveCommand.CanExecute(null), "header editor: the File title is removed by default");

        editor.Rows[2].Language = editor.Rows[2].LanguageChoices.First(c => c.Tag.Primary == "cs");
        editor.Rows[1].Name = "Originál";
        editor.Title = "Vymyslený film";
        editor.SelectedRow = editor.Rows[2];
        Check(editor.Library.Placement is not null && editor.Library.Description.StartsWith(Path.Combine("MOVIES", "Hlavička test (2026)"), StringComparison.Ordinal),
            "header editor: result goes into the library – " + editor.Library.Description);
        await Screenshot(window, "header-editor.png");
        await editor.SaveCommand.ExecuteAsync(null);
        Check(editor.Result.StartsWith("Uložené do knižnice", StringComparison.Ordinal), "header editor: saved – " + editor.Result);
        Check(File.ReadAllBytes(copy).AsSpan().SequenceEqual(originalBytes), "header editor: original unchanged, byte for byte");
        var result = editor.FilePath!;
        var edited = await inspector.InspectAsync(result);
        Check(services.Library!.Contains(result) && Path.GetFileName(result).StartsWith("Hlavička test (2026) - ", StringComparison.Ordinal),
            "header editor: new file named by the rules – " + Path.GetFileName(result));
        Check(edited.Title == "Vymyslený film" && edited.Tracks[2].Language.Primary == "cs" && edited.Tracks[1].Name == "Originál", "header editor: the library file has the changes");

        // the library file edited again: in place, with undo
        editor.Rows[1].Name = "Druhá úprava";
        await editor.SaveCommand.ExecuteAsync(null);
        Check(editor.UndoCommand.CanExecute(null), "header editor: a library file edited again can be undone – " + editor.Result);
        await editor.UndoCommand.ExecuteAsync(null);
        Check((await inspector.InspectAsync(editor.FilePath!)).Tracks[1].Name == "Originál", "header editor: undo – " + editor.Result);

        await editor.LoadAsync(copy);
        editor.Library.TitleText = "Hlavička test (2026)";
        editor.Rows[2].Language = editor.Rows[2].LanguageChoices.First(c => c.Tag.Primary == "cs");
        editor.Rows[3].Language = editor.Rows[3].LanguageChoices.First(c => c.Tag.Primary == "cs");
        editor.ApplyRules();
        Check(editor.Rows[1].TrackId == 2 && editor.Rows[1].IsDefault, "header editor: rules put the Czech audio first, default");
        Check(editor.Method.StartsWith("Zapíše sa nový súbor", StringComparison.Ordinal), "header editor: new order needs a new file – " + editor.Method);
        Check(editor.Findings.Count == 0, "header editor: nothing to check after the rules – " + string.Join(" | ", editor.Findings));
        await Screenshot(window, "header-editor-rules.png");
        editor.SelectedRow = editor.Rows[1];
        Check(!editor.MoveUpCommand.CanExecute(null), "header editor: audio cannot move above video");
        editor.Move(+1);
        Check(editor.Rows[1].TrackId == 1 && editor.Rows[2].TrackId == 2, "header editor: audio moved among audio");
        Check(editor.Findings.Any(f => f.StartsWith("Poradie zvuku", StringComparison.Ordinal)), "header editor: check panel notices the order");

        // the same change through the job queue: a new file in the library, output with the job's messages
        editor.QueueCommand.Execute(null);
        Check(editor.Result.StartsWith("Pridané do fronty", StringComparison.Ordinal), "queue: header editor adds a job – " + editor.Result);
        var job = (Core.Jobs.FileJob)services.Jobs.Jobs[^1];
        for (var i = 0; i < 300 && !job.IsFinished; i++)
        {
            await Task.Delay(100);
        }

        for (var i = 0; i < 50 && !editor.Result.StartsWith($"Úloha {job.Id}", StringComparison.Ordinal); i++)
        {
            await Task.Delay(100);
        }

        await Settle();
        Check(job.Status == Core.Jobs.JobStatus.Done, $"queue: job done – {job.Status} {job.Outcome} {string.Join(" | ", job.Messages.Select(m => m.Text))}");
        Check(File.ReadAllBytes(copy).AsSpan().SequenceEqual(originalBytes), "queue: original unchanged, byte for byte");
        if (job.Result?.OutputPath is { } queuedPath)
        {
            var queued = await inspector.InspectAsync(queuedPath);
            Check(services.Library!.Contains(queuedPath) && queued.Tracks[1].Language.Primary == "en" && queued.Tracks[2].Language.Primary == "cs", "queue: job wrote the library file");
        }

        Check(editor.Result.StartsWith($"Úloha {job.Id}", StringComparison.Ordinal), "queue: editor shows the job's outcome – " + editor.Result);
        model.SelectedTool = model.Tools.First(t => t.Page == model.Queue);
        model.Queue.SelectedRow = model.Queue.Rows.First(r => r.Job == job);
        await Screenshot(window, "queue.png");
        model.Queue.OutputCommand.Execute(null);
        Check(model.SelectedTool.Page == model.Output && model.Output.Messages.Count > 0, "output: the job's messages are shown");
        await Screenshot(window, "job-output.png");
    }

    /// <summary>
    /// Multiplexer: a film and a subtitle file (asked how to add them), a track left out, the rules over both; the output
    /// beside the source by default, then into the library as a queue job. Then tabs: new settings for each file,
    /// an appended file, closing with a question, and the Information page with the files in work.
    /// </summary>
    /// <summary>
    /// MEGA spracovanie from Zlučovanie and Single spracovanie (owner, 1. 11. 2026): a merged result previewed in the folder
    /// of previews (never the output), played in UNIVERSUM's player, sent to MEGA; a file of Single spracovanie added to
    /// the same batch; MEGA writes both – the original of Single kept – and the preview goes with its tab. Invented names.
    /// </summary>
    private static async Task TestMegaSourcesAsync(MainViewModel model, MainWindow window, AppServices services, string work)
    {
        var film = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv");
        var srt = Path.Combine(work, "mega-titulky.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nVymyslený titulok pre MEGA.\n\n");
        var mux = model.Mux;
        model.Open(mux);
        mux.NewSettingsCommand.Execute(null);
        var set = mux.Current;
        mux.AskHowToAdd = request => new MuxAddChoice(MuxAddAction.AddToCurrent, null, false);
        await mux.AddFilesAsync([film, srt]);
        var (dub, forcedRow, srtRow) = (set.Tracks[2], set.Tracks[3], set.Tracks[^1]);
        dub.Language = dub.LanguageChoices.First(c => c.Tag.Primary == "cs");
        forcedRow.Language = forcedRow.LanguageChoices.First(c => c.Tag.Primary == "cs");
        srtRow.Language = srtRow.LanguageChoices.First(c => c.Tag.Primary == "sk");
        set.ApplyRules();
        set.Library.IsMovie = true;
        set.Library.TitleText = "Zlúčený pre MEGA (2026)";
        set.UseLibrary = true;
        Check(set.PreviewCommand.CanExecute(null) && set.SendToMegaCommand.CanExecute(null) && !set.HasPreview, "mega/mux: a preview and MEGA offered – " + set.Problems);

        // the test film is short: the whole film (the parts are tried by the core tests)
        set.PreviewRange = MuxSettingsViewModel.PreviewRanges.First(r => r.Value == Core.Muxing.PreviewRange.Whole);
        await set.PreviewAsync();
        var preview = set.PreviewPath;
        Check(preview is not null && string.Equals(Path.GetDirectoryName(preview), Path.GetFullPath(services.PreviewFolder), StringComparison.OrdinalIgnoreCase) &&
              !preview.StartsWith(services.Library!.Root, StringComparison.OrdinalIgnoreCase) && !set.PreviewIsOld && !File.Exists(set.OutputPath),
            $"mega/mux: the preview in the folder of previews, nothing in the output – {preview} · {set.PreviewText}");
        string? played = null;
        set.PlayRequested = path => played = path;
        set.PlayPreviewCommand.Execute(null);
        Check(played == preview, "mega/mux: Prehrať náhľad opens the preview");
        var player = new PreviewViewModel(services, preview!);
        var playerWindow = new PreviewWindow(player) { Owner = window, Width = 1000, Height = 680 };
        playerWindow.Show();
        await WaitFor(() => player.Subtitles.Count > 1);
        Check(player.Subtitles.Count >= 2 && player.Subtitles.Any(s => s.Label.Contains("(sk)", StringComparison.Ordinal)),
            "mega/mux: the preview in UNIVERSUM's player with the subtitles of the result – " + string.Join(" | ", player.Subtitles.Select(s => s.Label)));
        await Screenshot(playerWindow, "mux-preview.png");
        playerWindow.Close();
        set.Title = "Zmenený vymyslený názov";
        Check(set.PreviewIsOld, "mega/mux: the settings changed since the preview");
        set.Title = "";
        Check(!set.PreviewIsOld, "mega/mux: the same again");
        await Screenshot(window, "mux-mega.png");

        var before = services.Mega.Batches.Count;
        set.Mega.Selected = set.Mega.Targets[0];
        set.SendToMegaCommand.Execute(null);
        var batch = set.Mega.Last;
        Check(batch is not null && services.Mega.Batches.Count == before + 1 && batch.Files.Single() is { Source: Core.Mega.MegaSource.Mux, KeepOriginal: true, Mux.Inputs.Count: 2 } &&
              set.Result.Contains(batch.Label, StringComparison.Ordinal), "mega/mux: the result sent to a new batch – " + set.Result);

        // Single spracovanie: a file with its decisions added to the same batch
        var single = Path.Combine(work, "Vymyslený film pre MEGA.mkv");
        File.Copy(film, single, overwrite: true);
        model.Open(model.Rules);
        await model.Rules.LoadAsync(single);
        for (var wait = 0; wait < 600 && model.Rules.IsReadingSubtitles; wait++)
        {
            await Task.Delay(100);
        }

        model.Rules.ChooseLanguage(2, "cs");
        model.Rules.ChooseLanguage(3, "cs");
        model.Rules.IsMovie = true;
        model.Rules.TitleText = "Vymyslený film pre MEGA (2026)";
        model.Rules.Mega.Selected = model.Rules.Mega.Targets.First(t => t.Batch == batch);
        Check(model.Rules.SendToMegaCommand.CanExecute(null), "mega/single: Odoslať do MEGA offered – " + model.Rules.Target);
        model.Rules.SendToMegaCommand.Execute(null);
        var singleFile = batch!.Files.FirstOrDefault(f => f.Source == Core.Mega.MegaSource.Single);
        Check(batch.Files.Count == 2 && singleFile is { KeepOriginal: true } && singleFile.Languages.TryGetValue(2, out var chosen) && chosen.Primary == "cs" && singleFile.Title.Name == "Vymyslený film pre MEGA",
            "mega/single: the file added to the batch with its decisions – " + model.Rules.Result);
        model.Open(model.Mega);
        model.Mega.Selected = model.Mega.Rows.First(r => r.Batch == batch);
        Check(model.Mega.Files.Select(f => f.Source).Order().SequenceEqual(["Single spracovanie", "Zlučovanie"]) && !model.Mega.EditCommand.CanExecute(null),
            "mega: where the files came from; nothing to open in Hromadné spracovanie");
        await Screenshot(window, "mega-sources.png");

        // owner, 2. 11. 2026: the file of Single spracovanie edited there again (with its decisions) and saved back into its batch
        model.Mega.SelectedFile = model.Mega.Files.First(f => f.File.Source == Core.Mega.MegaSource.Single);
        Check(model.Mega.EditFileCommand.CanExecute(null), "mega: Upraviť v Single spracovaní offered for the file of Single");
        model.Mega.EditFileCommand.Execute(null);
        for (var wait = 0; wait < 600 && (model.Rules.MegaBatch is null || model.Rules.IsReadingSubtitles || model.Rules.Plan is null); wait++)
        {
            await Task.Delay(100);
        }

        Check(model.SelectedTool.Page == model.Rules && model.Rules.MegaBatch == batch && model.Rules.TitleText == "Vymyslený film pre MEGA (2026)" &&
              model.Rules.SendToMegaText.Contains(batch.Label, StringComparison.Ordinal) && model.Rules.Plan?.Target.For(2).Language.Primary == "cs",
            $"mega/single: the file opened in Single spracovanie with its decisions – {model.Rules.TitleText} · {model.Rules.SendToMegaText} · {model.Rules.Result}");
        Check(model.Rules.EditInStudioCommand.CanExecute(null) && model.Rules.EditSoundInStudioCommand.CanExecute(null) && model.Rules.EditPictureInStudioCommand.CanExecute(null),
            "mega/single: the studios offered from Single spracovanie");
        await Screenshot(window, "mega-edit-single.png");
        // what a studio saves comes back into Single spracovanie (here: the English subtitles left out in Úprava titulkostopy)
        var refused = model.Rules.SaveStudio(single, new Core.Batch.StudioEdits { Removed = [5] });
        Check(refused is null && model.Rules.Plan?.Target.Tracks.All(t => t.TrackId != 5) == true && model.Rules.StudioText.Length > 0,
            $"mega/single: a studio's edits back in Single spracovanie – {refused} {model.Rules.StudioText}");
        // owner, 2. 11. 2026: Obraz v štúdiu – the proportions chosen in Úprava videostopy, back with Uložiť do úlohy (the
        // subtitles' edits stay)
        model.Rules.EditPictureInStudioCommand.Execute(null);
        await WaitFor(() => model.Video.IsTask && model.SelectedTool.Page == model.Video && model.Video.IsVideo);
        model.Video.Aspect = model.Video.Aspects.First(a => a.Value == "4/3");
        await Screenshot(window, "studio-video-single.png");
        model.Video.SaveToTaskCommand.Execute(null);
        Check(model.SelectedTool.Page == model.Rules && model.Rules.Plan?.Target.Tracks.Single(t => t.Type == TrackType.Video).AspectRatio == "4/3" &&
              model.Rules.Plan.Target.Tracks.All(t => t.TrackId != 5) && model.Rules.StudioText.Contains("4:3", StringComparison.Ordinal),
            $"mega/single: Obraz v štúdiu → Uložiť do úlohy – the proportions back in Single spracovanie, the subtitles' edits stay – {model.Rules.StudioText}");
        model.Rules.Addition = "Remaster";
        model.Rules.SendToMegaCommand.Execute(null);
        var edited = batch.Files.Where(f => f.Source == Core.Mega.MegaSource.Single).ToList();
        Check(batch.Files.Count == 2 && edited is [{ Addition: "Remaster" } one] && one.Languages.TryGetValue(2, out var kept) && kept.Primary == "cs" &&
              one.Studio is { Removed: [5], Video.Aspect: "4/3" } && one.Removed.Contains(5) && one.VideoAspect == "4/3" &&
              model.SelectedTool.Page == model.Mega && model.Rules.MegaBatch is null,
            "mega/single: the changes saved back into the batch, back in MEGA spracovanie – " + model.Rules.Result);

        // MEGA writes both
        await model.Mega.StartAsync(batch);
        for (var wait = 0; wait < 600 && model.Mega.LastJob is { IsFinished: false }; wait++)
        {
            await Task.Delay(100);
        }

        Check(batch.Files.All(f => f.State == Core.Mega.MegaFileState.Done && f.ResultPath is { } result && File.Exists(result) && result.StartsWith(services.Library!.Root, StringComparison.OrdinalIgnoreCase)) &&
              File.Exists(single) && File.Exists(film),
            "mega: the result of Zlučovanie and the file of Single written into the library, originals kept – " + string.Join(" | ", batch.Files.Select(f => $"{f.State}: {f.Note}")));

        // the tab closed: its preview is deleted (UNIVERSUM's own temporary file)
        mux.Confirm = _ => true;
        mux.CloseSettingsCommand.Execute(set);
        Check(!File.Exists(preview), "mega/mux: the preview went with its tab");
        services.Mega.Remove(batch);
        services.NotifyMegaChanged();
    }

    private static async Task TestMultiplexerAsync(MainViewModel model, MainWindow window, AppServices services, string work)
    {
        var srt = Path.Combine(work, "titulky-sk.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nVymyslený slovenský titulok.\n\n");
        var film = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv");
        var mux = model.Mux;
        MuxAddRequest? asked = null;
        var answer = MuxAddAction.AddToCurrent;
        var newForVideo = false;
        mux.AskHowToAdd = request =>
        {
            asked = request;
            return new MuxAddChoice(answer, answer is MuxAddAction.Append or MuxAddAction.AdditionalParts ? request.Sources[0] : null, newForVideo);
        };
        string? confirmed = null;
        var confirm = false;
        mux.Confirm = message =>
        {
            confirmed = message;
            return confirm;
        };
        model.SelectedTool = model.Tools.First(t => t.Page == mux);
        await mux.AddFilesAsync([film, srt]);
        var set = mux.Current;
        Check(asked is { Files.Count: 2 } && mux.Settings.Count == 1, $"mux: asked how to add two files ({asked?.Files.Count}), one settings ({mux.Settings.Count})");
        Check(set.Inputs.Count == 2 && set.Tracks.Count == 7 && set.SourceRows.Count == 2, $"mux: two inputs, 7 tracks ({set.Inputs.Count}, {set.Tracks.Count}) {set.Result}");
        Check(!set.UseLibrary && set.OutputEditable && set.OutputPath == Path.Combine(Path.GetDirectoryName(film)!, "en-und-cz-forced (1).mkv"),
            "mux: the output is beside the source by default, as in MKVToolNix – " + set.OutputPath);
        LanguageChoice Lang(MuxTrackRow row, string code) => row.LanguageChoices.First(c => c.Tag.Primary == code);
        // the rows held before: a language set moves its track among the tracks of its type (owner, 1. 11. 2026)
        var (dub, forcedRow, englishSubtitles, srtRow) = (set.Tracks[2], set.Tracks[3], set.Tracks[5], set.Tracks[6]);
        dub.Language = Lang(dub, "cs");
        Check(set.Tracks.IndexOf(dub) == 1, "mux: the Czech dub moved above the English audio as soon as its language was set");
        forcedRow.Language = Lang(forcedRow, "cs");
        srtRow.Language = Lang(srtRow, "sk");
        Check(set.Tracks.IndexOf(srtRow) == 5 && set.Tracks.IndexOf(englishSubtitles) == 6, "mux: the Slovak subtitles moved above the English ones");
        englishSubtitles.Include = false;
        set.ApplyRules();
        set.Library.IsMovie = true;
        set.Library.TitleText = "Zlúčený film (2026)";
        Check(set.Findings.Count == 0, "mux: nothing to check after the rules – " + string.Join(" | ", set.Findings));
        Check(Path.GetDirectoryName(set.OutputPath) == Path.GetDirectoryName(film) &&
              Path.GetFileName(set.OutputPath).StartsWith("Zlúčený film (2026) - CZ EN", StringComparison.Ordinal),
            "mux: with a title the name follows the rules, still beside the source – " + set.OutputPath);
        Check(set.CanUseLibrary, "mux: the library can be used");
        set.UseLibrary = true;
        Check(!set.OutputEditable && set.StartCommand.CanExecute(null), "mux: into the library, the path is greyed out, ready to write – " + set.Problems);
        Check(set.Library.Description.StartsWith(Path.Combine("MOVIES", "Zlúčený film (2026)", "Zlúčený film (2026) - CZ EN"), StringComparison.Ordinal),
            "mux: name by the rules – " + set.Library.Description);
        set.SelectedTrack = set.Tracks.First(t => t.Kind == TrackType.Audio);
        set.ShowAdvanced = true;
        Check(set.ShowsTrack && !set.ShowsSource, "mux: the properties show the selected track");
        await Screenshot(window, "mux.png");
        set.SelectedSource = set.SourceRows[0];
        Check(set.ShowsSource && set.SelectedTrack is null, "mux: the properties show the selected source file");
        await Screenshot(window, "mux-source.png");
        set.StartCommand.Execute(null);
        Check(!set.IsUnprocessed, "mux: in the queue, nothing unprocessed");
        var job = (Core.Jobs.MuxJob)services.Jobs.Jobs[^1];
        for (var i = 0; i < 300 && !job.IsFinished; i++)
        {
            await Task.Delay(100);
        }

        await Settle();
        Check(job.Status == Core.Jobs.JobStatus.Done, $"mux: job done – {job.Status} {job.Outcome} {string.Join(" | ", job.Messages.Select(m => m.Text))}");
        if (job.Result?.OutputPath is { } output)
        {
            var written = await new MkvMergeInspector(services.CurrentMkvToolNix!, services.Runner).InspectAsync(output);
            Check(services.Library!.Contains(output) && string.Join(",", written.Tracks.Select(t => t.Language.Primary)) == "und,cs,en,cs,cs,sk",
                "mux: written into the library in the rules' order – " + output + " " + string.Join(",", written.Tracks.Select(t => t.Language.Primary)));
        }

        Check(set.Result.StartsWith($"Úloha {job.Id}", StringComparison.Ordinal) && set.Progress >= 1, $"mux: the tab shows the outcome and 100 % – {set.Result} {set.Progress:P0}");
        Check(mux.QueueStatus.Contains("dokončené", StringComparison.Ordinal), "mux: the status line shows the queue – " + mux.QueueStatus);

        // tabs: new settings for each file (the subtitles get their own)
        mux.NewSettingsCommand.Execute(null);
        Check(mux.Settings.Count == 2 && mux.Current != set && mux.Current.Inputs.Count == 0, "mux: new empty settings as a second tab");
        answer = MuxAddAction.NewSettingsForEach;
        asked = null;
        var copy = Path.Combine(work, "film-časť-2.mkv");
        File.Copy(film, copy, overwrite: true);
        await mux.AddFilesAsync([film, srt]);
        Check(asked is not null && mux.Settings.Count == 3 && mux.Settings[1].Inputs.Count == 1 && mux.Settings[2].Inputs.Count == 1,
            $"mux: new settings for each file – {mux.Settings.Count} tabs");

        // a file appended to the film (it continues its tracks)
        mux.Current = mux.Settings[1];
        answer = MuxAddAction.Append;
        await mux.AddFilesAsync([copy]);
        var appended = mux.Settings[1];
        Check(appended.Inputs.Count == 1 && appended.Inputs[0].Appended.Count == 1 && appended.SourceRows.Count == 2 &&
              appended.SourceRows[1].Kind == MuxSourceKind.Appended && appended.Spec().Validate().Count == 0,
            "mux: a file appended to the film – " + string.Join(" | ", appended.Spec().Validate()));
        Check(appended.IsUnprocessed && mux.Unprocessed.Count == 2, $"mux: two settings not yet in the queue ({mux.Unprocessed.Count})");

        // the appended file as MKVToolNix GUI shows it: its own colour, its tracks under the tracks they continue, not editable
        var main = appended.Inputs[0];
        var children = appended.TrackRows.OfType<MuxAppendedTrackRow>().ToList();
        Check(appended.TrackRows.Count == appended.Tracks.Count * 2 && children.Count == appended.Tracks.Count &&
              appended.TrackRows[1] is MuxAppendedTrackRow { IsMain: false, IsMismatch: false } first && first.Parent == appended.Tracks[0],
            $"mux: the appended file's tracks under the main tracks ({appended.TrackRows.Count} rows)");
        Check(main.ColorIndex == 0 && appended.SourceRows[1].ColorIndex == 1 && children.All(c => c.ColorIndex == 1) &&
              appended.Tracks.All(t => t.ColorIndex == 0) && appended.SourceRows[1].IsChild,
            "mux: the main file green (colour 0), the appended one blue (colour 1)");
        appended.SelectedTrackItem = children[0];
        children[0].Include = false;
        Check(appended.ShowsAppendedTrack && !appended.ShowsTrack && appended.Tracks[0].Include && appended.SelectMainTrackCommand.CanExecute(null),
            "mux: a track of the appended file is not editable – " + children[0].Explanation);
        await Screenshot(window, "mux-appended.png");
        appended.SelectMainTrackCommand.Execute(null);
        Check(appended.SelectedTrack == appended.Tracks[0] && appended.ShowsTrack, "mux: from the appended track to its main track");
        appended.MoveDownCommand.Execute(null);
        Check(appended.Tracks[1] == children[0].Parent && appended.TrackRows[2] == children[0].Parent && appended.TrackRows[3] == children[0] &&
              appended.SelectedTrack == children[0].Parent,
            "mux: a moved track takes the appended track along and stays selected");
        appended.MoveUpCommand.Execute(null);

        // the source list's menu: take every track of the file, name the output by it, append without being asked
        appended.Tracks[1].Include = false;
        appended.SelectedSource = appended.SourceRows[0];
        appended.TakeAllTracksOfSourceCommand.Execute(null);
        Check(appended.Tracks.All(t => t.Include), "mux menu: Vybrať všetky stopy zo súboru");
        appended.OutputFromSourceCommand.Execute(null);
        Check(appended.OutputPath == Path.Combine(Path.GetDirectoryName(film)!, "en-und-cz-forced (1).mkv") && appended.OutputNote == "Zadané ručne.",
            "mux menu: the output named by the selected file, never over the source – " + appended.OutputPath);
        appended.SelectedSource = appended.SourceRows[1];
        appended.OutputFromSourceCommand.Execute(null);
        Check(appended.OutputPath == Path.Combine(Path.GetDirectoryName(film)!, "film-časť-2.mkv"),
            "mux menu: named by the appended file, in the output's folder – " + appended.OutputPath);
        Check(appended.SourceInfoCommand.CanExecute(null) && appended.AppendFilesCommand.CanExecute(null) && appended.AddPartsCommand.CanExecute(null),
            "mux menu: the commands of a selected file are available");
        await Screenshot(window, "mux-tabs.png");

        // closing asks; "no" keeps the settings
        mux.CloseSettingsCommand.Execute(appended);
        Check(confirmed?.Contains("boli zmenené", StringComparison.Ordinal) == true && mux.Settings.Contains(appended), "mux: closing changed settings asks first – " + confirmed);

        // the Information page lists the files in work and shows the chosen one
        appended.SelectedSource = appended.SourceRows[1];
        mux.ShowInfoCommand.Execute(null);
        for (var i = 0; i < 100 && model.Info.Tracks.Count == 0; i++)
        {
            await Task.Delay(100);
        }

        Check(model.SelectedTool.Page == model.Info && model.Info.KnownFiles.Count >= 3 && model.Info.Tracks.Count > 0 &&
              model.Info.SelectedKnown?.Path == copy,
            $"info: the multiplexer's files are listed and the chosen one is shown ({model.Info.KnownFiles.Count}, {model.Info.Tracks.Count})");
        await Screenshot(window, "info-files.png");

        // "video always into new settings": the subtitles go with their film even when picked first
        mux.NewSettingsCommand.Execute(null);
        var subtitlesOfCopy = Path.Combine(work, "film-časť-2.cz.srt");
        File.Copy(srt, subtitlesOfCopy, overwrite: true);
        answer = MuxAddAction.NewSettingsForEach;
        newForVideo = true;
        var before = mux.Settings.Count;
        await mux.AddFilesAsync([subtitlesOfCopy, copy]);
        var withFilm = mux.Settings.FirstOrDefault(s => s.Inputs.Any(i => i.File.Path == copy) && s.Inputs.Count == 2);
        Check(mux.Settings.Count == before && withFilm is not null && withFilm.Inputs[0].File.Path == copy,
            $"mux: the subtitles go with their film whatever the order ({mux.Settings.Count} tabs, {withFilm?.Inputs.Count} inputs)");
        newForVideo = false;

        // a typed output stays; the library replaces it while ticked; a delay must be a number
        if (withFilm is not null)
        {
            var typed = Path.Combine(work, "ručne pomenovaný.mkv");
            withFilm.OutputPath = typed;
            Check(withFilm.OutputNote == "Zadané ručne." && withFilm.StartCommand.CanExecute(null), "mux: a typed output path – " + withFilm.Problems);
            withFilm.UseLibrary = true;
            Check(withFilm.OutputPath != typed && !withFilm.Problems.Contains(Core.Muxing.MuxSpec.MissingOutput, StringComparison.Ordinal) &&
                  withFilm.Problems.Length > 0 && !withFilm.StartCommand.CanExecute(null),
                "mux: into the library without a title asks for the title – " + withFilm.Problems);
            withFilm.UseLibrary = false;
            Check(withFilm.OutputPath == typed, "mux: unticking the library returns the typed path – " + withFilm.OutputPath);
            withFilm.Tracks[1].Delay = "1,5";
            Check(withFilm.Problems.Contains("oneskorenie", StringComparison.Ordinal) && !withFilm.StartCommand.CanExecute(null), "mux: a delay that is not a number stops writing");
            withFilm.Tracks[1].Delay = "250 ms";
            Check(!withFilm.Problems.Contains("oneskorenie", StringComparison.Ordinal) && withFilm.Spec().Tracks.Any(t => t.Options.DelayMs == 250), "mux: a delay in ms is taken – " + withFilm.Problems);

            // menu: Pridať súbory… adds to these settings without asking; Pripojiť súbory… appends to the selected file
            var extraSubtitles = Path.Combine(work, "doplnok.cz.srt");
            File.Copy(srt, extraSubtitles, overwrite: true);
            asked = null;
            mux.PickFiles = _ => [extraSubtitles];
            await withFilm.AddSourcesCommand.ExecuteAsync(null);
            Check(asked is null && withFilm.Inputs.Count == 3 && withFilm.Inputs[2].File.Path == extraSubtitles, $"mux menu: Pridať súbory… ({withFilm.Inputs.Count} sources, not asked)");
            var secondPart = Path.Combine(work, "film-časť-3.mkv");
            File.Copy(film, secondPart, overwrite: true);
            mux.PickFiles = _ => [secondPart];
            withFilm.SelectedSource = withFilm.SourceRows[0];
            await withFilm.AppendFilesCommand.ExecuteAsync(null);
            Check(asked is null && withFilm.Inputs[0].Appended.Count == 1 && withFilm.SourceRows[1].Kind == MuxSourceKind.Appended &&
                  withFilm.SourceRows[1].ColorIndex == 1 && withFilm.Inputs[1].ColorIndex == 2,
                "mux menu: Pripojiť súbory… appends to the selected file (the next files get the next colours)");
            mux.PickFiles = _ => null;
            withFilm.RemoveAllSourcesCommand.Execute(null);
            Check(withFilm.Inputs.Count == 0 && withFilm.Tracks.Count == 0 && withFilm.TrackRows.Count == 0 && withFilm.SourceRows.Count == 0 && !withFilm.IsUnprocessed,
                "mux menu: Odstrániť všetky súbory");
        }

        // the add dialog itself (shown, not answered)
        var dialog = new MuxAddFilesWindow(new MuxAddFilesModel(new MuxAddRequest([film, srt], appended.Inputs.ToList()))) { Owner = window };
        dialog.Show();
        await Screenshot(dialog, "mux-add-files.png");
        dialog.Close();

        // owner, 7. 11. 2026: Ponechať MP4 – one MP4 with all its tracks, only header values changed, written as MP4 by
        // UNIVERSUM (checked by its reader and ffprobe); a track left out makes an MKV, and the note says why
        var mp4Source = Path.Combine(work, "Vymyslený film na zlúčenie.mp4");
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-cz.mp4"), mp4Source, overwrite: true);
        mux.NewSettingsCommand.Execute(null);
        var mp4Set = mux.Current;
        answer = MuxAddAction.AddToCurrent;
        newForVideo = false;
        await mux.AddFilesAsync([mp4Source]);
        var keepMp4 = services.KeepMp4;
        services.KeepMp4 = true;
        try
        {
            mp4Set.Title = "Vymyslený titul";
            Check(mp4Set.IsMp4 && mp4Set.Mp4Note.StartsWith("Výsledok ostane MP4", StringComparison.Ordinal) && mp4Set.OutputPath.EndsWith(".mp4", StringComparison.Ordinal),
                "mux: an MP4 stays MP4 – " + mp4Set.Mp4Note + " | " + mp4Set.OutputPath + " | " + mp4Set.Problems);
            var kept = await services.Multiplexer(services.CurrentMkvToolNix!).MuxAsync(mp4Set.Spec());
            Check(kept is { Status: Core.Muxing.MuxStatus.Done } && kept.OutputPath!.EndsWith(".mp4", StringComparison.Ordinal) &&
                  kept.Messages.Contains(Core.Mp4.Mp4Editing.KeptMessage) && Core.Mp4.Mp4Reader.Read(kept.OutputPath).Title == "Vymyslený titul",
                $"mux: written as MP4 (ffprobe: {kept.Messages.Contains(Core.Mp4.Mp4Editing.ProbedMessage)}) – " + string.Join("; ", kept.Messages));
            // subtitles left out stay MP4 (8. 11. 2026); a sound left out makes an MKV, and the note says why
            mp4Set.Tracks[^1].Include = false;
            Check(mp4Set.Mp4Note.StartsWith("Výsledok ostane MP4", StringComparison.Ordinal), "mux: subtitles left out – still MP4: " + mp4Set.Mp4Note);
            mp4Set.Tracks[^1].Include = true;
            mp4Set.Tracks.First(t => t.Track.Type == TrackType.Audio).Include = false;
            Check(mp4Set.Mp4Note.StartsWith("Výsledok bude MKV", StringComparison.Ordinal) && mp4Set.OutputPath.EndsWith(".mkv", StringComparison.Ordinal),
                "mux: a sound left out – an MKV, and why: " + mp4Set.Mp4Note + " | " + mp4Set.OutputPath);
        }
        finally
        {
            services.KeepMp4 = keepMp4;
        }

        // done: the extra tabs are closed ("yes" to the question)
        confirm = true;
        foreach (var extra in mux.Settings.Where(s => s != set).ToList())
        {
            mux.CloseSettingsCommand.Execute(extra);
        }

        Check(mux.Settings.Count == 1 && mux.Unprocessed.Count == 0, $"mux: closed after the question ({mux.Settings.Count})");
        model.SelectedTool = model.Tools.First(t => t.Page == mux);
    }

    /// <summary>Chapter editor: read, rename, add; the result is a library copy (original unchanged); a library file edited again can be undone.</summary>
    private static async Task TestChaptersAsync(MainViewModel model, MainWindow window, string work)
    {
        var copy = Path.Combine(work, "kapitoly.mkv");
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"), copy, overwrite: true);
        var originalBytes = File.ReadAllBytes(copy);
        var chapters = model.Chapters;
        model.SelectedTool = model.Tools.First(t => t.Page == chapters);
        await chapters.LoadAsync(copy);
        chapters.Library.IsMovie = true;
        chapters.Library.TitleText = "Kapitoly test (2026)";
        Check(chapters.Rows.Count == 2 && chapters.Rows[1].Name == "Koniec", $"chapters: two read ({chapters.Rows.Count}) – {chapters.Summary}");
        chapters.Rows[0].Name = "Začiatok";
        chapters.SelectedRow = chapters.Rows[0];
        chapters.AddCommand.Execute(null);
        chapters.Rows[1].Start = "00:00:01.5";
        chapters.Rows[1].Name = "Polovica";
        Check(chapters.Problems.Length == 0 && chapters.SaveCommand.CanExecute(null), "chapters: ready to save – " + chapters.Problems + " " + chapters.Library.Description);
        await Screenshot(window, "chapters.png");
        await chapters.SaveCommand.ExecuteAsync(null);
        Check(chapters.Result.StartsWith("Uložené do knižnice (3 kapitoly)", StringComparison.Ordinal), "chapters: saved – " + chapters.Result);
        Check(File.ReadAllBytes(copy).AsSpan().SequenceEqual(originalBytes), "chapters: original unchanged, byte for byte");
        Check(chapters.FilePath != copy && string.Join("|", chapters.Rows.Select(r => r.Name)) == "Začiatok|Koniec|Polovica",
            "chapters: the library copy has them, sorted by time – " + string.Join("|", chapters.Rows.Select(r => r.Name)));

        chapters.SelectedRow = chapters.Rows[2];
        chapters.RemoveCommand.Execute(null);
        await chapters.SaveCommand.ExecuteAsync(null);
        Check(chapters.Rows.Count == 2 && chapters.UndoCommand.CanExecute(null), "chapters: library file edited again, can be undone – " + chapters.Result);
        await chapters.UndoCommand.ExecuteAsync(null);
        Check(chapters.Rows.Count == 3, "chapters: undo – " + chapters.Result);

        chapters.Rows[0].Start = "1:75";
        Check(chapters.Problems.Contains("HH:MM:SS", StringComparison.Ordinal) && !chapters.SaveCommand.CanExecute(null), "chapters: wrong time blocks saving");
        chapters.Interval = "1";
        chapters.Generate();
        Check(chapters.Rows.Count == 1 && chapters.Rows[0].Name == "Kapitola 01", "chapters: generated for a 2-second file");
    }

    // owner, 31. 10. 2026: Záznamy spracovania – the runs of this self-test (Hromadné and Single spracovanie, the studios …)
    // in words; ticked logs and all deleted (the films are never touched)
    private static async Task TestProcessingLogsAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var page = model.Logs;
        model.SelectedTool = model.Tools.First(t => t.Page == page);
        await WaitFor(() => page.Logs.Count > 0);
        var kinds = page.Logs.Select(l => l.Kind).Distinct().ToList();
        Check(kinds.Contains("Hromadné spracovanie") && kinds.Contains("Úprava hlavičky"), "logs: the runs of the self-test – " + string.Join(", ", kinds));
        page.Selected = page.Logs.First(l => l.Kind == "Hromadné spracovanie");
        Check(page.Files.Count > 0 && page.Files.All(f => f.Headline.Length > 0 && (f.HasDone || f.HasProblems)), "logs: the files of a batch run in words – " +
              string.Join(" | ", page.Files.Take(3).Select(f => f.Headline)));
        await Screenshot(window, "logs.png");

        page.Confirm = _ => true;
        var count = page.Logs.Count;
        page.Logs[0].IsChecked = true;
        page.DeleteCommand.Execute(null);
        await WaitFor(() => page.Logs.Count == count - 1);
        Check(page.Logs.Count == count - 1 && services.ProcessingLogs.All().Count == count - 1, "logs: a ticked log deleted");
        page.DeleteAllCommand.Execute(null);
        await WaitFor(() => page.Logs.Count == 0);
        Check(page.Logs.Count == 0 && services.ProcessingLogs.All().Count == 0, "logs: all deleted");
    }

    // owner, 1. 11. 2026: subtitles of another format (WebVTT) written as SRT inside the result, only when ticked – in
    // Zlučovanie (a .vtt added to a film) and in Single spracovanie (WebVTT in the film); invented names
    private static async Task TestSrtConversionAsync(MainViewModel model, MainWindow window, AppServices services, string work)
    {
        var film = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv");
        var vtt = Path.Combine(work, "Vymyslená epizóda.vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00.500 --> 00:01.500\nŘekl jsem ti, že tady budu.\n\n00:01.600 --> 00:01.900\nCo ještě chceš?\n");
        var mux = model.Mux;
        var previous = mux.AskHowToAdd;
        mux.AskHowToAdd = request => new MuxAddChoice(MuxAddAction.AddToCurrent);
        try
        {
            model.Open(mux);
            mux.NewSettingsCommand.Execute(null);
            await mux.AddFilesAsync([film, vtt]);
            var set = mux.Current;
            var webVtt = set.Tracks.Single(t => t.Input.File.Path == vtt);
            Check(webVtt.CanConvertToSrt && set.Tracks.Where(t => t.Kind == TrackType.Subtitles && t != webVtt).All(t => !t.CanConvertToSrt),
                $"srt: Zlučovanie offers Na SRT for the WebVTT only – {webVtt.ToSrtHint}");
            webVtt.ToSrt = true;
            set.SelectedTrack = webVtt;
            Check(webVtt.FlagMarks.Contains("na SRT", StringComparison.Ordinal) && set.Spec().Tracks.Any(t => t.Options.ToSrt),
                "srt: ticked – shown in the list and in the recipe");
            await Screenshot(window, "mux-srt.png");
            var confirm = mux.Confirm;
            mux.Confirm = _ => true;
            mux.CloseSettingsCommand.Execute(set);
            mux.Confirm = confirm;
        }
        finally
        {
            mux.AskHowToAdd = previous;
        }

        // Single spracovanie: WebVTT inside the film
        if (services.CurrentMkvToolNix is not { } installation)
        {
            Fail("srt: MKVToolNix missing");
            return;
        }

        var withVtt = Path.Combine(work, "Vymyslený film s WebVTT.mkv");
        var made = await services.Runner.RunAsync(new Core.Processes.ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvMerge),
            ["-q", "-o", withVtt, film, "--language", "0:cze", vtt]));
        Check(made.ExitCode is 0 or 1, "srt: a film with WebVTT made – " + made.StandardError);
        model.Open(model.Rules);
        await model.Rules.LoadAsync(withVtt);
        for (var wait = 0; wait < 600 && model.Rules.IsReadingSubtitles; wait++)
        {
            await Task.Delay(100);
        }

        var row = model.Rules.Rows.FirstOrDefault(r => r.CanConvertToSrt);
        Check(row is not null && model.Rules.Rows.Count(r => r.CanConvertToSrt) == 1, "srt: Single offers Na SRT for the WebVTT track only");
        if (row is not null)
        {
            model.Rules.ChooseSrt(row.TrackId, true);
            Check(model.Rules.Plan is { Method: Core.Planning.WriteMethod.Remux } plan && plan.Changes.Any(c => c.Kind == Core.Planning.ChangeKind.ToSrt) &&
                  model.Rules.Rows.Single(r => r.TrackId == row.TrackId).ToSrt,
                "srt: Single plans the WebVTT as SRT – " + model.Rules.Summary);
            await Screenshot(window, "single-srt.png");
            model.Rules.ChooseSrt(row.TrackId, false);
        }
    }

    private static async Task TestMuxRecognitionAsync(MainViewModel model, MainWindow window, string work)
    {
        var mux = model.Mux;
        var speech = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "speech-und.mkv");
        var srt = Path.Combine(work, "Vymyslený film.forced.srt");
        File.WriteAllText(srt, string.Join("\n\n", Enumerable.Range(0, 12).Select(i =>
            $"{i + 1}\n00:00:{i * 2:00},000 --> 00:00:{(i * 2) + 1:00},500\nAko sa máš? Som tu, čo ešte chceš, keď je teraz všetko také, ako má byť.")) + "\n");
        var czechSrt = Path.Combine(work, "Vymyslený film.srt");
        File.WriteAllText(czechSrt, string.Join("\n\n", Enumerable.Range(0, 12).Select(i =>
            $"{i + 1}\n00:00:{i * 2:00},000 --> 00:00:{(i * 2) + 1:00},500\nŘekl jsem ti, že tady budu. Co ještě chceš, když je teď všechno takové, jaké to má být?")) + "\n");
        var previous = mux.AskHowToAdd;
        mux.AskHowToAdd = request => new MuxAddChoice(MuxAddAction.AddToCurrent);
        try
        {
            mux.NewSettingsCommand.Execute(null);
            await mux.AddFilesAsync([speech, srt, czechSrt]);
            var set = mux.Current;
            var audio = set.Tracks.Where(t => t.Kind == TrackType.Audio).ToList();
            Check(audio.Count == 3 && audio.All(a => a.Language.Tag.IsUndetermined) && set.DetectAudioCommand.CanExecute(null),
                $"mux recognition: three audio tracks without a language, the button offered ({audio.Count})");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await set.DetectAudioAsync();
            Write($"mux recognition: audio ({watch.Elapsed.TotalSeconds:0.0} s): {set.Result} | {string.Join(", ", audio.Select(a => a.Language.Display))}");
            Check(audio[1].Language.Tag.Primary == "en" && new[] { audio[0], audio[2] }.All(a => a.Language.Tag.Primary is "cs" or "sk"),
                "mux recognition: the languages of the speech written into the tracks – " + string.Join(", ", audio.Select(a => a.Language.Display)));
            await set.DetectSubtitlesAsync();
            var subtitles = set.Tracks.Single(t => t.Kind == TrackType.Subtitles && t.Input.File.Path == srt);
            Write("mux recognition: subtitles: " + set.Result);
            Check(subtitles.Language.Tag.Primary == "sk" && subtitles.IsForced, $"mux recognition: Slovak subtitles, forced by the file's name – {subtitles.Language.Display}, forced {subtitles.IsForced}");

            // owner, 1. 11. 2026: the order follows the languages (CZ at the top, SK under it) and is changed by hand
            var czech = set.Tracks.Single(t => t.Kind == TrackType.Subtitles && t.Input.File.Path == czechSrt);
            int At(MuxTrackRow row) => set.Tracks.IndexOf(row);
            var english = audio.FirstOrDefault(a => a.Language.Tag.Primary == "en");
            Check(czech.Language.Tag.Primary == "cs" && At(czech) < At(subtitles) && english is not null && audio.Where(a => a != english).All(a => At(a) < At(english)),
                $"mux order: Czech subtitles above the Slovak ones, Czech and Slovak audio above the English – {string.Join(", ", set.Tracks.Select(t => t.Language.Tag.Primary))}");
            set.MoveRowUpCommand.Execute(subtitles);
            var byHand = At(subtitles) < At(czech);
            set.MoveTrackTo(subtitles, czech);
            Check(byHand && At(czech) < At(subtitles), "mux order: a track moved by its arrow and dragged back");
            set.MoveRowUpCommand.Execute(subtitles);
            subtitles.Language = subtitles.LanguageChoices.First(c => c.Tag.Primary == "en");
            var english2 = At(czech) < At(subtitles);
            subtitles.Language = subtitles.LanguageChoices.First(c => c.Tag.Primary == "sk");
            Check(english2 && At(czech) < At(subtitles), "mux order: a language set by hand puts the track in its place");

            // owner, 1. 11. 2026: the order follows only after a pause – the track does not jump away from under the mouse
            ArrangeLater.Delay = TimeSpan.FromMilliseconds(400);
            czech.Language = czech.LanguageChoices.First(c => c.Tag.Primary == "sk");
            var stayed = At(czech) < At(subtitles);
            await Task.Delay(1200);
            var arranged = At(subtitles) < At(czech);
            ArrangeLater.Delay = TimeSpan.Zero;
            czech.Language = czech.LanguageChoices.First(c => c.Tag.Primary == "cs");
            Check(stayed && arranged && At(czech) < At(subtitles), $"mux order: arranged after the pause, not at once (stayed {stayed}, arranged {arranged})");

            // owner, 1. 11. 2026: the drop-down of the languages (thousands) opens at once – only the items seen are made
            model.Open(mux);
            set.SelectedTrack = czech;
            await Settle();
            if (FindVisual<System.Windows.Controls.ComboBox>(window, c => c.ItemsSource is IReadOnlyList<LanguageChoice> { Count: > 50 } && c.IsVisible) is { } combo)
            {
                var opening = System.Diagnostics.Stopwatch.StartNew();
                combo.IsDropDownOpen = true;
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var opened = opening.Elapsed.TotalMilliseconds;
                await Settle();
                var made = Enumerable.Range(0, combo.Items.Count).Count(i => combo.ItemContainerGenerator.ContainerFromIndex(i) is not null);
                var scroller = QuickDropDowns.Scroller(combo);
                var panel = scroller is null ? null : FindVisual<System.Windows.Controls.Panel>(scroller, p => p.IsItemsHost);
                Write($"mux languages drop-down: {combo.Items.Count} languages, {made} items made, opened in {opened:0} ms; list scrolls by items " +
                      $"{scroller?.CanContentScroll}, drop-down {System.Windows.Controls.ScrollViewer.GetCanContentScroll(combo)}, panel {panel?.GetType().Name}, " +
                      $"virtualizing {System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(combo)}, scroller {scroller?.GetType().Name} {scroller?.ActualHeight:0} px");
                combo.IsDropDownOpen = false;
                Check(made > 0 && (made < 200 || combo.Items.Count < 300), $"mux languages drop-down: only the items seen are made ({made} of {combo.Items.Count})");
            }
            else
            {
                Write($"mux languages drop-down: not shown – the track's {czech.LanguageChoices.Count} languages, track shown {set.ShowsTrack}");
                Fail("mux languages drop-down: not found");
            }

            await Screenshot(window, "mux-recognition.png");
            var confirm = mux.Confirm;
            mux.Confirm = _ => true;
            mux.CloseSettingsCommand.Execute(set);
            mux.Confirm = confirm;
        }
        finally
        {
            mux.AskHowToAdd = previous;
        }
    }
}
