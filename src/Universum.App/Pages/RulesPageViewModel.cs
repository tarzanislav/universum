using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Jobs;
using Universum.App.Localization;
using Universum.Core.Execution;
using Universum.Core.Jobs;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;
using Universum.Platform.Windows;

namespace Universum.App.Pages;

/// <summary>Applying the UNIVERSUM rules to one file: preview, languages for undetermined tracks, title and place in the library, writing, undo.</summary>
public sealed class RulesPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<int, LanguageTag> _languages = [];
    private readonly Dictionary<int, string> _videoNames = [];
    private readonly HashSet<int> _kept = [];
    private readonly Dictionary<int, bool> _forced = [];

    // owner, 30. 10. 2026: track names written by the user (they win over the rules'); empty = no name
    private readonly Dictionary<int, string> _names = [];

    // the languages UNIVERSUM read from the subtitles' text (owner, 30. 10. 2026), applied unless the user chose another
    private IReadOnlyDictionary<int, Core.Text.SubtitleLanguageResult> _detected = new Dictionary<int, Core.Text.SubtitleLanguageResult>();
    private CancellationTokenSource? _subtitleReading;
    private readonly List<int> _order = [];
    private MediaFile? _file;
    private EditPlan? _plan;
    private string _fileName = "";
    private string _summary = Loc.T("Otvorte súbor MKV alebo ho sem pretiahnite myšou.");
    private string _issues = "";
    private string _result = "";
    private double _progress;
    private bool _busy;
    private string? _undoRecord;
    private MediaKind _kind = MediaKind.Movie;
    private string _titleText = "";
    private string _addition = "";
    private string _episodeText = "";
    private string _episodeNote = "";
    private Placement? _placement;
    private TitleInfo? _rememberedSeries;
    private string _target = "";
    private TmdbTitle? _tmdbTitle;
    private string _titleNote = "";

    public RulesPageViewModel(AppServices services)
    {
        _services = services;
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !Busy);
        ClearCommand = new RelayCommand(Clear, () => !Busy);
        ApplyCommand = new AsyncRelayCommand(() => ApplyAsync(), CanApply);
        ReviseCommand = new AsyncRelayCommand(() => ApplyAsync(revision: true), () => CanRevise && CanApply());
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => !Busy && _undoRecord is not null);
        QueueCommand = new RelayCommand(Enqueue, CanApply);
        Mega = new MegaSender(services);
        SendToMegaCommand = new RelayCommand(SendToMega, CanSendToMega);
        ChooseOutputDirectoryCommand = new RelayCommand(ChooseOutputDirectory, () => !Busy);
        Tmdb = new TmdbPicker(services, () => _kind, () => TitleText, ChooseTmdbTitle);
        Tmdb.ConfirmationChanged += UpdateTarget;
        services.TitlesChanged += Tmdb.Refresh;
        services.LibraryChanged += () =>
        {
            OnPropertyChanged(nameof(OutputDescription));
            UpdateTarget();
        };
    }

    public AsyncRelayCommand OpenCommand { get; }

    /// <summary>Vymazať frontu (owner, 29. 10. 2026): forgets the opened file not processed yet – nothing on the disk changes.</summary>
    public RelayCommand ClearCommand { get; }

    /// <summary>Asks before the opened file is forgotten (the self-test answers itself).</summary>
    public Func<string, bool> Confirm { get; set; } = question =>
        System.Windows.MessageBox.Show(question, "UNIVERSUM", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    private void Clear()
    {
        if (Busy || !Confirm(Loc.T("Vymazať frontu Single spracovania?\n\nZabudne sa otvorený súbor a jeho nastavenie. Na disku sa nič nezmení.")))
        {
            return;
        }

        CanRevise = false;
        Result = "";
        _undoRecord = null;
        _languages.Clear();
        _videoNames.Clear();
        _kept.Clear();
        _order.Clear();
        _forced.Clear();
        _names.Clear();
        _detected = new Dictionary<int, Core.Text.SubtitleLanguageResult>();
        _subtitleReading?.Cancel();
        _subtitleReading = null;
        _file = null;
        _plan = null;
        _placement = null;
        _tmdbTitle = null;
        _titleText = "";
        _episodeText = "";
        _episodeNote = "";
        _addition = "";
        OnPropertyChanged(nameof(Addition));
        Rows.Clear();
        FileName = "";
        Issues = "";
        Target = "";
        TitleNote = "";
        Summary = Loc.T("Otvorte súbor MKV alebo ho sem pretiahnite myšou.");
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(EpisodeText));
        OnPropertyChanged(nameof(Plan));
        RefreshCommands();
    }

    public AsyncRelayCommand ApplyCommand { get; }

    /// <summary>
    /// Revízia (owner, 31. 10. 2026): the same result is in the library – the file is written again as a new version; it is
    /// verified, then the earlier one goes to the Recycle Bin.
    /// </summary>
    public AsyncRelayCommand ReviseCommand { get; }

    /// <summary>The last processing found the same result in the library (Revízia is offered).</summary>
    public bool CanRevise
    {
        get => _canRevise;
        private set
        {
            if (SetProperty(ref _canRevise, value))
            {
                ReviseCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool _canRevise;

    public AsyncRelayCommand UndoCommand { get; }

    /// <summary>Adds the file to the job queue instead of processing it now.</summary>
    public RelayCommand QueueCommand { get; }

    /// <summary>Where Odoslať do MEGA sends the file (owner, 1. 11. 2026): a new batch or a waiting one.</summary>
    public MegaSender Mega { get; }

    public RelayCommand SendToMegaCommand { get; }

    public RelayCommand ChooseOutputDirectoryCommand { get; }

    /// <summary>Looking up the title on TMDB.</summary>
    public TmdbPicker Tmdb { get; }

    /// <summary>A note to the title (e.g. TMDB has no English title).</summary>
    public string TitleNote { get => _titleNote; private set => SetProperty(ref _titleNote, value); }

    public ObservableCollection<RuleRow> Rows { get; } = [];

    public EditPlan? Plan => _plan;

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Issues { get => _issues; private set => SetProperty(ref _issues, value); }

    public string Result { get => _result; private set => SetProperty(ref _result, value); }

    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool KeepOriginal
    {
        get => _services.Settings.OriginalHandling == OriginalHandling.Keep;
        set => SetHandling(value ? OriginalHandling.Keep : null);
    }

    public bool DeleteOriginal
    {
        get => _services.Settings.OriginalHandling == OriginalHandling.Delete;
        set => SetHandling(value ? OriginalHandling.Delete : null);
    }

    public string OutputDescription => _services.Library?.Root ?? Loc.T("nie je nastavený – zvoľte ho tlačidlom Priečinok…");

    public bool IsMovie
    {
        get => _kind == MediaKind.Movie;
        set => SetKind(value, MediaKind.Movie);
    }

    public bool IsSeries
    {
        get => _kind == MediaKind.Series;
        set => SetKind(value, MediaKind.Series);
    }

    /// <summary>Title as in the library: "Creep (2004)" or "Nine Puzzles (2025) [tmdbid-227191]".</summary>
    public string TitleText
    {
        get => _titleText;
        set
        {
            if (SetProperty(ref _titleText, value))
            {
                // typed by the user: the title chosen on TMDB no longer applies; a title typed by the user is the user's
                // decision (the text that waited for confirmation waits again)
                _tmdbTitle = null;
                TitleNote = "";
                Tmdb.TextEdited(value);
                if (UseTmdbAddress(value))
                {
                    return;
                }

                UpdateTarget();
            }
        }
    }

    /// <summary>Episode of a series file: "S01E04".</summary>
    public string EpisodeText
    {
        get => _episodeText;
        set
        {
            if (SetProperty(ref _episodeText, value))
            {
                _episodeNote = "";
                UpdateTarget();
            }
        }
    }

    /// <summary>Doplnok k názvu (owner, 31. 10. 2026: as in Hromadné spracovanie): put into the name before the size class.</summary>
    public string Addition
    {
        get => _addition;
        set
        {
            var (addition, problem) = LibraryNames.Addition(value);
            if (problem is not null)
            {
                TitleNote = problem;
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _addition, addition ?? ""))
            {
                UpdateTarget();
            }
        }
    }

    /// <summary>The additions offered to choose.</summary>
    public IReadOnlyList<string> AdditionChoices => LibraryNames.AdditionChoices;

    /// <summary>Where the result goes (or why it cannot go anywhere yet).</summary>
    public string Target { get => _target; private set => SetProperty(ref _target, value); }

    public async Task LoadAsync(string path)
    {
        // a file being processed stays (its undo record belongs to it)
        if (Busy)
        {
            return;
        }

        Result = "";
        _undoRecord = null;
        CanRevise = false;
        _languages.Clear();
        _videoNames.Clear();
        _kept.Clear();
        _order.Clear();
        _forced.Clear();
        _names.Clear();
        _detected = new Dictionary<int, Core.Text.SubtitleLanguageResult>();
        _subtitleReading?.Cancel();
        _subtitleReading = null;
        _file = null;
        _plan = null;
        Rows.Clear();
        FileName = Path.GetFileName(path);
        GuessTitle(path);
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Summary = Loc.T("MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.");
            RefreshCommands();
            return;
        }

        try
        {
            _file = await WithExternalSubtitlesAsync(await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path));
            Recalculate();
            _ = ReadSubtitlesAsync(installation, _file);
        }
        catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException or TimeoutException)
        {
            Summary = ex.Message;
            RefreshCommands();
        }
    }

    /// <summary>
    /// Chooses the language of a track (also used by the self-test): an undetermined one, or a wrong one written in the
    /// file (owner, 29. 10. 2026: "EN" that is Czech); the file's own language again needs no choice.
    /// </summary>
    public void ChooseLanguage(int trackId, string code)
    {
        // the language that applies without a choice (read from the subtitles, else the file's) needs no choice
        var language = LanguageTag.FromCode(code);
        var source = _file?.Tracks.FirstOrDefault(t => t.Id == trackId);
        var automatic = _detected.TryGetValue(trackId, out var read) ? read.Language
            : source is { Language.IsUndetermined: false } ? source.Language.Primary
            : null;

        // while the subtitles are read, a choice is kept even when it is the file's language: what is read must not replace it
        var reading = _subtitleReading is not null && source is { Type: TrackType.Subtitles, IsTextSubtitles: true };
        if (!reading && automatic is not null && string.Equals(automatic, language.Primary, StringComparison.OrdinalIgnoreCase))
        {
            _languages.Remove(trackId);
        }
        else
        {
            _languages[trackId] = language;
        }

        Recalculate();
    }

    /// <summary>Moves a track one place up (-1) or down (+1) among the audio or subtitle tracks (owner, 29. 10. 2026).</summary>
    public void MoveTrack(int trackId, int by)
    {
        var row = Rows.FirstOrDefault(r => r.TrackId == trackId);
        var same = Rows.Where(r => r.CanMove && r.Target.Type == row?.Target.Type).ToList();
        var at = row is null ? -1 : same.IndexOf(row);
        if (at >= 0 && at + by >= 0 && at + by < same.Count)
        {
            MoveTrackTo(trackId, same[at + by].TrackId);
        }
    }

    /// <summary>Puts a dragged track at the place of another track of the same type.</summary>
    public void MoveTrackTo(int trackId, int targetId)
    {
        var order = Rows.Where(r => r.CanMove).ToList();
        if (order.FirstOrDefault(r => r.TrackId == trackId) is not { } row || order.FirstOrDefault(r => r.TrackId == targetId) is not { } target ||
            row == target || row.Target.Type != target.Target.Type)
        {
            return;
        }

        var down = order.IndexOf(row) < order.IndexOf(target);
        order.Remove(row);
        order.Insert(order.IndexOf(target) + (down ? 1 : 0), row);
        _order.Clear();
        _order.AddRange(order.Select(r => r.TrackId));
        Recalculate();
    }

    /// <summary>Keeps (or leaves out again) subtitles whose language is not among the subtitle languages.</summary>
    public void KeepTrack(int trackId, bool keep)
    {
        if (keep)
        {
            _kept.Add(trackId);
        }
        else
        {
            _kept.Remove(trackId);
        }

        Recalculate();
    }

    private void Recalculate()
    {
        if (_file is null)
        {
            return;
        }

        // the languages read from the subtitles, then the user's choices over them
        var languages = _detected.ToDictionary(d => d.Key, d => LanguageTag.FromCode(d.Value.Language));
        foreach (var (id, chosen) in _languages)
        {
            languages[id] = chosen;
        }

        var target = UniversumRules.Apply(_file, _services.Profile,
            new TrackDecisions { Languages = languages, VideoNames = _videoNames, Kept = _kept, Order = _order, Forced = _forced, Names = _names });
        foreach (var video in target.Tracks.Where(t => t.Type == TrackType.Video && t.Name is not null))
        {
            _videoNames[video.TrackId] = video.Name!;
        }

        _plan = EditPlan.For(target);
        Rows.Clear();
        for (var i = 0; i < target.Tracks.Count; i++)
        {
            var track = target.Tracks[i];
            var chosen = _languages.TryGetValue(track.TrackId, out var tag) ? Languages.Find(tag.Primary)
                : _detected.TryGetValue(track.TrackId, out var read) ? Languages.Find(read.Language)
                : null;
            Rows.Add(new RuleRow(track, i + 1, chosen, LanguageChosen, Moved, KeepChanged, kept: _kept.Contains(track.TrackId))
            {
                LanguageNote = !_languages.ContainsKey(track.TrackId) && _detected.TryGetValue(track.TrackId, out var note)
                    ? Loc.F("návrh UNIVERSUM: {0} – {1}", note.Certain ? Loc.T("isté") : Loc.T("najpravdepodobnejšie"), note.Detail)
                    : "",
                ForcedNote = track.Type != TrackType.Subtitles || _forced.ContainsKey(track.TrackId) || track.Source.Flags.Forced ? ""
                    : ForcedSubtitles.ByAmount(_file, track.Source) is { } amount && track.Role == TrackRole.Forced ? Loc.F("návrh UNIVERSUM: forced – {0}", amount)
                    : "",
                ForcedChanged = ForcedChanged,
                NameWritten = NameWritten,
            });
        }

        // owner, 29. 10. 2026: subtitles out of the language list are shown at the end – the user can keep them
        foreach (var removed in target.RemovedByLanguage)
        {
            Rows.Add(new RuleRow(new TrackTarget(removed, removed.Name, removed.Language, removed.Flags, TrackRole.Main), 0, null,
                LanguageChosen, Moved, KeepChanged, removedByLanguage: true));
        }

        Summary = _plan.Method switch
        {
            WriteMethod.None => Loc.T("Súbor už spĺňa pravidlá UNIVERSUM – len sa uloží do knižnice."),
            WriteMethod.HeaderEdit => Loc.F("Zmení sa len hlavička ({0}) – trvá to pár sekúnd.", ChangeCount(_plan.Changes.Count)),
            _ when target.ContainerChanged => Loc.F("Súbor {0} sa prevedie do MKV – vznikne nový súbor ({1}).", _file.ContainerName, ChangeCount(_plan.Changes.Count)),
            _ when target.AddsFiles || target.TimingChanged => Loc.F("Vznikne nový súbor ({0}).", ChangeCount(_plan.Changes.Count)),
            _ => Loc.F("Mení sa poradie stôp – vznikne nový súbor ({0}).", ChangeCount(_plan.Changes.Count)),
        } + (target.RemovedByLanguage.Count > 0
                ? Loc.F(" Odstránia sa titulky mimo zoznamu jazykov (Nastavenia): {0}.", string.Join(", ", target.RemovedByLanguage.Select(t => Texts.Language(t.Language))))
                : "")
          + (target.RemovedEmpty.Count > 0 ? Loc.F(" Odstránia sa prázdne titulkové stopy (bez titulkov): {0}.", target.RemovedEmpty.Count) : "")
          + (target.Tracks.Count(t => t.IsAdded) is > 0 and var added
              ? Loc.F(" Pridajú sa externé titulky pri videu ({0}): {1}.", added, string.Join(", ", target.Tracks.Where(t => t.IsAdded).Select(t => Path.GetFileName(t.Source.ExternalFile))))
              : "")
          + (_subtitleReading is not null ? Loc.T(" Čítam titulky (jazyk každej titulkovej stopy)…") : "")
          + (target.TagsChanged ? Loc.T(" Tagy sa odstránia.") : "") + (target.RepairIndex ? Loc.T(" Vytvorí sa chýbajúci index videa.") : "") + (_file.HasChapters ? Loc.T(" Kapitoly ostanú.") : "");
        Issues = string.Join("\n", target.Issues.Select(Describe));
        UpdateTarget();
    }

    // the rows are rebuilt by a recalculation: it runs after the control that changed has finished its update
    private void LanguageChosen(RuleRow row)
    {
        if (row.ChosenLanguage is { } language)
        {
            Later(() => ChooseLanguage(row.TrackId, language.Ietf));
        }
    }

    private void Moved(RuleRow row, int by) => Later(() => MoveTrack(row.TrackId, by));

    private void ForcedChanged(RuleRow row, bool forced) => Later(() => ChooseForced(row.TrackId, forced));

    private void NameWritten(RuleRow row, string name) => Later(() => ChooseName(row.TrackId, name));

    /// <summary>
    /// The name of an audio or subtitle track as the user writes it (also used by the self-test); the rules' own name
    /// again needs no choice.
    /// </summary>
    public void ChooseName(int trackId, string name)
    {
        if (_file is null)
        {
            return;
        }

        name = name.Trim();
        _names.Remove(trackId);
        var languages = _detected.ToDictionary(d => d.Key, d => LanguageTag.FromCode(d.Value.Language));
        foreach (var (id, chosen) in _languages)
        {
            languages[id] = chosen;
        }

        var rules = UniversumRules.Apply(_file, _services.Profile,
            new TrackDecisions { Languages = languages, VideoNames = _videoNames, Kept = _kept, Order = _order, Forced = _forced, Names = _names });
        if ((rules.Tracks.FirstOrDefault(t => t.TrackId == trackId)?.Name ?? "") != name)
        {
            _names[trackId] = name;
        }

        Recalculate();
    }

    /// <summary>Forced or not, as the user says (also used by the self-test); the rules' own answer needs no choice.</summary>
    public void ChooseForced(int trackId, bool forced)
    {
        if (_file?.Tracks.FirstOrDefault(t => t.Id == trackId) is not { } track)
        {
            return;
        }

        _forced.Remove(trackId);
        if (UniversumRules.IsForced(_file, track, _services.Profile) != forced)
        {
            _forced[trackId] = forced;
        }

        Recalculate();
    }

    // owner, 30. 10. 2026: the language of every text subtitle track, read in the background (Použiť waits for it)
    private async Task ReadSubtitlesAsync(MkvToolNixInstallation installation, MediaFile file)
    {
        if (!file.Tracks.Any(t => t.Type == TrackType.Subtitles && t.IsTextSubtitles))
        {
            return;
        }

        _subtitleReading?.Cancel();
        var cancellation = new CancellationTokenSource();
        _subtitleReading = cancellation;
        RefreshCommands();
        Recalculate();
        try
        {
            string? ffmpeg;
            try
            {
                ffmpeg = (await _services.SpeechAsync(cancellation.Token)).Ffmpeg;
            }
            catch (Exception ex) when (ex is Core.Speech.SpeechException or IOException or UnauthorizedAccessException)
            {
                ffmpeg = null;
            }

            var found = await SubtitleText.DetectAsync(installation, _services.Runner, ffmpeg, file, cancellation.Token);
            if (ReferenceEquals(_file, file))
            {
                _detected = found;
            }
        }
        catch (OperationCanceledException)
        {
            // another file was opened
        }
        finally
        {
            if (ReferenceEquals(_subtitleReading, cancellation))
            {
                _subtitleReading = null;
            }

            if (ReferenceEquals(_file, file))
            {
                Recalculate();
            }

            RefreshCommands();
        }
    }

    // owner, 30. 10. 2026 (bod 5): subtitle files lying with the video belong to it and are added as its tracks
    private async Task<MediaFile> WithExternalSubtitlesAsync(MediaFile file)
    {
        try
        {
            var siblings = Directory.EnumerateFiles(Path.GetDirectoryName(file.Path)!).ToList();
            var videos = Math.Max(1, siblings.Count(f => Core.Batch.BatchAnalyzer.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
            return await Core.Subtitles.ExternalSubtitleTracks.AddAsync(file, siblings, videos, _services.Profile.ForcedKeywords);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return file;
        }
    }

    /// <summary>The languages of the subtitles are being read (Použiť waits; the self-test waits for it).</summary>
    public bool IsReadingSubtitles => _subtitleReading is not null;

    private void KeepChanged(RuleRow row, bool keep) => Later(() => KeepTrack(row.TrackId, keep));

    private static void Later(Action action) => System.Windows.Application.Current.Dispatcher.InvokeAsync(action);

    /// <summary>Kind, title and episode guessed from the file and folder names; the user can correct them.</summary>
    private void GuessTitle(string path)
    {
        var guess = TitleGuess.ForFile(path);
        _kind = guess.Kind;
        // napovedkyňa (owner, 30. 10. 2026): a series processed before gets its remembered title – no search
        _rememberedSeries = _services.RememberedSeriesFor(path);
        _titleText = _rememberedSeries?.FolderName ?? guess.TitleText;
        // Season 00 or a regular season: asked – the field stays empty until the user answers
        _episodeText = guess.Episode is { IsSeasonQuestion: true } ? "" : guess.Episode?.Number.Tag ?? "";
        _episodeNote = LibraryTargetPicker.EpisodeNote(guess.Episode);
        _tmdbTitle = null;
        _addition = "";
        OnPropertyChanged(nameof(Addition));
        TitleNote = _rememberedSeries is null ? "" : Loc.T("Napovedkyňa: seriál, ktorý už spracúvate – názov bez hľadania (zmeniť ho môžete).");
        Tmdb.ClearConfirmation();
        Tmdb.Refresh();
        OnPropertyChanged(nameof(IsMovie));
        OnPropertyChanged(nameof(IsSeries));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(EpisodeText));
    }

    // the series to remember after the file went into the library: the remembered one while its name stands in the field
    // (it keeps its TMDB ID), otherwise the typed one with the ID of the TMDB title chosen for it
    private TitleInfo? SeriesToRemember()
    {
        if (_kind != MediaKind.Series || TitleGuess.Typed(_kind, TitleText) is not { } title)
        {
            return null;
        }

        return _rememberedSeries is { } remembered && remembered.FolderName == title.FolderName ? remembered
            : _tmdbTitle is { Kind: MediaKind.Series } chosen && title.TmdbId is null ? title with { TmdbId = chosen.Id }
            : title;
    }

    /// <summary>Computes the place in the library from the title, episode and the planned result.</summary>
    private void UpdateTarget()
    {
        _placement = null;
        var layout = _services.Library;
        var title = TitleGuess.Typed(_kind, TitleText);
        var episode = EpisodeNumber.TryParse(EpisodeText);
        if (_plan is null)
        {
            Target = "";
        }
        else if (layout is null)
        {
            Target = Loc.T("Najprv nastavte výstupný priečinok (knižnicu).");
        }
        else if (title is null)
        {
            Target = Loc.T("Zadajte názov titulu, napr. Creep (2004).");
        }
        else if (title.Problem is { } problem)
        {
            Target = Loc.F("Názov titulu: {0} – napr. Creep (2004).", problem);
        }
        else if (Tmdb.NeedsConfirmation)
        {
            Target = Tmdb.Warning;
        }
        else if (_kind == MediaKind.Series && episode is null)
        {
            Target = _episodeNote.StartsWith("OTÁZKA", StringComparison.Ordinal) ? _episodeNote : Loc.T("Zadajte epizódu, napr. S01E04.");
        }
        else
        {
            _placement = layout.Place(_plan.Target, title, episode, _services.Profile).WithAddition(_addition);
            var size = SourceSize();
            Target = _placement.Holds(_plan.Target.Source.Path, size) && _plan.Method == WriteMethod.None
                ? Loc.T("Súbor už je v knižnici na svojom mieste.")
                : Path.GetRelativePath(layout.Root, _placement.PathFor(size)) +
                  (_placement.EstimatedPathLength(size) > LibraryNames.MaxPathLength ? Loc.T("  – cesta je príliš dlhá pre Windows") : "") +
                  (episode is not null && _tmdbTitle is { Kind: MediaKind.Series } series && !series.HasEpisode(episode) ? Loc.T("  – TMDB takú epizódu nepozná") : "") +
                  (_kind == MediaKind.Series && _episodeNote.Length > 0 ? "  – " + _episodeNote : "");
        }

        RefreshCommands();
    }

    // a TMDB address or ID pasted as the title: the address tells film (/movie/) or series (/tv/); the title is read from TMDB
    private bool UseTmdbAddress(string text)
    {
        // only an address (with /movie/ or /tv/): a bare number may be a title like "1917"; the TMDB button looks numbers up
        if (TitleGuess.ParseTmdb(text) is not { Kind: { } kind })
        {
            return false;
        }

        if (kind != _kind)
        {
            _kind = kind;
            OnPropertyChanged(nameof(IsMovie));
            OnPropertyChanged(nameof(IsSeries));
        }

        // nothing is placed until a TMDB title is chosen
        _placement = null;
        if (_services.Tmdb is not null)
        {
            Target = Loc.T("Hľadám titul na TMDB…");
            _ = Tmdb.SearchAsync();
        }
        else
        {
            TitleNote = Loc.T("Adresa TMDB: na načítanie názvu treba kľúč TMDB v Nastaveniach – alebo napíšte Názov (rok).");
            Target = Loc.T("Zadajte názov titulu, napr. Creep (2004).");
        }

        RefreshCommands();
        return true;
    }

    private void ChooseTmdbTitle(TmdbTitle title)
    {
        _tmdbTitle = title;
        if (title.Kind != _kind)
        {
            _kind = title.Kind;
            OnPropertyChanged(nameof(IsMovie));
            OnPropertyChanged(nameof(IsSeries));
        }

        var (text, note) = TmdbPicker.TitleText(title);
        TitleNote = note;
        _titleText = text;
        OnPropertyChanged(nameof(TitleText));
        UpdateTarget();
    }

    // MEGA keeps the original of a file of Single spracovanie: the choice of the originals is not needed
    private bool CanSendToMega() =>
        !Busy && _subtitleReading is null && _file is not null && _plan is not null && !_plan.Target.IsBlocked && _placement is not null && _services.Library is not null &&
        File.Exists(_plan.Target.Source.Path) && !(_plan.Method == WriteMethod.None && _placement.Holds(_plan.Target.Source.Path, SourceSize())) &&
        _placement.EstimatedPathLength(SourceSize()) <= LibraryNames.MaxPathLength;

    /// <summary>
    /// Odoslať do MEGA (owner, 1. 11. 2026): the file with every decision of the page – languages, kept and ordered tracks,
    /// forced, names, title, episode and addition – is written later by MEGA spracovanie; its original is kept.
    /// </summary>
    private void SendToMega()
    {
        if (!CanSendToMega() || TitleGuess.Typed(_kind, TitleText) is not { Problem: null } title || _services.Library is not { } library)
        {
            return;
        }

        var file = Core.Mega.MegaFile.FromSingle(_file!, Decisions(), title, EpisodeNumber.TryParse(EpisodeText), _addition.Length > 0 ? _addition : null, revision: false,
            _services.Profile, Path.GetRelativePath(library.Root, _placement!.PathFor(SourceSize())));
        Result = Mega.Send([file], Loc.T("Single spracovanie: ") + title.Name + (EpisodeText.Length > 0 ? " " + EpisodeText : ""), library.Root, _services.Profile);
        _services.RememberSeries(SeriesToRemember(), _plan!.Target.Source.Path);
    }

    // the decisions of the page: the languages read from the subtitles, then the user's choices over them
    private TrackDecisions Decisions()
    {
        var languages = _detected.ToDictionary(d => d.Key, d => LanguageTag.FromCode(d.Value.Language));
        foreach (var (id, chosen) in _languages)
        {
            languages[id] = chosen;
        }

        return new TrackDecisions { Languages = languages, VideoNames = _videoNames, Kept = _kept, Order = _order, Forced = _forced, Names = _names };
    }

    private bool CanApply() =>
        !Busy && _subtitleReading is null && _plan is not null && !_plan.Target.IsBlocked && _services.Settings.OriginalHandling is not null && _placement is not null &&
        File.Exists(_plan.Target.Source.Path) &&
        !(_plan.Method == WriteMethod.None && _placement.Holds(_plan.Target.Source.Path, SourceSize())) &&
        _placement.EstimatedPathLength(SourceSize()) <= LibraryNames.MaxPathLength;

    /// <summary>Size of the opened file; 0 when it is no longer there (moved into the library).</summary>
    private long SourceSize() => _plan is not null && File.Exists(_plan.Target.Source.Path) ? new FileInfo(_plan.Target.Source.Path).Length : 0;

    private void SetKind(bool selected, MediaKind kind)
    {
        if (!selected || _kind == kind)
        {
            return;
        }

        _kind = kind;
        OnPropertyChanged(nameof(IsMovie));
        OnPropertyChanged(nameof(IsSeries));
        UpdateTarget();
    }

    private async Task ApplyAsync(bool revision = false)
    {
        if (_plan is null || _placement is null || _services.CurrentMkvToolNix is null || _services.Settings.OriginalHandling is not { } handling)
        {
            return;
        }

        var plan = revision ? _plan with { Revision = true } : _plan;
        CanRevise = false;
        Busy = true;
        Result = Loc.T("Pracujem…");
        Progress = 0;
        try
        {
            var processor = _services.Processor(_services.CurrentMkvToolNix);
            var series = SeriesToRemember();
            var started = DateTime.UtcNow;
            var result = await processor.ProcessAsync(plan, _placement, new OutputOptions(handling), new Progress<double>(p => Progress = p));
            _services.LogProcessing(revision ? Loc.T("Single spracovanie – revízia") : Loc.T("Single spracovanie"), plan, result, started);
            CanRevise = result.Status == ProcessingStatus.AlreadyInLibrary;
            _undoRecord = result.UndoRecordPath;
            if (result.Status is ProcessingStatus.Done or ProcessingStatus.Unchanged or ProcessingStatus.AlreadyInLibrary)
            {
                _services.RememberSeries(series, result.SourcePath);
            }

            var cache = _services.Inspector(_services.CurrentMkvToolNix);
            cache.Invalidate(result.SourcePath);
            if (result.OutputPath is not null)
            {
                cache.Invalidate(result.OutputPath);
            }
            Result = result.Status switch
            {
                ProcessingStatus.Done => (revision ? Loc.F("Hotovo (revízia – doterajšia verzia je v Koši): {0}", result.OutputPath) : Loc.F("Hotovo: {0}", result.OutputPath)) +
                                         (_undoRecord is null ? "" : "\n" + Loc.T("Zmenu je možné vrátiť späť.")),
                ProcessingStatus.Unchanged => Loc.T("Súbor už spĺňa pravidlá a je v knižnici."),
                ProcessingStatus.AlreadyInLibrary => Loc.F("Rovnaký stream už v knižnici je: {0}\nNič sa nezapísalo, originál ostal. Ak ho chcete spracovať znova ako novú verziu, stlačte Revízia.", result.OutputPath),
                ProcessingStatus.NeedsReview => Loc.T("Súbor treba najprv skontrolovať (pozri upozornenia)."),
                ProcessingStatus.Cancelled => Loc.T("Zrušené."),
                _ when FileProcessor.NeedsAttention(result) =>
                    Loc.T("POZOR: nepodarilo sa a originál sa nepodarilo úplne vrátiť – pozri podrobnosti; záznam na vrátenie: ") + result.UndoRecordPath + "\n" + string.Join("\n", result.Messages),
                _ when result.Messages.Contains(FileProcessor.RestoredMessage) => Loc.T("Nepodarilo sa – originál je vrátený do pôvodného stavu.") + "\n" + string.Join("\n", result.Messages),
                _ => Loc.T("Nepodarilo sa – originál ostal nezmenený.") + "\n" + string.Join("\n", result.Messages),
            };
            if (result.Status == ProcessingStatus.Done && result.Messages.Count > 0)
            {
                Result += "\n" + string.Join("\n", result.Messages);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private void Enqueue()
    {
        if (_plan is null || _placement is null || _services.CurrentMkvToolNix is not { } installation || _services.Settings.OriginalHandling is not { } handling)
        {
            return;
        }

        var job = new FileJob(Loc.F("Single spracovanie: {0} → {1}", Path.GetFileName(_plan.Target.Source.Path), Path.GetFileName(_placement.Directory)),
            _services.Processor(installation), _plan, _placement, new OutputOptions(handling));
        job.Finished += (_, result) => JobRow.OnUi(() =>
        {
            _services.Inspector(installation).Invalidate(result.SourcePath);
            if (result.OutputPath is not null)
            {
                _services.Inspector(installation).Invalidate(result.OutputPath);
            }
        });
        _services.Enqueue(job);
        _services.RememberSeries(SeriesToRemember(), _plan.Target.Source.Path);
        Result = _services.Settings.AutoStartQueue
            ? Loc.F("Pridané do fronty ako úloha {0}.", job.Id)
            : Loc.F("Pridané do fronty ako úloha {0} – frontu spustíte na stránke Fronta úloh.", job.Id);
    }

    private async Task UndoAsync()
    {
        if (_undoRecord is null || _services.CurrentMkvToolNix is null)
        {
            return;
        }

        Busy = true;
        try
        {
            var processor = _services.Processor(_services.CurrentMkvToolNix);
            var problems = await processor.UndoAsync(_undoRecord);
            Result = problems.Count == 0 ? Loc.T("Pôvodná hlavička je obnovená a súbor je na pôvodnom mieste.") : Loc.T("Vrátenie sa nepodarilo:") + "\n" + string.Join("\n", problems);
            if (problems.Count == 0)
            {
                _undoRecord = null;
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OpenAsync()
    {
        // every video format MKVToolNix reads; anything other than MKV becomes a new MKV
        var patterns = string.Join(";", Universum.Core.Batch.BatchAnalyzer.VideoExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog { Filter = $"Video (MKV, MP4, AVI, TS, M2TS, MOV, MPG, WebM…)|{patterns}|{Loc.T("Všetky súbory")}|*.*" };
        if (_services.Settings.InputDirectory is { } input && Directory.Exists(input))
        {
            // the input folder of the settings is where new files come
            dialog.InitialDirectory = input;
        }

        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(dialog.FileName);
        }
    }

    private void ChooseOutputDirectory()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Výstupný priečinok (knižnica s MOVIES a TVSHOWS)") };
        if (dialog.ShowDialog() == true)
        {
            _services.SetOutputDirectory(dialog.FolderName);
        }
    }

    private void SetHandling(OriginalHandling? handling)
    {
        if (handling is null)
        {
            return;
        }

        _services.Settings.OriginalHandling = handling;
        _services.Settings.Save();
        OnPropertyChanged(nameof(KeepOriginal));
        OnPropertyChanged(nameof(DeleteOriginal));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OpenCommand.NotifyCanExecuteChanged();
        ClearCommand?.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        QueueCommand?.NotifyCanExecuteChanged();
        SendToMegaCommand?.NotifyCanExecuteChanged();
        ReviseCommand?.NotifyCanExecuteChanged();
        ChooseOutputDirectoryCommand.NotifyCanExecuteChanged();
    }

    private string Describe(RuleIssue issue) => issue.Kind switch
    {
        RuleIssueKind.UndeterminedLanguage => Loc.F("Stopa {0}: neurčený jazyk – vyberte ho v stĺpci Jazyk.", Rows.FirstOrDefault(r => r.TrackId == issue.TrackId)?.Position),
        RuleIssueKind.MultipleVideoTracks => Loc.T("Súbor má viac video stôp – treba ho skontrolovať ručne."),
        RuleIssueKind.NoAudio => Loc.T("Súbor nemá zvukovú stopu – treba ho skontrolovať ručne."),
        RuleIssueKind.NoPreferredSubtitles => Loc.T("Zvuk nie je v preferovanom jazyku a chýbajú titulky v preferovanom jazyku – žiadne titulky sa nezobrazia automaticky."),
        RuleIssueKind.MissingIndex => Loc.T("Video nemá index (pretáčanie môže byť pomalé alebo nefunkčné) – nový zápis ho vytvorí."),
        RuleIssueKind.ContainerWarnings => Loc.T("mkvmerge pri čítaní hlási upozornenia: ") + string.Join(" ", _file?.Warnings ?? []),
        _ => issue.Kind.ToString(),
    };

    // "1 zmena", "3 zmeny", "5 zmien"
    private static string ChangeCount(int count) => count switch
    {
        1 => Loc.T("1 zmena"),
        >= 2 and <= 4 => Loc.F("{0} zmeny", count),
        _ => Loc.F("{0} zmien", count),
    };
}
