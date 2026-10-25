namespace Universum.Core.Processes;

public interface IProcessRunner
{
    /// <summary>Runs the program and waits for it. Cancellation kills the whole process tree.</summary>
    /// <exception cref="ProcessStartException">The program could not be started.</exception>
    /// <exception cref="TimeoutException">The program exceeded <see cref="ProcessRequest.Timeout"/>.</exception>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}
