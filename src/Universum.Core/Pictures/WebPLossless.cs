using System.Numerics;

namespace Universum.Core.Pictures;

/// <summary>
/// The bits of a WebP lossless stream: read from the lowest bit of each byte up, kept 64 at a time. Reading past the end
/// gives zeros and is noticed (<see cref="Overrun"/>) – a picture cut short is an error, not grey pixels.
/// </summary>
internal sealed class LosslessBits(byte[] data, int start, int length)
{
    private readonly int _end = start + length;
    private int _next = start;
    private ulong _bits;
    private int _count;
    private int _padding;

    /// <summary>More bits were read than the stream has.</summary>
    public bool Overrun => _padding * 8 > _count;

    public uint Read(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (_count < count)
        {
            Fill();
        }

        var value = (uint)(_bits & ((1UL << count) - 1));
        _bits >>= count;
        _count -= count;
        return value;
    }

    public bool Flag() => Read(1) == 1;

    /// <summary>The next symbol of a prefix code.</summary>
    public int Symbol(PrefixCode code)
    {
        if (_count < 32)
        {
            Fill();
        }

        var table = code.Table;
        var entry = table[(int)_bits & code.RootMask];
        if ((entry & PrefixCode.Link) != 0)
        {
            _bits >>= code.RootBits;
            _count -= code.RootBits;
            entry = table[(entry >> 8) + ((int)_bits & ((1 << (entry & 0x0F)) - 1))];
        }

        var length = entry & 0x0F;
        _bits >>= length;
        _count -= length;
        return entry >> 8;
    }

    private void Fill()
    {
        while (_count <= 56)
        {
            ulong value = 0;
            if (_next < _end)
            {
                value = data[_next++];
            }
            else
            {
                _padding++;
            }

            _bits |= value << _count;
            _count += 8;
        }
    }
}

/// <summary>
/// A canonical prefix code of WebP lossless as a lookup table: the next bits of the stream (lowest first) index a root
/// table of up to 8 bits; a longer code continues in a table of its own for the rest of its bits. Entries: symbol &lt;&lt; 8 |
/// length, or (start of the second table) &lt;&lt; 8 | <see cref="Link"/> | its bits.
/// </summary>
internal sealed class PrefixCode
{
    public const int Link = 0x80;
    private const int MaxLength = 15;
    private const int MaxRootBits = 8;

    private PrefixCode(int[] table, int rootBits)
    {
        Table = table;
        RootBits = rootBits;
        RootMask = (1 << rootBits) - 1;
    }

    public int[] Table { get; }

    public int RootBits { get; }

    public int RootMask { get; }

    /// <summary>The number of table entries (memory) of the code.</summary>
    public int Size => Table.Length;

