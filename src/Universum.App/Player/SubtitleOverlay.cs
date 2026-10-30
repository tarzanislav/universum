using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Universum.App.Player;

/// <summary>
/// The subtitles over the picture, drawn by UNIVERSUM itself: white letters with a black outline at the bottom (the
/// subtitles edited), yellow ones at the top (the subtitles compared with); italic, bold and underline as in the
/// subtitles. The size follows the height of the picture.
/// </summary>
public sealed class SubtitleOverlay : FrameworkElement
{
    public static readonly DependencyProperty PrimaryProperty = DependencyProperty.Register(
        nameof(Primary), typeof(string), typeof(SubtitleOverlay), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(
        nameof(Secondary), typeof(string), typeof(SubtitleOverlay), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush PrimaryFill = Frozen(Brushes.White);
    private static readonly Brush SecondaryFill = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x4D)));
    private static readonly Brush Outline = Frozen(Brushes.Black);

    /// <summary>The subtitles shown at the bottom (lines by "\n", tags &lt;i&gt; &lt;b&gt; &lt;u&gt;).</summary>
    public string Primary { get => (string)GetValue(PrimaryProperty); set => SetValue(PrimaryProperty, value); }

    /// <summary>The subtitles compared with, at the top.</summary>
    public string Secondary { get => (string)GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = RenderSize;
        if (size.Width < 10 || size.Height < 10)
        {
            return;
        }

        // 5 % of the picture's height, as players do
        var fontSize = Math.Max(9, size.Height * 0.05);
        Draw(drawingContext, Primary, fontSize, PrimaryFill, size, bottom: true);
        Draw(drawingContext, Secondary, fontSize * 0.9, SecondaryFill, size, bottom: false);
    }

    private void Draw(DrawingContext drawingContext, string? text, double fontSize, Brush fill, Size size, bool bottom)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var (plain, styles) = Styled(text);
        var formatted = new FormattedText(plain, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, fontSize, fill,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = size.Width * 0.9,
        };
        foreach (var (start, length, style) in styles)
        {
            if (style == 'i')
            {
                formatted.SetFontStyle(FontStyles.Italic, start, length);
            }
            else if (style == 'b')
            {
                formatted.SetFontWeight(FontWeights.Bold, start, length);
            }
            else
            {
                formatted.SetTextDecorations(TextDecorations.Underline, start, length);
            }
        }

        var margin = size.Height * 0.05;
        var origin = new Point(size.Width * 0.05, bottom ? size.Height - margin - formatted.Height : margin);
        var geometry = formatted.BuildGeometry(origin);
        var pen = new Pen(Outline, fontSize * 0.14) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
        drawingContext.DrawGeometry(fill, null, geometry);
    }

    // the text without the tags, and where italic, bold and underline are
    internal static (string Text, List<(int Start, int Length, char Style)> Styles) Styled(string text)
    {
        var plain = new System.Text.StringBuilder(text.Length);
        var styles = new List<(int, int, char)>();
        var open = new Dictionary<char, int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '<' && TagAt(text, i) is { } tag)
            {
                if (tag.Closing)
                {
                    if (open.Remove(tag.Style, out var from) && plain.Length > from)
                    {
                        styles.Add((from, plain.Length - from, tag.Style));
                    }
                }
                else
                {
                    open.TryAdd(tag.Style, plain.Length);
                }

                i += tag.Length - 1;
                continue;
            }

            plain.Append(text[i]);
        }

        foreach (var (style, from) in open.Where(o => plain.Length > o.Value))
        {
            styles.Add((from, plain.Length - from, style));
        }

        return (plain.ToString(), styles);
    }

    private static (char Style, bool Closing, int Length)? TagAt(string text, int at)
    {
        foreach (var style in "ibu")
        {
            if (string.Compare(text, at, $"<{style}>", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return (style, false, 3);
            }

            if (string.Compare(text, at, $"</{style}>", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return (style, true, 4);
            }
        }

        return null;
    }

    private static Brush Frozen(Brush brush)
    {
        var copy = brush.CloneCurrentValue();
        copy.Freeze();
        return copy;
    }
}
