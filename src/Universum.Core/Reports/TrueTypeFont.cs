using System.Buffers.Binary;
using System.Text;

namespace Universum.Core.Reports;

/// <summary>A TrueType font that cannot be used: damaged, not TrueType, or its licence forbids embedding.</summary>
public sealed class FontException(string message) : Exception(message);

/// <summary>
/// A TrueType font read by UNIVERSUM's own code (owner, 31. 10. 2026: the PDF of POZNÁMKY written anew, no library):
/// the widths of its letters for laying out text, which glyph shows a character, and a subset of the font with only the
/// glyphs used – what a PDF embeds so that Slovak and Czech letters print exactly. A font whose licence (OS/2 fsType)
/// forbids embedding is refused.
/// </summary>
public sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ushort> _glyphs = [];
    private readonly ushort[] _advances;
    private readonly int[] _locations;

    public TrueTypeFont(byte[] data)
    {
        _data = data;
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(data) is not (0x00010000 or 0x74727565))
        {
            throw new FontException("Nie je to písmo TrueType.");
        }

        var count = U16(4);
        for (var i = 0; i < count; i++)
        {
            var at = 12 + (16 * i);
            var tag = Encoding.ASCII.GetString(data, at, 4);
            var offset = (int)U32(at + 8);
            var length = (int)U32(at + 12);
            if (offset < 0 || length < 0 || offset + length > data.Length)
            {
                throw new FontException("Písmo je poškodené (tabuľka " + tag + ").");
            }

            _tables[tag] = (offset, length);
        }

        foreach (var needed in new[] { "head", "hhea", "hmtx", "maxp", "loca", "glyf" })
        {
            if (!_tables.ContainsKey(needed))
            {
                throw new FontException("Písmu chýba tabuľka " + needed + ".");
            }
        }

        var head = _tables["head"].Offset;
        UnitsPerEm = U16(head + 18);
        var longOffsets = S16(head + 50) == 1;
        BoundingBox = (S16(head + 36), S16(head + 38), S16(head + 40), S16(head + 42));
        var hhea = _tables["hhea"].Offset;
        Ascent = S16(hhea + 4);
        Descent = S16(hhea + 6);
        LineGap = S16(hhea + 8);
        var metrics = U16(hhea + 34);
        GlyphCount = U16(_tables["maxp"].Offset + 4);
        _advances = new ushort[GlyphCount];
        var hmtx = _tables["hmtx"].Offset;
        for (var g = 0; g < GlyphCount; g++)
        {
            _advances[g] = U16(hmtx + (4 * Math.Min(g, metrics - 1)));
        }

        _locations = new int[GlyphCount + 1];
        var loca = _tables["loca"].Offset;
        for (var g = 0; g <= GlyphCount; g++)
        {
            _locations[g] = longOffsets ? (int)U32(loca + (4 * g)) : U16(loca + (2 * g)) * 2;
        }

        // a subset made for a PDF has no map of characters (the PDF names the glyphs by their numbers)
        if (_tables.ContainsKey("cmap"))
        {
            ReadCmap();
        }

        if (_tables.TryGetValue("OS/2", out var os2))
        {
            var fsType = U16(os2.Offset + 8);
            // bit 1: restricted licence (no embedding); bit 9: only bitmaps may be embedded
            if ((fsType & 0x000F) == 0x0002 || (fsType & 0x0200) != 0)
            {
                throw new FontException("Licencia písma nedovoľuje vložiť ho do PDF.");
            }

            CapHeight = U16(os2.Offset) >= 2 && os2.Length >= 90 ? S16(os2.Offset + 88) : Ascent * 0.7;
        }
        else
        {
            CapHeight = Ascent * 0.7;
        }

        Name = PostScriptName() ?? "Font";
    }

    /// <summary>The font of a file.</summary>
    public static TrueTypeFont Load(string file) => new(File.ReadAllBytes(file));

    public string Name { get; }

    public int UnitsPerEm { get; }

    public int GlyphCount { get; }

    /// <summary>Above the baseline, in font units.</summary>
    public int Ascent { get; }

    /// <summary>Below the baseline (negative), in font units.</summary>
    public int Descent { get; }

    public int LineGap { get; }

    public double CapHeight { get; }

    public (int XMin, int YMin, int XMax, int YMax) BoundingBox { get; }

    /// <summary>The glyph of a character (0: the font has none).</summary>
    public ushort Glyph(int codePoint) => _glyphs.GetValueOrDefault(codePoint);

    /// <summary>The advance of a glyph in font units.</summary>
    public int Advance(ushort glyph) => glyph < _advances.Length ? _advances[glyph] : 0;

    /// <summary>The width of a text in points at a size.</summary>
    public double Width(string text, double size)
    {
        long units = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            units += Advance(Glyph(rune.Value));
        }

        return units * size / UnitsPerEm;
    }

    /// <summary>
    /// The font with only the glyphs given (and the empty glyph 0, and the parts of composite glyphs) – the numbers of the
    /// glyphs stay, the others are empty – with the tables a PDF reader needs.
    /// </summary>
    public byte[] Subset(IEnumerable<ushort> used)
    {
        var keep = new HashSet<ushort> { 0 };
        var queue = new Queue<ushort>(used.Where(g => g < GlyphCount));
        while (queue.TryDequeue(out var glyph))
        {
            if (!keep.Add(glyph) && glyph != 0)
            {
                continue;
            }

            foreach (var part in Components(glyph))
            {
                if (!keep.Contains(part))
                {
                    queue.Enqueue(part);
                }
            }
        }

        // glyf and loca anew (long offsets)
        var glyf = new MemoryStream();
        var loca = new byte[(GlyphCount + 1) * 4];
        for (var g = 0; g < GlyphCount; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)glyf.Length);
            var length = _locations[g + 1] - _locations[g];
            if (keep.Contains((ushort)g) && length > 0)
            {
                glyf.Write(_data, _tables["glyf"].Offset + _locations[g], length);
                while (glyf.Length % 4 != 0)
                {
                    glyf.WriteByte(0);
                }
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(GlyphCount * 4), (uint)glyf.Length);
        var head = Table("head");
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);        // long offsets
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);        // checksum adjustment, set below
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["glyf"] = glyf.ToArray(),
            ["head"] = head,
            ["hhea"] = Table("hhea"),
            ["hmtx"] = Table("hmtx"),
            ["loca"] = loca,
            ["maxp"] = Table("maxp"),
        };
        foreach (var hinting in new[] { "cvt ", "fpgm", "prep" })
        {
            if (_tables.ContainsKey(hinting))
            {
                tables[hinting] = Table(hinting);
            }
        }

        var font = Assemble(tables);
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(HeadOffset(font) + 8), unchecked(0xB1B0AFBA - Checksum(font)));
        return font;
    }

    // the glyphs a composite glyph is made of
    private IEnumerable<ushort> Components(ushort glyph)
    {
        var start = _tables["glyf"].Offset + _locations[glyph];
        if (_locations[glyph + 1] - _locations[glyph] < 10 || S16(start) >= 0)
        {
            yield break;
        }

        var at = start + 10;
        while (true)
        {
            var flags = U16(at);
            yield return U16(at + 2);
            at += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
            at += (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
            if ((flags & 0x0020) == 0)
            {
                break;
            }
        }
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var count = tables.Count;
        var power = 1;
        var log = 0;
        while (power * 2 <= count)
        {
            power *= 2;
            log++;
        }

        var output = new MemoryStream();
        var header = new byte[12 + (16 * count)];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)(power * 16));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)((count * 16) - (power * 16)));
        output.Write(header);
        var i = 0;
        foreach (var (tag, data) in tables)
        {
            var entry = header.AsSpan(12 + (16 * i++));
            Encoding.ASCII.GetBytes(tag, entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)output.Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], (uint)data.Length);
            output.Write(data);
            while (output.Length % 4 != 0)
            {
                output.WriteByte(0);
            }
        }

        var font = output.ToArray();
        header.CopyTo(font, 0);
        return font;
    }

    private static int HeadOffset(byte[] font)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            if (Encoding.ASCII.GetString(font, 12 + (16 * i), 4) == "head")
            {
                return (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(12 + (16 * i) + 8));
            }
        }

        throw new FontException("Podmnožine písma chýba tabuľka head.");
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var b = 0; b < 4; b++)
            {
                word = (word << 8) | (i + b < data.Length ? data[i + b] : 0u);
            }

            sum = unchecked(sum + word);
        }

        return sum;
    }

    private byte[] Table(string tag) => _data.AsSpan(_tables[tag].Offset, _tables[tag].Length).ToArray();

    // the Unicode cmap: format 12 (all of Unicode) or format 4 (the basic plane)
    private void ReadCmap()
    {
        var cmap = _tables["cmap"].Offset;
        var count = U16(cmap + 2);
        int? format4 = null, format12 = null;
        for (var i = 0; i < count; i++)
        {
            var platform = U16(cmap + 4 + (8 * i));
            var encoding = U16(cmap + 6 + (8 * i));
            var offset = cmap + (int)U32(cmap + 8 + (8 * i));
            var format = U16(offset);
            if (format == 12 && (platform == 3 && encoding == 10 || platform == 0))
            {
                format12 = offset;
            }
            else if (format == 4 && (platform == 3 && encoding is 1 or 0 || platform == 0))
            {
                format4 ??= offset;
            }
        }

        if (format12 is { } table12)
        {
            var groups = (int)U32(table12 + 12);
            for (var g = 0; g < groups; g++)
            {
                var at = table12 + 16 + (12 * g);
                var first = (int)U32(at);
                var last = (int)U32(at + 4);
                var glyph = (int)U32(at + 8);
                for (var c = first; c <= last && c - first < 0x10000; c++)
                {
                    _glyphs[c] = (ushort)(glyph + c - first);
                }
            }

            return;
        }

        if (format4 is not { } table)
        {
            throw new FontException("Písmo nemá mapu znakov Unicode.");
        }

        var segments = U16(table + 6) / 2;
        var ends = table + 14;
        var starts = ends + (2 * segments) + 2;
        var deltas = starts + (2 * segments);
        var ranges = deltas + (2 * segments);
        for (var s = 0; s < segments; s++)
        {
            int end = U16(ends + (2 * s)), start = U16(starts + (2 * s)), delta = S16(deltas + (2 * s)), range = U16(ranges + (2 * s));
            for (var c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (range == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    var at = ranges + (2 * s) + range + (2 * (c - start));
                    glyph = at + 1 < _data.Length ? U16(at) : 0;
                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }
                }

                if (glyph != 0)
                {
                    _glyphs[c] = (ushort)glyph;
                }
            }
        }
    }

    // the PostScript name (name ID 6), letters and digits only
    private string? PostScriptName()
    {
        if (!_tables.TryGetValue("name", out var name))
        {
            return null;
        }

        var count = U16(name.Offset + 2);
        var strings = name.Offset + U16(name.Offset + 4);
        for (var i = 0; i < count; i++)
        {
            var at = name.Offset + 6 + (12 * i);
            if (U16(at + 6) != 6)
            {
                continue;
            }

            var platform = U16(at);
            var length = U16(at + 8);
            var offset = strings + U16(at + 10);
            var text = platform is 0 or 3 ? Encoding.BigEndianUnicode.GetString(_data, offset, length) : Encoding.ASCII.GetString(_data, offset, length);
            var clean = new string(text.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
            if (clean.Length > 0)
            {
                return clean;
            }
        }

        return null;
    }

    private ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at));

    private short S16(int at) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(at));

    private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at));
}