    /// <summary>
    /// The code of the lengths (0 = the symbol is not used): exactly one symbol used, or a complete code (no bit sequence
    /// left over, none taken twice) – anything else is a broken stream (null). <paramref name="build"/> false only checks it.
    /// </summary>
    public static PrefixCode? From(ReadOnlySpan<int> lengths, bool build = true)
    {
        Span<int> counts = stackalloc int[MaxLength + 1];
        var used = 0;
        var only = 0;
        var longest = 0;
        for (var symbol = 0; symbol < lengths.Length; symbol++)
        {
            var length = lengths[symbol];
            if (length == 0)
            {
                continue;
            }

            if ((uint)length > MaxLength)
            {
                return null;
            }

            counts[length]++;
            used++;
            only = symbol;
            longest = Math.Max(longest, length);
        }

        if (used == 0)
        {
            return null;
        }

        if (used == 1)
        {
            // one symbol costs no bits at all
            return new PrefixCode([only << 8], 0);
        }

        var left = 1;
        for (var length = 1; length <= MaxLength; length++)
        {
            left = (left << 1) - counts[length];
            if (left < 0)
            {
                return null;
            }
        }

        if (left != 0)
        {
            return null;
        }

        if (!build)
        {
            return Checked;
        }

        // the first code of each length (canonical: shorter codes first, symbols in their order)
        Span<int> next = stackalloc int[MaxLength + 1];
        var code = 0;
        for (var length = 1; length <= MaxLength; length++)
        {
            next[length] = code;
            code = (code + counts[length]) << 1;
        }

        var rootBits = Math.Min(MaxRootBits, longest);
        var rootSize = 1 << rootBits;
        var reversed = new int[lengths.Length];
        int[]? deepest = longest > rootBits ? new int[rootSize] : null;
        for (var symbol = 0; symbol < lengths.Length; symbol++)
        {
            var length = lengths[symbol];
            if (length == 0)
            {
                continue;
            }

            reversed[symbol] = Reverse(next[length]++, length);
            if (length > rootBits)
            {
                ref var depth = ref deepest![reversed[symbol] & (rootSize - 1)];
                depth = Math.Max(depth, length);
            }
        }

        var size = rootSize;
        if (deepest is not null)
        {
            foreach (var depth in deepest)
            {
                size += depth > 0 ? 1 << (depth - rootBits) : 0;
            }
        }

        var table = new int[size];
        var free = rootSize;
        if (deepest is not null)
        {
            for (var prefix = 0; prefix < rootSize; prefix++)
            {
                if (deepest[prefix] > 0)
                {
                    var bits = deepest[prefix] - rootBits;
                    table[prefix] = (free << 8) | Link | bits;
                    free += 1 << bits;
                }
            }
        }

        for (var symbol = 0; symbol < lengths.Length; symbol++)
        {
            var length = lengths[symbol];
            if (length == 0)
            {
                continue;
            }

            var bitsOfCode = reversed[symbol];
            if (length <= rootBits)
            {
                for (var i = bitsOfCode; i < rootSize; i += 1 << length)
                {
                    table[i] = (symbol << 8) | length;
                }
            }
            else
            {
                var link = table[bitsOfCode & (rootSize - 1)];
                var start = link >> 8;
                var bits = link & 0x0F;
                var rest = length - rootBits;
                for (var i = bitsOfCode >> rootBits; i < 1 << bits; i += 1 << rest)
                {
                    table[start + i] = (symbol << 8) | rest;
                }
            }
        }

        return new PrefixCode(table, rootBits);
    }

    /// <summary>The stand-in for a code that was only checked (never read with).</summary>
    private static readonly PrefixCode Checked = new([0], 0);

    private static int Reverse(int code, int length)
    {
        var result = 0;
        for (var i = 0; i < length; i++)
        {
            result = (result << 1) | (code & 1);
            code >>= 1;
        }

        return result;
    }
}

/// <summary>
/// The WebP lossless format (VP8L) – UNIVERSUM's own decoder, written from the format's specification: the transforms
/// (prediction, colour decorrelation, green subtracted, palette with packed pixels), the prefix codes chosen block by block,
/// the backward copies and the colour cache. Gives ARGB pixels (0xAARRGGBB).
/// </summary>
internal sealed class WebPLossless
{
    private const int Literals = 256;
    private const int LengthCodes = 24;
    private const int DistanceCodes = 40;
    private const int MaxCacheBits = 11;

    /// <summary>The memory of all prefix codes of a picture at most (table entries) – a broken stream cannot claim gigabytes.</summary>
    private const long MaxTableEntries = 1L << 24;

    private static readonly int[] CodeLengthOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private readonly LosslessBits _bits;
    private long _tableEntries;

    private WebPLossless(byte[] data, int start, int length) => _bits = new LosslessBits(data, start, length);

    /// <summary>The 'VP8L' chunk: its header (signature, size, version) and the picture.</summary>
    public static (uint[] Argb, int Width, int Height, bool Alpha) DecodeChunk(byte[] data, int start, int length)
    {
        if (length < 5 || data[start] != 0x2F)
        {
            throw Pixels.Broken();
        }

        var decoder = new WebPLossless(data, start + 1, length - 1);
        var width = (int)decoder._bits.Read(14) + 1;
        var height = (int)decoder._bits.Read(14) + 1;
        var alpha = decoder._bits.Flag();
        if (decoder._bits.Read(3) != 0)
        {
            throw Pixels.Broken();
        }

        Pixels.CheckSize(width, height);
        return (decoder.Picture(width, height), width, height, alpha);
    }

