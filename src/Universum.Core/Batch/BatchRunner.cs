using Universum.Core.Execution;

namespace Universum.Core.Batch;

/// <summary>Progress of a running batch.</summary>
/// <param name="FileFraction">Progress of the current file (0–1).</param>
/// <param name="Result">Set when the file has finished.</param>
public sealed record BatchProgress(BatchItem Item, double FileFraction, ProcessingResult? Result, int Finished, int Total);

/// <summary>
/// Processes the files of a simulation that need work. Files on the same drive are written one after another,
/// different drives in parallel (specification, section 12): one disk is never overloaded by parallel writes.
/// </summary>
public static class BatchRunner
{
    public static async Task<IReadOnlyList<ProcessingResult>> RunAsync(
        IReadOnlyList<BatchItem> items,
        FileProcessor processor,
        Func<BatchItem, OutputOptions> output,
        IProgress<BatchProgress>? progress = null,
        BatchControl? control = null,
        CancellationToken cancellationToken = default)
    {
        control ??= new BatchControl();
        var work = items.Where(i => i.NeedsWork).ToList();
        var results = new List<ProcessingResult>();
        var finished = 0;
        var lanes = work.GroupBy(i => DriveOf(i.File.Path), StringComparer.OrdinalIgnoreCase);

        await Task.WhenAll(lanes.Select(lane => Task.Run(
            async () =>
            {
                foreach (var item in lane)
                {
                    try
                    {
                        await control.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (control.StopAfterCurrent || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var fileProgress = progress is null ? null : new InlineProgress(f => progress.Report(new BatchProgress(item, f, null, Volatile.Read(ref finished), work.Count)));
                    var result = await processor.ProcessAsync(item.Plan, output(item), fileProgress, cancellationToken).ConfigureAwait(false);
                    lock (results)
                    {
                        results.Add(result);
                    }

                    progress?.Report(new BatchProgress(item, 1, result, Interlocked.Increment(ref finished), work.Count));
                    if (result.Status == ProcessingStatus.Cancelled)
                    {
                        return;
                    }
                }
            },
            CancellationToken.None))).ConfigureAwait(false);

        return results;
    }

    /// <summary>Output options for one file: a target folder keeps the folder's subfolder structure.</summary>
    public static OutputOptions OutputFor(OutputOptions options, string rootFolder, string filePath)
    {
        if (options.TargetDirectory is null)
        {
            return options;
        }

        var relative = Path.GetRelativePath(rootFolder, Path.GetDirectoryName(filePath)!);
        return relative is "." || relative.StartsWith("..", StringComparison.Ordinal)
            ? options
            : options with { TargetDirectory = Path.Combine(options.TargetDirectory, relative) };
    }

    private static string DriveOf(string path) => Path.GetPathRoot(Path.GetFullPath(path)) ?? "";

    /// <summary>Reports directly on the calling thread (no posting), for progress inside a lane.</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
