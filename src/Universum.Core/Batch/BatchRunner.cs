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

        // owner, 2. 11. 2026: at most so many disks at the same time (one file per disk); the other lanes wait their turn
        using var disks = new SemaphoreSlim(Math.Max(1, processor.ParallelDisks));
        await Task.WhenAll(lanes.Select(lane => Task.Run(
            async () =>
            {
                try
                {
                    await disks.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await RunLaneAsync(lane).ConfigureAwait(false);
                }
                finally
                {
                    disks.Release();
                }
            },
            CancellationToken.None))).ConfigureAwait(false);

        return results;

        async Task RunLaneAsync(List<BatchItem> lane)
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
        }
    }

    /// <summary>
    /// Groups the items so that no two groups share a physical disk (source or target; owner, 2. 11. 2026: two partitions of
    /// one disk are one disk, a volume over several disks is one); order is kept inside a group. A group only on
    /// solid-state disks (SSD, NVMe – no seek penalty) is split into two lanes: such a disk writes two files at once
    /// without slowing down, a spinning one would seek between them.
    /// </summary>
    internal static IReadOnlyList<List<BatchItem>> Lanes(IReadOnlyList<BatchItem> items)
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var solid = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        string DiskOf(string path)
        {
            var disk = Paths.Disk(path);
            solid[disk.Key] = disk.SolidState;
            return disk.Key;
        }

        string Find(string drive)
        {
            parent.TryAdd(drive, drive);
            while (!string.Equals(parent[drive], drive, StringComparison.OrdinalIgnoreCase))
            {
                drive = parent[drive];
            }

            return drive;
        }

        var disks = items.Select(i => (Item: i, Source: DiskOf(i.File.Path), Target: DiskOf(i.Placement!.Directory))).ToList();
        foreach (var (_, source, target) in disks)
        {
            var a = Find(source);
            var b = Find(target);
            if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                parent[a] = b;
            }
        }

        var lanes = new List<List<BatchItem>>();
        foreach (var group in disks.GroupBy(d => Find(d.Source), StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            var allSolid = parent.Keys.Where(k => string.Equals(Find(k), group.Key, StringComparison.OrdinalIgnoreCase)).All(k => solid.GetValueOrDefault(k));
            if (allSolid && members.Count > 1)
            {
                lanes.Add(members.Where((_, i) => i % 2 == 0).Select(m => m.Item).ToList());
                lanes.Add(members.Where((_, i) => i % 2 == 1).Select(m => m.Item).ToList());
            }
            else
            {
                lanes.Add(members.Select(m => m.Item).ToList());
            }
        }

        return lanes;
    }

    /// <summary>Reports directly on the calling thread (no posting), for progress inside a lane.</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
