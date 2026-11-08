using System.Buffers.Binary;
using Universum.Core.Media;

namespace Universum.Core.Mp4;

/// <summary>An MP4 file as read: its top-level boxes, its movie box (in memory) and the tracks in it.</summary>
public sealed class Mp4File
{
    internal Mp4File(long length, IReadOnlyList<Mp4Box> boxes, Mp4Box movie, byte[] movieBytes, IReadOnlyList<Mp4Track> tracks, string? brand)
    {
        Brand = brand;
        Length = length;
        Boxes = boxes;
        Movie = movie;
        MovieBytes = movieBytes;
        Tracks = tracks;
    }

    /// <summary>The size of the file.</summary>
    public long Length { get; }

    /// <summary>The boxes at the top of the file ('ftyp', 'mdat', 'moov', 'free' …).</summary>
    public IReadOnlyList<Mp4Box> Boxes { get; }

    /// <summary>The movie box with all its boxes.</summary>
    public Mp4Box Movie { get; }

    /// <summary>The movie box as it is in the file (header included).</summary>
    internal byte[] MovieBytes { get; }

    public IReadOnlyList<Mp4Track> Tracks { get; }

    /// <summary>The media data come in fragments ('moof' boxes; a recording or a stream) – their tables are not in 'moov'.</summary>
    public bool IsFragmented => Movie.Find("mvex") is not null || Boxes.Any(b => b.Type == "moof");

    /// <summary>The movie box lies after the media data (no "fast start").</summary>
    public bool MovieAtEnd => Boxes.FirstOrDefault(b => b.Type == "mdat") is { } data && data.Start < Movie.Start;

    /// <summary>The last box says no size – it runs to the end of the file (a new movie after it would be swallowed).</summary>
    public bool LastBoxOpenEnded { get; internal init; }

    /// <summary>The brand of the file ('isom', 'mp42', 'qt  ' …), or null.</summary>
    public string? Brand { get; }

    /// <summary>
    /// The title of the film: iTunes '©nam' (in any 'meta' of 'udta'), else QuickTime's keyed one ("title",
    /// "com.apple.quicktime.title"), else QuickTime's '©nam'; null when none.
    /// </summary>
    public string? Title
    {
        get
        {
            var metas = (Movie.Find("udta")?.Children ?? []).Concat(Movie.Children).Where(b => b.Type == "meta").ToList();
            foreach (var meta in metas.Where(m => MetaHandler(m) != "mdta"))
            {
                if (meta.Find("ilst/©nam/data") is { ContentSize: >= 8 } data)
                {
                    return Mp4Reader.Utf8(Mp4Reader.Content(MovieBytes, Movie.Start, data)[8..]);
                }
            }

            foreach (var meta in metas.Where(m => MetaHandler(m) == "mdta"))
            {
                var titles = TitleKeys(meta);
                if (meta.Find("ilst")?.Children.FirstOrDefault(item => titles.Contains(ItemIndex(item))) is { } item &&
                    item.Find("data") is { ContentSize: >= 8 } keyed)
                {
                    return Mp4Reader.Utf8(Mp4Reader.Content(MovieBytes, Movie.Start, keyed)[8..]);
                }
            }

            // QuickTime: a 16-bit length, a 16-bit language, the text
            if (Movie.Find("udta/©nam") is { ContentSize: >= 4 } name)
            {
                var content = Mp4Reader.Content(MovieBytes, Movie.Start, name);
                var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(content), content.Length - 4);
                return Mp4Reader.Utf8(content.Slice(4, length));
            }

            return null;
        }
    }

    /// <summary>The handler of a 'meta' box ("mdir" iTunes, "mdta" QuickTime's keyed metadata …), or "".</summary>
    internal string MetaHandler(Mp4Box meta) =>
        meta.Find("hdlr") is { ContentSize: >= 12 } hdlr ? Mp4Reader.TypeName(Mp4Reader.Content(MovieBytes, Movie.Start, hdlr).Slice(8, 4)) : "";

    /// <summary>The numbers of the keys of a keyed 'meta' that name the title ("title", "com.apple.quicktime.title").</summary>
    internal IReadOnlySet<uint> TitleKeys(Mp4Box meta)
    {
        var titles = new HashSet<uint>();
        if (meta.Find("keys") is not { ContentSize: >= 8 } keys)
        {
            return titles;
        }

        var bytes = new Mp4Bytes(Mp4Reader.Content(MovieBytes, Movie.Start, keys), keys.ContentStart);
        bytes.Skip(4);
        var count = bytes.U32();
        for (uint index = 1; index <= count && bytes.Left >= 8; index++)
        {
            var size = (int)bytes.U32();
            if (size < 8 || size - 4 > bytes.Left)
            {
                break;
            }

            bytes.Skip(4); // the namespace ('mdta')
            var name = System.Text.Encoding.UTF8.GetString(bytes.Take(size - 8));
            if (name is "title" or "com.apple.quicktime.title")
            {
                titles.Add(index);
            }
        }

        return titles;
    }

    /// <summary>The number of the key an item of a keyed list stands for (its type is the number).</summary>
    internal uint ItemIndex(Mp4Box item) => BinaryPrimitives.ReadUInt32BigEndian(MovieBytes.AsSpan((int)(item.Start - Movie.Start) + 4, 4));
}

