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
internal static partial class SelfTest
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

        // the tracks arranged at once after a change (the pause the user gets is tried in Zlučovanie on its own)
        ArrangeLater.Delay = TimeSpan.Zero;
        Environment.SetEnvironmentVariable("UNIVERSUM_SETTINGS_DIR", Path.Combine(_outputDirectory, "settings"));
        // the language of audio is heard after an analysis only where a test asks for it (it is tried on its own)
        services.Settings.BatchDetectAudio = false;

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

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(100);
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
        Check(services.RememberedTitleFor(episode) is null, "napovedkyňa: an unknown series is not named");
        var series = new TitleInfo(MediaKind.Series, "Vymyslený napovedaný seriál", 2024, 990001);
        services.RememberTitle(series, Path.Combine(work, "Iny tyzden", "Vymysleny Napovedany Serial S02E01.mkv"));
        Check(services.RememberedTitleFor(episode) == series, "napovedkyňa: the next episode gets the remembered series");
        Check(model.Settings.SeriesMemory.Rows.Any(r => r.Series.Title == series), "napovedkyňa: the series is listed in Nastavenia");
        model.Settings.SeriesMemory.ForgetCommand.Execute(model.Settings.SeriesMemory.Rows.First(r => r.Series.Title == series));
        Check(services.RememberedTitleFor(episode) is null, "napovedkyňa: a forgotten series is searched again");
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
