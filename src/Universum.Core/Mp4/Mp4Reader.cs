using System.Buffers.Binary;
using System.Text;

namespace Universum.Core.Mp4;

/// <summary>
/// UNIVERSUM's own reader of MP4 files (ISO base media file format, QuickTime too): the boxes of the file – the large
/// media data only by their headers, the movie box ('moov') whole in memory – and from it the tracks, their languages,
/// names, flags, codecs and sample tables. Nothing is guessed: a box that does not fit its parent is an error with its
/// position, so a file is either understood or refused (and recorded).
/// </summary>
public static class Mp4Reader
{
    /// <summary>The largest movie box read (the sample tables of a long film are a few MB).</summary>
    public const int MaxMovieBytes = 256 << 20;

    private const int MaxDepth = 24;

    // boxes whose content is only other boxes
    private static readonly HashSet<string> Containers =
    [
        "moov", "trak", "mdia", "minf", "stbl", "edts", "dinf", "udta", "mvex", "tref", "ilst", "moof", "traf", "mfra", "gmhd", "sinf", "schi", "rinf", "strk", "strd",
    ];

    /// <summary>The file read: its top-level boxes and its movie with the tracks.</summary>
    public static Mp4File Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        return Read(stream);
    }

    public static Mp4File Read(Stream stream)
    {
        var length = stream.Length;
        var top = new List<Mp4Box>();
        Mp4Box? movie = null;
        byte[]? movieBytes = null;
        string? brand = null;
        Span<byte> header = stackalloc byte[32];
        long at = 0;
        while (at < length)
        {
            if (length - at < 8)
            {
                // a few bytes of padding at the very end are tolerated, they are recorded
                top.Add(new Mp4Box("(zvyšok)", at, 0, length - at, []));
                break;
            }

            stream.Position = at;
            stream.ReadExactly(header[..8]);
            var (type, headerSize, size) = Header(header, stream, at, length);
            if (at + size > length)
            {
                throw new Mp4FormatException($"box '{type}' claims {size} bytes, the file has only {length - at} from it (cut short?)", at);
            }

            if (type == "moov")
            {
                if (movie is not null)
                {
                    throw new Mp4FormatException("a second 'moov' box", at);
                }

                if (size > MaxMovieBytes)
                {
                    throw new Mp4FormatException($"the 'moov' box is too large ({size} bytes)", at);
                }

                movieBytes = new byte[size];
                stream.Position = at;
                stream.ReadExactly(movieBytes);
                movie = Parse(movieBytes, at, "moov", headerSize, 0);
                top.Add(movie);
            }
            else
            {
                if (type == "ftyp" && size >= headerSize + 4)
                {
                    stream.Position = at + headerSize;
                    stream.ReadExactly(header[..4]);
                    brand = TypeName(header[..4]);
                }

                top.Add(new Mp4Box(type, at, headerSize, size, []));
            }

            at += size;
        }

        if (movie is null || movieBytes is null)
        {
            throw new Mp4FormatException("the file has no 'moov' box (not an MP4, or cut short)", 0);
        }

        if (!top.Any(b => b.Type == "ftyp") && top[0].Type is not ("moov" or "wide" or "free" or "skip" or "mdat"))
        {
            throw new Mp4FormatException($"the file does not begin as an MP4 ('{top[0].Type}')", 0);
        }

        var tracks = movie.All("trak").Select(t => Mp4Track.From(t, movieBytes, movie.Start)).ToList();
        var duplicate = tracks.GroupBy(t => t.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new Mp4FormatException($"two tracks with the ID {duplicate.Key}", movie.Start);
        }

        return new Mp4File(length, top, movie, movieBytes, tracks, brand);
    }

    // the type and sizes of a box whose first 8 bytes are read (a 64-bit size and a 'uuid' read on)
    private static (string Type, int HeaderSize, long Size) Header(Span<byte> header, Stream stream, long at, long length)
    {
        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = TypeName(header.Slice(4, 4));
        var headerSize = 8;
        if (size == 1)
        {
            stream.ReadExactly(header.Slice(8, 8));
            size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8)), long.MaxValue);
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = length - at;
        }

        if (type == "uuid")
        {
            headerSize += 16;
        }

        if (size < headerSize)
        {
            throw new Mp4FormatException($"box '{type}' is smaller than its own header ({size})", at);
        }

        return (type, headerSize, size);
    }

    /// <summary>The boxes of a box in memory (its whole bytes from <paramref name="offset"/> in the file).</summary>
    internal static Mp4Box Parse(byte[] bytes, long offset, string type, int headerSize, int depth) =>
        ParseAt(bytes, 0, bytes.Length, offset, type, headerSize, depth, "");

    // an item of an iTunes list ('ilst': "©nam", "desc" …) holds boxes too ('data', 'mean', 'name')
    private static Mp4Box ParseAt(byte[] bytes, int start, int size, long fileOffset, string type, int headerSize, int depth, string parent)
    {
        if (depth > MaxDepth)
        {
            throw new Mp4FormatException("boxes nested too deep", fileOffset + start);
        }

        var children = new List<Mp4Box>();
        var content = start + headerSize;
        var end = start + size;
        if (type == "meta")
        {
            // ISO 'meta' is a full box (4 bytes of version and flags); QuickTime's is not – it begins with a box ('hdlr')
            var quickTime = end - content >= 8 && TypeName(bytes.AsSpan(content + 4, 4)) == "hdlr";
            content += quickTime ? 0 : 4;
        }

        if (type == "meta" || Containers.Contains(type) || parent == "ilst")
        {
            var at = content;
            while (at < end)
            {
                if (end - at < 8)
                {
                    // QuickTime ends some lists with 4 zero bytes
                    if (bytes.AsSpan(at, end - at).IndexOfAnyExcept((byte)0) < 0)
                    {
                        break;
                    }

                    throw new Mp4FormatException($"bytes left over in '{type}'", fileOffset + at);
                }

                long childSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
                var childType = TypeName(bytes.AsSpan(at + 4, 4));
                var childHeader = 8;
                if (childSize == 1)
                {
                    if (end - at < 16)
                    {
                        throw new Mp4FormatException($"box '{childType}' ends early", fileOffset + at);
                    }

                    childSize = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 8)), long.MaxValue);
                    childHeader = 16;
                }
                else if (childSize == 0)
                {
                    childSize = end - at;
                }

                if (childType == "uuid")
                {
                    childHeader += 16;
                }

                if (childSize < childHeader || childSize > end - at)
                {
                    throw new Mp4FormatException($"box '{childType}' in '{type}' does not fit it ({childSize} bytes, {end - at} left)", fileOffset + at);
                }

                children.Add(ParseAt(bytes, at, (int)childSize, fileOffset, childType, childHeader, depth + 1, type));
                at += (int)childSize;
            }
        }

        return new Mp4Box(type, fileOffset + start, headerSize, size, children);
    }

    /// <summary>Four bytes as a type ('©' kept as the character it stands for in Mac Roman).</summary>
    public static string TypeName(ReadOnlySpan<byte> four)
    {
        Span<char> text = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            text[i] = four[i] == 0xA9 ? '©' : four[i] is >= 0x20 and < 0x7F ? (char)four[i] : '?';
        }

        return new string(text);
    }

    /// <summary>A type as its four bytes.</summary>
    public static byte[] TypeBytes(string type)
    {
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            bytes[i] = type[i] == '©' ? (byte)0xA9 : (byte)type[i];
        }

        return bytes;
    }

    /// <summary>The content of a box from the movie in memory.</summary>
    internal static ReadOnlySpan<byte> Content(byte[] movie, long movieStart, Mp4Box box) =>
        movie.AsSpan((int)(box.ContentStart - movieStart), (int)box.ContentSize);

    internal static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes).TrimEnd('\0');
}