/// <summary>The kind of a track by its handler.</summary>
public enum Mp4TrackKind
{
    Other,
    Video,
    Audio,
    Subtitles,
}

/// <summary>One track of an MP4 file.</summary>
public sealed class Mp4Track
{
    private readonly Mp4Box _box;
    private readonly byte[] _movie;
    private readonly long _movieStart;
    private Mp4SampleTable? _samples;

    private Mp4Track(Mp4Box box, byte[] movie, long movieStart)
    {
        _box = box;
        _movie = movie;
        _movieStart = movieStart;
    }

    /// <summary>The track's box in the movie.</summary>
    public Mp4Box Box => _box;

    /// <summary>The track's ID ('tkhd').</summary>
    public int Id { get; private init; }

    /// <summary>The handler: "vide", "soun", "subt", "text", "sbtl", "clcp", "tmcd" …</summary>
    public string Handler { get; private init; } = "";

    public Mp4TrackKind Kind => Handler switch
    {
        "vide" => Mp4TrackKind.Video,
        "soun" => Mp4TrackKind.Audio,
        "subt" or "text" or "sbtl" or "clcp" => Mp4TrackKind.Subtitles,
        _ => Mp4TrackKind.Other,
    };

    /// <summary>The handler's name ("SoundHandler", or a track name some programs put there).</summary>
    public string HandlerName { get; private init; } = "";

    /// <summary>The track's name ('udta/name'), or null.</summary>
    public string? Name { get; private init; }

    /// <summary>
    /// The name players show: 'udta/name', else the handler's name unless it is only the program's usual one
    /// ("SoundHandler", "Core Media Audio", "GPAC ISO Audio Handler" …); null when there is none.
    /// </summary>
    public string? DisplayName => Name is { Length: > 0 } name ? name : IsUsualHandlerName(HandlerName.TrimStart(Controls)) ? null : HandlerName.TrimStart(Controls);

    // the names programs give every handler ("SoundHandler", Android's "SoundHandle", "Core Media Audio", "GPAC ISO Audio
    // Handler", "ISO Media file produced by …"); a name written as a length and the text in an ISO file keeps its length
    // byte when read as FFmpeg reads it – it is not part of the name
    private static readonly char[] Controls = [.. Enumerable.Range(0, 32).Select(c => (char)c)];

