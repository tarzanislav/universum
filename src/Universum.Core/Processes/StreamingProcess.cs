using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Universum.Core.Processes;

/// <summary>
/// A tool whose standard output is read as a stream while it runs – FFmpeg decoding pictures and sound for the player.
/// No shell, no window; the last part of its error output is kept for messages; disposing ends it (and what it started).
/// </summary>
public sealed class StreamingProcess : IAsyncDisposable
{
    private const int ErrorKept = 4096;

    // the tools running now: ended with UNIVERSUM, never left decoding on their own
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<StreamingProcess, byte> Running = new();

    static StreamingProcess()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var running in Running.Keys)
            {
                running.Kill();
            }
        };
    }

    private readonly Process _process;
    private readonly StringBuilder _error = new();
    private readonly Task _readingError;
    private int _disposed;

    private StreamingProcess(Process process)
    {
        _process = process;
        _readingError = ReadErrorAsync();
        Running.TryAdd(this, 0);
    }

    /// <summary>The standard output of the tool (binary).</summary>
    public Stream Output => _process.StandardOutput.BaseStream;

    /// <summary>The last part of the error output (FFmpeg: why it stopped).</summary>
    public string ErrorText
    {
        get
        {
            lock (_error)
            {
                return _error.ToString().Trim();
            }
        }
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Starts <paramref name="fileName"/>; <see cref="ProcessStartException"/> when it cannot be started.</summary>
    public static StreamingProcess Start(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            process.Dispose();
            throw new ProcessStartException(fileName, ex);
        }

        return new StreamingProcess(process);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Running.TryRemove(this, out _);
        try
        {
            Kill();
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            await _readingError.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or OperationCanceledException or IOException)
        {
            // ended meanwhile, or it does not end: nothing more to do here
        }
        finally
        {
            _process.Dispose();
        }
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // ended meanwhile
        }
    }

    private async Task ReadErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_error)
                {
                    _error.AppendLine(line);
                    if (_error.Length > ErrorKept * 2)
                    {
                        _error.Remove(0, _error.Length - ErrorKept);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // the process was ended
        }
    }
}
