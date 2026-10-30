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

    /// <summary>
    /// The groups split by title (owner, 29. 10. 2026: a folder of several titles – each group holds the files of one
    /// title only, so a group's decisions and "accept all" concern one series or film): files of the same structure and
    /// the same title stay together, in the order of the groups. Only right after the analysis (no decisions are carried).
    /// </summary>
    public BatchAnalysis SplitBy(Func<string, string> titleOf)
    {
        var split = new List<BatchGroup>();
        foreach (var group in Groups)
        {
            foreach (var title in group.Files.GroupBy(f => titleOf(f.Path), IO.Paths.Comparer))
            {
                var part = new BatchGroup(group.Signature, split.Count);
                part.Files.AddRange(title);
                split.Add(part);
            }
        }

        return this with { Groups = split };
    }
}

public static class BatchAnalyzer
{
    /// <summary>
    /// Video files mkvmerge reads (owner, 27. 10. 2026: as wide a use as possible): Matroska and WebM, MP4/MOV/3GP, AVI,
    /// MPEG transport streams (TV recordings, M2TS), MPEG program streams, OGM, FLV, RealMedia. All but Matroska are
    /// written as a new MKV. DVD VOB parts belong together – they are joined in Zlučovanie.
    /// </summary>
    public static IReadOnlyList<string> VideoExtensions { get; } =
    [
        ".mkv", ".mk3d", ".webm", ".mp4", ".m4v", ".mov", ".3gp", ".3g2", ".avi", ".divx", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg",
        ".ogm", ".ogv", ".flv", ".rm", ".rmvb",
    ];

    private static readonly string[] Extensions = [.. VideoExtensions, FileProcessor.TemporaryExtension];

