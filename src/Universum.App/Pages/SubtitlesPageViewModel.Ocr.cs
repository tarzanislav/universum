using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.MkvToolNix;
using Universum.Core.Subtitles;
using Universum.Core.Subtitles.Download;
using Universum.Core.Subtitles.Ocr;
using Universum.Core.Subtitles.Pictures;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>A letter shape the reading did not know, asked once for the whole film: the shape enlarged and the line it first stands in.</summary>
public sealed class OcrQuestion(OcrCluster cluster, BitmapSource shape, BitmapSource line, int number, int total)
{
    public OcrCluster Cluster { get; } = cluster;

    public BitmapSource Shape { get; } = shape;

    public BitmapSource Line { get; } = line;

    public string Title => $"Neznámy tvar {number} z {total} – vo filme {Cluster.Count}×";
}

/// <summary>A word the checking of the text changed, shown after saving; Vrátiť puts the word as read back.</summary>
public sealed class OcrCorrectionRow(OcrCorrection correction, RelayCommand<OcrCorrectionRow> revert)
{
    public OcrCorrection Correction { get; } = correction;

    public string Display => $"{SubtitleRow.Clock(Correction.At)}  „{Correction.Before}“ → „{Correction.After}“  ({Correction.Why})";

    public RelayCommand<OcrCorrectionRow> RevertCommand { get; } = revert;
}

/// <summary>
/// Text z obrázkov (owner, 31. 10. 2026: own, original, clean and efficient code): the picture subtitles (PGS) of the
/// selected track read by UNIVERSUM's own OCR – compared with letters drawn from the fonts of Windows; the text checked by
/// the language (the user's lexicon learned from their own subtitle files, the words read surely in the film); shapes
/// still unknown are asked once for the film and kept for other films. The text is saved next to the video as a new SRT
/// file (nothing is overwritten) and added as a new subtitle track; each correction can be put back.
/// </summary>
public sealed partial class SubtitlesPageViewModel
{
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub"];
    private readonly Dictionary<string, OcrLexicon> _lexicons = new(StringComparer.Ordinal);
    private CancellationTokenSource? _ocrStop;
    private CancellationTokenSource? _learnStop;
    private OcrRun? _ocrRun;
    private SubtitleRow? _ocrRow;
    private Core.Media.MediaFile? _ocrFile;
    private OcrLexicon? _ocrLexicon;
    private string? _ocrSaved;
    private Queue<OcrCluster> _ocrUnknown = new();
    private int _ocrAsked;
    private int _ocrTotal;
    private OcrQuestion? _ocrQuestion;
    private string _ocrAnswer = "";
    private string _ocrStatus = "";
    private string _lexiconSummary = "";
    private OcrMemory? _ocrMemory;

    /// <summary>Reads the text of the selected picture subtitles.</summary>
    public AsyncRelayCommand ReadTextCommand { get; private set; } = null!;

    /// <summary>Stops the reading or the learning of the lexicon.</summary>
    public RelayCommand StopReadingTextCommand { get; private set; } = null!;

    /// <summary>The asked shape is <see cref="OcrAnswer"/> (kept for other films).</summary>
    public AsyncRelayCommand TeachCommand { get; private set; } = null!;

    /// <summary>The asked shape is no letter (a speck): it is left out.</summary>
    public AsyncRelayCommand SpeckCommand { get; private set; } = null!;

    /// <summary>The asked shape stays unknown ("�" in the text, to be corrected by hand).</summary>
    public AsyncRelayCommand SkipShapeCommand { get; private set; } = null!;

    /// <summary>Saves the text now: the shapes not yet told stay unknown.</summary>
    public AsyncRelayCommand FinishReadingCommand { get; private set; } = null!;

    /// <summary>Learns the lexicon from the subtitle files of a folder (and its subfolders).</summary>
    public AsyncRelayCommand LearnLexiconCommand { get; private set; } = null!;

    /// <summary>The corrections of the last text read (Vrátiť puts a word back in the new track).</summary>
    public ObservableCollection<OcrCorrectionRow> OcrCorrections { get; } = [];

    public bool HasOcrCorrections => OcrCorrections.Count > 0;

    public string OcrStatus { get => _ocrStatus; private set => SetProperty(ref _ocrStatus, value); }

    /// <summary>How big the lexicon of each language is.</summary>
    public string LexiconSummary { get => _lexiconSummary; private set => SetProperty(ref _lexiconSummary, value); }

