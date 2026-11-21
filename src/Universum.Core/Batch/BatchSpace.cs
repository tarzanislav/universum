using Universum.Core.Execution;
using Universum.Core.IO;
using Universum.Core.Planning;

namespace Universum.Core.Batch;

/// <summary>A disk without room for the files a run writes to it.</summary>
public sealed record DiskShortage(string Root, long Needed, long Free);

/// <summary>
/// The room a run needs on each disk it writes to (owner, 21. 11. 2026: checked before it starts, so it does not stop
/// halfway on a full disk). Superrýchle needs almost none – a file with another header only is moved into the library and
/// edited there; any other file is written anew beside its original (an original going to the Recycle Bin frees nothing on
/// its disk). Each disk keeps <see cref="DiskSpace.Reserve"/> free.
/// </summary>
public static class BatchSpace
{
    public static IReadOnlyList<DiskShortage> Shortages(IEnumerable<BatchItem> items, OutputOptions output, Func<string, long?>? free = null)
    {
        free ??= DiskSpace.Free;
        var needed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => i.NeedsWork && i.Placement is not null))
        {
            if (DiskSpace.Root(item.Placement!.Directory) is { } root)
            {
                needed[root] = needed.GetValueOrDefault(root) + Needed(item, output);
            }
        }

        return [.. needed.Select(n => (Root: n.Key, Needed: n.Value + DiskSpace.Reserve, Free: free(n.Key)))
            .Where(n => n.Free is { } room && room < n.Needed)
            .Select(n => new DiskShortage(n.Root, n.Needed, n.Free!.Value))];
    }

    /// <summary>What one file needs on the disk of its place in the library.</summary>
    public static long Needed(BatchItem item, OutputOptions output)
    {
        var source = item.File.Path;
        if (item.Plan.Method != WriteMethod.Remux && output.Original == OriginalHandling.Delete && output.MoveIntoLibrary &&
            item.Placement is { } placement && Paths.SameVolume(source, placement.Directory))
        {
            // Superrýchle: the original itself goes into the library, its header grows a little
            return 0;
        }

        var times = item.Plan.Target.Tracks.Any(t => t.Source.VideoEncode is not null) ? 2 : 1;
        var added = item.Plan.Target.Tracks.Where(t => t.IsAdded && t.Source.ExternalFile is not null).Select(t => t.Source.ExternalFile!)
            .Distinct(Paths.Comparer).Sum(Length);
        return (Length(source) * times) + added;
    }

    private static long Length(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }
}
