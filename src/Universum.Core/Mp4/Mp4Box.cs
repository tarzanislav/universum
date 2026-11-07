using System.Buffers.Binary;
using System.Text;

namespace Universum.Core.Mp4;

/// <summary>
/// One box of an MP4 (ISO base media) file: its four-letter type, where it starts in the file, how large its header is
/// (8, 16 with a 64-bit size, +16 for 'uuid') and its whole size. Boxes UNIVERSUM reads into are given with their children.
/// </summary>
public sealed record Mp4Box(string Type, long Start, int HeaderSize, long Size, IReadOnlyList<Mp4Box> Children)
{
    /// <summary>Where the content (after the header) begins.</summary>
    public long ContentStart => Start + HeaderSize;

    public long ContentSize => Size - HeaderSize;

    public long End => Start + Size;

    /// <summary>The first child of the type (a path "mdia/minf/stbl" goes deeper); null when there is none.</summary>
    public Mp4Box? Find(string path)
    {
        var box = this;
        foreach (var part in path.Split('/'))
        {
            box = box.Children.FirstOrDefault(c => c.Type == part);
            if (box is null)
            {
                return null;
            }
        }

        return box;
    }

    public IEnumerable<Mp4Box> All(string type) => Children.Where(c => c.Type == type);
}

/// <summary>An MP4 file UNIVERSUM cannot read safely: what is wrong and where (for the MP4 record, never with names).</summary>
public sealed class Mp4FormatException(string message, long position) : FormatException(message)
{
    /// <summary>The byte of the file where the problem was found.</summary>
    public long Position { get; } = position;
}

/// <summary>Reading boxes from memory: big-endian numbers, four-letter types, bounds always checked.</summary>
internal ref struct Mp4Bytes(ReadOnlySpan<byte> data, long fileOffset)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private readonly long _offset = fileOffset;
    private int _at;

    public int Position { readonly get => _at; set => _at = value; }

    public readonly int Length => _data.Length;

    public readonly int Left => _data.Length - _at;

    /// <summary>The position in the file (for the record).</summary>
    public readonly long FilePosition => _offset + _at;

    public byte U8() => Take(1)[0];

    public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

    public uint U24()
    {
        var bytes = Take(3);
        return (uint)((bytes[0] << 16) | (bytes[1] << 8) | bytes[2]);
    }

    public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public int I32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    public ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));

    public string FourCc() => Mp4Reader.TypeName(Take(4));

    public void Skip(int count) => Take(count);

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Left)
        {
            throw new Mp4FormatException($"a box ends early (needs {count} more bytes, has {Left})", FilePosition);
        }

        var part = _data.Slice(_at, count);
        _at += count;
        return part;
    }

    /// <summary>A string ended by zero (or by the end), UTF-8.</summary>
    public string ZeroEnded(int maxLength)
    {
        var rest = _data.Slice(_at, Math.Min(maxLength, Left));
        var end = rest.IndexOf((byte)0);
        var text = Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]);
        _at += end < 0 ? rest.Length : end + 1;
        return text;
    }
}