    /// <summary>A stream without the header, of a known size (the alpha of a lossy picture).</summary>
    public static uint[] DecodeStream(byte[] data, int start, int length, int width, int height)
    {
        Pixels.CheckSize(width, height);
        return new WebPLossless(data, start, length).Picture(width, height);
    }

    private enum Kind
    {
        Predictor = 0,
        Color = 1,
        SubtractGreen = 2,
        ColorIndexing = 3,
    }

    private sealed record Transform(Kind Kind, int Width, int Bits, uint[] Data, int DataWidth);

    // the main picture: its transforms, then the pixels, then the transforms undone (the last one first)
    private uint[] Picture(int width, int height)
    {
        var transforms = new List<Transform>(4);
        var codedWidth = width;
        var seen = 0;
        while (_bits.Flag())
        {
            var kind = (Kind)_bits.Read(2);
            if ((seen & (1 << (int)kind)) != 0)
            {
                throw Pixels.Broken();
            }

            seen |= 1 << (int)kind;
            switch (kind)
            {
                case Kind.Predictor:
                case Kind.Color:
                {
                    var bits = (int)_bits.Read(3) + 2;
                    var blocksAcross = Blocks(codedWidth, bits);
                    var data = Image(blocksAcross, Blocks(height, bits), null);
                    transforms.Add(new Transform(kind, codedWidth, bits, data, blocksAcross));
                    break;
                }

                case Kind.SubtractGreen:
                    transforms.Add(new Transform(kind, codedWidth, 0, [], 0));
                    break;
                default:
                {
                    var colors = (int)_bits.Read(8) + 1;
                    var palette = Image(colors, 1, null);
                    for (var i = 1; i < colors; i++)
                    {
                        palette[i] = AddPixels(palette[i], palette[i - 1]);
                    }

                    var bits = colors <= 2 ? 3 : colors <= 4 ? 2 : colors <= 16 ? 1 : 0;
                    transforms.Add(new Transform(kind, codedWidth, bits, palette, colors));
                    codedWidth = Blocks(codedWidth, bits);
                    break;
                }
            }
        }

        var pixels = new uint[(long)width * height];
        Image(codedWidth, height, pixels);
        for (var i = transforms.Count - 1; i >= 0; i--)
        {
            var transform = transforms[i];
            switch (transform.Kind)
            {
                case Kind.Predictor:
                    Predict(pixels, transform, height);
                    break;
                case Kind.Color:
                    Decorrelate(pixels, transform, height);
                    break;
                case Kind.SubtractGreen:
                    AddGreen(pixels.AsSpan(0, transform.Width * height));
                    break;
                default:
                    Unpalette(pixels, transform, height);
                    break;
            }
        }

        return pixels;
    }

    private static int Blocks(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    // one picture of the stream (the main one into "into", a picture of a transform or of the code groups as a new array)
    private uint[] Image(int width, int height, uint[]? into)
    {
        var main = into is not null;
        var cacheBits = 0;
        if (_bits.Flag())
        {
            cacheBits = (int)_bits.Read(4);
            if (cacheBits is < 1 or > MaxCacheBits)
            {
                throw Pixels.Broken();
            }
        }

        // the code groups block by block (only the main picture has them)
        uint[]? groupsOfBlocks = null;
        var groupBits = 0;
        var groupsAcross = 0;
        var groupCount = 1;
        if (main && _bits.Flag())
        {
            groupBits = (int)_bits.Read(3) + 2;
            groupsAcross = Blocks(width, groupBits);
            groupsOfBlocks = Image(groupsAcross, Blocks(height, groupBits), null);
            for (var i = 0; i < groupsOfBlocks.Length; i++)
            {
                var group = (groupsOfBlocks[i] >> 8) & 0xFFFF;
                groupsOfBlocks[i] = group;
                groupCount = Math.Max(groupCount, (int)group + 1);
            }

            var used = new bool[groupCount];
            foreach (var group in groupsOfBlocks)
            {
                used[group] = true;
            }

            return ReadPixels(width, height, into!, cacheBits, ReadGroups(groupCount, cacheBits, used), groupsOfBlocks, groupBits, groupsAcross);
        }

        var groups = ReadGroups(1, cacheBits, null);
        return ReadPixels(width, height, into ?? new uint[width * height], cacheBits, groups, null, 0, 0);
    }

    // five codes a group: green with lengths and cache, red, blue, alpha, distance; a group no block uses is only checked
    private PrefixCode[][] ReadGroups(int count, int cacheBits, bool[]? used)
    {
        var greenSize = Literals + LengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0);
        var groups = new PrefixCode[count][];
        var lengths = new int[greenSize];
        for (var group = 0; group < count; group++)
        {
            var build = used is null || used[group];
            var codes = new PrefixCode[5];
            for (var i = 0; i < 5; i++)
            {
                var size = i == 0 ? greenSize : i == 4 ? DistanceCodes : Literals;
                codes[i] = ReadCode(lengths.AsSpan(0, size), build);
                if (build)
                {
                    _tableEntries += codes[i].Size;
                    if (_tableEntries > MaxTableEntries)
                    {
                        throw Pixels.Broken();
                    }
                }
            }

            groups[group] = codes;
        }

        if (_bits.Overrun)
        {
            throw Pixels.Broken();
        }

        return groups;
    }

