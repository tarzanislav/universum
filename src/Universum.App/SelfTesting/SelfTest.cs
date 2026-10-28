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
        Check(model.SelectedTool.Page == model.Dashboard && model.Tools[0].Title == "Dashboard", "the program starts with the Dashboard");
        Check(string.Join(" | ", model.ToolsView.Groups!.Cast<System.Windows.Data.CollectionViewGroup>()
                  .Select(g => $"{(g.Name is ToolGroup { IsFramed: true } group ? group.Title : "-")}: {string.Join(", ", g.Items.Cast<ToolItem>().Select(t => t.Title))}")) ==
              "-: Dashboard | MULTIPLEXING: Zlučovanie, Úprava hlavičky, Kapitoly, Informácie | " +
              "ŠABLÓNY (TEMPLATE): Single spracovanie, Hromadné spracovanie, MEGAspracovanie | ŠTATISTIKA: Fronta úloh, Výstup úloh, Nastavenia",
            "left rail: Dashboard, then the groups Multiplexing, Šablóny and Štatistika");
        model.Open(model.Info);
        await Screenshot(window, "main-info.png");

        foreach (var tool in model.Tools)
        {
            model.SelectedTool = tool;
            await Settle();
        }

        await TestDashboardAsync(model, window, services);

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

        await TestTitleSyncAsync(model, window, services);
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
        Check(model.Settings.SyncStatus.StartsWith("Zapnutá je len HISTRA", StringComparison.Ordinal), "settings: only HISTRA on");
        Check(services.Titles is { Tmdb: null, UsesHistra: true } && model.Rules.Tmdb.SearchCommand.CanExecute(null) &&
              model.Rules.Tmdb.Tooltip.Contains("katalógu HISTRA", StringComparison.Ordinal),
            "titles: without TMDB the title button searches the HISTRA catalog – " + model.Rules.Tmdb.Tooltip);
        model.Settings.TmdbEnabled = true;
        Check(!File.ReadAllText(Path.Combine(AppSettings.Directory, "settings.json")).Contains("hst_selftest", StringComparison.Ordinal), "settings hold no plain HISTRA token");
        Check(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/tmdb-logo.png")) is { } logo && logo.Stream.Length > 1000, "TMDB logo bundled");
        model.SelectedTool = model.Tools.First(t => t.Page == model.Settings);
        await Screenshot(window, "settings-library.png");
        services.SetTmdbKey(null);
        model.Settings.RemoveHistraTokenCommand.Execute(null);
        Check(services.HistraToken is null, "settings: HISTRA token removed");

        await TestBatchPageAsync(model, window, services);

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
        await TestContextMenuAsync(model);
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

        // done: the extra tabs are closed ("yes" to the question)
        confirm = true;
        foreach (var extra in mux.Settings.Where(s => s != set).ToList())
        {
            mux.CloseSettingsCommand.Execute(extra);
        }

        Check(mux.Settings.Count == 1 && mux.Unprocessed.Count == 0, $"mux: closed after the question ({mux.Settings.Count})");
        model.SelectedTool = model.Tools.First(t => t.Page == mux);
    }

    /// <summary>
    /// TMDB checked in HISTRA (synchronisation, 27. 10. 2026) on the rules page, with invented answers of both services
    /// (nothing goes to the internet): another title in HISTRA waits for confirmation; the same title is confirmed; a name
    /// found nowhere waits as a suggestion; a typed title is the user's decision. Only the name goes to HISTRA.
    /// </summary>
    private static async Task TestTitleSyncAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var fake = new FakeTitleServices();
        var http = new System.Net.Http.HttpClient(fake);
        TitleSearch Fresh() => new(
            new Core.Library.Tmdb.TmdbClient(http, "selftest-key", pacer: new RequestPacer(100)),
            new Core.Library.Histra.HistraCatalog(new Core.Library.Histra.HistraClient(http, "hst_selftest-0123456789", new RequestPacer(100))));
        var rules = model.Rules;
        model.SelectedTool = model.Tools.First(t => t.Page == rules);
        rules.IsMovie = true;

        // TMDB finds one title, HISTRA another of the same name and year: the title waits for confirmation
        fake.Tmdb = """{"id":9500,"title":"Vymyslený vlak","original_title":"Vymyslený vlak","release_date":"2019-02-01"}""";
        fake.Histra = """{"id":77,"title":"Vymyslený vlak","original_title":"Vymyslený vlak","release_date":"2019-05-30","external_ids":{"tmdb":"9001"}}""";
        services.TitlesForSelfTest = Fresh();
        rules.Tmdb.Refresh();
        rules.TitleText = "Vymyslený vlak (2019)";
        await rules.Tmdb.SearchAsync();
        Check(rules.Tmdb.NeedsConfirmation && rules.Tmdb.Check == TitleCheck.Different && rules.Target == rules.Tmdb.Warning &&
              rules.Tmdb.Warning.Contains("9001", StringComparison.Ordinal) && !rules.ApplyCommand.CanExecute(null),
            "titles: HISTRA found another title – waits for confirmation: " + rules.Target);
        await Screenshot(window, "title-unconfirmed.png");
        rules.Tmdb.ConfirmCommand.Execute(null);
        Check(!rules.Tmdb.NeedsConfirmation && rules.Target.StartsWith(Path.Combine("MOVIES", "Vymyslený vlak (2019)"), StringComparison.Ordinal),
            "titles: confirmed by the user – " + rules.Target);
        Check(fake.Requests.Where(r => r.Contains("histra.net", StringComparison.Ordinal)).All(r => r == "https://histra.net/api/v1/catalog/movies/search?q=Vymyslený vlak"),
            "titles: only the name goes to HISTRA – " + string.Join(" | ", fake.Requests));

        // the same title in HISTRA: confirmed
        fake.Tmdb = """{"id":9002,"title":"Vymyslený vlak 2","original_title":"Vymyslený vlak 2","release_date":"2021-03-01"}""";
        fake.Histra = """{"id":12,"title":"Vymyslený vlak 2","original_title":"Vymyslený vlak 2","release_date":"2021-03-01","external_ids":{"tmdb":"9002"}}""";
        services.TitlesForSelfTest = Fresh();
        rules.TitleText = "Vymyslený vlak 2 (2021)";
        await rules.Tmdb.SearchAsync();
        Check(!rules.Tmdb.NeedsConfirmation && rules.Tmdb.Check == TitleCheck.Confirmed && rules.Tmdb.Message.Contains("potvrdila", StringComparison.Ordinal) &&
              rules.Target.StartsWith(Path.Combine("MOVIES", "Vymyslený vlak 2 (2021)"), StringComparison.Ordinal),
            "titles: HISTRA confirmed the title – " + rules.Tmdb.Message);

        // found nowhere: the suggestion waits; a title typed by the user is the user's decision
        fake.Tmdb = "";
        fake.Histra = "";
        services.TitlesForSelfTest = Fresh();
        rules.TitleText = "Neexistujúci vlak (2020)";
        await rules.Tmdb.SearchAsync();
        Check(rules.Tmdb.NeedsConfirmation && rules.Tmdb.Warning.Contains("nenašli", StringComparison.Ordinal) && rules.Target == rules.Tmdb.Warning,
            "titles: found nowhere – the suggestion waits: " + rules.Tmdb.Warning);
        rules.TitleText = "Neexistujúci vlak (2021)";
        Check(!rules.Tmdb.NeedsConfirmation && rules.Target.StartsWith(Path.Combine("MOVIES", "Neexistujúci vlak (2021)"), StringComparison.Ordinal),
            "titles: a typed title needs no confirmation – " + rules.Target);
        rules.TitleText = "Neexistujúci vlak (2020)";
        Check(rules.Tmdb.NeedsConfirmation && rules.Target == rules.Tmdb.Warning, "titles: the text that waited waits again when typed back");

        // HISTRA set up but not reachable: TMDB's title is not checked and waits for confirmation
        fake.Tmdb = """{"id":9003,"title":"Vymyslený vlak 3","original_title":"Vymyslený vlak 3","release_date":"2023-03-01"}""";
        fake.HistraStatus = System.Net.HttpStatusCode.Unauthorized;
        services.TitlesForSelfTest = Fresh();
        rules.TitleText = "Vymyslený vlak 3 (2023)";
        await rules.Tmdb.SearchAsync();
        Check(rules.Tmdb.NeedsConfirmation && rules.Tmdb.Check == TitleCheck.Unavailable && rules.Tmdb.Warning.Contains("nedala opýtať", StringComparison.Ordinal),
            "titles: HISTRA not reachable – waits for confirmation: " + rules.Tmdb.Warning);
        fake.HistraStatus = System.Net.HttpStatusCode.OK;

        services.TitlesForSelfTest = null;
        rules.Tmdb.Refresh();
        rules.TitleText = "Vymyslený film: Návrat (2026)";
    }

    /// <summary>Stand-in TMDB and HISTRA: every search answers with the invented results set for each service.</summary>
    /// <summary>
    /// Dashboard: date and name day, the weather of the town from the settings (invented Open-Meteo answers – only the
    /// town and its coordinates are asked for), the overview of the tools.
    /// </summary>
    private static async Task TestDashboardAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var dashboard = model.Dashboard;
        model.Open(dashboard);
        Check(dashboard.NameDayText.Length > 0 && dashboard.DateText.Length > 0, $"dashboard: {dashboard.DateText}, {dashboard.NameDayText}");
        Check(!dashboard.HasPlace && dashboard.WeatherMessage.StartsWith("Zadajte mesto", StringComparison.Ordinal), "dashboard: no weather without a town");
        var weather = new FakeWeather();
        services.WeatherForSelfTest = new Universum.Core.Weather.OpenMeteo(new System.Net.Http.HttpClient(weather));
        await Screenshot(window, "dashboard-no-town.png");

        model.Settings.WeatherTownInput = "Nikde";
        weather.Found = false;
        await model.Settings.FindWeatherTownCommand.ExecuteAsync(null);
        Check(services.Settings.WeatherPlace is null && model.Settings.WeatherStatus.Contains("nenašlo", StringComparison.Ordinal), "settings: a town Open-Meteo does not know is not stored");
        weather.Found = true;
        model.Settings.WeatherTownInput = "Vymyslené Mesto";
        await model.Settings.FindWeatherTownCommand.ExecuteAsync(null);
        Check(services.Settings.WeatherPlace is { Name: "Vymyslené Mesto", Latitude: 48.7 }, "settings: the town stored with its coordinates – " + model.Settings.WeatherStatus);

        // a new town is read at once by the Dashboard
        for (var i = 0; i < 50 && !dashboard.HasWeather; i++)
        {
            await Task.Delay(100);
        }

        Check(dashboard.HasWeather && dashboard.TemperatureText == "−3 °C" && dashboard.WeatherDescription == "Slabé sneženie" && dashboard.WeatherDays.Count == 3,
            $"dashboard: weather {dashboard.TemperatureText}, {dashboard.WeatherDescription}, days {dashboard.WeatherDays.Count}");
        Check(dashboard.WeatherDays[0].Label == "Dnes" && dashboard.WeatherDays[1].Label == "Zajtra" && dashboard.WeatherDays[1].Rain == "zrážky 40 %",
            "dashboard: the days of the forecast – " + string.Join(", ", dashboard.WeatherDays.Select(d => $"{d.Label} {d.Temperatures} {d.Rain}")));
        Check(weather.Requests.Count == 3 && weather.Requests.Take(2).All(r => r.StartsWith("https://geocoding-api.open-meteo.com/v1/search?", StringComparison.Ordinal)) &&
              weather.Requests[2].StartsWith("https://api.open-meteo.com/v1/forecast?latitude=48.7&longitude=19.15&", StringComparison.Ordinal),
            "dashboard: only the town, then its coordinates went to Open-Meteo – " + string.Join(" | ", weather.Requests));
        weather.Fails = true;
        await dashboard.RefreshWeatherAsync(force: true);
        Check(dashboard.HasWeather && dashboard.WeatherMessage.Contains("zobrazené je počasie z", StringComparison.Ordinal), "dashboard: a failed reading keeps the last weather – " + dashboard.WeatherMessage);
        weather.Fails = false;
        await dashboard.RefreshWeatherAsync(force: true);
        dashboard.RefreshStatus();
        Check(dashboard.Queue.Text.Length > 0 && dashboard.Tools.Text.Contains("MKVToolNix", StringComparison.Ordinal) && dashboard.Status.Count == 6,
            "dashboard: the overview – " + string.Join(" | ", dashboard.Status.Select(s => $"{s.Title}: {s.Text}")));
        model.Open(dashboard);
        await Screenshot(window, "dashboard.png");
        dashboard.Library.OpenCommand.Execute(null);
        Check(model.SelectedTool.Page == model.Settings, "dashboard: a line of the overview opens its tool");

        // several places of one name (Valaská, 27. 10.): offered with district, region and height; the chosen one is stored
        var settings = model.Settings;
        settings.WeatherTownInput = "Vymyslená Lehota";
        await settings.FindWeatherTownCommand.ExecuteAsync(null);
        Check(settings.HasWeatherChoices && settings.WeatherChoices.Count == 2 && services.Settings.WeatherPlace?.Name == "Vymyslené Mesto" &&
              settings.WeatherStatus.Contains("vyberte", StringComparison.Ordinal),
            "settings: several towns of one name are offered, nothing stored yet – " + string.Join(" | ", settings.WeatherChoices.Select(c => c.FullDisplay)));
        Check(settings.WeatherChoices[1].FullDisplay == "Vymyslená Lehota – okres Druhý, Banskobystrický kraj, Slovensko · 612 m n. m.",
            "settings: a town is told apart by its district, region and height – " + settings.WeatherChoices[1].FullDisplay);
        await Settle();
        window.UpdateLayout();
        var choices = FindVisual<System.Windows.Controls.ListBox>(window, list => ReferenceEquals(list.ItemsSource, settings.WeatherChoices));
        Check(choices is { HasItems: true, IsVisible: true }, "settings: the list of places is shown");
        choices?.BringIntoView(new Rect(0, 0, choices.ActualWidth, choices.ActualHeight + 120));

        await Screenshot(window, "settings-weather-choice.png");
        settings.SelectedWeatherChoice = settings.WeatherChoices[1];
        settings.UseWeatherChoiceCommand.Execute(null);
        Check(services.Settings.WeatherPlace is { District: "okres Druhý", Latitude: 48.9 } && !settings.HasWeatherChoices &&
              settings.WeatherStatus.StartsWith("Počasie pre: Vymyslená Lehota – okres Druhý", StringComparison.Ordinal) &&
              dashboard.PlaceText == "Vymyslená Lehota, okres Druhý",
            $"settings: the chosen town is stored and shown with its district – {settings.WeatherStatus} | {dashboard.PlaceText}");
    }

    /// <summary>The first element of this type below <paramref name="root"/> that matches.</summary>
    private static T? FindVisual<T>(DependencyObject root, Func<T, bool> match)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && match(found))
            {
                return found;
            }

            if (FindVisual(child, match) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>Open-Meteo with invented answers: one town, a cold day with light snow.</summary>
    private sealed class FakeWeather : System.Net.Http.HttpMessageHandler
    {
        public bool Found { get; set; } = true;

        public bool Fails { get; set; }

        public List<string> Requests { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (Fails)
            {
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            }

            var today = DateTime.Now.Date;
            var days = string.Join(",", Enumerable.Range(0, 3).Select(i => $"\"{today.AddDays(i):yyyy-MM-dd}\""));
            var body = request.RequestUri.Host.StartsWith("geocoding", StringComparison.Ordinal)
                ? !Found ? "{}"
                  : request.RequestUri.Query.Contains("Lehota", StringComparison.Ordinal)
                      ? """{"results":[{"name":"Vymyslená Lehota","latitude":48.2,"longitude":17.5,"elevation":140,"country":"Slovensko","admin1":"Trnavský kraj","admin2":"okres Prvý"},{"name":"Vymyslená Lehota","latitude":48.9,"longitude":19.6,"elevation":612,"country":"Slovensko","admin1":"Banskobystrický kraj","admin2":"okres Druhý"}]}"""
                      : """{"results":[{"name":"Vymyslené Mesto","latitude":48.7,"longitude":19.15,"country":"Slovensko"}]}"""
                : "{\"current\":{\"temperature_2m\":-2.6,\"apparent_temperature\":-6.4,\"weather_code\":71,\"wind_speed_10m\":11.8,\"is_day\":1}," +
                  "\"daily\":{\"time\":[" + days + "],\"weather_code\":[71,3,1],\"temperature_2m_max\":[-0.4,2.4,4.9],\"temperature_2m_min\":[-5.2,-3.1,-1.0]," +
                  "\"precipitation_probability_max\":[70,40,10]}}";
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// The Explorer's context menu: each selected file starts UNIVERSUM, which hands its command line to the running one
    /// over the pipe; the running one opens the right tool.
    /// </summary>
    private static async Task TestContextMenuAsync(MainViewModel model)
    {
        var file = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv");
        var request = Universum.Core.Shell.ShellRequest.Parse(["--informacie", file]);
        await model.HandleAsync(request);
        Check(model.SelectedTool.Page == model.Info && model.Info.File?.Path == file, "context menu: Informácie o súbore opens the Information page");

        // two files selected for Single spracovanie: the Explorer starts two UNIVERSUMs, the second arrives later; they are
        // collected until none came for a moment, handled once in a stable order, and the file left out is named
        var menu = Path.Combine(_outputDirectory, "menu");
        Directory.CreateDirectory(menu);
        var first = Path.Combine(menu, "a-film.mkv");
        var second = Path.Combine(menu, "b-film.mkv");
        File.Copy(file, first, overwrite: true);
        File.Copy(file, second, overwrite: true);
        var notes = new List<string>();
        var notify = model.Notify;
        model.Notify = notes.Add;
        var early = model.HandleAsync(Universum.Core.Shell.ShellRequest.Parse(["--single", second]));
        await Task.Delay(400);
        var late = model.HandleAsync(Universum.Core.Shell.ShellRequest.Parse(["--single", first]));
        await Task.WhenAll(early, late);
        model.Notify = notify;
        Check(model.SelectedTool.Page == model.Rules && model.Rules.Plan?.Target.Source.Path == first && notes.Count == 1 &&
              notes[0].Contains("Neotvorené (1):\nb-film.mkv", StringComparison.Ordinal),
            $"context menu: files of one action handled once, in order, the rest named – {model.Rules.Plan?.Target.Source.Path} | {string.Join(" / ", notes)}");

        using var instance = SingleInstance.TryAcquire();
        Check(instance is not null, "single instance: the first UNIVERSUM owns the name");
        Check(SingleInstance.TryAcquire() is null, "single instance: a second start sees the running one");
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        instance?.Listen(args => received.TrySetResult(args));
        var sent = await Task.Run(() => SingleInstance.Forward(["--zlucovanie", file], TimeSpan.FromSeconds(5)));
        var arrived = await Task.WhenAny(received.Task, Task.Delay(5000)) == received.Task ? received.Task.Result : [];
        Check(sent && arrived.SequenceEqual(["--zlucovanie", file]), "single instance: the second start hands its files over the pipe – " + string.Join(" ", arrived));
    }

    private sealed class FakeTitleServices : System.Net.Http.HttpMessageHandler
    {
        public string Tmdb { get; set; } = "";

        public string Histra { get; set; } = "";

        public System.Net.HttpStatusCode HistraStatus { get; set; } = System.Net.HttpStatusCode.OK;

        public List<string> Requests { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var histra = request.RequestUri.Host == "histra.net";
            var results = histra ? Histra : Tmdb;
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(histra ? HistraStatus : System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new System.Net.Http.StringContent("{\"results\":[" + results + "]}", Encoding.UTF8, "application/json"),
            });
        }
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
            await batch.AnalyzeAsync();
            Check(batch.Groups.Count == 1 && batch.Groups[0].Title == "Zlatý kapor", "batch: the group is named by its film – " + batch.Groups.FirstOrDefault()?.Title);
            Check(batch.DetectAudioCommand.CanExecute(null), "batch: speech recognition offered for audio without a language");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await batch.DetectAudioCommand.ExecuteAsync(null);
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
                var result = await Core.Speech.SpokenLanguage.DetectAsync(tools, services.Runner, synthetic.Path, ordinal, synthetic.Duration);
                Write($"speech detail: audio {ordinal + 1} spoken {spoken} → {result.Language ?? "?"}, margin sk−cs {result.SlovakMargin?.ToString("0.000") ?? "–"}, " +
                      $"pieces {string.Join(" ", result.Pieces.Select(p => $"{p.Language}:{p.Probability:0.00}"))}, doubt: {result.Doubt}");
            }

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
