using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Universum.Core.Posters;

namespace Universum.App.Pages;

/// <summary>Pictures through Windows' own imaging (WIC): how a poster looks, and a poster written as JPEG.</summary>
public static class PictureTools
{
    /// <summary>The look of a picture file, measured on a copy 96 px wide; null when it cannot be read.</summary>
    public static PictureLook? Look(string file)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.UriSource = new Uri(file);
            bitmap.EndInit();
            bitmap.Freeze();
            var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = pixels.PixelWidth * 4;
            var data = new byte[stride * pixels.PixelHeight];
            pixels.CopyPixels(data, stride, 0);
            return PictureLooks.Measure(data, pixels.PixelWidth, pixels.PixelHeight, stride);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The picture (JPEG or PNG) as a JPEG at most <paramref name="maxHeight"/> pixels tall (never enlarged) of the quality
    /// (1–100); transparent parts on white.
    /// </summary>
    public static byte[] ToJpeg(byte[] picture, int maxHeight, int quality)
    {
        int height;
        using (var probe = new MemoryStream(picture))
        {
            height = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelHeight;
        }

        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(picture))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            if (height > maxHeight)
            {
                bitmap.DecodePixelHeight = maxHeight;
            }

            bitmap.EndInit();
        }

        bitmap.Freeze();
        BitmapSource frame = bitmap;
        if (frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Pbgra32 || frame.Format == PixelFormats.Prgba64 || frame.Format == PixelFormats.Rgba64)
        {
            // JPEG has no transparency: the picture on white
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
                context.DrawImage(frame, new Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
            }

            var flat = new RenderTargetBitmap(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            flat.Render(visual);
            frame = new FormatConvertedBitmap(flat, PixelFormats.Bgr24, null, 0);
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>A picture in memory as a frozen bitmap for the screen; null when it cannot be read.</summary>
    public static ImageSource? Show(byte[] picture, int width)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(picture);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = width;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException)
        {
            return null;
        }
    }
}
