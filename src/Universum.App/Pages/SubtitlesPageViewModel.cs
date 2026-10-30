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

/// <summary>A subtitle of the preview: its time now, the time it gets, its text.</summary>
public sealed record SubtitlePreviewRow(string Number, string Before, string After, string Text);

/// <summary>
/// Titulky (owner, 30. 10. 2026): the subtitles of a video or of subtitle files – Zmena FPS titulkov, Zmena rýchlosti
/// titulkov (percentá), Posun (Delay in ms), Konverzia do SRT from every text format, external subtitles added to the
/// video, and repairs (synchronizácia podľa dvoch titulkov, prvky pre nepočujúcich, formátovanie, prekrývanie časov).
/// A video is saved as a new file in the library (mkvmerge changes the times: --sync); subtitle files as new SRT files
/// next to them. Originals are never changed.
/// </summary>
public sealed class SubtitlesPageViewModel : ObservableObject
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
        Library = new LibraryTargetPicker(services, Target);
        Library.Changed += RefreshCommands;
        OpenVideoCommand = new AsyncRelayCommand(OpenVideoAsync, () => !_busy);
        OpenSubtitlesCommand = new AsyncRelayCommand(OpenSubtitlesAsync, () => !_busy);
        AddExternalCommand = new AsyncRelayCommand(AddExternalAsync, () => !_busy && _file is not null);
        RemoveCommand = new RelayCommand(Remove, () => !_busy && _selected is { IsExternal: true });
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
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
    private (string Path, IReadOnlyList<Core.Subtitles.Sync.ActivityWindow> Windows)? _speech;

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

    /// <summary>Times before and after of the selected subtitles (an external file; the video's track shows examples).</summary>
    public ObservableCollection<SubtitlePreviewRow> Preview { get; } = [];

    public SubtitleRow? SelectedRow
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                UpdatePreview();
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
            .Select(w => w.Length is 2 or 3 ? Languages.Find(w) : null).FirstOrDefault(l => l is not null);
        return new SubtitleFileMatch(path, 0, "pridané ručne", language is null ? null : LanguageTag.FromCode(language.Ietf),
            Core.Text.Words.ContainsAny(name, _services.Profile.ForcedKeywords), Core.Text.Words.ContainsAny(name, _services.Profile.HearingImpairedKeywords));
    }

    private void Remove()
    {
        if (_selected is { IsExternal: true } row)
        {
            Rows.Remove(row);
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
        Rows.Clear();
        Found.Clear();
        Preview.Clear();
        SelectedRow = null;
        Result = "";
        _nextId = FirstExternalId;
        OnPropertyChanged(nameof(HasFound));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsFiles));
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Blocked));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsFiles));
        UpdatePreview();
        Library?.Update();
        RefreshCommands();
    }

    private void UpdatePreview()
    {
        Preview.Clear();
        if (_selected is not { } row)
        {
            return;
        }

        var timing = row.Problem is null ? row.Timing : SubtitleTiming.None;
        if (row.Repaired(row.Document) is { } document)
        {
            var count = document.Cues.Count;
            foreach (var at in Enumerable.Range(0, count).Where(i => count <= 10 || i < 7 || i >= count - 2))
            {
                var cue = document.Cues[at];
                var moved = timing.Apply(cue.Start);
                Preview.Add(new SubtitlePreviewRow((at + 1).ToString(CultureInfo.InvariantCulture), SubtitleRow.Clock(cue.Start),
                    moved < TimeSpan.Zero ? "vypadne (pred začiatkom)" : SubtitleRow.Clock(moved), cue.Text.Replace('\n', ' ')));
            }

            return;
        }

        // a track of the video: where subtitles at a few times end up (its text is read only when saving)
        foreach (var at in new[] { 1, 10, 30, 60, 90 }.Select(m => TimeSpan.FromMinutes(m)).Where(t => _file?.Duration is not { } d || t <= d))
        {
            Preview.Add(new SubtitlePreviewRow("", SubtitleRow.Clock(at), SubtitleRow.Clock(timing.Apply(at)), "(príklad času)"));
        }
    }

    /// <summary>Why the video cannot be written by the rules (a track of an undetermined language …); empty when it can.</summary>
    public string Blocked => _file is null || !UseRules || Target() is not { IsBlocked: true } target ? ""
        : "Pravidlá UNIVERSUM: " + string.Join(" ", target.Issues.Where(i => i.IsBlocking).Select(i => i.Kind switch
        {
            RuleIssueKind.UndeterminedLanguage => $"stopa {(_file.Tracks.FirstOrDefault(t => t.Id == i.TrackId) is { } track ? $"{track.Id} ({Texts.Type(track.Type)})" : "")} má neurčený jazyk.",
            RuleIssueKind.MultipleVideoTracks => "súbor má viac video stôp.",
            RuleIssueKind.NoAudio => "súbor nemá zvukovú stopu.",
            _ => i.Kind.ToString(),
        }).Distinct()) + " Určte ho v Úprave hlavičky alebo v Single spracovaní – alebo vypnite pravidlá (stopy ostanú, ako sú).";

    private bool CanSave() =>
        !_busy && Rows.Count > 0 && Rows.All(r => r.Problem is null) && Blocked.Length == 0 &&
        (_file is null
            ? Rows.Any(r => r.IsText)
            : Library.Placement is not null && (Target() is { HasChanges: true } || Rows.Any(r => r is { Include: true, NeedsText: true, IsExternal: false })));

    /// <summary>The video with the subtitles as chosen – without the text changes (they are written when saving); null without a video.</summary>
    public TargetState? Target() => _file is null ? null : Build(_file, Rows.ToDictionary(r => r, r => r.Track)).Target;

    // the target state of the video: the rows' tracks (an external file or a rewritten track of the video by its track)
    private (TargetState Target, HashSet<int> Replaced) Build(MediaFile file, IReadOnlyDictionary<SubtitleRow, MediaTrack> written)
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
                ? new TrackEdit(t.Id, row.Name, row.Language.Tag, t.Flags with { Forced = row.Forced, HearingImpaired = row.HearingImpaired })
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
            var result = await _services.Processor(installation).ProcessAsync(EditPlan.For(target), placement, new OutputOptions(OriginalHandling.Keep));
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
    }

    // the times of a row's subtitles: the external file's, the index of the video's own track, or the whole track read
    private async Task<IReadOnlyList<(TimeSpan Start, TimeSpan End)>?> CuesOfAsync(SubtitleRow row)
    {
        if (row.Document is { } document)
        {
            return document.Cues.Select(c => (c.Start, c.End)).ToList();
        }

        if (_file is null || row.Track.Number is not { } number || _services.CurrentMkvToolNix is not { } installation)
        {
            return null;
        }

        var intervals = await MatroskaCues.ReadIntervalsAsync(_file.Path);
        if (intervals.TryGetValue(number, out var cues) && cues.Count > 0)
        {
            return cues;
        }

        SyncStatus = $"Čítam celú stopu {row.Track.Id} (súbor nemá index titulkov)…";
        row.Document = await SubtitleText.ExtractDocumentAsync(installation, _services.Runner, _file, row.Track);
        return row.Document?.Cues.Select(c => (c.Start, c.End)).ToList();
    }

    // where people speak in the film: parts of the default (or first) audio track, read once for the video
    private async Task<IReadOnlyList<Core.Subtitles.Sync.ActivityWindow>?> SpeechAsync()
    {
        if (_file is not { Duration: { } duration } file || !file.TracksOf(TrackType.Audio).Any())
        {
            return null;
        }

        if (_speech is { } known && known.Path == file.Path)
        {
            return known.Windows;
        }

        string ffmpeg;
        try
        {
            ffmpeg = (await _services.SpeechAsync()).Ffmpeg;
        }
        catch (Exception ex) when (ex is Core.Speech.SpeechException or IOException or UnauthorizedAccessException)
        {
            SyncStatus = "FFmpeg nie je k dispozícii: " + ex.Message;
            return null;
        }

        var audio = file.TracksOf(TrackType.Audio).ToList();
        var ordinal = Math.Max(0, audio.FindIndex(t => t.Flags.Default));
        var parts = Core.Subtitles.Sync.SpeechActivity.Parts(duration);
        var windows = await Core.Subtitles.Sync.SpeechActivity.ReadAsync(_services.Runner, ffmpeg, file.Path, ordinal, parts, TimeSpan.FromMinutes(2.5),
            new Progress<int>(i => SyncStatus = $"Počúvam zvuk filmu – časť {i + 1} z {parts.Count}…"));
        _speech = (file.Path, windows);
        return windows;
    }

    private async Task SyncAsync()
    {
        if (_selected is not { } row)
        {
            return;
        }

        _busy = true;
        RefreshCommands();
        try
        {
            if (await CuesOfAsync(row) is not { Count: > 0 } cues)
            {
                SyncStatus = "Časy týchto titulkov sa nedajú zistiť.";
                return;
            }

            var duration = _file?.Duration ?? (cues[^1].End + TimeSpan.FromMinutes(1));
            Core.Subtitles.Sync.SyncResult? byAudio = null, bySubtitles = null;
            if (SyncByAudio && await SpeechAsync() is { Count: > 0 } speech)
            {
                SyncStatus = "Porovnávam titulky s rečou…";
                byAudio = await Task.Run(() => Core.Subtitles.Sync.SubtitleSync.Align(cues, speech, "podľa zvuku"));
            }

            if (SyncBySubtitles && SyncReference is { } reference && reference != row && await CuesOfAsync(reference) is { Count: > 0 } referenceCues)
            {
                // the reference as it will be after its own changes
                var fitted = referenceCues.Select(c => (reference.Timing.Apply(c.Start), reference.Timing.Apply(c.End))).ToList();
                var windows = Core.Subtitles.Sync.SpeechActivity.Parts(duration).Select(s => Core.Subtitles.Sync.ActivityWindow.FromCues(fitted, s, TimeSpan.FromMinutes(2.5))).ToList();
                bySubtitles = await Task.Run(() => Core.Subtitles.Sync.SubtitleSync.Align(cues, windows, "podľa titulkov " + reference.Language.Tag.Primary));
            }

            var result = Core.Subtitles.Sync.SubtitleSync.Combine(byAudio, bySubtitles, duration);
            if (result is null)
            {
                SyncStatus = "Nedalo sa nič zistiť (bez zvuku aj bez iných titulkov).";
                return;
            }

            row.ApplySync(result.Timing, result.Detail);
            SyncStatus = result.Detail + (result.Reliable ? " Hodnoty sú nastavené pri titulkoch – skontrolujte náhľad a uložte." : " Hodnoty sú nastavené, ale overte ich.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MkvToolNixException or ProcessStartException or TimeoutException)
        {
            SyncStatus = "Napasovanie sa nepodarilo: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    // owner: the other subtitle tracks too – are they late or early against the audio?
    private async Task CheckTracksAsync()
    {
        if (_file is null)
        {
            return;
        }

        _busy = true;
        RefreshCommands();
        try
        {
            if (await SpeechAsync() is not { Count: > 0 } speech)
            {
                SyncStatus = "Zvuk filmu sa nepodarilo prečítať.";
                return;
            }

            var intervals = await MatroskaCues.ReadIntervalsAsync(_file.Path);
            var lines = new List<string>();
            foreach (var row in Rows.Where(r => !r.IsExternal))
            {
                var name = $"stopa {row.Track.Id} ({row.Language.Tag.Primary}{(row.Forced ? ", forced" : "")})";
                if (row.Track.Number is not { } number || !intervals.TryGetValue(number, out var cues) || cues.Count < 10)
                {
                    lines.Add($"{name}: málo titulkov alebo bez indexu – nedá sa posúdiť");
                    continue;
                }

                var result = await Task.Run(() => Core.Subtitles.Sync.SubtitleSync.Align(cues, speech, "podľa zvuku"));
                lines.Add(result is null ? $"{name}: nedá sa posúdiť"
                    : !result.Reliable ? $"{name}: neisté – {result.Detail}"
                    : result.Timing.IsNone ? $"{name}: sedia"
                    : $"{name}: {Lag(result)} – {result.Timing.Describe()} (Napasovať ich nastaví)");
            }

            SyncStatus = "Kontrola titulkov podľa zvuku: " + string.Join("; ", lines) + ".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProcessStartException or TimeoutException)
        {
            SyncStatus = "Kontrola sa nepodarila: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
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