    /// <summary>The text read is checked by the language and mended (a setting, on by default).</summary>
    public bool OcrCheckText
    {
        get => _services.Settings.OcrCheckText;
        set
        {
            _services.Settings.OcrCheckText = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

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

    /// <summary>The lexicon of a language (words of the user's own subtitles), in UNIVERSUM's settings folder.</summary>
    public static string LexiconFile(string language) => Path.Combine(AppSettings.Directory, $"ocr-slovnik-{language}.json");

    // the subtitle files the lexicon learned (a file is learned once): path, size and time
    private static string LearnedFile => Path.Combine(AppSettings.Directory, "ocr-slovnik-subory.json");

    private void CreateOcrCommands()
    {
        ReadTextCommand = new AsyncRelayCommand(ReadTextAsync, () => !_busy && _ocrRun is null && _learnStop is null && _selected is { IsText: false } row &&
                                                                       PictureSubtitles.CanShow(row.Track) && Anchor is not null);
        StopReadingTextCommand = new RelayCommand(() =>
        {
            _ocrStop?.Cancel();
            _learnStop?.Cancel();
            RefreshOcrCommands();
        }, () => _ocrStop is { IsCancellationRequested: false } || _learnStop is { IsCancellationRequested: false });
        TeachCommand = new AsyncRelayCommand(() => AnswerAsync(OcrAnswer.Trim()), () => !_busy && _ocrQuestion is not null && OcrAnswer.Trim().Length > 0);
        SpeckCommand = new AsyncRelayCommand(() => AnswerAsync(""), () => !_busy && _ocrQuestion is not null);
        SkipShapeCommand = new AsyncRelayCommand(() => AnswerAsync(null), () => !_busy && _ocrQuestion is not null);
        FinishReadingCommand = new AsyncRelayCommand(FinishReadingAsync, () => !_busy && _ocrQuestion is not null);
        LearnLexiconCommand = new AsyncRelayCommand(LearnLexiconAsync, () => !_busy && _ocrRun is null && _learnStop is null);
        OcrCorrections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOcrCorrections));
        LexiconSummary = SummaryOfLexicons();
    }

    private void RefreshOcrCommands()
    {
        ReadTextCommand?.NotifyCanExecuteChanged();
        StopReadingTextCommand?.NotifyCanExecuteChanged();
        TeachCommand?.NotifyCanExecuteChanged();
        SpeckCommand?.NotifyCanExecuteChanged();
        SkipShapeCommand?.NotifyCanExecuteChanged();
        FinishReadingCommand?.NotifyCanExecuteChanged();
        LearnLexiconCommand?.NotifyCanExecuteChanged();
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
        OcrCorrections.Clear();
        RefreshCommands();
        var save = false;
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
            if (letters.Count == 0)
            {
                OcrStatus = "Vo Windows sa nenašlo žiadne z písiem na porovnanie (Arial, Verdana, Tahoma, Segoe UI …).";
                return;
            }

            var language = row.Language.Tag.IsUndetermined ? null : row.Language.Tag.Primary;
            var memory = _ocrMemory ??= new OcrMemory(OcrMemoryFile);
            var check = OcrCheckText;
            var lexicon = check && language is not null ? Lexicon(language) : null;
            var progress = new Progress<double>(p => OcrStatus = $"Rozpoznávam text {cues.Count} titulkov… {p:P0}");
            var run = await Task.Run(() =>
            {
                var read = new SubtitleOcr(letters, memory, language).Read(OcrPicture.Of(cues, stop), progress, stop);
                if (check)
                {
                    lock (_lexicons)
                    {
                        read.Check(lexicon);
                    }
                }

                return read;
            }, stop);
            if (!ReferenceEquals(_file, file) || !Rows.Contains(row))
            {
                OcrStatus = "";
                return;
            }

            _ocrRun = run;
            _ocrRow = row;
            _ocrFile = file;
            _ocrLexicon = check ? lexicon ?? new OcrLexicon() : null;
            _ocrUnknown = new Queue<OcrCluster>(run.Unknown);
            _ocrAsked = 0;
            _ocrTotal = _ocrUnknown.Count;
            var (all, known) = run.Coverage;

            // nothing to ask: saved once the reading is over (below, after the busy state ends)
            save = _ocrTotal == 0;
            if (!save)
            {
                OcrStatus = $"Prečítaných {known} z {all} písmen ({run.Clusters.Count} rôznych tvarov). {_ocrTotal} tvarov UNIVERSUM nepozná – povedzte, čo sú: " +
                            "pýta sa raz za film a zapamätá si ich aj pre ďalšie filmy.";
                Ask();
            }
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

        if (save)
        {
            await SaveReadTextAsync();
        }
    }

