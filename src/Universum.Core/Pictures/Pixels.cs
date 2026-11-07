using System.Runtime.InteropServices;
using Universum.Core.Text;

namespace Universum.Core.Pictures;

/// <summary>The kinds of picture files UNIVERSUM tells apart – by their first bytes, never by the name.</summary>
public enum PictureKind
{
    Unknown,
    Jpeg,
    Png,
    WebP,
    Gif,
    Bmp,
}

/// <summary>What a picture file really is (its content), and the extension that belongs to it.</summary>
public static class PictureFormats
{
    /// <summary>The kind of the picture by its signature.</summary>
    public static PictureKind Of(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xFF, 0xD8, 0xFF, ..] => PictureKind.Jpeg,
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => PictureKind.Png,
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => PictureKind.WebP,
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => PictureKind.Gif,
        [(byte)'B', (byte)'M', ..] when bytes.Length > 26 => PictureKind.Bmp,
        _ => PictureKind.Unknown,
    };

    /// <summary>The kind of a picture file by its first bytes; Unknown when it cannot be read.</summary>
    public static PictureKind OfFile(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[32];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return Of(head[..read]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PictureKind.Unknown;
        }
    }

    /// <summary>".jpg", ".png", ".webp", ".gif", ".bmp"; null for an unknown kind.</summary>
    public static string? Extension(PictureKind kind) => kind switch
    {
        PictureKind.Jpeg => ".jpg",
        PictureKind.Png => ".png",
        PictureKind.WebP => ".webp",
        PictureKind.Gif => ".gif",
        PictureKind.Bmp => ".bmp",
        _ => null,
    };

    /// <summary>The kind a file name promises (".jpg" and ".jpeg" a JPEG …); Unknown for another extension.</summary>
    public static PictureKind Named(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => PictureKind.Jpeg,
        ".png" => PictureKind.Png,
        ".webp" => PictureKind.WebP,
        ".gif" => PictureKind.Gif,
        ".bmp" => PictureKind.Bmp,
        _ => PictureKind.Unknown,
    };
}

