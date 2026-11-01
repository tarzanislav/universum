using System.Globalization;
using System.Text;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;
using Universum.Core.Subtitles.Pictures;

namespace Universum.Tests;

/// <summary>
/// VobSub subtitles (DVD) read by UNIVERSUM itself (owner, 1. 11. 2026) – invented pictures, written by the test's own
/// small writer of ".idx" and ".sub".
/// </summary>
public sealed class VobSubTests
{
    // colours of the ".idx" palette: 0 black, 1 white, 2 grey, 3 red, the rest blue
    private const string Palette = "000000, ffffff, 808080, ff0000, 0000ff, 0000ff, 0000ff, 0000ff, 0000ff, 0000ff, 0000ff, 0000ff, 0000ff, 0000ff, 0000ff, 0000ff";

    [Test]
    public void SubtitlesOfIdxAndSub()
    {
        var (idx, sub) = Write(
        [
            new(0, 1.0, 2.5, 100, 400, Picture(80, 6, seed: 1)),
            new(0, 3.0, 4.0, 40, 420, Picture(30, 5, seed: 2)),
            new(1, 1.5, 2.0, 60, 410, Picture(20, 4, seed: 3)),
        ]);

        var streams = VobSubReader.Streams(idx);
        Assert.True(streams is [{ Index: 0, Language: "cs", Count: 2 }, { Index: 1, Language: "en", Count: 1 }], "two languages: " + string.Join(", ", streams));

        var cues = VobSubReader.Read(idx, sub);
        Assert.True(cues.Count == 2, $"the first language: two subtitles ({cues.Count})");
        Assert.True(Near(cues[0].Start, 1) && Near(cues[0].End, 2.5) && Near(cues[1].Start, 3) && Near(cues[1].End, 4), $"times {cues[0].Start}–{cues[0].End}, {cues[1].Start}–{cues[1].End}");
        Assert.True(cues[0] is { CanvasWidth: 720, CanvasHeight: 576, Objects: [{ X: 100, Y: 400, Width: 80, Height: 6 }] }, "the picture on the 720 × 576 canvas");
        Same(cues[0], 100, 400, Picture(80, 6, seed: 1), "the first picture, pixel by pixel (both fields, long runs)");
        Same(cues[1], 40, 420, Picture(30, 5, seed: 2), "the second picture (an odd number of lines)");

        var english = VobSubReader.Read(idx, sub, stream: 1);
        Assert.True(english.Count == 1 && Near(english[0].Start, 1.5) && Near(english[0].End, 2), "the second language by its index");
        Same(english[0], 60, 410, Picture(20, 4, seed: 3), "the second language's picture");

        // damaged data never fails: a cut ".sub" gives what is whole; nonsense gives nothing
        Assert.True(VobSubReader.Read(idx, sub.AsSpan(0, sub.Length / 2)).Count >= 1, "a cut .sub");
        Assert.True(VobSubReader.Read("size: 720x576\ntimestamp: 00:00:01:000, filepos: 000000000", [0, 0, 1, 0xBD, 0, 3, 1, 2, 3]).Count == 0, "nonsense");
        Assert.True(VobSubReader.Read("nič", sub).Count == 0, "no .idx");
    }

    [Test]
    public void DelayOfALanguage()
    {
        var (idx, sub) = Write([new(0, 2.0, 3.0, 10, 10, Picture(8, 2, seed: 4))]);
        var delayed = idx.Replace("id: cs, index: 0", "id: cs, index: 0\ndelay: 00:00:01:500", StringComparison.Ordinal);
        var moved = VobSubReader.Read(delayed, sub);
        Assert.True(moved.Count == 1 && Near(moved[0].Start, 3.5) && Near(moved[0].End, 4.5), "the language's delay moves its subtitles");
    }