    private PrefixCode ReadCode(Span<int> lengths, bool build)
    {
        lengths.Clear();
        if (_bits.Flag())
        {
            // one or two symbols of length 1
            var two = _bits.Flag();
            // a symbol outside the alphabet is not used (the code then has the other one, or none – broken)
            var first = (int)_bits.Read(_bits.Flag() ? 8 : 1);
            if (first < lengths.Length)
            {
                lengths[first] = 1;
            }

            if (two && (int)_bits.Read(8) is var second && second < lengths.Length)
            {
                lengths[second] = 1;
            }
        }
        else
        {
            Span<int> lengthCodeLengths = stackalloc int[CodeLengthOrder.Length];
            var given = (int)_bits.Read(4) + 4;
            for (var i = 0; i < given; i++)
            {
                lengthCodeLengths[CodeLengthOrder[i]] = (int)_bits.Read(3);
            }

            var lengthCode = PrefixCode.From(lengthCodeLengths) ?? throw Pixels.Broken();
            var codesToRead = lengths.Length;
            if (_bits.Flag())
            {
                var bits = 2 + (2 * (int)_bits.Read(3));
                codesToRead = 2 + (int)_bits.Read(bits);
                if (codesToRead > lengths.Length)
                {
                    throw Pixels.Broken();
                }
            }

            var symbol = 0;
            var previous = 8;
            while (symbol < lengths.Length && codesToRead-- > 0)
            {
                var length = _bits.Symbol(lengthCode);
                if (length < 16)
                {
                    lengths[symbol++] = length;
                    if (length != 0)
                    {
                        previous = length;
                    }

                    continue;
                }

                var repeat = length switch
                {
                    16 => 3 + (int)_bits.Read(2),
                    17 => 3 + (int)_bits.Read(3),
                    _ => 11 + (int)_bits.Read(7),
                };
                if (symbol + repeat > lengths.Length)
                {
                    throw Pixels.Broken();
                }

                lengths.Slice(symbol, repeat).Fill(length == 16 ? previous : 0);
                symbol += repeat;
            }
        }

        return PrefixCode.From(lengths, build) ?? throw Pixels.Broken();
    }

