using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Universum.App.Localization;
using Universum.Core.Pictures;
using Universum.Core.Posters;

namespace Universum.App.Pages;

/// <summary>
/// Pictures: how a poster looks, a picture for the screen, a poster written as a real JPEG (or a logo as a real PNG). JPEG,
/// PNG and the others through Windows' own imaging (WIC); WebP – TMDB and the web give it – through UNIVERSUM's own decoder,
/// so it never depends on a codec of the system. What a picture is, is told by its content, never by its name.
/// </summary>
public static class PictureTools
{
    /// <summary>The look of a picture file, measured on a copy 96 px wide; null when it cannot be read.</summary>
    public static PictureLook? Look(string file)
    {
        try
        {
            if (PictureFormats.OfFile(file) == PictureKind.WebP)
            {
                var small = WebP.Decode(File.ReadAllBytes(file)).Fitted(96, int.MaxValue);
                return PictureLooks.Measure(small.Bgra, small.Width, small.Height, small.Stride);
            }

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
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException or UriFormatException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The picture (JPEG, PNG, WebP …) converted – decoded and written anew, never only renamed – to a JPEG at most
    /// <paramref name="maxHeight"/> pixels tall (never enlarged) of the quality (1–100); transparent parts on white.
    /// InvalidDataException (a broken WebP) or one of WIC's exceptions when it cannot be read.
    /// </summary>
    public static byte[] ToJpeg(byte[] picture, int maxHeight, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(Opaque(picture, maxHeight)));
        using var output = new MemoryStream();
        encoder.Save(output);
        var jpeg = output.ToArray();
        return PictureFormats.Of(jpeg) == PictureKind.Jpeg ? jpeg : throw new InvalidDataException(Loc.T("Obrázok sa nepodarilo previesť na JPEG."));
    }

    /// <summary>
    /// The picture as a PNG with its transparency (clearlogo.png): a PNG as it is, another picture (WebP …) converted.
    /// </summary>
    public static byte[] ToPng(byte[] picture)
    {
        if (PictureFormats.Of(picture) == PictureKind.Png)
        {
            return picture;
        }

        BitmapSource frame;
        if (WebP.Is(picture))
        {
            frame = Frame(WebP.Decode(picture));
        }
        else
        {
            using var stream = new MemoryStream(picture);
            frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>A picture file as a frozen bitmap for the screen, <paramref name="width"/> pixels wide; null when it cannot be read.</summary>
    public static ImageSource? Thumbnail(string file, int width)
    {
        try
        {
            if (PictureFormats.OfFile(file) == PictureKind.WebP)
            {
                return Frame(WebP.Decode(File.ReadAllBytes(file)).Fitted(width, int.MaxValue));
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = width;
            bitmap.UriSource = new Uri(file);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException or UriFormatException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // the picture at most maxHeight tall, without transparency (laid on white)
    private static BitmapSource Opaque(byte[] picture, int maxHeight)
    {
        if (WebP.Is(picture))
        {
            return new FormatConvertedBitmap(Frame(WebP.Decode(picture).Fitted(int.MaxValue, maxHeight).OnWhite()), PixelFormats.Bgr24, null, 0);
        }

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

        return frame;
    }

    /// <summary>Decoded pixels as a frozen bitmap of WPF.</summary>
    public static BitmapSource Frame(Pixels pixels)
    {
        var frame = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, pixels.HasAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, pixels.Bgra, pixels.Stride);
        frame.Freeze();
        return frame;
    }

    /// <summary>A picture in memory as a frozen bitmap for the screen; null when it cannot be read.</summary>
    public static ImageSource? Show(byte[] picture, int width)
    {
        try
        {
            if (WebP.Is(picture))
            {
                return Frame(WebP.Decode(picture).Fitted(width, int.MaxValue));
            }

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
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException or InvalidDataException)
        {
            return null;
        }
    }
}
