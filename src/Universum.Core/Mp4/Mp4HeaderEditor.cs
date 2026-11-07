using System.Buffers.Binary;
using System.Text;
using Universum.Core.Media;

namespace Universum.Core.Mp4;

/// <summary>What changes in one track of an MP4 (null: stays as it is).</summary>
public sealed record Mp4TrackChange(int TrackId)
{
    public LanguageTag? Language { get; init; }

    /// <summary>The new name; "" removes it.</summary>
    public string? Name { get; init; }

    /// <summary>Enabled – the default track of its kind (MP4 has no other "default").</summary>
    public bool? Enabled { get; init; }
}

/// <summary>The changes of an MP4's header: of its tracks, its title ("" removes it) and the order of its tracks (IDs).</summary>
public sealed record Mp4Change(IReadOnlyList<Mp4TrackChange> Tracks, string? Title = null, IReadOnlyList<int>? Order = null);

/// <summary>What is needed to put an edited MP4 back exactly as it was: its length and where its movie box was.</summary>
public sealed record Mp4Undo(long Length, long MovieStart);

/// <summary>
/// Changes the header of an MP4 without writing its media anew (owner, 7. 11. 2026: Superrýchle also for MP4): the movie
/// box ('moov') is built again with the changes and written at the end of the file; only then is the old one turned into
/// free space ('free', 4 bytes). The media data and every offset into them stay untouched; the boxes UNIVERSUM does not
/// change are copied byte for byte. Undone by turning the old box back and cutting the file to its old length.
/// </summary>
public static class Mp4HeaderEditor
{
    /// <summary>Why the file cannot be edited in place (null: it can).</summary>
    public static string? CannotEdit(Mp4File file)
    {
        if (file.IsFragmented)
        {
            return "fragmented MP4 (the tables are in 'moof' fragments)";
        }

        if (file.Boxes.Count == 0 || file.Boxes[^1].End != file.Length)
        {
            return "the last box does not end with the file";
        }

        return null;
    }

    /// <summary>The movie box with the changes (the file itself is not touched).</summary>
    public static byte[] NewMovie(Mp4File file, Mp4Change change)
    {
        var known = file.Tracks.Select(t => t.Id).ToHashSet();
        if (change.Tracks.FirstOrDefault(t => !known.Contains(t.TrackId)) is { } unknown)
        {
            throw new ArgumentException($"the file has no track {unknown.TrackId}", nameof(change));
        }

        if (change.Order is { } order && (order.Count != known.Count || !order.ToHashSet().SetEquals(known)))
        {
            throw new ArgumentException("the order must name every track once", nameof(change));
        }

        var writer = new MovieWriter(file, change);
        return writer.Movie();
    }

    /// <summary>
    /// Writes the changes into the file: the new movie at the end, then the old one freed. The file must not be open in
    /// another program (it is opened alone). Returns what undoes it.
    /// </summary>
    public static Mp4Undo Apply(string path, Mp4Change change)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        var file = Mp4Reader.Read(stream);
        if (CannotEdit(file) is { } why)
        {
            throw new Mp4FormatException(why, 0);
        }

        // a last box "to the end of the file" (size 0) would swallow the new movie
        var last = file.Boxes[^1];
        Span<byte> size = stackalloc byte[4];
        stream.Position = last.Start;
        stream.ReadExactly(size);
        if (BinaryPrimitives.ReadUInt32BigEndian(size) == 0 || last.HeaderSize == 0)
        {
            throw new Mp4FormatException("the file ends with a box without a size (or with loose bytes)", last.Start);
        }

        var movie = NewMovie(file, change);
        var undo = new Mp4Undo(file.Length, file.Movie.Start);

        // 1. the new movie after everything (the old one still counts – the file is whole all the time)
        stream.Position = file.Length;
        stream.Write(movie);
        stream.Flush(flushToDisk: true);

