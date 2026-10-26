using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Jobs;

namespace Universum.App.Jobs;

/// <summary>Job queue (MKVToolNix GUI: Job queue): jobs added by the other tools run one after another.</summary>
public sealed class JobQueuePageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private JobRow? _selected;
    private string _message = "";

    public JobQueuePageViewModel(AppServices services)
    {
        _services = services;
        StartCommand = new RelayCommand(() => _ = Queue.Start(), () => !Queue.IsStarted && Queue.WaitingCount > 0);
        StopCommand = new RelayCommand(Queue.Stop, () => Queue.IsStarted);
        CancelCommand = new RelayCommand(() => Queue.Cancel(_selected!.Job), () => _selected is { Job.Status: JobStatus.Waiting or JobStatus.Running });
        RemoveCommand = new RelayCommand(() => Queue.Remove(_selected!.Job), () => _selected is { IsRunning: false });
        ClearCommand = new RelayCommand(Queue.ClearFinished, () => Queue.Jobs.Any(j => j.IsFinished));
        RetryCommand = new RelayCommand(() => Queue.Retry(_selected!.Job), () => _selected is { Job.Status: JobStatus.Failed or JobStatus.Cancelled });
        MoveUpCommand = new RelayCommand(() => Queue.Move(_selected!.Job, -1), () => _selected is { Job.Status: JobStatus.Waiting });
        MoveDownCommand = new RelayCommand(() => Queue.Move(_selected!.Job, +1), () => _selected is { Job.Status: JobStatus.Waiting });
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => _selected?.Job is FileJob { Result.UndoRecordPath: not null, IsFinished: true });
        OutputCommand = new RelayCommand(() => OutputRequested?.Invoke(_selected!), () => _selected is not null);
        Queue.Changed += () => JobRow.OnUi(Sync);
        Sync();
    }

    public JobQueue Queue => _services.Jobs;

    public ObservableCollection<JobRow> Rows { get; } = [];

    public JobRow? SelectedRow
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

    public RelayCommand StartCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand RetryCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public AsyncRelayCommand UndoCommand { get; }

    public RelayCommand OutputCommand { get; }

    /// <summary>Asks the main window to show the output of a job.</summary>
    public event Action<JobRow>? OutputRequested;

    public bool AutoStart
    {
        get => _services.Settings.AutoStartQueue;
        set
        {
            _services.Settings.AutoStartQueue = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>"Beží 1, čakajú 2" and the like.</summary>
    public string State
    {
        get
        {
            var jobs = Queue.Jobs;
            if (jobs.Count == 0)
            {
                return "Fronta je prázdna. Úlohy pridáte tlačidlom Do fronty v Úprave hlavičky alebo v Pravidlách UNIVERSUM.";
            }

            var waiting = jobs.Count(j => j.Status == JobStatus.Waiting);
            var failed = jobs.Count(j => j.Status == JobStatus.Failed);
            var done = jobs.Count(j => j.Status is JobStatus.Done or JobStatus.DoneWithWarnings);
            return (Queue.Running is { } running ? $"Beží: {running.Description}. " : Queue.IsStarted ? "" : "Fronta je zastavená. ") +
                   $"Čaká: {waiting}, hotovo: {done}" + (failed > 0 ? $", s chybou: {failed}" : "") + ".";
        }
    }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>Short text for the navigation ("2 čakajú"); empty when nothing waits or runs.</summary>
    public string Badge => Queue.Running is not null ? $"beží, čaká {Queue.WaitingCount}" : Queue.WaitingCount > 0 ? $"čaká {Queue.WaitingCount}" : "";

    private void Sync()
    {
        var jobs = Queue.Jobs;
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!jobs.Contains(Rows[i].Job))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < jobs.Count; i++)
        {
            var existing = Rows.FirstOrDefault(r => ReferenceEquals(r.Job, jobs[i]));
            if (existing is null)
            {
                Rows.Insert(i, new JobRow(jobs[i]));
            }
            else if (Rows.IndexOf(existing) != i)
            {
                Rows.Move(Rows.IndexOf(existing), i);
            }
        }

        if (_selected is not null && !Rows.Contains(_selected))
        {
            SelectedRow = null;
        }

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Badge));
        RefreshCommands();
    }

    private async Task UndoAsync()
    {
        if (_selected?.Job is not FileJob { Result.UndoRecordPath: { } record } job || _services.CurrentMkvToolNix is not { } installation)
        {
            return;
        }

        var problems = await _services.Processor(installation).UndoAsync(record);
        _services.Inspector(installation).Invalidate(job.Plan.Target.Source.Path);
        Message = problems.Count == 0
            ? $"Úloha {job.Id} je vrátená späť – pôvodná hlavička je obnovená."
            : "Vrátenie sa nepodarilo:\n" + string.Join("\n", problems);
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        OutputCommand.NotifyCanExecuteChanged();
    }
}
