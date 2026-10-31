using Universum.Core.Batch;
using Universum.Core.Execution;

namespace Universum.Core.Jobs;

/// <summary>
/// A run of Hromadné spracovanie as one job of the job queue (owner, 29. 10. 2026: the run is seen in Fronta úloh with
/// its progress and in Výstup úloh with a line for every file). The files are written by the <see cref="BatchRunner"/>
/// exactly as before – the same safe route per file, files on different drives in parallel; pause and "stop after the
/// current file" stay on the batch page (<see cref="Control"/>), Zrušiť in the queue cancels the run.
/// </summary>
public sealed class BatchJob(
    string description, IReadOnlyList<BatchItem> items, FileProcessor processor, OutputOptions output, IProgress<BatchProgress>? progress = null)
    : Job(description), ILoggedJob
{
    private readonly List<LoggedFile> _logged = [];
    private IReadOnlyList<ProcessingResult> _results = [];

    public string LogKind => "Hromadné spracovanie";

    public IReadOnlyList<LoggedFile> LoggedFiles
    {
        get
        {
            lock (_logged)
            {
                return [.. _logged];
            }
        }
    }

    /// <summary>Pause, resume and stop after the current file (the batch page's buttons).</summary>
    public BatchControl Control { get; } = new();

    /// <summary>The results of the run (empty before it).</summary>
    public IReadOnlyList<ProcessingResult> Results => _results;

    /// <summary>The files the run writes (MEGA spracovanie never starts them in another job meanwhile).</summary>
    public IReadOnlyList<string> Paths { get; } = items.Where(i => i.NeedsWork).Select(i => i.File.Path).ToList();

    /// <summary>The number of files the run writes.</summary>
    public int FileCount { get; } = items.Count(i => i.NeedsWork);

    protected override async Task<JobStatus> ExecuteAsync(IJobContext context, CancellationToken cancellationToken)
    {
        context.Log(JobSeverity.Info, $"Hromadné spracovanie: {FileCount} súborov do knižnice.");
        _results = await BatchRunner.RunAsync(items, processor, output, new Reporter(context, progress, Log), Control, cancellationToken).ConfigureAwait(false);

        var done = _results.Count(r => r.Status == ProcessingStatus.Done);
        var present = _results.Count(r => r.Status == ProcessingStatus.AlreadyInLibrary);
        var failed = _results.Count(r => r.Status == ProcessingStatus.Failed);
        var cancelled = cancellationToken.IsCancellationRequested || _results.Any(r => r.Status == ProcessingStatus.Cancelled);
        Outcome = $"{done} z {FileCount} súborov v knižnici" + (present > 0 ? $", {present} tam už bolo" : "") +
                  (failed > 0 ? $", {failed} s chybou" : "") +
                  (cancelled ? " – zrušené." : Control.StopAfterCurrent ? " – zastavené na požiadanie." : ".");
        return cancelled ? JobStatus.Cancelled
            : failed > 0 && done + present == 0 ? JobStatus.Failed
            : failed > 0 ? JobStatus.DoneWithWarnings
            : JobStatus.Done;
    }

    /// <summary>A finished run is not repeated from the queue: the batch page analyses the folder again.</summary>
    internal override bool Reset() => false;

    /// <summary>One line of the output per finished file: what was done and where it went.</summary>
    internal static (JobSeverity Severity, string Text) Line(BatchItem item, ProcessingResult result)
    {
        var name = Path.GetFileName(item.File.Path);
        return result.Status switch
        {
            ProcessingStatus.Done when result.Messages.FirstOrDefault(m => m.StartsWith(FileProcessor.OriginalKeptPrefix, StringComparison.Ordinal)) is { } kept =>
                (JobSeverity.Warning, $"OK: {name} → {result.OutputPath} · originál ostal ({kept[FileProcessor.OriginalKeptPrefix.Length..]})"),
            ProcessingStatus.Done => (JobSeverity.Info, $"OK: {name} → {result.OutputPath}" +
                                                         (result.Messages.Contains(FileProcessor.OriginalRecycledMessage) ? " · originál v Koši" : "")),
            ProcessingStatus.AlreadyInLibrary => (JobSeverity.Info, $"Už v knižnici: {name} ({result.OutputPath})"),
            ProcessingStatus.Unchanged => (JobSeverity.Info, $"Bez zmien: {name}"),
            ProcessingStatus.Cancelled => (JobSeverity.Warning, $"Zrušené: {name} – originál ostal nezmenený"),
            ProcessingStatus.NeedsReview => (JobSeverity.Warning, $"Na kontrolu: {name}"),
            _ => (JobSeverity.Error, $"CHYBA: {name}" + (result.Messages.Count > 0 ? " – " + string.Join(" | ", result.Messages) : "")),
        };
    }

    // a finished file into the log of processing (files on different drives finish in parallel)
    private void Log(BatchItem item, ProcessingResult result)
    {
        var file = ProcessingWords.Describe(item.File.Path, item.Plan, result);
        lock (_logged)
        {
            _logged.Add(file);
        }
    }

    // job progress and output, the batch page's own progress and the log
    private sealed class Reporter(IJobContext context, IProgress<BatchProgress>? page, Action<BatchItem, ProcessingResult> log) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value)
        {
            context.Report(value.Total == 0 ? 0 : (value.Finished + (value.Result is null ? value.FileFraction : 0)) / value.Total);
            if (value.Result is { } result)
            {
                var (severity, text) = Line(value.Item, result);
                context.Log(severity, $"{value.Finished} / {value.Total} · {text}");
                log(value.Item, result);
            }

            page?.Report(value);
        }
    }
}
