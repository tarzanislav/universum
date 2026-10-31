using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;
using Universum.Core.Subtitles;
using Universum.Core.Subtitles.Download;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>A subtitle file found with the video (owner, 30. 10. 2026, bod 5): why it belongs, added with one click.</summary>
public sealed record FoundSubtitle(SubtitleFileMatch Match, RelayCommand AddCommand)
{
    public string Display => $"{Path.GetFileName(Match.Path)} – {Match.Reason}" + (Match.Language is { } language ? $", jazyk z názvu {language.Primary}" : "") +
                             (Match.Forced ? ", forced" : "") + (Match.HearingImpaired ? ", pre nepočujúcich" : "");
}

/// <summary>
/// A subtitle of the list Časy titulkov: its time now, the time it gets (the player goes there), its text – which can be
/// changed when <see cref="Index"/> says which subtitle it is (lines shown with " | ").
/// </summary>
public sealed class SubtitlePreviewRow(string number, string before, string after, string text, TimeSpan? at = null, int? index = null, Action<int, string>? edit = null)
{
    private string _text = text;

    public string Number { get; } = number;

    public string Before { get; } = before;

    public string After { get; } = after;

    public TimeSpan? At { get; } = at;

    /// <summary>The place of the subtitle in the subtitles as read (null: it cannot be changed here).</summary>
    public int? Index { get; } = index;

    public bool IsEditable => Index is not null && edit is not null;

    public string Text
    {
        get => _text;
        set
        {
            if (value != _text && Index is { } place && edit is not null)
            {
                _text = value;
                edit(place, value);
            }
        }
    }
}

/// <summary>
/// Titulky (owner, 30. 10. 2026): the subtitles of a video or of subtitle files – Zmena FPS titulkov, Zmena rýchlosti
/// titulkov (percentá), Posun (Delay in ms), Konverzia do SRT from every text format, external subtitles added to the
/// video, and repairs (synchronizácia podľa dvoch titulkov, prvky pre nepočujúcich, formátovanie, prekrývanie časov).
/// A video is saved as a new file in the library (mkvmerge changes the times: --sync); subtitle files as new SRT files
/// next to them. Originals are never changed.
/// </summary>
public sealed partial class SubtitlesPageViewModel : ObservableObject
{
    private const int FirstExternalId = 1000;
    private readonly AppServices _services;
    private MediaFile? _file;
    private SubtitleRow? _selected;
    private string _fileName = "";
    private string _summary = "Otvorte video (MKV, MP4 …) alebo súbory titulkov (SRT, ASS, VTT, TTML, SUB, TXT …), alebo ich sem pretiahnite myšou.";
    private string _result = "";
    private bool _busy;
    private bool _useRules = true;
    private int _nextId = FirstExternalId;

