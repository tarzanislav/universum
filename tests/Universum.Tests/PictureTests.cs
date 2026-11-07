using System.Net;
using System.Security.Cryptography;
using Universum.Core.Library;
using Universum.Core.Pictures;
using Universum.Core.Posters;

namespace Universum.Tests;

/// <summary>
/// UNIVERSUM's own WebP decoder (owner, 7. 11. 2026: a WebP poster is converted, never only renamed) against the references
/// of other decoders (testdata/pictures: invented test patterns, see tools/make-test-pictures.py): the planes of a lossy
/// picture exactly as ffmpeg's VP8 decoder, the pixels of a lossless one exactly as libwebp.
/// </summary>
public sealed class PictureTests
{
    private static string Picture(string name) => Path.Combine(TestData.Root, "pictures", name);

    private static IEnumerable<(string Name, string Kind, string Hash)> References() =>
        File.ReadAllLines(Picture("references.txt")).Where(l => l.Length > 0).Select(l => l.Split(' ')).Select(p => (p[0], p[1], p[2]));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // the first 'VP8 ' chunk of a simple lossy file: its planes cropped to the picture (Y, then U and V)
    private static byte[] Planes(byte[] file)
    {
        var size = BitConverter.ToInt32(file, 16);
        var planes = WebPLossy.DecodePlanes(file, 20, size);
        using var output = new MemoryStream();
        for (var y = 0; y < planes.Height; y++)
        {
            output.Write(planes.Y, y * planes.YStride, planes.Width);
        }

        int chromaWidth = (planes.Width + 1) / 2, chromaHeight = (planes.Height + 1) / 2;
        foreach (var plane in new[] { planes.U, planes.V })
        {
            for (var y = 0; y < chromaHeight; y++)
            {
                output.Write(plane, y * planes.UvStride, chromaWidth);
            }
        }

        return output.ToArray();
    }

    [Test]
    public void LossyPicturesDecodeExactlyAsTheReference()
    {
        var lossy = References().Where(r => r.Kind == "yuv").ToList();
        Assert.True(lossy.Count >= 5, "lossy references: normal and simple loop filter, segments, 8 partitions, a thin picture");
        foreach (var (name, _, hash) in lossy)
        {
            var file = File.ReadAllBytes(Picture(name + ".webp"));
            Assert.Equal(hash, Hash(Planes(file)), name + ": Y, U, V as ffmpeg's VP8 decoder");
            var pixels = WebP.Decode(file);
            Assert.True(!pixels.HasAlpha && pixels.Bgra.Length == pixels.Width * pixels.Height * 4 && pixels.Bgra.Where((_, i) => i % 4 == 3).All(a => a == 255),
                        name + ": opaque pixels of the picture's size");
        }
    }

    [Test]
    public void LosslessPicturesDecodeExactlyAsLibwebp()
    {
        var lossless = References().Where(r => r.Kind == "bgra").ToList();
        Assert.True(lossless.Count >= 5, "lossless references: transforms, alpha, palettes with packed pixels, an animation");
        foreach (var (name, _, hash) in lossless)
        {
            var pixels = WebP.Decode(File.ReadAllBytes(Picture(name + ".webp")));
            Assert.Equal(hash, Hash(pixels.Bgra), name + ": every pixel as libwebp");
        }

        Assert.True(WebP.Decode(File.ReadAllBytes(Picture("lossless-alpha.webp"))).HasAlpha, "transparency is told");
        Assert.True(!WebP.Decode(File.ReadAllBytes(Picture("palette2.webp"))).HasAlpha, "an opaque picture is opaque");
        var animation = WebP.Decode(File.ReadAllBytes(Picture("animation.webp")));
        Assert.True(animation is { Width: 48, Height: 40 }, "an animation: its first frame on the canvas");
    }

    [Test]
    public void AlphaOfALossyPicture()
    {
        var pixels = WebP.Decode(File.ReadAllBytes(Picture("lossy-alpha.webp")));
        var reference = File.ReadAllBytes(Picture("lossy-alpha.bgra"));
        Assert.True(pixels.HasAlpha && pixels.Bgra.Length == reference.Length, "the size and transparency");
        var colours = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            if (i % 4 == 3)
            {
                Assert.True(pixels.Bgra[i] == reference[i], $"alpha exactly (byte {i})");
            }
            else
            {
                colours = Math.Max(colours, Math.Abs(pixels.Bgra[i] - reference[i]));
            }
        }

