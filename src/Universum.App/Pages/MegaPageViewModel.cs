using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Jobs;
using Universum.Core.Execution;
using Universum.Core.Jobs;
using Universum.Core.Mega;

namespace Universum.App.Pages;

/// <summary>
/// MEGA spracovanie (owner, 29. 10. 2026): batches prepared in Hromadné spracovanie, written later – when the user starts
/// them, right after the batch above, or at a date and time (UNIVERSUM has to be running then). Each batch runs as a job
/// of the job queue; right before it every file is read again and only an unchanged file is written.
/// </summary>
public sealed class MegaPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer? _timer;
    private MegaBatchRow? _selected;
    private MegaFileRow? _selectedFile;
    private string _message = "";

    public MegaPageViewModel(AppServices services, bool schedule = true)
    {
        _services = services;
        StartCommand = new AsyncRelayCommand(() => StartAsync(_selected!.Batch), () => _selected?.Batch is { State: MegaBatchState.Waiting or MegaBatchState.Missed });
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => _selected?.Batch is { State: MegaBatchState.DoneWithProblems or MegaBatchState.Cancelled } batch &&
                                                                   batch.Files.Any(f => f.State is MegaFileState.Skipped or MegaFileState.Failed or MegaFileState.Cancelled or MegaFileState.Waiting));
        MoveUpCommand = new RelayCommand(() => Move(-1), () => _selected is not null && Rows.IndexOf(_selected) > 0);
        MoveDownCommand = new RelayCommand(() => Move(1), () => _selected is not null && Rows.IndexOf(_selected) < Rows.Count - 1);
        RemoveCommand = new RelayCommand(Remove, () => _selected?.Batch is { State: not MegaBatchState.Queued, IsBeingEdited: false });
        RemoveFileCommand = new RelayCommand(RemoveFile, () => _selected?.Batch is { State: MegaBatchState.Waiting or MegaBatchState.Missed, IsBeingEdited: false } &&
                                                              _selectedFile?.File.State == MegaFileState.Waiting);
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => _services.Mega.Batches.Any(b => !b.IsOpen));
        DeleteOriginalsCommand = new AsyncRelayCommand(DeleteOriginalsAsync, () => _selected?.Batch is { Originals: not OriginalsMode.Keep, State: not MegaBatchState.Queued } batch &&
                                                                                   !batch.IsOpen && Originals(batch).Count > 0);
        EditCommand = new RelayCommand(() => EditRequested?.Invoke(_selected!.Batch),
            () => _selected?.Batch is { State: MegaBatchState.Waiting or MegaBatchState.Missed, IsBeingEdited: false } batch &&
                  batch.Files.Any(f => f.State == MegaFileState.Waiting));
        services.MegaChanged += Load;
        Load();
        _message = services.MegaProblem;

        if (schedule)
        {
            // a time missed while UNIVERSUM was not running is not caught up without the user
            Tick();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Load();
        }
    }

    public ObservableCollection<MegaBatchRow> Rows { get; } = [];

    public ObservableCollection<MegaFileRow> Files { get; } = [];

    public MegaBatchRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                ShowFiles();
                RefreshCommands();
            }
        }
    }

    public MegaFileRow? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                RemoveFileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public string Summary => Rows.Count == 0
        ? "Žiadna dávka. V Hromadnom spracovaní pripravte súbory a dajte Odoslať do MEGA."
        : $"{Texts.Count(Rows.Count, "dávka", "dávky", "dávok")} · čaká {Rows.Count(r => r.Batch.IsOpen)} · " +
          "dávka v nastavenom čase sa spustí len vtedy, keď UNIVERSUM beží.";

    public AsyncRelayCommand StartCommand { get; }

    /// <summary>Checks and writes again the files of a finished batch that were skipped, failed or cancelled.</summary>
    public AsyncRelayCommand RetryCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand RemoveFileCommand { get; }

    public RelayCommand ClearFinishedCommand { get; }

    /// <summary>
    /// Zmazať originály of a finished batch sent with "zmazať ručne" (owner, 30. 10. 2026): the originals of its results
    /// in the library go to the Recycle Bin.
    /// </summary>
    public AsyncRelayCommand DeleteOriginalsCommand { get; private set; } = null!;

    private static IReadOnlyList<WrittenOriginal> Originals(MegaBatch batch) =>
        batch.Files.Where(f => !f.OriginalRemoved && f.ResultPath is not null && f.State is MegaFileState.Done or MegaFileState.AlreadyInLibrary && File.Exists(f.Path))
            .Select(f => new WrittenOriginal(f.Path, f.Size, f.ModifiedUtc, f.ResultPath!)).ToList();

    private async Task DeleteOriginalsAsync()
    {
        if (_selected?.Batch is not { } batch || Originals(batch) is not { Count: > 0 } originals ||
            !Confirm($"Zmazať {Texts.Count(originals.Count, "originál", "originály", "originálov")} dávky {batch.Label}?\n\n" +
                     "Idú do Koša Windows (dajú sa z neho obnoviť). Zmaže sa len originál, ktorého výsledok je v knižnici a ktorý sa od prípravy dávky nezmenil; " +
                     "súbory v knižnici sa nikdy nemažú."))
        {
            return;
        }

        var layout = new Core.Library.LibraryLayout(batch.LibraryRoot);
        var index = _services.LibraryIndex;
        var current = _services.Library;
        var (removed, kept) = await Task.Run(() => OriginalCleanup.RemoveAsync(originals, index, new Platform.Windows.WindowsRecycleBin(),
            path => layout.HoldsResult(path) || current?.HoldsResult(path) == true));
        foreach (var file in batch.Files.Where(f => removed.Contains(f.Path, Core.IO.Paths.Comparer)))
        {
            file.OriginalRemoved = true;
            file.Note = (file.ResultPath ?? file.Note) + " · originál v Koši";
        }

        Message = $"{batch.Label}: do Koša {Texts.Count(removed.Count, "originál", "originály", "originálov")}" +
                  (kept.Count > 0 ? $" · ostalo {kept.Count}: " + string.Join("; ", kept.Take(5)) + (kept.Count > 5 ? " …" : "") : "");
        Saved(batch);
        _services.NotifyMegaChanged();
    }

    /// <summary>Opens the selected batch in Hromadné spracovanie to be corrected or completed.</summary>
    public RelayCommand EditCommand { get; }

    /// <summary>The main window opens Hromadné spracovanie with the batch.</summary>
    public event Action<MegaBatch>? EditRequested;

    /// <summary>Asks before something is removed (the self-test answers itself).</summary>
    public Func<string, bool> Confirm { get; set; } = question =>
        System.Windows.MessageBox.Show(question, "UNIVERSUM", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    /// <summary>The job of a batch that was started (the self-test waits for it).</summary>
    public MegaJob? LastJob { get; private set; }

    /// <summary>Adds the batch to the job queue and starts the queue.</summary>
    public async Task StartAsync(MegaBatch batch)
    {
        if (batch.State is not (MegaBatchState.Waiting or MegaBatchState.Missed))
        {
            return;
        }

        if (batch.IsBeingEdited)
        {
            Message = $"{batch.Label} sa práve upravuje v Hromadnom spracovaní – spustí sa po uložení.";
            return;
        }

        // taken at once: the timer, the button and the batch above cannot start it a second time meanwhile
        var before = batch.State;
        batch.State = MegaBatchState.Queued;
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            batch.State = before;
            Message = $"{batch.Label} sa nespustila: MKVToolNix nie je k dispozícii.";
            Load();
            return;
        }

        var inspector = _services.Inspector(installation);
        var job = new MegaJob(batch, _services.Processor(installation, deleteOriginals: batch.Originals == OriginalsMode.DeleteAutomatically), (path, token) => inspector.InspectAsync(path, token), b => JobRow.OnUi(() => Saved(b)));
        job.Changed += j =>
        {
            if (j.IsFinished)
            {
                JobRow.OnUi(() => Finished((MegaJob)j));
            }
        };
        batch.State = MegaBatchState.Queued;
        Save();
        LastJob = job;
        _services.Jobs.Add(job, start: true);
        Message = $"{batch.Label} je vo Fronte úloh (úloha {job.Id}).";
        Load();
    }

    /// <summary>A batch whose time has come is started (called every 30 seconds); a late one waits for the user.</summary>
    public void Tick()
    {
        try
        {
            var missed = _services.Mega.Batches.Count(b => b.State == MegaBatchState.Missed);
            foreach (var due in _services.Mega.Due(DateTime.Now))
            {
                _ = StartAsync(due);
            }

            if (_services.Mega.Batches.Count(b => b.State == MegaBatchState.Missed) != missed)
            {
                Message = "Dávka nespustená v čase – UNIVERSUM vtedy nebežalo alebo počítač spal. Spustite ju ručne alebo nastavte nový čas.";
                Load();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Zoznam dávok sa nepodarilo uložiť: " + ex.Message;
        }
    }

    /// <summary>The start of the selected batch changed on its row.</summary>
    internal void ScheduleChanged(MegaBatchRow row)
    {
        if (row.Batch.State == MegaBatchState.Missed && row.Batch.Start != MegaStart.At)
        {
            row.Batch.State = MegaBatchState.Waiting;
        }

        if (row.Batch is { State: MegaBatchState.Missed, Start: MegaStart.At } missed && missed.StartAt > DateTime.Now)
        {
            missed.State = MegaBatchState.Waiting;
        }

        Message = row.Batch is { Start: MegaStart.At, StartAt: { } at } && at <= DateTime.Now
            ? $"Čas {row.Batch.Label} je v minulosti – sama sa nespustí; nastavte budúci dátum a čas."
            : "";
        Save();
        row.Refresh();
        OnPropertyChanged(nameof(Summary));
        RefreshCommands();
    }

    internal void InvalidTime(MegaBatchRow row, string text)
    {
        Message = $"Čas „{text}“ sa nedá použiť – zadajte ho ako 2:00 alebo 23:30.";
        row.Refresh();
    }

    private void Finished(MegaJob job)
    {
        // the state of the whole batch, from all its files (also those of an earlier run)
        var batch = job.Batch;
        batch.State = job.Status == JobStatus.Cancelled ? MegaBatchState.Cancelled
            : batch.Files.All(f => f.State is MegaFileState.Done or MegaFileState.AlreadyInLibrary) ? MegaBatchState.Done
            : MegaBatchState.DoneWithProblems;
        batch.Outcome = job.Outcome.Length > 0 ? job.Outcome : job.Messages.LastOrDefault()?.Text ?? "";
        batch.FinishedUtc = DateTime.UtcNow;
        Save();
        _services.NotifyMegaChanged();

        // the next batch waiting for this one starts – not after a cancelled run
        if (batch.State != MegaBatchState.Cancelled && _services.Mega.Follower(batch) is { } next)
        {
            _ = StartAsync(next);
        }
    }

    private async Task RetryAsync()
    {
        if (_selected?.Batch is not { } batch)
        {
            return;
        }

        // files taken meanwhile – by another open batch or by a job not finished yet (a run of Hromadné spracovanie) –
        // stay out: a file is never in two runs
        var taken = new HashSet<string>(_services.Mega.Reserved().Keys, Core.IO.Paths.Comparer);
        foreach (var job in _services.Jobs.Jobs.Where(j => !j.IsFinished))
        {
            taken.UnionWith((job switch
            {
                BatchJob run => run.Paths,
                MegaJob other when other.Batch != batch => other.Batch.Files.Select(f => f.Path),
                _ => [],
            }).Select(Path.GetFullPath));
        }

        var again = 0;
        // also files a cancelled run did not reach: they were free meanwhile
        foreach (var file in batch.Files.Where(f => f.State is MegaFileState.Skipped or MegaFileState.Failed or MegaFileState.Cancelled or MegaFileState.Waiting))
        {
            if (taken.Contains(Path.GetFullPath(file.Path)))
            {
                file.State = MegaFileState.Skipped;
                file.Note = "medzičasom v inej dávke alebo úlohe – vynechaný";
                continue;
            }

            file.State = MegaFileState.Waiting;
            file.Note = "";
            again++;
        }

        if (again == 0 && !batch.Files.Any(f => f.State == MegaFileState.Waiting))
        {
            Message = "Nie je čo spustiť znova – súbory sú medzičasom v inej dávke alebo úlohe.";
            Saved(batch);
            return;
        }

        batch.State = MegaBatchState.Waiting;
        batch.Start = MegaStart.Manual;
        Save();

        // its files are taken again: Hromadné spracovanie shows them "v dávke"
        _services.NotifyMegaChanged();
        await StartAsync(batch);
    }

    private void Move(int step)
    {
        if (_selected is { } row && _services.Mega.Move(row.Batch, step))
        {
            Load();
            Selected = Rows.FirstOrDefault(r => r.Batch == row.Batch);
        }
    }

    private void Remove()
    {
        if (_selected?.Batch is not { } batch ||
            !Confirm($"Odstrániť {batch.Label} z MEGA spracovania?\n\n{batch.Name}\n\nSúbory sa nemenia; tie, čo sa ešte nezapísali, sa v Hromadnom spracovaní znova ukážu ako voľné."))
        {
            return;
        }

        _services.Mega.Remove(batch);
        _services.NotifyMegaChanged();
    }

    private void RemoveFile()
    {
        if (_selected?.Batch is not { } batch || _selectedFile?.File is not { } file ||
            !Confirm($"Vybrať súbor z dávky?\n\n{Path.GetFileName(file.Path)}\n\nSúbor sa nemení; v Hromadnom spracovaní sa znova ukáže ako voľný."))
        {
            return;
        }

        batch.Files.Remove(file);
        if (batch.Files.Count == 0)
        {
            _services.Mega.Remove(batch);
        }
        else
        {
            Save();
        }

        _services.NotifyMegaChanged();
    }

    private void ClearFinished()
    {
        _services.Mega.ClearFinished();
        _services.NotifyMegaChanged();
    }

    // a file of a running batch finished or was skipped
    private void Saved(MegaBatch batch)
    {
        Save();
        if (_selected?.Batch == batch)
        {
            foreach (var row in Files)
            {
                row.Refresh();
            }
        }

        foreach (var row in Rows.Where(r => r.Batch == batch))
        {
            row.Refresh();
        }
    }

    private void Save()
    {
        try
        {
            _services.Mega.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Zoznam dávok sa nepodarilo uložiť: " + ex.Message;
        }
    }

    private void Load()
    {
        // a batch just added is shown selected, otherwise the one selected before
        var selected = _services.Mega.Batches.Count > Rows.Count && Rows.Count > 0 || _selected is null && _services.Mega.Batches.Count > 0
            ? _services.Mega.Batches[^1]
            : _selected?.Batch;
        Rows.Clear();
        foreach (var batch in _services.Mega.Batches)
        {
            Rows.Add(new MegaBatchRow(batch, this));
        }

        _selected = null;
        Selected = Rows.FirstOrDefault(r => r.Batch == selected) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Summary));
        RefreshCommands();
    }

    private void ShowFiles()
    {
        Files.Clear();
        if (_selected is { } row)
        {
            foreach (var file in row.Batch.Files)
            {
                Files.Add(new MegaFileRow(file));
            }
        }
    }

    private void RefreshCommands()
    {
        foreach (var command in new IRelayCommand[] { StartCommand, RetryCommand, MoveUpCommand, MoveDownCommand, RemoveCommand, RemoveFileCommand, ClearFinishedCommand, EditCommand, DeleteOriginalsCommand })
        {
            command.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>One batch in the list: what it is, when it starts, how it went.</summary>
public sealed class MegaBatchRow(MegaBatch batch, MegaPageViewModel page) : ObservableObject
{
    private static readonly IReadOnlyList<KeyValuePair<MegaStart, string>> Choices =
    [
        new(MegaStart.Manual, "keď ju spustím"),
        new(MegaStart.AfterPrevious, "po predchádzajúcej dávke"),
        new(MegaStart.At, "v dátum a čas"),
    ];

    /// <summary>The ways a batch starts (an instance property: bindings do not reach static ones).</summary>
    public IReadOnlyList<KeyValuePair<MegaStart, string>> StartChoices => Choices;

    public MegaBatch Batch { get; } = batch;

    public string Label => Batch.Label;

    public string Name => Batch.Name;

    public string Count
    {
        get
        {
            var done = Batch.Files.Count(f => f.State is MegaFileState.Done or MegaFileState.AlreadyInLibrary);
            return Texts.Count(Batch.Files.Count, "súbor", "súbory", "súborov") + (done > 0 ? $" ({done} hotových)" : "");
        }
    }

    public string State => Batch.State switch
    {
        MegaBatchState.Waiting when Batch.IsBeingEdited => "upravuje sa v Hromadnom spracovaní",
        MegaBatchState.Waiting => Batch.Start switch
        {
            MegaStart.At when Batch.StartAt is { } at => "čaká na " + at.ToString("d. M. yyyy H:mm", CultureInfo.GetCultureInfo("sk-SK")),
            MegaStart.AfterPrevious => "čaká na predchádzajúcu",
            _ => "čaká na spustenie",
        },
        MegaBatchState.Queued => "vo Fronte úloh",
        MegaBatchState.Done => "HOTOVO",
        MegaBatchState.DoneWithProblems => "hotovo s problémami",
        MegaBatchState.Cancelled => "zrušené",
        MegaBatchState.Missed => "ZMEŠKANÉ – UNIVERSUM v tom čase nebežalo",
        _ => "",
    };

    public string Outcome => Batch.Outcome;

    /// <summary>What happens with the originals, as chosen in Hromadné spracovanie when the batch was sent.</summary>
    public string Originals => Batch.Originals switch
    {
        OriginalsMode.DeleteAutomatically => "mazať automaticky",
        OriginalsMode.DeleteManually => "mazať ručne",
        _ => "zachovať",
    };

    /// <summary>The start can be changed while the batch waits.</summary>
    public bool CanSchedule => Batch.State is MegaBatchState.Waiting or MegaBatchState.Missed && !Batch.IsBeingEdited;

    public MegaStart Start
    {
        get => Batch.Start;
        set
        {
            if (Batch.Start != value && CanSchedule)
            {
                Batch.Start = value;
                if (value == MegaStart.At && (Batch.StartAt is null || Batch.StartAt <= DateTime.Now))
                {
                    // the coming night at two o'clock as a starting point (never a time already past)
                    Batch.StartAt = DateTime.Today.AddDays(DateTime.Now.Hour >= 2 ? 1 : 0).AddHours(2);
                }

                page.ScheduleChanged(this);
            }
        }
    }

    public bool IsAt => Batch.Start == MegaStart.At;

    public DateTime? Date
    {
        get => Batch.StartAt?.Date;
        set
        {
            if (value is { } date && CanSchedule)
            {
                Batch.StartAt = date.Date + (Batch.StartAt?.TimeOfDay ?? TimeSpan.FromHours(2));
                page.ScheduleChanged(this);
            }
        }
    }

    /// <summary>"2:00" – hours and minutes.</summary>
    public string Time
    {
        get => Batch.StartAt is { } at ? at.ToString("H:mm", CultureInfo.InvariantCulture) : "";
        set
        {
            if (!CanSchedule)
            {
                return;
            }

            if (TimeSpan.TryParseExact(value.Trim(), ["h\\:mm", "hh\\:mm", "h\\.mm", "hh\\.mm"], CultureInfo.InvariantCulture, out var time) &&
                time < TimeSpan.FromDays(1))
            {
                Batch.StartAt = (Batch.StartAt?.Date ?? DateTime.Today) + time;
                page.ScheduleChanged(this);
            }
            else
            {
                // the box shows the time that holds again
                page.InvalidTime(this, value);
            }
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One file of the selected batch.</summary>
public sealed class MegaFileRow(MegaFile file) : ObservableObject
{
    public MegaFile File { get; } = file;

    public string Name => Path.GetFileName(File.Path);

    public string Folder => Path.GetDirectoryName(File.Path) ?? "";

    public string Target => File.PlannedPath;

    public string State => File.State switch
    {
        MegaFileState.Waiting => "čaká",
        MegaFileState.Done => "hotovo",
        MegaFileState.AlreadyInLibrary => "už v knižnici",
        MegaFileState.Skipped => "PRESKOČENÝ",
        MegaFileState.Failed => "CHYBA",
        MegaFileState.Cancelled => "zrušený",
        _ => "",
    };

    public string Note => File.Note;

    public void Refresh() => OnPropertyChanged(string.Empty);
}
