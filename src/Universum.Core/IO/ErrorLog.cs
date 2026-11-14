using System.Globalization;
using System.Text;

namespace Universum.Core.IO;

/// <summary>
/// The log of unexpected errors (14. 11. 2026, step 1 of the review of the code): an error of work done in the background
/// that nobody waits for is written here instead of being lost. One file on this computer only, a block per error – when,
/// where and what – kept small: past the limit the older half goes. A cancellation is no error.
/// </summary>
public sealed class ErrorLog(string file, long limit = 512 * 1024)
{
    private readonly Lock _gate = new();

    public string File { get; } = file;

    /// <summary>The errors inside an error (an <see cref="AggregateException"/> flattened); none for a cancellation.</summary>
    public static IReadOnlyList<Exception> Errors(Exception error) =>
        error switch
        {
            AggregateException many => [.. many.Flatten().InnerExceptions.SelectMany(Errors)],
            OperationCanceledException => [],
            _ => [error],
        };

    /// <summary>Writes the errors of <paramref name="error"/> made in <paramref name="where"/>; the errors written.</summary>
    public IReadOnlyList<Exception> Write(string where, Exception error, DateTimeOffset? now = null)
    {
        var errors = Errors(error);
        if (errors.Count == 0)
        {
            return errors;
        }

        var text = new StringBuilder();
        foreach (var one in errors)
        {
            var detail = one.ToString();
            text.Append(CultureInfo.InvariantCulture, $"{(now ?? DateTimeOffset.Now):yyyy-MM-dd HH:mm:ss zzz} · {where}\n")
                .Append(detail.Length > 4000 ? detail[..4000] + " …" : detail)
                .Append("\n\n");
        }

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
                System.IO.File.AppendAllText(File, text.ToString(), Encoding.UTF8);
                Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the log cannot be written now: the error is still told to the caller
        }

        return errors;
    }

    // past the limit the newer half stays, from the start of a block
    private void Trim()
    {
        var info = new FileInfo(File);
        if (info.Length <= limit)
        {
            return;
        }

        var text = System.IO.File.ReadAllText(File, Encoding.UTF8);
        var keep = text[(text.Length / 2)..];
        var block = keep.IndexOf("\n\n", StringComparison.Ordinal);
        System.IO.File.WriteAllText(File, block >= 0 ? keep[(block + 2)..] : keep, Encoding.UTF8);
    }
}
