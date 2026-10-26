namespace Universum.Core.Jobs;

/// <summary>
/// The job queue: jobs run one after another in their order while the queue is started. A running job can be
/// cancelled; waiting jobs can be moved, cancelled or removed; finished jobs stay for their output until cleared.
/// Events are raised on the thread that runs the queue (the UI thread when it is started from there).
/// </summary>
public sealed class JobQueue
{
    private readonly List<Job> _jobs = [];
    private CancellationTokenSource? _current;
    private Job? _running;
    private bool _started;
    private Task _loop = Task.CompletedTask;

    public IReadOnlyList<Job> Jobs
    {
        get
        {
            lock (_jobs)
            {
                return _jobs.ToList();
            }
        }
    }

    /// <summary>True while the queue takes waiting jobs one after another.</summary>
    public bool IsStarted => _started;

    public Job? Running => _running;

    public int WaitingCount => Jobs.Count(j => j.Status == JobStatus.Waiting);

    /// <summary>Raised when jobs are added, removed, moved or change state.</summary>
    public event Action? Changed;

    /// <summary>Raised when a job starts or finishes.</summary>
    public event Action<Job>? JobChanged;

    /// <summary>Adds a job at the end; with <paramref name="start"/> the queue starts if it is not running.</summary>
    public void Add(Job job, bool start = false)
    {
        lock (_jobs)
        {
            _jobs.Add(job);
        }

        job.Changed += OnJobChanged;
        Changed?.Invoke();
        if (start)
        {
            Start();
        }
    }

    /// <summary>Starts taking waiting jobs; returns the run (completes when nothing waits or the queue is stopped).</summary>
    public Task Start()
    {
        if (_started)
        {
            return _loop;
        }

        _started = true;
        Changed?.Invoke();
        _loop = RunAsync();
        return _loop;
    }

    /// <summary>Stops after the running job (which finishes normally).</summary>
    public void Stop()
    {
        _started = false;
        Changed?.Invoke();
    }

    /// <summary>Cancels a running job or takes a waiting one out of the run.</summary>
    public void Cancel(Job job)
    {
        if (ReferenceEquals(job, _running))
        {
            _current?.Cancel();
        }
        else if (job.Status == JobStatus.Waiting)
        {
            job.MarkCancelled();
        }
    }

    /// <summary>Removes a job that is not running.</summary>
    public bool Remove(Job job)
    {
        if (ReferenceEquals(job, _running))
        {
            return false;
        }

        lock (_jobs)
        {
            if (!_jobs.Remove(job))
            {
                return false;
            }
        }

        job.Changed -= OnJobChanged;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes all finished jobs.</summary>
    public void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.IsFinished))
        {
            Remove(job);
        }
    }

    /// <summary>Puts a finished job back to wait (run it again).</summary>
    public bool Retry(Job job)
    {
        if (!job.Reset())
        {
            return false;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves a waiting job earlier (-1) or later (+1) among the waiting jobs.</summary>
    public bool Move(Job job, int step)
    {
        lock (_jobs)
        {
            var index = _jobs.IndexOf(job);
            if (index < 0 || job.Status != JobStatus.Waiting)
            {
                return false;
            }

            var other = index + step;
            while (other >= 0 && other < _jobs.Count && _jobs[other].Status != JobStatus.Waiting)
            {
                other += step;
            }

            if (other < 0 || other >= _jobs.Count)
            {
                return false;
            }

            (_jobs[index], _jobs[other]) = (_jobs[other], _jobs[index]);
        }

        Changed?.Invoke();
        return true;
    }

    private async Task RunAsync()
    {
        try
        {
            while (_started && Next() is { } job)
            {
                using var cancellation = new CancellationTokenSource();
                _current = cancellation;
                _running = job;
                Changed?.Invoke();
                JobChanged?.Invoke(job);
                await job.RunAsync(cancellation.Token);
                _running = null;
                _current = null;
                JobChanged?.Invoke(job);
                Changed?.Invoke();
            }
        }
        finally
        {
            _running = null;
            _started = false;
            Changed?.Invoke();
        }
    }

    private Job? Next()
    {
        lock (_jobs)
        {
            return _jobs.FirstOrDefault(j => j.Status == JobStatus.Waiting);
        }
    }

    private void OnJobChanged(Job job) => Changed?.Invoke();
}
