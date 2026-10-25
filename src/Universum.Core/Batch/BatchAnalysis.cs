using Universum.Core.Execution;
using Universum.Core.Media;

namespace Universum.Core.Batch;

public sealed record FailedFile(string Path, string Error);

/// <summary>A folder analysed for batch processing: structure groups, unreadable files and left-over temporary files.</summary>
public sealed record BatchAnalysis(
    string Folder,
    bool Recursive,
    IReadOnlyList<BatchGroup> Groups,
    IReadOnlyList<FailedFile> Failed,
    IReadOnlyList<string> LeftoverTemporaryFiles)
{
    public int FileCount => Groups.Sum(g => g.Files.Count);

    public BatchGroup? GroupOf(string path) => Groups.FirstOrDefault(g => g.Files.Any(f => f.Path == path));
}

public static class BatchAnalyzer
{
    private static readonly string[] Extensions = [".mkv", FileProcessor.TemporaryExtension];

    /// <summary>Matroska files of the folder in natural order (Episode 2 before Episode 10).</summary>
    /// <param name="excludeDirectory">A folder to skip, e.g. the target folder of results inside the analysed folder.</param>
    public static (IReadOnlyList<string> Files, IReadOnlyList<string> Leftovers) FindFiles(string folder, bool recursive, string? excludeDirectory = null)
    {
        var excluded = excludeDirectory is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludeDirectory)) + Path.DirectorySeparatorChar;
        var all = Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
            })
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => excluded is null || !Path.GetFullPath(f).StartsWith(excluded, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .Order(NaturalComparer.Instance)
            .ToList();
        return (all.Where(f => !FileProcessor.IsTemporaryFile(f)).ToList(), all.Where(FileProcessor.IsTemporaryFile).ToList());
    }

    /// <summary>Reads the structure of every file (in parallel) and groups the files by structure.</summary>
    public static async Task<BatchAnalysis> AnalyzeAsync(
        string folder,
        bool recursive,
        IMediaInspector inspector,
        IProgress<(int Done, int Total)>? progress = null,
        int parallelism = 4,
        string? excludeDirectory = null,
        CancellationToken cancellationToken = default)
    {
        folder = Path.GetFullPath(folder);
        var (files, leftovers) = FindFiles(folder, recursive, excludeDirectory);
        var results = new (MediaFile? File, string? Error)[files.Count];
        var done = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
            async (index, token) =>
            {
                try
                {
                    var file = await inspector.InspectAsync(files[index], token).ConfigureAwait(false);
                    results[index] = file.IsRecognized && file.ContainerType == "Matroska"
                        ? (file, null)
                        : (null, file.Errors.FirstOrDefault() ?? "not a Matroska file");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results[index] = (null, ex.Message);
                }

                progress?.Report((Interlocked.Increment(ref done), files.Count));
            }).ConfigureAwait(false);

        var bySignature = new Dictionary<string, List<MediaFile>>(StringComparer.Ordinal);
        var order = new List<string>();
        var failed = new List<FailedFile>();
        for (var i = 0; i < files.Count; i++)
        {
            if (results[i].File is not { } file)
            {
                failed.Add(new FailedFile(files[i], results[i].Error ?? "unknown error"));
                continue;
            }

            var signature = StructureSignature.Of(file);
            if (!bySignature.TryGetValue(signature, out var members))
            {
                members = [];
                bySignature[signature] = members;
                order.Add(signature);
            }

            members.Add(file);
        }

        // the largest group first: usually the series or collection the folder is about
        var groups = order
            .Select((signature, position) => (signature, position))
            .OrderByDescending(x => bySignature[x.signature].Count)
            .ThenBy(x => x.position)
            .Select((x, index) =>
            {
                var group = new BatchGroup(x.signature, index);
                group.Files.AddRange(bySignature[x.signature]);
                return group;
            })
            .ToList();

        return new BatchAnalysis(folder, recursive, groups, failed, leftovers);
    }
}

/// <summary>Compares file names so that numbers sort by value ("2" before "10").</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.CompareOrdinal(x, y);
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var numberX = x[startX..i].TrimStart('0');
                var numberY = y[startY..j].TrimStart('0');
                var byLength = numberX.Length.CompareTo(numberY.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                var byValue = string.CompareOrdinal(numberX, numberY);
                if (byValue != 0)
                {
                    return byValue;
                }
            }
            else
            {
                var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (byChar != 0)
                {
                    return byChar;
                }

                i++;
                j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