    [Test]
    public async Task VobSubOfAnMkvTrack()
    {
        // the same subtitles in an MKV (mkvmerge takes the .idx with its .sub), read back from the track as UNIVERSUM reads it
        var installation = await TestData.RequireMkvToolNixAsync();
        var folder = Directory.CreateTempSubdirectory("universum-vobsub-").FullName;
        try
        {
            var (idx, sub) = Write([new(0, 1.0, 2.5, 100, 400, Picture(80, 6, seed: 1)), new(0, 3.0, 4.0, 40, 420, Picture(30, 5, seed: 2))]);
            var idxPath = Path.Combine(folder, "titulky.idx");
            await File.WriteAllTextAsync(idxPath, idx);
            await File.WriteAllBytesAsync(Path.Combine(folder, "titulky.sub"), sub);
            var fromFiles = await PictureSubtitles.ReadFileAsync(idxPath);
            Assert.True(fromFiles.Count == 2, "the files read as picture subtitles");

            var mkv = Path.Combine(folder, "film.mkv");
            var muxed = await new ProcessRunner().RunAsync(new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvMerge),
                ["-o", mkv, TestData.Media("cz-only.mkv"), idxPath]));
            Assert.True(muxed.ExitCode is 0 or 1, muxed.StandardOutput);
            var file = await new MkvMergeInspector(installation, new ProcessRunner()).InspectAsync(mkv);
            var track = file.TracksOf(Universum.Core.Media.TrackType.Subtitles).Single(t => t.CodecId == "S_VOBSUB");
            Assert.True(PictureSubtitles.CanShow(track), "a VobSub track can be shown and read");
            var cues = await PictureSubtitles.ExtractAsync(installation, new ProcessRunner(), file, track);
            Assert.True(cues.Count == 2 && Near(cues[0].Start, 1) && Near(cues[0].End, 2.5) && Near(cues[1].Start, 3) && Near(cues[1].End, 4),
                "the track's subtitles with their times: " + string.Join(", ", cues.Select(c => $"{c.Start}–{c.End}")));
            Same(cues[0], 100, 400, Picture(80, 6, seed: 1), "the picture after mkvmerge and mkvextract");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // the hiding time of a DVD subtitle counts in steps of 1024 / 90 000 s (11,4 ms)
    private static bool Near(TimeSpan time, double seconds) => Math.Abs(time.TotalSeconds - seconds) < 0.012;

