using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.MkvToolNix;
using Universum.Core.Subtitles.Download;
using Universum.Core.Subtitles.Ocr;
using Universum.Core.Subtitles.Pictures;

namespace Universum.App.Pages;

/// <summary>A letter shape the reading did not know, asked once for the whole film: the shape enlarged and the line it first stands in.</summary>
public sealed class OcrQuestion(OcrCluster cluster, BitmapSource shape, BitmapSource line, int number, int total)
{
    public OcrCluster Cluster { get; } = cluster;

    public BitmapSource Shape { get; } = shape;

    public BitmapSource Line { get; } = line;

    public string Title => $"Neznámy tvar {number} z {total} – vo filme {Cluster.Count}×";
}

/// <summary>
/// Text z obrázkov (owner, 31. 10. 2026: own, original, clean and efficient code): the picture subtitles (PGS) of the
/// selected track read by UNIVERSUM's own OCR – compared with letters drawn from the fonts of Windows; shapes it does not
/// know are asked once for the film and kept for other films. The text is saved next to the video as a new SRT file
/// (nothing is overwritten) and added as a new subtitle track.
/// </summary>
public sealed partial class SubtitlesPageViewModel
{
    private CancellationTokenSource? _ocrStop;
    private OcrRun? _ocrRun;
    private SubtitleRow? _ocrRow;
    private Queue<OcrCluster> _ocrUnknown = new();
    private int _ocrAsked;
    private int _ocrTotal;
    private OcrQuestion? _ocrQuestion;
    private string _ocrAnswer = "";
    private string _ocrStatus = "";
    private OcrMemory? _ocrMemory;

    /// <summary>Reads the text of the selected picture subtitles.</summary>
    public AsyncRelayCommand ReadTextCommand { get; private set; } = null!;

    public RelayCommand StopReadingTextCommand { get; private set; } = null!;

    /// <summary>The asked shape is <see cref="OcrAnswer"/> (kept for other films).</summary>
    public AsyncRelayCommand TeachCommand { get; private set; } = null!;

    /// <summary>The asked shape is no letter (a speck): it is left out.</summary>
    public AsyncRelayCommand SpeckCommand { get; private set; } = null!;

    /// <summary>The asked shape stays unknown ("�" in the text, to be corrected by hand).</summary>
    public AsyncRelayCommand SkipShapeCommand { get; private set; } = null!;

    /// <summary>Saves the text now: the shapes not yet told stay unknown.</summary>
    public AsyncRelayCommand FinishReadingCommand { get; private set; } = null!;

    public string OcrStatus { get => _ocrStatus; private set => SetProperty(ref _ocrStatus, value); }

    public OcrQuestion? OcrQuestion
    {
        get => _ocrQuestion;
        private set
        {
            if (SetProperty(ref _ocrQuestion, value))
            {
                OnPropertyChanged(nameof(IsAsking));
                RefreshOcrCommands();
            }
        }
    }

    public bool IsAsking => _ocrQuestion is not null;