    // the pixels: literals (green, red, blue, alpha), backward copies, colours from the cache
    private uint[] ReadPixels(int width, int height, uint[] into, int cacheBits, PrefixCode[][] groups, uint[]? groupsOfBlocks, int groupBits, int groupsAcross)
    {
        var total = width * height;
        var cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        var cacheShift = 32 - cacheBits;
        var cached = 0;
        var bits = _bits;
        var codes = groups[0];
        var mask = (1 << groupBits) - 1;
        int x = 0, y = 0, position = 0;
        while (position < total)
        {
            if (groupsOfBlocks is not null && (x & mask) == 0)
            {
                codes = groups[groupsOfBlocks[((y >> groupBits) * groupsAcross) + (x >> groupBits)]];
            }

            var green = bits.Symbol(codes[0]);
            if (green < Literals)
            {
                var red = bits.Symbol(codes[1]);
                var blue = bits.Symbol(codes[2]);
                var alpha = bits.Symbol(codes[3]);
                into[position++] = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
                if (++x == width)
                {
                    x = 0;
                    y++;
                    if (bits.Overrun)
                    {
                        throw Pixels.Broken();
                    }
                }
            }
            else if (green < Literals + LengthCodes)
            {
                var length = Value(green - Literals);
                var distance = Distance(Value(bits.Symbol(codes[4])), width);
                if (distance > position || length > total - position)
                {
                    throw Pixels.Broken();
                }

                var span = into.AsSpan();
                if (distance >= length)
                {
                    span.Slice(position - distance, length).CopyTo(span.Slice(position, length));
                }
                else if (distance == 1)
                {
                    span.Slice(position, length).Fill(into[position - 1]);
                }
                else
                {
                    for (var i = 0; i < length; i++)
                    {
                        into[position + i] = into[position + i - distance];
                    }
                }

                position += length;
                x += length;
                if (x >= width)
                {
                    y += x / width;
                    x %= width;
                }

                if (bits.Overrun)
                {
                    throw Pixels.Broken();
                }

                // the group may change within the copy
                if (groupsOfBlocks is not null && position < total)
                {
                    codes = groups[groupsOfBlocks[((y >> groupBits) * groupsAcross) + (x >> groupBits)]];
                }
            }
            else
            {
                var index = green - Literals - LengthCodes;
                if (cache is null || index >= cache.Length)
                {
                    throw Pixels.Broken();
                }

                // the cache learns the pixels since the last look into it (lazily – only now is it needed)
                for (; cached < position; cached++)
                {
                    var known = into[cached];
                    cache[(0x1E35A7BDu * known) >> cacheShift] = known;
                }

                into[position++] = cache[index];
                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }

        }

        if (bits.Overrun)
        {
            throw Pixels.Broken();
        }

        return into;

        int Value(int prefix)
        {
            if (prefix < 4)
            {
                return prefix + 1;
            }

            var extra = (prefix - 2) >> 1;
            return ((2 + (prefix & 1)) << extra) + (int)bits.Read(extra) + 1;
        }
    }

    // a distance code: 1–120 the neighbours nearby, larger ones a distance in the order of the pixels (less 120)
    private static int Distance(int code, int width)
    {
        if (code > 120)
        {
            return code - 120;
        }

        var neighbours = WebPTables.Neighbours;
        var distance = neighbours[(code - 1) * 2] + (neighbours[((code - 1) * 2) + 1] * width);
        return Math.Max(1, distance);
    }

    // the prediction undone: each pixel was stored as its difference from a prediction by its neighbours
    private static void Predict(uint[] pixels, Transform transform, int height)
    {
        var width = transform.Width;
        var modes = transform.Data;
        var bits = transform.Bits;
        pixels[0] = AddPixels(pixels[0], 0xFF000000);
        for (var x = 1; x < width; x++)
        {
            pixels[x] = AddPixels(pixels[x], pixels[x - 1]);
        }

        for (var y = 1; y < height; y++)
        {
            var row = y * width;
            pixels[row] = AddPixels(pixels[row], pixels[row - width]);
            var modeRow = (y >> bits) * transform.DataWidth;
            for (var x = 1; x < width; x++)
            {
                var i = row + x;
                var mode = (modes[modeRow + (x >> bits)] >> 8) & 0x0F;
                var left = pixels[i - 1];
                var top = pixels[i - width];
                var prediction = mode switch
                {
                    1 => left,
                    2 => top,
                    3 => pixels[i - width + 1],
                    4 => pixels[i - width - 1],
                    5 => Average(Average(left, pixels[i - width + 1]), top),
                    6 => Average(left, pixels[i - width - 1]),
                    7 => Average(left, top),
                    8 => Average(pixels[i - width - 1], top),
                    9 => Average(top, pixels[i - width + 1]),
                    10 => Average(Average(left, pixels[i - width - 1]), Average(top, pixels[i - width + 1])),
                    11 => Select(left, top, pixels[i - width - 1]),
                    12 => ClampedGradient(left, top, pixels[i - width - 1]),
                    13 => ClampedHalfGradient(Average(left, top), pixels[i - width - 1]),
                    _ => 0xFF000000,
                };
                pixels[i] = AddPixels(pixels[i], prediction);
            }
        }
    }

