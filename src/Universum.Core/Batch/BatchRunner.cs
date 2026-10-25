using Universum.Core.Execution;
using Universum.Core.IO;

namespace Universum.Core.Batch;

/// <summary>Progress of a running batch.</summary>
/// <param name="FileFraction">Progress of the current file (0–1).</param>
/// <param name="Result">Set when the file has finished.</param>
public sealed record BatchProgress(BatchItem Item, double FileFraction, ProcessingResult? Result, int Finished, int Total);

/// <summary>
/// Processes the files of a simulation that need work, into the library. Files whose source or target drives meet are
/// written one after another, others in parallel (specification, section 12): one disk is never overloaded by
/// parallel writes. With one output drive this means one file at a time.
/// </summary>
public static class BatchRunner
{
    public static async Task<IReadOnlyList<ProcessingResult>> RunAsync(
        IReadOnlyList<BatchItem> items,
        FileProcessor processor,
        OutputOptions output,
        IProgress<BatchProgress>? progress = null,
        BatchControl? control = null,
        CancellationToken cancellationToken = default)
    {
        control ??= new BatchControl();
        var work = items.Where(i => i.NeedsWork).ToList();
        var results = new List<ProcessingResult>();
        var finished = 0;
        var lanes = Lanes(work);

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
                    ProcessingResult result;
                    try
                    {
                        result = await processor.ProcessAsync(item.Plan, item.Placement!, output, fileProgress, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // one file never stops the batch
                        result = new ProcessingResult(item.File.Path, ProcessingStatus.Failed, item.Plan.Method, null, null, [ex.Message]);
                    }

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

    /// <summary>Groups the items so that no two groups share a drive (source or target); order is kept inside a group.</summary>
    internal static IReadOnlyList<List<BatchItem>> Lanes(IReadOnlyList<BatchItem> items)
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Find(string drive)
        {
            parent.TryAdd(drive, drive);
            while (!string.Equals(parent[drive], drive, StringComparison.OrdinalIgnoreCase))
            {
                drive = parent[drive];
            }

            return drive;
        }

        foreach (var item in items)
        {
            var a = Find(Paths.Volume(item.File.Path));
            var b = Find(Paths.Volume(item.Placement!.Directory));
            if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                parent[a] = b;
            }
        }

        return items.GroupBy(i => Find(Paths.Volume(i.File.Path)), StringComparer.OrdinalIgnoreCase).Select(g => g.ToList()).ToList();
    }

    /// <summary>Reports directly on the calling thread (no posting), for progress inside a lane.</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