    // the drawn subtitle has the picture's colours where the picture is not transparent (0 = transparent)
    private static void Same(PictureCue cue, int x, int y, byte[,] picture, string what)
    {
        var image = PgsReader.Render(cue);
        Assert.True(image is not null, what + ": drawn");
        uint[] colours = [0, 0xFFFFFFFF, 0xFF808080, 0xFFFF0000];
        var height = picture.GetLength(0);
        var width = picture.GetLength(1);
        var wrong = 0;
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var ix = x + column - image!.X;
                var iy = y + row - image.Y;
                var at = ((iy * image.Width) + ix) * 4;
                var drawn = ix < 0 || iy < 0 || ix >= image.Width || iy >= image.Height
                    ? 0u
                    : (uint)(image.Bgra[at + 3] << 24 | image.Bgra[at + 2] << 16 | image.Bgra[at + 1] << 8 | image.Bgra[at]);
                if (drawn != colours[picture[row, column]])
                {
                    wrong++;
                }
            }
        }

        Assert.True(wrong == 0, $"{what}: {wrong} pixels differ");
    }

    // an invented picture: runs of all four values, a long run (≥ 64) and a line of one colour to its end
    private static byte[,] Picture(int width, int height, int seed)
    {
        var random = new Random(seed);
        var picture = new byte[height, width];
        for (var row = 0; row < height; row++)
        {
            var column = 0;
            while (column < width)
            {
                var run = row == 1 ? width : random.Next(1, row == 0 ? 70 : 20);
                var colour = (byte)random.Next(0, 4);
                for (var i = 0; i < run && column < width; i++)
                {
                    picture[row, column++] = colour;
                }
            }
        }

        return picture;
    }

    private sealed record Shown(int Stream, double Start, double End, int X, int Y, byte[,] Picture);

    // the test's writer: an ".idx" (size, palette, the streams and their times and places) and a ".sub" (an MPEG program
    // stream: each subtitle unit cut into private packets of 60 bytes, each in its own pack)
    private static (string Idx, byte[] Sub) Write(IReadOnlyList<Shown> subtitles)
    {
        var sub = new List<byte>();
        var places = new List<(int Stream, double Start, long Position)>();
        foreach (var shown in subtitles)
        {
            var unit = Unit(shown);
            places.Add((shown.Stream, shown.Start, sub.Count));
            for (var at = 0; at < unit.Length; at += 60)
            {
                var part = unit.AsSpan(at, Math.Min(60, unit.Length - at)).ToArray();
                sub.AddRange([0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x89, 0xC3, 0xF8]);
                var first = at == 0;
                var header = first ? Pts(shown.Start) : [];
                var length = 3 + header.Length + 1 + part.Length;
                sub.AddRange([0x00, 0x00, 0x01, 0xBD, (byte)(length >> 8), (byte)length, 0x81, first ? (byte)0x80 : (byte)0x00, (byte)header.Length]);
                sub.AddRange(header);
                sub.Add((byte)(0x20 + shown.Stream));
                sub.AddRange(part);
            }
        }

        var idx = new StringBuilder();
        idx.Append("# VobSub index file, v7 (do not modify this line!)\n");
        idx.Append("size: 720x576\norg: 0, 0\nscale: 100%, 100%\nalpha: 100%\nsmooth: OFF\nfadein/out: 0, 0\nalign: OFF at LEFT TOP\ntime offset: 0\nforced subs: OFF\n");
        idx.Append("palette: ").Append(Palette).Append('\n');
        idx.Append("custom colors: OFF, tridx: 0000, colors: 000000, 000000, 000000, 000000\nlangidx: 0\n");
        foreach (var (stream, language) in new[] { (0, "cs"), (1, "en") })
        {
            if (places.All(p => p.Stream != stream))
            {
                continue;
            }

            idx.Append(CultureInfo.InvariantCulture, $"\nid: {language}, index: {stream}\n");
            foreach (var (_, start, position) in places.Where(p => p.Stream == stream))
            {
                var time = TimeSpan.FromSeconds(start);
                idx.Append(CultureInfo.InvariantCulture, $"timestamp: {time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}:{time.Milliseconds:000}, filepos: {position:x9}\n");
            }
        }

        return (idx.ToString(), [.. sub]);
    }

    private static byte[] Pts(double seconds)
    {
        var pts = (long)Math.Round(seconds * 90000);
        return [(byte)(0x21 | ((pts >> 29) & 0x0E)), (byte)(pts >> 22), (byte)(((pts >> 14) & 0xFE) | 1), (byte)(pts >> 7), (byte)(((pts << 1) & 0xFE) | 1)];
    }

    // one subtitle unit: the pixels of both fields (nibble runs), then two control sequences – show (colours 0 1 2 3 of
    // the palette, background transparent, the area, the fields) and hide after the subtitle's length
    private static byte[] Unit(Shown shown)
    {
        var height = shown.Picture.GetLength(0);
        var width = shown.Picture.GetLength(1);
        var unit = new List<byte> { 0, 0, 0, 0 };
        var top = unit.Count;
        Field(unit, shown.Picture, 0);
        var bottom = unit.Count;
        Field(unit, shown.Picture, 1);

        var control = unit.Count;
        var second = control + 4 + 3 + 3 + 7 + 5 + 1 + 1;
        int x2 = shown.X + width - 1, y2 = shown.Y + height - 1;
        unit.AddRange([0, 0, (byte)(second >> 8), (byte)second]);
        unit.AddRange([0x03, 0x32, 0x10]);
        unit.AddRange([0x04, 0xFF, 0xF0]);
        unit.AddRange([0x05, (byte)(shown.X >> 4), (byte)(((shown.X & 15) << 4) | (x2 >> 8)), (byte)x2, (byte)(shown.Y >> 4), (byte)(((shown.Y & 15) << 4) | (y2 >> 8)), (byte)y2]);
        unit.AddRange([0x06, (byte)(top >> 8), (byte)top, (byte)(bottom >> 8), (byte)bottom]);
        unit.Add(0x01);
        unit.Add(0xFF);
        var delay = (int)Math.Round((shown.End - shown.Start) * 90000 / 1024);
        unit.AddRange([(byte)(delay >> 8), (byte)delay, (byte)(second >> 8), (byte)second, 0x02, 0xFF]);
        unit[0] = (byte)(unit.Count >> 8);
        unit[1] = (byte)unit.Count;
        unit[2] = (byte)(control >> 8);
        unit[3] = (byte)control;
        return [.. unit];
    }

    private static void Field(List<byte> unit, byte[,] picture, int first)
    {
        var nibbles = new List<int>();
        var width = picture.GetLength(1);
        for (var row = first; row < picture.GetLength(0); row += 2)
        {
            var column = 0;
            while (column < width)
            {
                var colour = picture[row, column];
                var run = 1;
                while (column + run < width && picture[row, column + run] == colour && run < 255)
                {
                    run++;
                }

                var code = column + run == width && run > 255 ? colour : (run << 2) | colour;
                var size = column + run == width && run >= 64 ? 4 : run < 4 ? 1 : run < 16 ? 2 : run < 64 ? 3 : 4;
                if (column + run == width && run >= 64)
                {
                    // to the end of the line
                    code = colour;
                }

                for (var shift = 4 * (size - 1); shift >= 0; shift -= 4)
                {
                    nibbles.Add((code >> shift) & 15);
                }

                column += run;
            }

            if (nibbles.Count % 2 == 1)
            {
                nibbles.Add(0);
            }
        }

        for (var i = 0; i < nibbles.Count; i += 2)
        {
            unit.Add((byte)((nibbles[i] << 4) | nibbles[i + 1]));
        }
    }
}