    // the colour decorrelation undone: red from green, blue from green and red
    private static void Decorrelate(uint[] pixels, Transform transform, int height)
    {
        var width = transform.Width;
        var bits = transform.Bits;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var blockRow = (y >> bits) * transform.DataWidth;
            for (var x = 0; x < width; x++)
            {
                var element = transform.Data[blockRow + (x >> bits)];
                var greenToRed = (sbyte)(element & 0xFF);
                var greenToBlue = (sbyte)((element >> 8) & 0xFF);
                var redToBlue = (sbyte)((element >> 16) & 0xFF);
                var argb = pixels[row + x];
                var green = (sbyte)((argb >> 8) & 0xFF);
                var red = (int)((argb >> 16) & 0xFF);
                var blue = (int)(argb & 0xFF);
                red = (red + ((greenToRed * green) >> 5)) & 0xFF;
                blue = (blue + ((greenToBlue * green) >> 5) + ((redToBlue * (sbyte)red) >> 5)) & 0xFF;
                pixels[row + x] = (argb & 0xFF00FF00) | ((uint)red << 16) | (uint)blue;
            }
        }
    }

    private static void AddGreen(Span<uint> pixels)
    {
        for (var i = 0; i < pixels.Length; i++)
        {
            var argb = pixels[i];
            var green = (argb >> 8) & 0xFF;
            var redBlue = (argb & 0x00FF00FF) + ((green << 16) | green);
            pixels[i] = (argb & 0xFF00FF00) | (redBlue & 0x00FF00FF);
        }
    }

    // the palette undone (and the pixels packed several into one unpacked): from the end, so the packed ones are read first
    private static void Unpalette(uint[] pixels, Transform transform, int height)
    {
        var width = transform.Width;
        var bits = transform.Bits;
        var palette = transform.Data;
        var packedWidth = Blocks(width, bits);
        var perPixel = 8 >> bits;
        var valueMask = (1 << perPixel) - 1;
        var inPixel = (1 << bits) - 1;
        for (var y = height - 1; y >= 0; y--)
        {
            var packedRow = y * packedWidth;
            var row = y * width;
            for (var x = width - 1; x >= 0; x--)
            {
                var green = (int)((pixels[packedRow + (x >> bits)] >> 8) & 0xFF);
                var index = (green >> ((x & inPixel) * perPixel)) & valueMask;
                pixels[row + x] = index < palette.Length ? palette[index] : 0;
            }
        }
    }

    /// <summary>Each of the four bytes added on its own (modulo 256).</summary>
    private static uint AddPixels(uint a, uint b) =>
        (((a & 0xFF00FF00) + (b & 0xFF00FF00)) & 0xFF00FF00) | (((a & 0x00FF00FF) + (b & 0x00FF00FF)) & 0x00FF00FF);

    /// <summary>The average of each byte, rounded down.</summary>
    private static uint Average(uint a, uint b) => (((a ^ b) & 0xFEFEFEFE) >> 1) + (a & b);

    private static uint Select(uint left, uint top, uint topLeft)
    {
        // the neighbour nearer to the gradient's guess left + top - topLeft
        var toLeft = Distance(top, topLeft);
        var toTop = Distance(left, topLeft);
        return toLeft < toTop ? left : top;
    }

    private static int Distance(uint a, uint b) =>
        Math.Abs((int)(a >> 24) - (int)(b >> 24)) + Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)) +
        Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) + Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));

    private static uint ClampedGradient(uint left, uint top, uint topLeft)
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var value = (int)((left >> shift) & 0xFF) + (int)((top >> shift) & 0xFF) - (int)((topLeft >> shift) & 0xFF);
            result |= (uint)Math.Clamp(value, 0, 255) << shift;
        }

        return result;
    }

    private static uint ClampedHalfGradient(uint average, uint topLeft)
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var a = (int)((average >> shift) & 0xFF);
            var value = a + ((a - (int)((topLeft >> shift) & 0xFF)) / 2);
            result |= (uint)Math.Clamp(value, 0, 255) << shift;
        }

        return result;
    }
}