    // the next unknown shape (one told meanwhile with another is not asked)
    private void Ask()
    {
        while (_ocrRun is { } run && _ocrUnknown.TryDequeue(out var cluster))
        {
            _ocrAsked++;
            if (cluster.Text is not null)
            {
                continue;
            }

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
        if (_ocrQuestion is not { } question || _ocrRun is not { } run || _busy)
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

    private async Task FinishReadingAsync()
    {
        if (_busy)
        {
            return;
        }

        OcrQuestion = null;
        await SaveReadTextAsync();
    }

    // the text checked again with what the user told, saved next to the video and added as a track
    private async Task SaveReadTextAsync()
    {
        var run = _ocrRun;
        var row = _ocrRow;
        var file = _ocrFile;
        var lexicon = _ocrLexicon;
        ForgetReading();
        if (run is null || row is null || !Rows.Contains(row) || !ReferenceEquals(_file, file) || Anchor is not { } anchor || _busy)
        {
            return;
        }

        _busy = true;
        RefreshCommands();
        try
        {
            var document = await Task.Run(() =>
            {
                if (lexicon is not null)
                {
                    lock (_lexicons)
                    {
                        run.Check(lexicon);
                    }
                }

                return run.Document();
            });
            if (document.Cues.Count == 0)
            {
                OcrStatus = "V obrázkoch sa nenašiel žiadny text.";
                return;
            }

            var language = row.Language.Tag.IsUndetermined ? "und" : row.Language.Tag.Primary;
            var saved = await SubtitleDownloads.SaveBesideAsync(anchor, document, language, row.HearingImpaired, row.Forced);
            _taken.Add(saved);
            if (!ReferenceEquals(_file, file) || !Rows.Contains(row))
            {
                OcrStatus = $"Text je uložený vedľa videa ako {Path.GetFileName(saved)}; medzitým sa otvorilo iné video, stopa sa nepridala.";
                return;
            }

            var unknown = document.Cues.Sum(c => c.Text.Count(ch => ch == '�'));
            var corrections = run.Corrections;
            OcrStatus = $"Text rozpoznaný ({document.Cues.Count} titulkov) a uložený vedľa videa ako {Path.GetFileName(saved)}; pridaný ako nová titulková stopa." +
                        (corrections.Count > 0 ? $" Opravené podľa jazyka: {corrections.Count} (zoznam nižšie)." : "") +
                        (unknown > 0 ? $" Neznámych písmen: {unknown} – sú ako „�“, opravíte ich v Časoch titulkov." : "") +
                        " Skontrolujte ho prehraním.";
            _busy = false;
            await AddFilesAsync([saved]);
            _ocrSaved = saved;
            foreach (var correction in corrections)
            {
                OcrCorrections.Add(new OcrCorrectionRow(correction, new RelayCommand<OcrCorrectionRow>(Revert)));
            }

            // the text at once in Časy titulkov
            SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.ExternalPath, saved, StringComparison.OrdinalIgnoreCase)) ?? SelectedRow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OcrStatus = "Rozpoznaný text sa nepodarilo uložiť: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    // a correction put back: the word as read, in the new track (like a change in Časy titulkov)
    private void Revert(OcrCorrectionRow? item)
    {
        if (item is null || Rows.FirstOrDefault(r => string.Equals(r.ExternalPath, _ocrSaved, StringComparison.OrdinalIgnoreCase)) is not { Document: { } document } row)
        {
            return;
        }

        var index = item.Correction.Cue;
        if (index < 0 || index >= document.Cues.Count)
        {
            return;
        }

        var text = row.CueEdits.TryGetValue(index, out var edited) && edited is not null ? edited : document.Cues[index].Text;
        if (OcrWords.ReplaceWord(text, item.Correction.After, item.Correction.Before) is { } back)
        {
            row.EditCue(index, back);
            OcrCorrections.Remove(item);
            UpdatePreview();
            RefreshPlayer();
        }
    }

    // another video or the row removed: the reading and its questions end
    private void ForgetReading()
    {
        _ocrStop?.Cancel();
        _ocrRun = null;
        _ocrRow = null;
        _ocrFile = null;
        _ocrLexicon = null;
        _ocrUnknown.Clear();
        OcrQuestion = null;
        RefreshOcrCommands();
    }

    // the lexicon of a language, read from its file the first time
    private OcrLexicon Lexicon(string language)
    {
        lock (_lexicons)
        {
            if (!_lexicons.TryGetValue(language, out var lexicon))
            {
                _lexicons[language] = lexicon = OcrLexicon.Load(LexiconFile(language));
            }

            return lexicon;
        }
    }

    // the subtitle files of a folder read (each once), the language of each from its text, its words into that language's lexicon
    private async Task LearnLexiconAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Priečinok s vašimi titulkami (napr. knižnica) – UNIVERSUM sa z nich naučí slová" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        _learnStop = new CancellationTokenSource();
        var stop = _learnStop.Token;
        RefreshCommands();
        try
        {
            IProgress<string> progress = new Progress<string>(s => OcrStatus = s);
            var (files, words, languages) = await Task.Run(() => LearnFolderAsync(folder, progress, stop), stop);
            OcrStatus = files == 0
                ? "Nenašli sa žiadne nové súbory titulkov (SRT, ASS, VTT, SUB), z ktorých by sa UNIVERSUM ešte neučil."
                : $"Naučené: {files} súborov titulkov, {words:N0} slov ({string.Join(", ", languages)}). Slovník je len v tomto počítači.";
            LexiconSummary = SummaryOfLexicons();
        }
        catch (OperationCanceledException)
        {
            OcrStatus = "Učenie slovníka je zastavené – čo sa naučilo, ostáva uložené.";
            LexiconSummary = SummaryOfLexicons();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OcrStatus = "Slovník sa nepodarilo naučiť: " + ex.Message;
        }
        finally
        {
            _learnStop.Dispose();
            _learnStop = null;
            RefreshCommands();
        }
    }

    // on a thread of its own: the files read one after another; what was learned is saved also when stopped
    private async Task<(int Files, long Words, IReadOnlyList<string> Languages)> LearnFolderAsync(string folder, IProgress<string> progress, CancellationToken stop)
    {
        var learned = LoadLearned();
        var touched = new SortedSet<string>(StringComparer.Ordinal);
        var files = 0;
        long words = 0;
        try
        {
            var paths = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(p => SubtitleExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase));
            foreach (var path in paths)
            {
                stop.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                var key = $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                if (info.Length > 20_000_000 || learned.Contains(key))
                {
                    continue;
                }

                try
                {
                    var text = (await SubtitleReader.ReadFileAsync(path, cancellationToken: stop).ConfigureAwait(false)).PlainText;
                    learned.Add(key);
                    if (SubtitleLanguage.Detect(text) is not { } language)
                    {
                        continue;
                    }

                    var lexicon = Lexicon(language.Language);
                    lock (_lexicons)
                    {
                        var before = lexicon.Seen;
                        lexicon.Learn(text);
                        words += lexicon.Seen - before;
                    }

                    touched.Add(language.Language);
                    files++;
                    progress.Report($"Učím sa slová z vašich titulkov… {files} súborov, {words:N0} slov");
                }
                catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException or ArgumentException)
                {
                    // a file that is no text subtitles (a VobSub .sub) or cannot be read
                }
            }
        }
        finally
        {
            lock (_lexicons)
            {
                foreach (var language in touched)
                {
                    _lexicons[language].Save(LexiconFile(language));
                }
            }

            SaveLearned(learned);
        }