    // the files of a folder and how many videos it holds (external subtitles); nothing when it cannot be read
    private static (IReadOnlyList<string> Files, int Videos) Listing(string directory)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory).ToList();
            return (files, Math.Max(1, files.Count(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], 1);
        }
    }

    /// <summary>Video formats mkvmerge cannot read (named when nothing else is found).</summary>
    private static readonly string[] Unsupported = [".wmv", ".asf", ".dvr-ms", ".wtv"];

    /// <summary>DVD parts: they belong together and are joined in Zlučovanie (named when nothing else is found).</summary>
    private const string DvdPart = ".vob";

    // a disc copied as it is: its pieces are not films (Blu-ray clips, DVD sets) – the film is put together in Zlučovanie;
    // a camcorder's AVCHD folder (PRIVATE\AVCHD\BDMV\STREAM\*.MTS) holds separate clips and is read as usual
    private static bool InDiscStructure(string path)
    {
        var parts = path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return !parts.Any(p => p.Equals("AVCHD", StringComparison.OrdinalIgnoreCase)) &&
               parts.Any(p => p.Equals("BDMV", StringComparison.OrdinalIgnoreCase) || p.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Matroska files of the folder in natural order (Episode 2 before Episode 10).</summary>
    /// <param name="excludeDirectories">Folders to skip: where results are written (MOVIES and TVSHOWS of the library).</param>
    public static (IReadOnlyList<string> Files, IReadOnlyList<string> Leftovers) FindFiles(
        string folder, bool recursive, IEnumerable<string>? excludeDirectories = null, CancellationToken cancellationToken = default)
    {
        var excluded = Excluded(excludeDirectories);
        var all = Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
            })
            .TakeWhile(_ => !cancellationToken.IsCancellationRequested)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) || FileProcessor.IsTemporaryFile(f))
            .Where(f => !IsInside(Path.GetFullPath(f), excluded) && !InDiscStructure(f))
            .Order(NaturalComparer.Instance)
            .ToList();
        return (all.Where(f => !FileProcessor.IsTemporaryFile(f)).ToList(), all.Where(FileProcessor.IsTemporaryFile).ToList());
    }

    private static List<string> Excluded(IEnumerable<string>? folders) =>
        folders?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f))).ToList() ?? [];

    // the folder itself or anything below it (D:\Video\MOVIES, not D:\Video\MOVIES-old)
    private static bool IsInside(string path, List<string> folders)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return folders.Any(f => string.Equals(trimmed, f, comparison) || trimmed.StartsWith(f + Path.DirectorySeparatorChar, comparison));
    }

    /// <summary>
    /// Why a folder gives no file to process (Slovak, for the user): no video it can read, only in subfolders, only results
    /// in MOVIES / TVSHOWS, a disc structure; null when there are video files to read.
    /// </summary>
    public static string? WhyEmpty(string folder, bool recursive, IEnumerable<string>? excludeDirectories = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder))
        {
            return "Priečinok neexistuje alebo nie je dostupný: " + folder;
        }

        folder = Path.GetFullPath(folder);
        var excluded = Excluded(excludeDirectories);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool InLibrary(string path) => IsInside(path, excluded);

        if (InLibrary(folder))
        {
            return "Tento priečinok je v knižnici s výsledkami (MOVIES alebo TVSHOWS) – tie Hromadné spracovanie nespracúva, sú to " +
                   "už hotové súbory. Vyberte priečinok s pôvodnými súbormi.";
        }

        List<(string Path, string Extension, bool Top)> all;
        bool videoBelow;
        try
        {
            IEnumerable<(string Path, string Extension, bool Top)> List(bool below) => Directory.EnumerateFiles(folder, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = below,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
                })
                .TakeWhile(_ => !cancellationToken.IsCancellationRequested)
                .Where(f => !FileProcessor.IsTemporaryFile(f))
                .Select(f => (Path: f, Extension: Path.GetExtension(f).ToLowerInvariant(), Top: string.Equals(Path.GetDirectoryName(f), folder, comparison)))
                .Where(f => VideoExtensions.Contains(f.Extension) || Unsupported.Contains(f.Extension) || f.Extension == DvdPart);

            // without subfolders only the folder is listed; below it the first video found is enough for the hint
            all = List(recursive).ToList();
            videoBelow = !recursive && List(below: true).Any(f => !f.Top && VideoExtensions.Contains(f.Extension) && !InLibrary(f.Path) && !InDiscStructure(f.Path));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return "Do priečinka sa nedá pozrieť: " + ex.Message;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var video = all.Where(f => VideoExtensions.Contains(f.Extension)).ToList();
        bool Usable((string Path, string Extension, bool Top) f) => !InLibrary(f.Path) && !InDiscStructure(f.Path);
        if (video.Any(f => (recursive || f.Top) && Usable(f)))
        {
            return null;
        }

        if (videoBelow)
        {
            return "Priamo v priečinku nie sú video súbory, v podpriečinkoch áno – zaškrtnite „aj podpriečinky“ a dajte Analyzovať znova.";
        }

        var outsideDiscs = video.Where(f => !InDiscStructure(f.Path)).ToList();
        if (outsideDiscs.Count > 0 && outsideDiscs.All(f => InLibrary(f.Path)))
        {
            return $"Všetky video súbory ({outsideDiscs.Count}) sú v knižnici s výsledkami (MOVIES alebo TVSHOWS) – tie Hromadné spracovanie nespracúva.";
        }

        if (all.Any(f => InDiscStructure(f.Path)))
        {
            return "V priečinku je kópia disku (BDMV alebo VIDEO_TS) – jej časti sa nespracúvajú jednotlivo. Film z disku " +
                   "zostavte v Zlučovaní: z Blu-ray pridajte súbor playlistu (BDMV\\PLAYLIST\\*.mpls), z DVD súbory VOB ako ďalšie časti.";
        }

        var where = recursive ? "V priečinku a jeho podpriečinkoch" : "V priečinku";
        if (all.Any(f => f.Extension == DvdPart))
        {
            return $"{where} sú len súbory DVD (VOB) – ich časti patria k sebe, film z nich zostavte v Zlučovaní (pridajte ich ako ďalšie časti).";
        }

        var others = all.Where(f => Unsupported.Contains(f.Extension)).GroupBy(f => f.Extension)
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} × {g.Key.TrimStart('.').ToUpperInvariant()}").ToList();
        return others.Count > 0
            ? $"{where} sú len formáty, ktoré MKVToolNix nevie prečítať: {string.Join(", ", others)}."
            : $"{(recursive ? "V priečinku ani v jeho podpriečinkoch" : "V priečinku")} nie sú žiadne video súbory (MKV, MP4, AVI, TS, M2TS, MOV, MPG, WebM…).";
    }

    /// <summary>Reads the structure of every file (in parallel) and groups the files by structure.</summary>
    public static async Task<BatchAnalysis> AnalyzeAsync(
        string folder,
        bool recursive,
        IMediaInspector inspector,
        IProgress<(int Done, int Total)>? progress = null,
        int parallelism = 4,
        IEnumerable<string>? excludeDirectories = null,
        CancellationToken cancellationToken = default,
        IProgress<MediaFile>? found = null,
        IReadOnlyList<string>? only = null,
        bool externalSubtitles = true)
    {
        folder = Path.GetFullPath(folder);
        // owner, 30. 10. 2026 (bod 5): subtitle files lying with a video belong to it – each folder is listed once
        var folders = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<(IReadOnlyList<string> Files, int Videos)>>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // listing a big network folder takes a while: off the caller's thread, and it can be stopped; chosen files
        // (episodes sent from Novinky) are analysed as they are, in natural order
        var (files, leftovers) = only is not null
            ? ((IReadOnlyList<string>)only.Select(Path.GetFullPath).Distinct().Order(NaturalComparer.Instance).ToList(), (IReadOnlyList<string>)[])
            : await Task.Run(() => FindFiles(folder, recursive, excludeDirectories, cancellationToken), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
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
                    if (externalSubtitles && file.IsRecognized && file.IsSupported)
                    {
                        var (siblings, videos) = folders.GetOrAdd(Path.GetDirectoryName(file.Path)!, directory => new Lazy<(IReadOnlyList<string>, int)>(() => Listing(directory))).Value;
                        if (siblings.Any(s => Subtitles.ExternalSubtitles.Extensions.Contains(Path.GetExtension(s))))
                        {
                            file = await Subtitles.ExternalSubtitleTracks.AddAsync(file, siblings, videos, null, token).ConfigureAwait(false);
                        }
                    }

                    results[index] = !file.IsRecognized || !file.IsSupported
                        ? (null, file.Errors.FirstOrDefault() ?? "mkvmerge tento formát nevie spracovať")
                        : !file.IsMatroska && !file.TracksOf(TrackType.Video).Any()
                            ? (null, "neobsahuje video")
                            : (file, null);
                    if (results[index].File is { } read)
                    {
                        // shown at once (the groups grow while the folder is read)
                        found?.Report(read);
                    }
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
