namespace Universum.Core.MkvToolNix;

/// <summary>
/// The index (Cues) of a Matroska file: the times of the cue points of every track, by the track number. Only the
/// header, the seek head and the Cues element are read – a few kilobytes, never the film itself. mkvmerge indexes every
/// subtitle, so the index tells where the subtitles of a track are (forced ones too) and how many of them a part of the
/// film holds (owner, 30. 10. 2026: subtitles are read in a few parts, like the audio).
/// </summary>
public static class MatroskaCues
{
    private const uint EbmlHeader = 0x1A45DFA3;
    private const uint Segment = 0x18538067;
    private const uint SeekHead = 0x114D9B74;
    private const uint Seek = 0x4DBB;
    private const uint SeekIdentifier = 0x53AB;
    private const uint SeekPosition = 0x53AC;
    private const uint Info = 0x1549A966;
    private const uint TimestampScale = 0x2AD7B1;
    private const uint Cues = 0x1C53BB6B;
    private const uint CuePoint = 0xBB;
    private const uint CueTime = 0xB3;
    private const uint CueTrackPositions = 0xB7;
    private const uint CueTrack = 0xF7;
    private const uint CueDuration = 0xB2;
    private const uint Cluster = 0x1F43B675;

    // an index larger than this is not an index (a damaged file)
    private const long MaximumCues = 64L * 1024 * 1024;

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<TimeSpan>> None = new Dictionary<int, IReadOnlyList<TimeSpan>>();

