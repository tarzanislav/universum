using System.Buffers.Binary;

namespace Universum.Core.Subtitles.Pictures;

/// <summary>One picture of a picture subtitle: where it stands on the canvas and its run-length coded pixels.</summary>
/// <param name="Rle">The pixels as PGS codes them (indexes into the palette, runs of equal ones).</param>
public sealed record PictureObject(int X, int Y, int Width, int Height, byte[] Rle);

/// <summary>
/// A picture subtitle (PGS of a Blu-ray): shown from <see cref="Start"/> to <see cref="End"/>, its pictures on a canvas
/// of the video's size, coloured by its palette (BGRA, straight alpha). The pixels stay coded until it is shown.
/// </summary>
public sealed record PictureCue(TimeSpan Start, TimeSpan End, int CanvasWidth, int CanvasHeight, IReadOnlyList<PictureObject> Objects, uint[] Palette) : ITimedCue;

/// <summary>A picture subtitle drawn: its place on the canvas and BGRA pixels (straight alpha).</summary>
public sealed record PictureImage(int X, int Y, int Width, int Height, byte[] Bgra);

/// <summary>
/// Reads PGS subtitles (Blu-ray, ".sup"; mkvextract writes the tracks of MKV files so) by UNIVERSUM itself
/// (owner, 30. 10. 2026: own code): segments of display sets – composition, window, palette, object, end. A display
/// set with pictures shows them until the next display set (one that clears or another one). A damaged part is
/// skipped; reading never fails on the content.
/// </summary>
public static class PgsReader
{
    private const byte Palette = 0x14;
    private const byte Object = 0x15;
    private const byte Composition = 0x16;
    private const byte EndOfSet = 0x80;
    private const int MaximumSide = 8192;
    private const int MaximumObjectBytes = 16 * 1024 * 1024;

