using System.Numerics;
using System.Runtime.CompilerServices;

namespace Universum.Core.Pictures;

/// <summary>
/// The boolean entropy decoder of VP8: each bit read with the probability (1–255 of 256) that it is 0. The bytes are kept
/// in a 64-bit window whose top 8 bits are compared with the split of the range; past the end of the data come zeros.
/// </summary>
internal sealed class BoolBits
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _next;
    private ulong _value;
    private int _bits;
    private uint _range = 255;

    public BoolBits(byte[] data, int start, int length)
    {
        _data = data;
        _next = start;
        _end = start + length;
        Fill();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Bit(int probability)
    {
        var split = 1 + (((_range - 1) * (uint)probability) >> 8);
        var high = (ulong)split << 56;
        bool bit;
        if (_value >= high)
        {
            _range -= split;
            _value -= high;
            bit = true;
        }
        else
        {
            _range = split;
            bit = false;
        }

        // back to a range of 128–255, the window moved by as many bits
        var shift = BitOperations.LeadingZeroCount(_range) - 24;
        _range <<= shift;
        _value <<= shift;
        _bits -= shift;
        if (_bits < 8)
        {
            Fill();
        }

        return bit;
    }

    /// <summary>An unsigned number of <paramref name="count"/> bits, the highest first.</summary>
    public int Number(int count)
    {
        var value = 0;
        while (count-- > 0)
        {
            value = (value << 1) | (Bit(128) ? 1 : 0);
        }

        return value;
    }

    /// <summary>A number of <paramref name="count"/> bits followed by its sign.</summary>
    public int Signed(int count)
    {
        var value = Number(count);
        return Bit(128) ? -value : value;
    }

    public bool Flag() => Bit(128);

    private void Fill()
    {
        while (_bits <= 56)
        {
            ulong value = _next < _end ? _data[_next++] : 0UL;
            _value |= value << (56 - _bits);
            _bits += 8;
        }
    }
}

/// <summary>The modes of a 4×4 block (in the order of the probability tables).</summary>
internal enum SubblockMode
{
    Dc,
    TrueMotion,
    Vertical,
    Horizontal,
    DownLeft,
    DownRight,
    VerticalRight,
    VerticalLeft,
    HorizontalDown,
    HorizontalUp,
}

/// <summary>
/// The lossy WebP format (one VP8 key frame, RFC 6386) – UNIVERSUM's own decoder, written from the format's specification:
/// the frame header, the modes of the macroblocks, the coefficients (boolean coded in up to 8 partitions), the prediction
/// within the frame, the inverse transforms (Walsh–Hadamard and DCT), the loop filter, and the colours of BT.601 with the
/// chroma interpolated between its samples.
/// </summary>
internal sealed class WebPLossy
{
    // the modes of a whole macroblock (and of the chroma)
    private const int ModeDc = 0;
    private const int ModeVertical = 1;
    private const int ModeHorizontal = 2;
    private const int ModeTrueMotion = 3;
    private const int ModeSubblocks = 4;

    private const int TypeYAfterY2 = 0;
    private const int TypeY2 = 1;
    private const int TypeChroma = 2;
    private const int TypeYWithDc = 3;

    private readonly byte[] _data;
    private readonly int _width;
    private readonly int _height;
    private readonly int _across;
    private readonly int _down;
    private readonly byte[] _y;
    private readonly byte[] _u;
    private readonly byte[] _v;
    private readonly int _yStride;
    private readonly int _uvStride;

    // the frame header
    private readonly byte[] _probabilities = WebPTables.CoefficientProbabilities.ToArray();
    private readonly int[] _segmentProbabilities = [255, 255, 255];
    private readonly int[] _segmentQuantizer = new int[4];
    private readonly int[] _segmentFilter = new int[4];
    private readonly int[] _referenceDelta = new int[4];
    private readonly int[] _modeDelta = new int[4];
    private readonly Quantizer[] _quantizers = new Quantizer[4];
    private bool _segments;
    private bool _segmentMap;
    private bool _segmentAbsolute;
    private bool _simpleFilter;
    private int _filterLevel;
    private int _sharpness;
    private bool _filterDeltas;
    private bool _skipping;
    private int _skipProbability;

    // per macroblock: its segment, whether its 4×4 blocks are predicted on their own, whether its inner edges are filtered
    private readonly byte[] _segmentOf;
    private readonly bool[] _subblocksOf;
    private readonly bool[] _innerOf;

    // the contexts of the macroblock row above and of the macroblock on the left
    private readonly byte[] _modesAbove;
    private readonly byte[] _modesLeft = new byte[4];
    private readonly byte[] _nonZeroAbove;
    private readonly byte[] _nonZeroLeft = new byte[9];

    private readonly int[] _coefficients = new int[25 * 16];
    private readonly byte[] _subblockModes = new byte[16];

    private sealed record Quantizer(int YDc, int YAc, int Y2Dc, int Y2Ac, int UvDc, int UvAc);

    private WebPLossy(byte[] data, int width, int height)
    {
        _data = data;
        _width = width;
        _height = height;
        _across = (width + 15) >> 4;
        _down = (height + 15) >> 4;
        _yStride = _across * 16;
        _uvStride = _across * 8;
        _y = new byte[_yStride * _down * 16];
        _u = new byte[_uvStride * _down * 8];
        _v = new byte[_uvStride * _down * 8];
        _segmentOf = new byte[_across * _down];
        _subblocksOf = new bool[_across * _down];
        _innerOf = new bool[_across * _down];
        _modesAbove = new byte[_across * 4];
        _nonZeroAbove = new byte[_across * 9];
    }