        return (files, words, touched.ToList());
    }

    private static HashSet<string> LoadLearned()
    {
        try
        {
            return File.Exists(LearnedFile) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(LearnedFile)) is { } keys
                ? new HashSet<string>(keys, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static void SaveLearned(HashSet<string> keys)
    {
        Directory.CreateDirectory(AppSettings.Directory);
        var temporary = LearnedFile + ".part";
        File.WriteAllText(temporary, JsonSerializer.Serialize(keys.Order(StringComparer.Ordinal).ToList()));
        File.Move(temporary, LearnedFile, overwrite: true);
    }

    // the languages of the lexicon files and the number of their words
    private string SummaryOfLexicons()
    {
        try
        {
            var languages = Directory.Exists(AppSettings.Directory)
                ? Directory.EnumerateFiles(AppSettings.Directory, "ocr-slovnik-*.json").Select(f => Path.GetFileNameWithoutExtension(f)["ocr-slovnik-".Length..])
                    .Where(l => l.Length is >= 2 and <= 3 && l.All(char.IsAsciiLetterLower)).Order(StringComparer.Ordinal).ToList()
                : [];
            return languages.Count == 0
                ? "Slovník je zatiaľ prázdny – oprava podľa jazyka použije len slová, ktoré UNIVERSUM vo filme prečíta isto. Naučte ho z priečinka s vašimi titulkami."
                : "Slovník z vašich titulkov: " + string.Join(", ", languages.Select(l => _lexicons.TryGetValue(l, out var lexicon) ? $"{l} {lexicon.Count:N0} slov" : l)) +
                  " (len v tomto počítači).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
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
