using System.Buffers.Binary;
using System.Globalization;
using Universum.Core.Text;

namespace Universum.Core.Subtitles.Pictures;

/// <summary>
/// Reads VobSub subtitles (DVD; the ".idx" text with its ".sub" picture data – mkvextract writes the tracks of MKV files
/// so) by UNIVERSUM itself (owner, 1. 11. 2026: own code, as the PGS of a Blu-ray). The ".idx" tells the size of the
/// picture, the 16 colours and where each subtitle of each language starts in the ".sub"; the ".sub" is an MPEG program
/// stream whose private packets carry the subtitle units: two-bit pixels coded in runs of nibbles (the lines of the
/// two fields apart) and control sequences (when to show and hide, which four colours, how transparent, where). Each
/// subtitle becomes a <see cref="PictureCue"/> whose pixels are coded the PGS way, so the player and the reading of
/// text show and read it as a Blu-ray one. A damaged part is skipped; reading never fails on the content.
/// </summary>
public static class VobSubReader
{
    private const int MaximumSide = 4096;
    private const int MaximumUnitBytes = 65535;

    /// <summary>A ".sub" larger than this is not read.</summary>
    public const long MaximumFileBytes = 1L << 30;

    /// <summary>A language of an ".idx": its index (the stream in the ".sub"), code ("en", "cs") and how many subtitles it has.</summary>
    public sealed record VobSubStream(int Index, string Language, int Count);

    /// <summary>The languages (streams) an ".idx" holds.</summary>
    public static IReadOnlyList<VobSubStream> Streams(string idx) =>
        Parse(idx).Streams.Select(s => new VobSubStream(s.Index, s.Language, s.Times.Count)).ToList();

