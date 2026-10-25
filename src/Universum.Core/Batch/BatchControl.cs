namespace Universum.Core.Batch;

/// <summary>Pause, resume and "stop after the current file" for a running batch.</summary>
public sealed class BatchControl
{
    private readonly object _lock = new();
    private TaskCompletionSource _resumed = CompletedSource();

    /// <summary>When set, no further file is started; files in progress finish normally.</summary>
    public bool StopAfterCurrent { get; set; }

    public bool IsPaused { get; private set; }

    public void Pause()
    {
        lock (_lock)
        {
            if (!IsPaused)
            {
                IsPaused = true;
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            IsPaused = false;
            _resumed.TrySetResult();
        }
    }

    /// <summary>Waits while paused (between files).</summary>
    public Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _resumed.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }
}
