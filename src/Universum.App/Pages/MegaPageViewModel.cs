using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Jobs;
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
        RemoveCommand = new RelayCommand(Remove, () => _selected?.Batch is { State: not MegaBatchState.Queued });
        RemoveFileCommand = new RelayCommand(RemoveFile, () => _selected?.Batch is { State: MegaBatchState.Waiting or MegaBatchState.Missed } && _selectedFile?.File.State == MegaFileState.Waiting);
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => _services.Mega.Batches.Any(b => !b.IsOpen));
        services.MegaChanged += Load;
        Load();
        _message = services.MegaProblem;

        if (schedule)
        {
            // a time missed while UNIVERSUM was not running is not caught up without the user
            foreach (var due in _services.Mega.Due(DateTime.Now, started: false))
            {
                _ = StartAsync(due);
            }

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

        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Message = $"{batch.Label} sa nespustila: MKVToolNix nie je k dispozícii.";
            return;
        }

        var inspector = _services.Inspector(installation);
        var job = new MegaJob(batch, _services.Processor(installation), (path, token) => inspector.InspectAsync(path, token), b => JobRow.OnUi(() => Saved(b)));
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

    /// <summary>A batch whose time has come is started (called every 30 seconds).</summary>
    public void Tick()
    {
        foreach (var due in _services.Mega.Due(DateTime.Now, started: true))
        {
            _ = StartAsync(due);
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

        Save();
        row.Refresh();
        OnPropertyChanged(nameof(Summary));
        RefreshCommands();
    }

    private void Finished(MegaJob job)
    {
        var batch = job.Batch;
        batch.State = job.Status switch
        {
            JobStatus.Done => MegaBatchState.Done,
            JobStatus.Cancelled => MegaBatchState.Cancelled,
            _ => MegaBatchState.DoneWithProblems,
        };
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

        foreach (var file in batch.Files.Where(f => f.State is MegaFileState.Skipped or MegaFileState.Failed or MegaFileState.Cancelled))
        {
            file.State = MegaFileState.Waiting;
            file.Note = "";
        }

        batch.State = MegaBatchState.Waiting;
        batch.Start = MegaStart.Manual;
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
        foreach (var command in new IRelayCommand[] { StartCommand, RetryCommand, MoveUpCommand, MoveDownCommand, RemoveCommand, RemoveFileCommand, ClearFinishedCommand })
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

    /// <summary>The start can be changed while the batch waits.</summary>
    public bool CanSchedule => Batch.State is MegaBatchState.Waiting or MegaBatchState.Missed;

    public MegaStart Start
    {
        get => Batch.Start;
        set
        {
            if (Batch.Start != value && CanSchedule)
            {
                Batch.Start = value;
                if (value == MegaStart.At && Batch.StartAt is null)
                {
                    // the coming night at two o'clock as a starting point
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
            if (CanSchedule && TimeSpan.TryParseExact(value.Trim(), ["h\\:mm", "hh\\:mm", "h\\.mm", "hh\\.mm"], CultureInfo.InvariantCulture, out var time) &&
                time < TimeSpan.FromDays(1))
            {
                Batch.StartAt = (Batch.StartAt?.Date ?? DateTime.Today) + time;
                page.ScheduleChanged(this);
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
