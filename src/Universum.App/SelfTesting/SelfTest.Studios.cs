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

// the self-test: the studios – titulky (pictures, reading), video, zvuk
internal static partial class SelfTest
{
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
}
