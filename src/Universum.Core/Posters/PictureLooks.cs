namespace Universum.Core.Posters;

/// <summary>Measures how a picture looks from its pixels (a small copy is enough).</summary>
public static class PictureLooks
{
    /// <summary>
    /// Colourfulness (Hasler and Süsstrunk 2003: the spread and the mean of the opponent colours red–green and
    /// yellow–blue), brightness and contrast (the mean and the standard deviation of the luminance, 0–1) of pixels in
    /// BGRA order (4 bytes each, <paramref name="stride"/> bytes a row).
    /// </summary>
    public static PictureLook Measure(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < ((height - 1) * stride) + (width * 4))
        {
            return new PictureLook(0, 0, 0);
        }

        double sumRg = 0, sumYb = 0, sumRg2 = 0, sumYb2 = 0, sumL = 0, sumL2 = 0;
        var count = (double)width * height;
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                double b = row[(x * 4) + 0], g = row[(x * 4) + 1], r = row[(x * 4) + 2];
                var rg = r - g;
                var yb = (0.5 * (r + g)) - b;
                var luminance = ((0.2126 * r) + (0.7152 * g) + (0.0722 * b)) / 255;
                sumRg += rg;
                sumYb += yb;
                sumRg2 += rg * rg;
                sumYb2 += yb * yb;
                sumL += luminance;
                sumL2 += luminance * luminance;
            }
        }

        var meanRg = sumRg / count;
        var meanYb = sumYb / count;
        var spreadRg = Math.Max(0, (sumRg2 / count) - (meanRg * meanRg));
        var spreadYb = Math.Max(0, (sumYb2 / count) - (meanYb * meanYb));
        var colorfulness = Math.Sqrt(spreadRg + spreadYb) + (0.3 * Math.Sqrt((meanRg * meanRg) + (meanYb * meanYb)));
        var brightness = sumL / count;
        var contrast = Math.Sqrt(Math.Max(0, (sumL2 / count) - (brightness * brightness)));
        return new PictureLook(colorfulness, brightness, contrast);
    }
}