    /// <summary>
    /// The subtitles of an ".idx" file and the ".sub" beside it (read away from the caller's thread); the first stream
    /// with subtitles unless <paramref name="stream"/> is given. <see cref="InvalidDataException"/> when the ".sub" is
    /// missing or too large.
    /// </summary>
    public static async Task<IReadOnlyList<PictureCue>> ReadFilesAsync(string idxPath, int? stream = null, CancellationToken cancellationToken = default)
    {
        var subPath = Path.ChangeExtension(idxPath, ".sub");
        if (!File.Exists(subPath))
        {
            throw new InvalidDataException(CoreText.F("Chýba obrazová časť titulkov VobSub: {0}", Path.GetFileName(subPath)));
        }

        if (new FileInfo(subPath).Length > MaximumFileBytes)
        {
            throw new InvalidDataException(CoreText.T("Súbor obrázkových titulkov je príliš veľký."));
        }

        var idx = await File.ReadAllTextAsync(idxPath, cancellationToken).ConfigureAwait(false);
        var sub = await File.ReadAllBytesAsync(subPath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Read(idx, sub, stream, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The subtitles of one stream of VobSub data (the first one with subtitles when <paramref name="stream"/> is null).</summary>
    public static IReadOnlyList<PictureCue> Read(string idx, ReadOnlySpan<byte> sub, int? stream = null, CancellationToken cancellationToken = default)
    {
        var header = Parse(idx);
        var chosen = stream is { } wanted ? header.Streams.FirstOrDefault(s => s.Index == wanted) : header.Streams.FirstOrDefault(s => s.Times.Count > 0);
        if (chosen is null || header.Width is <= 0 or > MaximumSide || header.Height is <= 0 or > MaximumSide)
        {
            return [];
        }

        var units = new List<(TimeSpan At, Unit Unit)>();
        foreach (var (time, position) in chosen.Times)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Gather(sub, position, chosen.Index) is { } data && Decode(data) is { } unit)
            {
                units.Add((time + chosen.Delay, unit));
            }
        }

        units.Sort((a, b) => a.At.CompareTo(b.At));
        var cues = new List<PictureCue>(units.Count);
        for (var i = 0; i < units.Count; i++)
        {
            var (at, unit) = units[i];
            var start = at + unit.Show;
            // no time to hide: until the next subtitle, at most five seconds
            var next = i + 1 < units.Count ? units[i + 1].At : (TimeSpan?)null;
            var end = unit.Hide is { } hide && hide > unit.Show ? at + hide : start + TimeSpan.FromSeconds(5);
            if (next is { } following && end > following)
            {
                end = following;
            }

            if (end <= start || unit.Width <= 0 || unit.Height <= 0)
            {
                continue;
            }

            var palette = new uint[256];
            for (var c = 0; c < 4; c++)
            {
                var rgb = header.Palette[unit.Colours[c] & 15];
                var alpha = (uint)(unit.Alphas[c] * 17);
                palette[c] = alpha == 0 ? 0 : alpha << 24 | (rgb & 0xFFFFFF);
            }

            var x = Math.Max(0, unit.X + header.OriginX);
            var y = Math.Max(0, unit.Y + header.OriginY);
            cues.Add(new PictureCue(start, end, header.Width, header.Height, [new PictureObject(x, y, unit.Width, unit.Height, unit.Rle)], palette));
        }

        return cues;
    }

    // the ".idx": size, origin, palette and, per language ("id: en, index: 0"), its delay and the subtitles' times and places
    private sealed record IdxStream(int Index, string Language, TimeSpan Delay, List<(TimeSpan Time, long Position)> Times);

    private sealed record IdxHeader(int Width, int Height, int OriginX, int OriginY, uint[] Palette, List<IdxStream> Streams);

    private static IdxHeader Parse(string idx)
    {
        int width = 720, height = 576, originX = 0, originY = 0;
        var palette = new uint[16];
        for (var i = 0; i < 16; i++)
        {
            palette[i] = i == 0 ? 0x000000u : 0xFFFFFFu;
        }

        var streams = new List<IdxStream>();
        IdxStream? current = null;
        foreach (var raw in idx.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "size" when value.Split('x') is [var w, var h] && int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWidth) && int.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHeight):
                    (width, height) = (parsedWidth, parsedHeight);
                    break;
                case "org" when value.Split(',') is [var ox, var oy] && int.TryParse(ox, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedX) && int.TryParse(oy, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedY):
                    (originX, originY) = (parsedX, parsedY);
                    break;
                case "palette":
                    var colours = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i < Math.Min(16, colours.Length); i++)
                    {
                        if (uint.TryParse(colours[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var colour))
                        {
                            palette[i] = colour & 0xFFFFFF;
                        }
                    }

                    break;
                case "id":
                    // "id: en, index: 0"
                    var language = value.Split(',')[0].Trim();
                    var index = streams.Count;
                    var at = value.IndexOf("index:", StringComparison.OrdinalIgnoreCase);
                    if (at >= 0 && int.TryParse(value[(at + 6)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var given))
                    {
                        index = given;
                    }

                    current = new IdxStream(index, language, TimeSpan.Zero, []);
                    streams.Add(current);
                    break;
                case "delay" when current is not null && Timestamp(value) is { } delay:
                    streams[^1] = current = current with { Delay = delay };
                    break;
                case "timestamp" when Entry(value) is { } entry:
                    if (current is null)
                    {
                        current = new IdxStream(0, "", TimeSpan.Zero, []);
                        streams.Add(current);
                    }

                    current.Times.Add(entry);
                    break;
            }
        }

