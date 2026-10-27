using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Universum.App.Shell;

/// <summary>
/// One UNIVERSUM per Windows user: a second start (the Explorer's context menu starts one per selected file, or the Start
/// menu) hands its command line to the running one over a named pipe of the current user and ends. The running one
/// shows its window and handles the request.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string MutexName = @"Local\UNIVERSUM-3F7A2C91-instance";
    private static readonly string PipeName = "UNIVERSUM-3F7A2C91-" + Environment.UserName.Replace('\\', '-');
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>This process is the first UNIVERSUM of the user; null when another one runs.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (created)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>Hands the command line to the running UNIVERSUM; false when it does not answer (this one then starts itself).</summary>
    public static bool Forward(IReadOnlyList<string> args, TimeSpan timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args));
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Receives the command lines of later starts (on a background thread) until disposed.</summary>
    public void Listen(Action<string[]> received) => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var buffer = new MemoryStream();
                await pipe.CopyToAsync(buffer, _stop.Token);

                // a command line is a few paths: anything large or unreadable is not one
                if (buffer.Length is > 0 and < 1 << 20 && JsonSerializer.Deserialize<string[]>(buffer.ToArray()) is { } args)
                {
                    received(args);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a broken connection: wait for the next one
            }
        }
    });

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // not owned by this thread any more
        }

        _mutex.Dispose();
    }
}
