using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Universum.Core.Subtitles.Ocr;

namespace Universum.App.Pages;

/// <summary>
/// The letters UNIVERSUM's reading of picture subtitles compares with: drawn from the fonts of Windows subtitles are made
/// with (Arial, Verdana, Tahoma …) – upright, bold and italic, large and small – once a run of the program, on a thread
/// of their own (WPF draws on an STA thread); a font Windows does not have is left out.
/// </summary>
public static class OcrFonts
{
    private static readonly string[] Families = ["Arial", "Verdana", "Tahoma", "Segoe UI", "Calibri", "Times New Roman", "Trebuchet MS", "Georgia"];
    private static readonly int[] Sizes = [48, 24];
    private static readonly Lock Gate = new();
    private static Task<OcrTemplates>? _templates;

    /// <summary>The templates (drawn the first time they are needed; a failed drawing is tried again next time).</summary>
    public static Task<OcrTemplates> TemplatesAsync()
    {
        lock (Gate)
        {
            if (_templates is null || _templates.IsFaulted)
            {
                _templates = Draw();
            }

            return _templates;
        }
    }

    private static Task<OcrTemplates> Draw()
    {
        var done = new TaskCompletionSource<OcrTemplates>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(OcrTemplates.From(Glyphs()));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "UNIVERSUM – písma pre OCR",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static IEnumerable<RenderedGlyph> Glyphs()
    {
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        (FontWeight Weight, FontStyle Style, bool Italic)[] faces =
            [(FontWeights.Normal, FontStyles.Normal, false), (FontWeights.Bold, FontStyles.Normal, false), (FontWeights.Normal, FontStyles.Italic, true)];
        foreach (var family in Families.Where(installed.Contains))
        {
            foreach (var (weight, style, italic) in faces)
            {
                var typeface = new Typeface(new FontFamily(family), style, weight, FontStretches.Normal);
                foreach (var size in Sizes)
                {
                    foreach (var glyph in Sheet(typeface, size, italic))
                    {
                        yield return glyph;
                    }
                }
            }
        }
    }

    // every letter in its cell of 2 × size pixels, its baseline 1,5 × size from the cell's top; the height of capitals is
    // the top of "H" above the baseline (as the reading measures it on subtitles)
    private static List<RenderedGlyph> Sheet(Typeface typeface, int size, bool italic)
    {
        var characters = OcrTemplates.Characters.Select(c => c.ToString()).ToList();
        const int columns = 20;
        var cell = size * 2;
        var baseline = size * 3 / 2;
        var width = columns * cell;
        var height = (characters.Count + columns - 1) / columns * cell;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            for (var i = 0; i < characters.Count; i++)
            {
                var text = new FormattedText(characters[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.White, 1);
                drawing.DrawText(text, new Point((i % columns * cell) + (size / 2), (i / columns * cell) + baseline - text.Baseline));
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bgra = new byte[width * height * 4];
        bitmap.CopyPixels(bgra, width * 4, 0);

        InkBitmap Cell(int index)
        {
            var gray = new byte[cell * cell];
            int x0 = index % columns * cell, y0 = index / columns * cell;
            for (var y = 0; y < cell; y++)
            {
                for (var x = 0; x < cell; x++)
                {
                    var at = (((y0 + y) * width) + x0 + x) * 4;
                    gray[(y * cell) + x] = Math.Max(bgra[at], Math.Max(bgra[at + 1], bgra[at + 2]));
                }
            }

            return InkBitmap.FromGray(gray, cell, cell);
        }

        var inks = characters.Select((_, i) => Cell(i)).ToList();
        var cap = inks[characters.IndexOf("H")].Bounds() is { } h ? baseline - h.Y0 : size * 0.7f;
        return characters.Select((c, i) => new RenderedGlyph(c, italic, inks[i], baseline, cap)).ToList();
    }
}
