using Universum.Core.Media;

namespace Universum.Core.Mp4;

/// <summary>
/// The language of an MP4 track: ISO 639-2/T as three letters of 5 bits each ('mdhd'); a value below 0x400 is a
/// Macintosh language code (old QuickTime files).
/// </summary>
public static class Mp4Languages
{
    // the Macintosh language codes 0–40 (Apple's Script Manager) as ISO 639-2/B
    private static readonly string[] Macintosh =
    [
        "eng", "fre", "ger", "ita", "dut", "swe", "spa", "dan", "por", "nor", "heb", "jpn", "ara", "fin", "gre", "ice", "mlt", "tur", "hrv", "chi", "urd",
        "hin", "tha", "kor", "lit", "pol", "hun", "est", "lav", "smi", "fao", "per", "rus", "chi", "dut", "gle", "alb", "rum", "cze", "slo", "slv",
    ];

    /// <summary>The three letters of a packed language ("und" when there is none or it is not letters).</summary>
    public static string Unpack(ushort packed)
    {
        if (packed < 0x400)
        {
            return packed < Macintosh.Length ? Macintosh[packed] : "und";
        }

        if (packed == 0x7FFF)
        {
            return "und";
        }

        Span<char> letters = stackalloc char[3];
        for (var i = 0; i < 3; i++)
        {
            var value = (packed >> (10 - (5 * i))) & 0x1F;
            if (value is < 1 or > 26)
            {
                return "und";
            }

            letters[i] = (char)(value + 0x60);
        }

        return new string(letters);
    }

    /// <summary>A language packed for 'mdhd' – ISO 639-2/T ("ces", not "cze"); "und" for an unknown one.</summary>
    public static ushort Pack(LanguageTag language)
    {
        var known = Languages.Find(language.Primary);
        var code = language.IsUndetermined ? "und" : known?.Terminology ?? known?.Bibliographic ?? language.Legacy;
        if (code.Length != 3 || !code.All(char.IsAsciiLetterLower))
        {
            code = "und";
        }

        return (ushort)(((code[0] - 0x60) << 10) | ((code[1] - 0x60) << 5) | (code[2] - 0x60));
    }
}

/// <summary>
/// The samples of a track from its sample table: where each lies in the file, how large it is, when it is decoded and
/// shown (in the track's timescale), and whether it is a key frame. Built from 'stts', 'ctts', 'stsc', 'stsz' or 'stz2',
/// 'stco' or 'co64' and 'stss'; every count and position checked.
/// </summary>
public sealed class Mp4SampleTable
{
    /// <summary>The most samples of one track read (a long film has a few hundred thousand).</summary>
    public const int MaxSamples = 50_000_000;

    private Mp4SampleTable(long[] offsets, int[] sizes, long[] decode, int[] composition, bool[]? keys)
    {
        Offsets = offsets;
        Sizes = sizes;
        DecodeTimes = decode;
        CompositionOffsets = composition;
        _keys = keys;
    }

    private readonly bool[]? _keys;

    public int Count => Sizes.Length;

    /// <summary>Where each sample begins in the file.</summary>
    public long[] Offsets { get; }

    public int[] Sizes { get; }

    /// <summary>When each sample is decoded (track timescale, from 0).</summary>
    public long[] DecodeTimes { get; }

    /// <summary>How much later than decoded each sample is shown (may be negative with a version 1 'ctts').</summary>
    public int[] CompositionOffsets { get; }

    /// <summary>The sample is a key frame (without 'stss' every sample is one).</summary>
    public bool IsKey(int sample) => _keys is null || _keys[sample];

    /// <summary>The bytes of all samples.</summary>
    public long TotalBytes => Sizes.Sum(s => (long)s);

