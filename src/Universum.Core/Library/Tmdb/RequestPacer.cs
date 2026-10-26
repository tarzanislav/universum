namespace Universum.Core.Library.Tmdb;

/// <summary>
/// Spaces requests to a web service out: one at a time and at most <see cref="PerSecond"/> per second, however many
/// files ask at once; after "too many requests" everyone waits until the service allows more. Shared by all clients of
/// the process (<see cref="Tmdb"/>), so a second window or a new key does not double the rate.
/// </summary>
public sealed class RequestPacer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _interval;
    private DateTime _nextUtc = DateTime.MinValue;

    public RequestPacer(double perSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);
        PerSecond = perSecond;
        _interval = TimeSpan.FromSeconds(1 / perSecond);
    }

    /// <summary>
    /// The pacer of all TMDB requests: 4 per second. TMDB allows about 40 per second
    /// (https://developer.themoviedb.org/docs/rate-limiting), so UNIVERSUM uses at most a tenth of it.
    /// </summary>
    public static RequestPacer Tmdb { get; } = new(4);

    public double PerSecond { get; }

    /// <summary>Requests started through this pacer (for statistics and tests).</summary>
    public int Started => Volatile.Read(ref _started);

    private int _started;

    /// <summary>Waits for its turn, then runs the request; the next one may start one interval later.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _nextUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _nextUtc = DateTime.UtcNow + _interval;
            Interlocked.Increment(ref _started);
            return await request().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The service asked to slow down: no request starts before <paramref name="delay"/> has passed.</summary>
    public void PauseFor(TimeSpan delay)
    {
        var until = DateTime.UtcNow + delay;
        if (until > _nextUtc)
        {
            _nextUtc = until;
        }
    }
}