    /// <summary>The size a 'VP8 ' chunk gives (its frame header); null when it is not a VP8 key frame.</summary>
    public static (int Width, int Height)? SizeOf(byte[] data, int start, int length)
    {
        if (length < 10 || (data[start] & 1) != 0 || data[start + 3] != 0x9D || data[start + 4] != 0x01 || data[start + 5] != 0x2A)
        {
            return null;
        }

        var width = (data[start + 6] | (data[start + 7] << 8)) & 0x3FFF;
        var height = (data[start + 8] | (data[start + 9] << 8)) & 0x3FFF;
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>The planes Y, U and V (U and V of half the size) of a 'VP8 ' chunk, macroblock-aligned.</summary>
    public static YuvPlanes DecodePlanes(byte[] data, int start, int length)
    {
        var size = SizeOf(data, start, length) ?? throw Pixels.Broken();
        Pixels.CheckSize(size.Width, size.Height);
        var decoder = new WebPLossy(data, size.Width, size.Height);
        decoder.Decode(start, length);
        return new YuvPlanes(size.Width, size.Height, decoder._y, decoder._u, decoder._v, decoder._yStride, decoder._uvStride);
    }

    private void Decode(int start, int length)
    {
        var tag = _data[start] | (_data[start + 1] << 8) | (_data[start + 2] << 16);
        if (((tag >> 1) & 7) > 3)
        {
            throw Pixels.Broken();
        }

        var firstSize = tag >> 5;
        var first = start + 10;
        var end = start + length;
        if (firstSize == 0 || first + firstSize > end)
        {
            throw Pixels.Broken();
        }

        var header = new BoolBits(_data, first, firstSize);
        ReadHeader(header);
        var partitions = Partitions(header.Number(2), first + firstSize, end);
        ReadQuantizers(header);
        header.Flag(); // whether the probabilities are kept for the next frame – there is none
        ReadProbabilityUpdates(header);
        _skipping = header.Flag();
        _skipProbability = _skipping ? header.Number(8) : 0;

        for (var row = 0; row < _down; row++)
        {
            Array.Clear(_modesLeft);
            Array.Clear(_nonZeroLeft);
            var tokens = partitions[row % partitions.Length];
            for (var column = 0; column < _across; column++)
            {
                DecodeMacroblock(header, tokens, column, row);
            }
        }

        // a frame of level 0 is not filtered at all (whatever its segments say)
        if (_filterLevel > 0)
        {
            Filter();
        }
    }

    private void ReadHeader(BoolBits header)
    {
        header.Flag(); // colour space: only one is defined
        header.Flag(); // clamping: pixels are always clamped here
        _segments = header.Flag();
        if (_segments)
        {
            _segmentMap = header.Flag();
            if (header.Flag())
            {
                _segmentAbsolute = header.Flag();
                for (var s = 0; s < 4; s++)
                {
                    _segmentQuantizer[s] = header.Flag() ? header.Signed(7) : 0;
                }

                for (var s = 0; s < 4; s++)
                {
                    _segmentFilter[s] = header.Flag() ? header.Signed(6) : 0;
                }
            }

            if (_segmentMap)
            {
                for (var i = 0; i < 3; i++)
                {
                    _segmentProbabilities[i] = header.Flag() ? header.Number(8) : 255;
                }
            }
        }

        _simpleFilter = header.Flag();
        _filterLevel = header.Number(6);
        _sharpness = header.Number(3);
        _filterDeltas = header.Flag();
        if (_filterDeltas && header.Flag())
        {
            for (var i = 0; i < 4; i++)
            {
                if (header.Flag())
                {
                    _referenceDelta[i] = header.Signed(6);
                }
            }

            for (var i = 0; i < 4; i++)
            {
                if (header.Flag())
                {
                    _modeDelta[i] = header.Signed(6);
                }
            }
        }
    }

    // 1, 2, 4 or 8 partitions of the coefficients: the sizes of all but the last (3 bytes each), then the partitions
    private BoolBits[] Partitions(int log2, int start, int end)
    {
        var count = 1 << log2;
        var sizes = start;
        var next = start + (3 * (count - 1));
        if (next > end)
        {
            throw Pixels.Broken();
        }

        var partitions = new BoolBits[count];
        for (var i = 0; i < count; i++)
        {
            var size = i < count - 1
                ? _data[sizes + (3 * i)] | (_data[sizes + (3 * i) + 1] << 8) | (_data[sizes + (3 * i) + 2] << 16)
                : end - next;
            size = Math.Min(size, end - next);
            partitions[i] = new BoolBits(_data, next, size);
            next += size;
        }

        return partitions;
    }

    private void ReadQuantizers(BoolBits header)
    {
        var baseIndex = header.Number(7);
        var yDc = header.Flag() ? header.Signed(4) : 0;
        var y2Dc = header.Flag() ? header.Signed(4) : 0;
        var y2Ac = header.Flag() ? header.Signed(4) : 0;
        var uvDc = header.Flag() ? header.Signed(4) : 0;
        var uvAc = header.Flag() ? header.Signed(4) : 0;
        for (var s = 0; s < 4; s++)
        {
            var index = !_segments ? baseIndex : _segmentAbsolute ? _segmentQuantizer[s] : baseIndex + _segmentQuantizer[s];
            _quantizers[s] = new Quantizer(
                Dc(index + yDc),
                Ac(index),
                Dc(index + y2Dc) * 2,
                Math.Max(8, Ac(index + y2Ac) * 155 / 100),
                Math.Min(132, Dc(index + uvDc)),
                Ac(index + uvAc));
        }

        static int Dc(int index) => WebPTables.DcSteps[Math.Clamp(index, 0, 127)];

        static int Ac(int index) => WebPTables.AcSteps[Math.Clamp(index, 0, 127)];
    }

    private void ReadProbabilityUpdates(BoolBits header)
    {
        var updates = WebPTables.CoefficientUpdates;
        for (var i = 0; i < _probabilities.Length; i++)
        {
            if (header.Bit(updates[i]))
            {
                _probabilities[i] = (byte)header.Number(8);
            }
        }
    }

    private void DecodeMacroblock(BoolBits header, BoolBits tokens, int column, int row)
    {
        var index = (row * _across) + column;

        // the header of the macroblock (in the first partition)
        var segment = _segmentMap ? (header.Bit(_segmentProbabilities[0]) ? 2 + (header.Bit(_segmentProbabilities[2]) ? 1 : 0) : header.Bit(_segmentProbabilities[1]) ? 1 : 0) : 0;
        var skip = _skipping && header.Bit(_skipProbability);
        int lumaMode;
        if (!header.Bit(145))
        {
            lumaMode = ModeSubblocks;
            var subblockProbabilities = WebPTables.SubblockModeProbabilities;
            for (var y = 0; y < 4; y++)
            {
                var left = _modesLeft[y];
                for (var x = 0; x < 4; x++)
                {
                    var top = _modesAbove[(column * 4) + x];
                    var mode = ReadSubblockMode(header, subblockProbabilities.Slice(((top * 10) + left) * 9, 9));
                    _subblockModes[(y * 4) + x] = mode;
                    _modesAbove[(column * 4) + x] = mode;
                    left = mode;
                }

                _modesLeft[y] = left;
            }
        }
        else
        {
            lumaMode = header.Bit(156) ? (header.Bit(128) ? ModeTrueMotion : ModeHorizontal) : (header.Bit(163) ? ModeVertical : ModeDc);
            var implied = (byte)(lumaMode switch
            {
                ModeVertical => SubblockMode.Vertical,
                ModeHorizontal => SubblockMode.Horizontal,
                ModeTrueMotion => SubblockMode.TrueMotion,
                _ => SubblockMode.Dc,
            });
            _modesAbove.AsSpan(column * 4, 4).Fill(implied);
            _modesLeft.AsSpan().Fill(implied);
        }

        var chromaMode = !header.Bit(142) ? ModeDc : !header.Bit(114) ? ModeVertical : header.Bit(183) ? ModeTrueMotion : ModeHorizontal;
        var subblocks = lumaMode == ModeSubblocks;

        // the coefficients (in the partition of the row)
        Array.Clear(_coefficients);
        var coded = 0u;
        var above = _nonZeroAbove.AsSpan(column * 9, 9);
        if (skip)
        {
            above[..8].Clear();
            _nonZeroLeft.AsSpan(0, 8).Clear();
            if (!subblocks)
            {
                above[8] = 0;
                _nonZeroLeft[8] = 0;
            }
        }
        else
        {
            coded = ReadCoefficients(tokens, _quantizers[segment], subblocks, above);
        }

        _segmentOf[index] = (byte)segment;
        _subblocksOf[index] = subblocks;
        _innerOf[index] = subblocks || coded != 0;

        Reconstruct(column, row, lumaMode, chromaMode, coded);
    }

    private static byte ReadSubblockMode(BoolBits header, ReadOnlySpan<byte> p)
    {
        if (!header.Bit(p[0]))
        {
            return (byte)SubblockMode.Dc;
        }

        if (!header.Bit(p[1]))
        {
            return (byte)SubblockMode.TrueMotion;
        }

        if (!header.Bit(p[2]))
        {
            return (byte)SubblockMode.Vertical;
        }

        if (!header.Bit(p[3]))
        {
            if (!header.Bit(p[4]))
            {
                return (byte)SubblockMode.Horizontal;
            }

            return (byte)(header.Bit(p[5]) ? SubblockMode.VerticalRight : SubblockMode.DownRight);
        }

        if (!header.Bit(p[6]))
        {
            return (byte)SubblockMode.DownLeft;
        }

        if (!header.Bit(p[7]))
        {
            return (byte)SubblockMode.VerticalLeft;
        }

        return (byte)(header.Bit(p[8]) ? SubblockMode.HorizontalUp : SubblockMode.HorizontalDown);
    }

    // the 25 blocks of a macroblock: Y2 (the DCs of the luma) when it is predicted whole, 16 luma, 4 + 4 chroma; the bits of
    // the blocks with coefficients (0–15 luma, 16–23 chroma, 24 Y2). Contexts: 0–3 luma columns/rows, 4–5 U, 6–7 V, 8 Y2.
    private uint ReadCoefficients(BoolBits tokens, Quantizer q, bool subblocks, Span<byte> above)
    {
        var left = _nonZeroLeft.AsSpan();
        var coded = 0u;
        var first = 0;
        var lumaType = TypeYWithDc;
        if (!subblocks)
        {
            var count = Coefficients(tokens, TypeY2, above[8] + left[8], q.Y2Dc, q.Y2Ac, 0, 24 * 16);
            above[8] = left[8] = (byte)(count > 0 ? 1 : 0);
            if (count > 0)
            {
                coded |= 1u << 24;
                InverseWalshHadamard();
            }

            first = 1;
            lumaType = TypeYAfterY2;
        }

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var block = (y * 4) + x;
                var count = Coefficients(tokens, lumaType, above[x] + left[y], q.YDc, q.YAc, first, block * 16);
                var nonZero = count > first ? 1 : 0;
                above[x] = left[y] = (byte)nonZero;
                if (nonZero != 0 || _coefficients[block * 16] != 0)
                {
                    coded |= 1u << block;
                }
            }
        }