    internal static Mp4SampleTable Read(Mp4Box stbl, byte[] movie, long movieStart, int track)
    {
        Mp4Bytes Box(string type, bool required)
        {
            if (stbl.Find(type) is { } box)
            {
                return new Mp4Bytes(Mp4Reader.Content(movie, movieStart, box), box.ContentStart);
            }

            return required ? throw new Mp4FormatException($"track {track} has no '{type}'", stbl.Start) : default;
        }

        // sizes
        int[] sizes;
        if (stbl.Find("stsz") is not null)
        {
            var stsz = Box("stsz", true);
            stsz.Skip(4);
            var constant = stsz.U32();
            var count = Entries(stsz.U32(), constant == 0 ? 4 : 0, stsz, track);
            sizes = new int[count];
            for (var i = 0; i < count; i++)
            {
                sizes[i] = Size(constant == 0 ? stsz.U32() : constant, stsz);
            }
        }
        else
        {
            var stz2 = Box("stz2", true);
            stz2.Skip(7);
            var field = stz2.U8();
            if (field is not (4 or 8 or 16))
            {
                throw new Mp4FormatException($"'stz2' with {field}-bit sizes", stz2.FilePosition);
            }

            var count = Entries(stz2.U32(), field / 8.0, stz2, track);
            sizes = new int[count];
            for (var i = 0; i < count; i++)
            {
                sizes[i] = field switch
                {
                    16 => stz2.U16(),
                    8 => stz2.U8(),
                    _ => 0,
                };
            }

            if (field == 4)
            {
                var start = stz2.Position;
                for (var i = 0; i < count; i++)
                {
                    stz2.Position = start + (i / 2);
                    var pair = stz2.U8();
                    sizes[i] = i % 2 == 0 ? pair >> 4 : pair & 0x0F;
                }
            }
        }

        var samples = sizes.Length;

        // chunks: where each begins, then how many samples each has
        var chunkOffsets = Chunks(stbl.Find("co64") is not null ? Box("co64", true) : Box("stco", true), stbl.Find("co64") is not null, track);
        var offsets = new long[samples];
        var stsc = Box("stsc", true);
        stsc.Skip(4);
        var runs = Entries(stsc.U32(), 12, stsc, track);
        var sample = 0;
        var previousFirst = 0u;
        for (var run = 0; run < runs && sample < samples; run++)
        {
            var first = stsc.U32();
            var perChunk = stsc.U32();
            stsc.Skip(4);
            var next = run + 1 < runs ? PeekFirstChunk(stsc) : (uint)chunkOffsets.Length + 1;
            if (first == 0 || first <= previousFirst || next <= first || next - 1 > chunkOffsets.Length || perChunk == 0)
            {
                throw new Mp4FormatException($"track {track}: the chunk table ('stsc') is out of order", stsc.FilePosition);
            }

            previousFirst = first;
            for (var chunk = first; chunk < next && sample < samples; chunk++)
            {
                var at = chunkOffsets[chunk - 1];
                for (var i = 0; i < perChunk && sample < samples; i++)
                {
                    offsets[sample] = at;
                    at += sizes[sample];
                    sample++;
                }
            }
        }

        if (sample != samples)
        {
            throw new Mp4FormatException($"track {track}: {samples} samples, but the chunks hold {sample}", stbl.Start);
        }

        // times
        var decode = new long[samples];
        var stts = Box("stts", true);
        stts.Skip(4);
        var entries = Entries(stts.U32(), 8, stts, track);
        sample = 0;
        long time = 0;
        for (var i = 0; i < entries; i++)
        {
            var count = stts.U32();
            var delta = stts.U32();
            for (var j = 0u; j < count && sample < samples; j++)
            {
                decode[sample++] = time;
                time += delta;
            }
        }

        // a table shorter than the samples: the last ones keep the last step (tolerated, as players do)
        for (; sample < samples; sample++)
        {
            decode[sample] = time;
        }

        var composition = new int[samples];
        if (stbl.Find("ctts") is not null)
        {
            var ctts = Box("ctts", true);
            var version = ctts.U8();
            ctts.Skip(3);
            entries = Entries(ctts.U32(), 8, ctts, track);
            sample = 0;
            for (var i = 0; i < entries && sample < samples; i++)
            {
                var count = ctts.U32();
                var offset = version == 0 ? (int)Math.Min(ctts.U32(), int.MaxValue) : ctts.I32();
                for (var j = 0u; j < count && sample < samples; j++)
                {
                    composition[sample++] = offset;
                }
            }
        }

        bool[]? keys = null;
        if (stbl.Find("stss") is not null)
        {
            var stss = Box("stss", true);
            stss.Skip(4);
            entries = Entries(stss.U32(), 4, stss, track);
            keys = new bool[samples];
            for (var i = 0; i < entries; i++)
            {
                var number = stss.U32();
                if (number >= 1 && number <= samples)
                {
                    keys[number - 1] = true;
                }
            }
        }

        return new Mp4SampleTable(offsets, sizes, decode, composition, keys);
    }

    private static uint PeekFirstChunk(Mp4Bytes stsc)
    {
        var copy = stsc;
        return copy.U32();
    }

    private static long[] Chunks(Mp4Bytes box, bool wide, int track)
    {
        box.Skip(4);
        var count = Entries(box.U32(), wide ? 8 : 4, box, track);
        var offsets = new long[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = wide ? (long)Math.Min(box.U64(), long.MaxValue) : box.U32();
        }

        return offsets;
    }

    // a count the box can hold (each entry so many bytes)
    private static int Entries(uint count, double bytesEach, Mp4Bytes box, int track)
    {
        if (count > MaxSamples || (bytesEach > 0 && count * bytesEach > box.Left))
        {
            throw new Mp4FormatException($"track {track}: a table claims {count} entries, its box holds fewer", box.FilePosition);
        }

        return (int)count;
    }

    private static int Size(uint size, Mp4Bytes box) =>
        size <= int.MaxValue ? (int)size : throw new Mp4FormatException($"a sample of {size} bytes", box.FilePosition);
}
