using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Universum.Core.Processes;

/// <summary>Runs external tools without a shell and without a console window, reading output as UTF-8.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInputFile is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        if (!OperatingSystem.IsWindows() && !IsUtf8Locale())
        {
            // tools decide how to decode file names from the locale; force UTF-8 so non-ASCII names work
            startInfo.Environment["LC_ALL"] = "C.UTF-8";
        }

        if (request.Environment is not null)
        {
            foreach (var (name, value) in request.Environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new ProcessStartException(request.FileName, ex);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } timeout)
        {
            limit.CancelAfter(timeout);
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        var readOutput = ReadLinesAsync(process.StandardOutput, output, request.OnOutputLine);
        var readError = ReadLinesAsync(process.StandardError, error, null);
        var writeInput = request.StandardInputFile is { } input ? WriteInputAsync(process, input, limit.Token) : Task.CompletedTask;
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await Task.WhenAll(readOutput, readError).ConfigureAwait(false);
            await writeInput.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Program '{request.FileName}' did not finish within {request.Timeout}.");
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString(), stopwatch.Elapsed);
    }

    private static bool IsUtf8Locale()
    {
        var locale = System.Environment.GetEnvironmentVariable("LC_ALL") is { Length: > 0 } all
            ? all
            : System.Environment.GetEnvironmentVariable("LC_CTYPE") is { Length: > 0 } ctype
                ? ctype
                : System.Environment.GetEnvironmentVariable("LANG") ?? "";
        return locale.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || locale.Contains("utf8", StringComparison.OrdinalIgnoreCase);
    }

    // the file to the program's standard input; a program that stops reading early (exits) is not an error
    private static async Task WriteInputAsync(Process process, string path, CancellationToken cancellationToken)
    {
        try
        {
            await using (var input = File.OpenRead(path))
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // the program closed its input (it has ended)
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, StringBuilder target, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            target.AppendLine(line);
            onLine?.Invoke(line);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // the process ended on its own in the meantime
        }
    }
}
