using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Reports;
using Sheet = Universum.Core.Reports.Paper;

namespace Universum.App.Pages;

/// <summary>Another format a report's data can be saved in (iCalendar, CSV, Markdown, a backup …).</summary>
/// <param name="Save">Writes the file (its path given) and says what it did; an import gets the path of a file to read.</param>
public sealed record ReportExport(string Label, string Filter, string FileName, Func<string, string> Save, bool Opens = false, string ToolTip = "");

/// <summary>
/// Drawing the laid-out pages of a report in WPF (print preview and printing): the same pages as the PDF – the same sheets
/// (2 or 4 pages on a sheet), positions and font – so what is seen is what is printed and saved.
/// </summary>
public static class ReportPrinting
{
    // the pages are in points (1/72 in), WPF in device-independent pixels (1/96 in)
    private const double Dip = 96.0 / 72.0;

    public static FixedDocument Document(IReadOnlyList<ReportPage> pages, ReportFonts fonts, ReportOptions options)
    {
        var document = new FixedDocument();
        var family = new FontFamily(fonts.Family);
        foreach (var (width, height, placed) in Sheets.Of(pages, options.PagesPerSheet))
        {
            var sheet = new FixedPage { Width = width * Dip, Height = height * Dip, Background = Brushes.White };
            foreach (var (page, x, y, scale) in placed)
            {
                var canvas = new Canvas { Width = page.Width, Height = page.Height, RenderTransform = new MatrixTransform(scale * Dip, 0, 0, scale * Dip, x * Dip, y * Dip) };
                if (placed.Count > 1)
                {
                    canvas.Children.Add(new Rectangle { Width = page.Width, Height = page.Height, Stroke = Paint(new Rgb(0xCC, 0xCC, 0xCC)), StrokeThickness = 0.5 });
                }

                foreach (var item in page.Items)
                {
                    if (Element(item, family, options) is { } element)
                    {
                        canvas.Children.Add(element);
                    }
                }

                sheet.Children.Add(canvas);
            }

            var content = new PageContent();
            ((IAddChild)content).AddChild(sheet);
            document.Pages.Add(content);
        }

        return document;
    }

    private static UIElement? Element(PageItem item, FontFamily family, ReportOptions options)
    {
        switch (item)
        {
            case TextItem text:
                var block = new TextBlock
                {
                    Text = text.Text,
                    FontFamily = family,
                    FontSize = text.Size,
                    FontWeight = text.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = Paint(options.Ink(text.Color)),
                };
                Canvas.SetLeft(block, text.X);
                Canvas.SetTop(block, text.Y - (family.Baseline * text.Size));
                return block;
            case BoxItem box:
                var fill = options.Fill(box.Fill);
                if (fill is null && box.Stroke is null)
                {
                    return null;
                }

                var rectangle = new Rectangle
                {
                    Width = box.Width,
                    Height = box.Height,
                    Fill = fill is { } f ? Paint(f) : null,
                    Stroke = box.Stroke is { } s ? Paint(options.Ink(s)) : null,
                    StrokeThickness = box.Stroke is null ? 0 : box.StrokeWidth,
                    RadiusX = box.Radius,
                    RadiusY = box.Radius,
                };
                Canvas.SetLeft(rectangle, box.X);
                Canvas.SetTop(rectangle, box.Y);
                return rectangle;
            case ImageItem image:
                var picture = new Image { Width = image.Width, Height = image.Height, Stretch = Stretch.Fill };
                using (var stream = new MemoryStream(image.Picture.Bytes))
                {
                    picture.Source = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                }

                Canvas.SetLeft(picture, image.X);
                Canvas.SetTop(picture, image.Y);
                return picture;
            case LineItem line:
                return new Line { X1 = line.X1, Y1 = line.Y1, X2 = line.X2, Y2 = line.Y2, Stroke = Paint(options.Ink(line.Color)), StrokeThickness = line.Width };
            default:
                return null;
        }
    }