/// <summary>
/// A decoded picture: 4 bytes a pixel in the order blue, green, red, alpha (alpha not premultiplied), rows one after another
/// without gaps. Made by UNIVERSUM's own decoders (WebP) and shrunk or laid on white by its own code.
/// </summary>
public sealed class Pixels
{
    public Pixels(int width, int height, byte[] bgra, bool hasAlpha)
    {
        if (width <= 0 || height <= 0 || bgra.Length != (long)width * height * 4)
        {
            throw new ArgumentException("The pixels do not fit the size.", nameof(bgra));
        }

        Width = width;
        Height = height;
        Bgra = bgra;
        HasAlpha = hasAlpha;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Blue, green, red, alpha of each pixel.</summary>
    public byte[] Bgra { get; }

    /// <summary>Some pixel may be (partly) transparent.</summary>
    public bool HasAlpha { get; }

    /// <summary>Bytes a row.</summary>
    public int Stride => Width * 4;

    /// <summary>The largest picture a decoder makes (6000 × 6000 pixels): a poster or a background has a few million.</summary>
    public const long MaxPixels = 36_000_000;

    /// <summary>The size can be decoded; otherwise the reason (too large).</summary>
    internal static void CheckSize(long width, long height)
    {
        if (width <= 0 || height <= 0)
        {
            throw Broken();
        }

        if (width * height > MaxPixels)
        {
            throw new InvalidDataException(CoreText.F("Obrázok je príliš veľký ({0} × {1} bodov).", width, height));
        }
    }

    /// <summary>The error of a picture that cannot be read.</summary>
    internal static InvalidDataException Broken() => new(CoreText.T("Obrázok WebP sa nedá prečítať – je poškodený alebo neúplný."));

    /// <summary>
    /// The picture at most <paramref name="maxWidth"/> × <paramref name="maxHeight"/> (the proportions kept, never enlarged):
    /// each new pixel the average of the area of the old ones it covers – sharp and without the shimmer of skipped pixels.
    /// Transparent pixels do not darken their neighbours (the colours are weighted by their alpha).
    /// </summary>
    public Pixels Fitted(int maxWidth, int maxHeight)
    {
        var scale = Math.Min(1.0, Math.Min((double)Math.Max(1, maxWidth) / Width, (double)Math.Max(1, maxHeight) / Height));
        if (scale >= 1.0)
        {
            return this;
        }

        var width = Math.Max(1, (int)Math.Round(Width * scale));
        var height = Math.Max(1, (int)Math.Round(Height * scale));
        if (width == Width && height == Height)
        {
            return this;
        }

        // across first (each row of the old picture to the new width), then down; premultiplied by alpha meanwhile
        var columns = Spans(Width, width);
        var rows = Spans(Height, height);
        var across = new float[width * Height * 4];
        var source = Bgra;
        for (var y = 0; y < Height; y++)
        {
            var from = y * Width * 4;
            var to = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                foreach (var (index, weight) in columns[x])
                {
                    var i = from + (index * 4);
                    var alpha = source[i + 3] * weight;
                    b += source[i] * alpha;
                    g += source[i + 1] * alpha;
                    r += source[i + 2] * alpha;
                    a += alpha;
                }

                var o = to + (x * 4);
                across[o] = b;
                across[o + 1] = g;
                across[o + 2] = r;
                across[o + 3] = a;
            }
        }

        var result = new byte[width * height * 4];
        var sums = new float[width * 4];
        for (var y = 0; y < height; y++)
        {
            Array.Clear(sums);
            foreach (var (index, weight) in rows[y])
            {
                var from = index * width * 4;
                for (var i = 0; i < sums.Length; i++)
                {
                    sums[i] += across[from + i] * weight;
                }
            }

            var to = y * width * 4;
            for (var x = 0; x < width * 4; x += 4)
            {
                var alpha = sums[x + 3];
                if (alpha <= 0.001f)
                {
                    continue;
                }

                result[to + x] = Byte(sums[x] / alpha);
                result[to + x + 1] = Byte(sums[x + 1] / alpha);
                result[to + x + 2] = Byte(sums[x + 2] / alpha);
                result[to + x + 3] = Byte(alpha);
            }
        }

        return new Pixels(width, height, result, HasAlpha);
    }

    /// <summary>The picture laid on white (JPEG has no transparency); the same picture when it has none.</summary>
    public Pixels OnWhite()
    {
        if (!HasAlpha)
        {
            return this;
        }

        var result = (byte[])Bgra.Clone();
        for (var i = 0; i < result.Length; i += 4)
        {
            int alpha = result[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            var white = 255 * (255 - alpha);
            result[i] = (byte)(((result[i] * alpha) + white + 127) / 255);
            result[i + 1] = (byte)(((result[i + 1] * alpha) + white + 127) / 255);
            result[i + 2] = (byte)(((result[i + 2] * alpha) + white + 127) / 255);
            result[i + 3] = 255;
        }

        return new Pixels(Width, Height, result, false);
    }

    /// <summary>The pixels as 32-bit values 0xAARRGGBB (the same memory).</summary>
    internal Span<uint> AsArgb() => MemoryMarshal.Cast<byte, uint>(Bgra.AsSpan());

    private static byte Byte(float value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(value + 0.5f);

    // for each new position the old ones it covers and how much of each (the weights of one position add up to 1)
    private static (int Index, float Weight)[][] Spans(int old, int now)
    {
        var step = (double)old / now;
        var spans = new (int, float)[now][];
        for (var i = 0; i < now; i++)
        {
            var start = i * step;
            var end = Math.Min(old, (i + 1) * step);
            var first = (int)Math.Floor(start);
            var last = Math.Min(old - 1, (int)Math.Ceiling(end) - 1);
            var parts = new (int, float)[last - first + 1];
            for (var j = first; j <= last; j++)
            {
                var covered = Math.Min(end, j + 1) - Math.Max(start, j);
                parts[j - first] = (j, (float)(covered / step));
            }

            spans[i] = parts;
        }

        return spans;
    }
}