        // the planes are exact; BT.601 and the chroma between its samples are computed in UNIVERSUM's own way
        Assert.True(colours <= 2, $"the colours as libwebp's within 2 of 255 (here {colours})");
        Assert.True(pixels.Bgra.Where((_, i) => i % 4 == 3).Distinct().Count() > 10, "real shades of transparency");
    }

    [Test]
    public void ColoursOfBt601()
    {
        static byte[] Colour(byte y, byte u, byte v)
        {
            var planes = new YuvPlanes(2, 2, [y, y, y, y], [u], [v], 2, 1);
            return WebP.YuvToBgra(planes)[..4];
        }

        Assert.Sequence([0, 0, 0, 255], Colour(16, 128, 128).Select(b => (int)b), "black");
        Assert.Sequence([255, 255, 255, 255], Colour(235, 128, 128).Select(b => (int)b), "white");
        var red = Colour(81, 90, 240);
        Assert.True(red[2] >= 250 && red[1] <= 3 && red[0] <= 3, "red: " + string.Join(",", red));
        var grey = Colour(126, 128, 128);
        Assert.True(grey[0] == grey[1] && grey[1] == grey[2] && grey[0] is 127 or 128, "grey stays grey");
    }

    [Test]
    public void BrokenPicturesAreRefusedNeverCrash()
    {
        var random = new Random(7);
        var files = Directory.GetFiles(Path.Combine(TestData.Root, "pictures"), "*.webp").Select(File.ReadAllBytes).ToList();
        int decoded = 0, refused = 0;
        for (var round = 0; round < 1500; round++)
        {
            var bytes = (byte[])files[round % files.Count].Clone();
            if (round % 3 == 0)
            {
                bytes = bytes[..random.Next(1, bytes.Length)];
            }
            else
            {
                for (var i = random.Next(1, 12); i > 0; i--)
                {
                    bytes[random.Next(round % 3 == 1 ? 12 : 20, bytes.Length)] = (byte)random.Next(256);
                }
            }

            try
            {
                WebP.Decode(bytes);
                decoded++;
            }
            catch (InvalidDataException)
            {
                refused++;
            }
        }

        Assert.True(decoded > 0 && refused > 0, $"damaged pictures: {decoded} still readable, {refused} refused – nothing else thrown");
        Assert.Throws<InvalidDataException>(() => WebP.Decode("not a picture at all, just text"u8.ToArray()), "not a WebP");

        // a canvas larger than any poster is refused before anything is allocated
        var huge = File.ReadAllBytes(Picture("animation.webp"));
        huge[24] = huge[25] = huge[26] = 0xFF;
        huge[27] = huge[28] = huge[29] = 0xFF;
        var error = Assert.Throws<InvalidDataException>(() => WebP.Decode(huge), "too large");
        Assert.True(error.Message.Contains("príliš veľký", StringComparison.Ordinal), error.Message);
    }

    [Test]
    public void KindsByContentNotByName()
    {
        Assert.Equal(PictureKind.WebP, PictureFormats.Of(File.ReadAllBytes(Picture("lossy.webp"))), "WebP");
        Assert.Equal(PictureKind.Jpeg, PictureFormats.Of([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]), "JPEG");
        Assert.Equal(PictureKind.Png, PictureFormats.Of([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]), "PNG");
        Assert.Equal(PictureKind.Unknown, PictureFormats.Of("RIFF1234WAVE"u8), "another RIFF file is no WebP");
        Assert.Equal(PictureKind.Jpeg, PictureFormats.Named("FOLDER.JPEG"), "a name");
        Assert.Equal(".webp", PictureFormats.Extension(PictureKind.WebP), "its extension");
        Assert.True(PosterCache.IsPicture(File.ReadAllBytes(Picture("lossy.webp"))) && PosterCache.IsValidPath("/abc.webp"), "posters may be WebP");
    }

    [Test]
    public void ShrunkByTheAreaAndLaidOnWhite()
    {
        // black and white columns shrink to grey; never enlarged
        var stripes = new Pixels(4, 2, [.. Enumerable.Range(0, 8).SelectMany(i => i % 2 == 0 ? new byte[] { 0, 0, 0, 255 } : [255, 255, 255, 255])], false);
        var half = stripes.Fitted(2, 100);
        Assert.True(half is { Width: 2, Height: 1 } && half.Bgra.Where((_, i) => i % 4 != 3).All(b => b is 127 or 128), "the average of the area: " + string.Join(",", half.Bgra));
        Assert.True(ReferenceEquals(stripes, stripes.Fitted(10, 10)), "a smaller picture stays as it is");

        // a transparent neighbour does not darken a colour
        var edge = new Pixels(2, 1, [0, 0, 255, 255, 0, 0, 0, 0], true);
        var one = edge.Fitted(1, 1);
        Assert.True(one.Bgra[2] == 255 && one.Bgra[0] == 0 && one.Bgra[3] is 127 or 128, "red half transparent: " + string.Join(",", one.Bgra));
        var white = one.OnWhite();
        Assert.True(!white.HasAlpha && white.Bgra[3] == 255 && white.Bgra[2] == 255 && white.Bgra[0] is 127 or 128, "on white: pink – " + string.Join(",", white.Bgra));

        var poster = WebP.Decode(File.ReadAllBytes(Picture("lossless.webp"))).Fitted(int.MaxValue, 72);
        Assert.True(poster is { Width: 80, Height: 72 }, $"the proportions kept: {poster.Width} × {poster.Height}");
    }

    [Test]
    public void AWebPUnderAJpgNameIsKeptAsWebPAndNeverWrittenAsJpg()
    {
        var folder = Directory.CreateTempSubdirectory("universum-webp-").FullName;
        try
        {
            var webp = File.ReadAllBytes(Picture("lossy.webp"));
            File.WriteAllBytes(Path.Combine(folder, "folder.jpg"), webp);
            byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
            var saved = PosterPlaces.Save(folder, jpeg);
            Assert.True(saved is { Done: true, KeptAs: "folder-povodny.webp" } && File.ReadAllBytes(Path.Combine(folder, "folder-povodny.webp")).SequenceEqual(webp),
                        "the older picture keeps its content under the extension of its content – " + saved.KeptAs);
            Assert.True(File.ReadAllBytes(Path.Combine(folder, "folder.jpg")).SequenceEqual(jpeg), "folder.jpg is the JPEG");

            var refused = PosterPlaces.Save(folder, webp);
            Assert.True(refused is { Done: false } && refused.Problem!.Contains("folder.jpg", StringComparison.Ordinal) &&
                        File.ReadAllBytes(Path.Combine(folder, "folder.jpg")).SequenceEqual(jpeg), "a WebP is never written as folder.jpg – " + refused.Problem);
            Assert.True(PosterPlaces.Save(folder, webp, "clearlogo.png") is { Done: false }, "nor as a PNG");
            Assert.True(PosterPlaces.Save(folder, jpeg) is { Done: true, KeptAs: "folder-povodny.jpg" }, "an older JPEG stays .jpg");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task PostersInWebPAreDownloadedAndJpegIsAskedForFirst()
    {
        var folder = Path.Combine(Path.GetTempPath(), "universum-webp-posters-" + Guid.NewGuid().ToString("N"));
        try
        {
            var server = new Server(File.ReadAllBytes(Picture("lossy.webp")));
            var posters = new PosterCache(new HttpClient(server), folder, PosterCache.DefaultServer);
            var file = await posters.GetAsync("/vymysleny.jpg", PosterSize.Original);
            Assert.True(file is not null && PictureFormats.OfFile(file) == PictureKind.WebP, "a WebP under a name ending .jpg is kept (it is converted when written)");
            Assert.True(server.Accept is { } accept && accept.StartsWith("image/jpeg", StringComparison.Ordinal) && accept.Contains("image/webp", StringComparison.Ordinal),
                        "JPEG asked for first – " + server.Accept);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private sealed class Server(byte[] picture) : HttpMessageHandler
    {
        public string? Accept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Accept = request.Headers.Accept.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture), RequestMessage = request });
        }
    }
}
