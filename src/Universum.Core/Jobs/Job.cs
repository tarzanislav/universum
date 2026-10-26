namespace Universum.Core.Jobs;

public enum JobStatus
{
    Waiting,
    Running,
    Done,

    /// <summary>Finished, but the tools reported warnings worth reading.</summary>
    DoneWithWarnings,

    Failed,
    Cancelled,
}

public enum JobSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One line of a job's output.</summary>
public sealed record JobMessage(DateTime TimeUtc, JobSeverity Severity, string Text);

/// <summary>What a running job reports: progress (0–1) and messages.</summary>
public interface IJobContext
{
    void Report(double progress);

    void Log(JobSeverity severity, string text);
}

/// <summary>
/// A unit of work in the job queue (MKVToolNix GUI: job queue and job output): a description, status, progress, its
/// output and the times it started and finished. Subclasses do the work in <see cref="ExecuteAsync"/> and return the
/// final status; an exception fails the job with its message.
/// </summary>
public abstract class Job
{
    private readonly List<JobMessage> _messages = [];
    private static int _nextId;

    protected Job(string description)
    {
        Description = description;
        Id = Interlocked.Increment(ref _nextId);
    }

    public int Id { get; }

    public string Description { get; }

    public JobStatus Status { get; private set; } = JobStatus.Waiting;

    public double Progress { get; private set; }

    public DateTime AddedUtc { get; } = DateTime.UtcNow;

    public DateTime? StartedUtc { get; private set; }

    public DateTime? FinishedUtc { get; private set; }

    /// <summary>One sentence about the outcome, shown in the queue.</summary>
    public string Outcome { get; protected set; } = "";

    public IReadOnlyList<JobMessage> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    public int Warnings => Messages.Count(m => m.Severity == JobSeverity.Warning);

    public int Errors => Messages.Count(m => m.Severity == JobSeverity.Error);

    public bool IsFinished => Status is JobStatus.Done or JobStatus.DoneWithWarnings or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>Raised when the status, progress or output change (on the thread that changed them).</summary>
    public event Action<Job>? Changed;

    /// <summary>Runs the job once; the queue calls it.</summary>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        StartedUtc = DateTime.UtcNow;
        Status = JobStatus.Running;
        Changed?.Invoke(this);
        var context = new Context(this);
        JobStatus status;
        try
        {
            status = await ExecuteAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            status = JobStatus.Cancelled;
        }
        catch (Exception ex)
        {
            AddMessage(JobSeverity.Error, ex.Message);
            status = JobStatus.Failed;
        }

        if (status == JobStatus.Cancelled && Outcome.Length == 0)
        {
            Outcome = "Zrušené.";
        }

        if (status == JobStatus.Done && Warnings > 0)
        {
            status = JobStatus.DoneWithWarnings;
        }

        Progress = status is JobStatus.Done or JobStatus.DoneWithWarnings ? 1 : Progress;
        Status = status;
        FinishedUtc = DateTime.UtcNow;
        Changed?.Invoke(this);
    }

    /// <summary>A waiting job taken out of the queue run is marked cancelled.</summary>
    internal void MarkCancelled()
    {
        Status = JobStatus.Cancelled;
        Outcome = "Zrušené pred spustením.";
        FinishedUtc = DateTime.UtcNow;
        Changed?.Invoke(this);
    }

    /// <summary>A finished job is put back to wait for another run.</summary>
    internal virtual bool Reset()
    {
        if (!IsFinished)
        {
            return false;
        }

        lock (_messages)
        {
            _messages.Clear();
        }

        Status = JobStatus.Waiting;
        Progress = 0;
        Outcome = "";
        StartedUtc = FinishedUtc = null;
        Changed?.Invoke(this);
        return true;
    }

    protected abstract Task<JobStatus> ExecuteAsync(IJobContext context, CancellationToken cancellationToken);

    private void AddMessage(JobSeverity severity, string text)
    {
        lock (_messages)
        {
            _messages.Add(new JobMessage(DateTime.UtcNow, severity, text));
        }

        Changed?.Invoke(this);
    }

    private sealed class Context(Job job) : IJobContext
    {
        public void Report(double progress)
        {
            job.Progress = Math.Clamp(progress, 0, 1);
            job.Changed?.Invoke(job);
        }

        public void Log(JobSeverity severity, string text) => job.AddMessage(severity, text);
    }
}
