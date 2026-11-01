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
        await Step("languages", () => TestLanguagesAsync(services));

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

        // owner, 30. 10. 2026: every language of MKVToolNix in the lists (a film in Filipino), the common ones on top
        Check(Languages.All.Count > 7000 && Languages.All.Take(Languages.Common.Count).SequenceEqual(Languages.Common) &&
              Languages.Find("fil")?.SlovakName == "filipínčina" && LanguageChoice.Common.Any(c => c.Display == "filipínčina (fil)") &&
              FileLanguageItem.All.Count == Languages.All.Count,
            $"languages: all of MKVToolNix ({Languages.All.Count}), the common ones first, Filipino among them");
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
        PremieresPageViewModel.AutoLoad = false;
        SeriesNewsPageViewModel.AutoLoad = false;
        var window = new MainWindow(model) { Width = 1180, Height = 740, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        await model.RefreshStatusAsync();
        await model.Info.LoadAsync(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"));
        Check(model.Info.Tracks.Count == 6, $"information page shows 6 tracks ({model.Info.Tracks.Count})");
        Check(model.Info.Error.Length == 0, "information page shows no error: " + model.Info.Error);
        Check(model.SelectedTool.Page == model.Dashboard && model.Tools[0].Title == "Dashboard", "the program starts with the Dashboard");

        // owner, 31. 10. 2026: Rozhranie CZ/EN – Slovak by default; Czech and English after a restart (the texts by their Slovak key)
        Localization.Loc.Init("en");
        var englishTexts = (Localization.Loc.T("Uložiť"), Localization.Loc.T("Hromadné spracovanie"), Localization.Loc.T("Vymyslený text bez prekladu"));
        Localization.Loc.Init("cs");
        var czechText = Localization.Loc.T("Hromadné spracovanie");
        Localization.Loc.Init(services.Settings.UiLanguage);
        Check(englishTexts == ("Save", "Batch processing", "Vymyslený text bez prekladu") && czechText == "Hromadné zpracování" && Localization.Loc.Language == "sk",
            $"languages: English and Czech texts, Slovak by default – {englishTexts} {czechText}");
        model.Settings.UiLanguage = "en";
        Check(services.Settings.UiLanguage == "en" && AppSettings.Load().UiLanguage == "en", "languages: the choice kept for the next start");
        model.Settings.UiLanguage = "sk";
        Check(string.Join(" | ", model.ToolsView.Groups!.Cast<System.Windows.Data.CollectionViewGroup>()
                  .Select(g => $"{(g.Name is ToolGroup { IsFramed: true } group ? group.Title : "-")}: {string.Join(", ", g.Items.Cast<ToolItem>().Select(t => t.Title))}")) ==
              "-: Dashboard | NOVINKY A OBSAH: Premiéry, Novinky – Seriály, Listy z HISTRA, TMDB poster, Zaujímavosti, Návod | POZNÁMKY: Kalendár, Plánované úlohy, Poznámky, Návod | " +
              "MULTIPLEXING: Zlučovanie, Úprava hlavičky, Kapitoly, Informácie, Návod | " +
              "EDITÁCIA VIDEOSTÔP (video, audio, titulky): Úprava videostopy, Úprava audiostopy, Úprava titulkostopy, Návod | " +
              "ŠABLÓNY (TEMPLATE): Single spracovanie, Hromadné spracovanie, MEGA spracovanie, Návod | ŠTATISTIKA: Fronta úloh, Výstup úloh, Záznamy spracovania, Nastavenia, Návod",
            "left rail: Dashboard, then the groups Novinky a obsah, Poznámky, Multiplexing, Šablóny and Štatistika, each with its guide last");
        model.Open(model.Info);
        await Screenshot(window, "main-info.png");

        foreach (var tool in model.Tools)
        {
            model.SelectedTool = tool;
            await Settle();
        }

        await TestDashboardAsync(model, window, services);
        await TestHistraListsAsync(model, window, services);
        await TestPremieresAsync(model, window, services);
        await TestInterestsAsync(model, window, services);

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
        for (var wait = 0; wait < 600 && model.Rules.IsReadingSubtitles; wait++)
        {
            await Task.Delay(100);
        }

        // owner, 30. 10. 2026: the language of every text subtitle track is read – the Czech full subtitles certainly Czech
        Check(model.Rules.Rows.FirstOrDefault(r => r.TrackId == 4)?.LanguageNote.StartsWith("návrh UNIVERSUM: isté", StringComparison.Ordinal) == true,
            "rules: the subtitles' language read from their text – " + model.Rules.Rows.FirstOrDefault(r => r.TrackId == 4)?.LanguageNote);
        Check(model.Rules.Plan?.Target.IsBlocked == true, "rules: undetermined languages block the file");
        Check(!model.Rules.ApplyCommand.CanExecute(null), "rules: apply disabled while blocked");
        Check(services.Library is not null || model.Rules.Target.Contains("výstupný priečinok", StringComparison.Ordinal),
            "rules: asks for the output folder – " + model.Rules.Target);
        await Screenshot(window, "rules-undetermined.png");
        model.Rules.ChooseLanguage(2, "cs");
        model.Rules.ChooseLanguage(3, "cs");

        // owner, 29. 10. 2026: in Single spracovanie too – tracks moved up or down, a language written in the file corrected
        string RuleSubtitles() => string.Join(",", model.Rules.Plan!.Target.Tracks.Where(t => t.Type == TrackType.Subtitles)
            .Select(t => t.Language.Primary + (t.Flags.Forced ? "!" : "")));
        Check(RuleSubtitles() == "cs!,cs,en", "rules: subtitles in the order of the rules – " + RuleSubtitles());
        Check(model.Rules.Plan!.Target.For(3).Name == "Forced" && model.Rules.Plan.Target.For(4).Name is null,
            "rules: forced subtitles are named Forced, the other names removed (owner, 29. 10. 2026)");
        model.Rules.MoveTrack(5, -1);
        Check(RuleSubtitles() == "cs!,en,cs" && model.Rules.Rows.Where(r => r.Target.Type == TrackType.Subtitles).Select(r => r.TrackId).SequenceEqual([3, 5, 4]),
            "rules: English subtitles moved up – " + RuleSubtitles());
        await Screenshot(window, "rules-track-order.png");
        model.Rules.MoveTrack(5, 1);
        Check(RuleSubtitles() == "cs!,cs,en", "rules: and back – " + RuleSubtitles());
        Check(model.Rules.Rows.First(r => r.TrackId == 1).ChosenLanguage == Languages.English, "rules: the file's language is shown in the list");
        model.Rules.ChooseLanguage(1, "sk");
        Check(model.Rules.Plan!.Target.For(1).Language.Primary == "sk", "rules: a language written in the file corrected");
        model.Rules.ChooseLanguage(1, "en");
        Check(model.Rules.Plan!.Target.For(1).Language.Primary == "en" && !model.Rules.Plan.Target.For(1).LanguageChanged, "rules: the file's own language again");

        // owner, 30. 10. 2026: Track name – written in Single spracovanie, it wins over the rules' name; the rules' one again
        Check(model.Rules.Rows.First(r => r.TrackId == 3) is { CanName: true, EditedName: "Forced" }, "rules: the name to write is the result's");
        model.Rules.ChooseName(3, "Forced – vymyslené");
        model.Rules.ChooseName(1, " Vymyslený zvuk ");
        Check(model.Rules.Plan!.Target.For(3).Name == "Forced – vymyslené" && model.Rules.Plan.Target.For(1).Name == "Vymyslený zvuk" &&
              model.Rules.Rows.First(r => r.TrackId == 1).EditedName == "Vymyslený zvuk", "rules: the names written are used");
        model.Rules.ChooseName(3, "Forced");
        model.Rules.ChooseName(1, "");
        Check(model.Rules.Plan!.Target.For(3).Name == "Forced" && model.Rules.Plan.Target.For(1).Name is null, "rules: the rules' name again, an empty one = no name");
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
        await TestMegaSourcesAsync(model, window, services, work);
        await TestChaptersAsync(model, window, work);
        await TestSubtitlesAsync(model, window, services, work);
        await TestAudioAsync(model, window, services, work);
        await TestVideoAsync(model, window, services, work);
        await TestPostersAsync(model, window, services, library);
        TestSeriesMemory(model, services, work);

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

        await TestSeriesNewsAsync(model, window, services);
        var sounds = new List<bool>();
        services.PlayFinishSound = sounds.Add;
        await TestBatchPageAsync(model, window, services);
        await WaitFor(() => sounds.Count > 0);
        Check(sounds.Count > 0 && services.Settings.FinishSound, "queue: the sound of the end after Hromadné spracovanie");
        await TestProcessingLogsAsync(model, window, services);

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

        // "Otvoriť v UNIVERSUM" from the Explorer: a folder opens in batch processing (the batch folder's originals went to
        // the Recycle Bin in the test of Zmazať originály; the folder of the rules page still has its file)
        await model.OpenPathAsync(Path.Combine(_outputDirectory, "rules"));
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
        set.Tracks[2].Language = set.Tracks[2].LanguageChoices.First(c => c.Tag.Primary == "cs");
        set.Tracks[3].Language = set.Tracks[3].LanguageChoices.First(c => c.Tag.Primary == "cs");
        set.Tracks[^1].Language = set.Tracks[^1].LanguageChoices.First(c => c.Tag.Primary == "sk");
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

    /// <summary>
    /// Listy z HISTRA with invented lists (HISTRA and the poster server answer here, nothing goes to the internet): the
    /// import window with preview posters, a list read in pages, only GET requests, the detail with IDs and links,
    /// synchronisation keeping a list HISTRA no longer has, removal, and the lists kept for the next start.
    /// </summary>
    private static async Task TestHistraListsAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var page = model.HistraLists;
        model.Open(page);
        Check(!page.CanUse && page.Requirement.Contains("token HISTRA", StringComparison.Ordinal) && page.IsEmpty, "lists: a HISTRA token is needed – " + page.Requirement);

        var histra = new FakeHistra();
        var http = new System.Net.Http.HttpClient(histra);
        services.HistraListsForSelfTest = new Core.Library.Histra.HistraLists(new Core.Library.Histra.HistraClient(http, "hst_selftest-0123456789", new RequestPacer(1000)));
        services.PostersForSelfTest = new PosterCache(http, Path.Combine(_outputDirectory, "posters"), PosterCache.DefaultServer);
        page.Refresh();
        Check(page.CanUse && page.ImportCommand.CanExecute(null), "lists: import possible with HISTRA");

        var import = await page.PrepareImportAsync();
        Check(import is { Rows.Count: 3 } && import.Rows.All(r => !r.IsChecked) && import.Rows[0].Posters.Count == 5,
            "lists: the overview of the lists with five preview posters – " + page.Status);
        if (import is null)
        {
            return;
        }

        var importWindow = new HistraImportWindow(import) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        importWindow.Show();
        await WaitFor(() => import.Rows.SelectMany(r => r.Posters).All(p => p.Image is not null));
        await Screenshot(importWindow, "histra-import.png");
        importWindow.Close();

        import.Rows[0].IsChecked = true;
        import.Rows[1].IsChecked = true;
        Check(import.ImportText == "Importovať vybrané (2)", "lists: two lists chosen – " + import.ImportText);
        await page.ImportAsync(import.Chosen);
        Check(page.Lists.Count == 2 && page.Lists[0].All.Count == 30 && page.Lists[1].All.Count == 3,
            $"lists: imported with all titles, read in pages – {page.Status}");
        Check(histra.Requests.All(r => r.Method == "GET") && histra.Requests.Count(r => r.Address.Contains("/api/v1/lists/1?", StringComparison.Ordinal)) == 2,
            "lists: only read from HISTRA, the long list in pages – " + string.Join(" | ", histra.Requests.Select(r => $"{r.Method} {r.Address}")));
        Check(page.Lists[0].Visible.Count == HistraListRow.Collapsed && page.Lists[0].ExpandText.StartsWith("Zobraziť všetky (30)", StringComparison.Ordinal),
            "lists: the first posters, then Zobraziť všetky – " + page.Lists[0].ExpandText);
        Check(File.Exists(AppSettings.HistraListsFile), "lists: kept in a file of this account");
        var second = page.Lists[1];
        Check(!page.Lists[0].MoveUpCommand.CanExecute(null) && page.Lists[0].MoveDownCommand.CanExecute(null) && !second.MoveDownCommand.CanExecute(null),
            "lists: the arrows at the ends of the order");
        second.MoveUpCommand.Execute(null);
        Check(page.Lists[0] == second && new HistraListsPageViewModel(services).Lists[0].Name == second.Name && second.MoveDownCommand.CanExecute(null),
            "lists: moved up, the order kept for the next start – " + page.Status);
        page.MoveTo(second, 1);
        Check(page.Lists[1] == second, "lists: moved back (as when dragged with the mouse)");

        page.Filter = "pribeh 2";
        Check(page.Lists[0].Visible.Count == 9 && page.Lists[0].FilterResult == "Zhoda: 9", "lists: search without diacritics – " + page.Lists[0].FilterResult);
        page.Filter = "";

        var opened = new List<Uri>();
        var browser = Browser.Open;
        Browser.Open = opened.Add;
        var film = page.Lists[0].All[0];
        film.OpenCommand.Execute(null);
        Check(page.Selected == film && film.Caption == "Invented Story 1 (1991)" && film.LocalTitle == "Vymyslený príbeh 1",
            $"lists: the detail of a title – {film.Caption} / {film.LocalTitle}");
        Check(page.Card is { MainName: "Invented Story 1 (1991)" } card && card.OtherNames.SequenceEqual(["Vymyslený príbeh 1 (1991)"]) &&
              card.Links.Any(l => l.Text == "Trakt" && l.Address.AbsoluteUri == "https://trakt.tv/search/tmdb/9001?id_type=movie") && card.Ids.Count == film.Ids.Count,
            $"lists: the card of the title (names, Trakt, IDs) – {page.Card?.MainName} | {string.Join(", ", page.Card?.Links.Select(l => l.Text) ?? [])}");
        Check(string.Join(", ", film.Links.Select(l => $"{l.Text} {l.Address.AbsoluteUri}")) ==
              "HISTRA https://histra.net/movie/1001, TMDB https://www.themoviedb.org/movie/9001, IMDb https://www.imdb.com/title/tt0009001/, " +
              "ČSFD (hľadať) https://www.csfd.cz/hledat/?q=Invented%20Story%201",
            "lists: links of the title – " + string.Join(", ", film.Links.Select(l => $"{l.Text} {l.Address.AbsoluteUri}")));
        Check(string.Join(", ", film.Ids.Select(i => $"{i.Name} {i.Value}")) == "HISTRA 1001, TMDB 9001, IMDb tt0009001, Trakt 5001",
            "lists: IDs of the title – " + string.Join(", ", film.Ids.Select(i => $"{i.Name} {i.Value}")));
        film.Links[0].OpenCommand.Execute(null);
        Check(opened.Count == 1 && opened[0].AbsoluteUri == "https://histra.net/movie/1001", "lists: the HISTRA link opens the title's page");
        Browser.Open = browser;

        // the title shown goes on (owner, 31. 10. 2026): to TMDB poster by its TMDB ID, to processing by its names and year
        var toPoster = page.OpenInPoster;
        var toSingle = page.SendToSingle;
        (MediaKind Kind, int Id)? posterAsked = null;
        (IReadOnlyList<string> Names, int? Year, string Caption)? singleAsked = null;
        page.OpenInPoster = (kind, id) => { posterAsked = (kind, id); return Task.CompletedTask; };
        page.SendToSingle = (kind, names, year, caption) => { singleAsked = (names, year, caption); return Task.FromResult<string?>("vymyslené: nenašiel sa"); };
        await page.ToPosterCommand.ExecuteAsync(null);
        await page.ToSingleCommand.ExecuteAsync(null);
        Check(posterAsked == (MediaKind.Movie, 9001) && singleAsked is { } asked && asked.Names.Contains("Invented Story 1") && asked.Year == 1991 &&
              page.Status == "vymyslené: nenašiel sa",
            $"lists: Otvoriť v TMDB Poster and Do Single spracovania – {posterAsked} | {(singleAsked is { } a ? string.Join("/", a.Names) + " " + a.Year + " " + a.Caption : "")} | {page.Status}");
        page.OpenInPoster = toPoster;
        page.SendToSingle = toSingle;
        await WaitFor(() => page.Lists.SelectMany(l => l.Visible).All(r => r.SmallPoster.Image is not null) && film.LargePoster.Image is not null);
        await Screenshot(window, "histra-lists.png");

        histra.Deleted.Add(2);
        await page.SyncAsync();
        Check(page.Lists.Count == 2 && page.Lists[1].Missing && page.Lists[1].All.Count == 3 && page.Status.Contains("v HISTRA už nie je: 1", StringComparison.Ordinal),
            "lists: a list HISTRA no longer has stays, marked – " + page.Status);
        page.Confirm = _ => true;
        page.Lists[1].RemoveCommand.Execute(null);
        Check(page.Lists.Count == 1, "lists: removed from UNIVERSUM");
        var again = new HistraListsPageViewModel(services);
        Check(again.Lists.Count == 1 && again.Lists[0].All.Count == 30 && again.Lists[0].All[0].Item.Ids.Tmdb == 9001, "lists: kept for the next start");

        services.HistraListsForSelfTest = null;
        services.PostersForSelfTest = null;
        page.Refresh();
        page.Selected = null;
    }

    /// <summary>
    /// Premiéry with invented sources (an invented page read by imported rules, TVmaze and TMDB answer here, nothing goes
    /// to the internet): the rules imported in the settings, the four tables, robots.txt asked first, only the days of
    /// the window read, periodicity and episode counts from TVmaze, and the kept days used again without requests.
    /// </summary>
    private static async Task TestPremieresAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var news = new FakeNews();
        var http = new System.Net.Http.HttpClient(news);
        var cache = Path.Combine(_outputDirectory, "news-cache");
        Core.News.WebPageClient.PagesPerSecond = 1000;
        services.SeriesNewsForSelfTest = new Core.News.SeriesNews(new Core.News.WebPageClient(http), new Core.News.TvMazeClient(http, new RequestPacer(1000)), new Core.News.NewsCache(cache));
        var tmdb = new Core.Library.Tmdb.TmdbClient(http, "selftest-key", null, new RequestPacer(1000));
        services.FilmsForSelfTest = new Core.News.FilmPremieres(tmdb);
        services.SeriesTmdbForSelfTest = new Core.News.SeriesTmdb(tmdb);
        services.PostersForSelfTest = new PosterCache(http, Path.Combine(_outputDirectory, "posters"), PosterCache.DefaultServer);

        var rulesFile = Path.Combine(_outputDirectory, "vymysleny-zdroj.json");
        File.WriteAllText(rulesFile, FakeNews.Rules);
        var sources = model.Settings.NewsSources;
        Check(sources.Rows.Count == 2 && sources.Rows[0].Name == "TVmaze" && sources.Rows[1].Name == "TMDB – filmy", "sources: TVmaze and TMDB by default");
        File.WriteAllText(rulesFile + ".bad", "{}");
        Check(!sources.ImportFile(rulesFile + ".bad") && sources.Status.StartsWith("Zdroj sa nepridal", StringComparison.Ordinal),
            "sources: a file that is no rules is refused – " + sources.Status);
        Check(sources.ImportFile(rulesFile) && sources.Rows.Count == 3 && sources.Rows[2].Address == "rozpis.example",
            "sources: an invented page added from its rules – " + sources.Status);
        model.SelectedTool = model.Tools.First(t => t.Page == model.Settings);
        await Screenshot(window, "settings-sources.png");

        var page = model.Premieres;
        model.Open(page);
        await page.LoadAsync(refresh: true);
        var films = page.Tab(PremiereTab.FilmDomestic).Rows.OfType<PremiereFilmRow>().ToList();
        var world = page.Tab(PremiereTab.FilmWorld).Rows.OfType<PremiereFilmRow>().ToList();
        var domesticSeries = page.Tab(PremiereTab.SeriesDomestic).Rows.OfType<PremiereSeriesRow>().ToList();
        var worldSeries = page.Tab(PremiereTab.SeriesWorld).Rows.OfType<PremiereSeriesRow>().ToList();
        Check(!page.HasProblems, "premieres: all sources read – " + page.Problems);
        Check(films.Count == 2 && films[0].IsToday && films[0].When == "DNES" && films[1].Caption == $"The Invented Premiere ({FakeNews.Day(3).Year})" &&
              films[1].Where == $"CZ · SK {FakeNews.Day(4).Day}. {FakeNews.Day(4).Month}." && films[1].Availability.Contains("VOD ", StringComparison.Ordinal) && films[1].Availability.StartsWith("kino ", StringComparison.Ordinal) &&
              films[1].Production == "ČR, SR · Invented Pictures" && films[1].When == "o 3 dni",
            "premieres: Czech and Slovak films in the cinemas here, the nearest first – " + string.Join(" | ", films.Select(f => $"{f.Caption} {f.Date} {f.When} {f.Where} {f.Production} {f.Availability}")));
        Check(world.Count == 1 && world[0].Caption.StartsWith("Later Invented Film", StringComparison.Ordinal),
            "premieres: only coming films of the world, the past one is in the history – " + string.Join(" | ", world.Select(f => $"{f.Caption} {f.Date}")));
        Check(domesticSeries.Count == 1 && domesticSeries[0].Caption == "Invented Show (2026)" && domesticSeries[0].LocalTitle == "Vymyslený seriál" &&
              domesticSeries[0].Production == "ČT1" && domesticSeries[0].Periodicity == "každý pracovný deň (po–pi)" && domesticSeries[0].Episodes == "10 epizód",
            "premieres: a new series on a Czech channel, its rhythm from TVmaze – " +
            string.Join(" | ", domesticSeries.Select(r => $"{r.Caption} {r.Production} {r.What} {r.Periodicity} {r.Episodes}")));
        Check(worldSeries.Count == 3 && worldSeries.Any(r => r.Caption == "Invented Stream (2024)" && r.What.StartsWith("3. séria", StringComparison.Ordinal) &&
                                                            r.Periodicity.StartsWith("raz do týždňa – ", StringComparison.Ordinal) && r.Episodes == "8 epizód"),
            "premieres: new series and seasons of the world – " + string.Join(" | ", worldSeries.Select(r => $"{r.Caption} {r.Production} {r.What} {r.Periodicity} {r.Episodes}")));
        var pages = news.Requests.Where(r => r.Contains("rozpis.example/den/", StringComparison.Ordinal)).ToList();
        Check(pages.Count == 1 + Core.News.SeriesNews.PageDaysAhead + 1 && pages.Distinct().Count() == pages.Count &&
              news.Requests.IndexOf("https://rozpis.example/robots.txt") < news.Requests.FindIndex(r => r.Contains("rozpis.example/den/", StringComparison.Ordinal)),
            $"premieres: robots.txt first, then each day of the window once – Czech channels {Core.News.SeriesNews.PageDaysAhead} days ahead ({pages.Count} days)");
        Check(news.Requests.All(r => r.StartsWith("https://", StringComparison.Ordinal) &&
                                     new Uri(r).Host is "rozpis.example" or "api.tvmaze.com" or "api.themoviedb.org" or "image.tmdb.org"),
            "premieres: only the sources were asked");
        Check(news.UserAgents.All(a => a.StartsWith("UNIVERSUM/", StringComparison.Ordinal)), "premieres: UNIVERSUM introduces itself to the pages");

        // marks: chosen and a note, kept; only the chosen ones
        films[1].IsChosen = true;
        films[1].Note = "  teším sa  ";
        page.OnlyChosen = true;
        Check(page.Tab(PremiereTab.FilmDomestic).Rows.Count == 1 && page.Tab(PremiereTab.FilmDomestic).Rows[0] == films[1] &&
              services.Marks.Get(Core.News.MarkStore.Film(films[1].Film.TmdbId)) == new Core.News.TitleMark(true, "teším sa"),
            "premieres: Zobraziť vybrané shows only the chosen film, its note kept");
        page.OnlyChosen = false;
        page.Filter = "teSim";
        Check(page.Tab(PremiereTab.FilmDomestic).Rows.Count == 1, "premieres: the note is searched too");
        page.Filter = "";
        // owner, 29. 10. 2026: a link is only a word beside the note's text, the address hides in it
        films[1].AddLink("Kino", new Uri("https://kino.example/vymyslena-premiera"));
        films[1].AddLink("  ", new Uri("https://www.trailer.example/vymyslena"));
        Check(films[1].NoteText == "teším sa" && string.Join(", ", films[1].Links.Select(l => l.Name)) == "Kino, trailer.example",
            "premieres: the note's text without addresses, its links as words – " + films[1].Note);
        films[1].Links[1].RemoveCommand.Execute(null);
        films[1].NoteText = "teším sa veľmi";
        Check(films[1].Note == "teším sa veľmi [Kino](https://kino.example/vymyslena-premiera)" && films[1].Links.Count == 1,
            "premieres: a link removed, the text changed, the other link kept – " + films[1].Note);
        Check(films[1].HasLinks && services.Marks.Get(Core.News.MarkStore.Film(films[1].Film.TmdbId)).Note.Contains("vymyslena-premiera", StringComparison.Ordinal),
            "premieres: a web address in the note is kept and shown as a link");
        page.PosterScale = 2;
        Check(page.PosterWidth == 76 && page.PosterHeight == 114 && services.Settings.PremierePosterScale == 2, "premieres: posters up to twice as large, kept");

        page.SelectedTab = page.Tab(PremiereTab.FilmDomestic);
        await WaitFor(() => films.All(f => f.Poster.Image is not null));
        await Screenshot(window, "premieres-films.png");
        page.PosterScale = 1;
        page.SelectedTab = page.Tab(PremiereTab.SeriesWorld);
        await WaitFor(() => worldSeries.All(r => r.Poster.Image is not null));
        Check(worldSeries.All(r => r.Poster.Image is not null), "premieres: posters of the series found on TMDB");
        await Screenshot(window, "premieres-series.png");
        page.ShowPosters = false;
        Check(!services.Settings.PremierePosters, "premieres: posters can be hidden (kept in the settings)");
        await Screenshot(window, "premieres-series-noposters.png");
        page.ShowPosters = true;

        page.ShowRecent = true;
        page.SelectedTab = page.Tab(PremiereTab.FilmWorld);
        Check(page.SelectedTab.Rows.Count == 1 && page.SelectedTab.Rows[0].Caption.StartsWith("World Invented Film", StringComparison.Ordinal) &&
              page.SelectedTab.Rows[0].When == "pred 2 dňami",
            "premieres: Posledný týždeň shows the premieres of the last days – " + string.Join(", ", page.SelectedTab.Rows.Select(r => $"{r.Caption} {r.When}")));
        page.ShowHistory = true;
        Check(page.SelectedTab.Rows.Count == 1 && page.SelectedTab.Rows[0] is PremiereFilmRow { Caption: var past } && past.StartsWith("World Invented Film", StringComparison.Ordinal),
            "premieres: the film that is over is in the history");
        await Screenshot(window, "premieres-history.png");
        page.ShowAll = true;
        var everything = page.SelectedTab.Rows;
        Check(everything.Count == page.SelectedTab.All.Count && everything.Any(r => r.SortDate < DateOnly.FromDateTime(DateTime.Now)) &&
              everything.Zip(everything.Skip(1)).All(p => p.First.SortDate >= p.Second.SortDate),
            $"premieres: Zobraziť všetko shows the history and the coming premieres, the latest first ({everything.Count} of {page.SelectedTab.All.Count})");
        page.ShowUpcoming = true;

        int Days() => news.Requests.Count(r => r.Contains("rozpis.example/den/", StringComparison.Ordinal) || r.Contains("/schedule", StringComparison.Ordinal));
        var asked = Days();
        await page.LoadAsync(refresh: false);
        Check(Days() == asked, $"premieres: the kept days are used again without asking the sources ({asked} → {Days()})");

        var requests = news.Requests.Count;
        var again = new PremieresPageViewModel(services);
        again.Shown();
        Check(news.Requests.Count == requests && again.Tab(PremiereTab.FilmDomestic).Rows.Count == 2 && again.Tab(PremiereTab.SeriesWorld).Rows.Count == 3 &&
              again.Status.StartsWith("Uložené premiéry z", StringComparison.Ordinal) && File.Exists(AppSettings.PremieresFile),
            "premieres: kept for the next start and shown at once, without asking the sources – " + again.Status);

        services.SeriesNewsForSelfTest = null;
        services.FilmsForSelfTest = null;
        services.SeriesTmdbForSelfTest = null;
        services.PostersForSelfTest = null;
        services.SetNewsSources(Core.News.NewsSource.Defaults);
    }

    /// <summary>
    /// Zaujímavosti with invented sources (a page naming its RSS feed, a Czech Atom feed, pictures) and an invented DeepL:
    /// sources added in the settings, articles read, translated once, kept for the next start, filtered and starred.
    /// </summary>
    private static async Task TestInterestsAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var feeds = new FakeFeeds();
        var http = new System.Net.Http.HttpClient(feeds);
        Core.News.WebPageClient.PagesPerSecond = 1000;
        services.InterestsForSelfTest = new Core.News.Interests(new Core.News.WebPageClient(http), new Core.News.InterestStore(AppSettings.InterestsFile));
        services.TranslatorForSelfTest = new Core.News.DeepLClient(http, "selftest-0000-0000-0000-000000000000:fx");
        services.ImagesForSelfTest = new Core.News.WebImageCache(http, Path.Combine(_outputDirectory, "images"));

        var settings = model.Settings.InterestSources;
        Check(settings.AddSource("Vymyslené filmy", "https://filmy.example/", "en") && !settings.AddSource("Zlý", "http://filmy.example/", "en"),
            "interests: a source added in the settings (https only) – " + settings.Status);
        var file = Path.Combine(_outputDirectory, "vymyslene-zdroje.json");
        File.WriteAllText(file, """{"format":"UNIVERSUM-zaujimavosti 1","sources":[{"name":"Vymyslené kino","url":"https://kino.example/atom","language":"cs"}]}""");
        Check(settings.ImportFile(file) && services.FeedSources.Count == 2 && settings.Rows.Count == 2, "interests: sources imported from a file – " + settings.Status);

        var page = model.Interests;
        model.Open(page);
        Check(page.IsEmpty, "interests: nothing kept yet");
        await page.RefreshAsync();
        Check(!page.HasProblems && page.Featured is { } featured && featured.Title == "SK: Invented festival opens" && page.Cards.Count == 2,
            $"interests: this week's articles, translated – {page.Featured?.Title} + {page.Cards.Count} | {page.Problems}");
        var translated = feeds.DeepL;
        Check(translated == 1 && page.Cards.All(c => c.Title.StartsWith("SK: ", StringComparison.Ordinal)), "interests: one batch to DeepL, Czech too");
        page.ShowAll = true;
        Check(page.Cards.Count == 3, "interests: Všetko shows the history");
        page.Source = "Vymyslené kino";
        Check(page.Featured?.Source == "Vymyslené kino" && page.Cards.Count == 0, "interests: only one source");
        page.Source = null;
        page.ShowOriginal = true;
        Check(page.Featured?.Title == "Invented festival opens", "interests: the original text");
        page.ShowOriginal = false;
        page.Featured!.FavoriteCommand.Execute(null);
        page.OnlyFavorites = true;
        Check(page.Featured?.IsFavorite == true && page.Cards.Count == 0, "interests: the starred article");
        page.OnlyFavorites = false;
        page.ShowWeek = true;
        await WaitFor(() => page.Featured?.Image is not null && page.Cards.All(c => !c.HasImage || c.Image is not null));
        Check(page.Featured?.Image is not null, "interests: pictures of the articles");
        await Screenshot(window, "zaujimavosti.png");

        await page.RefreshAsync();
        Check(feeds.DeepL == translated, "interests: nothing translated twice");
        var again = new InterestsPageViewModel(services);
        again.ShowAll = true;
        again.Shown();
        Check(again.Cards.Count + (again.Featured is null ? 0 : 1) == 4 && again.Status.StartsWith("Uložené zaujímavosti", StringComparison.Ordinal),
            "interests: kept for the next start");
        Check(feeds.Requests.All(r => new Uri(r).Host is "filmy.example" or "kino.example" or "api-free.deepl.com"), "interests: only the sources and DeepL were asked");
        model.SelectedTool = model.Tools.First(t => t.Page == model.Settings);
        await Screenshot(window, "settings-interests.png");

        services.SetFeedSources([]);
        services.InterestsForSelfTest = null;
        services.TranslatorForSelfTest = null;
        services.ImagesForSelfTest = null;
    }

    /// <summary>
    /// Novinky – Seriály with the invented sources: the input folder (one setting for all pages), one ticked episode
    /// found goes to Single spracovanie with its series, several episodes of a series go to Hromadné spracovanie (only
    /// those files), an episode not in the folder waits and is found by "Skontrolovať znova".
    /// </summary>
    private static async Task TestSeriesNewsAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var news = new FakeNews();
        var http = new System.Net.Http.HttpClient(news);
        // an old day kept from earlier: the history
        var cache = new Core.News.NewsCache(Path.Combine(_outputDirectory, "news-cache-2"));
        cache.Put("Vymyslený rozpis", FakeNews.Day(-20), [new Core.News.SeriesEpisode
        {
            Source = "Vymyslený rozpis", Region = Core.News.NewsRegion.Domestic, Title = "Starý seriál", OriginalTitle = "Starý seriál", Year = 2019,
            Season = 2, Number = 5, AirDate = FakeNews.Day(-20), Channel = "Nova",
        }]);
        services.SeriesNewsForSelfTest = new Core.News.SeriesNews(new Core.News.WebPageClient(http), new Core.News.TvMazeClient(http, new RequestPacer(1000)), cache);
        services.SeriesTmdbForSelfTest = new Core.News.SeriesTmdb(new Core.Library.Tmdb.TmdbClient(http, "selftest-key", null, new RequestPacer(1000)));
        services.PostersForSelfTest = new PosterCache(http, Path.Combine(_outputDirectory, "posters"), PosterCache.DefaultServer);
        services.SetNewsSources([.. Core.News.NewsSource.Defaults, new Core.News.NewsSource
        {
            Name = "Vymyslený rozpis", Kind = Core.News.NewsSourceKind.Page, Rules = Core.News.PageRules.Load(FakeNews.Rules),
        }]);

        // the input folder: the new Czech series and two days of a Czech series (copies of a test file)
        var input = Path.Combine(_outputDirectory, "vstup");
        var sample = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv");
        int Number(int offset) => 10 + FakeNews.Day(offset).DayNumber % 50;
        Directory.CreateDirectory(Path.Combine(input, "Invented Show"));
        File.Copy(sample, Path.Combine(input, "Invented Show", "Invented.Show.S01E01.1080p.WEB.mkv"), overwrite: true);
        File.Copy(sample, Path.Combine(input, $"Domaci serial S05E{Number(0):00}.mkv"), overwrite: true);
        File.Copy(sample, Path.Combine(input, $"Domaci.serial.S05E{Number(-1):00}.mkv"), overwrite: true);
        File.Copy(sample, Path.Combine(input, "Iny serial S05E01.mkv"), overwrite: true);
        var before = Directory.GetFiles(input, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);

        var page = model.SeriesNews;
        model.Open(page);
        await page.LoadAsync(refresh: true);
        var domestic = page.Tabs[0];
        Check(!page.HasProblems && string.Join(", ", domestic.Rows.Select(r => r.Caption)) == "Domáci seriál (2020), Invented Show (2026)",
            "novinky: only the series, alphabetically – " + string.Join(", ", domestic.Rows.Select(r => r.Caption)) + " " + page.Problems);
        var home = domestic.Rows[0];
        var show = domestic.Rows[1];
        Check(home.NearestCode == $"S05E{Number(0):00}" && home.When == "DNES" && home.Episodes.Count == 2 && home.Episodes.All(e => e.CanCheck && e.State == "odvysielaná") &&
              home.Rhythm == "každý deň",
            $"novinky: the last week – the nearest episode, the tree of the aired ones, the rhythm – {home.NearestCode} {home.When} {home.Episodes.Count} {home.Rhythm}");
        page.ShowUpcoming = true;
        var coming = domestic.Rows.First(r => r.Caption == "Domáci seriál (2020)");
        // the invented numbers repeat after 50 days (the same episode is shown once), so the far days are checked by date
        Check(coming.Episodes.Count >= 45 && coming.Episodes.Max(e => e.Episode.AirDate) >= FakeNews.Day(Core.News.SeriesNews.PageDaysAhead - 12) &&
              coming.Episodes[^1].State == "nadchádzajúca" && !coming.Episodes[^1].CanCheck,
            $"novinky: Nadchádzajúce – the episodes to come, {Core.News.SeriesNews.PageDaysAhead} days ahead ({coming.Episodes.Count})");
        page.ShowRecent = true;
        home = domestic.Rows[0];
        show = domestic.Rows[1];
        Check(show.NearestCode == "S01E01" && show.NearestNote.StartsWith("nový seriál", StringComparison.Ordinal), "novinky: a new series – " + show.NearestNote);

        services.InputFolder.Set(null);
        var premiere = show.Episodes[0];
        premiere.IsChecked = true;
        Check(page.SendText == "Odoslať na spracovanie (1)" && show.IsChecked && show.CheckedText == "1/1", "novinky: one episode ticked – " + page.SendText);
        await page.SendAsync();
        Check(page.Status.StartsWith("Najprv nastavte vstupný priečinok", StringComparison.Ordinal) && model.SelectedTool.Page == page,
            "novinky: without the input folder nothing is sent – " + page.Status);
        Check(!services.InputFolder.Set(Path.Combine(services.Library!.Root, "MOVIES")) && services.InputFolder.Message.Contains("MOVIES", StringComparison.Ordinal),
            "input folder: not in the library – " + services.InputFolder.Message);
        Check(services.InputFolder.Set(input) && services.Settings.InputDirectory == Path.GetFullPath(input) && model.Premieres.InputFolder.Folder == Path.GetFullPath(input),
            "input folder: one setting for every page");
        Check(page.Status.StartsWith("Vstupný priečinok je nastavený", StringComparison.Ordinal), "novinky: the old message goes once the folder is set – " + page.Status);

        // owner, 31. 10. 2026: the input folder watched – a film copied in is told (nothing is processed by itself)
        services.WatchSettle = TimeSpan.FromMilliseconds(500);
        services.RestartWatch();
        var arriving = Path.Combine(input, "Vymyslený.Prichádzajúci.Film.2026.mkv");
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"), arriving, overwrite: true);
        await WaitFor(() => services.Arrivals.Any(a => a.EndsWith("Prichádzajúci.Film.2026.mkv", StringComparison.Ordinal)));
        Check(model.HasArrivals && model.ArrivalsText.Contains("Prichádzajúci", StringComparison.Ordinal), "input folder: a new film is told – " + model.ArrivalsText);
        await Screenshot(window, "input-arrivals.png");
        model.DismissArrivalsCommand.Execute(null);
        Check(!model.HasArrivals, "input folder: the notice hidden");
        File.Delete(arriving);

        // the mark of a series: the same in Premiéry
        show.IsChosen = true;
        show.Note = "pozrieť s rodinou";
        Check(model.Premieres.Tab(PremiereTab.SeriesDomestic).All.Any(r => r.Caption == "Invented Show (2026)" && r.IsChosen && r.Note == "pozrieť s rodinou"),
            "novinky: a series chosen here is chosen in Premiéry too");
        page.OnlyChosen = true;
        Check(domestic.Rows.Count == 1 && domestic.Rows[0] == show, "novinky: Zobraziť vybrané");
        page.OnlyChosen = false;
        home.IsExpanded = true;
        await WaitFor(() => domestic.Rows.All(r => r.Poster.Image is not null));
        Check(domestic.Rows.All(r => r.Poster.Image is not null), "novinky: posters of the whole series");
        await Screenshot(window, "novinky.png");
        home.IsExpanded = false;

        await page.SendAsync();
        Check(model.SelectedTool.Page == model.Rules && model.Rules.IsSeries && model.Rules.TitleText == "Invented Show (2026)" &&
              model.Rules.Plan?.Target.Source.Path.EndsWith("Invented.Show.S01E01.1080p.WEB.mkv", StringComparison.Ordinal) == true,
            $"novinky: one file goes to Single spracovanie with its series – {page.Status} | {model.Rules.TitleText} | {model.Rules.EpisodeText}");
        await Screenshot(window, "novinky-single.png");

        model.Open(page);
        home.IsChecked = true;
        Check(home.CheckedText == "2/2" && home.Episodes.Where(e => !e.CanCheck).All(e => !e.IsChecked), "novinky: the series' tick ticks its aired episodes only – " + home.CheckedText);
        var world = page.Tabs[1].Rows.First(r => r.Caption == "World Invented (2024)");
        world.Episodes[0].IsChecked = true;
        await page.SendAsync();
        Check(model.SelectedTool.Page == model.Batch && model.Batch.ChosenFiles?.Count == 2 && model.Batch.IsSeries &&
              model.Batch.SeriesTitle.StartsWith("Domáci seriál (2020)", StringComparison.Ordinal) && model.Batch.Analysis?.FileCount == 2,
            $"novinky: episodes of a series go to Hromadné spracovanie, only their files – {page.Status} | {model.Batch.SeriesTitle} | {model.Batch.Summary}");
        // owner, 1. 11. 2026: the groups in two lists – a series in Skupina Seriál, nothing among the films
        Check(model.Batch.SeriesGroups.Count == model.Batch.Groups.Count && model.Batch.FilmGroups.Count == 0 &&
              (model.Batch.SelectedGroup is null || model.Batch.SelectedSeriesGroup == model.Batch.SelectedGroup && model.Batch.SelectedFilmGroup is null),
            $"batch: a series is in Skupina Seriál – series {model.Batch.SeriesGroups.Count}, films {model.Batch.FilmGroups.Count}");
        Check(page.Waiting.Count == 1 && page.Waiting[0].Episode.OriginalTitle == "World Invented" && page.Status.Contains("čakajú nižšie", StringComparison.Ordinal),
            "novinky: an episode not in the input folder waits – " + page.Status);
        await Screenshot(window, "novinky-batch.png");

        model.Open(page);
        File.Copy(sample, Path.Combine(input, "World.Invented.S02E01.mkv"), overwrite: true);
        await page.CheckAgainCommand.ExecuteAsync(null);
        Check(page.Waiting.Count == 0 && model.SelectedTool.Page == model.Rules && model.Rules.TitleText == "World Invented (2024)",
            "novinky: Skontrolovať znova finds the episode now in the folder – " + page.Status);
        Check(before.All(f => File.Exists(f.Key) && File.GetLastWriteTimeUtc(f.Key) == f.Value), "novinky: files in the input folder are only read");

        // episodes of two series at once: one batch, each into its own series (owner, 29. 10. 2026)
        model.Open(page);
        domestic.Rows.First(r => r.Caption == "Domáci seriál (2020)").IsChecked = true;
        page.Tabs[1].Rows.First(r => r.Caption == "World Invented (2024)").Episodes[0].IsChecked = true;
        await page.SendAsync();
        Check(model.SelectedTool.Page == model.Batch && model.Batch.ChosenFiles?.Count == 3 && model.Batch.SeriesTitle.StartsWith("Domáci seriál (2020)", StringComparison.Ordinal) &&
              model.Batch.OtherSeries.Contains("World Invented (2024)", StringComparison.Ordinal) && page.Ready.Count == 0,
            $"novinky: episodes of two series go to one batch – {page.Status} | {model.Batch.SeriesTitle} | {model.Batch.OtherSeries}");
        await Screenshot(window, "novinky-batch-two.png");

        // Vymazať frontu (owner, 29. 10. 2026): the analysed files and the opened file are forgotten, nothing on the disk changes
        model.Batch.Confirm = _ => true;
        model.Batch.ClearCommand.Execute(null);
        Check(model.Batch.Groups.Count == 0 && model.Batch.ChosenFiles is null && model.Batch.OtherSeries.Length == 0 && model.Batch.Analysis is null,
            "batch: Vymazať frontu forgets the files not processed – " + model.Batch.Summary);
        model.Rules.Confirm = _ => true;
        model.Rules.ClearCommand.Execute(null);
        Check(model.Rules.FileName.Length == 0 && model.Rules.Plan is null && model.Rules.Rows.Count == 0, "single: Vymazať frontu forgets the opened file");
        Check(before.All(f => File.Exists(f.Key)), "Vymazať frontu: the files stay on the disk");

        model.Open(page);
        page.ShowHistory = true;
        await WaitFor(() => domestic.Rows.Count > 0);
        Check(domestic.Rows.Count == 1 && domestic.Rows[0].Caption == "Starý seriál (2019)" && domestic.Rows[0].NearestCode == "S02E05",
            "novinky: older episodes are in the history – " + string.Join(", ", domestic.Rows.Select(r => r.Caption)));
        await Screenshot(window, "novinky-history.png");
        page.ShowAll = true;
        Check(new[] { "Domáci seriál (2020)", "Invented Show (2026)", "Starý seriál (2019)" }.All(c => domestic.Rows.Any(r => r.Caption == c)) &&
              domestic.Rows.First(r => r.Caption == "Domáci seriál (2020)").Episodes.Count >= 45,
            "novinky: Zobraziť všetko shows the history, the last week and the coming episodes – " + string.Join(", ", domestic.Rows.Select(r => $"{r.Caption} {r.Episodes.Count}")));
        page.ShowRecent = true;

        // Kde pozerať: the chosen series and film on services in Slovakia; the chosen titles in the input folder
        services.WatchForSelfTest = new Core.News.WatchProviders(new Core.Library.Tmdb.TmdbClient(http, "selftest-key", null, new RequestPacer(1000)));
        Check(await services.Watchlist.CheckAsync() is null && services.Watchlist.Offered() is var offered &&
              offered.Any(o => o.Title.Caption == "Invented Show (2026)" && o.Title.Text == "Vymyslené+ (SK)" && o.IsNew) &&
              offered.Any(o => o.Title.Text == "Obchod (SK) – požičanie"),
            "watch: where the chosen titles can be watched legally – " + string.Join(" | ", services.Watchlist.Offered().Select(o => $"{o.Title.Caption}: {o.Title.Text}")));
        Check(model.Premieres.Tab(PremiereTab.SeriesDomestic).All.First(r => r.Caption == "Invented Show (2026)").Streaming == "▶ Vymyslené+ (SK)",
            "watch: shown under the title in Premiéry");
        await services.InputWatch.ScanAsync();
        Check(services.InputWatch.Found.Any(f => f.Title == "Invented Show (2026)" && f.IsSeries && f.Files.Count == 1) &&
              domestic.Rows.First(r => r.Caption == "Invented Show (2026)") is { FolderText: "✓ v priečinku: 1" } found && found.Episodes[0].State == "✓ vo vstupnom priečinku",
            "input watch: an episode found in the input folder – " + string.Join(" | ", services.InputWatch.Found.Select(f => $"{f.Title} {f.Detail}")));
        Check(services.InputWatch.Found.Any(f => f.Title == "Domáci seriál (2020)" && f.IsSeries) && !services.Marks.Get(Core.News.MarkStore.Series(home.Episodes[0].Episode)).Chosen,
            "input watch: episodes of series not chosen are found too (owner, 29. 10. 2026)");

        // the Dashboard's week: the chosen series' episodes and the premieres, the films of the week
        model.Open(model.Dashboard);
        await model.Dashboard.Week.RefreshAsync();
        Check(model.Dashboard.Week.Episodes.Any(e => e.Title == "Invented Show (2026)" && e.Chosen && e.IsToday) && model.Dashboard.Week.Films.Count >= 2 &&
              model.Dashboard.Week.Films.Any(f => f.Chosen) && model.Dashboard.Week.Offers.Count == 2 && model.Dashboard.Week.Found.Count >= 1,
            "dashboard: this week's episodes and films – " + string.Join(" | ", model.Dashboard.Week.Episodes.Select(e => $"{e.Day} {e.Title} {e.Detail}")));
        await Screenshot(window, "dashboard-week.png");
        services.WatchForSelfTest = null;
        model.Open(page);

        var asked = news.Requests.Count;
        var again = new SeriesNewsPageViewModel(services);
        again.Shown();
        await WaitFor(() => again.Tabs[0].Rows.Count > 0);
        Check(news.Requests.Count == asked && again.Tabs[0].Rows.Count == 2, "novinky: the kept days are shown at once at the next start, without asking the sources");

        await TestCalendarAsync(model, window, services);
        await TestTasksAsync(model, window, services);
        await TestNotesAsync(model, window, services);
        await TestReportsAsync(model, services);
        await TestDashboardAgendaAsync(model, window, services);
        await TestActionsAsync(model, window, services);
        await TestGuidesAsync(model, window);

        services.SeriesTmdbForSelfTest = null;
        services.PostersForSelfTest = null;
        services.SeriesNewsForSelfTest = null;
        services.SetNewsSources(Core.News.NewsSource.Defaults);
        model.Open(model.Dashboard);
    }

    /// <summary>
    /// Tlač a export (owner, 31. 10. 2026, stage P3): the reports of Kalendár, Plánované úlohy and Poznámky – the preview,
    /// the forms of the calendar, pages on a sheet, the PDF of UNIVERSUM's own writer with the font embedded, HTML, iCalendar,
    /// CSV, Markdown, a backup and its import; the report window shown. Invented records only; the files in the output.
    /// </summary>
    private static async Task TestReportsAsync(MainViewModel model, AppServices services)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var store = services.Agenda;
        var cinema = new Core.Agenda.AgendaItem
        {
            Title = "Kino – vymyslený ďábelský film", Tags = ["kino"],
            Time = new Core.Agenda.AgendaTime { Start = today, StartTime = new TimeOnly(20, 0), ReminderMinutes = 60, Repeat = new Core.Agenda.Recurrence { Every = Core.Agenda.RepeatEvery.Week, Count = 3 } },
        };
        store.Save(cinema);
        store.Save(new Core.Agenda.AgendaItem { Title = "Spracovať vymyslený seriál", Task = new Core.Agenda.AgendaTask { Due = today, Checklist = [new Core.Agenda.CheckItem { Text = "ťažký krok", Done = true }] } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená poznámka", Text = "Žltý kôň úpel ďábelské ódy.", Note = new Core.Agenda.AgendaNote() });
        var folder = Path.Combine(_outputDirectory, "tlac");
        Directory.CreateDirectory(folder);
        string? Ask(string filter, string name) => Path.Combine(folder, name.Length > 0 ? name : "subor");

        ReportViewModel? shown = null;
        var calendar = model.Calendar;
        model.Open(calendar);
        calendar.IsMonth = true;
        calendar.TodayCommand.Execute(null);
        calendar.ShowReport = report => shown = report;
        calendar.PrintCommand.Execute(null);
        var report = shown!;
        report.AskSave = Ask;
        report.AskOpen = _ => Path.Combine(folder, $"UNIVERSUM – záloha poznámok {DateTime.Now:yyyy-MM-dd}.json");
        Check(report.Document is { Pages.Count: 1 } && report.Status.Length == 0, "print: the month of the calendar on one sheet – " + report.Status);
        var forms = new Dictionary<string, int>();
        foreach (var form in report.Forms)
        {
            report.Form = form;
            forms[form.Value] = report.PageCount;
        }

        Check(forms["yearrows"] == 1 && forms["yearmonths"] == 1 && forms["month"] == 1 && forms["day"] >= 1 && forms["monthlist"] >= 1,
            "print: every form of the calendar laid out – " + string.Join(", ", forms.Select(f => $"{f.Key} {f.Value}")));
        report.Form = report.Forms.First(f => f.Value == "monthlist");
        var pages = report.PageCount;
        report.PagesPerSheet = ReportViewModel.PerSheet.First(p => p.Value == 2);
        Check(report.Document!.Pages.Count == (pages + 1) / 2, "print: two pages on a sheet");
        report.PagesPerSheet = ReportViewModel.PerSheet[0];
        report.Lighter = true;
        report.PunchMargin = true;
        Check(services.Settings.Print is { Lighter: true, PunchMargin: true }, "print: the choices kept in the settings");
        var pdf = report.Pdf();
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Check(text.StartsWith("%PDF-1.7", StringComparison.Ordinal) && text.Contains("/FontFile2", StringComparison.Ordinal) && text.Contains("/ToUnicode", StringComparison.Ordinal) &&
              System.Text.RegularExpressions.Regex.Matches(text, @"/Type /Page\b").Count == report.Document.Pages.Count, $"print: the own PDF with the font embedded ({pdf.Length / 1024} kB, font {services.ReportFonts?.Family})");
        report.SavePdfCommand.Execute(null);
        report.SaveHtmlCommand.Execute(null);
        foreach (var export in report.Exports.Take(4))
        {
            export.Command.Execute(null);
        }

        var ics = File.ReadAllText(Path.Combine(folder, "UNIVERSUM – kalendár.ics"));
        Check(File.Exists(Path.Combine(folder, report.Title + ".pdf")) && File.Exists(Path.Combine(folder, report.Title + ".html")) &&
              ics.Contains("RRULE:FREQ=WEEKLY;COUNT=3", StringComparison.Ordinal) && ics.Contains("TRIGGER:-PT60M", StringComparison.Ordinal) && ics.Contains("BEGIN:VTODO", StringComparison.Ordinal) &&
              File.ReadAllText(Path.Combine(folder, "UNIVERSUM – záznamy.csv")).Contains("Kino – vymyslený ďábelský film", StringComparison.Ordinal),
            "print: PDF, HTML, iCalendar and CSV saved – " + report.Status);
        store.Delete(cinema.Id);
        store.EmptyBin();
        report.Exports[4].Command.Execute(null);
        Check(store.Find(cinema.Id) is not null && report.Status.Contains('1', StringComparison.Ordinal), "print: the backup brings the record back – " + report.Status);

        var window = new ReportWindow(report) { Owner = Application.Current.MainWindow, Width = 1180, Height = 820 };
        window.Show();
        await Screenshot(window, "print-preview.png");
        window.Close();
        services.Settings.Print = new Core.Reports.ReportOptions();

        model.Open(model.AgendaTasks);
        model.AgendaTasks.ShowReport = r => shown = r;
        model.AgendaTasks.PrintCommand.Execute(null);
        Check(shown!.PageCount >= 1 && shown.Pdf().Length > 1000, "print: the tasks");
        shown.AskSave = Ask;
        shown.Exports[2].Command.Execute(null);
        Check(File.ReadAllText(Path.Combine(folder, "UNIVERSUM – úlohy.md")).Contains("  - [x] ťažký krok", StringComparison.Ordinal), "print: the tasks in Markdown");
        model.Open(model.AgendaNotes);
        model.AgendaNotes.ShowReport = r => shown = r;
        model.AgendaNotes.PrintCommand.Execute(null);
        Check(shown!.PageCount >= 1 && shown.Pdf().Length > 1000, "print: the notes");

        foreach (var record in store.Items.ToList())
        {
            store.Delete(record.Id);
        }

        store.EmptyBin();
    }

    /// <summary>
    /// Návody (owner, 31. 10. 2026, stage N): each guide in Slovak, Czech and English with the same parts, its pictures
    /// and the categories it opens all there, the contents and search, a part shown, and the guide printed to a PDF.
    /// </summary>
    private static async Task TestGuidesAsync(MainViewModel model, MainWindow window)
    {
        foreach (var guide in model.Guides)
        {
            model.Open(guide);
            var parts = guide.Guide.Sections.Count;
            Check(guide.Title.Length > 0 && parts >= 3 && guide.Sections.Count == parts && model.Tools.Any(t => t.Page == guide && t.IsGuide),
                $"guide {guide.Key}: {guide.Title} – parts {parts}");
            foreach (var language in new[] { "cs", "en" })
            {
                var text = GuidePageViewModel.Text(guide.Key, language);
                var translated = text is null ? null : Core.Guides.GuideDocument.Parse(text);
                Check(translated is not null && translated.Sections.Count == parts && translated.Pictures.SequenceEqual(guide.Guide.Pictures),
                    $"guide {guide.Key}: the {language} translation has the same parts and pictures – {translated?.Sections.Count}");
            }

            var missing = guide.Guide.Pictures.Where(p => GuidePageViewModel.Picture(p) is null).ToList();
            Check(missing.Count == 0 && guide.Guide.Pictures.Any(), $"guide {guide.Key}: every picture there – missing: {string.Join(", ", missing)}");
            foreach (var open in guide.Guide.Blocks.Where(b => b.Kind == Core.Guides.GuideBlockKind.Open))
            {
                Check(model.OpenNamed(open.Target), $"guide {guide.Key}: the button opens {open.Target}");
            }

            model.Open(guide);
            guide.Search = "vymyslenéslovonikde";
            Check(guide.NothingFound && guide.Sections.Count == 0, $"guide {guide.Key}: nothing found for a word not in it");
            guide.Search = "";
            if (guide.Sections.Count < 2)
            {
                continue;
            }

            guide.Sections[1].GoCommand.Execute(null);
            Check(guide.ShownPart == guide.Sections[1].Index, $"guide {guide.Key}: a part of the contents shown");
            await Screenshot(window, $"guide-{guide.Key}.png");
            var report = guide.Report();
            var pdf = report.Pdf();
            File.WriteAllBytes(Path.Combine(_outputDirectory, $"navod-{guide.Key}.pdf"), pdf);
            Check(report.PageCount >= 2 && pdf.Length > 20000 && System.Text.Encoding.Latin1.GetString(pdf).Contains("/DCTDecode", StringComparison.Ordinal),
                $"guide {guide.Key}: printed – {report.PageCount} pages, PDF {pdf.Length / 1024} kB with its pictures");
        }
    }

    /// <summary>
    /// POZNÁMKY, stage P4 (owner, 31. 10. 2026): an action of UNIVERSUM planned in Plánované úlohy (every night at three),
    /// done by itself at its time and told, shown in Kalendár; an action needs an hour; an iCalendar file of another
    /// program read (twice – nothing comes twice); the live counts in the left rail; invented texts only.
    /// </summary>
    private static async Task TestActionsAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var store = services.Agenda;
        var tasks = model.AgendaTasks;
        model.Open(tasks);
        tasks.NewAction(Core.Agenda.AgendaActionKind.SyncHistra);
        var editor = tasks.Editor!;
        Check(editor is { IsEvent: true, HasAction: true, AllDay: false, StartTimeText: "03:00" } && editor.Repeat.Value == Core.Agenda.RepeatEvery.Day && editor.Title.Length > 0,
            "actions: a new action of UNIVERSUM – every night at three – " + editor.Title);
        editor.AllDay = true;
        editor.SaveCommand.Execute(null);
        Check(tasks.HasEditor && editor.Problem.Contains("čas", StringComparison.Ordinal), "actions: an action needs an hour – " + editor.Problem);
        editor.AllDay = false;
        editor.Title = "Vymyslená nočná synchronizácia";
        await Screenshot(window, "actions-editor.png");
        editor.SaveCommand.Execute(null);
        var nightly = store.Items.SingleOrDefault(i => i.Title == "Vymyslená nočná synchronizácia");
        Check(nightly?.Action is { Kind: Core.Agenda.AgendaActionKind.SyncHistra } && nightly.Time is { StartTime.Hour: 3 } && tasks.Actions.Any(a => a.Item == nightly && a.Next.Contains("03:00", StringComparison.Ordinal)),
            "actions: kept, listed with its next time – " + string.Join(" | ", tasks.Actions.Select(a => a.Title + " " + a.Next)));

        // the next night: UNIVERSUM does it by itself and tells it
        var night = today.AddDays(1).ToDateTime(new TimeOnly(3, 0, 10));
        await model.CheckActionsAsync(night);
        Check(nightly!.Action!.LastRun is not null && nightly.Action.LastOutcome.Length > 0 && model.ReminderText.StartsWith("⚙ UNIVERSUM vykonal", StringComparison.Ordinal),
            "actions: done at its time and told – " + model.ReminderText);
        await model.CheckActionsAsync(night.AddSeconds(30));
        Check(store.Find(nightly.Id)?.Action?.LastRun == nightly.Action.LastRun, "actions: done once");
        Check(tasks.Actions.Any(a => a.Item.Id == nightly.Id && a.HasLast), "actions: the last outcome listed");
        await Screenshot(window, "actions-tasks.png");
        model.DismissReminderCommand.Execute(null);
        model.Calendar.Select(today.AddDays(1));
        Check(model.Calendar.DetailSections.Any(s => s.Title == "UNIVERSUM" && s.Entries.Any(e => e.Layer == CalendarLayer.Actions && e.Title.StartsWith("⚙ ", StringComparison.Ordinal))),
            "actions: in Kalendár in the layer UNIVERSUM");

        // an iCalendar file of another program, read twice
        var ics = Path.Combine(_outputDirectory, "vymysleny-kalendar.ics");
        var day = today.AddDays(5);
        File.WriteAllText(ics, "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Vymyslený program//EN\r\n" +
                               $"BEGIN:VEVENT\r\nUID:vymysleny-1@example.invalid\r\nDTSTART:{day:yyyyMMdd}T180000\r\nDTEND:{day:yyyyMMdd}T193000\r\nSUMMARY:Vymyslený koncert z iného programu\r\n" +
                               "BEGIN:VALARM\r\nTRIGGER:-PT30M\r\nACTION:DISPLAY\r\nEND:VALARM\r\nEND:VEVENT\r\n" +
                               $"BEGIN:VTODO\r\nUID:vymysleny-2@example.invalid\r\nDUE;VALUE=DATE:{day:yyyyMMdd}\r\nPRIORITY:1\r\nSUMMARY:Vymyslená úloha z iného programu\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
        var first = model.Calendar.ImportIcs(ics);
        var again = model.Calendar.ImportIcs(ics);
        var concert = store.Items.SingleOrDefault(i => i.Title == "Vymyslený koncert z iného programu");
        Check(first.Contains("2 z 2", StringComparison.Ordinal) && again.Contains("0 z 2", StringComparison.Ordinal) && concert?.Time is { StartTime.Hour: 18, ReminderMinutes: 30 } &&
              store.Items.Any(i => i.Title == "Vymyslená úloha z iného programu" && i.Task is { Priority: 2, Due: { } due } && due == day),
            $"actions: an iCalendar file read, the second time nothing added – {first} / {again}");

        // the live counts of POZNÁMKY in the left rail
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená úloha po termíne", Task = new Core.Agenda.AgendaTask { Due = today.AddDays(-3) } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená poznámka", Note = new Core.Agenda.AgendaNote() });
        Check(model.TasksBadge is { IsShown: true, Alert: true, Text: "1" } && model.NotesBadge.Text == "1" && model.Tools.First(t => t.Page == model.AgendaTasks).Badge == model.TasksBadge,
            $"actions: the counts in the left rail – úlohy {model.TasksBadge.Text} (red), poznámky {model.NotesBadge.Text}");
        await Screenshot(window, "rail-counts.png");

        foreach (var record in store.Items.ToList())
        {
            store.Delete(record.Id);
        }

        store.EmptyBin();
        Check(!model.TasksBadge.IsShown && !model.NotesBadge.IsShown, "actions: no counts without records");
    }

    /// <summary>
    /// The Dashboard's POZNÁMKY (owner, 31. 10. 2026, stage P3b): the calendar of the coming week without the tasks (they
    /// have their own list), the late and today's tasks (not those of a month later), the pinned note with the beginning of
    /// its text, a task written in one line, a task ticked off, an event and a note opened in their pages; invented texts.
    /// </summary>
    private static async Task TestDashboardAgendaAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var store = services.Agenda;
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslený koncert", Time = new Core.Agenda.AgendaTime { Start = today.AddDays(2), StartTime = new TimeOnly(19, 30) } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená úloha po termíne", Task = new Core.Agenda.AgendaTask { Due = today.AddDays(-1) } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená úloha na dnes", Task = new Core.Agenda.AgendaTask { Due = today, Priority = 2 } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená úloha o mesiac", Task = new Core.Agenda.AgendaTask { Due = today.AddDays(30) } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená pripnutá poznámka", Text = "Prvý riadok textu.\nĎalší riadok.", Note = new Core.Agenda.AgendaNote { Pinned = true } });
        store.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená obyčajná poznámka", Note = new Core.Agenda.AgendaNote() });

        var dashboard = model.Dashboard;
        var agenda = dashboard.Agenda;
        model.Open(dashboard);
        agenda.Refresh();
        Check(agenda.Calendar.Any(r => r.Title == "Vymyslený koncert" && r.Time == "19:30" && !r.IsToday) && !agenda.Calendar.Any(r => r.Title.StartsWith("Vymyslená úloha", StringComparison.Ordinal)),
            "dashboard: the event of the coming week in the calendar, the tasks not there – " + string.Join(" | ", agenda.Calendar.Select(r => $"{r.Day} {r.Time} {r.Title}")));
        Check(agenda.Tasks.Select(t => t.Title).SequenceEqual(["Vymyslená úloha po termíne", "Vymyslená úloha na dnes"]) && agenda.Tasks[0].Late && agenda.Tasks[1].High && !agenda.Tasks[1].Late,
            "dashboard: the late task first, then today's, the one of a month later not yet – " + string.Join(" | ", agenda.Tasks.Select(t => $"{t.Due} {t.Title}")));
        Check(agenda.Notes.Count == 1 && agenda.Notes[0].Title == "Vymyslená pripnutá poznámka" && agenda.Notes[0].Preview == "Prvý riadok textu.",
            "dashboard: only the pinned note, with the beginning of its text");

        agenda.QuickText = "zajtra 18:00 vymyslená úloha z dashboardu #test !";
        Check(agenda.QuickHint.Contains("vymyslená úloha z dashboardu", StringComparison.Ordinal) && agenda.QuickHint.Contains("18:00", StringComparison.Ordinal),
            "dashboard: the line understood before it is added – " + agenda.QuickHint);
        agenda.AddCommand.Execute(null);
        var quick = store.Items.SingleOrDefault(i => i.Title == "vymyslená úloha z dashboardu");
        Check(quick?.Task is { Priority: 2, DueTime.Hour: 18 } && quick.Task.Due == today.AddDays(1) && quick.Tags.SequenceEqual(["test"]) && agenda.QuickText.Length == 0 &&
              agenda.Tasks.Any(t => t.Title == quick.Title && t.High), "dashboard: a task written in one line – " + agenda.Status);
        await Screenshot(window, "dashboard-agenda.png");

        agenda.Tasks.First(t => t.Title == "Vymyslená úloha po termíne").IsDone = true;
        Check(store.Items.Single(i => i.Title == "Vymyslená úloha po termíne").Task!.State == Core.Agenda.TaskState.Done && agenda.Tasks.All(t => t.Title != "Vymyslená úloha po termíne"),
            "dashboard: a task ticked off – done, it leaves the list");

        agenda.Calendar.First(r => r.Title == "Vymyslený koncert").OpenCommand.Execute(null);
        Check(model.SelectedTool.Page == model.Calendar && model.Calendar.SelectedDate == today.AddDays(2) && model.Calendar.Editor is { Title: "Vymyslený koncert" },
            "dashboard: an event opens in Kalendár on its day, in its editor");
        model.Calendar.Editor?.CancelCommand.Execute(null);
        model.Open(dashboard);
        agenda.Notes[0].OpenCommand.Execute(null);
        Check(model.SelectedTool.Page == model.AgendaNotes && model.AgendaNotes.Editor is AgendaEditor { Title: "Vymyslená pripnutá poznámka" }, "dashboard: a pinned note opens in Poznámky");
        (model.AgendaNotes.Editor as AgendaEditor)?.CancelCommand.Execute(null);

        foreach (var record in store.Items.ToList())
        {
            store.Delete(record.Id);
        }

        store.EmptyBin();
        model.Open(dashboard);
        Check(!agenda.Calendar.Any(r => r.Title.StartsWith("Vymysl", StringComparison.Ordinal)) && agenda.NoTasks && !agenda.HasNotes,
            "dashboard: no record left when the records are gone (the holidays stay) – " + string.Join(", ", agenda.Calendar.Select(r => r.Title)));
    }

    /// <summary>
    /// Poznámky (owner, 31. 10. 2026, stage P2): a quick note (its first sentence the title, #tags), a note of a day from
    /// Kalendár, the note of a title of Premiéry kept with the title, pinning, search, the bin, and a recording written
    /// down in the computer (the English speech of the test file, whisper.cpp); invented texts only.
    /// </summary>
    private static async Task TestNotesAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var notes = model.AgendaNotes;
        var today = DateOnly.FromDateTime(DateTime.Now);
        model.Open(notes);
        await notes.Ready;
        notes.ShowAll = true;
        notes.QuickText = "Vymyslený nápad na plagát. Skúsiť tmavšie pozadie #nápad";
        notes.AddCommand.Execute(null);
        var idea = services.Agenda.Items.Single(i => i.Title == "Vymyslený nápad na plagát.");
        Check(idea.Text == "Skúsiť tmavšie pozadie" && idea.Tags.SequenceEqual(["nápad"]) && idea.Note is not null && notes.Cards.Any(c => c.Record == idea),
            "notes: a quick note – the first sentence its title, the tag – " + notes.Status);

        model.Calendar.Select(today);
        model.Calendar.NewNoteCommand.Execute(null);
        model.Calendar.Editor!.Title = "Vymyslená poznámka k dňu";
        model.Calendar.Editor.SaveCommand.Execute(null);
        model.Open(notes);
        notes.ShowDays = true;
        Check(notes.Cards.Count == 1 && notes.Cards[0].Title == "Vymyslená poznámka k dňu" && notes.Cards[0].Where.StartsWith("k dňu", StringComparison.Ordinal),
            "notes: the note of a day written in Kalendár – " + string.Join(", ", notes.Cards.Select(c => c.Title + " / " + c.Where)));

        var film = services.PremiereStore.Load().Films.First();
        var key = Core.News.MarkStore.Film(film.TmdbId);
        var before = services.Marks.Get(key);
        services.Marks.Set(key, before with { Note = "Vymyslená poznámka k titulu" });
        notes.ShowTitles = true;
        var titleCard = notes.Cards.SingleOrDefault(c => c.TitleKey == key);
        Check(titleCard is not null && titleCard.Title == film.Caption && titleCard.Text == "Vymyslená poznámka k titulu", "notes: the note of a title of Premiéry, by its name – " + titleCard?.Title);
        titleCard!.OpenCommand.Execute(null);
        var titleEditor = (TitleNoteEditor)notes.Editor!;
        titleEditor.Text = "Vymyslená poznámka k titulu – upravená";
        titleEditor.SaveCommand.Execute(null);
        Check(services.Marks.Get(key).Note == "Vymyslená poznámka k titulu – upravená" && !notes.HasEditor, "notes: the note of a title changed – kept with the title (Premiéry show it too)");

        notes.ShowAll = true;
        notes.Cards.First(c => c.Record == idea).PinCommand!.Execute(null);
        Check(idea.Note!.Pinned && notes.Cards[0].Record == idea, "notes: a pinned note first");
        notes.Filter = "pozadie";
        Check(notes.Cards.Count == 1 && notes.Cards[0].Record == idea, "notes: looked for in the text");
        notes.Filter = "";
        await Screenshot(window, "notes.png");
        notes.Cards.First(c => c.Record == idea).DeleteCommand.Execute(null);
        notes.ShowBin = true;
        Check(notes.Cards.Any(c => c.Record == idea && c.InBin) && notes.BinText.EndsWith("(1)", StringComparison.Ordinal), "notes: in the bin – " + notes.BinText);
        notes.Cards.First(c => c.Record == idea).RestoreCommand!.Execute(null);
        notes.ShowAll = true;
        Check(notes.Cards.Any(c => c.Record == idea && !c.InBin), "notes: taken back from the bin");

        // the voice written down in the computer: the English sentences of the test file (synthetic voice)
        notes.Edit(idea);
        var editor = (AgendaEditor)notes.Editor!;
        Check(editor.HasVoice && editor.IsNote, "notes: the editor of a note offers the voice");
        try
        {
            var tools = await services.SpeechAsync();
            var samples = Path.Combine(_outputDirectory, "hlas.pcm");
            var wave = Path.Combine(_outputDirectory, "hlas.wav");
            var media = Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "speech-und.mkv");
            await services.Runner.RunAsync(Core.Speech.VoiceNotes.SamplesFromFileRequest(tools, media, 1, TimeSpan.Zero, TimeSpan.FromSeconds(28), samples));
            Core.Speech.VoiceNotes.Wave(samples, wave);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (text, problem) = await services.Voice.TranscribeAsync(wave, "en");
            Write($"notes: the voice written down ({watch.Elapsed.TotalSeconds:0.0} s): {text ?? problem}");
            Check(text is not null && new[] { "evening", "village", "miller", "river" }.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)),
                "notes: English speech written down in the computer – " + (text ?? problem));
            editor.AddSpoken(text ?? "", null);
            Check(editor.Text.StartsWith("Skúsiť tmavšie pozadie", StringComparison.Ordinal) && editor.Text.Length > "Skúsiť tmavšie pozadie".Length + 10, "notes: the written text added to the note");
        }
        catch (Core.Speech.SpeechException ex)
        {
            Write("notes: the voice not tried – " + ex.Message);
        }

        await Screenshot(window, "notes-editor.png");
        editor.CancelCommand.Execute(null);

        services.Marks.Set(key, before);
        foreach (var record in services.Agenda.Items.ToList())
        {
            services.Agenda.Delete(record.Id);
        }

        services.Agenda.EmptyBin();
    }

    /// <summary>
    /// Plánované úlohy (owner, 31. 10. 2026, stage P2): tasks written in one line and understood, the groups by due day,
    /// done, the board, the checklist in the editor, the filters, the suggestions of UNIVERSUM (a chosen title in the input
    /// folder), and the task in Kalendár; invented titles only.
    /// </summary>
    private static async Task TestTasksAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var tasks = model.AgendaTasks;
        var today = DateOnly.FromDateTime(DateTime.Now);
        model.Open(tasks);
        await tasks.Ready;
        tasks.IsList = true;
        tasks.QuickText = "zajtra o 20:00 vymyslená úloha #test !";
        Check(tasks.QuickHint.Contains("vymyslená úloha", StringComparison.Ordinal) && tasks.QuickHint.Contains("20:00", StringComparison.Ordinal), "tasks: the line understood before it is added – " + tasks.QuickHint);
        tasks.AddCommand.Execute(null);
        var made = services.Agenda.Items.Single(i => i.Title == "vymyslená úloha");
        Check(made.Task is { Priority: 2, DueTime.Hour: 20 } && made.Task.Due == today.AddDays(1) && made.Tags.SequenceEqual(["test"]) && tasks.QuickText.Length == 0 &&
              tasks.Groups.Any(g => g.Title == "Zajtra" && g.Rows.Any(r => r.Item == made)), "tasks: a task of tomorrow 20:00, high priority, tagged – " + tasks.Status);
        tasks.QuickText = "skontrolovať vymyslené titulky";
        tasks.AddCommand.Execute(null);
        services.Agenda.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená úloha po termíne", Task = new Core.Agenda.AgendaTask { Due = today.AddDays(-2) } });
        Check(tasks.Groups.Select(g => g.Title).SequenceEqual(["Po termíne", "Zajtra", "Bez termínu"]) && tasks.Groups[0].Rows[0].IsOverdue,
            "tasks: the groups by due day, the overdue one first – " + string.Join(", ", tasks.Groups.Select(g => g.Title)));
        await Screenshot(window, "tasks-list.png");

        tasks.Groups.First(g => g.Title == "Po termíne").Rows[0].IsDone = true;
        Check(!tasks.Groups.Any(g => g.Title == "Po termíne") && services.Agenda.Items.Single(i => i.Title == "Vymyslená úloha po termíne").Task!.State == Core.Agenda.TaskState.Done,
            "tasks: a task done leaves the open groups");
        tasks.ShowDone = true;
        Check(tasks.Groups.Any(g => g.Title == "Hotové"), "tasks: the done tasks shown on demand");
        tasks.ShowDone = false;

        tasks.Edit(made);
        var editor = tasks.Editor!;
        editor.NewCheckText = "vymyslený krok 1";
        editor.AddCheckCommand.Execute(null);
        editor.NewCheckText = "vymyslený krok 2";
        editor.AddCheckCommand.Execute(null);
        editor.Checklist[0].Done = true;
        await Screenshot(window, "tasks-editor.png");
        editor.SaveCommand.Execute(null);
        Check(!tasks.HasEditor && made.Task!.Checklist.Count == 2 && made.Task.Checklist.Count(c => c.Done) == 1 &&
              tasks.Groups.SelectMany(g => g.Rows).First(r => r.Item == made).Progress == "☑ 1/2", "tasks: the checklist of the task – 1 of 2 done");

        tasks.Filter = "titulky";
        Check(tasks.Groups.SelectMany(g => g.Rows).Select(r => r.Title).SequenceEqual(["skontrolovať vymyslené titulky"]), "tasks: looked for without diacritics");
        tasks.Filter = "";
        tasks.Tag = "test";
        Check(tasks.Groups.SelectMany(g => g.Rows).All(r => r.Item == made), "tasks: by the tag");
        tasks.Tag = TasksPageViewModel.AllTags;

        tasks.IsBoard = true;
        var plain = tasks.ToDo.First(r => r.Title == "skontrolovať vymyslené titulky");
        plain.RightCommand.Execute(null);
        Check(tasks.InProgress.Any(r => r.Title == "skontrolovať vymyslené titulky"), "tasks: on the board to In progress");
        await Screenshot(window, "tasks-board.png");
        tasks.InProgress.First(r => r.Title == "skontrolovať vymyslené titulky").RightCommand.Execute(null);
        Check(tasks.Done.Any(r => r.Title == "skontrolovať vymyslené titulky"), "tasks: on the board to Done");
        tasks.IsBoard = false;

        if (services.InputWatch.Found.Count > 0)
        {
            var suggestion = tasks.Suggestions.FirstOrDefault(s => s.Key.StartsWith("vstup|", StringComparison.Ordinal));
            Check(suggestion is { HasAction: true }, "tasks: UNIVERSUM suggests to process a chosen title found in the input folder – " + string.Join(", ", tasks.Suggestions.Select(s => s.Title)));
            suggestion!.TakeCommand.Execute(null);
            Check(services.Agenda.Items.Any(i => i.Title == suggestion.Title && i.Task?.Due == today) && !tasks.Suggestions.Contains(suggestion) &&
                  services.Settings.HiddenSuggestions.Contains(suggestion.Key), "tasks: a suggestion taken as a task of today, then hidden");
        }

        model.Calendar.Select(today.AddDays(1));
        Check(model.Calendar.DetailSections.Any(s => s.Entries.Any(e => e.Record == made && e.TimeText == "20:00")), "tasks: the task of tomorrow is in Kalendár");

        foreach (var record in services.Agenda.Items.ToList())
        {
            services.Agenda.Delete(record.Id);
        }

        services.Agenda.EmptyBin();
        services.Settings.HiddenSuggestions.Clear();
        services.Settings.Save();
    }

    /// <summary>
    /// Kalendár (owner, 31. 10. 2026): the kept premieres and episodes of the tests before on their days, the holidays and
    /// names, the school holidays of a district, the layers, a record made in the editor (repeating, with a reminder), a
    /// task and a note of the day, the seven views and the way to Novinky; invented titles only.
    /// </summary>
    private static async Task TestCalendarAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var calendar = model.Calendar;
        model.Open(calendar);
        await calendar.Ready;
        // the calendar was shown once before (all pages are opened at the start): what Premiéry and Novinky keep now is read again
        await calendar.RefreshCommand.ExecuteAsync(null);
        calendar.IsMonth = true;
        calendar.TodayCommand.Execute(null);
        Check(calendar.Days.Count == 42 && calendar.Days.Count(d => d.IsToday) == 1 && calendar.Status.Length == 0, "calendar: the month of 42 days with today – " + calendar.Status);
        var todayCell = calendar.Days.First(d => d.IsToday);
        Check(todayCell.Entries.Any(e => e.Layer == CalendarLayer.EpisodesDomestic && e.Title.Contains("Domáci seriál", StringComparison.Ordinal)),
            "calendar: today's episode of Novinky – " + string.Join(", ", todayCell.Entries.Select(e => e.Title)));
        calendar.Select(FakeNews.Day(-2));
        Check(calendar.DetailSections.Any(s => s.Entries.Any(e => e.Layer == CalendarLayer.FilmsWorld && e.Title.Contains("World Invented Film", StringComparison.Ordinal))),
            "calendar: the premiere of Premiéry on its day – " + string.Join(" | ", calendar.DetailSections.Select(s => s.Title + ": " + string.Join(", ", s.Entries.Select(e => e.Title)))));
        calendar.LayerGroups[0].AllOn = false;
        Check(calendar.LayerGroups[0].Layers.All(l => !l.IsOn) && !calendar.DetailSections.Any(s => s.Entries.Any(e => e.Layer == CalendarLayer.FilmsWorld)) &&
              services.Settings.CalendarHiddenLayers.Contains(nameof(CalendarLayer.FilmsWorld)), "calendar: the premieres switched off all at once (kept in the settings)");
        calendar.LayerGroups[0].Layers[1].IsOn = true;
        Check(calendar.LayerGroups[0].AllOn is null, "calendar: one layer of the group on – the group's switch shows some");
        calendar.LayerGroups[0].AllOn = true;

        calendar.Select(new DateOnly(2026, 12, 24));
        Check(calendar.DetailSections.Any(s => s.Entries.Any(e => e.Layer == CalendarLayer.Holidays && e.Title == "Štedrý deň")) &&
              calendar.DetailNames.Contains("SK: Adam, Eva") && calendar.DetailNames.Contains("CZ: Adam, Eva"),
            "calendar: a holiday and the names of the day – " + string.Join(", ", calendar.DetailNames));
        calendar.RegionSwitches.First(r => r.Code == Core.Calendar.HolidayRegions.Czechia).IsOn = true;
        calendar.SchoolDistrict = calendar.SchoolDistricts.First(d => d.Value == "Zlín");
        calendar.Select(new DateOnly(2027, 3, 10));
        Check(calendar.HasCzechSchool && calendar.DetailSections.Any(s => s.Entries.Any(e => e.Layer == CalendarLayer.School && e.Title.StartsWith("Jarné prázdniny", StringComparison.Ordinal))),
            "calendar: the spring holidays of the district Zlín");
        calendar.Select(new DateOnly(2026, 9, 28));
        Check(calendar.DetailSections.Any(s => s.Entries.Any(e => e.Title.Contains("Den české státnosti", StringComparison.Ordinal) || e.Title.Contains("Deň českej štátnosti", StringComparison.Ordinal))),
            "calendar: the Czech holidays switched on");
        calendar.RegionSwitches.First(r => r.Code == Core.Calendar.HolidayRegions.Czechia).IsOn = false;

        // a record of the day: an event of the evening, weekly three times, an hour's reminder
        var eventDay = FakeNews.Day(3);
        calendar.Select(eventDay);
        calendar.NewEventCommand.Execute(null);
        var editor = calendar.Editor!;
        editor.SaveCommand.Execute(null);
        Check(calendar.HasEditor && editor.Problem.Length > 0, "calendar: a record without a name is not saved – " + editor.Problem);
        editor.Title = "Kino – vymyslený film";
        editor.AllDay = false;
        editor.StartTimeText = "20:00";
        editor.EndTimeText = "22:15";
        editor.Repeat = AgendaEditor.Repeats.First(r => r.Value == Core.Agenda.RepeatEvery.Week);
        editor.UntilDate = eventDay.AddDays(14).ToDateTime(TimeOnly.MinValue);
        editor.Reminder = AgendaEditor.Reminders.First(r => r.Value == 60);
        editor.TagsText = "#kino";
        await Screenshot(window, "calendar-editor.png");
        editor.SaveCommand.Execute(null);
        var cinema = editor.Saved!;
        Check(!calendar.HasEditor && calendar.DetailSections.Any(s => s.Entries.Any(e => e.Record == cinema && e.TimeText == "20:00")) &&
              services.Agenda.Between(eventDay, eventDay.AddDays(30)).Count(o => o.Item == cinema) == 3 && cinema.Tags.SequenceEqual(["kino"]),
            "calendar: the event saved, weekly three times");
        model.CheckReminders(eventDay.ToDateTime(new TimeOnly(19, 5)));
        Check(model.HasReminder && model.ReminderText.Contains("Kino – vymyslený film", StringComparison.Ordinal), "calendar: the reminder an hour before – " + model.ReminderText);
        await Screenshot(window, "calendar-reminder.png");
        model.DismissReminderCommand.Execute(null);

        calendar.NewTaskCommand.Execute(null);
        calendar.Editor!.Title = "Spracovať vymyslený seriál";
        calendar.Editor.SaveCommand.Execute(null);
        calendar.NewNoteCommand.Execute(null);
        calendar.Editor!.Title = "Poznámka k vymyslenému dňu";
        calendar.Editor.Text = "Vymyslený text poznámky.";
        calendar.Editor.SaveCommand.Execute(null);
        var mine = calendar.DetailSections.FirstOrDefault(s => s.Entries.Any(e => e.Record is not null));
        Check(mine is not null && mine.Entries.Any(e => e.Layer == CalendarLayer.Tasks) && mine.Entries.Any(e => e.Layer == CalendarLayer.Notes) && mine.Entries.Any(e => e.Layer == CalendarLayer.Events),
            "calendar: the event, the task and the note of the day together – " + string.Join(", ", mine?.Entries.Select(e => e.Layer + " " + e.Title) ?? []));
        mine!.Entries.First(e => e.Record == cinema).OpenCommand!.Execute(null);
        Check(calendar.Editor is { IsEvent: true, Title: "Kino – vymyslený film", HasRepeat: true }, "calendar: a record opens in the editor");
        await Screenshot(window, "calendar-detail.png");
        calendar.Editor!.CancelCommand.Execute(null);

        calendar.TodayCommand.Execute(null);
        // the panel of the layers folded: the month has the room
        calendar.ShowLayers = false;
        Check(!services.Settings.CalendarLayersShown, "calendar: the panel of the layers folded (kept in the settings)");
        await Screenshot(window, "calendar-month.png");
        calendar.ShowLayers = true;
        calendar.IsWeek = true;
        Check(calendar.Days.Count == 7, "calendar: the week of seven days");
        await Screenshot(window, "calendar-week.png");
        calendar.IsWeekList = true;
        Check(calendar.Days.Count == 7 && calendar.IsList, "calendar: the week as a list");
        await Screenshot(window, "calendar-weeklist.png");
        calendar.IsMonthList = true;
        var today = DateOnly.FromDateTime(DateTime.Now);
        Check(calendar.Days.Count == DateTime.DaysInMonth(today.Year, today.Month), "calendar: the month as a list");
        calendar.HideEmptyDays = true;
        Check(calendar.Days.All(d => d.Shown.Count > 0 || d.HasHoliday || d.IsToday), "calendar: the empty days left out");
        await Screenshot(window, "calendar-monthlist.png");
        calendar.HideEmptyDays = false;
        calendar.IsYearMonths = true;
        Check(calendar.Months.Count == 12 && calendar.Months[1].Cells.Count(c => c is not null) == DateTime.DaysInMonth(today.Year, 2), "calendar: the year as twelve months");
        await Screenshot(window, "calendar-year.png");
        calendar.IsYearRows = true;
        Check(calendar.YearRows.Count == 31 && calendar.YearRows[30].Months.Count(m => m is not null) == 7, "calendar: the year in rows (31 days in seven months)");
        await Screenshot(window, "calendar-yearrows.png");
        calendar.IsAgenda = true;
        Check(calendar.Days.Count > 0 && calendar.Days.All(d => d.Shown.Count > 0 || d.HasHoliday || d.IsToday) && calendar.Days.Any(d => d.Date == eventDay),
            "calendar: the agenda – only the days with something");
        await Screenshot(window, "calendar-agenda.png");
        Check(services.Settings.CalendarView == nameof(CalendarView.Agenda), "calendar: the view kept in the settings");

        // a click on an episode opens Novinky with the series looked for
        calendar.IsMonth = true;
        calendar.TodayCommand.Execute(null);
        var episode = calendar.DetailSections.SelectMany(s => s.Entries).First(e => e.Layer == CalendarLayer.EpisodesDomestic);
        episode.OpenCommand!.Execute(null);
        Check(model.SelectedTool.Page == model.SeriesNews && model.SeriesNews.Filter.Length > 0 && episode.Title.TrimStart('★', ' ').StartsWith(model.SeriesNews.Filter, StringComparison.Ordinal),
            $"calendar: an episode opens in Novinky with its series looked for – {episode.Title} → {model.SeriesNews.Filter}");
        model.SeriesNews.Filter = "";

        // the layer UNIVERSUM: today's runs of processing of the tests before, a run opened in Záznamy spracovania
        model.Open(calendar);
        calendar.TodayCommand.Execute(null);
        var runs = calendar.DetailSections.FirstOrDefault(s => s.Title == "UNIVERSUM");
        var run = runs?.Entries.FirstOrDefault(e => e.Layer == CalendarLayer.Processed);
        Check(run is not null && run.HasTime && run.Detail.Length > 0, "calendar: today's runs of processing in the layer UNIVERSUM – " + string.Join(", ", runs?.Entries.Select(e => $"{e.TimeText} {e.Title}") ?? []));
        run!.OpenCommand!.Execute(null);
        Check(model.SelectedTool.Page == model.Logs && model.Logs.Selected is { } shownLog && shownLog.Kind == run.Title.TrimStart('⚠', ' '),
            "calendar: a run opens its log – " + run.Title);
        model.Open(calendar);

        foreach (var record in services.Agenda.Items.ToList())
        {
            services.Agenda.Delete(record.Id);
        }

        services.Agenda.EmptyBin();
        services.Settings.CalendarRegions = null;
        services.Settings.CalendarSchoolDistrict = null;
        services.Settings.Save();
        model.Open(calendar);
        calendar.Build();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// The sources of news with invented answers: a schedule page (rules in <see cref="Rules"/>) with a new Czech
    /// series and a new season today, TVmaze with a new season on a streaming service and a new series in the USA,
    /// TMDB with two films in Czech cinemas and two in the world, and drawn posters.
    /// </summary>
    private sealed class FakeNews : System.Net.Http.HttpMessageHandler
    {
        public const string Rules = """
            {
              "format": "UNIVERSUM-zdroj 1",
              "name": "Vymyslený rozpis",
              "address": "https://rozpis.example/den/{yyyy}-{MM}-{dd}",
              "item": "div.zaznam",
              "section": "h3",
              "sections": [ { "contains": "Nové seriály", "kind": "series-premiere" }, { "contains": "Ostatné", "kind": "episode" } ],
              "fields": {
                "title": { "select": "a.meno", "take": "own" },
                "years": { "select": "a.meno > span" },
                "original": { "select": "a.povodny" },
                "genres": { "select": "div.zaner" },
                "episode": { "select": "div.cislo", "take": "own" },
                "channel": { "select": "div.cislo span" },
                "channelId": { "select": ":self", "take": "attr:data-kanal" },
                "link": { "select": "a.meno", "take": "attr:href" }
              },
              "domestic": [ "ct1", "nova" ],
              "daysBack": 1,
              "daysAhead": 2
            }
            """;

        private readonly List<byte[]> _posters = Enumerable.Range(0, 4).Select(FakeHistra.DrawPoster).ToList();

        public List<string> Requests { get; } = [];

        public List<string> UserAgents { get; } = [];

        public static DateOnly Day(int offset) => DateOnly.FromDateTime(DateTime.Now).AddDays(offset);

        private static string Iso(int offset) => Day(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add(uri.AbsoluteUri);
                if (uri.Host == "rozpis.example")
                {
                    UserAgents.Add(request.Headers.UserAgent.ToString());
                }
            }

            if (uri.Host == "image.tmdb.org")
            {
                return Answer(request, new System.Net.Http.ByteArrayContent(_posters[uri.AbsolutePath.Length % _posters.Count]));
            }

            var body = uri.Host switch
            {
                "rozpis.example" when uri.AbsolutePath == "/robots.txt" => "User-agent: *\nDisallow: /tajne/\n",
                "rozpis.example" => Page(uri.AbsolutePath),
                "api.tvmaze.com" => TvMaze(uri),
                "api.themoviedb.org" => Tmdb(uri),
                _ => null,
            };
            return body is null
                ? Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request })
                : Answer(request, new System.Net.Http.StringContent(body, Encoding.UTF8, uri.Host == "rozpis.example" ? "text/html" : "application/json"));
        }

        private static Task<System.Net.Http.HttpResponseMessage> Answer(System.Net.Http.HttpRequestMessage request, System.Net.Http.HttpContent content) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request, Content = content });

        // today: a new Czech series and a new season of a foreign one; every weekday an episode of a Czech series
        private static string Page(string path)
        {
            var today = path.EndsWith(Iso(0), StringComparison.Ordinal);
            var number = 10 + DateOnly.ParseExact(path[^10..], "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber % 50;
            return "<html><head><meta charset=\"utf-8\"><script>var x = '<div class=\"zaznam\">';</script></head><body>" +
                   (today
                       ? "<h3>Nové seriály</h3><div class=\"zaznam\" data-kanal=\"ct1\"><a class=\"meno\" href=\"https://rozpis.example/serial/1\">Vymyslený seriál <span>(2026–????)</span></a>" +
                         "<a class=\"povodny\">Invented Show</a><div class=\"zaner\">Drama / Krimi</div><div class=\"cislo\">01&times;01<br /><span>ČT1</span></div></div>"
                       : "") +
                   "<h3>Ostatné vysielané</h3>" +
                   (today
                       ? "<div class=\"zaznam\" data-kanal=\"netflix\"><a class=\"meno\">Svetový seriál <span>(2024–????)</span></a><a class=\"povodny\">World Invented</a>" +
                         "<div class=\"cislo\">02&times;01<br /><span>Netflix</span></div></div>"
                       : "") +
                   $"<div class=\"zaznam\" data-kanal=\"nova\"><a class=\"meno\">Domáci seriál <span>(2020–????)</span></a><a class=\"povodny\">Domáci seriál</a>" +
                   $"<div class=\"cislo\">05&times;{number}<br /><span>Nova</span></div></div><!-- <div class=\"zaznam\">komentár</div> --></body></html>";
        }

        private static string? TvMaze(Uri uri)
        {
            var today = uri.Query.Contains("date=" + Iso(0), StringComparison.Ordinal);
            if (uri.AbsolutePath == "/schedule/web")
            {
                return today
                    ? $$$$"""[{"season":3,"number":1,"type":"regular","airdate":"{{{{Iso(0)}}}}","airstamp":"{{{{Iso(0)}}}}T10:00:00+00:00","_embedded":{"show":{"id":501,"name":"Invented Stream","type":"Scripted","weight":90,"premiered":"2024-02-01","genres":["Drama"],"webChannel":{"name":"Invented+","country":null},"externals":{"imdb":"tt0000501","thetvdb":7501},"url":"https://www.tvmaze.com/shows/501/invented-stream"}}},""" +
                      """{"season":4,"number":2,"type":"regular","airdate":"2026-01-01","airstamp":"2026-01-01T10:00:00+00:00","_embedded":{"show":{"id":502,"name":"Invented News","type":"News","weight":99}}}]"""
                    : "[]";
            }

            if (uri.AbsolutePath == "/schedule")
            {
                return today
                    ? $$$$"""[{"season":1,"number":1,"type":"regular","airdate":"{{{{Iso(0)}}}}","airstamp":"{{{{Iso(0)}}}}T12:00:00+00:00","show":{"id":601,"name":"Invented Network Show","type":"Scripted","weight":80,"premiered":"{{{{Iso(0)}}}}","network":{"name":"INV","country":{"code":"US"}},"externals":{}}}]"""
                    : "[]";
            }

            if (uri.AbsolutePath == "/singlesearch/shows")
            {
                return uri.Query.Contains("Invented%20Show", StringComparison.Ordinal) ? """{"id":77,"name":"Invented Show","premiered":"2026-09-01","language":"Czech","network":{"name":"ČT1","country":{"code":"CZ"}}}""" : null;
            }

            // a season: weekly for the streaming show, weekdays for the Czech one
            var show = uri.AbsolutePath.Split('/')[2];
            var dates = show switch
            {
                "501" => Enumerable.Range(0, 8).Select(i => (3, Day(7 * i))),
                "77" => Enumerable.Range(0, 14).Select(Day).Where(d => d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday).Take(10).Select(d => (1, d)),
                _ => Enumerable.Range(0, 6).Select(i => (1, Day(i))),
            };
            return "[" + string.Join(",", dates.Select((d, i) => $$$"""{"season":{{{d.Item1}}},"number":{{{i + 1}}},"type":"regular","airdate":"{{{d.Item2:yyyy-MM-dd}}}"}""")) + "]";
        }

        private static string? Tmdb(Uri uri)
        {
            if (uri.AbsolutePath.EndsWith("/watch/providers", StringComparison.Ordinal))
            {
                return uri.AbsolutePath.StartsWith("/3/tv/", StringComparison.Ordinal)
                    ? """{"results":{"SK":{"flatrate":[{"provider_name":"Vymyslené+"}]},"US":{"flatrate":[{"provider_name":"Inde"}]}}}"""
                    : """{"results":{"SK":{"rent":[{"provider_name":"Obchod"}]}}}""";
            }

            if (uri.AbsolutePath == "/3/search/tv")
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var name = query["query"] ?? "";
                var year = query["first_air_date_year"] ?? "2024";
                var id = 700 + name.Length;
                return $$$"""{"results":[{"id":{{{id}}},"name":"{{{name}}}","original_name":"{{{name}}}","first_air_date":"{{{year}}}-01-01","poster_path":"/s{{{id}}}.jpg"}]}""";
            }

            if (uri.AbsolutePath == "/3/discover/movie")
            {
                return uri.Query.Contains("region=", StringComparison.Ordinal)
                    ? """{"page":1,"total_pages":1,"results":[{"id":101,"title":"The Invented Premiere","popularity":50.5},{"id":102,"title":"Today Invented","popularity":20}]}"""
                    : """{"page":1,"total_pages":1,"results":[{"id":201,"title":"World Invented Film","popularity":90},{"id":202,"title":"Later Invented Film","popularity":40}]}""";
            }

            var (title, original, release, cz, sk) = uri.AbsolutePath switch
            {
                "/3/movie/101" => ("Vymyslená premiéra", "The Invented Premiere", 3, 3, 4),
                "/3/movie/102" => ("Dnešný vymyslený", "Today Invented", 0, 0, 0),
                "/3/movie/201" => ("Svetový vymyslený film", "World Invented Film", -2, 40, 40),
                "/3/movie/202" => ("Neskorší vymyslený film", "Later Invented Film", 10, 30, 30),
                _ => ("", "", 0, 0, 0),
            };
            // the films of the cinemas here are Czech and Slovak, the films of the world American and British
            var countries = uri.AbsolutePath.StartsWith("/3/movie/10", StringComparison.Ordinal)
                ? """[{"iso_3166_1":"CZ"},{"iso_3166_1":"SK"}]"""
                : """[{"iso_3166_1":"US"},{"iso_3166_1":"GB"}]""";
            return title.Length == 0 ? null : $$$"""
                {"title":"{{{title}}}","original_title":"{{{original}}}","release_date":"{{{Iso(release)}}}","poster_path":"/f{{{uri.AbsolutePath[^3..]}}}.jpg",
                 "production_countries":{{{countries}}},"production_companies":[{"name":"Invented Pictures"},{"name":"Second"}],
                 "release_dates":{"results":[
                   {"iso_3166_1":"CZ","release_dates":[{"type":3,"release_date":"{{{Iso(cz)}}}T00:00:00.000Z"},{"type":4,"release_date":"{{{Iso(cz + 45)}}}T00:00:00.000Z"}]},
                   {"iso_3166_1":"SK","release_dates":[{"type":3,"release_date":"{{{Iso(sk)}}}T00:00:00.000Z"}]},
                   {"iso_3166_1":"US","release_dates":[{"type":6,"release_date":"{{{Iso(release + 200)}}}T00:00:00.000Z"}]},
                   {"iso_3166_1":"DE","release_dates":[{"type":3,"release_date":"{{{Iso(release)}}}T00:00:00.000Z"}]}]}}
                """;
        }
    }

    /// <summary>
    /// Invented sources of Zaujímavosti: a page that names its RSS feed (three English articles of the last days and one
    /// older, pictures drawn), a Czech Atom feed, and DeepL answering "SK: " + the text.
    /// </summary>
    private sealed class FakeFeeds : System.Net.Http.HttpMessageHandler
    {
        private readonly byte[] _picture = FakeHistra.DrawPoster(2);

        public List<string> Requests { get; } = [];

        public int DeepL { get; private set; }

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add(uri.AbsoluteUri);
            }

            static string Rfc(int hours) => DateTimeOffset.UtcNow.AddHours(-hours).ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);
            string? body = (uri.Host, uri.AbsolutePath) switch
            {
                ("api-free.deepl.com", _) => await TranslateAsync(request),
                (_, "/robots.txt") => null,
                ("filmy.example", "/") => """<html><head><link rel="alternate" type="application/rss+xml" href="https://filmy.example/rss"></head><body>Vymyslené</body></html>""",
                ("filmy.example", "/rss") => $$$"""
                    <rss version="2.0"><channel><title>Invented films</title>
                      <item><title>Invented festival opens</title><link>https://filmy.example/a/1</link><description>&lt;p&gt;An invented festival opened today. Stars came.&lt;/p&gt;</description>
                        <enclosure url="https://filmy.example/img/1.png" type="image/png" /><pubDate>{{{Rfc(1)}}}</pubDate></item>
                      <item><title>A review of an invented film</title><link>https://filmy.example/a/2</link><description>Good, not great.</description>
                        <enclosure url="https://filmy.example/img/2.png" type="image/png" /><pubDate>{{{Rfc(50)}}}</pubDate></item>
                      <item><title>An old invented story</title><link>https://filmy.example/a/3</link><description>From the archive.</description><pubDate>{{{Rfc(240)}}}</pubDate></item>
                    </channel></rss>
                    """,
                ("kino.example", "/atom") => $$$"""
                    <feed xmlns="http://www.w3.org/2005/Atom"><title>Vymyšlené kino</title>
                      <entry><title>Vymyšlená novinka z kina</title><link href="https://kino.example/n/1"/><published>{{{DateTimeOffset.UtcNow.AddHours(-20):yyyy-MM-ddTHH:mm:ssZ}}}</published><summary>Krátky úvod.</summary></entry>
                    </feed>
                    """,
                _ => null,
            };
            if (uri.AbsolutePath.StartsWith("/img/", StringComparison.Ordinal))
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request, Content = new System.Net.Http.ByteArrayContent(_picture) };
            }

            return body is null
                ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request }
                : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new System.Net.Http.StringContent(body, Encoding.UTF8, uri.Host == "api-free.deepl.com" ? "application/json" : "text/xml"),
                };
        }

        private async Task<string> TranslateAsync(System.Net.Http.HttpRequestMessage request)
        {
            DeepL++;
            using var document = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var texts = document.RootElement.GetProperty("text").EnumerateArray().Select(t => "SK: " + t.GetString());
            return System.Text.Json.JsonSerializer.Serialize(new { translations = texts.Select(t => new { text = t }) });
        }
    }

    /// <summary>
    /// HISTRA and its poster server with invented lists: three lists; the first has 30 titles, given at most 20 per page
    /// (as HISTRA cuts the limit), the second three; a deleted list answers 404. Posters are drawn pictures.
    /// </summary>
    // owner, 31. 10. 2026: TMDB poster – an invented film found, its card, the poster recommended, folder.jpg in POSTER (an
    // older one renamed), a draft by AI, merged into the film processed; invented answers, nothing goes to the internet
    private static async Task TestPostersAsync(MainViewModel model, MainWindow window, AppServices services, string library)
    {
        var fake = new FakePosters();
        var http = new System.Net.Http.HttpClient(fake);
        services.PosterTmdbForSelfTest = new Core.Library.Tmdb.TmdbClient(http, "selftest-key", null, new RequestPacer(1000));
        services.PostersForSelfTest = new PosterCache(http, Path.Combine(_outputDirectory, "posters-tmdb"), PosterCache.DefaultServer);
        services.DraftsForSelfTest = new Core.Posters.PosterDrafts(http);
        services.DraftKeysForSelfTest = new Core.Posters.DraftKeys(new string('a', 32), new string('t', 40), null);
        var page = model.Posters;
        model.SelectedTool = model.Tools.First(t => t.Page == page);
        page.Merge = false;
        page.Query = "Vymyslený plagát 2026";
        await page.SearchCommand.ExecuteAsync(null);
        await page.OfferReady;
        Check(page.Results.Count == 1 && page.Card?.Details is { OriginalTitle: "Vymyslený plagát" }, "posters: the film found and opened – " + page.SearchStatus);
        var card = page.Card!;
        Check(card.MainName == "Vymyslený plagát (2026)" && card.OtherNames.SequenceEqual(["Invented Poster (2026)", "Vymyšlený plakát (2026)"]) && card.Genre == "komédia" &&
              card.Facts == "Film · 1 h 40 min (100 min)" && card.Directors == "Vymyslená Režisérka" && card.HasCollection,
            $"posters: the card – {card.MainName} | {string.Join(" / ", card.OtherNames)} | {card.Facts} | {card.Genre} | {card.Directors} | {card.Collection.Count}");
        Check(page.Recommended?.Candidate?.Picture.Path == "/cz-plagat.png" && page.Selected == page.Recommended && page.Opinion.StartsWith("Odporúčam český plagát", StringComparison.Ordinal) &&
              page.Groups.Select(g => g.Name).SequenceEqual(["EN", "CZ"]), $"posters: the Czech one recommended, the groups – {page.Opinion} | {string.Join(", ", page.Groups.Select(g => g.Name))}");
        await Screenshot(window, "tmdb-poster.png");

        await page.ProcessCommand.ExecuteAsync(null);
        var folder = Path.Combine(library, "POSTER", "Vymyslený plagát (2026)");
        var jpeg = Path.Combine(folder, "folder.jpg");
        Check(File.Exists(jpeg) && File.ReadAllBytes(jpeg) is [0xFF, 0xD8, ..], "posters: folder.jpg (JPEG) in POSTER – " + page.Result);
        page.Groups.Single(g => g.Name == "EN").Posters[0].SelectCommand.Execute(null);
        await page.ProcessCommand.ExecuteAsync(null);
        Check(File.Exists(Path.Combine(folder, "folder-povodny.jpg")) && page.Result.Contains("folder-povodny.jpg", StringComparison.Ordinal), "posters: the older one renamed – " + page.Result);

        // Ďalšie TMDB obrázky (owner, 31. 10. 2026): off by default, nothing offered; then a block of their own – the background
        // and the logo chosen with the mouse, written by their own button (Spracovať poster writes only the poster)
        Check(!page.Extras && !File.Exists(Path.Combine(folder, "fanart.jpg")) && page.Backdrops.Count == 0 && !page.ProcessExtrasCommand.CanExecute(null),
            "posters: the other pictures are off by default, nothing offered");
        page.Extras = true;
        Check(page.Backdrops.Count > 0 && page.Logos.Count > 0 && page.SelectedBackdrop is { IsRecommended: true, IsSelected: true } && page.SelectedLogo is not null,
            $"posters: the backgrounds and logos offered, the best chosen ({page.Backdrops.Count}, {page.Logos.Count})");
        await page.ProcessCommand.ExecuteAsync(null);
        Check(!File.Exists(Path.Combine(folder, "fanart.jpg")), "posters: Spracovať poster writes only the poster – " + page.Result);
        page.Backdrops[^1].SelectCommand.Execute(null);
        Check(page.SelectedBackdrop == page.Backdrops[^1] && page.Backdrops.Count(b => b.IsSelected) == 1, "posters: another background chosen with the mouse");
        await Screenshot(window, "tmdb-poster-extras.png");
        await page.ProcessExtrasCommand.ExecuteAsync(null);
        Check(File.ReadAllBytes(Path.Combine(folder, "fanart.jpg")) is [0xFF, 0xD8, ..] && File.Exists(Path.Combine(folder, "clearlogo.png")) && page.ExtrasText.Contains("pozadie", StringComparison.Ordinal) &&
              page.ExtrasSizeText.Contains("1920 × 1080", StringComparison.Ordinal),
            $"posters: Spracovať ďalšie TMDB obrázky – fanart.jpg and clearlogo.png in POSTER – {page.ExtrasResult} | {page.ExtrasText} | {page.ExtrasSizeText}");

        await page.DraftCommand.ExecuteAsync(null);
        Check(page.Groups[0].Name == "Návrhy AI" && page.Selected?.Draft is not null && fake.Requests.Any(r => r.Contains("api.cloudflare.com", StringComparison.Ordinal)),
            "posters: a draft by AI (Cloudflare) – " + page.DraftStatus);

        // merged: the film processed (its folder in MOVIES) gets the poster
        var movie = Path.Combine(library, "MOVIES", "Vymyslený plagát (2026)");
        Directory.CreateDirectory(movie);
        page.Merge = true;
        await page.ProcessCommand.ExecuteAsync(null);
        Check(File.Exists(Path.Combine(movie, "folder.jpg")) && !File.Exists(Path.Combine(movie, "fanart.jpg")), "posters: merged into the film's folder – " + page.Result);
        page.Merge = false;
        page.ExtrasMerge = true;
        await page.ProcessExtrasCommand.ExecuteAsync(null);
        Check(File.Exists(Path.Combine(movie, "fanart.jpg")) && File.Exists(Path.Combine(movie, "clearlogo.png")),
            "posters: Zlúčiť ďalšie obrázky z TMDB s filmom/seriálom – to the film processed – " + page.ExtrasResult);
        page.ExtrasMerge = false;
        page.Extras = false;

        services.PosterTmdbForSelfTest = null;
        services.PostersForSelfTest = null;
        services.DraftsForSelfTest = null;
        services.DraftKeysForSelfTest = null;
    }

    private sealed class FakePosters : System.Net.Http.HttpMessageHandler
    {
        private readonly byte[] _poster = FakeHistra.DrawPoster(4);

        public List<string> Requests { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add(uri.AbsoluteUri);
            }

            if (uri.Host == "image.tmdb.org")
            {
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(_poster), RequestMessage = request });
            }

            var body = uri.Host switch
            {
                "api.cloudflare.com" => $$"""{ "result": { "image": "{{Convert.ToBase64String(_poster)}}" }, "success": true }""",
                _ => uri.AbsolutePath switch
                {
                    "/3/search/multi" => """{ "results": [ { "media_type": "movie", "id": 777001, "title": "Vymyslený plagát", "original_title": "Vymyslený plagát", "release_date": "2026-03-01", "popularity": 3 } ] }""",
                    "/3/movie/777001" => """
                        { "id": 777001, "title": "Invented Poster", "original_title": "Vymyslený plagát", "original_language": "sk", "release_date": "2026-03-01", "runtime": 100,
                          "vote_average": 7.1, "vote_count": 42, "genres": [ { "id": 35 } ], "overview": "An invented film.", "poster_path": "/cz-plagat.png",
                          "belongs_to_collection": { "id": 777, "name": "Vymyslené plagáty" },
                          "credits": { "cast": [ { "name": "Vymyslený Herec" } ], "crew": [ { "job": "Director", "name": "Vymyslená Režisérka" } ] },
                          "external_ids": { "imdb_id": "tt7770010" },
                          "translations": { "translations": [ { "iso_639_1": "cs", "iso_3166_1": "CZ", "data": { "title": "Vymyšlený plakát", "overview": "Vymyšlený film." } } ] } }
                        """,
                    "/3/movie/777001/images" => """
                        { "posters": [ { "file_path": "/en-plagat.png", "iso_639_1": "en", "width": 2000, "height": 3000, "vote_average": 6, "vote_count": 3 },
                                       { "file_path": "/cz-plagat.png", "iso_639_1": "cs", "width": 1000, "height": 1500, "vote_average": 5.3, "vote_count": 1 } ],
                          "backdrops": [ { "file_path": "/pozadie.png", "iso_639_1": null, "width": 1920, "height": 1080, "vote_average": 5, "vote_count": 1 },
                                         { "file_path": "/pozadie-en.png", "iso_639_1": "en", "width": 1920, "height": 1080, "vote_average": 6, "vote_count": 2 } ],
                          "logos": [ { "file_path": "/logo-cz.png", "iso_639_1": "cs", "width": 400, "height": 150 } ] }
                        """,
                    "/3/collection/777" => """
                        { "name": "Vymyslené plagáty", "parts": [ { "id": 777001, "title": "Invented Poster", "original_title": "Vymyslený plagát", "release_date": "2026-03-01" },
                                                                 { "id": 777002, "title": "Invented Poster 2", "original_title": "Vymyslený plagát 2", "release_date": "2028-03-01" } ] }
                        """,
                    _ => null,
                },
            };
            return Task.FromResult(body is null
                ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FakeHistra : System.Net.Http.HttpMessageHandler
    {
        private readonly List<byte[]> _posters = Enumerable.Range(0, 6).Select(DrawPoster).ToList();

        public List<(string Method, string Address)> Requests { get; } = [];

        public HashSet<int> Deleted { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add((request.Method.Method, uri.AbsoluteUri));
            }

            if (uri.Host == "image.tmdb.org")
            {
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new System.Net.Http.ByteArrayContent(_posters[(uri.AbsolutePath.Length + uri.AbsolutePath[^5]) % _posters.Count]),
                });
            }

            var offset = System.Text.RegularExpressions.Regex.Match(uri.Query, "offset=([0-9]+)") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            string? body = null;
            if (uri.AbsolutePath == "/api/v1/lists")
            {
                body = offset > 0 ? """{"lists":[]}""" : """
                    {"lists":[
                      {"id":1,"name":"Vymyslené príbehy","description":"","visibility":"public","item_count":30,"updated_at":"2026-10-20T18:00:00.000000+00:00","owner_name":"","is_mine":true,"preview_posters":["/p1.jpg","/p2.jpg","/p3.jpg","/p4.jpg","/p5.jpg"]},
                      {"id":2,"name":"Na víkend","description":"Pár vymyslených titulov","visibility":"private","item_count":3,"updated_at":"2026-10-25T09:30:00.000000+00:00","owner_name":"","is_mine":true,"preview_posters":["/p6.jpg","/p7.jpg"]},
                      {"id":3,"name":"Cudzí list","item_count":12,"is_mine":false,"owner_name":"niekto","preview_posters":[]}
                    ]}
                    """;
            }
            else if (uri.AbsolutePath is "/api/v1/lists/1" or "/api/v1/lists/2")
            {
                var id = uri.AbsolutePath[^1] - '0';
                var total = id == 1 ? 30 : 3;
                if (!Deleted.Contains(id))
                {
                    var items = Enumerable.Range(offset + 1, Math.Max(0, Math.Min(20, total - offset)))
                        .Select(i => id == 1 ? Item(i, i % 7 == 0) : Item(100 + i, i == 3));
                    body = $$"""{"list":{"id":{{id}},"name":"{{(id == 1 ? "Vymyslené príbehy" : "Na víkend")}}","item_count":{{total}},"is_mine":true,"visibility":"public"},"items":[{{string.Join(",", items)}}]}""";
                }
            }

            return Task.FromResult(body is null
                ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request }
                : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"),
                });
        }

        private static string Item(int i, bool series)
        {
            var kind = series ? "show" : "movie";
            var word = series ? "seriál" : "príbeh";
            var original = series ? "Series" : "Story";
            var tvdb = series ? ",\"tvdb\":\"" + (7000 + i).ToString(CultureInfo.InvariantCulture) + "\"" : "";
            var status = series ? ",\"status\":\"Returning Series\"" : "";
            var rating = (5 + i % 5).ToString(CultureInfo.InvariantCulture) + ".4";
            var imdb = "tt" + (9000 + i).ToString("0000000", CultureInfo.InvariantCulture);
            return $$"""{"media_type":"{{kind}}","media_id":{{1000 + i}},"title":"Vymyslený {{word}} {{i}}","original_title":"Invented {{original}} {{i}}","year":{{1990 + i}},"overview":"Vymyslený dej číslo {{i}} – len na skúšku programu.","rating":{{rating}},"runtime":{{90 + i}},"poster_path":"/p{{i}}.jpg","external_ids":{"imdb":"{{imdb}}","tmdb":"{{9000 + i}}","trakt":"{{5000 + i}}"{{tvdb}}}{{status}},"genres":["Dráma","Sci-Fi"]}""";
        }

        // a coloured poster with a gradient (drawn on the window's thread, before any request comes)
        internal static byte[] DrawPoster(int seed)
        {
            var colors = new[] { (0x6C, 0x5C, 0xE7), (0x00, 0xB8, 0x94), (0xE1, 0x70, 0x55), (0x09, 0x84, 0xE3), (0xE8, 0x43, 0x93), (0xFD, 0xCB, 0x6E) };
            var (r, g, b) = colors[seed % colors.Length];
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new LinearGradientBrush(Color.FromRgb((byte)r, (byte)g, (byte)b), Color.FromRgb(0x1E, 0x1E, 0x2E), 90), null, new Rect(0, 0, 154, 231));
                context.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), null, new Point(77, 80), 34, 34);
            }

            var bitmap = new RenderTargetBitmap(154, 231, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
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
            // a title read by its ID (movie/9700) is the title itself, a search a list of results
            var details = !histra && System.Text.RegularExpressions.Regex.IsMatch(request.RequestUri.AbsolutePath, @"/3/(movie|tv)/\d+$");
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(histra ? HistraStatus : System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new System.Net.Http.StringContent(details ? results : "{\"results\":[" + results + "]}", Encoding.UTF8, "application/json"),
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

    // owner, 30. 10. 2026: Titulky – external subtitles found with the video (name, title words), old encodings, FPS and
    // delay (mkvmerge --sync), and subtitle files saved as SRT; invented names only
    // owner, 30. 10. 2026: picture subtitles (PGS of a Blu-ray) shown in the player – an invented .sup next to a video
    private static async Task TestPictureSubtitlesAsync(SubtitlesPageViewModel page, MainWindow window, string folder)
    {
        var pictures = Path.Combine(folder, "obrazky");
        Directory.CreateDirectory(pictures);
        var video = Path.Combine(pictures, "Vymyslený.Obrázkový.Film.2026.mkv");
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"), video, overwrite: true);
        File.WriteAllBytes(Path.Combine(pictures, "Vymyslený.Obrázkový.Film.2026.de.sup"), Sup());
        await page.LoadAsync([video]);
        foreach (var found in page.Found.ToList())
        {
            found.AddCommand.Execute(null);
            await WaitFor(() => !page.Rows.All(r => r.ExternalPath != found.Match.Path) && page.OpenVideoCommand.CanExecute(null));
        }

        var row = page.Rows.FirstOrDefault(r => r.ExternalPath?.EndsWith(".sup", StringComparison.Ordinal) == true);
        Check(row is { IsText: false }, "studio: the .sup next to the video found – " + string.Join(" | ", page.Rows.Select(r => r.Summary)));
        if (row is null)
        {
            return;
        }
        page.SelectedRow = row;
        await page.PlayerOpening;
        await WaitFor(() => page.Player.FramesShown > 0);
        await page.Player.SeekAsync(TimeSpan.FromSeconds(1));
        await WaitFor(() => page.Player.PrimaryPicture is not null);
        // the 1920 × 1080 canvas in the 1280 × 720 frame: two thirds
        var place = page.Player.PrimaryPicturePlace;
        Check(page.Player.PrimaryPicture is BitmapSource { PixelWidth: 6, PixelHeight: 2 } && Math.Abs(place.X - 66.67) < 0.1 && Math.Abs(place.Y - 600) < 0.1 && Math.Abs(place.Width - 4) < 0.01,
            $"studio: the picture subtitle shown where it stands – {place} | {page.Player.Message}");
        Check(page.Preview is [{ Text: "(obrázok)", Before: "0:00:00,200" }], "studio: the picture subtitles in Časy titulkov – " + string.Join(" | ", page.Preview.Select(p => p.Before + " " + p.Text)));
        row.DelayText = "1000";
        await WaitFor(() => page.Player.PrimaryPicture is null);
        Check(page.Player.PrimaryPicture is null, "studio: shifted by a second, the picture subtitle is not shown at 1 s any more");
        await Screenshot(window, "studio-pictures.png");
        row.DelayText = "0";
        page.ClearCommand.Execute(null);

        // one invented subtitle 0,2 s – 1,6 s: 6 × 2 pixels at 100, 900 of a 1920 × 1080 canvas (white, a transparent gap, yellow)
        static byte[] Sup()
        {
            var data = new List<byte>();
            void Segment(double seconds, byte type, byte[] body)
            {
                var pts = (uint)Math.Round(seconds * 90000);
                data.AddRange([(byte)'P', (byte)'G', (byte)(pts >> 24), (byte)(pts >> 16), (byte)(pts >> 8), (byte)pts, 0, 0, 0, 0, type, (byte)(body.Length >> 8), (byte)body.Length]);
                data.AddRange(body);
            }

            byte[] window = [0x01, 0x00, 0x00, 0x64, 0x03, 0x84, 0x00, 0x06, 0x00, 0x02];
            byte[] rle = [0x01, 0x01, 0x00, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x86, 0x02, 0x00, 0x00];
            Segment(0.2, 0x16, [0x07, 0x80, 0x04, 0x38, 0x10, 0x00, 0x00, 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x64, 0x03, 0x84]);
            Segment(0.2, 0x17, window);
            Segment(0.2, 0x14, [0x00, 0x00, 0x01, 235, 128, 128, 255, 0x02, 210, 146, 16, 255]);
            Segment(0.2, 0x15, [0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, (byte)(rle.Length + 4), 0x00, 0x06, 0x00, 0x02, .. rle]);
            Segment(0.2, 0x80, []);
            Segment(1.6, 0x16, [0x07, 0x80, 0x04, 0x38, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00]);
            Segment(1.6, 0x17, window);
            Segment(1.6, 0x80, []);
            return [.. data];
        }
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

    // owner, 31. 10. 2026: Text z obrázkov – UNIVERSUM's own OCR reads invented PGS subtitles drawn in Arial (a white text
    // with a black outline, as on a Blu-ray); a shape no font has (■) is asked once and kept; the text is saved next to
    // the video as a new SRT and added as a track, the .sup stays as it was
    private static async Task TestReadingTextAsync(SubtitlesPageViewModel page, MainWindow window, string folder)
    {
        // anew each run: no result of an earlier run, no shape told before
        var reading = Path.Combine(folder, "citanie");
        if (Directory.Exists(reading))
        {
            Directory.Delete(reading, recursive: true);
        }

        Directory.CreateDirectory(reading);
        File.Delete(SubtitlesPageViewModel.OcrMemoryFile);
        var video = Path.Combine(reading, "Vymyslený.Čítaný.Film.2026.mkv");
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"), video, overwrite: true);
        string[] lines = ["Vymyslený titulok číslo jeden.", "Žltý kôň úpel ďábelské ódy!", "■ Ľudia ťahajú kapra ■"];
        var sup = Path.Combine(reading, "Vymyslený.Čítaný.Film.2026.sk.sup");
        File.WriteAllBytes(sup, TextSup(lines));
        var supBytes = File.ReadAllBytes(sup);
        await page.LoadAsync([video]);
        foreach (var found in page.Found.ToList())
        {
            found.AddCommand.Execute(null);
            await WaitFor(() => !page.Rows.All(r => r.ExternalPath != found.Match.Path) && page.OpenVideoCommand.CanExecute(null));
        }

        var row = page.Rows.FirstOrDefault(r => r.ExternalPath == sup);
        page.SelectedRow = row;
        Check(row is { IsText: false } && page.ReadTextCommand.CanExecute(null), "OCR: the picture subtitles selected, Rozpoznať text ready");
        if (row is null)
        {
            return;
        }

        Show(window, "Text z obrázkov (OCR)");
        var readingText = page.ReadTextCommand.ExecuteAsync(null);
        var asked = 0;
        for (var i = 0; i < 600 && (page.ReadTextCommand.IsRunning || page.IsAsking); i++)
        {
            if (page.IsAsking)
            {
                // the first unknown shape is told (■), any other stays unknown
                asked++;
                if (asked == 1)
                {
                    await Screenshot(window, "ocr-question.png");
                    page.OcrAnswer = "■";
                    await page.TeachCommand.ExecuteAsync(null);
                }
                else
                {
                    await page.SkipShapeCommand.ExecuteAsync(null);
                }

                continue;
            }

            await Task.Delay(100);
        }

        var srt = Path.Combine(reading, "Vymyslený.Čítaný.Film.2026.sk.srt");
        await WaitFor(() => page.Rows.Any(r => r.ExternalPath == srt));
        Check(readingText.IsCompletedSuccessfully, $"OCR: the reading finished – {readingText.Status} {readingText.Exception?.GetBaseException()}".Replace('\n', ' ').Replace('\r', ' '));
        if (asked == 0)
        {
            // what the reading took the square for: the shapes of the third subtitle and their nearest letters
            var shapes = new Core.Subtitles.Ocr.SubtitleOcr(await OcrFonts.TemplatesAsync(), null, "sk")
                .Read(Core.Subtitles.Ocr.OcrPicture.Of(Core.Subtitles.Pictures.PgsReader.Read(supBytes)));
            foreach (var shape in shapes.Clusters.Where(c => c.Cue == 2 && shapes.Context(c).X0 == 0).Take(2))
            {
                Check(false, $"OCR diagnostics: shape {shape.Text} {shape.Distance:0.000} {shape.Sample.Width}x{shape.Sample.Height}: " +
                             string.Join(", ", shape.Candidates.Select(c => $"{c.Text} {c.Distance:0.000}")));
            }
        }

        var read = page.Rows.FirstOrDefault(r => r.ExternalPath == srt)?.Document?.Cues.Select(c => c.Text).ToList() ?? [];
        Check(read.SequenceEqual(lines), $"OCR: the text read and added as a new track ({asked} shape(s) asked) – {string.Join(" | ", read)} | {page.OcrStatus}");
        Check(asked >= 1 && File.Exists(SubtitlesPageViewModel.OcrMemoryFile), "OCR: the unknown shape asked and kept for other films");
        Check(File.ReadAllBytes(sup).SequenceEqual(supBytes) && page.SelectedRow?.ExternalPath == srt, "OCR: the .sup unchanged, the text shown at once");
        await Screenshot(window, "ocr-read.png");
        Show(window, "Stopy titulkov");
        page.ClearCommand.Execute(null);

        // a tab of the page brought forward (for the pictures)
        static void Show(DependencyObject parent, string header)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                if (child is System.Windows.Controls.TabItem { Header: string text } tab && text == header)
                {
                    tab.IsSelected = true;
                    return;
                }

                Show(child, header);
            }
        }

        // the lines as PGS subtitles (2 s each from 1 s): Arial 44 px white, a black outline of 3 px, the rest transparent
        static byte[] TextSup(IReadOnlyList<string> texts)
        {
            var data = new List<byte>();
            void Segment(double seconds, byte type, byte[] body)
            {
                var pts = (uint)Math.Round(seconds * 90000);
                data.AddRange([(byte)'P', (byte)'G', (byte)(pts >> 24), (byte)(pts >> 16), (byte)(pts >> 8), (byte)pts, 0, 0, 0, 0, type, (byte)(body.Length >> 8), (byte)body.Length]);
                data.AddRange(body);
            }

            static byte[] Two(int value) => [(byte)(value >> 8), (byte)value];
            for (var i = 0; i < texts.Count; i++)
            {
                var (width, height, colours) = Draw(texts[i]);
                var rle = Rle(colours, width, height);
                double start = 1 + (i * 2), end = start + 1.5;
                const int x = 200, y = 900;
                Segment(start, 0x16, [0x07, 0x80, 0x04, 0x38, 0x10, .. Two(i * 2), 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, .. Two(x), .. Two(y)]);
                Segment(start, 0x17, [0x01, 0x00, .. Two(x), .. Two(y), .. Two(width), .. Two(height)]);
                Segment(start, 0x14, [0x00, 0x00, 0x01, 235, 128, 128, 255, 0x02, 16, 128, 128, 255]);
                var length = rle.Length + 4;
                Segment(start, 0x15, [0x00, 0x00, 0x00, 0xC0, (byte)(length >> 16), (byte)(length >> 8), (byte)length, .. Two(width), .. Two(height), .. rle]);
                Segment(start, 0x80, []);
                Segment(end, 0x16, [0x07, 0x80, 0x04, 0x38, 0x10, .. Two((i * 2) + 1), 0x00, 0x00, 0x00, 0x00]);
                Segment(end, 0x17, [0x01, 0x00, .. Two(x), .. Two(y), .. Two(width), .. Two(height)]);
                Segment(end, 0x80, []);
            }

            return [.. data];
        }

        // 1 where the letters are, 2 for three pixels around them, 0 elsewhere
        static (int Width, int Height, byte[] Colours) Draw(string text)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 44, Brushes.White, 1);
            int width = (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 16, height = (int)Math.Ceiling(formatted.Height) + 16;
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawText(formatted, new Point(8, 8));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var bgra = new byte[width * height * 4];
            bitmap.CopyPixels(bgra, width * 4, 0);
            var colours = new byte[width * height];
            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    if (bgra[(((py * width) + px) * 4) + 3] < 128)
                    {
                        continue;
                    }

                    colours[(py * width) + px] = 1;
                    for (var dy = -3; dy <= 3; dy++)
                    {
                        for (var dx = -3; dx <= 3; dx++)
                        {
                            int nx = px + dx, ny = py + dy;
                            if (nx >= 0 && ny >= 0 && nx < width && ny < height && colours[(ny * width) + nx] == 0 && bgra[(((ny * width) + nx) * 4) + 3] < 128)
                            {
                                colours[(ny * width) + nx] = 2;
                            }
                        }
                    }
                }
            }

            return (width, height, colours);
        }

        // PGS runs line by line: a colour byte, or 0 and a code (a run of 0 or of a colour, short or long), 0 0 at the end of a line
        static byte[] Rle(byte[] colours, int width, int height)
        {
            var rle = new List<byte>();
            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width;)
                {
                    var colour = colours[(py * width) + px];
                    var run = 1;
                    while (px + run < width && colours[(py * width) + px + run] == colour && run < 16383)
                    {
                        run++;
                    }

                    if (colour != 0 && run < 3)
                    {
                        rle.AddRange(Enumerable.Repeat(colour, run));
                    }
                    else
                    {
                        var flags = (colour != 0 ? 0x80 : 0) | (run >= 64 ? 0x40 : 0);
                        rle.Add(0);
                        if (run >= 64)
                        {
                            rle.Add((byte)(flags | (run >> 8)));
                            rle.Add((byte)run);
                        }
                        else
                        {
                            rle.Add((byte)(flags | run));
                        }

                        if (colour != 0)
                        {
                            rle.Add(colour);
                        }
                    }

                    px += run;
                }

                rle.Add(0);
                rle.Add(0);
            }

            return [.. rle];
        }
    }

    // owner, 30. 10. 2026: Úprava videostopy – the proportions (without writing the picture), an exact cut of a turned picture
    private static async Task TestVideoAsync(MainViewModel model, MainWindow window, AppServices services, string work)
    {
        var folder = Path.Combine(work, "obraz");
        Directory.CreateDirectory(folder);
        var video = Path.Combine(folder, "Vymyslený.Obrazový.Film.2026.mkv");
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"), video, overwrite: true);
        var originalBytes = File.ReadAllBytes(video);
        var page = model.Video;
        model.SelectedTool = model.Tools.First(t => t.Page == page);
        await page.LoadAsync([video]);
        Check(page.IsVideo && page.Summary.Contains("video", StringComparison.Ordinal), "video: the film open – " + page.Summary);
        await page.PlayerOpening;
        await WaitFor(() => page.Player.FramesShown > 0);
        await page.KeyframesReading;
        Check(page.Player.IsOpen && page.Status.StartsWith("Kľúčové snímky", StringComparison.Ordinal), $"video: in the player, its key frames read – {page.Status} | {page.Player.Message}");

        page.Aspect = page.Aspects.First(a => a.Value == "4/3");
        page.UseRules = false;
        page.Library.IsMovie = true;
        page.Library.TitleText = "Obrazový test (2026)";
        Check(page.SaveCommand.CanExecute(null) && page.Changes.Contains("4:3", StringComparison.Ordinal), $"video: 4:3 ready to save – {page.Changes} {page.Blocked}");
        await page.SaveCommand.ExecuteAsync(null);
        Check(page.Result.StartsWith("Uložené do knižnice", StringComparison.Ordinal), "video: saved – " + page.Result);
        var result = Directory.EnumerateFiles(Path.Combine(_outputDirectory, "library"), "*.mkv", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Contains("Obrazový test (2026)", StringComparison.Ordinal));
        if (result is not null)
        {
            var written = await new MkvMergeInspector(services.CurrentMkvToolNix!, services.Runner).InspectAsync(result);
            Check(written.TracksOf(TrackType.Video).Single().DisplayAspect is { } aspect && Math.Abs(aspect - (4 / 3.0)) < 0.05,
                "video: shown 4:3 – " + written.TracksOf(TrackType.Video).Single().DisplayDimensions);
        }
        else
        {
            Check(false, "video: the result in the library");
        }

        // an exact cut of a turned picture: the encoders of Windows (Media Foundation) – a computer without them is said
        var original = (await new MkvMergeInspector(services.CurrentMkvToolNix!, services.Runner).InspectAsync(video)).TracksOf(TrackType.Video).Single();
        page.Aspect = page.Aspects[0];
        page.AddPart(TimeSpan.FromSeconds(0.3), TimeSpan.FromSeconds(1.5));
        Check(page.Parts is [{ Written.Length: > 0 } row] && row.Written.StartsWith("nič", StringComparison.Ordinal) == (page.Problem is { Length: > 0 }),
            $"video: the part moved to key frames (lost between two of them: said) – {page.Parts.FirstOrDefault()?.Written} | {page.Problem}");
        page.ExactCut = true;
        page.Rotation = page.Rotations.First(r => r.Value == 90);
        page.Library.TitleText = "Obrazový test strih (2026)";
        Check(page.Parts is [{ Written: "presne" }] && page.WritesPicture && page.Changes.Contains("presne", StringComparison.Ordinal), "video: an exact cut, turned – " + page.Changes);
        await Screenshot(window, "studio-video.png");
        await page.SaveCommand.ExecuteAsync(null);
        var cut = Directory.EnumerateFiles(Path.Combine(_outputDirectory, "library"), "*.mkv", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Contains("Obrazový test strih (2026)", StringComparison.Ordinal));
        if (page.Result.StartsWith("Uložené do knižnice", StringComparison.Ordinal) && cut is not null)
        {
            var written = await new MkvMergeInspector(services.CurrentMkvToolNix!, services.Runner).InspectAsync(cut);
            var picture = written.TracksOf(TrackType.Video).Single();
            Check(picture.CodecId == "V_MPEG4/ISO/AVC" && written.Duration is { } length && Math.Abs(length.TotalSeconds - 1.2) < 0.35,
                $"video: written anew by the encoders of Windows, cut exactly – {picture.PixelDimensions} {written.Duration}");
            Check(original.PixelDimensions?.Split('x') is [var w, var h] && picture.PixelDimensions == $"{h}x{w}" &&
                  (original.DisplayAspect is not { } before || picture.DisplayAspect is { } after && Math.Abs(after - (1 / before)) < 0.05),
                $"video: turned on its side – {original.PixelDimensions} {original.DisplayAspect:0.###} → {picture.PixelDimensions} {picture.DisplayAspect:0.###}");
        }
        else
        {
            Check(page.Result.Contains("FFmpeg", StringComparison.Ordinal) && !File.Exists(cut ?? ""), "video: without the encoders of Windows it is said, nothing written – " + page.Result);
            Write("info  video: the encoders of Windows did not write here – " + page.Result);
        }

        Check(File.ReadAllBytes(video).AsSpan().SequenceEqual(originalBytes), "video: the original unchanged");
        page.ClearCommand.Execute(null);
    }

    // owner, 30. 10. 2026: Úprava audiostopy – a dub (invented: the film's own sound moved) added, written anew, saved
    private static async Task TestAudioAsync(MainViewModel model, MainWindow window, AppServices services, string work)
    {
        var folder = Path.Combine(work, "zvuk");
        Directory.CreateDirectory(folder);
        var video = Path.Combine(folder, "Vymyslený.Zvukový.Film.2026.mkv");
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"), video, overwrite: true);
        var originalBytes = File.ReadAllBytes(video);
        var ffmpeg = (await services.SpeechAsync()).Ffmpeg;
        var dub = Path.Combine(folder, "Vymyslený dabing.mka");
        var made = await services.Runner.RunAsync(new Core.Processes.ProcessRequest(ffmpeg,
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-i", video, "-map", "0:a:0", "-af", "adelay=300:all=1", "-c:a", "flac", dub]));
        Check(made.ExitCode == 0 && File.Exists(dub), "audio: an invented dub made – " + made.StandardError);

        var page = model.Audio;
        model.SelectedTool = model.Tools.First(t => t.Page == page);
        await page.LoadAsync([video]);
        Check(page.IsVideo && page.Rows.Count == 2 && page.Reference is { IsExternal: false } && page.SelectedRow == page.Reference, "audio: the video's two sounds – " + page.Summary);
        await page.PlayerOpening;
        await WaitFor(() => page.Player.FramesShown > 0);
        Check(page.Player.IsOpen && page.Player.FramesShown > 0, "audio: the film in the player – " + page.Player.Message);

        await page.LoadAsync([dub]);
        var row = page.Rows.LastOrDefault();
        Check(row is { IsExternal: true, Track.ExternalFile: not null } && page.SelectedRow == row && page.Rows.Count == 3, "audio: the dub added and selected – " + page.Result);
        if (row is null)
        {
            return;
        }
        // too short to be fitted: said, nothing changed
        await page.SyncCommand.ExecuteAsync(null);
        Check(page.SyncStatus.Length > 0 && row.DelayText == "0", "audio: Napasovať on two seconds says something – " + page.SyncStatus);

        row.DelayText = "-300";
        row.FpsFrom = Core.Subtitles.FrameRate.All.Single(r => r.Label == "25");
        row.FpsTo = Core.Subtitles.FrameRate.All.Single(r => r.Label == "23,976");
        row.Codec = row.Codecs.Single(c => c.Codec == Core.Audio.AudioCodec.Aac);
        row.Stereo = true;
        row.Language = row.LanguageChoices.First(c => c.Tag.Primary == "cs");
        row.Name = "Vymyslený dabing CZ";
        Check(row.Recipe.NeedsEncoding && row.Problem is null && row.Summary.Contains("pomalšie", StringComparison.Ordinal) && row.Summary.Contains("zapíše znova", StringComparison.Ordinal),
            "audio: the dub's changes – " + row.Summary);
        await Screenshot(window, "studio-audio.png");
        // the rules stop on the undetermined subtitles of the video (set elsewhere): written as the tracks are
        Check(page.Blocked.Contains("Úprave titulkostopy", StringComparison.Ordinal), "audio: the rules say where to set the subtitles' language – " + page.Blocked);
        page.UseRules = false;
        page.Library.IsMovie = true;
        page.Library.TitleText = "Zvukový test (2026)";
        Check(page.SaveCommand.CanExecute(null), "audio: ready to save – " + page.Blocked + " " + page.Library.Description);
        await page.SaveCommand.ExecuteAsync(null);
        Check(page.Result.StartsWith("Uložené do knižnice", StringComparison.Ordinal), "audio: saved – " + page.Result);
        Check(File.ReadAllBytes(video).AsSpan().SequenceEqual(originalBytes) && File.Exists(dub), "audio: the originals unchanged");
        var result = Directory.EnumerateFiles(Path.Combine(_outputDirectory, "library"), "*.mkv", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Contains("Zvukový test (2026)", StringComparison.Ordinal));
        if (result is not null)
        {
            var written = await new MkvMergeInspector(services.CurrentMkvToolNix!, services.Runner).InspectAsync(result);
            Check(written.TracksOf(TrackType.Audio).Count() == 3 &&
                  written.TracksOf(TrackType.Audio).Any(t => t is { CodecId: "A_AAC", AudioChannels: 2, Name: "Vymyslený dabing CZ" } && t.Language.Primary == "cs"),
                "audio: the dub written as stereo AAC with its language and name – " + string.Join(" | ", written.TracksOf(TrackType.Audio).Select(t => $"{t.CodecId} {t.AudioChannels} {t.Language.Primary} {t.Name}")));
        }
        else
        {
            Check(false, "audio: the result in the library");
        }

        // Uložiť prácu: the sounds come back with the video, the dub's file too
        page.SaveWorkCommand.Execute(null);
        Check(page.HasWork && services.AudioWork.Load(video) is { Sounds.Count: 3 }, "audio: the work saved – " + page.WorkNote);
        page.ClearCommand.Execute(null);
        await page.LoadAsync([video]);
        var back = page.Rows.FirstOrDefault(r => r.IsExternal);
        Check(page.HasWork && page.Rows.Count == 3 && back is { DelayText: "-300", Stereo: true, Name: "Vymyslený dabing CZ", Codec.Codec: Core.Audio.AudioCodec.Aac } && back.Language.Tag.Primary == "cs",
            $"audio: the work comes back with the video – {page.WorkNote} | {back?.Summary}");
        page.DiscardWorkCommand.Execute(null);
        Check(!page.HasWork && services.AudioWork.Load(video) is null, "audio: the work discarded");
        page.ClearCommand.Execute(null);
    }

    private static async Task TestSubtitlesAsync(MainViewModel model, MainWindow window, AppServices services, string work)
    {
        var folder = Path.Combine(work, "titulky");
        Directory.CreateDirectory(folder);
        var video = Path.Combine(folder, "Vymyslený.Titulkový.Film.2026.mkv");
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA")!, "media", "en-und-cz-forced.mkv"), video, overwrite: true);
        var originalBytes = File.ReadAllBytes(video);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(Path.Combine(folder, "Vymyslený.Titulkový.Film.2026.sk.srt"),
            Encoding.GetEncoding(1250).GetBytes("1\r\n00:00:00,500 --> 00:00:01,200\r\nĽudia sa vrátia domov, päť dní je veľmi dlho.\r\n"));
        var vtt = Path.Combine(folder, "vymysleny_titulkovy_film_2026_en.vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00.300 --> 00:01.000\nPeople will come home.\n");
        File.WriteAllText(Path.Combine(folder, "Iný.Film.2026.cze.srt"), "1\r\n00:00:00,500 --> 00:00:01,000\r\nIný film.\r\n");

        var page = model.Subtitles;
        model.SelectedTool = model.Tools.First(t => t.Page == page);
        await page.LoadAsync([video]);
        Check(page.IsVideo && page.Rows.Count == 3 && page.Found.Count == 2,
            $"subtitles: three tracks of the video, two files found with it (not another film) – {page.Summary} | {string.Join(" | ", page.Found.Select(f => f.Display))}");
        foreach (var found in page.Found.ToList())
        {
            found.AddCommand.Execute(null);
            await WaitFor(() => !page.Rows.All(r => r.ExternalPath != found.Match.Path) && page.OpenVideoCommand.CanExecute(null));
        }

        var slovak = page.Rows.FirstOrDefault(r => r.ExternalPath?.EndsWith(".sk.srt", StringComparison.Ordinal) == true);
        var english = page.Rows.FirstOrDefault(r => r.ExternalPath == vtt);
        Check(slovak is { Language.Tag.Primary: "sk" } && slovak.Note.Contains("Windows-1250", StringComparison.Ordinal), "subtitles: Slovak file in Windows-1250 – " + slovak?.Note);
        Check(english is { Format: "WebVTT", Language.Tag.Primary: "en" }, "subtitles: English WebVTT by the title words – " + english?.Note);
        page.SelectedRow = slovak;
        // owner, 30. 10. 2026: Track name – the rules' name shown until the user writes his own
        Check(slovak!.Name.Length == 0 && page.Target()!.Tracks.All(t => t.Type != TrackType.Subtitles || t.TrackId == 3 || t.Name is null), "studio: the rules' names shown – " + slovak.Name);
        slovak.Name = "Slovenské – vymyslené";
        Check(page.Target()!.Tracks.Any(t => t.TrackId == slovak.Track.Id && t.Name == "Slovenské – vymyslené"), "studio: the name written wins over the rules'");
        slovak.FpsFrom = Core.Subtitles.FrameRate.All.Single(r => r.Label == "25");
        slovak.FpsTo = Core.Subtitles.FrameRate.All.Single(r => r.Label == "23,976");
        slovak.DelayText = "250";
        Check(page.Preview.Count == 1 && page.Preview[0].After == "0:00:00,771", "subtitles: 25 → 23,976 FPS and +250 ms in the preview – " + string.Join(" | ", page.Preview.Select(p => p.After)));
        Check(slovak.Summary.Contains("FPS 25 → 23,976", StringComparison.Ordinal) && slovak.Summary.Contains("posun +250 ms", StringComparison.Ordinal), "subtitles: the summary – " + slovak.Summary);

        // owner, 30. 10. 2026: Úprava titulkostopy – the film plays here with the subtitles being edited (the top priority)
        await page.PlayerOpening;
        await WaitFor(() => page.Player.FramesShown > 0);
        Check(page.Player.IsOpen && page.Player.FramesShown > 0, $"studio: the film open in the player, its first picture shown ({page.Player.FramesShown}) – {page.Player.Message}");
        page.CompareRow = english;
        await page.Player.SeekAsync(TimeSpan.FromSeconds(0.8));
        await WaitFor(() => page.Player.PrimaryText.Length > 0 && page.Player.SecondaryText.Length > 0);
        Check(page.Player.PrimaryText.StartsWith("Ľudia sa vrátia domov", StringComparison.Ordinal) && page.Player.SecondaryText == "People will come home.",
            $"studio: at 0,8 s the Slovak subtitles with their changes (from 0,771 s) and the English ones to compare – „{page.Player.PrimaryText}“ / „{page.Player.SecondaryText}“");
        page.StartsNowCommand.Execute(null);
        Check(slovak.DelayText == "279", $"studio: Titulok začína teraz – the shift grows by 29 ms ({slovak.DelayText}) – {page.LiveStatus}");
        slovak.DelayText = "250";
        var shown = page.Player.FramesShown;
        await page.Player.PlayPauseCommand.ExecuteAsync(null);
        await Task.Delay(700);
        var played = page.Player.Now;
        page.Player.Pause();
        Check(played > TimeSpan.FromSeconds(1.2) && page.Player.FramesShown > shown, $"studio: playing moves the film on ({played}, {page.Player.FramesShown - shown} pictures)");
        await page.Player.SeekAsync(TimeSpan.FromSeconds(1));

        // owner, 30. 10. 2026: the text of a single subtitle changed in Časy titulkov, a subtitle left out and back
        Check(page.Preview is [{ IsEditable: true, Index: 0 }] && page.RemoveCueCommand.CanExecute(page.Preview[0]), "studio: the subtitle can be changed in the list");
        page.RemoveCueCommand.Execute(page.Preview[0]);
        Check(page.Preview.Count == 0 && slovak.TextEdited && page.UndoCueEditsCommand.CanExecute(null), "studio: the subtitle left out");
        page.UndoCueEditsCommand.Execute(null);
        Check(page.Preview.Count == 1 && !slovak.TextEdited, "studio: and back");
        page.SelectedPreview = page.Preview[0];
        page.Preview[0].Text = "Ľudia sa vrátia | domov.";
        await WaitFor(() => slovak.TextEdited);
        Check(slovak.CueEdits.TryGetValue(0, out var edited) && edited == "Ľudia sa vrátia\ndomov." && page.Preview is [{ Text: "Ľudia sa vrátia | domov." }] &&
              page.SelectedPreview == page.Preview[0] && page.Player.PrimaryText.Contains("domov.", StringComparison.Ordinal) && !page.Player.PrimaryText.Contains("päť", StringComparison.Ordinal),
            $"studio: the text changed – „{edited}“ | player „{page.Player.PrimaryText}“");
        slovak.RemoveHearingImpaired = true;
        Check(!slovak.CanEditCues && page.Preview is [{ IsEditable: false, Text: "Ľudia sa vrátia | domov." }], "studio: repairs of the whole text work on the changed text, the list cannot be changed then");
        slovak.RemoveHearingImpaired = false;
        await Screenshot(window, "studio-subtitles.png");
        // automatic sync on a 2-second file: nothing to tell, never a crash
        page.SyncByAudio = true;
        await page.SyncCommand.ExecuteAsync(null);
        Check(page.SyncStatus.Length > 0 && slovak.DelayText == "250", "subtitles: automatic sync says something and changes nothing it cannot tell – " + page.SyncStatus);
        await page.CheckTracksCommand.ExecuteAsync(null);
        Check(page.SyncStatus.Length > 0, "subtitles: the check of the video's tracks – " + page.SyncStatus);
        slovak.SpeedText = "-100";
        Check(slovak.Problem is not null && !page.SaveCommand.CanExecute(null), "subtitles: −100 % is not possible");
        slovak.SpeedText = "0";
        page.Library.IsMovie = true;
        page.Library.TitleText = "Titulkový test (2026)";
        Check(page.Blocked.Contains("neurčený jazyk", StringComparison.Ordinal) && !page.SaveCommand.CanExecute(null),
            "subtitles: the rules stop on the audio of an undetermined language – " + page.Blocked);
        page.UseRules = false;
        Check(page.SaveCommand.CanExecute(null), "subtitles: without the rules ready to save – " + page.Library.Description);
        await Screenshot(window, "subtitles.png");
        await page.SaveCommand.ExecuteAsync(null);
        Check(page.Result.StartsWith("Uložené do knižnice", StringComparison.Ordinal), "subtitles: saved – " + page.Result);
        Check(File.ReadAllBytes(video).AsSpan().SequenceEqual(originalBytes), "subtitles: original unchanged, byte for byte");
        var result = Directory.EnumerateFiles(Path.Combine(_outputDirectory, "library"), "*.mkv", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Contains("Titulkový test (2026)", StringComparison.Ordinal));
        Check(result is not null, "subtitles: the result in the library");
        if (result is not null)
        {
            var written = await new MkvMergeInspector(services.CurrentMkvToolNix!, services.Runner).InspectAsync(result);
            Check(written.TracksOf(TrackType.Subtitles).Any(t => t.Language.Primary == "sk" && t.Name == "Slovenské – vymyslené") && written.TracksOf(TrackType.Subtitles).Any(t => t.Language.Primary == "en"),
                "subtitles: Slovak and English added – " + string.Join(", ", written.TracksOf(TrackType.Subtitles).Select(t => t.Language.Primary)));
        }

        // owner, 30. 10. 2026: Uložiť prácu – the values of the subtitles come back when the video is opened again
        page.SaveWorkCommand.Execute(null);
        Check(page.HasWork && page.WorkNote.StartsWith("Práca je uložená", StringComparison.Ordinal), "studio: the work saved – " + page.WorkNote);
        await page.LoadAsync([video]);
        var restored = page.Rows.FirstOrDefault(r => r.ExternalPath?.EndsWith(".sk.srt", StringComparison.Ordinal) == true);
        Check(page.HasWork && page.WorkNote.StartsWith("Obnovená uložená práca", StringComparison.Ordinal) && page.Rows.Count == 5 &&
              restored is { FpsFrom.Label: "25", FpsTo.Label: "23,976", DelayText: "250", TextEdited: true, WrittenName: "Slovenské – vymyslené" },
            $"studio: the work comes back with the video, the changed text too – {page.WorkNote} | {restored?.Summary}");
        page.DiscardWorkCommand.Execute(null);
        Check(!page.HasWork && services.StudioWork.Load(video) is null, "studio: the work discarded");
        await TestPictureSubtitlesAsync(page, window, folder);
        await TestReadingTextAsync(page, window, folder);

        // subtitle files alone: a new SRT in UTF-8 next to them, the original stays
        page.ClearCommand.Execute(null);
        await page.LoadAsync([vtt]);
        Check(page.IsFiles && page.Rows.Count == 1 && page.SaveCommand.CanExecute(null), "subtitles: a WebVTT file alone – " + page.Summary);
        await page.SaveCommand.ExecuteAsync(null);
        var srt = Path.ChangeExtension(vtt, ".srt");
        Check(File.Exists(srt) && File.ReadAllText(srt).Contains("00:00:00,300 --> 00:00:01,000", StringComparison.Ordinal) && File.Exists(vtt),
            "subtitles: converted to SRT next to it, the original kept – " + page.Result);
        page.OutputFormat = "WebVTT";
        await page.SaveCommand.ExecuteAsync(null);
        var newVtt = Path.Combine(folder, "vymysleny_titulkovy_film_2026_en (upravené).vtt");
        Check(File.Exists(newVtt) && File.ReadAllText(newVtt).StartsWith("WEBVTT", StringComparison.Ordinal), "subtitles: saved as WebVTT too, never over the original – " + page.Result);
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
        batch.ConfirmTitlesCommand.Execute(null);
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
              batch.RunState.Contains("úprava titulkostopy v úlohe", StringComparison.Ordinal),
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
        batch.AcceptGroupCommand.Execute(null);
        Check(batch.SeriesTitle == "Vymyslený seriál (2024)" && batch.FileRows.Select(f => f.Naming).Order().SequenceEqual(["S01E01", "S01E02"]) &&
              batch.Items.Where(i => i.File.Path.StartsWith(show, StringComparison.Ordinal))
                  .All(i => i.PlannedPath?.Contains(Path.Combine("TVSHOWS", "Vymyslený seriál (2024)", "Season 01"), StringComparison.Ordinal) == true),
            "batch: Prijať všetko v skupine – the series and its episodes accepted – " + batch.RunState);
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
    }

    // owner, 1. 11. 2026 (bezchybné preklady): the window drawn in English and in Czech; what is still Slovak on the pages
    // is written to lang-en-slovak.txt / lang-cs-slovak.txt (the invented names of the test data are among them)
    private static async Task TestLanguagesAsync(AppServices services)
    {
        // every translation keeps the values of its Slovak text ({0}, {1:0.#} …) and is not empty
        var values = new System.Text.RegularExpressions.Regex(@"\{\d+(?:[,:][^}]*)?\}");
        string Values(string text) => string.Join(" ", values.Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal));
        var wrong = new[] { ("cs", Localization.Translations.Czech), ("en", Localization.Translations.English) }
            .SelectMany(table => table.Item2.Where(t => t.Value.Trim().Length == 0 || Values(t.Key) != Values(t.Value)).Select(t => $"{table.Item1}: {t.Key}"))
            .ToList();
        Check(wrong.Count == 0 && Localization.Translations.Czech.Count == Localization.Translations.English.Count,
            $"languages: {Localization.Translations.English.Count} translations with the same values as the Slovak texts – {string.Join(" | ", wrong.Take(5))}");

        foreach (var (language, marks) in new[] { ("en", "áäčďéíĺľňóôŕšťúýžÁÄČĎÉÍĽĹŇÓÔŔŠŤÚÝŽ"), ("cs", "äĺľôŕÄĹĽÔŔ") })
        {
            Localization.Loc.Init(language);

            // stage 4: the texts of the core follow the window – names of languages, the weather
            var german = Universum.Core.Media.Languages.Find("de")?.Name;
            var snow = Universum.Core.Weather.OpenMeteo.Describe(71);
            Check((language, german, snow) is ("en", "German", "light snow") or ("cs", "němčina", "slabé sněžení"),
                $"languages: the core's texts in {language} – {german}, {snow}");
            MainViewModel? model = null;
            Window? window = null;
            try
            {
                model = new MainViewModel(services);
                window = new MainWindow(model) { Width = 1180, Height = 740, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                window.Show();
                await Settle();
                var shots = new Dictionary<object, string>
                {
                    [model.Dashboard] = "dashboard", [model.Mux] = "zlucovanie", [model.Batch] = "hromadne", [model.Mega] = "mega",
                    [model.Rules] = "single", [model.Settings] = "nastavenia", [model.Calendar] = "kalendar",
                };
                var slovak = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var tool in model.Tools.Where(t => !t.IsGuide))
                {
                    model.SelectedTool = tool;
                    await Settle();
                    foreach (var text in VisibleTexts(window).Where(t => t.IndexOfAny(marks.ToCharArray()) >= 0))
                    {
                        slovak.Add(text);
                    }

                    if (shots.TryGetValue(tool.Page, out var name))
                    {
                        await Screenshot(window, $"lang-{language}-{name}.png");
                    }
                }

                File.WriteAllLines(Path.Combine(_outputDirectory, $"lang-{language}-slovak.txt"), slovak, new UTF8Encoding(false));
                Write($"languages: {language} – {slovak.Count} texts on the pages still look Slovak");
                var expected = language == "en" ? ("Batch processing", "Settings") : ("Hromadné zpracování", "Nastavení");
                var titles = (model.Tools.First(t => t.Page == model.Batch).Title, model.Tools.First(t => t.Page == model.Settings).Title);
                Check(titles == expected, $"languages: the left rail in {language} – {titles}");
            }
            finally
            {
                model?.StopTimers();
                window?.Close();
                Localization.Loc.Init(services.Settings.UiLanguage);
            }
        }
    }

    // the texts drawn in the window now (TextBlocks of the visual tree – not what a user typed into a TextBox)
    private static IEnumerable<string> VisibleTexts(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (item is System.Windows.Controls.TextBox or System.Windows.Controls.RichTextBox)
            {
                continue;
            }

            if (item is System.Windows.Controls.TextBlock { Text: { Length: > 0 } text } block && block.IsVisible && text.Trim().Length > 0)
            {
                yield return text.Trim().ReplaceLineEndings(" ⏎ ");
            }

            for (var i = VisualTreeHelper.GetChildrenCount(item) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(item, i));
            }
        }
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

    // napovedkyňa (owner, 30. 10. 2026): a series processed before is named at once, without a search (invented names)
    private static void TestSeriesMemory(MainViewModel model, AppServices services, string work)
    {
        var episode = Path.Combine(work, "Stiahnute", "Vymysleny.Napovedany.Serial.S02E07.1080p.WEB.mkv");
        Check(services.RememberedSeriesFor(episode) is null, "napovedkyňa: an unknown series is not named");
        var series = new TitleInfo(MediaKind.Series, "Vymyslený napovedaný seriál", 2024, 990001);
        services.RememberSeries(series, Path.Combine(work, "Iny tyzden", "Vymysleny Napovedany Serial S02E01.mkv"));
        Check(services.RememberedSeriesFor(episode) == series, "napovedkyňa: the next episode gets the remembered series");
        Check(model.Settings.SeriesMemory.Rows.Any(r => r.Series.Title == series), "napovedkyňa: the series is listed in Nastavenia");
        model.Settings.SeriesMemory.ForgetCommand.Execute(model.Settings.SeriesMemory.Rows.First(r => r.Series.Title == series));
        Check(services.RememberedSeriesFor(episode) is null, "napovedkyňa: a forgotten series is searched again");
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
