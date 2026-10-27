using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Universum.App;

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Pastel colours of track types, the same everywhere tracks are shown: video lavender, audio mint, subtitles peach.
/// Parameter "chip" gives the solid colour of the type label (with dark text, readable in light and dark mode);
/// otherwise a light tint for whole rows that works on light and dark backgrounds. Accepts a TrackType or its
/// Slovak name ("Video", "Zvuk", "Titulky").
/// </summary>
public sealed class TrackTypeBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.SolidColorBrush VideoChip = Frozen(0xC9, 0xD3, 0xF6);
    private static readonly System.Windows.Media.SolidColorBrush AudioChip = Frozen(0xC4, 0xEB, 0xD2);
    private static readonly System.Windows.Media.SolidColorBrush SubtitlesChip = Frozen(0xFA, 0xDF, 0xB8);
    private static readonly System.Windows.Media.SolidColorBrush OtherChip = Frozen(0xDD, 0xDD, 0xDD);
    private static readonly System.Windows.Media.SolidColorBrush VideoTint = Frozen(0x7B, 0x93, 0xE6, 0x2E);
    private static readonly System.Windows.Media.SolidColorBrush AudioTint = Frozen(0x4C, 0xB8, 0x7A, 0x2E);
    private static readonly System.Windows.Media.SolidColorBrush SubtitlesTint = Frozen(0xF0, 0xA4, 0x4C, 0x2E);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var chip = parameter as string == "chip";
        return Kind(value) switch
        {
            Core.Media.TrackType.Video => chip ? VideoChip : VideoTint,
            Core.Media.TrackType.Audio => chip ? AudioChip : AudioTint,
            Core.Media.TrackType.Subtitles => chip ? SubtitlesChip : SubtitlesTint,
            _ => chip ? OtherChip : System.Windows.Media.Brushes.Transparent,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Core.Media.TrackType Kind(object value) => value switch
    {
        Core.Media.TrackType type => type,
        "Video" => Core.Media.TrackType.Video,
        "Zvuk" or "Audio" => Core.Media.TrackType.Audio,
        "Titulky" or "Subtitles" => Core.Media.TrackType.Subtitles,
        _ => Core.Media.TrackType.Other,
    };

    private static System.Windows.Media.SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Colour marking a source file (index 0–5) and its tracks, as MKVToolNix GUI does.</summary>
public sealed class FileColorConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush[] Colors =
    [
        Frozen(0x4A, 0x7B, 0xD9), Frozen(0x3E, 0xA8, 0x5F), Frozen(0xD9, 0x8B, 0x2B),
        Frozen(0xB0, 0x5C, 0xC8), Frozen(0xD0, 0x4E, 0x6A), Frozen(0x2E, 0xA6, 0xA6),
    ];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Colors[value is int index ? ((index % Colors.Length) + Colors.Length) % Colors.Length : 0];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
