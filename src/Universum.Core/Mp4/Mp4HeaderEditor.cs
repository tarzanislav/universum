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

    /// <summary>
    /// The roles UNIVERSUM manages the track is to have (<see cref="Mp4Roles.Managed"/>; empty: none of them) – null keeps
    /// them; other roles stay.
    /// </summary>
    public IReadOnlyList<string>? Roles { get; init; }
}

/// <summary>
/// The changes of an MP4's header: of its tracks, its title ("" removes it), the order of its tracks (IDs of the tracks
/// that stay) and the tracks left out (IDs; owner, 8. 11. 2026 – subtitles: their few bytes stay in the file unused, the
/// track is gone from its header).
/// </summary>
public sealed record Mp4Change(IReadOnlyList<Mp4TrackChange> Tracks, string? Title = null, IReadOnlyList<int>? Order = null)
{
    public IReadOnlyList<int> Removed { get; init; } = [];
}

/// <summary>
/// What is needed to put an edited MP4 back exactly as it was: its length, where its movie box was and how large it was
/// (null in records of 0.3.23, which did not keep it).
/// </summary>
public sealed record Mp4Undo(long Length, long MovieStart, long? MovieSize = null);

/// <summary>The MP4 is not as UNIVERSUM left it (another program wrote it since): nothing is undone.</summary>
public sealed class Mp4UndoRefusedException(string message) : IOException(message);

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

        if (file.Boxes.Count == 0 || file.Boxes[^1].End != file.Length || file.Boxes[^1].HeaderSize == 0 || file.LastBoxOpenEnded)
        {
            return "the file does not end with a whole box (a box without a size, or loose bytes)";
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

        if (change.Removed.Where(id => !known.Contains(id)).Select(id => (int?)id).FirstOrDefault() is { } missing)
        {
            throw new ArgumentException($"the file has no track {missing} to leave out", nameof(change));
        }

        var staying = known.Except(change.Removed).ToHashSet();
        if (staying.Count == 0 || change.Tracks.Any(t => change.Removed.Contains(t.TrackId)))
        {
            throw new ArgumentException("a track left out cannot change, and some track must stay", nameof(change));
        }

        if (change.Order is { } order && (order.Distinct().Count() != order.Count || !order.All(staying.Contains)))
        {
            throw new ArgumentException("the order names a track twice, or one that does not stay", nameof(change));
        }

        var writer = new MovieWriter(file, change);
        return writer.Movie();
    }

    /// <summary>
    /// The IDs of the tracks in the order they will have: the tracks the order names fill the places of the named ones
    /// in its order; a track it does not name (one MKVToolNix does not show – QuickTime's chapters) keeps its place; the
    /// tracks left out are gone.
    /// </summary>
    public static IReadOnlyList<int> ResultOrder(Mp4File file, Mp4Change change)
    {
        var staying = file.Tracks.Select(t => t.Id).Where(id => !change.Removed.Contains(id)).ToList();
        if (change.Order is not { } order)
        {
            return staying;
        }

        var named = order.ToHashSet();
        var queue = new Queue<int>(order);
        return [.. staying.Select(id => named.Contains(id) ? queue.Dequeue() : id)];
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
        var undo = new Mp4Undo(file.Length, file.Movie.Start, file.Movie.Size);

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

    /// <summary>
    /// The file exactly as before <see cref="Apply"/>: the old movie box back, the new one cut away. Done only when the
    /// file is just as the edit left it – the new movie box from the old end of the file to its end, the old one (free
    /// space now) where it was and as large as it was; otherwise <see cref="Mp4UndoRefusedException"/> and nothing changes
    /// (owner, 8. 11. 2026: a file another program wrote since must never be cut). A file the edit never reached is left
    /// as it is.
    /// </summary>
    public static void Undo(string path, Mp4Undo undo)
    {
        // looked at first without writing (a file the edit never reached may be open elsewhere)
        using (var look = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (look.Length == undo.Length && BoxAt(look, undo.MovieStart) is ("moov", var size) && (undo.MovieSize is null || size == undo.MovieSize))
            {
                return;
            }
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        var length = stream.Length;
        if (length <= undo.Length || undo.MovieStart + 8 > undo.Length)
        {
            throw new Mp4UndoRefusedException($"the file is {length} bytes, not longer than the {undo.Length} it had before the edit");
        }

        if (BoxAt(stream, undo.Length) is not ("moov", var newSize) || newSize != length - undo.Length)
        {
            throw new Mp4UndoRefusedException("the movie box UNIVERSUM wrote is not at the end of the file any more");
        }

        if (BoxAt(stream, undo.MovieStart) is not ("free" or "moov", var oldSize) || (undo.MovieSize is { } recorded && oldSize != recorded) ||
            undo.MovieStart + oldSize > undo.Length)
        {
            throw new Mp4UndoRefusedException("the old movie box is not where it was, or not as large");
        }

        stream.Position = undo.MovieStart + 4;
        stream.Write("moov"u8);
        stream.Flush(flushToDisk: true);
        stream.SetLength(undo.Length);
        stream.Flush(flushToDisk: true);
    }

    // the type and size of the box that begins at a place (a 64-bit size read on); null when there is none
    private static (string Type, long Size)? BoxAt(Stream stream, long at)
    {
        if (at < 0 || at + 8 > stream.Length)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[16];
        stream.Position = at;
        stream.ReadExactly(header[..8]);
        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (size == 1 && at + 16 <= stream.Length)
        {
            stream.ReadExactly(header.Slice(8, 8));
            size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8)), long.MaxValue);
        }

        return (Mp4Reader.TypeName(header.Slice(4, 4)), size);
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

        // the children of 'moov': the tracks that stay in the order asked, the title in 'udta'
        private IEnumerable<Part> MovieChildren(Mp4Box moov)
        {
            // the tracks that stay take the places of the tracks that stay, in the order asked (the boxes between stay where they were)
            var byId = file.Tracks.ToDictionary(t => t.Id, t => t.Box);
            var queue = new Queue<Mp4Box>(ResultOrder(file, change).Select(id => byId[id]));
            var removed = file.Tracks.Where(t => change.Removed.Contains(t.Id)).Select(t => t.Box).ToHashSet();
            foreach (var child in moov.Children)
            {
                if (child.Type != "trak")
                {
                    yield return Part.Of(child, change.Title is null ? NoChange : child.Type == "udta" ? TitleChildren : child.Type == "meta" ? KeyedTitle : NoChange);
                }
                else if (!removed.Contains(child))
                {
                    yield return Part.Of(queue.Dequeue(), TrackChildren);
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
                if (child.Type == "tref" && change.Removed.Count > 0)
                {
                    // references to a track left out go with it (QuickTime's forced subtitles, chapters …)
                    if (References(child) is { } kept)
                    {
                        yield return Part.New(kept);
                    }
                }
                else if (wanted is null)
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
                else if (child.Type == "udta" && (wanted.Name is not null || wanted.Roles is not null))
                {
                    yield return Part.Of(child, box => TrackUserData(box, wanted));
                }
                else
                {
                    yield return Part.Of(child, null);
                }
            }

            if (wanted is not null && trak.Find("udta") is null && (wanted.Name is { Length: > 0 } || wanted.Roles is { Count: > 0 }))
            {
                var content = new List<byte>();
                if (wanted.Name is { Length: > 0 } name)
                {
                    content.AddRange(Box("name", Encoding.UTF8.GetBytes(name)));
                }

                foreach (var role in wanted.Roles ?? [])
                {
                    content.AddRange(Kind(role));
                }

                yield return Part.New(UserData([.. content]));
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
                        if (Mp4Languages.Extended(language) is { } extended)
                        {
                            // a region or script ("pt-BR") 'mdhd' cannot say: the extended language ('elng', BCP 47)
                            yield return Part.New(Box("elng", Join([0, 0, 0, 0], Encoding.UTF8.GetBytes(extended), [0])));
                        }
                    }
                    else if (child.Type == "elng" && wanted!.Language is not null)
                    {
                        // the extended language would say the old one: it goes (a new one is written after 'mdhd' when needed)
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

        // the handler with another name: its first 24 bytes as they are, the name as FFmpeg reads it (a QuickTime file: its
        // length first; an ISO file: ended by zero); no name gives the usual one of the kind
        private byte[] Handler(Mp4Box hdlr, string name)
        {
            var content = _bytes.AsSpan((int)(hdlr.ContentStart - _start), (int)hdlr.ContentSize);
            var fixedPart = content[..24];
            var handler = Mp4Reader.TypeName(fixedPart.Slice(8, 4));
            var text = name.Length > 0 ? name : handler switch
            {
                "vide" => "VideoHandler",
                "soun" => "SoundHandler",
                "subt" or "text" or "sbtl" => "SubtitleHandler",
                _ => "Handler",
            };
            var utf8 = Encoding.UTF8.GetBytes(text);
            // QuickTime's way (its length first) where FFmpeg reads it so: in a file that is no ISO file
            var pascal = file.Brand is null or "qt  ";
            byte[] written = pascal
                ? [(byte)Math.Min(utf8.Length, 255), .. utf8.AsSpan(0, Math.Min(utf8.Length, 255))]
                : [.. utf8, 0];
            return Box("hdlr", Join(fixedPart.ToArray(), written));
        }

        // a track's 'udta': its 'name' replaced (or added, or removed for ""), its roles of MPEG-DASH ('kind') written
        // anew – the kinds of other schemes and all other boxes as they are
        private IEnumerable<Part> TrackUserData(Mp4Box udta, Mp4TrackChange wanted)
        {
            var named = false;
            foreach (var child in udta.Children)
            {
                if (child.Type == "name" && wanted.Name is { } name)
                {
                    if (!named && name.Length > 0)
                    {
                        yield return Part.New(Box("name", Encoding.UTF8.GetBytes(name)));
                    }

                    named = true;
                    continue;
                }

                if (child.Type == "kind" && wanted.Roles is not null && IsManagedRole(child))
                {
                    continue;
                }

                yield return Part.Of(child, null);
            }

            if (!named && wanted.Name is { Length: > 0 } added)
            {
                yield return Part.New(Box("name", Encoding.UTF8.GetBytes(added)));
            }

            foreach (var role in wanted.Roles ?? [])
            {
                yield return Part.New(Kind(role));
            }
        }

        // 'tref' without the IDs of the tracks left out (a type of reference left empty goes); null when nothing is left
        private byte[]? References(Mp4Box tref)
        {
            var kept = new List<byte>();
            foreach (var reference in tref.Children)
            {
                var content = _bytes.AsSpan((int)(reference.ContentStart - _start), (int)reference.ContentSize);
                var ids = new List<byte>();
                for (var at = 0; at + 4 <= content.Length; at += 4)
                {
                    if (!change.Removed.Contains((int)BinaryPrimitives.ReadUInt32BigEndian(content[at..])))
                    {
                        ids.AddRange(content.Slice(at, 4).ToArray());
                    }
                }

                if (ids.Count > 0)
                {
                    kept.AddRange(Box(reference.Type, ids.ToArray()));
                }
            }

            return kept.Count == 0 ? null : Box("tref", kept.ToArray());
        }

        // a 'kind' of the scheme of MPEG-DASH roles with a role UNIVERSUM sets (forced, caption, commentary, description)
        private bool IsManagedRole(Mp4Box kind)
        {
            var content = new Mp4Bytes(_bytes.AsSpan((int)(kind.ContentStart - _start), (int)kind.ContentSize), kind.ContentStart);
            if (content.Left <= 4)
            {
                return false;
            }

            content.Skip(4);
            return content.ZeroEnded(256) == Mp4Roles.Scheme && Mp4Roles.Managed.Contains(content.ZeroEnded(256));
        }

        // 'kind' (a full box): the scheme and the value, each ended by zero
        private static byte[] Kind(string role) =>
            Box("kind", Join([0, 0, 0, 0], Encoding.UTF8.GetBytes(Mp4Roles.Scheme), [0], Encoding.UTF8.GetBytes(role), [0]));

        // the movie's 'udta': the title in iTunes 'meta/ilst/©nam' (the first such list; a 'meta' of iTunes without a list gets
        // one, none at all a new 'meta'), in QuickTime's keyed metadata where it has one, QuickTime's '©nam' removed; for ""
        // the title is removed everywhere
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

                if (child.Type == "meta" && file.MetaHandler(child) == "mdta")
                {
                    yield return Part.Of(child, KeyedTitle);
                    continue;
                }

                if (child.Type == "meta" && child.Find("ilst") is not null)
                {
                    var first = !placed;
                    placed = true;
                    yield return Part.Of(child, meta => meta.Children.Select(c => c.Type == "ilst" ? Part.Of(c, ilst => ListChildren(ilst, first ? title : "")) : Part.Of(c, null)));
                    continue;
                }

                if (child.Type == "meta" && file.MetaHandler(child) == "mdir" && !placed && title.Length > 0)
                {
                    placed = true;
                    yield return Part.Of(child, meta => [.. meta.Children.Select(c => Part.Of(c, null)), Part.New(Box("ilst", Item(title)))]);
                    continue;
                }

                yield return Part.Of(child, null);
            }

            if (!placed && title.Length > 0)
            {
                yield return Part.New(Title(title));
            }
        }

        // QuickTime's keyed metadata ('mdta'): the item of the title key replaced by the new title, or removed for ""; a
        // title is not added where there was none (the key list stays as it is)
        private IEnumerable<Part> KeyedTitle(Mp4Box meta)
        {
            if (file.MetaHandler(meta) != "mdta")
            {
                foreach (var child in meta.Children)
                {
                    yield return Part.Of(child, null);
                }

                yield break;
            }

            var keys = file.TitleKeys(meta);
            var title = change.Title!;
            foreach (var child in meta.Children)
            {
                yield return child.Type != "ilst" || keys.Count == 0 ? Part.Of(child, null) : Part.Of(child, ilst => KeyedItems(ilst, keys, title));
            }
        }

        private IEnumerable<Part> KeyedItems(Mp4Box ilst, IReadOnlySet<uint> keys, string title)
        {
            foreach (var item in ilst.Children)
            {
                var index = file.ItemIndex(item);
                if (!keys.Contains(index))
                {
                    yield return Part.Of(item, null);
                }
                else if (title.Length > 0)
                {
                    var type = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(type, index);
                    yield return Part.New(Box(type, Box("data", Join([0, 0, 0, 1, 0, 0, 0, 0], Encoding.UTF8.GetBytes(title)))));
                }
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

            // the bytes before the first child (the 4 of 'meta') and after the last one (QuickTime's zeros) stay as they are;
            // a box with no child but zeros (QuickTime's end of a list) gets its new children before them
            var original = box.Children;
            var firstChild = original.Count > 0 ? original[0].Start : box.End;
            var onlyZeros = original.Count == 0 && box.Type != "meta" && _bytes.AsSpan((int)(box.ContentStart - _start), (int)box.ContentSize).IndexOfAnyExcept((byte)0) < 0;
            if (onlyZeros)
            {
                firstChild = box.ContentStart;
            }

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
            else if (onlyZeros)
            {
                output.Write(_bytes, (int)(box.ContentStart - _start), (int)box.ContentSize);
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

        private static byte[] Box(string type, ReadOnlySpan<byte> content) => Box(Mp4Reader.TypeBytes(type), content);

        private static byte[] Box(byte[] type, ReadOnlySpan<byte> content)
        {
            var box = new byte[8 + content.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            type.CopyTo(box, 4);
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
