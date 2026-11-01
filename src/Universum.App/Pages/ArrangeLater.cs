using System.Windows.Threading;

namespace Universum.App.Pages;

/// <summary>
/// The order of the tracks follows a change of the user only after a pause (owner, 1. 11. 2026: a Forced box clicked
/// twice, or a language chosen, must not make the track jump away from under the mouse – it is arranged two seconds after
/// the last change). Each new change starts the pause again; an order asked for at once (a track moved by hand) cancels it.
/// </summary>
public sealed class ArrangeLater
{
    private DispatcherTimer? _timer;
    private Action? _arrange;

    /// <summary>The pause (the self-test arranges at once, except where it tries the pause itself).</summary>
    public static TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>An arrangement waits.</summary>
    public bool IsPending => _arrange is not null;

    /// <summary>Arranges after the pause (the pause starts again); without a pause at once.</summary>
    public void Schedule(Action arrange)
    {
        _arrange = arrange;
        if (Delay <= TimeSpan.Zero)
        {
            Now();
            return;
        }

        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Tick += (_, _) => Now();
        }

        _timer.Stop();
        _timer.Interval = Delay;
        _timer.Start();
    }

    /// <summary>The waiting arrangement is not needed (the order was made otherwise).</summary>
    public void Cancel()
    {
        _timer?.Stop();
        _arrange = null;
    }

    /// <summary>The waiting arrangement at once.</summary>
    public void Now()
    {
        _timer?.Stop();
        var arrange = _arrange;
        _arrange = null;
        arrange?.Invoke();
    }
}
