namespace Universum.Core.Subtitles.Ocr;

/// <summary>A letter drawn in a font to compare with: its text, upright or italic, and its shape.</summary>
public readonly record struct OcrTemplate(string Text, bool Italic, GlyphShape Shape);

/// <summary>
/// A letter drawn by a font renderer (the App draws them with the fonts of Windows, the tests with FFmpeg): its ink and
/// where its baseline and the top of its capitals lie (pixels from the top of the picture).
/// </summary>
public sealed record RenderedGlyph(string Text, bool Italic, InkBitmap Ink, float Baseline, float CapHeight);

/// <summary>
/// The letters UNIVERSUM recognises – drawn from fonts subtitles use – and the nearest of them to a shape. The search goes
/// through all of them (a few thousand shapes of 256 bits: microseconds); a film has a few hundred different letters.
/// </summary>
public sealed class OcrTemplates
{
    /// <summary>The letters drawn for each font: Latin with the Czech and Slovak ones, digits and punctuation.</summary>
    public const string Characters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" +
        "áäčďéěíĺľňóôŕřšťúůýžÁÄČĎÉĚÍĹĽŇÓÔŔŘŠŤÚŮÝŽöüßÖÜàèìòùÀÈÌÒÙâêîûÂÊÎÛçÇñÑãõ" +
        ".,:;!?'\"-–()/&%+*=„“”‘’«»…#@$€";

    private readonly OcrTemplate[] _templates;

    public OcrTemplates(IEnumerable<OcrTemplate> templates) => _templates = templates.ToArray();

    public int Count => _templates.Length;

    public IReadOnlyList<OcrTemplate> All => _templates;

    // letters whose pieces stand side by side (a caron beside the letter, double quotation marks): what each piece is
    private static readonly Dictionary<string, string> PiecesOf = new(StringComparer.Ordinal)
    {
        ["ď"] = "d'", ["ť"] = "t'", ["ľ"] = "l'", ["Ľ"] = "L'", ["\""] = "''", ["„"] = ",,", ["“"] = "''", ["”"] = "''", ["…"] = "...",
    };

    /// <summary>
    /// The templates of letters drawn: each one's ink as one letter, and – for a letter whose pieces stand side by side
    /// (ď ť ľ Ľ, quotation marks), as the reading finds them – its pieces too (the caron as an apostrophe, mended back by
    /// the context).
    /// </summary>
    public static OcrTemplates From(IEnumerable<RenderedGlyph> glyphs)
    {
        var templates = new List<OcrTemplate>();
        foreach (var glyph in glyphs)
        {
            if (glyph.Ink.Bounds() is not { } box)
            {
                continue;
            }

            templates.Add(new OcrTemplate(glyph.Text, glyph.Italic, GlyphShape.Of(glyph.Ink.Crop(box.X0, box.Y0, box.X1, box.Y1), glyph.Baseline - box.Y0, glyph.CapHeight)));
            if (PiecesOf.TryGetValue(glyph.Text, out var pieces) && Segmenter.Split(glyph.Ink) is { Lines: [{ Glyphs: var found }] } split && found.Count == pieces.Length)
            {
                for (var i = 0; i < found.Count; i++)
                {
                    templates.Add(new OcrTemplate(pieces[i].ToString(), glyph.Italic, GlyphShape.Of(split.Cut(found[i]), glyph.Baseline - found[i].Y0, glyph.CapHeight)));
                }
            }
        }

        return new OcrTemplates(templates);
    }

    /// <summary>The nearest template and its distance; the nearest of another text and its distance (how sure the first is).</summary>
    public (OcrTemplate Best, float Distance, float Second) Nearest(in GlyphShape shape)
    {
        var (best, distance, second, _) = NearestTwo(shape);
        return (best, distance, second.Distance);
    }

    /// <summary>The nearest template and the nearest one of another text, each with its distance.</summary>
    public (OcrTemplate Best, float Distance, (OcrTemplate Template, float Distance) Second, bool Found) NearestTwo(in GlyphShape shape)
    {
        var best = -1;
        var second = -1;
        var bestDistance = float.MaxValue;
        var secondDistance = float.MaxValue;
        for (var i = 0; i < _templates.Length; i++)
        {
            var distance = GlyphShape.Distance(shape, _templates[i].Shape);
            if (distance < bestDistance)
            {
                if (best >= 0 && _templates[best].Text != _templates[i].Text)
                {
                    (second, secondDistance) = (best, bestDistance);
                }

                (best, bestDistance) = (i, distance);
            }
            else if (distance < secondDistance && _templates[best].Text != _templates[i].Text)
            {
                (second, secondDistance) = (i, distance);
            }
        }

        return best < 0
            ? (default, float.MaxValue, (default, float.MaxValue), false)
            : (_templates[best], bestDistance, (second < 0 ? default : _templates[second], secondDistance), true);
    }
}
