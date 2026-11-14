using System.Runtime.CompilerServices;
using Universum.App.Localization;
using Universum.Core.IO;

namespace Universum.App;

/// <summary>
/// Work started in the background that nobody waits for (14. 11. 2026, step 1 of the review of the code): an error of it
/// was lost before; now it is written to the log of errors (chyby.log beside the journal) and told by a short notice in
/// the corner of the screen. A cancellation is no error.
/// </summary>
public static class BackgroundWork
{
    /// <summary>The log of errors (the self-test puts its own here).</summary>
    public static ErrorLog Log { get; set; } = new(AppSettings.ErrorLogFile);

    /// <summary>Raised with the text of an error told (on the thread of the error).</summary>
    public static event Action<string>? Failed;

    /// <summary>Lets the task run; its error is written and told when it fails.</summary>
    public static void Run(Task task, [CallerArgumentExpression(nameof(task))] string what = "")
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        task.ContinueWith(done => Report(done.Exception!, what), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>An error nobody else catches: written to the log and told (nothing for a cancellation).</summary>
    public static void Report(Exception error, string where)
    {
        var errors = Log.Write(where, error);
        if (errors.Count == 0)
        {
            return;
        }

        var text = Loc.F("Práca na pozadí zlyhala: {0} (podrobnosti v chyby.log)", errors[0].Message);
        Failed?.Invoke(text);
        Shell.TrayToast.Show(text);
    }
}