    private static bool IsUsualHandlerName(string name) =>
        name.Length == 0 || name.Contains("Handle", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Core Media", StringComparison.Ordinal) ||
        name.StartsWith("ISO Media", StringComparison.OrdinalIgnoreCase);

    /// <summary>The track's references to other tracks ('tref'): the type ("chap", "forc" …) and the IDs.</summary>
    public IReadOnlyList<(string Type, IReadOnlyList<int> Ids)> References { get; private init; } = [];

    /// <summary>The 'kind' boxes of the track's 'udta': (scheme, value).</summary>
    public IReadOnlyList<(string Scheme, string Value)> Kinds { get; private init; } = [];

    /// <summary>The roles of MPEG-DASH the track has ("forced-subtitle", "caption", "commentary" …; <see cref="Mp4Roles"/>).</summary>
    public IReadOnlyList<string> Roles => [.. Kinds.Where(k => k.Scheme == Mp4Roles.Scheme).Select(k => k.Value)];

    /// <summary>The language of 'mdhd' (ISO 639-2, "und" when none).</summary>
    public string Language { get; private init; } = "und";

    /// <summary>The extended language ('elng', BCP 47), or null.</summary>
    public string? ExtendedLanguage { get; private init; }

    /// <summary>The language as UNIVERSUM keeps it.</summary>
    public LanguageTag LanguageTag => ExtendedLanguage is { Length: > 0 } extended ? LanguageTag.FromCode(extended) : LanguageTag.FromCode(Language);

    /// <summary>'tkhd' flags: 1 enabled (the default track of its kind), 2 in the movie, 4 in the preview.</summary>
    public uint Flags { get; private init; }

    public bool Enabled => (Flags & 1) != 0;

    /// <summary>The group of tracks that are alternatives of each other (0: none).</summary>
    public int AlternateGroup { get; private init; }

    /// <summary>The codec by its sample entry ("avc1", "hvc1", "mp4a", "ac-3", "tx3g" …).</summary>
    public string Codec { get; private init; } = "";

    /// <summary>Sample entries in 'stsd' (more than one: the codec changes within the track).</summary>
    public int SampleEntries { get; private init; }

    public uint Timescale { get; private init; }

    /// <summary>The duration in <see cref="Timescale"/> units ('mdhd').</summary>
    public ulong Duration { get; private init; }

    public int Width { get; private init; }

    public int Height { get; private init; }

    public int Channels { get; private init; }

    public int SampleRate { get; private init; }

    /// <summary>The edit list: (duration in the movie's timescale, media time; −1 an empty edit).</summary>
    public IReadOnlyList<(ulong Duration, long MediaTime)> Edits { get; private init; } = [];

    /// <summary>The samples – where each lies, how large, its decoding and presentation time, whether a key frame.</summary>
    public Mp4SampleTable Samples => _samples ??= Mp4SampleTable.Read(_box.Find("mdia/minf/stbl") ?? throw new Mp4FormatException($"track {Id} has no sample table", _box.Start), _movie, _movieStart, Id);

    internal static Mp4Track From(Mp4Box trak, byte[] movie, long movieStart, bool quickTime)
    {
        var tkhd = trak.Find("tkhd") ?? throw new Mp4FormatException("a track without 'tkhd'", trak.Start);
        var header = new Mp4Bytes(Mp4Reader.Content(movie, movieStart, tkhd), tkhd.ContentStart);
        var version = header.U8();
        var flags = header.U24();
        header.Skip(version == 1 ? 16 : 8);
        var id = (int)header.U32();
        header.Skip(4);
        header.Skip(version == 1 ? 8 : 4);
        header.Skip(8);
        header.Skip(2); // layer
        var alternate = header.U16();
        header.Skip(2 + 2 + 36);
        var width = (int)(header.U32() >> 16);
        var height = (int)(header.U32() >> 16);

        var mdhd = trak.Find("mdia/mdhd") ?? throw new Mp4FormatException($"track {id} has no 'mdhd'", trak.Start);
        var media = new Mp4Bytes(Mp4Reader.Content(movie, movieStart, mdhd), mdhd.ContentStart);
        var mediaVersion = media.U8();
        media.Skip(3);
        media.Skip(mediaVersion == 1 ? 16 : 8);
        var timescale = media.U32();
        var duration = mediaVersion == 1 ? media.U64() : media.U32();
        var language = Mp4Languages.Unpack(media.U16());

        var hdlr = trak.Find("mdia/hdlr") ?? throw new Mp4FormatException($"track {id} has no 'hdlr'", trak.Start);
        var handler = new Mp4Bytes(Mp4Reader.Content(movie, movieStart, hdlr), hdlr.ContentStart);
        handler.Skip(8);
        var handlerType = handler.FourCc();
        handler.Skip(12);
        var handlerName = HandlerText(handler.Take(handler.Left), quickTime);

        string? elng = null;
        if (trak.Find("mdia/elng") is { } extended)
        {
            var bytes = new Mp4Bytes(Mp4Reader.Content(movie, movieStart, extended), extended.ContentStart);
            bytes.Skip(4);
            elng = bytes.ZeroEnded(64);
        }

        string? name = null;
        if (trak.Find("udta/name") is { } named)
        {
            name = Mp4Reader.Utf8(Mp4Reader.Content(movie, movieStart, named));
        }

        var kinds = new List<(string, string)>();
        foreach (var kind in trak.Find("udta")?.Children.Where(c => c.Type == "kind") ?? [])
        {
            var bytes = new Mp4Bytes(Mp4Reader.Content(movie, movieStart, kind), kind.ContentStart);
            if (bytes.Left > 4)
            {
                bytes.Skip(4);
                var scheme = bytes.ZeroEnded(256);
                kinds.Add((scheme, bytes.ZeroEnded(256)));
            }
        }

        var references = new List<(string, IReadOnlyList<int>)>();
        foreach (var reference in trak.Find("tref")?.Children ?? [])
        {
            var bytes = new Mp4Bytes(Mp4Reader.Content(movie, movieStart, reference), reference.ContentStart);
            var ids = new List<int>();
            while (bytes.Left >= 4)
            {
                ids.Add((int)bytes.U32());
            }

            references.Add((reference.Type, ids));
        }

        var (codec, entries, entryWidth, entryHeight, channels, sampleRate) = SampleEntry(trak, movie, movieStart, id, handlerType);
        return new Mp4Track(trak, movie, movieStart)
        {
            Id = id,
            Handler = handlerType,
            HandlerName = handlerName,
            Name = name,
            Kinds = kinds,
            References = references,
            Language = language,
            ExtendedLanguage = elng,
            Flags = flags,
            AlternateGroup = alternate,
            Codec = codec,
            SampleEntries = entries,
            Timescale = timescale,
            Duration = duration,
            Width = width > 0 ? width : entryWidth,
            Height = height > 0 ? height : entryHeight,
            Channels = channels,
            SampleRate = sampleRate,
            Edits = EditList(trak, movie, movieStart),
        };
    }

    /// <summary>A handler's name is written as QuickTime does – its length first – when FFmpeg reads it so (owner, 8. 11. 2026).</summary>
    internal static bool IsPascal(ReadOnlySpan<byte> name, bool quickTime) => quickTime && name.Length > 1 && name[0] == name.Length - 1;

    // ISO: a string ended by zero; QuickTime: its length in the first byte
    private static string HandlerText(ReadOnlySpan<byte> bytes, bool quickTime)
    {
        if (IsPascal(bytes, quickTime))
        {
            return Mp4Reader.Utf8(bytes[1..]);
        }

        var end = bytes.IndexOf((byte)0);
        return Mp4Reader.Utf8(end < 0 ? bytes : bytes[..end]);
    }

    // the first sample entry: the codec, and the size of the picture or the channels and rate of the sound
    private static (string Codec, int Entries, int Width, int Height, int Channels, int SampleRate) SampleEntry(Mp4Box trak, byte[] movie, long movieStart, int id, string handler)
    {
        var stsd = trak.Find("mdia/minf/stbl/stsd") ?? throw new Mp4FormatException($"track {id} has no 'stsd'", trak.Start);
        var bytes = new Mp4Bytes(Mp4Reader.Content(movie, movieStart, stsd), stsd.ContentStart);
        bytes.Skip(4);
        var count = (int)bytes.U32();
        if (count == 0)
        {
            return ("", 0, 0, 0, 0, 0);
        }

        var size = bytes.U32();
        var codec = bytes.FourCc();
        if (size < 8 || size - 8 > bytes.Left)
        {
            throw new Mp4FormatException($"the sample entry of track {id} does not fit 'stsd'", bytes.FilePosition);
        }

        var entry = new Mp4Bytes(bytes.Take((int)size - 8), bytes.FilePosition - (size - 8));
        if (handler == "vide" && entry.Left >= 28)
        {
            entry.Skip(24);
            return (codec, count, entry.U16(), entry.U16(), 0, 0);
        }

        if (handler == "soun" && entry.Left >= 28)
        {
            entry.Skip(8);
            var soundVersion = entry.U16();
            entry.Skip(6);
            int channels = entry.U16();
            entry.Skip(6);
            var rate = (int)(entry.U32() >> 16);
            if (soundVersion == 2 && entry.Left >= 20)
            {
                // QuickTime sound description version 2: the rate as a 64-bit float, the channels as 32 bits
                entry.Skip(4);
                rate = (int)BitConverter.Int64BitsToDouble((long)entry.U64());
                channels = (int)entry.U32();
            }

            return (codec, count, 0, 0, channels, rate);
        }

        return (codec, count, 0, 0, 0, 0);
    }

    private static IReadOnlyList<(ulong, long)> EditList(Mp4Box trak, byte[] movie, long movieStart)
    {
        if (trak.Find("edts/elst") is not { } elst)
        {
            return [];
        }

        var bytes = new Mp4Bytes(Mp4Reader.Content(movie, movieStart, elst), elst.ContentStart);
        var version = bytes.U8();
        bytes.Skip(3);
        var count = bytes.U32();
        if (count > (uint)bytes.Left / (version == 1 ? 20 : 12))
        {
            throw new Mp4FormatException("the edit list claims more entries than it has", elst.Start);
        }

        var edits = new List<(ulong, long)>((int)count);
        for (var i = 0; i < count; i++)
        {
            var duration = version == 1 ? bytes.U64() : bytes.U32();
            var time = version == 1 ? (long)bytes.U64() : bytes.I32();
            bytes.Skip(4); // the rate
            edits.Add((duration, time));
        }

        return edits;
    }
}
