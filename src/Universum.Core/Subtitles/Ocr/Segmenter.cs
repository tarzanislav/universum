namespace Universum.Core.Subtitles.Ocr;

/// <summary>A letter found in a picture: its box and the parts (connected pieces of ink) it is made of.</summary>
public sealed class FoundGlyph(int x0, int y0, int x1, int y1, int[] parts)
{
    public int X0 { get; private set; } = x0;

    public int Y0 { get; private set; } = y0;

    public int X1 { get; private set; } = x1;

    public int Y1 { get; private set; } = y1;

    public int Width => X1 - X0;

    public int Height => Y1 - Y0;

    /// <summary>The labels of its pieces of ink (a letter with a dot or an accent has more).</summary>
    public int[] Parts { get; private set; } = parts;

    /// <summary>The top and bottom of its body – its largest piece, without a dot or an accent.</summary>
    public int BodyY0 { get; } = y0;

    public int BodyY1 { get; } = y1;

    internal void Absorb(FoundGlyph other)
    {
        X0 = Math.Min(X0, other.X0);
        Y0 = Math.Min(Y0, other.Y0);
        X1 = Math.Max(X1, other.X1);
        Y1 = Math.Max(Y1, other.Y1);
        Parts = [.. Parts, .. other.Parts];
    }
}

/// <summary>A line of text of a picture: its letters from the left, its baseline and the height of its capitals (pixels).</summary>
public sealed record FoundLine(IReadOnlyList<FoundGlyph> Glyphs, float Baseline, float CapHeight);

/// <summary>A picture split into lines and letters, with the labels of its pieces of ink.</summary>
public sealed record Segmentation(InkBitmap Ink, int[] Labels, IReadOnlyList<FoundLine> Lines)
{
    /// <summary>The pixel belongs to the letter (one of its pieces).</summary>
    public bool Belongs(FoundGlyph glyph, int x, int y)
    {
        var label = Labels[(y * Ink.Width) + x];
        return label != 0 && Array.IndexOf(glyph.Parts, label) >= 0;
    }

    /// <summary>The ink of one letter alone (its pieces), as a picture of its box.</summary>
    public InkBitmap Cut(FoundGlyph glyph)
    {
        var ink = new byte[glyph.Width * glyph.Height];
        for (var y = 0; y < glyph.Height; y++)
        {
            for (var x = 0; x < glyph.Width; x++)
            {
                if (Belongs(glyph, glyph.X0 + x, glyph.Y0 + y))
                {
                    ink[(y * glyph.Width) + x] = 1;
                }
            }
        }

        return new InkBitmap(glyph.Width, glyph.Height, ink);
    }
}

/// <summary>
/// Splits the ink of a subtitle picture into lines and letters: connected pieces of ink (8-neighbourhood, one pass with
/// union–find), lines by the rows with ink (an accent above a capital belongs to its line), letters from the pieces – a
/// dot, an accent, the dots of ":" or "ä" stacked over or under another piece belong to it.
/// </summary>
public static class Segmenter
{
    public static Segmentation Split(InkBitmap ink)
    {
        var (labels, boxes) = Pieces(ink);
        var lines = new List<FoundLine>();
        foreach (var (top, bottom) in Bands(ink))
        {
            var glyphs = boxes.Where(b => b.Value.Count > 1 && Center(b.Value) >= top && Center(b.Value) < bottom)
                .Select(b => new FoundGlyph(b.Value.X0, b.Value.Y0, b.Value.X1, b.Value.Y1, [b.Key]))
                .ToList();
            if (glyphs.Count == 0)
            {
                continue;
            }

            Stack(glyphs);
            glyphs.Sort((a, b) => a.X0.CompareTo(b.X0));
            var (baseline, cap) = Metrics(glyphs);
            lines.Add(new FoundLine(glyphs, baseline, cap));
        }

        return new Segmentation(ink, labels, lines);
    }

    private static float Center(Box box) => (box.Y0 + box.Y1) / 2f;

    // the baseline (where most of the tall letters end) and the height of capitals (the tall letters' tops above it) –
    // measured on the letters' bodies: the accent of "Č" or "Á" is above the height of capitals
    private static (float Baseline, float Cap) Metrics(List<FoundGlyph> glyphs)
    {
        var tallest = glyphs.Max(g => g.BodyY1 - g.BodyY0);
        var tall = glyphs.Where(g => g.BodyY1 - g.BodyY0 >= tallest * 0.5f).ToList();
        var baseline = tall.GroupBy(g => g.BodyY1).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;

        // letters on the baseline (not descenders): the 90th percentile of their heights above it
        var heights = tall.Where(g => Math.Abs(g.BodyY1 - baseline) <= Math.Max(2, tallest / 10)).Select(g => (float)(baseline - g.BodyY0)).Order().ToList();
        var cap = heights.Count == 0 ? tallest : heights[(int)Math.Min(heights.Count - 1, Math.Floor(heights.Count * 0.9))];
        return (baseline, Math.Max(1, cap));
    }

