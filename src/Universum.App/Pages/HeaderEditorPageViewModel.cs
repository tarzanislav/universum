using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Jobs;
using Universum.Core.Execution;
using Universum.Core.Jobs;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>
/// Header editor (like MKVToolNix GUI): names, languages and flags of the tracks, the segment title, tracks left out
/// and the order of audio and subtitles – edited in the file where it is. The UNIVERSUM rules fill everything in with
/// one click; the Check panel points out what is worth a second look. Only the header changes when it can (seconds);
/// a new order or a left-out track writes a new file under the same name and the old one goes to the Recycle Bin.
/// </summary>
public sealed class HeaderEditorPageViewModel : ObservableObject
{
    private static IReadOnlyList<LanguageChoice> Choices => LanguageChoice.Common;

    private readonly AppServices _services;
    private MediaFile? _file;
    private HeaderRow? _selected;
    private string _fileName = "";
    private string _summary = "Otvorte súbor MKV alebo ho sem pretiahnite myšou.";
    private string _title = "";
    private bool _removeTags;
    private string _method = "";
    private string _result = "";
    private double _progress;
    private bool _busy;
    private string? _undoRecord;
    private bool _loading;

    public HeaderEditorPageViewModel(AppServices services)
    {
        _services = services;
        Library = new LibraryTargetPicker(services, Target);
        Library.Changed += RefreshCommands;
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !Busy);
        ApplyRulesCommand = new AsyncRelayCommand(ApplyRulesAsync, () => !Busy && _file is not null);
        ResetCommand = new RelayCommand(() => Fill(_file), () => !Busy && _file is not null);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => Move(+1), () => CanMove(+1));
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !Busy && Target() is { HasChanges: true } && Library.Placement is not null);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => !Busy && _undoRecord is not null);
        QueueCommand = new RelayCommand(Enqueue, () => !Busy && Target() is { HasChanges: true } && Library.Placement is not null);
    }

    /// <summary>Adds the change to the job queue instead of saving now.</summary>
    public RelayCommand QueueCommand { get; }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand ApplyRulesCommand { get; }

    public RelayCommand ResetCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand UndoCommand { get; }

    /// <summary>All tracks in their new order (left-out ones stay in the list, marked).</summary>
    public ObservableCollection<HeaderRow> Rows { get; } = [];

    /// <summary>The Check panel: what is worth a second look in the result.</summary>
    public ObservableCollection<string> Findings { get; } = [];

    public bool HasFindings => Findings.Count > 0;

    public HeaderRow? SelectedRow
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                RefreshCommands();
            }
        }
    }

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    /// <summary>Segment title of the file ("názov súboru" in MKVToolNix); empty removes it.</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                Recalculate();
            }
        }
    }

    public bool RemoveTags
    {
        get => _removeTags;
        set
        {
            if (SetProperty(ref _removeTags, value))
            {
                Recalculate();
            }
        }
    }

    public bool HasTags => _file?.HasTags == true;

    /// <summary>How the change will be written: header only (seconds) or a new file (minutes).</summary>
    public string Method { get => _method; private set => SetProperty(ref _method, value); }

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

    public string? FilePath => _file?.Path;

    /// <summary>Where the result goes in the library (title, episode, name by the rules).</summary>
    public LibraryTargetPicker Library { get; }

    public async Task LoadAsync(string path)
    {
        Result = "";
        _undoRecord = null;
        await ReadAsync(path);
        Library.Guess(path);
    }

    /// <summary>The target state the editor describes now; null without a file.</summary>
    public TargetState? Target()
    {
        if (_file is null)
        {
            return null;
        }

        var kept = Rows.Where(r => !r.IsRemoved).Select(r => r.ToEdit()).ToList();
        var removed = Rows.Where(r => r.IsRemoved).Select(r => r.TrackId).ToHashSet();
        return ManualTarget.Create(_file, kept, removed, RemoveTags && _file.HasTags, Title == (_file.Title ?? "") ? null : Title);
    }

    // owner, 30. 10. 2026: the language of every text subtitle track read from its text (once for the opened file)
    private IReadOnlyDictionary<int, Core.Text.SubtitleLanguageResult> _detected = new Dictionary<int, Core.Text.SubtitleLanguageResult>();
    private MediaFile? _detectedFor;

    /// <summary>
    /// Použiť pravidlá: the languages of the subtitles are read from their text first (owner, 30. 10. 2026; once for the
    /// opened file – a few parts of the film, like the audio), then the rules fill everything in.
    /// </summary>
    public async Task ApplyRulesAsync()
    {
        if (_file is not { } file)
        {
            return;
        }

        if (!ReferenceEquals(_detectedFor, file) && file.Tracks.Any(t => t.Type == TrackType.Subtitles && t.IsTextSubtitles) &&
            _services.CurrentMkvToolNix is { } installation)
        {
            Busy = true;
            Result = "Čítam titulky – jazyk každej titulkovej stopy z jej textu…";
            try
            {
                string? ffmpeg;
                try
                {
                    ffmpeg = (await _services.SpeechAsync()).Ffmpeg;
                }
                catch (Exception ex) when (ex is Core.Speech.SpeechException or IOException or UnauthorizedAccessException)
                {
                    ffmpeg = null;
                }

                var found = await SubtitleText.DetectAsync(installation, _services.Runner, ffmpeg, file);
                if (ReferenceEquals(_file, file))
                {
                    _detected = found;
                    _detectedFor = file;
                }
            }
            finally
            {
                Busy = false;
            }

            if (!ReferenceEquals(_file, file))
            {
                return;
            }
        }

        ApplyRules();
    }

    /// <summary>
    /// Fills everything in by the UNIVERSUM rules: the languages read from the subtitles (návrh UNIVERSUM), and a language
    /// the user chose for a track wins.
    /// </summary>
    public void ApplyRules()
    {
        if (_file is null)
        {
            return;
        }

        var languages = new Dictionary<int, LanguageTag>();
        var detected = ReferenceEquals(_detectedFor, _file) ? _detected : new Dictionary<int, Core.Text.SubtitleLanguageResult>();
        foreach (var (id, found) in detected)
        {
            languages[id] = LanguageTag.FromCode(found.Language);
        }

        foreach (var row in Rows.Where(r => !r.Language.Tag.IsUndetermined && !r.Language.Same(r.Source.Language)))
        {
            languages[row.TrackId] = row.Language.Tag;
        }

        var target = UniversumRules.Apply(_file, _services.Profile, new TrackDecisions { Languages = languages });
        _loading = true;
        try
        {
            var ordered = target.Tracks.Select(t => Rows.First(r => r.TrackId == t.TrackId)).ToList();
            foreach (var (row, track) in ordered.Zip(target.Tracks))
            {
                row.Take(track);
            }

            // subtitles out of the language list and empty ones are marked for removal (the user can take them back)
            var rest = Rows.Where(r => !ordered.Contains(r)).ToList();
            foreach (var row in rest.Where(r => target.RemovedByLanguage.Any(t => t.Id == r.TrackId) || target.RemovedEmpty.Any(t => t.Id == r.TrackId)))
            {
                row.IsRemoved = true;
            }

            Rows.Clear();
            foreach (var row in ordered.Concat(rest))
            {
                Rows.Add(row);
            }

            RemoveTags = target.RemoveTags && _file.HasTags;
        }
        finally
        {
            _loading = false;
        }

        Recalculate();
        var read = detected.Count == 0 ? ""
            : $" Jazyk {detected.Count} titulkových stôp je návrh UNIVERSUM podľa ich textu" +
              (detected.Values.Count(r => !r.Certain) is > 0 and var probable ? $" ({probable} len najpravdepodobnejší – overte)" : "") + ".";
        var empty = target.RemovedEmpty.Count == 0 ? "" : $" Prázdne titulkové stopy ({target.RemovedEmpty.Count}) sú označené na odstránenie.";
        Result = (target.Issues.Any(i => i.Kind == RuleIssueKind.UndeterminedLanguage)
            ? "Pravidlá sú použité. Stopám s neurčeným jazykom ho vyberte a pravidlá použite znova."
            : "Pravidlá UNIVERSUM sú použité – skontrolujte a uložte.") + read + empty;
    }

    private async Task ReadAsync(string path)
    {
        _file = null;
        Rows.Clear();
        FileName = Path.GetFileName(path);
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Summary = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            Recalculate();
            return;
        }

        try
        {
            var file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
            Fill(file);
        }
        catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException or TimeoutException)
        {
            Summary = ex.Message;
            Recalculate();
        }
    }

    private void Fill(MediaFile? file)
    {
        _file = file;
        _loading = true;
        try
        {
            Rows.Clear();
            foreach (var track in file?.Tracks ?? [])
            {
                Rows.Add(new HeaderRow(track, Choices, Recalculate));
            }

            // owner, 29. 10. 2026: the segment title is removed unless the user types one
            _title = "";
            _removeTags = false;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(RemoveTags));
            OnPropertyChanged(nameof(HasTags));
            OnPropertyChanged(nameof(FilePath));
            Summary = file is null ? "" : Texts.FileSummary(file);
        }
        finally
        {
            _loading = false;
        }

        Recalculate();
    }

    private void Recalculate()
    {
        if (_loading)
        {
            return;
        }

        var position = 1;
        foreach (var row in Rows)
        {
            row.Position = row.IsRemoved ? 0 : position++;
        }

        Findings.Clear();
        var target = Target();
        if (target is null)
        {
            Method = "";
        }
        else
        {
            var plan = EditPlan.For(target);
            Method = plan.Method switch
            {
                WriteMethod.None => "Bez zmien.",
                WriteMethod.HeaderEdit => $"Kópia v knižnici so zmenenou hlavičkou ({Count(plan.Changes.Count)}) – trvá to sekundy až minúty (kopírovanie). Originál ostane nezmenený.",
                _ => $"Zapíše sa nový súbor do knižnice ({Count(plan.Changes.Count)}; poradie alebo vynechané stopy) – trvá to minúty. Originál ostane nezmenený.",
            };
            foreach (var finding in TargetChecks.Review(target, _services.Profile))
            {
                Findings.Add(Describe(finding));
            }
        }

        OnPropertyChanged(nameof(HasFindings));
        Library?.Update();
        RefreshCommands();
    }

    /// <summary>A path in the library, relative to it (or as it is).</summary>
    private string LibraryPath(string? path) =>
        path is null ? "" : _services.Library is { } layout && layout.Contains(path) ? Path.GetRelativePath(layout.Root, path) : path;

    private static string Count(int changes) => changes switch
    {
        1 => "1 zmena",
        >= 2 and <= 4 => $"{changes} zmeny",
        _ => $"{changes} zmien",
    };

    private string Describe(CheckFinding finding)
    {
        var track = finding.TrackId is { } id ? Rows.FirstOrDefault(r => r.TrackId == id) : null;
        var where = track is null ? "" : $"Stopa {track.Position} ({track.Type.ToLowerInvariant()}): ";
        var kind = finding.Type switch
        {
            TrackType.Video => "videa",
            TrackType.Audio => "zvuku",
            TrackType.Subtitles => "titulkov",
            _ => "stôp",
        };
        return finding.Kind switch
        {
            CheckKind.UndeterminedLanguage => where + "neurčený jazyk (und).",
            CheckKind.SeveralDefaults => $"Viac stôp {kind} má Default – prehrávač vyberie ktorúkoľvek.",
            CheckKind.NoneEnabled => $"Žiadna stopa {kind} nie je zapnutá (Enabled).",
            CheckKind.ForcedWithoutDefault => where + "forced titulky bez Default – väčšina prehrávačov ich sama nezobrazí.",
            CheckKind.TagsRemain => "Súbor si ponechá tagy (názvy, štatistiky, údaje o kódovaní).",
            CheckKind.OrderNotPreferred => $"Poradie {kind} nie je CZ → SK → ostatné (komentáre na koniec).",
            _ => finding.Kind.ToString(),
        };
    }

    /// <summary>Audio moves among audio and subtitles among subtitles; video and other tracks stay.</summary>
    private bool CanMove(int step) =>
        !Busy && SelectedRow is { CanRemove: true } row && Neighbour(row, step) is not null;

    private HeaderRow? Neighbour(HeaderRow row, int step)
    {
        for (var i = Rows.IndexOf(row) + step; i >= 0 && i < Rows.Count; i += step)
        {
            if (Rows[i].TrackType == row.TrackType)
            {
                return Rows[i];
            }
        }

        return null;
    }

    /// <summary>Moves the selected track one place among its kind (also Ctrl+↑/↓).</summary>
    public void Move(int step)
    {
        if (SelectedRow is not { } row || Neighbour(row, step) is not { } other)
        {
            return;
        }

        var (a, b) = (Rows.IndexOf(row), Rows.IndexOf(other));
        Rows.Move(a, b);
        Rows.Move(b + (a < b ? -1 : 1), a);
        SelectedRow = row;
        Recalculate();
    }

    private async Task SaveAsync()
    {
        if (Target() is not { HasChanges: true } target || _services.CurrentMkvToolNix is not { } installation || Library.Placement is not { } placement)
        {
            return;
        }

        Busy = true;
        Result = "Ukladám…";
        Progress = 0;
        try
        {
            var plan = EditPlan.For(target);
            var processor = _services.Processor(installation);
            var started = DateTime.UtcNow;
            var result = await processor.ProcessAsync(plan, placement, new OutputOptions(OriginalHandling.Keep), new Progress<double>(p => Progress = p));
            _services.LogProcessing("Úprava hlavičky", plan, result, started);
            _services.Inspector(installation).Invalidate(target.Source.Path);
            _undoRecord = result.UndoRecordPath;
            Result = result.Status switch
            {
                ProcessingStatus.Done => $"Uložené do knižnice: {LibraryPath(result.OutputPath)}." +
                                         (_undoRecord is null ? " Originál ostal nezmenený." : " Zmenu súboru v knižnici je možné vrátiť späť."),
                ProcessingStatus.AlreadyInLibrary => $"Rovnaký výsledok už v knižnici je: {LibraryPath(result.OutputPath)}. Nič sa nezapísalo.",
                ProcessingStatus.Unchanged => "Bez zmien.",
                ProcessingStatus.Cancelled => "Zrušené.",
                _ when FileProcessor.NeedsAttention(result) =>
                    "POZOR: nepodarilo sa a súbor sa nepodarilo úplne vrátiť – záznam na vrátenie: " + result.UndoRecordPath + "\n" + string.Join("\n", result.Messages),
                _ when result.Messages.Contains(FileProcessor.RestoredMessage) => "Nepodarilo sa – súbor je vrátený do pôvodného stavu.\n" + string.Join("\n", result.Messages),
                _ => "Nepodarilo sa – súbor ostal nezmenený.\n" + string.Join("\n", result.Messages),
            };
            if (result.Status == ProcessingStatus.Done && result.Messages.Count > 0)
            {
                Result += "\n" + string.Join("\n", result.Messages);
            }

            if (result.Status == ProcessingStatus.Done)
            {
                var message = Result;
                await ReadAsync(result.OutputPath ?? target.Source.Path);
                Result = message;
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private void Enqueue()
    {
        if (Target() is not { HasChanges: true } target || _services.CurrentMkvToolNix is not { } installation || Library.Placement is not { } placement)
        {
            return;
        }

        var path = target.Source.Path;
        var plan = EditPlan.For(target);
        var job = new FileJob($"Úprava hlavičky: {Path.GetFileName(path)}", _services.Processor(installation), plan,
            placement, new OutputOptions(OriginalHandling.Keep));
        job.Finished += (finished, result) => JobRow.OnUi(() => _ = AfterJobAsync(finished, result, installation));
        _services.Enqueue(job);
        Result = $"Pridané do fronty ako úloha {job.Id}" + (_services.Settings.AutoStartQueue ? "." : " – frontu spustíte na stránke Fronta úloh.");
    }

    /// <summary>A queued change finished: the editor shows the file as it is now (when it still shows that file).</summary>
    private async Task AfterJobAsync(FileJob job, ProcessingResult result, MkvToolNixInstallation installation)
    {
        var path = job.Plan.Target.Source.Path;
        _services.Inspector(installation).Invalidate(path);
        if (_file is null || !Universum.Core.IO.Paths.Same(_file.Path, path) || Busy)
        {
            return;
        }

        if (result.Status == ProcessingStatus.Done)
        {
            _undoRecord = result.UndoRecordPath;
            await ReadAsync(result.OutputPath ?? path);
        }

        Result = $"Úloha {job.Id}: {job.Outcome}";
        RefreshCommands();
    }

    private async Task UndoAsync()
    {
        if (_undoRecord is null || _services.CurrentMkvToolNix is not { } installation || _file is null)
        {
            return;
        }

        Busy = true;
        try
        {
            var path = _file.Path;
            var problems = await _services.Processor(installation).UndoAsync(_undoRecord);
            _services.Inspector(installation).Invalidate(path);
            if (problems.Count == 0)
            {
                _undoRecord = null;
                await ReadAsync(path);
                Result = "Pôvodná hlavička je obnovená.";
            }
            else
            {
                Result = "Vrátenie sa nepodarilo:\n" + string.Join("\n", problems);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Súbory Matroska (*.mkv;*.mka;*.mks)|*.mkv;*.mka;*.mks|Všetky súbory|*.*" };
        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(dialog.FileName);
        }
    }

    private void RefreshCommands()
    {
        OpenCommand.NotifyCanExecuteChanged();
        ApplyRulesCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        QueueCommand?.NotifyCanExecuteChanged();
    }
}
