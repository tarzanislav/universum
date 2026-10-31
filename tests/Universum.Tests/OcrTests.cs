using System.Globalization;
using Universum.Core.Processes;
using Universum.Core.Subtitles.Ocr;
using Universum.Core.Subtitles.Pictures;

namespace Universum.Tests;

/// <summary>
/// UNIVERSUM's own reading of picture subtitles (owner, 31. 10. 2026): letters drawn by FFmpeg in a free font are the
/// templates, invented subtitles drawn as on a Blu-ray (white with a black outline, another size) are read back.
/// </summary>
public sealed class OcrTests
{
    private const int Size = 48;

    [Test]
    public void ContextMendsLookAlikeLetters()
    {
        Assert.Equal("Bolo to 1990 a ples, ľudí je veľa", OcrWords.Mend("Bolo to I99O a pIes, l'udí je veI'a", "sk"), "numbers, I/l, the Slovak ľ");
        Assert.Equal("„Áno,“ povedal.", OcrWords.Mend(",,Áno,'' povedal.", "cs"), "Czech quotation marks");
        Assert.Equal("didn't it'd", OcrWords.Mend("didn't it'd", "en"), "English apostrophes stay");
        Assert.Equal("Ide Ivan", OcrWords.Mend("Ide Ivan", "sk"), "a capital I at the start of a word stays");
        Assert.Equal("<i>Ľudia ťahajú, ťahali</i>", OcrWords.ReplaceWord("<i>Ľudia tahajú, ťahali</i>", "tahajú", "ťahajú"), "a whole word put back");
        Assert.True(OcrWords.ReplaceWord("ťahajúci", "ťahajú", "x") is null, "not a part of a longer word");
    }