        // 2. the old one becomes free space: from now on the new one counts
        stream.Position = file.Movie.Start + 4;
        stream.Write("free"u8);
        stream.Flush(flushToDisk: true);
        return undo;
    }

    /// <summary>The file exactly as before <see cref="Apply"/>: the old movie box back, the new one cut away.</summary>
    public static void Undo(string path, Mp4Undo undo)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        if (stream.Length < undo.Length || undo.MovieStart + 8 > undo.Length)
        {
            throw new IOException("the file is shorter than before the change – it was changed elsewhere");
        }

        Span<byte> type = stackalloc byte[4];
        stream.Position = undo.MovieStart + 4;
        stream.ReadExactly(type);
        if (!type.SequenceEqual("free"u8) && !type.SequenceEqual("moov"u8))
        {
            throw new IOException("the old movie box is not where it was – the file was changed elsewhere");
        }

        stream.Position = undo.MovieStart + 4;
        stream.Write("moov"u8);
        stream.Flush(flushToDisk: true);
        stream.SetLength(undo.Length);
        stream.Flush(flushToDisk: true);
    }

    // builds the movie box again: changed boxes replaced, all others (and the bytes between boxes) copied as they are
    private sealed class MovieWriter(Mp4File file, Mp4Change change)
    {
        private readonly byte[] _bytes = file.MovieBytes;
        private readonly long _start = file.Movie.Start;
        private readonly Dictionary<int, Mp4TrackChange> _tracks = change.Tracks.ToDictionary(t => t.TrackId);

        public byte[] Movie()
        {
            var output = new MemoryStream(_bytes.Length + 4096);
            Write(output, file.Movie, MovieChildren);
            return output.ToArray();
        }

        // the children of 'moov': the tracks in the order asked, the title in 'udta'
        private IEnumerable<Part> MovieChildren(Mp4Box moov)
        {
            var tracks = moov.All("trak").ToList();
            if (change.Order is { } order)
            {
                var byId = file.Tracks.ToDictionary(t => t.Id, t => t.Box);
                var queue = new Queue<Mp4Box>(order.Select(id => byId[id]));
                // each track takes the place of the next one in the original order (the boxes between stay where they were)
                foreach (var child in moov.Children)
                {
                    yield return child.Type == "trak" ? Part.Of(queue.Dequeue(), TrackChildren) : Part.Of(child, child.Type == "udta" && change.Title is not null ? TitleChildren : NoChange);
                }
            }
            else
            {
                foreach (var child in moov.Children)
                {
                    yield return Part.Of(child, child.Type == "trak" ? TrackChildren : child.Type == "udta" && change.Title is not null ? TitleChildren : NoChange);
                }
            }

            if (change.Title is { Length: > 0 } title && moov.Find("udta") is null)
            {
                yield return Part.New(UserData(Title(title)));
            }
        }

        // a box copied as it is
        private static readonly Func<Mp4Box, IEnumerable<Part>>? NoChange = null;

        private IEnumerable<Part> TrackChildren(Mp4Box trak)
        {
            var id = file.Tracks.First(t => t.Box == trak).Id;
            _tracks.TryGetValue(id, out var wanted);
            foreach (var child in trak.Children)
            {
                if (wanted is null)
                {
                    yield return Part.Of(child, null);
                }
                else if (child.Type == "tkhd" && wanted.Enabled is { } enabled)
                {
                    var bytes = Raw(child);
                    var flags = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(child.HeaderSize)) & 0x00FFFFFF;
                    flags = enabled ? flags | 1 : flags & ~1u;
                    bytes[child.HeaderSize + 1] = (byte)(flags >> 16);
                    bytes[child.HeaderSize + 2] = (byte)(flags >> 8);
                    bytes[child.HeaderSize + 3] = (byte)flags;
                    yield return Part.New(bytes);
                }
                else if (child.Type == "mdia" && (wanted.Language is not null || wanted.Name is not null))
                {
                    yield return Part.Of(child, MediaChildren);
                }
                else if (child.Type == "udta" && wanted.Name is not null)
                {
                    yield return Part.Of(child, box => NamedChildren(box, wanted.Name));
                }
                else
                {
                    yield return Part.Of(child, null);
                }
            }

            if (wanted?.Name is { Length: > 0 } name && trak.Find("udta") is null)
            {
                yield return Part.New(UserData(Box("name", Encoding.UTF8.GetBytes(name))));
            }

            IEnumerable<Part> MediaChildren(Mp4Box mdia)
            {
                foreach (var child in mdia.Children)
                {
                    if (child.Type == "mdhd" && wanted!.Language is { } language)
                    {
                        var bytes = Raw(child);
                        var version = bytes[child.HeaderSize];
                        var at = child.HeaderSize + 4 + (version == 1 ? 28 : 16);
                        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at), Mp4Languages.Pack(language));
                        yield return Part.New(bytes);
                    }
                    else if (child.Type == "elng" && wanted!.Language is not null)
                    {
                        // the extended language would say the old one: it goes, the language of 'mdhd' counts
                        continue;
                    }
                    else if (child.Type == "hdlr" && wanted!.Name is { } name && child.ContentSize >= 24)
                    {
                        // players (and ffprobe, so media servers) show the handler's name as the track's name
                        yield return Part.New(Handler(child, name));
                    }
                    else
                    {
                        yield return Part.Of(child, null);
                    }
                }
            }
        }

        // the handler with another name: its first 24 bytes as they are, the name as the original wrote it (QuickTime:
        // its length first; ISO: ended by zero); no name gives the usual one of the kind
        private byte[] Handler(Mp4Box hdlr, string name)
        {
            var content = _bytes.AsSpan((int)(hdlr.ContentStart - _start), (int)hdlr.ContentSize);
            var fixedPart = content[..24];
            var oldName = content[24..];
            var handler = Mp4Reader.TypeName(fixedPart.Slice(8, 4));
            var text = name.Length > 0 ? name : handler switch
            {
                "vide" => "VideoHandler",
                "soun" => "SoundHandler",
                "subt" or "text" or "sbtl" => "SubtitleHandler",
                _ => "Handler",
            };
            var utf8 = Encoding.UTF8.GetBytes(text);
            var pascal = oldName.Length > 0 && oldName[0] == oldName.Length - 1 && oldName[0] > 0;
            byte[] written = pascal
                ? [(byte)Math.Min(utf8.Length, 255), .. utf8.AsSpan(0, Math.Min(utf8.Length, 255))]
                : [.. utf8, 0];
            return Box("hdlr", Join(fixedPart.ToArray(), written));
        }

        // a track's 'udta': its 'name' replaced (or added, or removed for "")
        private IEnumerable<Part> NamedChildren(Mp4Box udta, string name)
        {
            var written = false;
            foreach (var child in udta.Children)
            {
                if (child.Type == "name")
                {
                    if (!written && name.Length > 0)
                    {
                        yield return Part.New(Box("name", Encoding.UTF8.GetBytes(name)));
                    }

                    written = true;
                    continue;
                }

                yield return Part.Of(child, null);
            }

            if (!written && name.Length > 0)
            {
                yield return Part.New(Box("name", Encoding.UTF8.GetBytes(name)));
            }
        }

        // the movie's 'udta': the title in 'meta/ilst/©nam' (and QuickTime's '©nam'), or both removed for ""
        private IEnumerable<Part> TitleChildren(Mp4Box udta)
        {
            var title = change.Title!;
            var placed = false;
            foreach (var child in udta.Children)
            {
                if (child.Type == "©nam")
                {
                    continue;
                }

                if (child.Type == "meta" && child.Find("ilst") is not null)
                {
                    placed = true;
                    yield return Part.Of(child, meta => meta.Children.Select(c => c.Type == "ilst" ? Part.Of(c, ilst => ListChildren(ilst, title)) : Part.Of(c, null)));
                    continue;
                }

                yield return Part.Of(child, null);
            }

            if (!placed && title.Length > 0)
            {
                yield return Part.New(Title(title));
            }
        }

        private static IEnumerable<Part> ListChildren(Mp4Box ilst, string title)
        {
            var placed = false;
            foreach (var item in ilst.Children)
            {
                if (item.Type == "©nam")
                {
                    if (!placed && title.Length > 0)
                    {
                        yield return Part.New(Item(title));
                    }

                    placed = true;
                    continue;
                }

                yield return Part.Of(item, null);
            }

            if (!placed && title.Length > 0)
            {
                yield return Part.New(Item(title));
            }
        }

        // writes a box: as it is, or its header and its children (and the bytes between them) again
        private void Write(Stream output, Mp4Box box, Func<Mp4Box, IEnumerable<Part>>? children)
        {
            if (children is null)
            {
                output.Write(_bytes, (int)(box.Start - _start), (int)box.Size);
                return;
            }

            var headerAt = output.Position;
            var wide = box.HeaderSize >= 16 && box.Type != "uuid" || box.HeaderSize == 32;
            output.Write(_bytes, (int)(box.Start - _start), box.HeaderSize);

            // the bytes before the first child (the 4 of 'meta') and after the last one (QuickTime's zeros) stay as they are
            var original = box.Children;
            var firstChild = original.Count > 0 ? original[0].Start : box.End;
            output.Write(_bytes, (int)(box.ContentStart - _start), (int)(firstChild - box.ContentStart));
            foreach (var part in children(box))
            {
                if (part.Bytes is { } fresh)
                {
                    output.Write(fresh);
                }
                else
                {
                    Write(output, part.Box!, part.Children);
                }
            }

            if (original.Count > 0)
            {
                output.Write(_bytes, (int)(original[^1].End - _start), (int)(box.End - original[^1].End));
            }

            // the size of the box as it is now
            var size = output.Position - headerAt;
            var end = output.Position;
            output.Position = headerAt;
            Span<byte> number = stackalloc byte[8];
            if (wide)
            {
                output.Position = headerAt + 8;
                BinaryPrimitives.WriteUInt64BigEndian(number, (ulong)size);
                output.Write(number);
            }
            else
            {
                if (size > uint.MaxValue)
                {
                    throw new Mp4FormatException($"the box '{box.Type}' grew beyond 4 GB", box.Start);
                }

                BinaryPrimitives.WriteUInt32BigEndian(number, (uint)size);
                output.Write(number[..4]);
            }

            output.Position = end;
        }

        private byte[] Raw(Mp4Box box) => _bytes.AsSpan((int)(box.Start - _start), (int)box.Size).ToArray();

        private static byte[] Box(string type, ReadOnlySpan<byte> content)
        {
            var box = new byte[8 + content.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Mp4Reader.TypeBytes(type).CopyTo(box, 4);
            content.CopyTo(box.AsSpan(8));
            return box;
        }

        private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        private static byte[] UserData(byte[] content) => Box("udta", content);

        // iTunes: an item whose 'data' is UTF-8 text (type 1, no locale)
        private static byte[] Item(string title) =>
            Box("©nam", Box("data", Join([0, 0, 0, 1, 0, 0, 0, 0], Encoding.UTF8.GetBytes(title))));

        // 'meta' (a full box) with its handler 'mdir' and a list of one item
        private static byte[] Title(string title) =>
            Box("meta", Join([0, 0, 0, 0], Box("hdlr", Join(new byte[8], "mdirappl"u8.ToArray(), new byte[9])), Box("ilst", Item(title))));
    }

    // a child written again: a box of the file (as it is, or rebuilt by its own children) or new bytes
    private sealed record Part(Mp4Box? Box, Func<Mp4Box, IEnumerable<Part>>? Children, byte[]? Bytes)
    {
        public static Part Of(Mp4Box box, Func<Mp4Box, IEnumerable<Part>>? children) => new(box, children, null);

        public static Part New(byte[] bytes) => new(null, null, bytes);
    }
}