    // pieces stacked over each other (a dot, an accent, ":" "=" "ä") become one letter: the smaller joins the larger
    private static void Stack(List<FoundGlyph> glyphs)
    {
        var byArea = glyphs.OrderByDescending(g => g.Width * g.Height).ToList();
        foreach (var small in byArea.AsEnumerable().Reverse().ToList())
        {
            if (!glyphs.Contains(small))
            {
                continue;
            }

            FoundGlyph? partner = null;
            var best = 0;
            foreach (var large in glyphs)
            {
                if (ReferenceEquals(large, small) || large.Width * large.Height < small.Width * small.Height)
                {
                    continue;
                }

                var overlap = Math.Min(large.X1, small.X1) - Math.Max(large.X0, small.X0);
                var stacked = small.Y1 <= large.Y0 + (large.Height / 4) || small.Y0 >= large.Y1 - (large.Height / 4);

                // the dot of an italic i or j lies beside its narrow stem rather than over it
                var dot = small.Y1 <= large.Y0 + 1 && large.Y0 - small.Y1 <= large.Height / 2 && large.Width * 2 < large.Height &&
                          small.Width * 2 >= small.Height && overlap > -small.Width;
                if (stacked && (overlap * 2 >= Math.Min(small.Width, large.Width) || dot) && overlap + small.Width > best)
                {
                    partner = large;
                    best = overlap + small.Width;
                }
            }

            if (partner is not null)
            {
                partner.Absorb(small);
                glyphs.Remove(small);
            }
        }
    }

    // the rows with ink in runs; a thin run close to another (accents above capitals) joins it – the one below first; a
    // thin run farther away is a line of its own ("- ...")
    private static List<(int Top, int Bottom)> Bands(InkBitmap ink)
    {
        var runs = new List<(int Top, int Bottom)>();
        var start = -1;
        for (var y = 0; y <= ink.Height; y++)
        {
            var any = false;
            if (y < ink.Height)
            {
                var row = ink.Ink.AsSpan(y * ink.Width, ink.Width);
                any = row.IndexOfAnyExcept((byte)0) >= 0;
            }

            if (any && start < 0)
            {
                start = y;
            }
            else if (!any && start >= 0)
            {
                runs.Add((start, y));
                start = -1;
            }
        }

        if (runs.Count < 2)
        {
            return runs;
        }

        var tallest = runs.Max(r => r.Bottom - r.Top);
        for (var i = 0; i < runs.Count; i++)
        {
            if (runs.Count < 2 || runs[i].Bottom - runs[i].Top >= tallest * 0.4f)
            {
                continue;
            }

            // join the nearer neighbour – the one below first (accents are above their letters)
            var below = i + 1 < runs.Count ? runs[i + 1].Top - runs[i].Bottom : int.MaxValue;
            var above = i > 0 ? runs[i].Top - runs[i - 1].Bottom : int.MaxValue;
            if (Math.Min(below, above) > Math.Max(2, tallest * 0.15f))
            {
                continue;
            }

            if (below <= above && i + 1 < runs.Count)
            {
                runs[i + 1] = (runs[i].Top, runs[i + 1].Bottom);
            }
            else if (i > 0)
            {
                runs[i - 1] = (runs[i - 1].Top, runs[i].Bottom);
            }
            else
            {
                continue;
            }

            runs.RemoveAt(i);
            i--;
        }

        return runs;
    }

    private sealed class Box(int x, int y)
    {
        public int X0 = x, Y0 = y, X1 = x + 1, Y1 = y + 1, Count;
    }

    // connected pieces of ink (8-neighbourhood): labels per pixel and the box of each label
    private static (int[] Labels, Dictionary<int, Box> Boxes) Pieces(InkBitmap ink)
    {
        var width = ink.Width;
        var labels = new int[width * ink.Height];
        var parent = new List<int> { 0 };
        int Find(int label)
        {
            while (parent[label] != label)
            {
                parent[label] = parent[parent[label]];
                label = parent[label];
            }

            return label;
        }

        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b)
            {
                parent[Math.Max(a, b)] = Math.Min(a, b);
            }
        }

        for (var y = 0; y < ink.Height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (ink.Ink[i] == 0)
                {
                    continue;
                }

                var label = 0;
                void Neighbour(int nx, int ny)
                {
                    if (nx < 0 || nx >= width || ny < 0)
                    {
                        return;
                    }

                    var other = labels[(ny * width) + nx];
                    if (other == 0)
                    {
                        return;
                    }

                    if (label == 0)
                    {
                        label = other;
                    }
                    else
                    {
                        Union(label, other);
                    }
                }

                Neighbour(x - 1, y);
                Neighbour(x - 1, y - 1);
                Neighbour(x, y - 1);
                Neighbour(x + 1, y - 1);
                if (label == 0)
                {
                    label = parent.Count;
                    parent.Add(label);
                }

                labels[i] = label;
            }
        }

        var boxes = new Dictionary<int, Box>();
        for (var y = 0; y < ink.Height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (labels[i] == 0)
                {
                    continue;
                }

                var root = Find(labels[i]);
                labels[i] = root;
                if (!boxes.TryGetValue(root, out var box))
                {
                    boxes[root] = box = new Box(x, y);
                }

                box.X0 = Math.Min(box.X0, x);
                box.Y0 = Math.Min(box.Y0, y);
                box.X1 = Math.Max(box.X1, x + 1);
                box.Y1 = Math.Max(box.Y1, y + 1);
                box.Count++;
            }
        }

        return (labels, boxes);
    }
}