    [Test]
    public void TaughtLettersAreRememberedForOtherFilms()
    {
        // an invented shape (a small hook) told once is found again by a new memory read from the file
        byte[] pixels =
        [
            0, 1, 1, 1, 0,
            1, 0, 0, 0, 1,
            0, 0, 0, 1, 0,
            0, 0, 1, 0, 0,
            0, 0, 1, 0, 0,
            0, 0, 0, 0, 0,
            0, 0, 1, 0, 0,
        ];
        var shape = GlyphShape.Of(new InkBitmap(5, 7, pixels), 7, 7);
        var folder = Directory.CreateTempSubdirectory("universum-ocr-").FullName;
        try
        {
            var file = Path.Combine(folder, "naucene.json");
            new OcrMemory(file).Add(shape, "¿");
            var again = new OcrMemory(file);
            Assert.True(again.Count == 1 && again.Nearest(shape) is { Text: "¿", Distance: 0 }, "the told letter read back from the file");
            again.Add(shape, "?");
            Assert.True(new OcrMemory(file) is { Count: 1 } newest && newest.Nearest(shape)?.Text == "?", "telling the same shape again replaces it");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task InventedSubtitlesAreReadBack()
    {
        if (Tools() is not { } tools)
        {
            Assert.Skip("FFmpeg with drawtext and the DejaVu font are not here");
            return;
        }

        var (ffmpeg, font) = tools;
        var folder = Directory.CreateTempSubdirectory("universum-ocr-").FullName;
        try
        {
            var templates = OcrTemplates.From(await DrawAlphabetAsync(ffmpeg, font, folder));
            Assert.True(templates.Count >= OcrTemplates.Characters.Length - 2, $"templates drawn: {templates.Count}");

            string[] lines =
            [
                "Vymyslený titulok č. 12, ďalší!",
                "Žltý kôň úpel ďábelské ódy?",
                "Ľudia ťahajú vymysleného kapra.",
            ];
            var pictures = new List<OcrPicture>();
            for (var i = 0; i < lines.Length; i++)
            {
                pictures.Add(new OcrPicture(TimeSpan.FromSeconds(i * 3), TimeSpan.FromSeconds((i * 3) + 2), await DrawSubtitleAsync(ffmpeg, font, folder, lines[i], 38 + (i * 4))));
            }

            var memory = new OcrMemory(Path.Combine(folder, "pamat.json"));
            var started = DateTime.UtcNow;
            var run = new SubtitleOcr(templates, memory, "sk").Read(pictures);
            var took = DateTime.UtcNow - started;
            foreach (var unknown in run.Unknown)
            {
                run.Teach(unknown, "?");
            }

            var read = run.Document().Cues.Select(c => c.Text).ToList();
            var (letters, known) = run.Coverage;
            Assert.True(read.SequenceEqual(lines), $"read back ({known}/{letters} letters, {run.Clusters.Count} shapes, {took.TotalMilliseconds:0} ms): {string.Join(" | ", read)}");
            Assert.True(took < TimeSpan.FromSeconds(5), "quick");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void LexiconKnowsWordsAndTheirLetters()
    {
        var lexicon = new OcrLexicon();
        lexicon.Learn("<i>Vymyslený</i> kôň ťahá voz, kôň spí.\nĽudia sa pozerajú.");
        Assert.True(lexicon.Frequency("Kôň") == 2 && lexicon.Frequency("ľudia") == 1 && lexicon.Frequency("i") == 0 && lexicon.Count == 8, "words counted, tags left out");
        Assert.True(lexicon.Likeness("kôň") > lexicon.Likeness("ňkô") + 1, $"usual letters more likely: {lexicon.Likeness("kôň"):0.00} / {lexicon.Likeness("ňkô"):0.00}");
        var folder = Directory.CreateTempSubdirectory("universum-ocr-").FullName;
        try
        {
            var file = Path.Combine(folder, "slovnik.json");
            lexicon.Save(file);
            var again = OcrLexicon.Load(file);
            Assert.True(again.Count == 8 && again.Frequency("kôň") == 2 && Math.Abs(again.Likeness("ťahá") - lexicon.Likeness("ťahá")) < 1e-9, "kept in a file");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task CapitalsWithAccentsAndShortLinesAreRead()
    {
        if (Tools() is not { } tools)
        {
            Assert.Skip("FFmpeg with drawtext and the DejaVu font are not here");
            return;
        }

        var (ffmpeg, font) = tools;
        var folder = Directory.CreateTempSubdirectory("universum-ocr-").FullName;
        try
        {
            var templates = OcrTemplates.From(await DrawAlphabetAsync(ffmpeg, font, folder));

            // a few letters, an accented capital the tallest of them: its accent is not the height of capitals
            string[] lines = ["Čo je to?", "Áno.", "Žiadny.", "Vymyslený titulok číslo dva.", "Čo tu robíte? Ústa zavrite, Úbohý Šimon."];
            var pictures = new List<OcrPicture>();
            for (var i = 0; i < lines.Length; i++)
            {
                pictures.Add(new OcrPicture(TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1), await DrawSubtitleAsync(ffmpeg, font, folder, lines[i], 44)));
            }

            // a line of a dash and dots under a line of text, close: a line of its own
            pictures.Add(new OcrPicture(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10), await DrawLinesAsync(ffmpeg, font, folder, ["- Kto je tam?", "- ..."], 44, 1.09)));
            var read = new SubtitleOcr(templates, null, "sk").Read(pictures).Document().Cues.Select(c => c.Text).ToList();
            Assert.True(read.SequenceEqual([.. lines, "- Kto je tam?\n- …"]), "read: " + string.Join(" | ", read));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task PgsPicturesAreReadWithTheirTimes()
    {
        if (Tools() is not { } tools)
        {
            Assert.Skip("FFmpeg with drawtext and the DejaVu font are not here");
            return;
        }

        var (ffmpeg, font) = tools;
        var folder = Directory.CreateTempSubdirectory("universum-ocr-").FullName;
        try
        {
            var templates = OcrTemplates.From(await DrawAlphabetAsync(ffmpeg, font, folder));

            // a Blu-ray subtitle: white letters with a black outline on a transparent picture, placed on a 1920 × 1080 canvas
            var drawn = await DrawSubtitleAsync(ffmpeg, font, folder, "Ôsmy vymyslený titulok?", 44);
            var cue = new PictureCue(TimeSpan.FromSeconds(61.5), TimeSpan.FromSeconds(64), 1920, 1080,
                [new PictureObject(300, 900, drawn.Width, drawn.Height, Rle(drawn))], Palette());
            var pictures = OcrPicture.Of([cue]);
            Assert.Equal(1, pictures.Count, "the picture drawn");

            var document = new SubtitleOcr(templates, null, "sk").Read(pictures).Document();
            Assert.True(document.Cues is [{ Text: "Ôsmy vymyslený titulok?" } only] && only.Start == cue.Start && only.End == cue.End,
                "read with its times – " + string.Join(" | ", document.Cues.Select(c => $"{c.Start} {c.Text}")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        // 0 transparent, 1 white, 2 black (the outline)
        static uint[] Palette()
        {
            var palette = new uint[256];
            palette[1] = 0xFFFFFFFF;
            palette[2] = 0xFF000000;
            return palette;
        }

        // the letters (ink) white, three pixels around them black, the rest transparent – coded as PGS runs, line by line
        static byte[] Rle(InkBitmap ink)
        {
            var colours = new byte[ink.Width * ink.Height];
            for (var y = 0; y < ink.Height; y++)
            {
                for (var x = 0; x < ink.Width; x++)
                {
                    colours[(y * ink.Width) + x] = ink[x, y] ? (byte)1 : Near(ink, x, y) ? (byte)2 : (byte)0;
                }
            }

            var rle = new List<byte>();
            for (var y = 0; y < ink.Height; y++)
            {
                for (var x = 0; x < ink.Width;)
                {
                    var colour = colours[(y * ink.Width) + x];
                    var run = 1;
                    while (x + run < ink.Width && colours[(y * ink.Width) + x + run] == colour && run < 16383)
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

                    x += run;
                }

                rle.Add(0);
                rle.Add(0);
            }

            return [.. rle];
        }

        static bool Near(InkBitmap ink, int x, int y)
        {
            for (var dy = -3; dy <= 3; dy++)
            {
                for (var dx = -3; dx <= 3; dx++)
                {
                    if (x + dx >= 0 && y + dy >= 0 && x + dx < ink.Width && y + dy < ink.Height && ink[x + dx, y + dy])
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    [Test]
    public async Task AnotherFontIsReadByTheNearestShapes()
    {
        const string fonts = "/usr/share/fonts/truetype/";
        if (Tools() is not { } tools || !File.Exists(fonts + "liberation/LiberationSans-Regular.ttf"))
        {
            Assert.Skip("FFmpeg with drawtext and the Liberation fonts are not here");
            return;
        }

        var (ffmpeg, dejavu) = tools;
        var folder = Directory.CreateTempSubdirectory("universum-ocr-").FullName;
        try
        {
            // the templates of other fonts (as Windows has Arial, Verdana, Tahoma …): DejaVu Sans upright, bold and oblique, a serif
            var glyphs = new List<RenderedGlyph>();
            glyphs.AddRange(await DrawAlphabetAsync(ffmpeg, dejavu, folder));
            glyphs.AddRange(await DrawAlphabetAsync(ffmpeg, fonts + "dejavu/DejaVuSans-Bold.ttf", folder));
            glyphs.AddRange(await DrawAlphabetAsync(ffmpeg, fonts + "dejavu/DejaVuSans-Oblique.ttf", folder, italic: true));
            glyphs.AddRange(await DrawAlphabetAsync(ffmpeg, fonts + "dejavu/DejaVuSerif.ttf", folder));
            glyphs.AddRange(await DrawAlphabetAsync(ffmpeg, dejavu, folder, size: 22));
            glyphs.AddRange(await DrawAlphabetAsync(ffmpeg, fonts + "dejavu/DejaVuSans-Oblique.ttf", folder, italic: true, size: 22));
            var templates = OcrTemplates.From(glyphs);

            // the subtitles in Liberation Sans (Arial's proportions), a font not among the templates
            string[] lines = ["Vymyslený titulok č. 12, ďalší!", "Žltý kôň úpel ďábelské ódy?", "Ľudia ťahajú vymysleného kapra."];
            var pictures = new List<OcrPicture>();
            for (var i = 0; i < lines.Length; i++)
            {
                pictures.Add(new OcrPicture(TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1), await DrawSubtitleAsync(ffmpeg, fonts + "liberation/LiberationSans-Regular.ttf", folder, lines[i], 40)));
            }

            var run = new SubtitleOcr(templates, null, "sk").Read(pictures);
            foreach (var unknown in run.Unknown)
            {
                run.Teach(unknown, "?");
            }

            var read = run.Document().Cues.Select(c => c.Text).ToList();
            Assert.True(read.SequenceEqual(lines), $"another font read back: {string.Join(" | ", read)}");

            // a shape no font of the templates has (a black square) is asked, not taken for a letter
            var square = new SubtitleOcr(templates, null, "sk").Read(
                [new OcrPicture(TimeSpan.Zero, TimeSpan.FromSeconds(1), await DrawSubtitleAsync(ffmpeg, fonts + "dejavu/DejaVuSans.ttf", folder, "■ Ľudia ■", 44))]);
            var squareRead = string.Join(" | ", square.Document().Cues.Select(c => c.Text));
            Assert.True(square.Unknown.Count == 1 && squareRead == "� Ľudia �", "the square is asked – " + squareRead);

            // italic and small (as DVD subtitles), the same font
            var small = new List<OcrPicture>();
            for (var i = 0; i < lines.Length; i++)
            {
                small.Add(new OcrPicture(TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1),
                    await DrawSubtitleAsync(ffmpeg, fonts + (i == 2 ? "liberation/LiberationSans-Italic.ttf" : "liberation/LiberationSans-Regular.ttf"), folder, lines[i], 24)));
            }

            var second = new SubtitleOcr(templates, null, "sk").Read(small);

            // small italic letters are the hardest (a caron or a dot may land on the next letter): most of the text
            var smallRead = second.Document().Cues.Select(c => c.Text).ToList();

            // checked by the language: the words of the user's own subtitles (an invented text) mend what the reading doubted
            var lexicon = new OcrLexicon();
            lexicon.Learn(InventedText);
            second.Check(lexicon);
            var checkedRead = second.Document().Cues.Select(c => c.Text).ToList();
            Assert.True(checkedRead.Select(t => t.Replace("<i>", "").Replace("</i>", "")).SequenceEqual(lines),
                $"checked by the language – {string.Join(" | ", checkedRead)} ({string.Join("; ", second.Corrections.Select(c => $"{c.Before} → {c.After} ({c.Why})"))})");
            var all = lines.Sum(l => l.Length);
            var wrong = lines.Zip(smallRead).Sum(p => Edits(p.First, p.Second.Replace("<i>", "").Replace("</i>", "")));
            Assert.True(wrong <= all / 10 && smallRead[0] == lines[0] && smallRead[2].StartsWith("<i>", StringComparison.Ordinal),
                $"small letters ({wrong} of {all} wrong), italic marked – {string.Join(" | ", smallRead)}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // an invented Slovak text, as the user's own subtitles would teach the lexicon
    private const string InventedText =
        "Vymyslený titulok číslo jeden ukáže, čo vieme. Ďalší titulok je tiež vymyslený. Ľudia ťahajú vymysleného kapra z rybníka " +
        "a žltý kôň sa na nich pozerá. Kôň úpel, keď počul ďábelské ódy. Vymysleného kapra potom ľudia pustili späť do vody. " +
        "Ďalší deň ťahajú siete znova. Žltý titulok sa nikomu nepáčil, ale ľudia ho čítali. Ódy sa spievajú pri ohni, kapor pláva a kôň spí.";

    // lines of a subtitle drawn as a Blu-ray draws them, the baselines <paramref name="spacing"/> × size apart
    private static async Task<InkBitmap> DrawLinesAsync(string ffmpeg, string font, string folder, string[] lines, int size, double spacing)
    {
        var filters = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var file = Path.Combine(folder, $"r{i}.txt");
            await File.WriteAllTextAsync(file, lines[i]);
            var baseline = 70 + (int)Math.Round(i * spacing * size);
            filters.Add($"drawtext=fontfile={font}:textfile={file}:fontsize={size}:fontcolor=white:borderw=3:bordercolor=black:x=20:y={baseline}-ascent");
        }

        const int width = 1100, height = 200;
        var bgra = await DrawAsync(ffmpeg, folder, width, height, string.Join(",", filters), "bgra");
        return InkBitmap.FromBgra(bgra, width, height, width * 4);
    }

    // how many letters must change to make one text the other (Levenshtein)
    private static int Edits(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }

        return row[b.Length];
    }

    // FFmpeg with drawtext (FreeType) and the DejaVu Sans font, when this computer has them
    private static (string Ffmpeg, string Font)? Tools()
    {
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);
        var font = new[] { "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/dejavu/DejaVuSans.ttf", @"C:\Windows\Fonts\DejaVuSans.ttf" }.FirstOrDefault(File.Exists);
        return ffmpeg is null || font is null ? null : (ffmpeg, font);
    }

    // every letter in its cell, the baseline of each row known: y = baseline − ascent
    private static async Task<List<RenderedGlyph>> DrawAlphabetAsync(string ffmpeg, string font, string folder, bool italic = false, int size = Size)
    {
        var characters = OcrTemplates.Characters.Select(c => c.ToString()).ToList();
        const int columns = 20;
        int cellWidth = size * 2, cellHeight = size * 2;
        var rows = (characters.Count + columns - 1) / columns;
        var filters = new List<string>();
        for (var i = 0; i < characters.Count; i++)
        {
            var file = Path.Combine(folder, $"z{i}.txt");
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            await File.WriteAllTextAsync(file, characters[i]);
            var x = (i % columns * cellWidth) + (size / 2);
            var baseline = (i / columns * cellHeight) + (size * 3 / 2);
            filters.Add($"drawtext=fontfile={font}:textfile={file}:fontsize={size}:fontcolor=white:x={x}:y={baseline}-ascent");
        }

        var gray = await DrawAsync(ffmpeg, folder, columns * cellWidth, rows * cellHeight, string.Join(",", filters), "gray");
        var width = columns * cellWidth;

        // the height of capitals: the top of "H" above the baseline
        var h = characters.IndexOf("H");
        var hInk = Cell(gray, width, h, columns, cellWidth, cellHeight);
        var cap = size * 3 / 2 - (float)hInk.Bounds()!.Value.Y0;
        return characters.Select((c, i) => new RenderedGlyph(c, italic, Cell(gray, width, i, columns, cellWidth, cellHeight), size * 3 / 2, cap)).ToList();
    }

    private static InkBitmap Cell(byte[] gray, int width, int index, int columns, int cellWidth, int cellHeight)
    {
        var cell = new byte[cellWidth * cellHeight];
        var x0 = index % columns * cellWidth;
        var y0 = index / columns * cellHeight;
        for (var y = 0; y < cellHeight; y++)
        {
            Array.Copy(gray, ((y0 + y) * width) + x0, cell, y * cellWidth, cellWidth);
        }

        return InkBitmap.FromGray(cell, cellWidth, cellHeight);
    }

    // a subtitle as a Blu-ray draws it: white letters with a black outline (here on black)
    private static async Task<InkBitmap> DrawSubtitleAsync(string ffmpeg, string font, string folder, string text, int size)
    {
        var file = Path.Combine(folder, $"t{size}.txt");
        await File.WriteAllTextAsync(file, text);
        const int width = 1100, height = 120;
        var bgra = await DrawAsync(ffmpeg, folder, width, height,
            $"drawtext=fontfile={font}:textfile={file}:fontsize={size}:fontcolor=white:borderw=3:bordercolor=black:x=20:y=80-ascent", "bgra");
        return InkBitmap.FromBgra(bgra, width, height, width * 4);
    }

    private static async Task<byte[]> DrawAsync(string ffmpeg, string folder, int width, int height, string filter, string format)
    {
        var output = Path.Combine(folder, $"obraz-{Guid.NewGuid():N}.raw");
        var made = await new ProcessRunner().RunAsync(new ProcessRequest(ffmpeg,
        [
            "-nostdin", "-v", "error", "-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"color=c=black:s={width}x{height}"),
            "-vf", filter, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", format, output,
        ]));
        Assert.True(made.ExitCode == 0, made.StandardError);
        return await File.ReadAllBytesAsync(output);
    }
}