    /// <summary>The cue times of every track (key = Matroska track number), sorted; empty when the file has no readable index.</summary>
    public static Task<IReadOnlyDictionary<int, IReadOnlyList<TimeSpan>>> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
                return Read(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return None;
            }
        }, cancellationToken);

    /// <summary>The cue times of every track of a Matroska stream (key = track number); empty without an index.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<TimeSpan>> Read(Stream stream) =>
        Entries(stream).ToDictionary(t => t.Key, t => (IReadOnlyList<TimeSpan>)t.Value.Select(e => e.Start).ToList());

    /// <summary>
    /// When the subtitles of every track are shown, by the index alone (mkvmerge writes how long each subtitle lasts;
    /// without it two seconds) – the timing of a subtitle track without reading the film (automatic sync).
    /// </summary>
    public static Task<IReadOnlyDictionary<int, IReadOnlyList<(TimeSpan Start, TimeSpan End)>>> ReadIntervalsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
                return (IReadOnlyDictionary<int, IReadOnlyList<(TimeSpan Start, TimeSpan End)>>)Entries(stream).ToDictionary(
                    t => t.Key, t => (IReadOnlyList<(TimeSpan Start, TimeSpan End)>)t.Value.Select(e => (e.Start, e.Start + (e.Duration ?? TimeSpan.FromSeconds(2)))).ToList());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new Dictionary<int, IReadOnlyList<(TimeSpan Start, TimeSpan End)>>();
            }
        }, cancellationToken);

    // the cue points of every track (key = track number), sorted by time: the time and the duration when the index has it
    private static Dictionary<int, List<(TimeSpan Start, TimeSpan? Duration)>> Entries(Stream stream)
    {
        if (Header(stream, 0) is not { Id: EbmlHeader, Size: { } headerSize } header ||
            Header(stream, header.Data + (long)headerSize) is not { Id: Segment } segment)
        {
            return [];
        }

        var start = segment.Data;
        var end = segment.Size is { } size ? Math.Min(stream.Length, start + (long)Math.Min(size, long.MaxValue / 2)) : stream.Length;
        long? cues = null, info = null;
        var heads = new Queue<long>();
        var seen = new HashSet<long>();

        // the top-level elements before the first cluster (the index of an mkvmerge file is found by the seek head)
        for (var position = start; position < end;)
        {
            if (Header(stream, position) is not { Size: { } length } element || element.Id == Cluster)
            {
                break;
            }

            switch (element.Id)
            {
                case SeekHead:
                    heads.Enqueue(position);
                    break;
                case Info:
                    info ??= position;
                    break;
                case Cues:
                    cues ??= position;
                    break;
            }

            position = element.Data + (long)length;
        }

        // the seek heads (one may point to another at the end of the file)
        while (heads.Count > 0)
        {
            var position = heads.Dequeue();
            if (!seen.Add(position) || Body(stream, position, SeekHead, 1024 * 1024) is not { } body)
            {
                continue;
            }

            foreach (var seek in Children(body, 0, body.Length).Where(c => c.Id == Seek))
            {
                uint? id = null;
                long? at = null;
                foreach (var child in Children(body, seek.Start, seek.Start + seek.Length))
                {
                    if (child.Id == SeekIdentifier)
                    {
                        id = (uint)Unsigned(body, child.Start, child.Length);
                    }
                    else if (child.Id == SeekPosition)
                    {
                        at = start + (long)Unsigned(body, child.Start, child.Length);
                    }
                }

                switch (id)
                {
                    case Cues when at is { } cuesAt:
                        cues ??= cuesAt;
                        break;
                    case Info when at is { } infoAt:
                        info ??= infoAt;
                        break;
                    case SeekHead when at is { } headAt:
                        heads.Enqueue(headAt);
                        break;
                }
            }
        }

        var scale = 1_000_000UL;
        if (info is { } infoPosition && Body(stream, infoPosition, Info, 1024 * 1024) is { } infoBody &&
            Children(infoBody, 0, infoBody.Length).FirstOrDefault(c => c.Id == TimestampScale) is { Length: > 0 } scaleElement)
        {
            scale = Unsigned(infoBody, scaleElement.Start, scaleElement.Length);
        }

        if (cues is not { } cuesPosition || Body(stream, cuesPosition, Cues, MaximumCues) is not { } index)
        {
            return [];
        }

        var entries = new Dictionary<int, List<(TimeSpan Start, TimeSpan? Duration)>>();
        TimeSpan Time(ulong ticks) => TimeSpan.FromTicks((long)Math.Min((decimal)long.MaxValue, (decimal)ticks * scale / 100));
        foreach (var point in Children(index, 0, index.Length).Where(c => c.Id == CuePoint))
        {
            ulong? time = null;
            var positions = new List<(int Track, ulong? Duration)>();
            foreach (var child in Children(index, point.Start, point.Start + point.Length))
            {
                if (child.Id == CueTime)
                {
                    time = Unsigned(index, child.Start, child.Length);
                }
                else if (child.Id == CueTrackPositions)
                {
                    int? track = null;
                    ulong? duration = null;
                    foreach (var part in Children(index, child.Start, child.Start + child.Length))
                    {
                        if (part.Id == CueTrack)
                        {
                            track = (int)Unsigned(index, part.Start, part.Length);
                        }
                        else if (part.Id == CueDuration)
                        {
                            duration = Unsigned(index, part.Start, part.Length);
                        }
                    }

                    if (track is { } number)
                    {
                        positions.Add((number, duration));
                    }
                }
            }

            if (time is not { } ticks)
            {
                continue;
            }

            foreach (var (track, duration) in positions)
            {
                if (!entries.TryGetValue(track, out var list))
                {
                    entries[track] = list = [];
                }

                list.Add((Time(ticks), duration is { } length ? Time(length) : null));
            }
        }

        foreach (var list in entries.Values)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        return entries;
    }

    // the ID, the size (null = unknown) and where the data starts, of the element at the position; null past the end
    private static (uint Id, ulong? Size, long Data)? Header(Stream stream, long position)
    {
        if (position < 0 || position + 2 > stream.Length)
        {
            return null;
        }

        var bytes = new byte[12];
        stream.Position = position;
        var read = stream.ReadAtLeast(bytes, 2, throwOnEndOfStream: false);
        var idLength = Length(bytes[0], 4);
        if (idLength == 0 || idLength >= read)
        {
            return null;
        }

        var sizeLength = Length(bytes[idLength], 8);
        if (sizeLength == 0 || idLength + sizeLength > read)
        {
            return null;
        }

        uint id = 0;
        for (var i = 0; i < idLength; i++)
        {
            id = (id << 8) | bytes[i];
        }

        return (id, Size(bytes, idLength, sizeLength), position + idLength + sizeLength);
    }

    // the data of the element at the position when it is the expected one and not too large
    private static byte[]? Body(Stream stream, long position, uint id, long maximum)
    {
        if (Header(stream, position) is not { Size: { } size } element || element.Id != id || (long)size > maximum || element.Data + (long)size > stream.Length)
        {
            return null;
        }

        var body = new byte[(int)size];
        stream.Position = element.Data;
        stream.ReadExactly(body);
        return body;
    }

    // the child elements in a part of a buffer (an unknown or overlong size ends the list)
    private static IEnumerable<(uint Id, int Start, int Length)> Children(byte[] buffer, int start, int end)
    {
        var position = start;
        while (position < end)
        {
            var idLength = Length(buffer[position], 4);
            if (idLength == 0 || position + idLength >= end)
            {
                yield break;
            }

            var sizeLength = Length(buffer[position + idLength], 8);
            if (sizeLength == 0 || position + idLength + sizeLength > end)
            {
                yield break;
            }

            uint id = 0;
            for (var i = 0; i < idLength; i++)
            {
                id = (id << 8) | buffer[position + i];
            }

            var data = position + idLength + sizeLength;
            if (Size(buffer, position + idLength, sizeLength) is not { } size || (ulong)(end - data) < size)
            {
                yield break;
            }

            yield return (id, data, (int)size);
            position = data + (int)size;
        }
    }

    // the length of a variable-size number from its first byte (the place of the first set bit); 0 when invalid
    private static int Length(byte first, int maximum)
    {
        for (var length = 1; length <= maximum; length++)
        {
            if ((first & (0x80 >> (length - 1))) != 0)
            {
                return length;
            }
        }

        return 0;
    }

    // an element size without its length marker; null when all its bits are set (unknown size)
    private static ulong? Size(byte[] bytes, int start, int length)
    {
        var value = (ulong)(bytes[start] & (0xFF >> length));
        var unknown = value == (ulong)(0xFF >> length);
        for (var i = 1; i < length; i++)
        {
            value = (value << 8) | bytes[start + i];
            unknown &= bytes[start + i] == 0xFF;
        }

        return unknown ? null : value;
    }

    private static ulong Unsigned(byte[] buffer, int start, int length)
    {
        ulong value = 0;
        for (var i = 0; i < Math.Min(length, 8); i++)
        {
            value = (value << 8) | buffer[start + i];
        }

        return value;
    }
}