        return new IdxHeader(width, height, originX, originY, palette, streams);
    }

    // "00:00:01:101, filepos: 000000000"
    private static (TimeSpan, long)? Entry(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || Timestamp(parts[0]) is not { } time)
        {
            return null;
        }

        var position = parts[1].IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 ? parts[1][(colon + 1)..].Trim() : "";
        return long.TryParse(position, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var at) && at >= 0 ? (time, at) : null;
    }

    // "hh:mm:ss:mmm" (a minus sign allowed for a delay)
    private static TimeSpan? Timestamp(string value)
    {
        var negative = value.StartsWith('-');
        var parts = value.TrimStart('-', '+').Split(':');
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return null;
        }

        var n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var time = new TimeSpan(0, n[0], n[1], n[2], n[3]);
        return negative ? -time : time;
    }

    // the subtitle unit that starts at a place of the ".sub": the payloads of the private packets of its stream, joined
    // until the unit's own length (its first two bytes)
    private static byte[]? Gather(ReadOnlySpan<byte> sub, long position, int stream)
    {
        if (position < 0 || position >= sub.Length)
        {
            return null;
        }

        var unit = new List<byte>();
        var expected = -1;
        var at = (int)position;
        var packets = 0;
        while (at + 4 <= sub.Length && packets++ < 4096)
        {
            if (sub[at] != 0 || sub[at + 1] != 0 || sub[at + 2] != 1)
            {
                // lost: the next start code
                at++;
                continue;
            }

            var code = sub[at + 3];
            if (code == 0xBA)
            {
                // pack header: MPEG-2 (14 bytes and stuffing) or MPEG-1 (12 bytes)
                if (at + 14 > sub.Length)
                {
                    break;
                }

                at += (sub[at + 4] & 0xC0) == 0x40 ? 14 + (sub[at + 13] & 7) : 12;
                continue;
            }

            if (at + 6 > sub.Length)
            {
                break;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(sub[(at + 4)..]);
            var end = at + 6 + length;
            if (code != 0xBD)
            {
                // padding, video or another stream: passed over
                at = end;
                continue;
            }

            if (end > sub.Length || at + 9 > sub.Length)
            {
                break;
            }

            // MPEG-2 PES header: two flag bytes and its own length, then the substream (0x20 + the stream index)
            var payload = at + 9 + sub[at + 8];
            if (payload >= end)
            {
                at = end;
                continue;
            }

            var substream = sub[payload];
            if (substream == 0x20 + stream)
            {
                unit.AddRange(sub[(payload + 1)..end]);
                if (expected < 0 && unit.Count >= 2)
                {
                    expected = (unit[0] << 8) | unit[1];
                    if (expected < 4)
                    {
                        return null;
                    }
                }

                if (expected >= 0 && unit.Count >= expected)
                {
                    return unit.Take(expected).ToArray();
                }
            }

            at = end;
        }

        return null;
    }

    private sealed record Unit(int X, int Y, int Width, int Height, byte[] Rle, int[] Colours, int[] Alphas, TimeSpan Show, TimeSpan? Hide);

    // a subtitle unit: its control sequences (when, colours, transparency, area, where the two fields start) and its
    // pixels decoded and coded again the PGS way
    private static Unit? Decode(byte[] unit)
    {
        if (unit.Length < 4 || unit.Length > MaximumUnitBytes)
        {
            return null;
        }

        var control = (unit[2] << 8) | unit[3];
        int[] colours = [0, 1, 2, 3];
        int[] alphas = [0, 15, 15, 15];
        int x1 = -1, x2 = -1, y1 = -1, y2 = -1, top = -1, bottom = -1;
        TimeSpan? show = null, hide = null;
        var visited = new HashSet<int>();
        var at = control;
        while (at + 4 <= unit.Length && visited.Add(at))
        {
            // the delay in units of 1024 ticks of 90 kHz
            var delay = TimeSpan.FromTicks(((unit[at] << 8) | unit[at + 1]) * 1024L * TimeSpan.TicksPerSecond / 90_000);
            var next = (unit[at + 2] << 8) | unit[at + 3];
            var i = at + 4;
            var done = false;
            while (i < unit.Length && !done)
            {
                switch (unit[i++])
                {
                    case 0x00:
                        // forced display
                        show ??= delay;
                        break;
                    case 0x01:
                        show ??= delay;
                        break;
                    case 0x02:
                        hide ??= delay;
                        break;
                    case 0x03 when i + 2 <= unit.Length:
                        colours = [unit[i + 1] & 15, unit[i + 1] >> 4, unit[i] & 15, unit[i] >> 4];
                        i += 2;
                        break;
                    case 0x04 when i + 2 <= unit.Length:
                        alphas = [unit[i + 1] & 15, unit[i + 1] >> 4, unit[i] & 15, unit[i] >> 4];
                        i += 2;
                        break;
                    case 0x05 when i + 6 <= unit.Length:
                        x1 = (unit[i] << 4) | (unit[i + 1] >> 4);
                        x2 = ((unit[i + 1] & 15) << 8) | unit[i + 2];
                        y1 = (unit[i + 3] << 4) | (unit[i + 4] >> 4);
                        y2 = ((unit[i + 4] & 15) << 8) | unit[i + 5];
                        i += 6;
                        break;
                    case 0x06 when i + 4 <= unit.Length:
                        top = (unit[i] << 8) | unit[i + 1];
                        bottom = (unit[i + 2] << 8) | unit[i + 3];
                        i += 4;
                        break;
                    case 0x07 when i + 2 <= unit.Length:
                        // change of colour and contrast inside the area: its length is told – passed over
                        i += Math.Max(2, (unit[i] << 8) | unit[i + 1]);
                        break;
                    default:
                        // 0xFF ends the sequence (an unknown command too: nothing more can be read of it)
                        done = true;
                        break;
                }
            }

            if (next == at || next < control)
            {
                break;
            }

            at = next;
        }

        var width = x2 - x1 + 1;
        var height = y2 - y1 + 1;
        if (x1 < 0 || y1 < 0 || width <= 0 || height <= 0 || width > MaximumSide || height > MaximumSide ||
            top < 4 || bottom < 4 || top >= unit.Length || bottom >= unit.Length)
        {
            return null;
        }

        var pixels = new byte[width * height];
        Field(unit, top, control, pixels, width, height, 0);
        Field(unit, bottom, control, pixels, width, height, 1);
        return new Unit(x1, y1, width, height, PgsRle(pixels, width, height), colours, alphas, show ?? TimeSpan.Zero, hide);
    }

    // the lines of one field (every second line from the first one): runs of nibbles – a two-bit colour and the length
    // (1, 2, 3 or 4 nibbles); length 0 fills the rest of the line; every line starts on a whole byte
    private static void Field(byte[] unit, int start, int limit, byte[] pixels, int width, int height, int first)
    {
        var end = start < limit ? limit : unit.Length;
        var nibble = start * 2;
        int Next()
        {
            if (nibble / 2 >= end)
            {
                return -1;
            }

            var b = unit[nibble / 2];
            var value = nibble % 2 == 0 ? b >> 4 : b & 15;
            nibble++;
            return value;
        }

        for (var y = first; y < height; y += 2)
        {
            var x = 0;
            while (x < width)
            {
                var code = Next();
                if (code < 0)
                {
                    return;
                }

                for (var size = 1; code < 0x04 << (2 * (size - 1)) && size < 4; size++)
                {
                    var more = Next();
                    if (more < 0)
                    {
                        return;
                    }

                    code = (code << 4) | more;
                }

                var colour = (byte)(code & 3);
                var run = code >> 2;
                if (run == 0 || x + run > width)
                {
                    run = width - x;
                }

                pixels.AsSpan((y * width) + x, run).Fill(colour);
                x += run;
            }

            // the next line on a whole byte
            if (nibble % 2 == 1)
            {
                nibble++;
            }
        }
    }

    // the pixels coded as PGS codes them (what the player and the reading of text decode): a colour byte for a single
    // pixel, 0 and a code for runs, 0 0 at the end of a line
    internal static byte[] PgsRle(byte[] pixels, int width, int height)
    {
        var rle = new List<byte>(pixels.Length / 4);
        for (var y = 0; y < height; y++)
        {
            var x = 0;
            while (x < width)
            {
                var colour = pixels[(y * width) + x];
                var run = 1;
                while (x + run < width && pixels[(y * width) + x + run] == colour && run < 16383)
                {
                    run++;
                }

                if (colour != 0 && run <= 2)
                {
                    for (var i = 0; i < run; i++)
                    {
                        rle.Add(colour);
                    }
                }
                else
                {
                    rle.Add(0);
                    var flags = (colour != 0 ? 0x80 : 0) | (run >= 64 ? 0x40 : 0);
                    if (run >= 64)
                    {
                        rle.Add((byte)(flags | (run >> 8)));
                        rle.Add((byte)(run & 0xFF));
                    }
                    else
                    {
                        rle.Add((byte)(flags | run));
                    }

                    if (colour != 0)
                    {
                        rle.Add(colour);
                    }
                }

                x += run;
            }

            rle.Add(0);
            rle.Add(0);
        }

        return [.. rle];
    }
}
