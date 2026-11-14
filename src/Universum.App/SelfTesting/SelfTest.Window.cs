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

// the self-test: the window – its pages and menus, the context menu of the Explorer, guides, actions, reports
internal static partial class SelfTest
{
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
              "-: Dashboard | NOVINKY A OBSAH: Premiéry, Novinky – Seriály, Listy z HISTRA, TMDB poster, Zaujímavosti | POZNÁMKY: Kalendár, Plánované úlohy, Poznámky | " +
              "MULTIPLEXING: Zlučovanie, Úprava hlavičky, Kapitoly, Informácie | " +
              "EDITÁCIA VIDEOSTÔP (video, audio, titulky): Úprava videostopy, Úprava audiostopy, Úprava titulkostopy | " +
              "ŠABLÓNY (TEMPLATE): Single spracovanie, Hromadné spracovanie, MEGA spracovanie | ŠTATISTIKA: Fronta úloh, Výstup úloh, Záznamy spracovania, Spracované tituly, Nastavenia | " +
              "NÁPOVEDA: Novinky a obsah, Poznámky, Multiplexing, Editácia videostôp, Šablóny (template), Štatistika",
            "left rail: Dashboard, the groups, then NÁPOVEDA with the guides in the order of the groups (owner, 1. 11. 2026)");
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
        await TestMuxRecognitionAsync(model, window, work);
        await TestSrtConversionAsync(model, window, services, work);
        await TestNewsToAgendaAsync(model, window, services);
        await TestDisksAsync(model, window, services);
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
}
