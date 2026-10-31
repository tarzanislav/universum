namespace Universum.Core.Subtitles.Ocr;

/// <summary>
/// A picture as ink: 1 where a letter is. Subtitles are bright letters with a dark outline on a transparent picture,
/// so ink is the opaque pixels brighter than the threshold that splits the opaque ones best (Otsu) – whatever the colour
/// of the letters (white, yellow) and of the outline.
/// </summary>
public sealed class InkBitmap
{
    public InkBitmap(int width, int height, byte[] ink)
    {
        if (ink.Length != width * height)
        {
            throw new ArgumentException("The ink must have width × height pixels.", nameof(ink));
        }

        Width = width;
        Height = height;
        Ink = ink;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>1 ink, 0 none; row after row.</summary>
    public byte[] Ink { get; }

    public bool this[int x, int y] => Ink[(y * Width) + x] != 0;

    /// <summary>A part of the picture (a box).</summary>
    public InkBitmap Crop(int x0, int y0, int x1, int y1)
    {
        var width = x1 - x0;
        var height = y1 - y0;
        var ink = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(Ink, ((y0 + y) * Width) + x0, ink, y * width, width);
        }

        return new InkBitmap(width, height, ink);
    }

    /// <summary>The box of the ink; null when there is none.</summary>
    public (int X0, int Y0, int X1, int Y1)? Bounds()
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (var y = 0; y < Height; y++)
        {
            var row = Ink.AsSpan(y * Width, Width);
            var first = row.IndexOfAnyExcept((byte)0);
            if (first < 0)
            {
                continue;
            }

            x0 = Math.Min(x0, first);
            x1 = Math.Max(x1, row.LastIndexOfAnyExcept((byte)0) + 1);
            y0 = Math.Min(y0, y);
            y1 = y + 1;
        }

        return x1 < 0 ? null : (x0, y0, x1, y1);
    }

    /// <summary>The ink of a picture in BGRA order (4 bytes a pixel, <paramref name="stride"/> bytes a row).</summary>
    public static InkBitmap FromBgra(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var luminance = new byte[width * height];
        var opaque = new bool[width * height];
        Span<int> histogram = stackalloc int[256];
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                var i = x * 4;
                if (row[i + 3] < 128)
                {
                    continue;
                }

                // the brightest channel: yellow letters are as bright as white ones
                var value = Math.Max(row[i], Math.Max(row[i + 1], row[i + 2]));
                luminance[(y * width) + x] = value;
                opaque[(y * width) + x] = true;
                histogram[value]++;
                count++;
            }
        }

        var threshold = Otsu(histogram, count);
        var ink = new byte[width * height];
        for (var i = 0; i < ink.Length; i++)
        {
            ink[i] = (byte)(opaque[i] && luminance[i] > threshold ? 1 : 0);
        }

        return new InkBitmap(width, height, ink);
    }

    /// <summary>The ink of a grey picture (1 byte a pixel): brighter than the Otsu threshold.</summary>
    public static InkBitmap FromGray(ReadOnlySpan<byte> gray, int width, int height)
    {
        Span<int> histogram = stackalloc int[256];
        foreach (var value in gray[..(width * height)])
        {
            histogram[value]++;
        }

        var threshold = Otsu(histogram, width * height);
        var ink = new byte[width * height];
        for (var i = 0; i < ink.Length; i++)
        {
            ink[i] = (byte)(gray[i] > threshold ? 1 : 0);
        }

        return new InkBitmap(width, height, ink);
    }

    /// <summary>The threshold between two groups of values that keeps each group tightest (Otsu 1979).</summary>
    public static int Otsu(ReadOnlySpan<int> histogram, int count)
    {
        if (count == 0)
        {
            return 127;
        }

        double total = 0;
        for (var i = 0; i < 256; i++)
        {
            total += i * (double)histogram[i];
        }

        double below = 0, weightBelow = 0, best = -1;
        var threshold = 127;
        for (var t = 0; t < 255; t++)
        {
            weightBelow += histogram[t];
            if (weightBelow == 0)
            {
                continue;
            }

            var weightAbove = count - weightBelow;
            if (weightAbove == 0)
            {
                break;
            }

            below += t * (double)histogram[t];
            var meanBelow = below / weightBelow;
            var meanAbove = (total - below) / weightAbove;
            var between = weightBelow * weightAbove * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }

        return threshold;
    }
}
