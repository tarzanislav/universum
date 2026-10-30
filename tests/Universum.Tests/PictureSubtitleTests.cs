using System.Buffers.Binary;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;
using Universum.Core.Subtitles.Pictures;

namespace Universum.Tests;

/// <summary>PGS subtitles read by UNIVERSUM itself (owner, 30. 10. 2026) – invented pictures only.</summary>
public sealed class PictureSubtitleTests
{
    private static TimeSpan T(double seconds) => TimeSpan.FromSeconds(seconds);

    [Test]
    public void PicturesOfAPgsStream()
    {
        var sup = Sup();
        var cues = PgsReader.Read(sup);
        Assert.True(cues.Count == 2, $"two subtitles ({cues.Count})");
        Assert.True(cues[0].Start == T(1) && cues[0].End == T(2.5) && cues[1].Start == T(3) && cues[1].End == T(4), "shown until it is cleared or replaced");
        Assert.True(cues[0] is { CanvasWidth: 1920, CanvasHeight: 1080, Objects: [{ X: 100, Y: 900, Width: 6, Height: 2 }] }, "the picture on the canvas");

        var image = PgsReader.Render(cues[0])!;
        Assert.True(image is { X: 100, Y: 900, Width: 6, Height: 2 }, "the drawn area");
        // line 1: two white, one transparent, three white; line 2: six yellow (a long run with a colour)
        string Pixel(int x, int y) => string.Join(",", image.Bgra.Skip(((y * 6) + x) * 4).Take(4));
        Assert.Equal("255,255,255,255", Pixel(0, 0), "white (Y 235, BT.709)");
        Assert.Equal("0,0,0,0", Pixel(2, 0), "transparent");
        Assert.Equal("255,255,255,255", Pixel(5, 0), "white again");
        Assert.True(image.Bgra[((1 * 6) + 3) * 4 + 2] > 200 && image.Bgra[((1 * 6) + 3) * 4] < 60 && image.Bgra[((1 * 6) + 3) * 4 + 3] == 255, "yellow: much red, little blue");

        // damaged data never fails: a cut stream gives what is whole
        Assert.True(PgsReader.Read(sup.AsSpan(0, sup.Length - 20)).Count >= 1 && PgsReader.Read([1, 2, 3, 0x50, 0x47]).Count == 0, "damaged");
    }

    [Test]
    public async Task PicturesOfAnMkvTrack()
    {
        // the same subtitles in an MKV (mkvmerge takes the .sup), read back from the track as UNIVERSUM reads it
        var installation = await TestData.RequireMkvToolNixAsync();
        var folder = Directory.CreateTempSubdirectory("universum-pgs-").FullName;
        try
        {
            var sup = Path.Combine(folder, "titulky.sup");
            await File.WriteAllBytesAsync(sup, Sup());
            var mkv = Path.Combine(folder, "film.mkv");
            var muxed = await new ProcessRunner().RunAsync(new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvMerge),
                ["-o", mkv, TestData.Media("cz-only.mkv"), "--language", "0:cs", sup]));
            Assert.True(muxed.ExitCode is 0 or 1, muxed.StandardOutput);
            var file = await new MkvMergeInspector(installation, new ProcessRunner()).InspectAsync(mkv);
            var track = file.TracksOf(Universum.Core.Media.TrackType.Subtitles).Single(t => t.CodecId == "S_HDMV/PGS");
            var cues = await PictureSubtitles.ExtractAsync(installation, new ProcessRunner(), file, track);
            Assert.True(cues is [{ Start.TotalMilliseconds: 1000 }, { Start.TotalMilliseconds: 3000, End.TotalMilliseconds: 4000 }], "the track's subtitles with their times");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // two invented subtitles: 1 s – 2,5 s (cleared), 3 s – 4 s (cleared)
    private static byte[] Sup()
    {
        var data = new List<byte>();
        void Segment(double seconds, byte type, byte[] body)
        {
            var pts = (uint)Math.Round(seconds * 90000);
            data.AddRange("PG"u8.ToArray());
            data.AddRange(BigEndian(pts));
            data.AddRange(BigEndian(0u));
            data.Add(type);
            data.Add((byte)(body.Length >> 8));
            data.Add((byte)body.Length);
            data.AddRange(body);
        }

        byte[] Composition(int number, bool epoch, bool withObject) =>
        [
            0x07, 0x80, 0x04, 0x38, 0x10, (byte)(number >> 8), (byte)number, epoch ? (byte)0x80 : (byte)0x00, 0x00, 0x00, withObject ? (byte)1 : (byte)0,
            .. withObject ? new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x64, 0x03, 0x84 } : [],
        ];
        // palette 0: 1 white (Y 235), 2 yellow, 0 stays transparent
        byte[] palette = [0x00, 0x00, 0x01, 235, 128, 128, 255, 0x02, 210, 146, 16, 255];
        // 6 × 2: line 1 = 1 1 0 1 1 1 (a run of one transparent: 00 01), line 2 = a run of 6 yellow (00 86 02)
        byte[] rle = [0x01, 0x01, 0x00, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x86, 0x02, 0x00, 0x00];
        byte[] objectBody = [0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, (byte)(rle.Length + 4), 0x00, 0x06, 0x00, 0x02, .. rle];
        byte[] window = [0x01, 0x00, 0x00, 0x64, 0x03, 0x84, 0x00, 0x06, 0x00, 0x02];
        foreach (var (show, clear, number) in new[] { (1.0, 2.5, 0), (3.0, 4.0, 2) })
        {
            Segment(show, 0x16, Composition(number, epoch: true, withObject: true));
            Segment(show, 0x17, window);
            Segment(show, 0x14, palette);
            Segment(show, 0x15, objectBody);
            Segment(show, 0x80, []);
            Segment(clear, 0x16, Composition(number + 1, epoch: false, withObject: false));
            Segment(clear, 0x17, window);
            Segment(clear, 0x80, []);
        }

        return [.. data];
    }

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}
