using System.Numerics;

namespace Universum.Core.Subtitles.Ocr;

/// <summary>
/// The shape of a letter as UNIVERSUM compares it. Its body (the largest piece of ink) stretched to 16 × 16 cells (256
/// bits) – compared exactly and with a tolerance of one cell (another font's thicker or shifted stroke); its mark (an
/// accent, a dot, the dots of "ü") on its own 8 × 8 cells with its place over the body, so that ô, õ, ö and ó differ;
/// its proportions and where it lies on the line: <see cref="Top"/> and <see cref="Bottom"/> above the baseline in heights
/// of capitals (an "o" reaches 0,7, an "O" 1; a "," goes below 0, a "'" stays near 1); <see cref="Pixels"/>: its height –
/// the cells of a mark a few pixels high say little, its place says more.
/// </summary>
public readonly record struct GlyphShape(
    ulong Body0, ulong Body1, ulong Body2, ulong Body3,
    ulong Near0, ulong Near1, ulong Near2, ulong Near3,
    ulong Mark, ulong MarkNear, int Marks, float MarkX, float MarkY,
    float Aspect, float Top, float Bottom, float Pixels)
{
    public const int Grid = 16;

    /// <summary>How different two shapes are: 0 the same; about 0,1–0,2 the same letter of another font; 0,3 and more another letter.</summary>
    public static float Distance(in GlyphShape a, in GlyphShape b)
    {
        var exact = BitOperations.PopCount(a.Body0 ^ b.Body0) + BitOperations.PopCount(a.Body1 ^ b.Body1) +
                    BitOperations.PopCount(a.Body2 ^ b.Body2) + BitOperations.PopCount(a.Body3 ^ b.Body3);

        // ink of one not even next to ink of the other: the stroke is elsewhere, not just thicker
        var away = BitOperations.PopCount(a.Body0 & ~b.Near0) + BitOperations.PopCount(a.Body1 & ~b.Near1) +
                   BitOperations.PopCount(a.Body2 & ~b.Near2) + BitOperations.PopCount(a.Body3 & ~b.Near3) +
                   BitOperations.PopCount(b.Body0 & ~a.Near0) + BitOperations.PopCount(b.Body1 & ~a.Near1) +
                   BitOperations.PopCount(b.Body2 & ~a.Near2) + BitOperations.PopCount(b.Body3 & ~a.Near3);
        var ink = Math.Max(1, Count(a) + Count(b));
        var trust = MathF.Min(1, MathF.Max(0.35f, MathF.Min(a.Pixels, b.Pixels) / 16));
        var body = trust * ((0.25f * exact / 256f) + (0.9f * away / ink));
        var proportions = MathF.Min(1, MathF.Abs(MathF.Log(a.Aspect / b.Aspect)));
        var place = MathF.Abs(a.Top - b.Top) + MathF.Abs(a.Bottom - b.Bottom);
        return body + (0.35f * proportions) + (0.8f * place) + MarkDistance(a, b);
    }

    /// <summary>The height of capitals of its line in pixels (the scale it was drawn at); 0 when not known.</summary>
    public float CapPixels() => Top - Bottom > 0.01f ? Pixels / (Top - Bottom) : 0;

    private static int Count(in GlyphShape s) =>
        BitOperations.PopCount(s.Body0) + BitOperations.PopCount(s.Body1) + BitOperations.PopCount(s.Body2) + BitOperations.PopCount(s.Body3);

    private static float MarkDistance(in GlyphShape a, in GlyphShape b)
    {
        if (a.Marks == 0 && b.Marks == 0)
        {
            return 0;
        }

        if (a.Marks == 0 || b.Marks == 0)
        {
            return 0.45f;
        }

        var away = BitOperations.PopCount(a.Mark & ~b.MarkNear) + BitOperations.PopCount(b.Mark & ~a.MarkNear);
        var ink = Math.Max(1, BitOperations.PopCount(a.Mark) + BitOperations.PopCount(b.Mark));
        var shape = (0.25f * BitOperations.PopCount(a.Mark ^ b.Mark) / 64f) + (0.6f * away / ink);
        return shape + (0.6f * (MathF.Abs(a.MarkX - b.MarkX) + MathF.Abs(a.MarkY - b.MarkY))) + (a.Marks == b.Marks ? 0 : 0.15f);
    }

    /// <summary>
    /// The shape of a letter: <paramref name="glyph"/> is its ink alone in its box; <paramref name="baseline"/> the baseline
    /// of its line from the top of the box, <paramref name="capHeight"/> the height of capitals – in pixels.
    /// </summary>
    public static GlyphShape Of(InkBitmap glyph, float baseline, float capHeight)
    {
        var (labels, sizes) = Pieces(glyph);
        var body = 1;
        for (var i = 2; i < sizes.Count; i++)
        {
            if (sizes[i] > sizes[body])
            {
                body = i;
            }
        }

        var (bx0, by0, bx1, by1) = Box(glyph, labels, l => l == body);
        Span<ulong> bodyBits = stackalloc ulong[4];
        Cells(glyph, labels, l => l == body, bx0, by0, bx1, by1, Grid, bodyBits);
        Span<ulong> near = stackalloc ulong[4];
        Dilate(bodyBits, near, Grid);

        var marks = sizes.Count - 2;
        ulong mark = 0, markNear = 0;
        float markX = 0, markY = 0;
        var cap = MathF.Max(1, capHeight);
        if (marks > 0)
        {
            var (mx0, my0, mx1, my1) = Box(glyph, labels, l => l != 0 && l != body);
            Span<ulong> markBits = stackalloc ulong[1];
            Cells(glyph, labels, l => l != 0 && l != body, mx0, my0, mx1, my1, 8, markBits);
            Span<ulong> markDilated = stackalloc ulong[1];
            Dilate(markBits, markDilated, 8);
            mark = markBits[0];
            markNear = markDilated[0];
            markX = (((mx0 + mx1) / 2f) - ((bx0 + bx1) / 2f)) / MathF.Max(1, bx1 - bx0);
            markY = (baseline - ((my0 + my1) / 2f)) / cap;
        }

        return new GlyphShape(bodyBits[0], bodyBits[1], bodyBits[2], bodyBits[3], near[0], near[1], near[2], near[3], mark, markNear, Math.Min(marks, 3), markX, markY,
            glyph.Width / (float)Math.Max(1, glyph.Height), baseline / cap, (baseline - glyph.Height) / cap, glyph.Height);
    }

    // the pieces of ink of a small picture (8-neighbourhood): a label per pixel (0 none, 1…) and each piece's size (index 0 unused)
    private static (int[] Labels, List<int> Sizes) Pieces(InkBitmap ink)
    {
        var labels = new int[ink.Width * ink.Height];
        var sizes = new List<int> { 0 };
        var stack = new Stack<int>();
        for (var start = 0; start < labels.Length; start++)
        {
            if (ink.Ink[start] == 0 || labels[start] != 0)
            {
                continue;
            }

            var label = sizes.Count;
            sizes.Add(0);
            labels[start] = label;
            stack.Push(start);
            while (stack.TryPop(out var at))
            {
                sizes[label]++;
                int x = at % ink.Width, y = at / ink.Width;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= ink.Width || ny >= ink.Height)
                        {
                            continue;
                        }

                        var next = (ny * ink.Width) + nx;
                        if (ink.Ink[next] != 0 && labels[next] == 0)
                        {
                            labels[next] = label;
                            stack.Push(next);
                        }
                    }
                }
            }
        }

        if (sizes.Count == 1)
        {
            sizes.Add(0);
        }

        return (labels, sizes);
    }

    private static (int X0, int Y0, int X1, int Y1) Box(InkBitmap ink, int[] labels, Func<int, bool> chosen)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
        for (var y = 0; y < ink.Height; y++)
        {
            for (var x = 0; x < ink.Width; x++)
            {
                if (chosen(labels[(y * ink.Width) + x]))
                {
                    x0 = Math.Min(x0, x);
                    y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x + 1);
                    y1 = Math.Max(y1, y + 1);
                }
            }
        }

        return x0 == int.MaxValue ? (0, 0, Math.Max(1, ink.Width), Math.Max(1, ink.Height)) : (x0, y0, x1, y1);
    }

    // the ink of the chosen pieces in a box stretched to grid × grid cells; each pixel spreads over the cells it covers
    // (area sampling: small letters are not lost, large ones not aliased); a cell at least half full is ink
    private static void Cells(InkBitmap ink, int[] labels, Func<int, bool> chosen, int x0, int y0, int x1, int y1, int grid, Span<ulong> bits)
    {
        Span<float> cells = stackalloc float[grid * grid];
        Span<float> area = stackalloc float[grid * grid];
        var cellWidth = (x1 - x0) / (float)grid;
        var cellHeight = (y1 - y0) / (float)grid;
        for (var y = y0; y < y1; y++)
        {
            var cy0 = (y - y0) / cellHeight;
            var cy1 = (y + 1 - y0) / cellHeight;
            for (var x = x0; x < x1; x++)
            {
                var value = chosen(labels[(y * ink.Width) + x]) ? 1f : 0f;
                var cx0 = (x - x0) / cellWidth;
                var cx1 = (x + 1 - x0) / cellWidth;
                for (var gy = (int)cy0; gy < grid && gy < cy1; gy++)
                {
                    var overlapY = MathF.Min(cy1, gy + 1) - MathF.Max(cy0, gy);
                    for (var gx = (int)cx0; gx < grid && gx < cx1; gx++)
                    {
                        var overlap = overlapY * (MathF.Min(cx1, gx + 1) - MathF.Max(cx0, gx));
                        cells[(gy * grid) + gx] += value * overlap;
                        area[(gy * grid) + gx] += overlap;
                    }
                }
            }
        }

        bits.Clear();
        for (var i = 0; i < grid * grid; i++)
        {
            if (area[i] > 0 && cells[i] / area[i] >= 0.5f)
            {
                bits[i >> 6] |= 1UL << (i & 63);
            }
        }
    }

    // the cells with ink and their eight neighbours
    private static void Dilate(ReadOnlySpan<ulong> bits, Span<ulong> dilated, int grid)
    {
        dilated.Clear();
        for (var i = 0; i < grid * grid; i++)
        {
            if ((bits[i >> 6] & (1UL << (i & 63))) == 0)
            {
                continue;
            }

            int x = i % grid, y = i / grid;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < grid && ny < grid)
                    {
                        var j = (ny * grid) + nx;
                        dilated[j >> 6] |= 1UL << (j & 63);
                    }
                }
            }
        }
    }
}
