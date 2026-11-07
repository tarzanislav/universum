using System.Buffers.Binary;

namespace Universum.Core.Pictures;

/// <summary>The planes of a decoded lossy picture: luma of the whole macroblocks, chroma of half the size.</summary>
internal sealed record YuvPlanes(int Width, int Height, byte[] Y, byte[] U, byte[] V, int YStride, int UvStride);

/// <summary>
/// WebP pictures (TMDB and the web give them) read by UNIVERSUM's own code: the RIFF container – a simple lossy ('VP8 ') or
/// lossless ('VP8L') picture, or the extended one ('VP8X') with alpha ('ALPH') or an animation (its first frame) – decoded
/// into <see cref="Pixels"/>. No codec of the system and no library is needed.
/// </summary>
public static class WebP
{
    /// <summary>The bytes are a WebP file (by its signature).</summary>
    public static bool Is(ReadOnlySpan<byte> bytes) => PictureFormats.Of(bytes) == PictureKind.WebP;

    /// <summary>The picture; InvalidDataException when it is not a readable WebP.</summary>
    public static Pixels Decode(byte[] file)
    {
        if (file.Length < 20 || !Is(file))
        {
            throw Pixels.Broken();
        }

        // the file may claim more than it has (cut short) or have something after it
        var end = (int)Math.Min(file.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
        var chunks = Chunks(file, 12, end).GetEnumerator();
        if (!chunks.MoveNext())
        {
            throw Pixels.Broken();
        }

        var (name, start, length) = chunks.Current;
        switch (name)
        {
            case "VP8 ":
                return Lossy(file, start, length, null);
            case "VP8L":
                return Lossless(file, start, length);
            case "VP8X":
                return Extended(file, start, length, chunks);
            default:
                throw Pixels.Broken();
        }
    }

    // the chunks: a four-letter name, the size, the content (padded to an even size)
    private static IEnumerable<(string Name, int Start, int Length)> Chunks(byte[] file, int from, int end)
    {
        var at = from;
        while (at + 8 <= end)
        {
            var name = System.Text.Encoding.ASCII.GetString(file, at, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
            var start = at + 8;
            var length = (int)Math.Min(size, (uint)(end - start));
            yield return (name, start, length);
            at = (int)Math.Min(end, start + (long)size + (size & 1));
        }
    }

    private static Pixels Lossless(byte[] file, int start, int length)
    {
        var (argb, width, height, _) = WebPLossless.DecodeChunk(file, start, length);
        return FromArgb(argb, width, height);
    }

    private static Pixels FromArgb(uint[] argb, int width, int height)
    {
        var pixels = new Pixels(width, height, new byte[width * height * 4], hasAlpha: true);
        argb.AsSpan(0, width * height).CopyTo(pixels.AsArgb());
        var opaque = true;
        foreach (var value in argb.AsSpan(0, width * height))
        {
            if (value < 0xFF000000)
            {
                opaque = false;
                break;
            }
        }

        return opaque ? new Pixels(width, height, pixels.Bgra, false) : pixels;
    }

    private static Pixels Lossy(byte[] file, int start, int length, (int Start, int Length)? alpha)
    {
        var planes = WebPLossy.DecodePlanes(file, start, length);
        var bgra = YuvToBgra(planes);
        if (alpha is not { } chunk)
        {
            return new Pixels(planes.Width, planes.Height, bgra, false);
        }

        var values = Alpha(file, chunk.Start, chunk.Length, planes.Width, planes.Height);
        for (var i = 0; i < values.Length; i++)
        {
            bgra[(i * 4) + 3] = values[i];
        }

        return new Pixels(planes.Width, planes.Height, bgra, true);
    }

    // the extended file: its canvas, alpha before the picture, or the first frame of an animation laid on the canvas
    private static Pixels Extended(byte[] file, int start, int length, IEnumerator<(string Name, int Start, int Length)> chunks)
    {
        if (length < 10)
        {
            throw Pixels.Broken();
        }

        var flags = file[start];
        var canvasWidth = 1 + (file[start + 4] | (file[start + 5] << 8) | (file[start + 6] << 16));
        var canvasHeight = 1 + (file[start + 7] | (file[start + 8] << 8) | (file[start + 9] << 16));
        Pixels.CheckSize(canvasWidth, canvasHeight);
        var animated = (flags & 0x02) != 0;
        (int, int)? alpha = null;
        while (chunks.MoveNext())
        {
            var (name, at, size) = chunks.Current;
            switch (name)
            {
                case "ANMF" when animated:
                    return FirstFrame(file, at, size, canvasWidth, canvasHeight);
                case "ALPH":
                    alpha = (at, size);
                    break;
                case "VP8 ":
                    return Lossy(file, at, size, alpha);
                case "VP8L":
                    return Lossless(file, at, size);
            }
        }

        throw Pixels.Broken();
    }

    private static Pixels FirstFrame(byte[] file, int start, int length, int canvasWidth, int canvasHeight)
    {
        if (length < 16)
        {
            throw Pixels.Broken();
        }

        var x = 2 * (file[start] | (file[start + 1] << 8) | (file[start + 2] << 16));
        var y = 2 * (file[start + 3] | (file[start + 4] << 8) | (file[start + 5] << 16));
        Pixels? frame = null;
        (int, int)? alpha = null;
        foreach (var (name, at, size) in Chunks(file, start + 16, start + length))
        {
            if (name == "ALPH")
            {
                alpha = (at, size);
            }
            else if (name == "VP8 ")
            {
                frame = Lossy(file, at, size, alpha);
                break;
            }
            else if (name == "VP8L")
            {
                frame = Lossless(file, at, size);
                break;
            }
        }

        if (frame is null)
        {
            throw Pixels.Broken();
        }

        // the frame on a transparent canvas (the first frame has nothing under it); what lies outside it is left out
        var canvas = new byte[canvasWidth * canvasHeight * 4];
        var rows = Math.Max(0, Math.Min(frame.Height, canvasHeight - y));
        var columns = Math.Max(0, Math.Min(frame.Width, canvasWidth - x));
        for (var row = 0; row < rows && columns > 0; row++)
        {
            frame.Bgra.AsSpan(row * frame.Stride, columns * 4).CopyTo(canvas.AsSpan((((y + row) * canvasWidth) + x) * 4));
        }

        var covers = x == 0 && y == 0 && frame.Width >= canvasWidth && frame.Height >= canvasHeight;
        return new Pixels(canvasWidth, canvasHeight, canvas, frame.HasAlpha || !covers);
    }

    // the alpha of a lossy picture: raw or compressed as a lossless picture (in its green), then the filter undone
    private static byte[] Alpha(byte[] file, int start, int length, int width, int height)
    {
        if (length < 1)
        {
            throw Pixels.Broken();
        }

        var header = file[start];
        var compression = header & 0x03;
        var filter = (header >> 2) & 0x03;
        var values = new byte[width * height];
        if (compression == 0)
        {
            if (length - 1 < values.Length)
            {
                throw Pixels.Broken();
            }

            file.AsSpan(start + 1, values.Length).CopyTo(values);
        }
        else if (compression == 1)
        {
            var argb = WebPLossless.DecodeStream(file, start + 1, length - 1, width, height);
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = (byte)(argb[i] >> 8);
            }
        }
        else
        {
            throw Pixels.Broken();
        }

        Unfilter(values, width, height, filter);
        return values;
    }

    // each value was stored as its difference from a guess: none, the one on the left, the one above, or the gradient
    internal static void Unfilter(byte[] values, int width, int height, int filter)
    {
        if (filter == 0)
        {
            return;
        }

        // the first row from the left (the first value as it is)
        for (var x = 1; x < width; x++)
        {
            values[x] += values[x - 1];
        }

        for (var y = 1; y < height; y++)
        {
            var row = y * width;
            var above = row - width;
            values[row] += values[above];
            for (var x = 1; x < width; x++)
            {
                var guess = filter switch
                {
                    1 => values[row + x - 1],
                    2 => values[above + x],
                    _ => Math.Clamp(values[row + x - 1] + values[above + x] - values[above + x - 1], 0, 255),
                };
                values[row + x] = (byte)(values[row + x] + guess);
            }
        }
    }

    // BT.601 (studio range) to RGB, the chroma interpolated between its samples (they lie in the middle of 2×2 pixels:
    // 9/16 of the nearest, 3/16 of the two beside it, 1/16 of the one across)
    internal static byte[] YuvToBgra(YuvPlanes planes)
    {
        var width = planes.Width;
        var height = planes.Height;
        var chromaWidth = (width + 1) >> 1;
        var chromaHeight = (height + 1) >> 1;
        var bgra = new byte[width * height * 4];
        var u = new int[chromaWidth];
        var v = new int[chromaWidth];
        for (var y = 0; y < height; y++)
        {
            // down: 3/4 of the nearer chroma row, 1/4 of the other one (the edge repeated)
            var near = y >> 1;
            var far = Math.Clamp((y & 1) == 0 ? near - 1 : near + 1, 0, chromaHeight - 1);
            var nearRow = near * planes.UvStride;
            var farRow = far * planes.UvStride;
            for (var x = 0; x < chromaWidth; x++)
            {
                u[x] = (3 * planes.U[nearRow + x]) + planes.U[farRow + x];
                v[x] = (3 * planes.V[nearRow + x]) + planes.V[farRow + x];
            }

            var luma = y * planes.YStride;
            var to = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var c = x >> 1;
                var other = Math.Clamp((x & 1) == 0 ? c - 1 : c + 1, 0, chromaWidth - 1);
                var cb = ((3 * u[c]) + u[other] + 8) >> 4;
                var cr = ((3 * v[c]) + v[other] + 8) >> 4;
                var scaled = LumaScale[planes.Y[luma + x]];
                var o = to + (x * 4);
                bgra[o] = Channel(scaled + BlueFromCb[cb]);
                bgra[o + 1] = Channel(scaled - GreenFromCb[cb] - GreenFromCr[cr]);
                bgra[o + 2] = Channel(scaled + RedFromCr[cr]);
                bgra[o + 3] = 255;
            }
        }

        return bgra;
    }

    // BT.601: R = 1.164 (Y − 16) + 1.596 (Cr − 128) …, in 16 fractional bits (+ half for rounding in the luma)
    private const double YScale = 255.0 / 219.0;
    private const double CScale = 255.0 / 224.0;
    private static readonly int[] LumaScale = Table(i => (YScale * (i - 16)) + 0.5);
    private static readonly int[] RedFromCr = Table(i => 1.402 * CScale * (i - 128));
    private static readonly int[] BlueFromCb = Table(i => 1.772 * CScale * (i - 128));
    private static readonly int[] GreenFromCb = Table(i => 1.772 * 0.114 / 0.587 * CScale * (i - 128));
    private static readonly int[] GreenFromCr = Table(i => 1.402 * 0.299 / 0.587 * CScale * (i - 128));

    private static int[] Table(Func<int, double> value)
    {
        var table = new int[256];
        for (var i = 0; i < 256; i++)
        {
            table[i] = (int)Math.Round(value(i) * 65536);
        }

        return table;
    }

    private static byte Channel(int fixedPoint)
    {
        var value = fixedPoint >> 16;
        return (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
    }
}
