using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Guides;
using Universum.Core.Reports;

namespace Universum.App.Pages;

/// <summary>A part of a guide in its contents on the left; a click shows it.</summary>
public sealed class GuideSectionRow(string title, int index, Action<int> go)
{
    public string Title { get; } = title;

    public int Index { get; } = index;

    public RelayCommand GoCommand { get; } = new(() => go(index));
}

/// <summary>
/// Návod of a group of the left rail (owner, 31. 10. 2026 – brief, exact, with emphasis, colours, lists and pictures): the
/// guide in UNIVERSUM's own markup in the language of the interface, its contents on the left with search, buttons that
/// open the categories it speaks about, and printing, PDF and HTML through the report window.
/// </summary>
public sealed class GuidePageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<string, bool> _open;
    private readonly Dictionary<int, Block> _anchors = [];
    private GuideDocument? _guide;
    private FlowDocument? _document;
    private string _language = "";
    private string _search = "";

    public GuidePageViewModel(AppServices services, string key, Color color, Func<string, bool> open)
    {
        _services = services;
        _open = open;
        Key = key;
        Color = color;
        Accent = Frozen(color);
        PrintCommand = new RelayCommand(() => ShowReport(Report()), () => Guide.Blocks.Count > 0);
    }

    /// <summary>The name of the guide's text ("poznamky": Guides/poznamky.sk.txt …).</summary>
    public string Key { get; }

    public Color Color { get; }

    public Brush Accent { get; }

    /// <summary>The guide in the language of the interface (Slovak when it has no translation).</summary>
    public GuideDocument Guide
    {
        get
        {
            if (_guide is null || _language != Loc.Language)
            {
                _language = Loc.Language;
                _guide = GuideDocument.Parse(Text(Key, _language) ?? Text(Key, "sk") ?? "");
                _document = null;
            }

            return _guide;
        }
    }

    public string Title => Guide.Title;

    /// <summary>The guide drawn for the page (made when it is first shown).</summary>
    public FlowDocument Document => _document ??= Render();

    public ObservableCollection<GuideSectionRow> Sections { get; } = [];

    /// <summary>Words looked for in the parts (without diacritics).</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                FillSections();
            }
        }
    }

    public bool NothingFound => Sections.Count == 0 && Search.Trim().Length > 0;

    public RelayCommand PrintCommand { get; }

    public Action<ReportViewModel> ShowReport { get; set; } = model =>
        new ReportWindow(model) { Owner = Application.Current.MainWindow }.Show();

    /// <summary>The part shown last (the self-test).</summary>
    public int ShownPart { get; private set; } = -1;

    /// <summary>The page was opened: the guide in the current language, the contents.</summary>
    public void Opened()
    {
        // a document belongs to one viewer: the page shown again gets a new one
        _document = null;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Document));
        FillSections();
    }

    /// <summary>Shows a part of the guide.</summary>
    public void Go(int index)
    {
        ShownPart = index;
        if (_anchors.TryGetValue(index, out var block))
        {
            block.BringIntoView();
        }
    }

    public ReportViewModel Report() => new(_services, Guide.Title, [new VideoChoice<string>("guide", Guide.Title)],
        (_, options, fonts) => GuideReport.Layout(Guide, fonts, options, new Rgb(Color.R, Color.G, Color.B), Picture), []);

    /// <summary>The text of a guide in a language; null when there is none.</summary>
    public static string? Text(string key, string language)
    {
        using var stream = typeof(GuidePageViewModel).Assembly.GetManifestResourceStream($"Guides.{key}.{language}.txt");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>A picture of the guides (cut from the self-test's screenshots); null when it is missing.</summary>
    public static Jpeg? Picture(string name)
    {
        using var stream = typeof(GuidePageViewModel).Assembly.GetManifestResourceStream("Guides.obrazky." + name);
        if (stream is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Jpeg.Read(copy.ToArray());
    }

    private void FillSections()
    {
        Sections.Clear();
        foreach (var (title, index) in Guide.Find(Search))
        {
            Sections.Add(new GuideSectionRow(title, index, Go));
        }

        OnPropertyChanged(nameof(NothingFound));
    }

    // ---------------------------------------------------------------- drawing

    private static readonly Dictionary<GuideBoxKind, (Color Fill, Color Edge, string Glyph)> Boxes = new()
    {
        [GuideBoxKind.Tip] = (Color.FromArgb(0x2E, 0x3E, 0xA8, 0x5F), Color.FromRgb(0x3E, 0xA8, 0x5F), ""),
        [GuideBoxKind.Warning] = (Color.FromArgb(0x2E, 0xD9, 0x7A, 0x2B), Color.FromRgb(0xD9, 0x7A, 0x2B), ""),
        [GuideBoxKind.Info] = (Color.FromArgb(0x2E, 0x4A, 0x7B, 0xD9), Color.FromRgb(0x4A, 0x7B, 0xD9), ""),
    };

    private FlowDocument Render()
    {
        _anchors.Clear();
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(4, 0, 24, 24),
            TextAlignment = TextAlignment.Left,
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        List? list = null;
        var guide = Guide;
        for (var i = 0; i < guide.Blocks.Count; i++)
        {
            var block = guide.Blocks[i];
            if (block.Kind is not (GuideBlockKind.Bullet or GuideBlockKind.Step) || list?.MarkerStyle != (block.Kind == GuideBlockKind.Step ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc))
            {
                list = null;
            }

            switch (block.Kind)
            {
                case GuideBlockKind.Title:
                    document.Blocks.Add(new Paragraph(new Run(block.Plain)) { FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Accent, Margin = new Thickness(0, 0, 0, 6) });
                    break;
                case GuideBlockKind.Section:
                    var section = new Paragraph(new Run(block.Plain))
                    {
                        FontSize = 19,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Accent,
                        Margin = new Thickness(0, 18, 0, 6),
                        BorderBrush = Frozen(Color.FromArgb(0x66, Color.R, Color.G, Color.B)),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(0, 0, 0, 3),
                    };
                    _anchors[i] = section;
                    document.Blocks.Add(section);
                    break;
                case GuideBlockKind.Subsection:
                    document.Blocks.Add(new Paragraph(new Run(block.Plain)) { FontSize = 15.5, FontWeight = FontWeights.SemiBold, Foreground = Accent, Margin = new Thickness(0, 10, 0, 4) });
                    break;
                case GuideBlockKind.Paragraph:
                    document.Blocks.Add(Styled(new Paragraph { Margin = new Thickness(0, 0, 0, 8), LineHeight = 21 }, block.Spans));
                    break;
                case GuideBlockKind.Bullet or GuideBlockKind.Step:
                    if (list is null)
                    {
                        list = new List
                        {
                            MarkerStyle = block.Kind == GuideBlockKind.Step ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                            Margin = new Thickness(0, 0, 0, 8),
                            Padding = new Thickness(26, 0, 0, 0),
                            Foreground = Accent,
                        };
                        document.Blocks.Add(list);
                    }

                    var item = Styled(new Paragraph { Margin = new Thickness(0, 0, 0, 3) }, block.Spans);
                    item.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
                    list.ListItems.Add(new ListItem(item));
                    break;
                case GuideBlockKind.Box:
                    var (fill, edge, glyph) = Boxes[block.Box];
                    var boxed = Styled(new Paragraph(), block.Spans);
                    boxed.Inlines.InsertBefore(boxed.Inlines.FirstInline,
                        new Run(glyph + "  ") { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = Frozen(edge) });
                    boxed.Margin = new Thickness(0, 2, 0, 10);
                    boxed.Padding = new Thickness(12, 8, 12, 8);
                    boxed.Background = Frozen(fill);
                    boxed.BorderBrush = Frozen(edge);
                    boxed.BorderThickness = new Thickness(4, 0, 0, 0);
                    document.Blocks.Add(boxed);
                    break;
                case GuideBlockKind.Image:
                    if (Bitmap(block.Target) is { } bitmap)
                    {
                        var image = new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = Math.Min(bitmap.PixelWidth, 860), HorizontalAlignment = HorizontalAlignment.Left };
                        var frame = new System.Windows.Controls.Border
                        {
                            Child = image,
                            BorderBrush = Frozen(Color.FromArgb(0x55, 0x80, 0x80, 0x80)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Margin = new Thickness(0, 4, 0, 0),
                        };
                        document.Blocks.Add(new BlockUIContainer(frame) { Margin = new Thickness(0) });
                        if (block.Plain.Length > 0)
                        {
                            var caption = new Paragraph(new Run(block.Plain)) { FontSize = 12, FontStyle = FontStyles.Italic, Margin = new Thickness(2, 3, 0, 12) };
                            caption.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
                            document.Blocks.Add(caption);
                        }
                    }

                    break;
                case GuideBlockKind.Open:
                    var target = block.Target;
                    var button = new System.Windows.Controls.Button
                    {
                        Content = "→ " + block.Plain,
                        Padding = new Thickness(14, 5, 14, 5),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Command = new RelayCommand(() => _open(target)),
                    };
                    document.Blocks.Add(new BlockUIContainer(button) { Margin = new Thickness(0, 2, 0, 12) });
                    break;
            }
        }

        return document;
    }

    // the spans of a line: bold, italic, and the names of buttons framed like keys
    private Paragraph Styled(Paragraph paragraph, IReadOnlyList<GuideSpan> spans)
    {
        foreach (var span in spans)
        {
            if (span.Key)
            {
                var key = new System.Windows.Controls.Border
                {
                    Child = new System.Windows.Controls.TextBlock { Text = span.Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold },
                    Padding = new Thickness(6, 0, 6, 1),
                    Margin = new Thickness(1, 0, 1, -3),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Frozen(Color.FromArgb(0x80, Color.R, Color.G, Color.B)),
                    Background = Frozen(Color.FromArgb(0x22, Color.R, Color.G, Color.B)),
                };
                paragraph.Inlines.Add(new InlineUIContainer(key) { BaselineAlignment = BaselineAlignment.Center });
                continue;
            }

            var run = new Run(span.Text);
            if (span.Bold)
            {
                run.FontWeight = FontWeights.SemiBold;
            }

            if (span.Italic)
            {
                run.FontStyle = FontStyles.Italic;
            }

            paragraph.Inlines.Add(run);
        }

        return paragraph;
    }

    private static BitmapSource? Bitmap(string name)
    {
        if (Picture(name) is not { } jpeg)
        {
            return null;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(jpeg.Bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