        for (var plane = 0; plane < 2; plane++)
        {
            for (var y = 0; y < 2; y++)
            {
                for (var x = 0; x < 2; x++)
                {
                    var block = 16 + (plane * 4) + (y * 2) + x;
                    var a = 4 + (plane * 2) + x;
                    var l = 4 + (plane * 2) + y;
                    var count = Coefficients(tokens, TypeChroma, above[a] + left[l], q.UvDc, q.UvAc, 0, block * 16);
                    above[a] = left[l] = (byte)(count > 0 ? 1 : 0);
                    if (count > 0)
                    {
                        coded |= 1u << block;
                    }
                }
            }
        }

        return coded;
    }

    // the tokens of one block (from position "n"): their values times the quantizer step, in the order of the zigzag; the
    // position after the last token read
    private int Coefficients(BoolBits tokens, int type, int context, int dcStep, int acStep, int n, int at)
    {
        var probabilities = _probabilities;
        var bands = WebPTables.Bands;
        var zigzag = WebPTables.Zigzag;
        var p = Node(type, bands[n], context);
        for (; n < 16; n++)
        {
            if (!tokens.Bit(probabilities[p]))
            {
                return n; // no more coefficients
            }

            while (!tokens.Bit(probabilities[p + 1]))
            {
                // a zero: the next one cannot be the end
                if (++n == 16)
                {
                    return 16;
                }

                p = Node(type, bands[n], 0);
            }

            int value;
            int next;
            if (!tokens.Bit(probabilities[p + 2]))
            {
                value = 1;
                next = 1;
            }
            else
            {
                value = LargeValue(tokens, probabilities, p);
                next = 2;
            }

            if (tokens.Bit(128))
            {
                value = -value;
            }

            _coefficients[at + zigzag[n]] = value * (n > 0 ? acStep : dcStep);
            p = Node(type, bands[n + 1], next);
        }

        return 16;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Node(int type, int band, int context) => ((((type * 8) + band) * 3) + context) * 11;

    private static int LargeValue(BoolBits tokens, byte[] probabilities, int p)
    {
        if (!tokens.Bit(probabilities[p + 3]))
        {
            return !tokens.Bit(probabilities[p + 4]) ? 2 : 3 + (tokens.Bit(probabilities[p + 5]) ? 1 : 0);
        }

        if (!tokens.Bit(probabilities[p + 6]))
        {
            if (!tokens.Bit(probabilities[p + 7]))
            {
                return 5 + (tokens.Bit(159) ? 1 : 0);
            }

            return 7 + (tokens.Bit(165) ? 2 : 0) + (tokens.Bit(145) ? 1 : 0);
        }

        var high = tokens.Bit(probabilities[p + 8]) ? 1 : 0;
        var low = tokens.Bit(probabilities[p + 9 + high]) ? 1 : 0;
        var category = (2 * high) + low;
        var extra = category switch
        {
            0 => WebPTables.Category3,
            1 => WebPTables.Category4,
            2 => WebPTables.Category5,
            _ => WebPTables.Category6,
        };
        var value = 0;
        for (var i = 0; extra[i] != 0; i++)
        {
            value = (value << 1) | (tokens.Bit(extra[i]) ? 1 : 0);
        }

        return value + 3 + (8 << category);
    }

    // Y2 → the DC of each of the 16 luma blocks
    private void InverseWalshHadamard()
    {
        var c = _coefficients;
        const int at = 24 * 16;
        Span<int> temporary = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            var a1 = c[at + i] + c[at + 12 + i];
            var b1 = c[at + 4 + i] + c[at + 8 + i];
            var c1 = c[at + 4 + i] - c[at + 8 + i];
            var d1 = c[at + i] - c[at + 12 + i];
            temporary[i] = a1 + b1;
            temporary[4 + i] = c1 + d1;
            temporary[8 + i] = a1 - b1;
            temporary[12 + i] = d1 - c1;
        }

        for (var i = 0; i < 4; i++)
        {
            var a1 = temporary[(4 * i) + 0] + temporary[(4 * i) + 3];
            var b1 = temporary[(4 * i) + 1] + temporary[(4 * i) + 2];
            var c1 = temporary[(4 * i) + 1] - temporary[(4 * i) + 2];
            var d1 = temporary[(4 * i) + 0] - temporary[(4 * i) + 3];
            c[((4 * i) + 0) * 16] = (a1 + b1 + 3) >> 3;
            c[((4 * i) + 1) * 16] = (c1 + d1 + 3) >> 3;
            c[((4 * i) + 2) * 16] = (a1 - b1 + 3) >> 3;
            c[((4 * i) + 3) * 16] = (d1 - c1 + 3) >> 3;
        }
    }

    // the residue of a 4×4 block (inverse DCT) added to the prediction already in the plane
    private void AddInverseDct(byte[] plane, int at, int stride, int block)
    {
        var c = _coefficients;
        var from = block * 16;
        Span<int> temporary = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            var a = c[from + i] + c[from + 8 + i];
            var b = c[from + i] - c[from + 8 + i];
            var x = Times35468(c[from + 4 + i]) - Times85627(c[from + 12 + i]);
            var d = Times85627(c[from + 4 + i]) + Times35468(c[from + 12 + i]);
            temporary[i] = a + d;
            temporary[4 + i] = b + x;
            temporary[8 + i] = b - x;
            temporary[12 + i] = a - d;
        }

        for (var i = 0; i < 4; i++)
        {
            var row = temporary.Slice(4 * i, 4);
            var a = row[0] + row[2];
            var b = row[0] - row[2];
            var x = Times35468(row[1]) - Times85627(row[3]);
            var d = Times85627(row[1]) + Times35468(row[3]);
            var o = at + (i * stride);
            plane[o] = Clip(plane[o] + ((a + d + 4) >> 3));
            plane[o + 1] = Clip(plane[o + 1] + ((b + x + 4) >> 3));
            plane[o + 2] = Clip(plane[o + 2] + ((b - x + 4) >> 3));
            plane[o + 3] = Clip(plane[o + 3] + ((a - d + 4) >> 3));
        }

        // sin(π/8)·√2 and cos(π/8)·√2 in 16 fractional bits (the second as 1 + 20091/65536, as the format defines)
        static int Times35468(int value) => (value * 35468) >> 16;

        static int Times85627(int value) => value + ((value * 20091) >> 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    private void Reconstruct(int column, int row, int lumaMode, int chromaMode, uint coded)
    {
        var x0 = column * 16;
        var y0 = row * 16;
        var at = (y0 * _yStride) + x0;
        if (lumaMode == ModeSubblocks)
        {
            Span<byte> edge = stackalloc byte[13];
            for (var block = 0; block < 16; block++)
            {
                var bx = x0 + ((block & 3) * 4);
                var by = y0 + ((block >> 2) * 4);
                SubblockEdge(edge, bx, by, x0, y0, block);
                var position = (by * _yStride) + bx;
                PredictSubblock((SubblockMode)_subblockModes[block], edge, position);
                if ((coded & (1u << block)) != 0)
                {
                    AddInverseDct(_y, position, _yStride, block);
                }
            }
        }
        else
        {
            PredictWhole(_y, _yStride, x0, y0, 16, lumaMode, column, row);
            for (var block = 0; block < 16; block++)
            {
                if ((coded & (1u << block)) != 0)
                {
                    AddInverseDct(_y, at + ((block >> 2) * 4 * _yStride) + ((block & 3) * 4), _yStride, block);
                }
            }
        }

        var cx = column * 8;
        var cy = row * 8;
        PredictWhole(_u, _uvStride, cx, cy, 8, chromaMode, column, row);
        PredictWhole(_v, _uvStride, cx, cy, 8, chromaMode, column, row);
        for (var block = 0; block < 4; block++)
        {
            var position = ((cy + ((block >> 1) * 4)) * _uvStride) + cx + ((block & 1) * 4);
            if ((coded & (1u << (16 + block))) != 0)
            {
                AddInverseDct(_u, position, _uvStride, 16 + block);
            }

            if ((coded & (1u << (20 + block))) != 0)
            {
                AddInverseDct(_v, position, _uvStride, 20 + block);
            }
        }
    }

    // a whole macroblock (16×16 luma, 8×8 chroma) predicted from the row above and the column on the left; outside the
    // frame the row above is 127 and the column on the left 129 (the average leaves out what is outside)
    private static void PredictWhole(byte[] plane, int stride, int x0, int y0, int size, int mode, int column, int row)
    {
        var at = (y0 * stride) + x0;
        switch (mode)
        {
            case ModeDc:
            {
                var sum = 0;
                var parts = 0;
                if (row > 0)
                {
                    for (var i = 0; i < size; i++)
                    {
                        sum += plane[at - stride + i];
                    }

                    parts++;
                }

                if (column > 0)
                {
                    for (var i = 0; i < size; i++)
                    {
                        sum += plane[at + (i * stride) - 1];
                    }

                    parts++;
                }

                var shift = size == 16 ? 3 : 2;
                var value = parts == 0 ? 128 : (sum + (1 << (shift + parts - 1))) >> (shift + parts);
                for (var y = 0; y < size; y++)
                {
                    plane.AsSpan(at + (y * stride), size).Fill((byte)value);
                }

                break;
            }

            case ModeVertical:
                for (var y = 0; y < size; y++)
                {
                    if (row > 0)
                    {
                        plane.AsSpan(at - stride, size).CopyTo(plane.AsSpan(at + (y * stride), size));
                    }
                    else
                    {
                        plane.AsSpan(at + (y * stride), size).Fill(127);
                    }
                }

                break;
            case ModeHorizontal:
                for (var y = 0; y < size; y++)
                {
                    plane.AsSpan(at + (y * stride), size).Fill(column > 0 ? plane[at + (y * stride) - 1] : (byte)129);
                }

                break;
            default:
            {
                // true motion: left + above - above-left
                var corner = row == 0 ? 127 : column == 0 ? 129 : plane[at - stride - 1];
                for (var y = 0; y < size; y++)
                {
                    var left = column > 0 ? plane[at + (y * stride) - 1] : 129;
                    var o = at + (y * stride);
                    for (var x = 0; x < size; x++)
                    {
                        var above = row > 0 ? plane[at - stride + x] : 127;
                        plane[o + x] = Clip(left + above - corner);
                    }
                }

                break;
            }
        }
    }

    // the 13 pixels around a 4×4 block: left column bottom to top (0–3), the corner (4), above (5–8), above right (9–12).
    // Above right of the blocks on the right of the macroblock is always taken from the row above the macroblock.
    private void SubblockEdge(Span<byte> edge, int bx, int by, int x0, int y0, int block)
    {
        var plane = _y;
        var stride = _yStride;
        for (var i = 0; i < 4; i++)
        {
            edge[3 - i] = bx > 0 ? plane[((by + i) * stride) + bx - 1] : (byte)129;
        }

        edge[4] = by == 0 ? (byte)127 : bx == 0 ? (byte)129 : plane[((by - 1) * stride) + bx - 1];
        for (var i = 0; i < 4; i++)
        {
            edge[5 + i] = by > 0 ? plane[((by - 1) * stride) + bx + i] : (byte)127;
        }

        // the blocks of the top row and of the right column take it from the row above the macroblock, the others from the
        // block above right (already decoded)
        AboveRight(edge, (block & 3) == 3 ? y0 - 1 : by - 1, bx + 4, x0);
    }

    private void AboveRight(Span<byte> edge, int y, int x, int x0)
    {
        if (y < 0)
        {
            edge.Slice(9, 4).Fill(127);
            return;
        }

        var row = y * _yStride;
        if (x + 3 < _yStride)
        {
            _y.AsSpan(row + x, 4).CopyTo(edge.Slice(9, 4));
        }
        else
        {
            // the last macroblock of a row: the last pixel above it repeated
            edge.Slice(9, 4).Fill(_y[row + x0 + 15]);
        }
    }

    private void PredictSubblock(SubblockMode mode, ReadOnlySpan<byte> e, int at)
    {
        var plane = _y;
        var stride = _yStride;
        switch (mode)
        {
            case SubblockMode.Dc:
            {
                var sum = 4;
                for (var i = 0; i < 4; i++)
                {
                    sum += e[i] + e[5 + i];
                }

                var value = (byte)(sum >> 3);
                for (var y = 0; y < 4; y++)
                {
                    plane.AsSpan(at + (y * stride), 4).Fill(value);
                }

                break;
            }

            case SubblockMode.TrueMotion:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        plane[at + (y * stride) + x] = Clip(e[3 - y] + e[5 + x] - e[4]);
                    }
                }

                break;
            case SubblockMode.Vertical:
                for (var x = 0; x < 4; x++)
                {
                    var value = Average3(e[4 + x], e[5 + x], e[6 + x]);
                    for (var y = 0; y < 4; y++)
                    {
                        plane[at + (y * stride) + x] = value;
                    }
                }

                break;
            case SubblockMode.Horizontal:
            {
                // left from the top: e[3], e[2], e[1], e[0]; the corner above the first
                Span<byte> rows = [Average3(e[4], e[3], e[2]), Average3(e[3], e[2], e[1]), Average3(e[2], e[1], e[0]), Average3(e[1], e[0], e[0])];
                for (var y = 0; y < 4; y++)
                {
                    plane.AsSpan(at + (y * stride), 4).Fill(rows[y]);
                }

                break;
            }

            case SubblockMode.DownLeft:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var i = 5 + x + y;
                        plane[at + (y * stride) + x] = Average3(e[i], e[i + 1], e[Math.Min(i + 2, 12)]);
                    }
                }

                break;
            case SubblockMode.DownRight:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var i = 4 - y + x;
                        plane[at + (y * stride) + x] = Average3(e[i - 1], e[i], e[i + 1]);
                    }
                }

                break;
            case SubblockMode.VerticalRight:
                Put(plane, at, stride, 3, 0, Average3(e[1], e[2], e[3]));
                Put(plane, at, stride, 2, 0, Average3(e[2], e[3], e[4]));
                Put(plane, at, stride, 3, 1, Average3(e[3], e[4], e[5]), 1, 0);
                Put(plane, at, stride, 2, 1, Average2(e[4], e[5]), 0, 0);
                Put(plane, at, stride, 3, 2, Average3(e[4], e[5], e[6]), 1, 1);
                Put(plane, at, stride, 2, 2, Average2(e[5], e[6]), 0, 1);
                Put(plane, at, stride, 3, 3, Average3(e[5], e[6], e[7]), 1, 2);
                Put(plane, at, stride, 2, 3, Average2(e[6], e[7]), 0, 2);
                Put(plane, at, stride, 1, 3, Average3(e[6], e[7], e[8]));
                Put(plane, at, stride, 0, 3, Average2(e[7], e[8]));
                break;
            case SubblockMode.VerticalLeft:
                Put(plane, at, stride, 0, 0, Average2(e[5], e[6]));
                Put(plane, at, stride, 1, 0, Average3(e[5], e[6], e[7]));
                Put(plane, at, stride, 2, 0, Average2(e[6], e[7]), 0, 1);
                Put(plane, at, stride, 1, 1, Average3(e[6], e[7], e[8]), 3, 0);
                Put(plane, at, stride, 2, 1, Average2(e[7], e[8]), 0, 2);
                Put(plane, at, stride, 3, 1, Average3(e[7], e[8], e[9]), 1, 2);
                Put(plane, at, stride, 2, 2, Average2(e[8], e[9]), 0, 3);
                Put(plane, at, stride, 3, 2, Average3(e[8], e[9], e[10]), 1, 3);
                Put(plane, at, stride, 2, 3, Average3(e[9], e[10], e[11]));
                Put(plane, at, stride, 3, 3, Average3(e[10], e[11], e[12]));
                break;
            case SubblockMode.HorizontalDown:
                Put(plane, at, stride, 3, 0, Average2(e[0], e[1]));
                Put(plane, at, stride, 3, 1, Average3(e[0], e[1], e[2]));
                Put(plane, at, stride, 2, 0, Average2(e[1], e[2]), 3, 2);
                Put(plane, at, stride, 2, 1, Average3(e[1], e[2], e[3]), 3, 3);
                Put(plane, at, stride, 2, 2, Average2(e[2], e[3]), 1, 0);
                Put(plane, at, stride, 2, 3, Average3(e[2], e[3], e[4]), 1, 1);
                Put(plane, at, stride, 1, 2, Average2(e[3], e[4]), 0, 0);
                Put(plane, at, stride, 1, 3, Average3(e[3], e[4], e[5]), 0, 1);
                Put(plane, at, stride, 0, 2, Average3(e[4], e[5], e[6]));
                Put(plane, at, stride, 0, 3, Average3(e[5], e[6], e[7]));
                break;
            default:
                // horizontal up: from the left column only
                Put(plane, at, stride, 0, 0, Average2(e[3], e[2]));
                Put(plane, at, stride, 0, 1, Average3(e[3], e[2], e[1]));
                Put(plane, at, stride, 0, 2, Average2(e[2], e[1]), 1, 0);
                Put(plane, at, stride, 0, 3, Average3(e[2], e[1], e[0]), 1, 1);
                Put(plane, at, stride, 1, 2, Average2(e[1], e[0]), 2, 0);
                Put(plane, at, stride, 1, 3, Average3(e[1], e[0], e[0]), 2, 1);
                Put(plane, at, stride, 2, 2, e[0], 2, 3);
                Put(plane, at, stride, 3, 0, e[0], 3, 1);
                Put(plane, at, stride, 3, 2, e[0], 3, 3);
                break;
        }
    }

    // a value at (row, column) of the block, and at a second place too when given
    private static void Put(byte[] plane, int at, int stride, int row, int column, byte value, int row2 = -1, int column2 = -1)
    {
        plane[at + (row * stride) + column] = value;
        if (row2 >= 0)
        {
            plane[at + (row2 * stride) + column2] = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Average2(int a, int b) => (byte)((a + b + 1) >> 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Average3(int a, int b, int c) => (byte)((a + (2 * b) + c + 2) >> 2);

    // the loop filter over the whole frame, macroblock after macroblock: the left edge, the inner vertical edges, the top
    // edge, the inner horizontal edges
    private void Filter()
    {
        var strengths = new (int Edge, int Inner, int Interior, int Hev)[8];
        for (var segment = 0; segment < 4; segment++)
        {
            for (var subblocks = 0; subblocks < 2; subblocks++)
            {
                var level = !_segments ? _filterLevel : _segmentAbsolute ? _segmentFilter[segment] : _filterLevel + _segmentFilter[segment];
                if (_filterDeltas)
                {
                    level += _referenceDelta[0];
                    if (subblocks == 1)
                    {
                        level += _modeDelta[0];
                    }
                }

                level = Math.Clamp(level, 0, 63);
                if (level == 0)
                {
                    strengths[(segment * 2) + subblocks] = (0, 0, 0, 0);
                    continue;
                }

                var interior = level;
                if (_sharpness > 0)
                {
                    interior >>= _sharpness > 4 ? 2 : 1;
                    interior = Math.Min(interior, 9 - _sharpness);
                }

                interior = Math.Max(1, interior);
                var hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                strengths[(segment * 2) + subblocks] = (((level + 2) * 2) + interior, (level * 2) + interior, interior, hev);
            }
        }

        for (var row = 0; row < _down; row++)
        {
            for (var column = 0; column < _across; column++)
            {
                var index = (row * _across) + column;
                var (edge, inner, interior, hev) = strengths[(_segmentOf[index] * 2) + (_subblocksOf[index] ? 1 : 0)];
                if (edge == 0)
                {
                    continue;
                }

                var y = (row * 16 * _yStride) + (column * 16);
                var uv = (row * 8 * _uvStride) + (column * 8);
                var inside = _innerOf[index];
                if (_simpleFilter)
                {
                    if (column > 0)
                    {
                        Simple(_y, y, 1, _yStride, 16, edge);
                    }

                    if (inside)
                    {
                        for (var x = 4; x < 16; x += 4)
                        {
                            Simple(_y, y + x, 1, _yStride, 16, inner);
                        }
                    }

                    if (row > 0)
                    {
                        Simple(_y, y, _yStride, 1, 16, edge);
                    }

                    if (inside)
                    {
                        for (var x = 4; x < 16; x += 4)
                        {
                            Simple(_y, y + (x * _yStride), _yStride, 1, 16, inner);
                        }
                    }

                    continue;
                }

                if (column > 0)
                {
                    MacroblockEdge(_y, y, 1, _yStride, 16, edge, interior, hev);
                    MacroblockEdge(_u, uv, 1, _uvStride, 8, edge, interior, hev);
                    MacroblockEdge(_v, uv, 1, _uvStride, 8, edge, interior, hev);
                }

                if (inside)
                {
                    for (var x = 4; x < 16; x += 4)
                    {
                        InnerEdge(_y, y + x, 1, _yStride, 16, inner, interior, hev);
                    }

                    InnerEdge(_u, uv + 4, 1, _uvStride, 8, inner, interior, hev);
                    InnerEdge(_v, uv + 4, 1, _uvStride, 8, inner, interior, hev);
                }

                if (row > 0)
                {
                    MacroblockEdge(_y, y, _yStride, 1, 16, edge, interior, hev);
                    MacroblockEdge(_u, uv, _uvStride, 1, 8, edge, interior, hev);
                    MacroblockEdge(_v, uv, _uvStride, 1, 8, edge, interior, hev);
                }

                if (inside)
                {
                    for (var x = 4; x < 16; x += 4)
                    {
                        InnerEdge(_y, y + (x * _yStride), _yStride, 1, 16, inner, interior, hev);
                    }

                    InnerEdge(_u, uv + (4 * _uvStride), _uvStride, 1, 8, inner, interior, hev);
                    InnerEdge(_v, uv + (4 * _uvStride), _uvStride, 1, 8, inner, interior, hev);
                }
            }
        }
    }

    // signed values of the filter: pixel − 128, clamped to −128…127
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int S(int value) => value < -128 ? -128 : value > 127 ? 127 : value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte U(int value) => (byte)(S(value) + 128);

    // an edge across "step" (1: a vertical edge, the stride: a horizontal one), "count" pixels along it ("along" apart)
    private static void Simple(byte[] p, int at, int step, int along, int count, int edge)
    {
        for (var i = 0; i < count; i++, at += along)
        {
            int p1 = p[at - (2 * step)], p0 = p[at - step], q0 = p[at], q1 = p[at + step];
            if ((Math.Abs(p0 - q0) * 2) + (Math.Abs(p1 - q1) >> 1) > edge)
            {
                continue;
            }

            var a = S(S(p1 - q1) + (3 * (q0 - p0)));
            var b = S(a + 3) >> 3;
            a = S(a + 4) >> 3;
            p[at] = U(q0 - 128 - a);
            p[at - step] = U(p0 - 128 + b);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Filtered(byte[] p, int at, int step, int edge, int interior)
    {
        int p3 = p[at - (4 * step)], p2 = p[at - (3 * step)], p1 = p[at - (2 * step)], p0 = p[at - step];
        int q0 = p[at], q1 = p[at + step], q2 = p[at + (2 * step)], q3 = p[at + (3 * step)];
        return (Math.Abs(p0 - q0) * 2) + (Math.Abs(p1 - q1) >> 1) <= edge &&
               Math.Abs(p3 - p2) <= interior && Math.Abs(p2 - p1) <= interior && Math.Abs(p1 - p0) <= interior &&
               Math.Abs(q3 - q2) <= interior && Math.Abs(q2 - q1) <= interior && Math.Abs(q1 - q0) <= interior;
    }

    private static void MacroblockEdge(byte[] p, int at, int step, int along, int count, int edge, int interior, int hev)
    {
        for (var i = 0; i < count; i++, at += along)
        {
            if (!Filtered(p, at, step, edge, interior))
            {
                continue;
            }

            int p2 = p[at - (3 * step)] - 128, p1 = p[at - (2 * step)] - 128, p0 = p[at - step] - 128;
            int q0 = p[at] - 128, q1 = p[at + step] - 128, q2 = p[at + (2 * step)] - 128;
            if (Math.Abs(p1 - p0) > hev || Math.Abs(q1 - q0) > hev)
            {
                // high edge variance: only the two pixels at the edge
                var a = S(S(p1 - q1) + (3 * (q0 - p0)));
                var b = S(a + 3) >> 3;
                a = S(a + 4) >> 3;
                p[at] = U(q0 - a);
                p[at - step] = U(p0 + b);
                continue;
            }

            var w = S(S(p1 - q1) + (3 * (q0 - p0)));
            var a0 = S(((27 * w) + 63) >> 7);
            var a1 = S(((18 * w) + 63) >> 7);
            var a2 = S(((9 * w) + 63) >> 7);
            p[at] = U(q0 - a0);
            p[at - step] = U(p0 + a0);
            p[at + step] = U(q1 - a1);
            p[at - (2 * step)] = U(p1 + a1);
            p[at + (2 * step)] = U(q2 - a2);
            p[at - (3 * step)] = U(p2 + a2);
        }
    }

    private static void InnerEdge(byte[] p, int at, int step, int along, int count, int edge, int interior, int hev)
    {
        for (var i = 0; i < count; i++, at += along)
        {
            if (!Filtered(p, at, step, edge, interior))
            {
                continue;
            }

            int p1 = p[at - (2 * step)] - 128, p0 = p[at - step] - 128, q0 = p[at] - 128, q1 = p[at + step] - 128;
            var variance = Math.Abs(p1 - p0) > hev || Math.Abs(q1 - q0) > hev;
            var a = S((variance ? S(p1 - q1) : 0) + (3 * (q0 - p0)));
            var b = S(a + 3) >> 3;
            a = S(a + 4) >> 3;
            p[at] = U(q0 - a);
            p[at - step] = U(p0 + b);
            if (!variance)
            {
                a = (a + 1) >> 1;
                p[at + step] = U(q1 - a);
                p[at - (2 * step)] = U(p1 + a);
            }
        }
    }
}
