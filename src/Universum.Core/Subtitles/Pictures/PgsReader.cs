using System.Buffers.Binary;

namespace Universum.Core.Subtitles.Pictures;

/// <summary>
/// One picture of a picture subtitle: where it stands on the canvas, its size and run-length coded pixels; a cropped one
/// shows only its part <see cref="Crop"/> (at X, Y).
/// </summary>
/// <param name="Rle">The pixels as PGS codes them (indexes into the palette, runs of equal ones) – shared by the subtitles that show it.</param>
public sealed record PictureObject(int X, int Y, int Width, int Height, byte[] Rle, (int X, int Y, int Width, int Height)? Crop = null)
{
    /// <summary>The size shown (the part when cropped).</summary>
    public int ShownWidth => Crop is { } part ? Math.Min(part.Width, Math.Max(0, Width - part.X)) : Width;

    public int ShownHeight => Crop is { } part ? Math.Min(part.Height, Math.Max(0, Height - part.Y)) : Height;
}

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

    // as the format allows: two pictures a subtitle, 64 pictures an epoch (more is damage)
    private const int MaximumPlaced = 2;
    private const int MaximumObjects = 64;

    /// <summary>A ".sup" file larger than this is not read (the subtitles of a long film are tens of megabytes).</summary>
    public const long MaximumFileBytes = 1L << 30;

    /// <summary>The subtitles of a ".sup" file (read away from the caller's thread); <see cref="InvalidDataException"/> for a file too large.</summary>
    public static async Task<IReadOnlyList<PictureCue>> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(path).Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Súbor obrázkových titulkov je príliš veľký.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Read(bytes), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The subtitles of PGS data.</summary>
    public static IReadOnlyList<PictureCue> Read(ReadOnlySpan<byte> data)
    {
        var shown = new List<(TimeSpan Time, PictureCue? Cue)>();
        var palettes = new Dictionary<int, uint[]>();
        // an object's pixels: collected from its fragments, made one array once shown (shared by every subtitle showing it)
        var objects = new Dictionary<int, (int Width, int Height, List<byte> Rle, byte[]? Whole)>();
        var started = new HashSet<int>();
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
                    for (var i = 0; i < count && placed.Count < MaximumPlaced && offset + 8 <= body.Length; i++)
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

                        // the same picture twice is damage: once
                        if (placed.All(p => p.Item1 != id))
                        {
                            placed.Add((id, x, y, crop));
                        }
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
                        if (width is 0 or > MaximumSide || height is 0 or > MaximumSide || length > MaximumObjectBytes ||
                            (!objects.ContainsKey(id) && objects.Count >= MaximumObjects))
                        {
                            objects.Remove(id);
                            started.Remove(id);
                            break;
                        }

                        objects[id] = (width, height, [.. rle[7..]], null);
                        started.Add(id);
                    }
                    else if (started.Contains(id) && objects.TryGetValue(id, out var begun) && begun.Rle.Count + rle.Length <= MaximumObjectBytes)
                    {
                        // a later part of a picture whose first part came in this display set (else it is lost)
                        begun.Rle.AddRange(rle);
                    }

                    break;
                }

                case EndOfSet:
                {
                    started.Clear();
                    if (composition is not { } set)
                    {
                        break;
                    }

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
                            if (picture.Whole is null)
                            {
                                picture = picture with { Whole = [.. picture.Rle] };
                                objects[id] = picture;
                            }

                            pictures.Add(new PictureObject(x, y, picture.Width, picture.Height, picture.Whole, crop is { Item3: > 0, Item4: > 0 } part ? part : null));
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
    /// The pictures of a subtitle drawn into one BGRA image of the area they cover on the canvas (what lies outside the
    /// canvas is left out); null when nothing can be drawn.
    /// </summary>
    public static PictureImage? Render(PictureCue cue)
    {
        if (cue.CanvasWidth is <= 0 or > MaximumSide || cue.CanvasHeight is <= 0 or > MaximumSide)
        {
            return null;
        }

        var shown = cue.Objects.Where(o => o.X < cue.CanvasWidth && o.Y < cue.CanvasHeight && o.ShownWidth > 0 && o.ShownHeight > 0).ToList();
        if (shown.Count == 0)
        {
            return null;
        }

        var left = shown.Min(o => o.X);
        var top = shown.Min(o => o.Y);
        var width = Math.Min(cue.CanvasWidth, shown.Max(o => o.X + o.ShownWidth)) - left;
        var height = Math.Min(cue.CanvasHeight, shown.Max(o => o.Y + o.ShownHeight)) - top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var bgra = new byte[width * height * 4];
        foreach (var picture in shown)
        {
            Decode(picture, cue.Palette, bgra, width, height, picture.X - left, picture.Y - top);
        }

        return new PictureImage(left, top, width, height, bgra);
    }

    // the run-length coded pixels into the image (a cropped picture: only its part, from the part's corner): a colour
    // byte, or 0 and a code (end of line, runs of 0 or of a colour)
    private static void Decode(PictureObject picture, uint[] palette, byte[] target, int stride, int rows, int left, int top)
    {
        var (cropX, cropY, cropWidth, cropHeight) = picture.Crop ?? (0, 0, picture.Width, picture.Height);
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
            var row = top + y - cropY;
            for (var i = 0; i < run && x < picture.Width; i++, x++)
            {
                var column = left + x - cropX;
                if ((argb >> 24) != 0 && x >= cropX && x < cropX + cropWidth && y >= cropY && y < cropY + cropHeight &&
                    column >= 0 && column < stride && row >= 0 && row < rows)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(((row * stride) + column) * 4), argb);
                }
            }
        }
    }

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