    private static SolidColorBrush Paint(Rgb color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Tlač a export (owner, 31. 10. 2026 – POZNÁMKY, stage P3): a report of Kalendár, Plánované úlohy or Poznámky with the
/// choices of printing (paper, orientation, pages on a sheet, lighter print, no backgrounds, a punch margin, double-sided,
/// header and footer – kept in the settings), its preview page by page, printing, PDF written by UNIVERSUM's own code,
/// HTML, and the other formats of its data.
/// </summary>
public sealed class ReportViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<string, ReportOptions, ReportFonts, IReadOnlyList<ReportPage>> _build;
    private IReadOnlyList<ReportPage> _pages = [];
    private FixedDocument? _document;
    private VideoChoice<string> _form;
    private string _status = "";

    public ReportViewModel(AppServices services, string title, IReadOnlyList<VideoChoice<string>> forms, Func<string, ReportOptions, ReportFonts, IReadOnlyList<ReportPage>> build,
        IReadOnlyList<ReportExport> exports)
    {
        _services = services;
        _build = build;
        Title = title;
        Forms = forms;
        _form = forms[0];
        Exports = [.. exports.Select(e => new ReportExportRow(e, this))];
        PrintCommand = new RelayCommand(Print, () => _document is not null);
        SavePdfCommand = new RelayCommand(() => Save("PDF|*.pdf", Title + ".pdf", path => File.WriteAllBytes(path, PdfWriter.Write(_pages, Fonts!, Options, Title))), () => _document is not null);
        SaveHtmlCommand = new RelayCommand(() => Save("HTML|*.html", Title + ".html", path => File.WriteAllText(path, HtmlWriter.Write(_pages, Options, Title))), () => _document is not null);
        Refresh();
    }

    public string Title { get; }

    public IReadOnlyList<VideoChoice<string>> Forms { get; }

    public bool HasForms => Forms.Count > 1;

    /// <summary>What is printed (the calendar: as on the screen, the chosen day, a month as a grid …).</summary>
    public VideoChoice<string> Form
    {
        get => _form;
        set
        {
            if (SetProperty(ref _form, value))
            {
                Refresh();
            }
        }
    }

    public static IReadOnlyList<VideoChoice<Paper>> Papers { get; } = [new(Sheet.A4, "A4"), new(Sheet.A3, "A3"), new(Sheet.A5, "A5"), new(Sheet.Letter, "Letter")];

    public static IReadOnlyList<VideoChoice<bool?>> Orientations { get; } = [new(null, Loc.T("podľa zostavy")), new(false, Loc.T("na výšku")), new(true, Loc.T("na šírku"))];

    public static IReadOnlyList<VideoChoice<int>> PerSheet { get; } = [new(1, Loc.T("1 strana na list")), new(2, Loc.T("2 strany na list")), new(4, Loc.T("4 strany na list"))];

    private ReportOptions Options => _services.Settings.Print;

    public VideoChoice<Paper> Paper { get => Papers.First(p => p.Value == Options.Paper); set => Change(Options with { Paper = value.Value }); }

    public VideoChoice<bool?> Orientation { get => Orientations.First(o => o.Value == Options.Landscape); set => Change(Options with { Landscape = value.Value }); }

    public VideoChoice<int> PagesPerSheet { get => PerSheet.FirstOrDefault(p => p.Value == Options.PagesPerSheet) ?? PerSheet[0]; set => Change(Options with { PagesPerSheet = value.Value }); }

    public bool Lighter { get => Options.Lighter; set => Change(Options with { Lighter = value }); }

    public bool NoBackgrounds { get => Options.NoBackgrounds; set => Change(Options with { NoBackgrounds = value }); }

    public bool PunchMargin { get => Options.PunchMargin; set => Change(Options with { PunchMargin = value }); }

    public bool DoubleSided { get => Options.DoubleSided; set => Change(Options with { DoubleSided = value }); }

    public bool HeaderAndFooter { get => Options.HeaderAndFooter; set => Change(Options with { HeaderAndFooter = value }); }

    /// <summary>The preview (and what is printed).</summary>
    public FixedDocument? Document { get => _document; private set => SetProperty(ref _document, value); }

    public int PageCount => _pages.Count;

    public string Summary => _document is null ? "" : Loc.F("Strany: {0} · listy papiera: {1}", _pages.Count, _document.Pages.Count);

    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public IReadOnlyList<ReportExportRow> Exports { get; }

    public bool HasExports => Exports.Count > 0;

    public RelayCommand PrintCommand { get; }

    public RelayCommand SavePdfCommand { get; }

    public RelayCommand SaveHtmlCommand { get; }

    /// <summary>Asks where to save (filter, suggested name); null: cancelled. The self-test answers itself.</summary>
    public Func<string, string, string?> AskSave { get; set; } = (filter, name) =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = Clean(name) };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    /// <summary>Asks for a file to read (filter); null: cancelled.</summary>
    public Func<string, string?> AskOpen { get; set; } = filter =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    /// <summary>The PDF of the report as it is now (the self-test).</summary>
    public byte[] Pdf() => PdfWriter.Write(_pages, Fonts!, Options, Title);

    private ReportFonts? Fonts => _services.ReportFonts;

    /// <summary>The report laid out again (another form or choices).</summary>
    public void Refresh()
    {
        if (Fonts is not { } fonts)
        {
            Status = Loc.T("Nenašlo sa písmo, ktoré sa smie vložiť do PDF (Segoe UI, Arial, Tahoma, Verdana, Calibri).");
            Document = null;
            return;
        }

        _pages = _build(Form.Value, Options, fonts);
        Document = ReportPrinting.Document(_pages, fonts, Options);
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(Summary));
        PrintCommand.NotifyCanExecuteChanged();
        SavePdfCommand.NotifyCanExecuteChanged();
        SaveHtmlCommand.NotifyCanExecuteChanged();
    }

    private void Change(ReportOptions options, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (options == Options)
        {
            return;
        }

        _services.Settings.Print = options;
        _services.Settings.Save();
        OnPropertyChanged(name);
        Refresh();
    }

    private void Print()
    {
        if (Document is not { } document)
        {
            return;
        }

        var dialog = new PrintDialog();
        if (dialog.ShowDialog() == true)
        {
            dialog.PrintDocument(document.DocumentPaginator, "UNIVERSUM – " + Title);
            Status = Loc.T("Odoslané na tlačiareň.");
        }
    }

    internal void Save(string filter, string name, Action<string> write)
    {
        if (AskSave(filter, name) is not { } path)
        {
            return;
        }

        try
        {
            write(path);
            Status = Loc.T("Uložené: ") + path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = Loc.T("Nedá sa uložiť: ") + ex.Message;
        }
    }

    internal void Run(ReportExport export)
    {
        var path = export.Opens ? AskOpen(export.Filter) : AskSave(export.Filter, export.FileName);
        if (path is null)
        {
            return;
        }

        try
        {
            Status = export.Save(path);
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Status = (export.Opens ? Loc.T("Nedá sa načítať: ") : Loc.T("Nedá sa uložiť: ")) + ex.Message;
        }
    }

    // a file name without the letters Windows does not allow
    private static string Clean(string name) => string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}

/// <summary>A button of another format in the report window.</summary>
public sealed class ReportExportRow(ReportExport export, ReportViewModel report)
{
    public string Label => export.Label;

    public string ToolTip => export.ToolTip;

    public RelayCommand Command { get; } = new(() => report.Run(export));
}