    public string OcrAnswer
    {
        get => _ocrAnswer;
        set
        {
            if (SetProperty(ref _ocrAnswer, value))
            {
                TeachCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The OCR memory file (shapes the user told), in UNIVERSUM's settings folder.</summary>
    public static string OcrMemoryFile => Path.Combine(AppSettings.Directory, "ocr-naucene-pismena.json");

    private void CreateOcrCommands()
    {
        ReadTextCommand = new AsyncRelayCommand(ReadTextAsync, () => !_busy && _ocrRun is null && _selected is { IsText: false } row &&
                                                                       PictureSubtitles.CanShow(row.Track) && Anchor is not null);
        StopReadingTextCommand = new RelayCommand(() => _ocrStop?.Cancel(), () => _ocrStop is { IsCancellationRequested: false });
        TeachCommand = new AsyncRelayCommand(() => AnswerAsync(OcrAnswer.Trim()), () => _ocrQuestion is not null && OcrAnswer.Trim().Length > 0);
        SpeckCommand = new AsyncRelayCommand(() => AnswerAsync(""), () => _ocrQuestion is not null);
        SkipShapeCommand = new AsyncRelayCommand(() => AnswerAsync(null), () => _ocrQuestion is not null);
        FinishReadingCommand = new AsyncRelayCommand(SaveReadTextAsync, () => _ocrQuestion is not null);
    }

    private void RefreshOcrCommands()
    {
        ReadTextCommand?.NotifyCanExecuteChanged();
        StopReadingTextCommand?.NotifyCanExecuteChanged();
        TeachCommand?.NotifyCanExecuteChanged();
        SpeckCommand?.NotifyCanExecuteChanged();
        SkipShapeCommand?.NotifyCanExecuteChanged();
        FinishReadingCommand?.NotifyCanExecuteChanged();
    }

    private async Task ReadTextAsync()
    {
        if (_selected is not { IsText: false } row || !PictureSubtitles.CanShow(row.Track))
        {
            return;
        }

        var file = _file;
        _busy = true;
        _ocrStop = new CancellationTokenSource();
        var stop = _ocrStop.Token;
        RefreshCommands();
        try
        {
            var templates = OcrFonts.TemplatesAsync();
            if (!_pictures.TryGetValue(row, out var cues) || cues.Count == 0)
            {
                if (row.ExternalPath is { } external)
                {
                    OcrStatus = "Čítam obrázkové titulky súboru…";
                    cues = await PgsReader.ReadFileAsync(external, stop);
                }
                else if (file is not null && _services.CurrentMkvToolNix is { } installation)
                {
                    OcrStatus = $"Čítam obrázkové titulky stopy {row.Track.Id} (číta sa celý súbor)…";
                    cues = await PictureSubtitles.ExtractAsync(installation, _services.Runner, file, row.Track, stop);
                }
                else
                {
                    OcrStatus = "Na čítanie stopy videa treba MKVToolNix.";
                    return;
                }

                if (ReferenceEquals(_file, file) && Rows.Contains(row))
                {
                    _pictures[row] = cues;
                }
            }

            if (cues.Count == 0)
            {
                OcrStatus = "Obrázkové titulky sa nepodarilo prečítať (žiadne titulky).";
                return;
            }

            OcrStatus = "Pripravujem písma Windows na porovnanie…";
            var letters = await templates;
            var language = row.Language.Tag.IsUndetermined ? null : row.Language.Tag.Primary;
            var memory = _ocrMemory ??= new OcrMemory(OcrMemoryFile);
            var progress = new Progress<double>(p => OcrStatus = $"Rozpoznávam text {cues.Count} titulkov… {p:P0}");
            var run = await Task.Run(() => new SubtitleOcr(letters, memory, language).Read(OcrPicture.Of(cues, stop), progress, stop), stop);
            if (!ReferenceEquals(_file, file) || !Rows.Contains(row))
            {
                OcrStatus = "";
                return;
            }

            _ocrRun = run;
            _ocrRow = row;
            _ocrUnknown = new Queue<OcrCluster>(run.Unknown);
            _ocrAsked = 0;
            _ocrTotal = _ocrUnknown.Count;
            var (all, known) = run.Coverage;
            if (_ocrTotal == 0)
            {
                _busy = false;
                await SaveReadTextAsync();
                return;
            }

            OcrStatus = $"Prečítaných {known} z {all} písmen ({run.Clusters.Count} rôznych tvarov). {_ocrTotal} tvarov UNIVERSUM nepozná – povedzte, čo sú: " +
                        "pýta sa raz za film a zapamätá si ich aj pre ďalšie filmy.";
            Ask();
        }
        catch (OperationCanceledException)
        {
            OcrStatus = "Rozpoznávanie je zastavené.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or MkvToolNixException or Core.Processes.ProcessStartException or TimeoutException)
        {
            OcrStatus = "Obrázkové titulky sa nepodarilo prečítať: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _ocrStop?.Dispose();
            _ocrStop = null;
            RefreshCommands();
        }
    }

    // the next unknown shape (one told meanwhile with another is not asked)
    private void Ask()
    {
        while (_ocrRun is { } run && _ocrUnknown.TryDequeue(out var cluster))
        {
            if (cluster.Text is not null)
            {
                _ocrAsked++;
                continue;
            }

            _ocrAsked++;
            var (line, x0, x1) = run.Context(cluster);
            OcrQuestion = new OcrQuestion(cluster, ShapePicture(cluster.Sample), LinePicture(line, x0, x1), _ocrAsked, _ocrTotal);
            OcrAnswer = "";
            return;
        }

        OcrQuestion = null;
    }

    // text: the letter(s) told; "": no letter; null: unknown
    private async Task AnswerAsync(string? text)
    {
        if (_ocrQuestion is not { } question || _ocrRun is not { } run)
        {
            return;
        }

        if (text is not null)
        {
            var told = run.Teach(question.Cluster, text);
            if (told > 1)
            {
                OcrStatus = $"„{(text.Length > 0 ? text : "nie je písmeno")}“ – platí aj pre {told - 1} podobných tvarov.";
            }
        }

        Ask();
        if (_ocrQuestion is null)
        {
            await SaveReadTextAsync();
        }
    }

    private async Task SaveReadTextAsync()
    {
        var run = _ocrRun;
        var row = _ocrRow;
        ForgetReading();
        if (run is null || row is null || !Rows.Contains(row) || Anchor is not { } anchor)
        {
            return;
        }

        var document = run.Document();
        if (document.Cues.Count == 0)
        {
            OcrStatus = "V obrázkoch sa nenašiel žiadny text.";
            return;
        }

        try
        {
            var language = row.Language.Tag.IsUndetermined ? "und" : row.Language.Tag.Primary;
            var saved = await SubtitleDownloads.SaveBesideAsync(anchor, document, language, row.HearingImpaired, row.Forced);
            _taken.Add(saved);
            var unknown = document.Cues.Sum(c => c.Text.Count(ch => ch == '�'));
            OcrStatus = $"Text rozpoznaný ({document.Cues.Count} titulkov) a uložený vedľa videa ako {Path.GetFileName(saved)}; pridaný ako nová titulková stopa." +
                        (unknown > 0 ? $" Neznámych písmen: {unknown} – sú ako „�“, opravíte ich v Časoch titulkov." : "") +
                        " Skontrolujte ho prehraním.";
            await AddFilesAsync([saved]);

            // the text at once in Časy titulkov
            SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.ExternalPath, saved, StringComparison.OrdinalIgnoreCase)) ?? SelectedRow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OcrStatus = "Rozpoznaný text sa nepodarilo uložiť: " + ex.Message;
        }
    }

    // another video or the row removed: the reading and its questions end
    private void ForgetReading()
    {
        _ocrStop?.Cancel();
        _ocrRun = null;
        _ocrRow = null;
        _ocrUnknown.Clear();
        OcrQuestion = null;
        RefreshOcrCommands();
    }

    // the shape alone, white on dark with a margin (shown enlarged without smoothing)
    private static BitmapSource ShapePicture(InkBitmap ink)
    {
        const int margin = 2;
        int width = ink.Width + (2 * margin), height = ink.Height + (2 * margin);
        var gray = new byte[width * height];
        Array.Fill(gray, (byte)40);
        for (var y = 0; y < ink.Height; y++)
        {
            for (var x = 0; x < ink.Width; x++)
            {
                if (ink[x, y])
                {
                    gray[((y + margin) * width) + x + margin] = 255;
                }
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, gray, width);
        bitmap.Freeze();
        return bitmap;
    }

    // the line: its letters white on dark, the asked shape yellow on brown
    private static BitmapSource LinePicture(InkBitmap ink, int shapeX0, int shapeX1)
    {
        var bgra = new byte[ink.Width * ink.Height * 4];
        for (var y = 0; y < ink.Height; y++)
        {
            for (var x = 0; x < ink.Width; x++)
            {
                var asked = x >= shapeX0 && x < shapeX1;
                (byte B, byte G, byte R) colour = ink[x, y] ? asked ? ((byte)0, (byte)220, (byte)255) : ((byte)255, (byte)255, (byte)255)
                    : asked ? ((byte)0, (byte)60, (byte)120) : ((byte)40, (byte)40, (byte)40);
                var at = ((y * ink.Width) + x) * 4;
                (bgra[at], bgra[at + 1], bgra[at + 2], bgra[at + 3]) = (colour.B, colour.G, colour.R, 255);
            }
        }

        var bitmap = BitmapSource.Create(ink.Width, ink.Height, 96, 96, PixelFormats.Bgra32, null, bgra, ink.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