    /// <summary>The subtitles of a ".sup" file.</summary>
    public static async Task<IReadOnlyList<PictureCue>> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Read(bytes);
    }

    /// <summary>The subtitles of PGS data.</summary>
    public static IReadOnlyList<PictureCue> Read(ReadOnlySpan<byte> data)
    {
        var shown = new List<(TimeSpan Time, PictureCue? Cue)>();
        var palettes = new Dictionary<int, uint[]>();
        var objects = new Dictionary<int, (int Width, int Height, List<byte> Rle)>();
        (TimeSpan Time, int Width, int Height, int Palette, List<(int Id, int X, int Y, (int X, int Y, int W, int H)? Crop)> Placed)? composition = null;

        var at = 0;
        while (at + 13 <= data.Length)
        {
            if (data[at] != (byte)'P' || data[at + 1] != (byte)'G')
            {
                // lost: the next segment's mark
                at++;
                continue;
            }

            var pts = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 2)..]);
            var type = data[at + 10];
            var size = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 11)..]);
            var body = at + 13 + size <= data.Length ? data.Slice(at + 13, size) : ReadOnlySpan<byte>.Empty;
            at += 13 + size;
            if (body.IsEmpty && type != EndOfSet)
            {
                continue;
            }

            switch (type)
            {
                case Composition when body.Length >= 11:
                {
                    var state = body[7];
                    if ((state & 0x80) != 0)
                    {
                        // a new epoch: nothing of the old one is kept
                        objects.Clear();
                        palettes.Clear();
                    }

                    var placed = new List<(int, int, int, (int, int, int, int)?)>();
                    var count = body[10];
                    var offset = 11;
                    for (var i = 0; i < count && offset + 8 <= body.Length; i++)
                    {
                        var id = BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
                        var cropped = (body[offset + 3] & 0x80) != 0;
                        var x = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 4)..]);
                        var y = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 6)..]);
                        offset += 8;
                        (int, int, int, int)? crop = null;
                        if (cropped && offset + 8 <= body.Length)
                        {
                            crop = (BinaryPrimitives.ReadUInt16BigEndian(body[offset..]), BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 2)..]),
                                BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 4)..]), BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 6)..]));
                            offset += 8;
                        }

                        placed.Add((id, x, y, crop));
                    }

                    composition = (Time(pts), BinaryPrimitives.ReadUInt16BigEndian(body), BinaryPrimitives.ReadUInt16BigEndian(body[2..]), body[9], placed);
                    break;
                }

                case Palette when body.Length >= 2:
                {
                    var height = composition?.Height ?? 1080;
                    var palette = palettes.TryGetValue(body[0], out var known) ? known : new uint[256];
                    for (var offset = 2; offset + 5 <= body.Length; offset += 5)
                    {
                        palette[body[offset]] = Color(body[offset + 1], body[offset + 2], body[offset + 3], body[offset + 4], height);
                    }

                    palettes[body[0]] = palette;
                    break;
                }

                case Object when body.Length >= 4:
                {
                    var id = BinaryPrimitives.ReadUInt16BigEndian(body);
                    var sequence = body[3];
                    var rle = body[4..];
                    if ((sequence & 0x80) != 0)
                    {
                        if (rle.Length < 7)
                        {
                            break;
                        }

                        var length = (rle[0] << 16) | (rle[1] << 8) | rle[2];
                        var width = BinaryPrimitives.ReadUInt16BigEndian(rle[3..]);
                        var height = BinaryPrimitives.ReadUInt16BigEndian(rle[5..]);
                        if (width is 0 or > MaximumSide || height is 0 or > MaximumSide || length > MaximumObjectBytes)
                        {
                            objects.Remove(id);
                            break;
                        }

                        objects[id] = (width, height, [.. rle[7..]]);
                    }
                    else if (objects.TryGetValue(id, out var started) && started.Rle.Count + rle.Length <= MaximumObjectBytes)
                    {
                        started.Rle.AddRange(rle);
                    }

                    break;
                }

                case EndOfSet when composition is { } set:
                {
                    composition = null;
                    if (set.Placed.Count == 0)
                    {
                        shown.Add((set.Time, null));
                        break;
                    }

                    var pictures = new List<PictureObject>();
                    foreach (var (id, x, y, crop) in set.Placed)
                    {
                        if (objects.TryGetValue(id, out var picture) && picture.Rle.Count > 0)
                        {
                            var whole = new PictureObject(x, y, picture.Width, picture.Height, [.. picture.Rle]);
                            pictures.Add(crop is { } part ? Cropped(whole, part) : whole);
                        }
                    }

                    var colours = palettes.TryGetValue(set.Palette, out var palette) ? (uint[])palette.Clone() : new uint[256];
                    shown.Add((set.Time, pictures.Count == 0 ? null : new PictureCue(set.Time, set.Time, set.Width, set.Height, pictures, colours)));
                    break;
                }
            }
        }

        // a subtitle is shown until the next display set (one that clears it or the next subtitle); the last one 5 s
        var cues = new List<PictureCue>();
        for (var i = 0; i < shown.Count; i++)
        {
            if (shown[i].Cue is not { } cue)
            {
                continue;
            }

            var end = i + 1 < shown.Count ? shown[i + 1].Time : cue.Start + TimeSpan.FromSeconds(5);
            if (end > cue.Start)
            {
                cues.Add(cue with { End = end });
            }
        }

        return cues;
    }

    /// <summary>
    /// The pictures of a subtitle drawn into one BGRA image of the area they cover (on the canvas); null when nothing
    /// can be drawn.
    /// </summary>
    public static PictureImage? Render(PictureCue cue)
    {
        if (cue.Objects.Count == 0)
        {
            return null;
        }

        var left = cue.Objects.Min(o => o.X);
        var top = cue.Objects.Min(o => o.Y);
        var width = cue.Objects.Max(o => o.X + o.Width) - left;
        var height = cue.Objects.Max(o => o.Y + o.Height) - top;
        if (width <= 0 || height <= 0 || width > MaximumSide || height > MaximumSide)
        {
            return null;
        }

        var bgra = new byte[width * height * 4];
        foreach (var picture in cue.Objects)
        {
            Decode(picture, cue.Palette, bgra, width, picture.X - left, picture.Y - top);
        }

        return new PictureImage(left, top, width, height, bgra);
    }

    // the run-length coded pixels into the image: a colour byte, or 0 and a code (end of line, runs of 0 or of a colour)
    private static void Decode(PictureObject picture, uint[] palette, byte[] target, int stride, int left, int top)
    {
        var rle = picture.Rle;
        int x = 0, y = 0, at = 0;
        while (at < rle.Length && y < picture.Height)
        {
            int colour, run;
            var first = rle[at++];
            if (first != 0)
            {
                (colour, run) = (first, 1);
            }
            else
            {
                if (at >= rle.Length)
                {
                    break;
                }

                var code = rle[at++];
                if (code == 0)
                {
                    x = 0;
                    y++;
                    continue;
                }

                var longRun = (code & 0x40) != 0;
                run = code & 0x3F;
                if (longRun)
                {
                    if (at >= rle.Length)
                    {
                        break;
                    }

                    run = (run << 8) | rle[at++];
                }

                if ((code & 0x80) != 0)
                {
                    if (at >= rle.Length)
                    {
                        break;
                    }

                    colour = rle[at++];
                }
                else
                {
                    colour = 0;
                }
            }

            var argb = palette[colour];
            for (var i = 0; i < run && x < picture.Width; i++, x++)
            {
                var offset = (((top + y) * stride) + left + x) * 4;
                if (offset >= 0 && offset + 4 <= target.Length && (argb >> 24) != 0)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset), argb);
                }
            }
        }
    }

    // a cropped object: only its part (rare); the pixels are decoded whole, the part is cut by the picture's size
    private static PictureObject Cropped(PictureObject whole, (int X, int Y, int W, int H) crop) =>
        crop.X == 0 && crop.Y == 0 && crop.W > 0 && crop.H > 0 ? whole with { Width = Math.Min(whole.Width, crop.W), Height = Math.Min(whole.Height, crop.H) } : whole;

    // Y Cr Cb (limited range; BT.709 for HD, BT.601 for SD) and alpha → BGRA as one little-endian number
    private static uint Color(byte y, byte cr, byte cb, byte alpha, int height)
    {
        var luma = (y - 16) * 1.164383;
        double r, g, b;
        if (height > 576)
        {
            r = luma + (1.792741 * (cr - 128));
            g = luma - (0.213249 * (cb - 128)) - (0.532909 * (cr - 128));
            b = luma + (2.112402 * (cb - 128));
        }
        else
        {
            r = luma + (1.596027 * (cr - 128));
            g = luma - (0.391762 * (cb - 128)) - (0.812968 * (cr - 128));
            b = luma + (2.017232 * (cb - 128));
        }

        static uint Byte(double value) => (uint)Math.Clamp((int)Math.Round(value), 0, 255);
        return (uint)alpha << 24 | Byte(r) << 16 | Byte(g) << 8 | Byte(b);
    }

    private static TimeSpan Time(uint pts) => TimeSpan.FromTicks((long)pts * TimeSpan.TicksPerMillisecond / 90);
}