    public SubtitlesPageViewModel(AppServices services)
    {
        _services = services;
        SearchOnlineCommand = new AsyncRelayCommand(SearchOnlineAsync, () => !_busy && (_file is not null || Rows.Count > 0));
        DownloadOnlineCommand = new AsyncRelayCommand<OpenSubtitlesResult>(DownloadOnlineAsync, r => !_busy && r is not null);
        SearchTitulkyCommand = new RelayCommand(SearchTitulky, () => !_busy && (_file is not null || Rows.Count > 0));
        TranslateCommand = new AsyncRelayCommand(TranslateAsync, () => !_busy && _selected is { IsText: true } && _services.SvSubToken is not null);
        SyncCommand = new AsyncRelayCommand(SyncAsync, () => !_busy && _selected is not null &&
                                                             ((SyncByAudio && _file is not null) || (SyncBySubtitles && SyncReference is not null && SyncReference != _selected)));
        CheckTracksCommand = new AsyncRelayCommand(CheckTracksAsync, () => !_busy && _file is { IsMatroska: true } && _file.TracksOf(TrackType.Audio).Any());
        StopSyncCommand = new RelayCommand(() => _syncCancellation?.Cancel(), () => _syncCancellation is { IsCancellationRequested: false });
        ApplyProposalCommand = new RelayCommand(ApplyProposal, () => !_busy && _proposal is not null);
        Library = new LibraryTargetPicker(services, Target);
        Library.Changed += RefreshCommands;
        OpenVideoCommand = new AsyncRelayCommand(OpenVideoAsync, () => !_busy);
        OpenSubtitlesCommand = new AsyncRelayCommand(OpenSubtitlesAsync, () => !_busy);
        AddExternalCommand = new AsyncRelayCommand(AddExternalAsync, () => !_busy && _file is not null);
        RemoveCommand = new RelayCommand(Remove, () => !_busy && _selected is { IsExternal: true });
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        Player = new Player.PlayerViewModel(services);
        CreateTaskCommands();
        CreateOcrCommands();
        StartsNowCommand = new RelayCommand(StartsNow, () => Player.IsOpen && _selected is not null);
        MarkFirstCommand = new RelayCommand(() => MarkPoint(first: true), () => Player.IsOpen && _selected is not null);
        MarkSecondCommand = new RelayCommand(() => MarkPoint(first: false), () => Player.IsOpen && _selected is not null);
        SeekToCueCommand = new AsyncRelayCommand<SubtitlePreviewRow>(SeekToCueAsync);
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Player.IsOpen))
            {
                RefreshCommands();
            }
        };
        ClearCommand = new RelayCommand(() =>
        {
            Clear();
            FileName = "";
            Summary = "Otvorte video alebo súbory titulkov, alebo ich sem pretiahnite myšou.";
            RefreshCommands();
        }, () => !_busy && (_file is not null || Rows.Count > 0));
    }

    /// <summary>OpenSubtitles.com: search the subtitles of the title (the language, SDH).</summary>
    public AsyncRelayCommand SearchOnlineCommand { get; }

    /// <summary>OpenSubtitles.com: download one, save it next to the video and add it.</summary>
    public AsyncRelayCommand<OpenSubtitlesResult> DownloadOnlineCommand { get; }

    /// <summary>titulky.com: the search in the browser; the downloaded file is taken from Downloads.</summary>
    public RelayCommand SearchTitulkyCommand { get; }

    /// <summary>svsub.net: translates the selected subtitles into <see cref="TranslateTo"/>.</summary>
    public AsyncRelayCommand TranslateCommand { get; }

    /// <summary>Automatic sync of the selected subtitles (owner, 30. 10. 2026: by the audio, other subtitles or both).</summary>
    public AsyncRelayCommand SyncCommand { get; private set; } = null!;

    /// <summary>Checks every subtitle track of the video against its audio: late, early, another frame rate.</summary>
    public AsyncRelayCommand CheckTracksCommand { get; private set; } = null!;

    /// <summary>Stops automatic sync or the check of the tracks (FFmpeg reads parts of the film).</summary>
    public RelayCommand StopSyncCommand { get; private set; } = null!;

    /// <summary>Applies a proposal of automatic sync that is not sure (owner checks it by playing).</summary>
    public RelayCommand ApplyProposalCommand { get; private set; } = null!;

    /// <summary>Automatic sync found something, but not for sure: it is applied only by <see cref="ApplyProposalCommand"/>.</summary>
    public bool HasProposal => _proposal is not null;

    /// <summary>Sync by the audio of the film.</summary>
    public bool SyncByAudio { get => _syncByAudio; set { if (SetProperty(ref _syncByAudio, value)) { RefreshCommands(); } } }

    /// <summary>Sync by other subtitles that fit the film (<see cref="SyncReference"/>).</summary>
    public bool SyncBySubtitles { get => _syncBySubtitles; set { if (SetProperty(ref _syncBySubtitles, value)) { RefreshCommands(); } } }

    /// <summary>The subtitles that fit the film, for sync by subtitles.</summary>
    public SubtitleRow? SyncReference { get => _syncReference; set { if (SetProperty(ref _syncReference, value)) { RefreshCommands(); } } }

    /// <summary>The report of automatic sync and of the check of the tracks.</summary>
    public string SyncStatus { get => _syncStatus; private set => SetProperty(ref _syncStatus, value); }

    private bool _syncByAudio = true;
    private bool _syncBySubtitles;
    private SubtitleRow? _syncReference;
    private string _syncStatus = "";
    private (string Key, IReadOnlyList<Core.Subtitles.Sync.ActivityWindow> Windows)? _speech;
    private CancellationTokenSource? _syncCancellation;
    private (SubtitleRow Row, Core.Subtitles.Sync.SyncResult Result)? _proposal;

    /// <summary>The results of OpenSubtitles.com.</summary>
    public ObservableCollection<OpenSubtitlesResult> OnlineResults { get; } = [];

    /// <summary>The languages subtitles are downloaded in and translated into.</summary>
    public IReadOnlyList<string> OnlineLanguages { get; } = ["en", "cs", "sk"];

    private string _onlineLanguage = "en";
    private string _hearingImpairedFilter = "všetky";
    private string _translateTo = "cs";
    private string _onlineStatus = "";
    private System.Windows.Threading.DispatcherTimer? _downloadsWatch;
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The language searched on OpenSubtitles.com (owner: English, also SDH).</summary>
    public string OnlineLanguage { get => _onlineLanguage; set => SetProperty(ref _onlineLanguage, value); }

    public IReadOnlyList<string> HearingImpairedFilters { get; } = ["všetky", "len SDH", "bez SDH"];

    public string HearingImpairedFilter { get => _hearingImpairedFilter; set => SetProperty(ref _hearingImpairedFilter, value); }

    /// <summary>The language svsub.net translates into (owner: mostly "cz" = cs; also sk, en).</summary>
    public string TranslateTo { get => _translateTo; set => SetProperty(ref _translateTo, value); }

    public string OnlineStatus { get => _onlineStatus; private set => SetProperty(ref _onlineStatus, value); }

    private string _outputFormat = "SRT";

    /// <summary>The format subtitle files are saved in (owner, 30. 10. 2026: WebVTT besides SRT).</summary>
    public string OutputFormat { get => _outputFormat; set => SetProperty(ref _outputFormat, value); }

    public IReadOnlyList<string> OutputFormats { get; } = ["SRT", "WebVTT"];

    /// <summary>Forgets the video and the subtitles (nothing changes on the disk); subtitle files dropped then are opened on their own.</summary>
    public RelayCommand ClearCommand { get; }

    public AsyncRelayCommand OpenVideoCommand { get; }

    public AsyncRelayCommand OpenSubtitlesCommand { get; }

    public AsyncRelayCommand AddExternalCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>The subtitle tracks of the video and the added files – or the opened subtitle files.</summary>
    public ObservableCollection<SubtitleRow> Rows { get; } = [];

    /// <summary>Subtitle files lying with the video that belong to it and are not added yet.</summary>
    public ObservableCollection<FoundSubtitle> Found { get; } = [];

    public bool HasFound => Found.Count > 0;

    /// <summary>
    /// Times before and after of every subtitle of the selected ones (a track of the video before it is read: examples);
    /// replaced as a whole on each change (thousands of subtitles).
    /// </summary>
    public IReadOnlyList<SubtitlePreviewRow> Preview { get => _preview; private set => SetProperty(ref _preview, value); }

    private IReadOnlyList<SubtitlePreviewRow> _preview = [];

    /// <summary>The subtitle chosen in Časy titulkov (it stays chosen when the list is made anew).</summary>
    public SubtitlePreviewRow? SelectedPreview { get => _selectedPreview; set => SetProperty(ref _selectedPreview, value); }

    private SubtitlePreviewRow? _selectedPreview;
    private SubtitleRow? _previewOf;

    public SubtitleRow? SelectedRow
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                UpdatePreview();
                RefreshPlayer();
                RefreshCommands();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Result { get => _result; private set => SetProperty(ref _result, value); }

    /// <summary>A video is open (else subtitle files are).</summary>
    public bool IsVideo => _file is not null;

    public bool IsFiles => _file is null && Rows.Count > 0;

    /// <summary>The UNIVERSUM rules order the tracks and set the flags (Forced, default) – on by default.</summary>
    public bool UseRules { get => _useRules; set { if (SetProperty(ref _useRules, value)) { Changed(); } } }

    /// <summary>Where the video goes in the library.</summary>
    public LibraryTargetPicker Library { get; }

    public string? FilePath => _file?.Path;

    /// <summary>Opens a video or subtitle files (also drag and drop).</summary>
    public async Task LoadAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || _busy)
        {
            return;
        }

        if (paths.All(IsSubtitleFile))
        {
            if (_file is not null)
            {
                await AddFilesAsync(paths);
            }
            else
            {
                await LoadFilesAsync(paths);
            }

            return;
        }

        await LoadVideoAsync(paths[0]);
    }

    private static bool IsSubtitleFile(string path) => ExternalSubtitles.Extensions.Contains(Path.GetExtension(path)) || Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase);

    private async Task OpenVideoAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Video|*.mkv;*.mp4;*.m4v;*.avi;*.ts;*.m2ts;*.mov;*.webm;*.mpg|Všetky súbory|*.*" };
        if (dialog.ShowDialog() == true)
        {
            await LoadVideoAsync(dialog.FileName);
        }
    }

    private async Task OpenSubtitlesAsync()
    {
        if (Pick() is { } files)
        {
            await LoadFilesAsync(files);
        }
    }

    private async Task AddExternalAsync()
    {
        if (Pick() is { } files)
        {
            await AddFilesAsync(files);
        }
    }

    private static string[]? Pick()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Titulky|*.srt;*.vtt;*.webvtt;*.ass;*.ssa;*.ttml;*.dfxp;*.xml;*.smi;*.sami;*.sub;*.txt;*.mpl;*.sup;*.idx|Všetky súbory|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    private async Task LoadVideoAsync(string path)
    {
        Clear();
        FileName = Path.GetFileName(path);
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Summary = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            return;
        }

        _busy = true;
        RefreshCommands();
        try
        {
            _file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
            foreach (var track in _file.TracksOf(TrackType.Subtitles))
            {
                var forced = UniversumRules.IsForced(_file, track, _services.Profile);
                var hearingImpaired = track.Flags.HearingImpaired || Core.Text.Words.ContainsAny(track.Name, _services.Profile.HearingImpairedKeywords);
                Rows.Add(new SubtitleRow(track, null, null, track.Language, track.Name, forced, hearingImpaired, ForcedSubtitles.IsEmpty(track) ? "prázdna stopa – pravidlá ju odstránia" : "", Changed));
            }

            var fps = _file.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond;
            Summary = $"{_file.ContainerName}, titulkové stopy: {Rows.Count}" + (fps is { } rate ? $", video {rate.ToString("0.###", CultureInfo.GetCultureInfo("sk-SK"))} FPS" : "") +
                      (_file.IsMatroska ? "" : " – zmeny textu (konverzia, opravy) sú len pri MKV; časy a pridanie titulkov fungujú.");
            Library.Guess(path);
            FindBeside(path);
            // owner, 30. 10. 2026: the film plays here with its subtitles (paused at its start)
            PlayerOpening = Player.OpenAsync(_file);
        }
        catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException or TimeoutException)
        {
            Summary = ex.Message;
        }
        finally
        {
            _busy = false;
            Changed();
        }

        await RestoreWorkAsync();
    }

    // owner, bod 5: subtitle files lying with the video that belong to it
    private void FindBeside(string video)
    {
        Found.Clear();
        try
        {
            var folder = Path.GetDirectoryName(video)!;
            var files = Directory.EnumerateFiles(folder).ToList();
            var videos = files.Count(f => BatchAnalyzer.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            foreach (var match in ExternalSubtitles.For(video, files, Math.Max(1, videos), _services.Profile.ForcedKeywords))
            {
                FoundSubtitle? found = null;
                found = new FoundSubtitle(match, new RelayCommand(() =>
                {
                    Found.Remove(found!);
                    OnPropertyChanged(nameof(HasFound));
                    _ = AddFilesAsync([match.Path], match);
                }));
                Found.Add(found);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the folder cannot be read: nothing found
        }

        OnPropertyChanged(nameof(HasFound));
    }

    private async Task LoadFilesAsync(IReadOnlyList<string> paths)
    {
        Clear();
        FileName = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} súborov titulkov";
        await AddFilesAsync(paths);
        Summary = "Súbory titulkov – uložia sa ako nové súbory SRT (UTF-8) vedľa pôvodných; pôvodné ostanú nezmenené.";
    }

    // external subtitle files: read, their language from the text (návrh UNIVERSUM) or the name
    private async Task AddFilesAsync(IReadOnlyList<string> paths, SubtitleFileMatch? known = null)
    {
        _busy = true;
        RefreshCommands();
        var problems = new List<string>();
        try
        {
            var fps = _file?.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond;
            foreach (var path in paths.Where(p => Rows.All(r => !string.Equals(r.ExternalPath, p, StringComparison.OrdinalIgnoreCase))))
            {
                var match = known is not null && known.Path == path ? known : ExternalSubtitles.For(path, [path]).FirstOrDefault() ?? Named(path);
                var picture = ExternalSubtitles.PictureExtensions.Contains(Path.GetExtension(path));
                if (picture)
                {
                    if (_file is null)
                    {
                        problems.Add($"{Path.GetFileName(path)}: obrazové titulky sa dajú len pridať k videu.");
                        continue;
                    }

                    var codec = Path.GetExtension(path).Equals(".sup", StringComparison.OrdinalIgnoreCase) ? "S_HDMV/PGS" : "S_VOBSUB";
                    var track = new MediaTrack { Id = _nextId++, Type = TrackType.Subtitles, CodecId = codec, Codec = codec == "S_VOBSUB" ? "VobSub" : "PGS", ExternalFile = path };
                    Rows.Add(new SubtitleRow(track, null, path, match.Language ?? LanguageTag.Undetermined, null, match.Forced, match.HearingImpaired, "obrazové titulky – jazyk podľa názvu", Changed));
                    continue;
                }

                SubtitleDocument document;
                try
                {
                    document = await SubtitleReader.ReadFileAsync(path, fps);
                }
                catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    continue;
                }

                var language = match.Language ?? LanguageTag.Undetermined;
                var note = $"{document.Format}, {document.Encoding}" + (document.FramesPerSecond is { } rate ? $", {rate.ToString("0.###", CultureInfo.GetCultureInfo("sk-SK"))} FPS" : "");
                if (SubtitleLanguage.Detect(document.PlainText) is { } read && read.Replaces(language))
                {
                    language = LanguageTag.FromCode(read.Language);
                    note += $" · návrh UNIVERSUM: {(read.Certain ? "isté" : "najpravdepodobnejšie")} – {read.Detail}";
                }

                var synthetic = new MediaTrack
                {
                    Id = _nextId++, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8", Codec = "SubRip/SRT", IsTextSubtitles = true,
                    StatisticsFrames = document.Cues.Count, ExternalFile = path,
                };
                Rows.Add(new SubtitleRow(synthetic, document, path, language, null, match.Forced, match.HearingImpaired, note, Changed));
            }
        }
        finally
        {
            _busy = false;
            Result = problems.Count == 0 ? "" : "Niektoré súbory sa nedali prečítať:\n" + string.Join("\n", problems);
            SelectedRow ??= Rows.FirstOrDefault();
            Changed();
        }
    }

    // a file added by hand: what its name says
    private SubtitleFileMatch Named(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var words = Words.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var language = words.Reverse().Select(w => w switch { "cz" or "czech" => "cs", "slovak" => "sk", "english" => "en", _ => w })
            .Select(w => w.Length is 2 or 3 ? Languages.FindCommon(w) : null).FirstOrDefault(l => l is not null);
        return new SubtitleFileMatch(path, 0, "pridané ručne", language is null ? null : LanguageTag.FromCode(language.Ietf),
            Core.Text.Words.ContainsAny(name, _services.Profile.ForcedKeywords), Core.Text.Words.ContainsAny(name, _services.Profile.HearingImpairedKeywords));
    }

    private void Remove()
    {
        if (_selected is { IsExternal: true } row)
        {
            if (SyncReference == row)
            {
                SyncReference = null;
            }

            if (Proposal?.Row == row)
            {
                Proposal = null;
            }

            if (row == _ocrRow)
            {
                ForgetReading();
                OcrStatus = "";
            }

            Rows.Remove(row);
            _pictures.Remove(row);
            _reading.Remove(row);
            SelectedRow = Rows.FirstOrDefault();
            if (_file is not null)
            {
                FindBeside(_file.Path);
                foreach (var found in Found.Where(f => Rows.Any(r => string.Equals(r.ExternalPath, f.Match.Path, StringComparison.OrdinalIgnoreCase))).ToList())
                {
                    Found.Remove(found);
                }

                OnPropertyChanged(nameof(HasFound));
            }

            Changed();
        }
    }

    private void Clear()
    {
        _file = null;
        _batchNames = [];
        SyncReference = null;
        Proposal = null;
        _speech = null;
        Rows.Clear();
        Found.Clear();
        ShowPreview(null, []);
        _pictures.Clear();
        ForgetReading();
        OcrCorrections.Clear();
        OcrStatus = "";
        _reading.Clear();
        _readingStop.Cancel();
        _readingStop.Dispose();
        _readingStop = new CancellationTokenSource();
        CompareRow = null;
        _ = Player.CloseAsync();
        HasWork = false;
        WorkNote = "";
        if (_task is not null)
        {
            EndTask();
        }
        SelectedRow = null;
        Result = "";
        _nextId = FirstExternalId;
        OnPropertyChanged(nameof(HasFound));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsFiles));
    }

    private void Changed()
    {
        // many values set at once (a saved work, a task opened again): computed once at the end
        if (_quiet > 0)
        {
            return;
        }

        OnPropertyChanged(nameof(Blocked));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsFiles));
        ShowNames();
        UpdatePreview();
        RefreshPlayer();
        Library?.Update();
        RefreshCommands();
    }

    // the names the tracks would get without the user's (the rules' or, in a task, the batch's) shown in their rows
    private void ShowNames()
    {
        if (_file is null)
        {
            return;
        }

        var target = Build(_file, Rows.ToDictionary(r => r, r => r.Track), names: false).Target;

        foreach (var row in Rows)
        {
            if (target.Tracks.FirstOrDefault(t => t.TrackId == row.Track.Id) is { } track)
            {
                row.ShowName(track.Name);
            }
        }
    }

    private void UpdatePreview()
    {
        if (_selected is not { } row)
        {
            ShowPreview(null, []);
            return;
        }

        var timing = row.Problem is null ? row.Timing : SubtitleTiming.None;
        if (row.CanEditCues && row.Document is { } read)
        {
            // every subtitle as read, with its own text changes – each can be changed (or left out) here
            var rows = new List<SubtitlePreviewRow>(read.Cues.Count);
            for (var index = 0; index < read.Cues.Count; index++)
            {
                if (row.CueEdits.TryGetValue(index, out var changed) && changed is null)
                {
                    continue;
                }

                var cue = read.Cues[index];
                rows.Add(PreviewRow((index + 1).ToString(CultureInfo.InvariantCulture), cue.Start, timing, changed ?? cue.Text, index, row));
            }

            ShowPreview(row, rows);
            return;
        }

        if (row.Repaired(row.Document) is { } document)
        {
            ShowPreview(row, document.Cues.Select((cue, at) => PreviewRow((at + 1).ToString(CultureInfo.InvariantCulture), cue.Start, timing, cue.Text, null, row)).ToList());
            return;
        }

        if (_pictures.TryGetValue(row, out var pictures))
        {
            ShowPreview(row, pictures.Select((cue, at) => PreviewRow((at + 1).ToString(CultureInfo.InvariantCulture), cue.Start, timing, "(obrázok)", null, row)).ToList());
            return;
        }

        // a track of the video not read yet: where subtitles at a few times end up
        ShowPreview(row, new[] { 1, 10, 30, 60, 90 }.Select(m => TimeSpan.FromMinutes(m)).Where(t => _file?.Duration is not { } d || t <= d)
            .Select(at => new SubtitlePreviewRow("", SubtitleRow.Clock(at), SubtitleRow.Clock(timing.Apply(at)), "(príklad času)")).ToList());
    }

    // the list made anew (a change of time or of a text): the subtitle chosen in it stays chosen – the next one when it was
    // left out; another track starts with none
    private void ShowPreview(SubtitleRow? row, IReadOnlyList<SubtitlePreviewRow> rows)
    {
        var chosen = ReferenceEquals(row, _previewOf) ? _selectedPreview : null;
        _previewOf = row;
        Preview = rows;
        SelectedPreview = chosen is null ? null
            : chosen.Index is { } index && rows.Any(r => r.Index is not null) ? rows.FirstOrDefault(r => r.Index >= index) ?? rows.LastOrDefault()
            : rows.FirstOrDefault(r => r.Number.Length > 0 && r.Number == chosen.Number);
    }

    private SubtitlePreviewRow PreviewRow(string number, TimeSpan start, SubtitleTiming timing, string text, int? index, SubtitleRow row)
    {
        var moved = timing.Apply(start);
        return new SubtitlePreviewRow(number, SubtitleRow.Clock(start), moved < TimeSpan.Zero ? "vypadne (pred začiatkom)" : SubtitleRow.Clock(moved),
            text.Replace("\n", " | ", StringComparison.Ordinal), moved, index,
            index is null ? null : (place, value) => EditCueLater(row, place, value));
    }

    // a text changed in the list ("|" between its lines, as the list shows them; nothing left: the subtitle is left out) –
    // after the list has finished its edit, as the list is made anew then
    private static void EditCueLater(SubtitleRow row, int index, string value)
    {
        // " | " (as the list shows a new line); a "|" in a word ("|t's" read from a picture) stays
        var lines = LineSeparator().Split(value).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var text = lines.Length == 0 ? null : string.Join('\n', lines);
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => row.EditCue(index, text));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+\|\s+")]
    private static partial System.Text.RegularExpressions.Regex LineSeparator();

    /// <summary>Why the video cannot be written by the rules (a track of an undetermined language …); empty when it can.</summary>
    public string Blocked => _file is null || !UseRules || Target() is not { IsBlocked: true } target ? ""
        : "Pravidlá UNIVERSUM: " + string.Join(" ", target.Issues.Where(i => i.IsBlocking).Select(i => i.Kind switch
        {
            RuleIssueKind.UndeterminedLanguage => $"stopa {(_file.Tracks.FirstOrDefault(t => t.Id == i.TrackId) is { } track ? $"{track.Id} ({Texts.Type(track.Type)})" : "")} má neurčený jazyk.",
            RuleIssueKind.MultipleVideoTracks => "súbor má viac video stôp.",
            RuleIssueKind.NoAudio => "súbor nemá zvukovú stopu.",
            _ => i.Kind.ToString(),
        }).Distinct()) + " Určte ho v Úprave hlavičky alebo v Single spracovaní – alebo vypnite pravidlá (stopy ostanú, ako sú).";

    // the names of the result: the batch's for the video's own tracks (a task), the ones the user wrote over them
    private Dictionary<int, string> NamesOf(Dictionary<int, SubtitleRow> rowOf, bool written)
    {
        var names = rowOf.Where(r => r.Value is { Include: true, IsExternal: false } && _batchNames.ContainsKey(r.Value.Track.Id))
            .ToDictionary(r => r.Key, r => _batchNames[r.Value.Track.Id]);
        if (written)
        {
            foreach (var (id, row) in rowOf.Where(r => r.Value is { Include: true, WrittenName: not null }))
            {
                names[id] = row.WrittenName!;
            }
        }

        return names;
    }

    private bool CanSave() =>
        !_busy && Rows.Count > 0 && Rows.All(r => r.Problem is null) && Blocked.Length == 0 &&
        (_file is null
            ? Rows.Any(r => r.IsText)
            : Library.Placement is not null && (Target() is { HasChanges: true } || Rows.Any(r => r is { Include: true, NeedsText: true, IsExternal: false })));

    /// <summary>The video with the subtitles as chosen – without the text changes (they are written when saving); null without a video.</summary>
    public TargetState? Target() => _file is null ? null : Build(_file, Rows.ToDictionary(r => r, r => r.Track)).Target;

    // the target state of the video: the rows' tracks (an external file or a rewritten track of the video by its track)
    // names: with the names the user wrote (false: what the tracks would be called without them)
    private (TargetState Target, HashSet<int> Replaced) Build(MediaFile file, IReadOnlyDictionary<SubtitleRow, MediaTrack> written, bool names = true)
    {
        var tracks = new List<MediaTrack>();
        var replaced = new HashSet<int>();
        var rowOf = new Dictionary<int, SubtitleRow>();
        foreach (var track in file.Tracks)
        {
            tracks.Add(track);
            if (Rows.FirstOrDefault(r => !r.IsExternal && r.Track.Id == track.Id) is { } row)
            {
                var result = written[row];
                if (result.Id != track.Id)
                {
                    // the text of the track is written anew: the new file takes its place
                    replaced.Add(track.Id);
                    tracks.Add(result with { Flags = result.Flags with { HearingImpaired = row.HearingImpaired } });
                }

                rowOf[result.Id] = row;
            }
        }

        foreach (var row in Rows.Where(r => r.IsExternal))
        {
            tracks.Add(written[row] with { Flags = new TrackFlags(Default: false, HearingImpaired: row.HearingImpaired) });
            rowOf[written[row].Id] = row;
        }

        var augmented = file with { Tracks = tracks };
        var removed = replaced.Concat(rowOf.Where(r => !r.Value.Include).Select(r => r.Key)).ToHashSet();
        TargetState target;
        if (UseRules)
        {
            var decisions = new TrackDecisions
            {
                Languages = rowOf.Where(r => r.Value.Include).ToDictionary(r => r.Key, r => r.Value.Language.Tag),
                Forced = rowOf.Where(r => r.Value is { Include: true, ForcedDecision: not null }).ToDictionary(r => r.Key, r => r.Value.ForcedDecision!.Value),
                Removed = removed,
                Kept = rowOf.Where(r => r.Value.Include).Select(r => r.Key).ToHashSet(),
                Names = NamesOf(rowOf, names),
            };
            target = UniversumRules.Apply(augmented, _services.Profile, decisions);

            // "pre nepočujúcich" changed by the user (the rules take it from the file and the name)
            target = target with
            {
                Tracks = target.Tracks.Select(t => rowOf.TryGetValue(t.TrackId, out var row) && row.HearingImpaired != row.InitialHearingImpaired && !row.IsExternal
                    ? t with { Flags = t.Flags with { HearingImpaired = row.HearingImpaired } }
                    : t).ToList(),
            };
        }
        else
        {
            var edits = tracks.Where(t => !removed.Contains(t.Id)).Select(t => rowOf.TryGetValue(t.Id, out var row)
                ? new TrackEdit(t.Id, (names ? row.WrittenName : null) ?? row.InitialName, row.Language.Tag, t.Flags with { Forced = row.Forced, HearingImpaired = row.HearingImpaired })
                : new TrackEdit(t.Id, t.Name, t.Language, t.Flags)).ToList();
            target = ManualTarget.Create(augmented, edits, removed, title: "");
        }

        target = target with
        {
            Tracks = target.Tracks.Select(t => rowOf.TryGetValue(t.TrackId, out var row) && !row.Timing.IsNone ? t with { Timing = row.Timing } : t).ToList(),
        };
        return (target, replaced);
    }

    private async Task SaveAsync()
    {
        if (_file is null)
        {
            await SaveFilesAsync();
            return;
        }

        if (_services.CurrentMkvToolNix is not { } installation || Library.Placement is not { } placement)
        {
            return;
        }

        _busy = true;
        RefreshCommands();
        var work = Path.Combine(Path.GetTempPath(), "UNIVERSUM", "titulky-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            var written = new Dictionary<SubtitleRow, MediaTrack>();
            var number = 0;
            foreach (var row in Rows)
            {
                if (!row.Include || !row.NeedsText)
                {
                    written[row] = row.Track;
                    continue;
                }

                var document = row.Document;
                if (document is null)
                {
                    Result = $"Čítam celú titulkovú stopu {row.Track.Id} (číta sa celý súbor)…";
                    document = await SubtitleText.ExtractDocumentAsync(installation, _services.Runner, _file, row.Track);
                    if (document is null)
                    {
                        Result = $"Titulkovú stopu {row.Track.Id} sa nepodarilo prečítať – nič sa nezapísalo.";
                        return;
                    }

                    row.Document = document;
                }

                var srt = Path.Combine(work, $"{++number:00}.srt");
                await SrtWriter.SaveAsync(row.Repaired(document)!, srt);
                written[row] = row.Track with
                {
                    Id = row.IsExternal ? row.Track.Id : _nextId++, ExternalFile = srt, CodecId = "S_TEXT/UTF8", Codec = "SubRip/SRT", IsTextSubtitles = true,
                    StatisticsFrames = row.Repaired(document)!.Cues.Count, StatisticsBytes = null, Number = null, Uid = null,
                };
            }

            var (target, _) = Build(_file, written);
            Result = "Zapisujem nový súbor…";
            var plan = EditPlan.For(target);
            var started = DateTime.UtcNow;
            var result = await _services.Processor(installation).ProcessAsync(plan, placement, new OutputOptions(OriginalHandling.Keep));
            _services.LogProcessing("Úprava titulkostopy", plan, result, started);
            Result = result.Status switch
            {
                ProcessingStatus.Done => $"Uložené do knižnice: {result.OutputPath}. Originál ostal nezmenený.",
                ProcessingStatus.AlreadyInLibrary => $"Rovnaký výsledok už v knižnici je: {result.OutputPath}. Nič sa nezapísalo.",
                _ => "Nepodarilo sa – nič sa nezmenilo.\n" + string.Join("\n", result.Messages),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MkvToolNixException or ProcessStartException or TimeoutException)
        {
            Result = "Nepodarilo sa: " + ex.Message;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a temporary folder left behind is harmless
            }

            _busy = false;
            RefreshCommands();
        }
    }

    // subtitle files alone: each one as a new SRT file next to it (never over an existing file)
    private async Task SaveFilesAsync()
    {
        _busy = true;
        RefreshCommands();
        var saved = new List<string>();
        try
        {
            foreach (var row in Rows.Where(r => r is { Include: true, Document: not null }))
            {
                var document = row.Timing.Apply(row.Repaired(row.Document)!);
                var folder = Path.GetDirectoryName(row.ExternalPath)!;
                var name = Path.GetFileNameWithoutExtension(row.ExternalPath);
                var extension = OutputFormat == "WebVTT" ? ".vtt" : ".srt";
                var path = Path.Combine(folder, name + extension);
                for (var copy = 1; File.Exists(path); copy++)
                {
                    path = Path.Combine(folder, $"{name} (upravené{(copy > 1 ? " " + copy.ToString(CultureInfo.InvariantCulture) : "")}){extension}");
                }

                if (OutputFormat == "WebVTT")
                {
                    await VttWriter.SaveAsync(document, path);
                }
                else
                {
                    await SrtWriter.SaveAsync(document, path);
                }

                saved.Add(Path.GetFileName(path));
            }

            Result = saved.Count == 0 ? "Nebolo čo uložiť." : $"Uložené ako {OutputFormat} (UTF-8), pôvodné súbory ostali: {string.Join(", ", saved)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Result = "Nepodarilo sa uložiť: " + ex.Message + (saved.Count > 0 ? $" (uložené: {string.Join(", ", saved)})" : "");
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        OpenVideoCommand?.NotifyCanExecuteChanged();
        OpenSubtitlesCommand?.NotifyCanExecuteChanged();
        AddExternalCommand?.NotifyCanExecuteChanged();
        RemoveCommand?.NotifyCanExecuteChanged();
        SaveCommand?.NotifyCanExecuteChanged();
        ClearCommand?.NotifyCanExecuteChanged();
        SearchOnlineCommand?.NotifyCanExecuteChanged();
        DownloadOnlineCommand?.NotifyCanExecuteChanged();
        SearchTitulkyCommand?.NotifyCanExecuteChanged();
        TranslateCommand?.NotifyCanExecuteChanged();
        SyncCommand?.NotifyCanExecuteChanged();
        CheckTracksCommand?.NotifyCanExecuteChanged();
        StartsNowCommand?.NotifyCanExecuteChanged();
        MarkFirstCommand?.NotifyCanExecuteChanged();
        MarkSecondCommand?.NotifyCanExecuteChanged();
        StopSyncCommand?.NotifyCanExecuteChanged();
        SaveToTaskCommand?.NotifyCanExecuteChanged();
        LeaveTaskCommand?.NotifyCanExecuteChanged();
        SaveWorkCommand?.NotifyCanExecuteChanged();
        UndoCueEditsCommand?.NotifyCanExecuteChanged();
        DiscardWorkCommand?.NotifyCanExecuteChanged();
        ApplyProposalCommand?.NotifyCanExecuteChanged();
        RefreshOcrCommands();
    }

    // the times of a row's subtitles: the external file's, the index of the video's own track, or the whole track read
    private async Task<IReadOnlyList<(TimeSpan Start, TimeSpan End)>?> CuesOfAsync(SubtitleRow row, CancellationToken cancellationToken)
    {
        if (row.Document is { } document)
        {
            return document.Cues.Select(c => (c.Start, c.End)).ToList();
        }

        if (_file is null || row.Track.Number is not { } number || _services.CurrentMkvToolNix is not { } installation)
        {
            return null;
        }

        var index = await MatroskaCues.ReadPointsAsync(_file.Path, cancellationToken);
        if (index.TryGetValue(number, out var points) && Core.Subtitles.Sync.SubtitleSync.Intervals(points, picture: !row.IsText) is { Count: > 0 } cues)
        {
            return cues;
        }

        SyncStatus = $"Čítam celú stopu {row.Track.Id} (súbor nemá index titulkov)…";
        row.Document = await SubtitleText.ExtractDocumentAsync(installation, _services.Runner, _file, row.Track);
        return row.Document?.Cues.Select(c => (c.Start, c.End)).ToList();
    }

    // where people speak in the film: parts of the default (or first) audio track, read once for the video as it is now
    private async Task<(IReadOnlyList<Core.Subtitles.Sync.ActivityWindow>? Windows, string? Problem)> SpeechAsync(CancellationToken cancellationToken)
    {
        if (_file is not { Duration: { } duration } file || !file.TracksOf(TrackType.Audio).Any())
        {
            return (null, "video nemá zvuk");
        }

        var audio = file.TracksOf(TrackType.Audio).ToList();
        var listed = Math.Max(0, audio.FindIndex(t => t.Flags.Default));
        var ordinal = await _services.AudioOrdinalAsync(file.Path, audio[listed], listed, cancellationToken);
        var info = new FileInfo(file.Path);
        var key = $"{file.Path}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}|{ordinal}";
        if (_speech is { } known && known.Key == key)
        {
            return (known.Windows, null);
        }

        string ffmpeg;
        try
        {
            ffmpeg = (await _services.SpeechAsync(cancellationToken)).Ffmpeg;
        }
        catch (Exception ex) when (ex is Core.Speech.SpeechException or IOException or UnauthorizedAccessException)
        {
            return (null, "FFmpeg nie je k dispozícii: " + ex.Message);
        }

        var parts = Core.Subtitles.Sync.SpeechActivity.Parts(duration);
        var windows = await Core.Subtitles.Sync.SpeechActivity.ReadAsync(_services.Runner, ffmpeg, file.Path, ordinal, parts, TimeSpan.FromMinutes(2.5),
            new Progress<int>(i => SyncStatus = $"Počúvam zvuk filmu – časť {i + 1} z {parts.Count}…"), cancellationToken);
        if (windows.Count == 0)
        {
            return (null, $"FFmpeg neprečítal zvukovú stopu {audio[ordinal].Id} (súbor je nedostupný alebo poškodený)");
        }

        // a read that failed in parts is not kept: the next try reads the film again
        if (windows.Count * 2 >= parts.Count)
        {
            _speech = (key, windows);
        }

        return (windows, null);
    }

    // the frame rate of the video (which change of frame rate sync names)
    private double? FilmFps => _file?.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond;

    private CancellationToken StartSync()
    {
        _busy = true;
        Proposal = null;
        _syncCancellation = new CancellationTokenSource();
        RefreshCommands();
        return _syncCancellation.Token;
    }

    private void EndSync()
    {
        _syncCancellation?.Dispose();
        _syncCancellation = null;
        _busy = false;
        RefreshCommands();
    }

    private (SubtitleRow Row, Core.Subtitles.Sync.SyncResult Result)? Proposal
    {
        get => _proposal;
        set
        {
            _proposal = value;
            OnPropertyChanged(nameof(HasProposal));
            ApplyProposalCommand?.NotifyCanExecuteChanged();
        }
    }

    private async Task SyncAsync()
    {
        if (_selected is not { } row)
        {
            return;
        }

        var cancellationToken = StartSync();
        try
        {
            if (await CuesOfAsync(row, cancellationToken) is not { Count: > 0 } cues)
            {
                SyncStatus = "Časy týchto titulkov sa nedajú zistiť.";
                return;
            }

            var duration = _file?.Duration ?? (cues[^1].End + TimeSpan.FromMinutes(1));
            var fps = FilmFps;
            Core.Subtitles.Sync.SyncResult? byAudio = null, bySubtitles = null;
            string? problem = null;
            if (SyncByAudio && _file is not null)
            {
                var (speech, why) = await SpeechAsync(cancellationToken);
                problem = why;
                if (speech is { Count: > 0 })
                {
                    SyncStatus = "Porovnávam titulky s rečou…";
                    byAudio = await Task.Run(() => Core.Subtitles.Sync.SubtitleSync.Align(cues, speech, "podľa zvuku", filmFps: fps), cancellationToken);
                }
            }

            if (SyncBySubtitles && SyncReference is { } reference && reference != row && Rows.Contains(reference) &&
                await CuesOfAsync(reference, cancellationToken) is { Count: > 0 } referenceCues)
            {
                // the reference as it will be after its own changes
                var fitted = referenceCues.Select(c => (reference.Timing.Apply(c.Start), reference.Timing.Apply(c.End))).ToList();
                var windows = Core.Subtitles.Sync.SpeechActivity.Parts(duration).Select(s => Core.Subtitles.Sync.ActivityWindow.FromCues(fitted, s, TimeSpan.FromMinutes(2.5))).ToList();
                bySubtitles = await Task.Run(() => Core.Subtitles.Sync.SubtitleSync.Align(cues, windows, "podľa titulkov " + reference.Language.Tag.Primary, filmFps: fps), cancellationToken);
            }

            var result = Core.Subtitles.Sync.SubtitleSync.Combine(byAudio, bySubtitles, duration);
            if (result is null)
            {
                SyncStatus = "Nedalo sa nič zistiť" + (problem is null ? " (bez zvuku aj bez iných titulkov)." : $": {problem}.");
                return;
            }

            if (result.Reliable)
            {
                row.ApplySync(result);
                SyncStatus = result.Detail + " Hodnoty sú nastavené pri titulkoch – skontrolujte náhľad a uložte.";
            }
            else if (result.IsProposal)
            {
                // never applied by itself: the user's own values stay until the proposal is taken
                Proposal = (row, result);
                SyncStatus = result.Detail + " Hodnoty titulkov sa nezmenili – návrh použijete tlačidlom „Použiť návrh“.";
            }
            else
            {
                SyncStatus = result.Detail + " Hodnoty titulkov sa nezmenili." + (problem is null ? "" : $" ({problem})");
            }
        }
        catch (OperationCanceledException)
        {
            SyncStatus = "Napasovanie je zastavené – hodnoty titulkov sa nezmenili.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MkvToolNixException or ProcessStartException or TimeoutException)
        {
            SyncStatus = "Napasovanie sa nepodarilo: " + ex.Message;
        }
        finally
        {
            EndSync();
        }
    }

    private void ApplyProposal()
    {
        if (Proposal is { } proposal && Rows.Contains(proposal.Row))
        {
            proposal.Row.ApplySync(proposal.Result);
            SyncStatus = proposal.Result.Detail + " Návrh je nastavený – overte ho prehraním.";
        }

        Proposal = null;
    }

    // owner: the other subtitle tracks too – are they late or early against the audio?
    private async Task CheckTracksAsync()
    {
        if (_file is null)
        {
            return;
        }

        var cancellationToken = StartSync();
        try
        {
            var (speech, problem) = await SpeechAsync(cancellationToken);
            if (speech is not { Count: > 0 })
            {
                SyncStatus = "Zvuk filmu sa nepodarilo prečítať" + (problem is null ? "." : $": {problem}.");
                return;
            }

            var index = await MatroskaCues.ReadPointsAsync(_file.Path, cancellationToken);
            var fps = FilmFps;
            var lines = new List<string>();
            foreach (var row in Rows.Where(r => !r.IsExternal).ToList())
            {
                var name = $"stopa {row.Track.Id} ({row.Language.Tag.Primary}{(row.Forced ? ", forced" : "")})";
                var cues = row.Track.Number is { } number && index.TryGetValue(number, out var points)
                    ? Core.Subtitles.Sync.SubtitleSync.Intervals(points, picture: !row.IsText)
                    : [];
                if (cues.Count < 10)
                {
                    lines.Add($"{name}: málo titulkov alebo bez indexu – nedá sa posúdiť");
                    continue;
                }

                var result = await Task.Run(() => Core.Subtitles.Sync.SubtitleSync.Align(cues, speech, "podľa zvuku", filmFps: fps), cancellationToken);
                lines.Add(result is null ? $"{name}: nedá sa posúdiť"
                    : !result.Reliable ? $"{name}: neisté – {result.Detail}"
                    : result.Timing.IsNone ? $"{name}: sedia"
                    : $"{name}: {Lag(result)}{(result.Rates is { } rates ? $", iné FPS ({rates.From.Label} → {rates.To.Label})" : "")} – {result.Timing.Describe()} (Napasovať ich nastaví)");
            }

            SyncStatus = "Kontrola titulkov podľa zvuku: " + string.Join("; ", lines) + ".";
        }
        catch (OperationCanceledException)
        {
            SyncStatus = "Kontrola je zastavená.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProcessStartException or TimeoutException)
        {
            SyncStatus = "Kontrola sa nepodarila: " + ex.Message;
        }
        finally
        {
            EndSync();
        }

        string Lag(Core.Subtitles.Sync.SyncResult result)
        {
            var at = (_file?.Duration ?? TimeSpan.FromHours(1)) / 2;
            var lag = result.LagAt(at).TotalSeconds;
            return lag > 0 ? $"meškajú o {lag:0.0} s" : $"idú skôr o {-lag:0.0} s";
        }
    }

    // the title for the services: the library picker's (with the TMDB ID), else the name of the file
    private (string Name, int? Year, int? TmdbId, EpisodeNumber? Episode) Title()
    {
        if (Library.Title is { } title)
        {
            return (title.Name, title.Year, title.TmdbId, Library.Episode);
        }

        var path = _file?.Path ?? Rows.FirstOrDefault()?.ExternalPath ?? "";
        var guess = TitleGuess.ForFile(path);
        return (guess.Title?.Name ?? Path.GetFileNameWithoutExtension(path), guess.Title?.Year, guess.Title?.TmdbId, guess.Episode?.Number);
    }

    // where downloaded and translated subtitles are saved: next to the video, or next to the opened subtitle file
    private string? Anchor => _file?.Path ?? Rows.FirstOrDefault(r => r.ExternalPath is not null)?.ExternalPath;

    private async Task SearchOnlineAsync()
    {
        if (_services.OpenSubtitles() is not { } client)
        {
            OnlineStatus = "OpenSubtitles: zadajte API kľúč v Nastaveniach (Titulky – sťahovanie a preklad).";
            return;
        }

        _busy = true;
        RefreshCommands();
        OnlineResults.Clear();
        try
        {
            var (name, year, tmdb, episode) = Title();
            var filter = HearingImpairedFilter switch { "len SDH" => "only", "bez SDH" => "exclude", _ => "include" };
            var results = await client.SearchAsync(new OpenSubtitlesQuery(OnlineLanguage, tmdb, name, year, episode?.Season, episode?.Episode, filter));
            // owner, 30. 10. 2026: the release that fits the video and the frame rate of the film first
            var video = _file?.Path ?? Anchor ?? "";
            var fps = _file?.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond;
            var compared = results.Select(r =>
            {
                var match = ReleaseMatch.Score(r.Release, video);
                return r with { Match = match, MatchNote = ReleaseMatch.FpsNote(r.Fps, fps) + (match > 0 ? $" · release {match * 100:0} %" : "") };
            });
            foreach (var result in compared.OrderByDescending(r => ReleaseMatch.FpsFits(r.Fps, fps)).ThenByDescending(r => Math.Round(r.Match, 1)).ThenByDescending(r => r.Downloads).Take(40))
            {
                OnlineResults.Add(result);
            }

            OnlineStatus = results.Count == 0
                ? $"OpenSubtitles: pre „{name}“{(episode is null ? "" : " " + episode.Tag)} nič nenašiel" + (tmdb is null ? " – skúste titul s TMDB ID." : ".")
                : $"OpenSubtitles: {results.Count} výsledkov" + (tmdb is null ? " (podľa názvu – presnejšie s TMDB ID)" : " (podľa TMDB ID)") + ".";
        }
        catch (SubtitleServiceException ex)
        {
            OnlineStatus = ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    private async Task DownloadOnlineAsync(OpenSubtitlesResult? result)
    {
        if (result is null || _services.OpenSubtitles() is not { } client || Anchor is not { } anchor)
        {
            return;
        }

        var (_, user, password) = _services.OpenSubtitlesAccount;
        if (user is null || password is null)
        {
            OnlineStatus = "OpenSubtitles: na stiahnutie treba meno a heslo účtu v Nastaveniach.";
            return;
        }

        _busy = true;
        RefreshCommands();
        try
        {
            await client.LoginAsync(user, password);
            var (name, content) = await client.DownloadAsync(result.FileId);
            var document = SubtitleReader.Read(SubtitleDownloads.Text(content), _file?.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond);
            var language = result.Language.Length > 0 ? result.Language : OnlineLanguage;
            var saved = await SubtitleDownloads.SaveBesideAsync(anchor, document, language, result.HearingImpaired, result.ForeignPartsOnly);
            _taken.Add(saved);
            OnlineStatus = $"Stiahnuté ({name}) a uložené vedľa videa ako {Path.GetFileName(saved)}" + (client.Remaining is { } left ? $"; dnes ešte {left} stiahnutí." : ".");
            _busy = false;
            await AddFilesAsync([saved]);
        }
        catch (Exception ex) when (ex is SubtitleServiceException or FormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            OnlineStatus = ex is SubtitleServiceException ? ex.Message : "Stiahnuté titulky sa nepodarilo uložiť: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    // owner: titulky.com allows only a browser – UNIVERSUM opens the search and takes the downloaded file from Downloads
    private void SearchTitulky()
    {
        var (name, _, _, episode) = Title();
        var query = name + (episode is null ? "" : " " + episode.Tag);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SubtitleDownloads.TitulkyCom(query).AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            OnlineStatus = "Prehliadač sa nepodarilo otvoriť: " + ex.Message;
            return;
        }

        var since = DateTime.UtcNow.AddSeconds(-2);
        var until = DateTime.UtcNow.AddMinutes(30);
        _downloadsWatch?.Stop();
        _downloadsWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _downloadsWatch.Tick += async (_, _) =>
        {
            if (DateTime.UtcNow > until || Anchor is null)
            {
                _downloadsWatch?.Stop();
                return;
            }

            foreach (var download in SubtitleDownloads.NewFiles(AppServices.DownloadsFolder, since).Where(_taken.Add))
            {
                await TakeDownloadAsync(download);
            }
        };
        _downloadsWatch.Start();
        OnlineStatus = $"titulky.com je otvorený v prehliadači („{query}“). Stiahnuté titulky (aj ZIP) UNIVERSUM do 30 minút sám vezme zo Stiahnutých súborov, uloží ich vedľa videa a pridá.";
    }

    private async Task TakeDownloadAsync(string download)
    {
        if (Anchor is not { } anchor || _busy)
        {
            _taken.Remove(download);
            return;
        }

        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "UNIVERSUM", "downloads-" + Guid.NewGuid().ToString("N"));
            var saved = new List<string>();
            foreach (var file in SubtitleDownloads.Unpack(download, folder))
            {
                if (ExternalSubtitles.PictureExtensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }

                var document = await SubtitleReader.ReadFileAsync(file, _file?.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond);
                var language = SubtitleLanguage.Detect(document.PlainText)?.Language ?? "und";
                var name = Words.Normalize(Path.GetFileNameWithoutExtension(file));
                var path = await SubtitleDownloads.SaveBesideAsync(anchor, document, language, forced: Core.Text.Words.ContainsAny(name, _services.Profile.ForcedKeywords));
                _taken.Add(path);
                saved.Add(path);
            }

            if (saved.Count > 0)
            {
                OnlineStatus = $"Prevzaté zo Stiahnutých súborov: {Path.GetFileName(download)} → {string.Join(", ", saved.Select(Path.GetFileName))} (vedľa videa; pôvodný súbor ostal).";
                await AddFilesAsync(saved);
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            OnlineStatus = $"{Path.GetFileName(download)} sa nedá prevziať: {ex.Message}";
        }
    }

    // svsub.net (owner: when no subtitles exist): the selected subtitles translated with the user's token
    private async Task TranslateAsync()
    {
        if (_selected is not { IsText: true } row || _services.SvSub() is not { } client || Anchor is not { } anchor || _services.CurrentMkvToolNix is not { } installation)
        {
            return;
        }

        if (string.Equals(row.Language.Tag.Primary, TranslateTo, StringComparison.OrdinalIgnoreCase))
        {
            OnlineStatus = "Zdrojový a cieľový jazyk nemôžu byť rovnaké – vyberte iný cieľový jazyk.";
            return;
        }

        _busy = true;
        RefreshCommands();
        try
        {
            var document = row.Document;
            if (document is null && _file is not null)
            {
                OnlineStatus = "Čítam celú titulkovú stopu (číta sa celý súbor)…";
                document = row.Document = await SubtitleText.ExtractDocumentAsync(installation, _services.Runner, _file, row.Track);
            }

            if (document is null)
            {
                OnlineStatus = "Titulky sa nepodarilo prečítať.";
                return;
            }

            var source = row.Language.Tag.IsUndetermined ? null : row.Language.Tag.Primary;
            var translated = await client.TranslateAsync(SrtWriter.Write(document), TranslateTo, _services.SvSubProvider, new Progress<string>(s => OnlineStatus = "svsub.net: " + s),
                model: _services.SvSubModel, sourceLanguage: source);
            var saved = await SubtitleDownloads.SaveBesideAsync(anchor, SubtitleReader.Read(translated), TranslateTo, row.HearingImpaired, row.Forced);
            _taken.Add(saved);
            OnlineStatus = $"Preložené ({row.Language.Tag.Primary} → {TranslateTo}) a uložené vedľa videa ako {Path.GetFileName(saved)}; " +
                           (client.JobDeleted ? "úloha na svsub.net je zmazaná." : "úlohu na svsub.net sa nepodarilo zmazať – zmažte ju na stránke služby.");
            _busy = false;
            await AddFilesAsync([saved]);
        }
        catch (Exception ex) when (ex is SubtitleServiceException or FormatException or IOException or UnauthorizedAccessException)
        {
            OnlineStatus = ex is SubtitleServiceException ? ex.Message : "Preklad sa nepodarilo uložiť: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }
}
