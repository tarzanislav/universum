using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Universum.App.Pages;
using Universum.App.Setup;
using Universum.App.Shell;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Platform.Windows;

namespace Universum.App.SelfTesting;

/// <summary>
/// Self-test run by CI ("Universum.exe --samotest") on a real Windows machine with a real MKVToolNix.
/// Environment: UNIVERSUM_MKVTOOLNIX_DIR (tools), UNIVERSUM_TESTDATA (testdata folder),
/// UNIVERSUM_SELFTEST_DIR (log and screenshots). Exit code 0 = passed.
/// </summary>
internal static class SelfTest
{
    private static readonly StringBuilder Log = new();
    private static string _outputDirectory = "";
    private static int _failures;

    public static async Task<int> RunAsync(AppServices services)
    {
        _outputDirectory = Environment.GetEnvironmentVariable("UNIVERSUM_SELFTEST_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "UNIVERSUM-selftest");
        Directory.CreateDirectory(_outputDirectory);
        Environment.SetEnvironmentVariable("UNIVERSUM_SETTINGS_DIR", Path.Combine(_outputDirectory, "settings"));

        MkvToolNixInstallation? installation = null;
        await Step("MKVToolNix", async () => installation = await TestMkvToolNixAsync(services));
        await Step("inspection", () => TestInspectionAsync(services));
        await Step("release feed and download", () => TestReleaseFeedAndDownloadAsync(installation));
        await Step("windows", () => TestWindowsAsync(services));

        Write(_failures == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({_failures})");
        File.WriteAllText(Path.Combine(_outputDirectory, "selftest.log"), Log.ToString(), new UTF8Encoding(false));
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Runs one part of the self-test; an exception fails it but the other parts still run.</summary>
    private static async Task Step(string name, Func<Task> test)
    {
        Write($"--- {name}");
        try
        {
            await test();
        }
        catch (Exception ex)
        {
            Fail($"{name}: unexpected exception: {ex}");
        }
    }

    private static async Task<MkvToolNixInstallation?> TestMkvToolNixAsync(AppServices services)
    {
        var installation = await services.FindMkvToolNixAsync();
        Check(installation is not null, "MKVToolNix found");
        if (installation is null)
        {
            return null;
        }

        Write($"using {installation}");
        Check(installation.IsSupportedVersion, $"version {installation.Version} >= {MkvToolNixVersion.Minimum}");
        foreach (var tool in Enum.GetValues<MkvToolNixTool>())
        {
            Check(installation.HasTool(tool), $"tool present: {MkvToolNixToolNames.FileName(tool)}");
        }

        var signature = AuthenticodeVerifier.Verify(installation.ToolPath(MkvToolNixTool.MkvMerge));
        Write($"mkvmerge.exe signature: valid={signature.IsValid}, signer={signature.SignerName}, status=0x{signature.Status:X8}, revocation checked={signature.RevocationChecked}");
        Check(signature.IsSignedBy(MkvToolNixDownloads.WindowsSignerName), "mkvmerge.exe signed by " + MkvToolNixDownloads.WindowsSignerName);

        var unsigned = Path.Combine(_outputDirectory, "unsigned.exe");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0, 0]);
        Check(!AuthenticodeVerifier.Verify(unsigned).IsValid, "unsigned file is rejected");

        // Recycle Bin: only where Windows really keeps files (never a silent permanent delete)
        var bin = new WindowsRecycleBin();
        var probe = Path.Combine(_outputDirectory, "recycle-probe.txt");
        File.WriteAllText(probe, "x");
        var reason = bin.CannotRecycle(probe);
        Write($"Recycle Bin on the work drive: {reason ?? "available"}");
        Check(reason is null, "Recycle Bin usable on the work drive (volume, settings and content read): " + (reason ?? "ok"));
        Check(bin.CannotRecycle(@"\\server\share\film.mkv") is not null, "network share refused");
        File.Delete(probe);

        var registry = new RegistryMkvToolNixLocations().CandidateDirectories().ToList();
        Write($"registry candidates: {(registry.Count == 0 ? "none" : string.Join("; ", registry))}");
        return installation;
    }

    private static async Task TestInspectionAsync(AppServices services)
    {
        var testData = Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA");
        if (string.IsNullOrEmpty(testData) || services.CurrentMkvToolNix is null)
        {
            Fail("UNIVERSUM_TESTDATA or MKVToolNix missing");
            return;
        }

        var inspector = new MkvMergeInspector(services.CurrentMkvToolNix, services.Runner);
        var file = await inspector.InspectAsync(Path.Combine(testData, "media", "en-und-cz-forced.mkv"));
        Check(file.Tracks.Count == 6, $"en-und-cz-forced: 6 tracks ({file.Tracks.Count})");
        Check(file.TracksOf(TrackType.Audio).Count(t => t.Language.IsUndetermined) == 1, "en-und-cz-forced: one undetermined audio track");
        Check(file.HasTags && file.HasChapters, "en-und-cz-forced: tags and chapters");
        Write($"identification format version {file.IdentificationFormatVersion}");
    }

    private static async Task TestReleaseFeedAndDownloadAsync(MkvToolNixInstallation? installation)
    {
        var latest = await MkvToolNixInstaller.GetLatestVersionAsync();
        Write($"latest MKVToolNix release: {latest}");
        Check(installation is null || latest >= installation.Version, "release feed is not older than the tested version");

        // full download and verification path, without running the installer
        var installer = await MkvToolNixInstaller.DownloadAsync(latest, null);
        Check(File.Exists(installer), "installer downloaded and verified (SHA-256 + signature)");
        File.Delete(installer);
    }

    private static async Task TestWindowsAsync(AppServices services)
    {
        var model = new MainViewModel(services);
        MainWindow.ConfirmOnClose = false;
        var window = new MainWindow(model) { Width = 1180, Height = 740, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        await model.RefreshStatusAsync();
        await model.Info.LoadAsync(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"));
        Check(model.Info.Tracks.Count == 6, $"information page shows 6 tracks ({model.Info.Tracks.Count})");
        Check(model.Info.Error.Length == 0, "information page shows no error: " + model.Info.Error);
        await Screenshot(window, "main-info.png");

        foreach (var tool in model.Tools)
        {
            model.SelectedTool = tool;
            await Settle();
        }

        model.SelectedTool = model.Tools.First(t => t.Page == model.Settings);
        await Screenshot(window, "settings.png");

        // rules page: undetermined languages chosen by the user, result written into the library, original kept
        var library = Path.Combine(_outputDirectory, "library");
        var work = Path.Combine(_outputDirectory, "rules");
        Directory.CreateDirectory(work);
        var copy = Path.Combine(work, "en-und-cz-forced.mkv");
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"), copy, overwrite: true);
        model.SelectedTool = model.Tools.First(t => t.Page == model.Rules);
        await model.Rules.LoadAsync(copy);
        Check(model.Rules.Plan?.Target.IsBlocked == true, "rules: undetermined languages block the file");
        Check(!model.Rules.ApplyCommand.CanExecute(null), "rules: apply disabled while blocked");
        Check(services.Library is not null || model.Rules.Target.Contains("výstupný priečinok", StringComparison.Ordinal),
            "rules: asks for the output folder – " + model.Rules.Target);
        await Screenshot(window, "rules-undetermined.png");
        model.Rules.ChooseLanguage(2, "cs");
        model.Rules.ChooseLanguage(3, "cs");
        model.Rules.KeepOriginal = true;
        services.SetOutputDirectory(library);
        Check(Directory.Exists(Path.Combine(library, "MOVIES")) && Directory.Exists(Path.Combine(library, "TVSHOWS")), "library: MOVIES and TVSHOWS created");
        model.Rules.IsMovie = true;
        model.Rules.TitleText = "Vymyslený film: Návrat (2026)";
        Check(model.Rules.Target == Path.Combine("MOVIES", "Vymyslený film Návrat (2026)", "Vymyslený film Návrat (2026) - CZ EN SD H264 SMALL.mkv"),
            "rules: target in the library – " + model.Rules.Target);
        Check(model.Rules.ApplyCommand.CanExecute(null), "rules: apply enabled after choosing languages and title");
        await Screenshot(window, "rules-preview.png");
        await model.Rules.ApplyCommand.ExecuteAsync(null);
        Check(model.Rules.Result.StartsWith("Hotovo", StringComparison.Ordinal), "rules: written – " + model.Rules.Result);
        var result = Path.Combine(library, "MOVIES", "Vymyslený film Návrat (2026)", "Vymyslený film Návrat (2026) - CZ EN SD H264 SMALL.mkv");
        Check(File.Exists(result) && File.Exists(copy), "rules: result in the library, original kept");
        await Screenshot(window, "rules-done.png");
        await model.Rules.ApplyCommand.ExecuteAsync(null);
        Check(model.Rules.Result.StartsWith("Rovnaký stream", StringComparison.Ordinal), "rules: the same stream is not written twice – " + model.Rules.Result);

        await TestHeaderEditorAsync(model, window, services, work);
        await TestMultiplexerAsync(model, window, services, work);
        await TestChaptersAsync(model, window, work);

        // TMDB key: protected for the Windows account, never stored as plain text
        var secret = UserSecret.Protect("test-key-123");
        Check(UserSecret.Unprotect(secret) == "test-key-123" && !secret.Contains("test-key", StringComparison.Ordinal), "TMDB key protected with DPAPI");
        Check(!model.Rules.Tmdb.SearchCommand.CanExecute(null), "rules: TMDB button disabled without a key");
        services.SetTmdbKey("test-key-123");
        Check(model.Rules.Tmdb.SearchCommand.CanExecute(null), "rules: TMDB button enabled with a key");
        model.Settings.TmdbEnabled = false;
        Check(!model.Rules.Tmdb.SearchCommand.CanExecute(null) && services.TmdbKey == "test-key-123", "settings: TMDB switched off, key kept");
        model.Settings.TmdbEnabled = true;
        Check(model.Rules.Tmdb.SearchCommand.CanExecute(null), "settings: TMDB switched on again");
        Check(!File.ReadAllText(Path.Combine(AppSettings.Directory, "settings.json")).Contains("test-key-123", StringComparison.Ordinal), "settings hold no plain TMDB key");
        // HISTRA token: checked for the Bearer header, protected like the TMDB key
        model.Settings.HistraTokenInput = "bad token\r\nX: 1";
        model.Settings.SaveHistraTokenCommand.Execute(null);
        Check(services.HistraToken is null && model.Settings.HistraStatus.StartsWith("Token sa neuložil", StringComparison.Ordinal), "settings: unsafe HISTRA token refused");
        model.Settings.HistraTokenInput = "hst_selftest-0123456789";
        model.Settings.SaveHistraTokenCommand.Execute(null);
        Check(services.HistraToken == "hst_selftest-0123456789" && model.Settings.HistraTokenInput.Length == 0, "settings: HISTRA token saved");
        Check(services.SyncActive && model.Settings.SyncStatus.StartsWith("TMDB aj HISTRA", StringComparison.Ordinal), "settings: TMDB and HISTRA both on");
        Check(services.Histra is not null, "settings: HISTRA client ready with a token");
        model.Settings.HistraEnabled = false;
        Check(!services.HistraActive && !services.SyncActive && services.HistraToken is not null && model.Settings.SyncStatus == "Zapnuté je len TMDB.", "settings: HISTRA switched off, token kept");
        Check(services.Histra is null, "settings: no HISTRA client while switched off");
        model.Settings.TmdbEnabled = false;
        model.Settings.HistraEnabled = true;
        Check(model.Settings.SyncStatus == "Zapnutá je len HISTRA.", "settings: only HISTRA on");
        model.Settings.TmdbEnabled = true;
        Check(!File.ReadAllText(Path.Combine(AppSettings.Directory, "settings.json")).Contains("hst_selftest", StringComparison.Ordinal), "settings hold no plain HISTRA token");
        Check(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/tmdb-logo.png")) is { } logo && logo.Stream.Length > 1000, "TMDB logo bundled");
        model.SelectedTool = model.Tools.First(t => t.Page == model.Settings);
        await Screenshot(window, "settings-library.png");
        services.SetTmdbKey(null);
        model.Settings.RemoveHistraTokenCommand.Execute(null);
        Check(services.HistraToken is null, "settings: HISTRA token removed");

        await TestBatchPageAsync(model, window);

        // updates: the real MKVToolNix feed and change log, the GitHub releases of UNIVERSUM
        var updates = await services.Updates.CheckAsync();
        Write($"update check: MKVToolNix {updates.MkvToolNix?.Latest} (news {updates.MkvToolNix?.News.Count}), " +
              $"UNIVERSUM release {updates.Universum?.Release.Version.ToString() ?? "none"}; problems: {string.Join(" | ", updates.Problems)}");
        Check(updates.MkvToolNix is not null, "update check reads the MKVToolNix release feed");
        var updatesWindow = model.CreateUpdatesWindow();
        updatesWindow.WindowStartupLocation = WindowStartupLocation.Manual;
        updatesWindow.Left = 0;
        updatesWindow.Top = 0;
        updatesWindow.Show();
        await Screenshot(updatesWindow, "updates.png");
        updatesWindow.Close();

        // "Otvoriť v UNIVERSUM" from the Explorer: a folder opens in batch processing
        await model.OpenPathAsync(Path.Combine(_outputDirectory, "batch"));
        Check(model.SelectedTool.Page == model.Batch && model.Batch.Groups.Count > 0, "a folder from the command line opens in batch processing");
        window.Close();

        var setup = new MkvToolNixSetupWindow(new MkvToolNixSetupViewModel(services)) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        setup.Show();
        await Task.Delay(3000);
        await Screenshot(setup, "setup.png");
        setup.Close();
    }

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
        Check(!editor.SaveCommand.CanExecute(null), "header editor: nothing to save yet");

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
    private static async Task TestMultiplexerAsync(MainViewModel model, MainWindow window, AppServices services, string work)
    {
        var srt = Path.Combine(work, "titulky-sk.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nVymyslený slovenský titulok.\n\n");
        var film = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv");
        var mux = model.Mux;
        MuxAddRequest? asked = null;
        var answer = MuxAddAction.AddToCurrent;
        mux.AskHowToAdd = request =>
        {
            asked = request;
            return new MuxAddChoice(answer, answer is MuxAddAction.Append or MuxAddAction.AdditionalParts ? request.Sources[0] : null);
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
        set.Tracks[2].Language = Lang(set.Tracks[2], "cs");
        set.Tracks[3].Language = Lang(set.Tracks[3], "cs");
        set.Tracks[6].Language = Lang(set.Tracks[6], "sk");
        set.Tracks[5].Include = false;
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

        // the add dialog itself (shown, not answered)
        var dialog = new MuxAddFilesWindow(new MuxAddFilesModel(new MuxAddRequest([film, srt], appended.Inputs.ToList()))) { Owner = window };
        dialog.Show();
        await Screenshot(dialog, "mux-add-files.png");
        dialog.Close();

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

    private static async Task TestBatchPageAsync(MainViewModel model, MainWindow window)
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
        batch.Folder = folder;
        batch.Recursive = true;
        await batch.AnalyzeAsync();
        Check(batch.Groups.Count == 2 && batch.Groups[0].Count == 3, $"batch: 2 groups, the series first ({batch.Summary})");
        Check(batch.IsSeries, "batch: series recognised from the episode numbers");
        Check(batch.SeriesSuggestion.Contains("Seriál", StringComparison.Ordinal), "batch: series title suggested – " + batch.SeriesSuggestion);
        Check(batch.Items.Count(i => i.Status == Core.Batch.BatchStatus.NeedsReview) == 4, "batch: everything waits for the title and the subtitle language");
        await Screenshot(window, "batch-analysed.png");

        var forcedSlot = batch.SlotRows.First(s => s.Key == new Core.Batch.SlotKey(TrackType.Subtitles, 0));
        forcedSlot.ChosenLanguage = Languages.Czech;
        batch.ConfirmTitlesCommand.Execute(null);
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
        await Screenshot(window, "batch-done.png");

        var editor = new Templates.TemplateEditorWindow(new Templates.TemplateEditorViewModel(
            Core.Templates.BatchTemplate.FromTarget("Podľa skupiny A", batch.Items.First().Target))) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        editor.Show();
        await Screenshot(editor, "template-editor.png");
        editor.Close();
    }

    private static async Task Screenshot(Window window, string fileName)
    {
        await Settle();
        if (window.Content is not FrameworkElement content || content.ActualWidth < 1)
        {
            Fail($"screenshot {fileName}: nothing rendered");
            return;
        }

        // the content is drawn at its margin offset, so the picture includes the margins (or the edges are cut off)
        var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap(
            (int)(width * dpi.DpiScaleX), (int)(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
        {
            context.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var stream = File.Create(Path.Combine(_outputDirectory, fileName));
        encoder.Save(stream);
        Write("screenshot " + fileName);
    }

    private static async Task Settle()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
    }

    private static void Check(bool condition, string what)
    {
        if (condition)
        {
            Write("ok    " + what);
        }
        else
        {
            Fail(what);
        }
    }

    private static void Fail(string what)
    {
        _failures++;
        Write("ERROR " + what);
    }

    private static void Write(string line) => Log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
}
