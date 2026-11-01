using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.Jobs;
using Universum.App.Localization;

namespace Universum.App.Jobs;

/// <summary>Job output (MKVToolNix GUI: Job output): progress, warnings and errors of one job.</summary>
public sealed class JobOutputPageViewModel : ObservableObject
{
    private readonly JobQueuePageViewModel _queue;
    private JobRow? _job;
    private bool _followRunning = true;

    public JobOutputPageViewModel(JobQueuePageViewModel queue)
    {
        _queue = queue;
        queue.Queue.JobChanged += job => JobRow.OnUi(() =>
        {
            if (_followRunning && job.Status == JobStatus.Running)
            {
                Show(_queue.Rows.FirstOrDefault(r => ReferenceEquals(r.Job, job)), follow: true);
            }
        });
    }

    /// <summary>All jobs to choose from (the queue's rows).</summary>
    public ObservableCollection<JobRow> Jobs => _queue.Rows;

    public JobRow? Job
    {
        get => _job;
        set => Show(value, follow: false);
    }

    public ObservableCollection<JobMessageRow> Messages { get; } = [];

    public string Summary => _job is null
        ? Loc.T("Vyberte úlohu. Počas behu fronty sa tu ukazuje práve spracovaná úloha.")
        : $"{char.ToUpper(_job.Status[0])}{_job.Status[1..]}. {_job.Outcome}".Trim() + (_job.Job.StartedUtc is null ? "" : Loc.F(" Trvanie: {0}.", _job.Duration)) +
          Loc.F(" Upozornenia: {0}, chyby: {1}.", _job.Job.Warnings, _job.Job.Errors);

    public double Progress => _job?.Progress ?? 0;

    public bool IsRunning => _job?.IsRunning == true;

    /// <summary>Shows a job's output; <paramref name="follow"/> keeps following the running job.</summary>
    public void Show(JobRow? job, bool follow)
    {
        if (_job is not null)
        {
            _job.PropertyChanged -= OnJobChanged;
        }

        _followRunning = follow || job is null || job.IsRunning;
        SetProperty(ref _job, job, nameof(Job));
        Messages.Clear();
        if (_job is not null)
        {
            _job.PropertyChanged += OnJobChanged;
        }

        Refresh();
    }

    private void OnJobChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var messages = _job?.Job.Messages ?? [];
        if (messages.Count < Messages.Count)
        {
            Messages.Clear();
        }

        foreach (var message in messages.Skip(Messages.Count))
        {
            Messages.Add(JobMessageRow.Of(message));
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsRunning));
    }
}
