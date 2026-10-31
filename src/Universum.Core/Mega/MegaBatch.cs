using System.Text.Json;
using System.Text.Json.Serialization;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.Core.Mega;

/// <summary>When a batch of MEGA spracovanie starts (owner, 29. 10. 2026).</summary>
public enum MegaStart
{
    /// <summary>Only when the user starts it.</summary>
    Manual,

    /// <summary>Right after the batch above it in the list has finished.</summary>
    AfterPrevious,

    /// <summary>At a date and time (UNIVERSUM has to be running then).</summary>
    At,
}

public enum MegaBatchState
{
    /// <summary>Waits for its start.</summary>
    Waiting,

    /// <summary>Added to the job queue, waits there or runs.</summary>
    Queued,

    Done,

    /// <summary>Finished, but some files were skipped by the check before the run or failed.</summary>
    DoneWithProblems,

    Cancelled,

    /// <summary>Its time passed while UNIVERSUM was not running: it is not started late without the user.</summary>
    Missed,
}

public enum MegaFileState
{
    Waiting,
    Done,

    /// <summary>The same stream is already in the library.</summary>
    AlreadyInLibrary,

    /// <summary>The check before the run found it missing or changed; nothing was written.</summary>
    Skipped,

    Failed,
    Cancelled,
}

/// <summary>
/// One file of a MEGA batch exactly as it was prepared in Hromadné spracovanie: the original (path, size, time of the
/// last change, its tracks) and every decision about it – the track decisions (by track ID), the title, the episode.
/// Before the run the file is read again; it is written only when it is still the same file.
/// </summary>
public sealed class MegaFile
{
    public required string Path { get; init; }

    public required long Size { get; init; }

    public required DateTime ModifiedUtc { get; init; }

    /// <summary>
    /// The file as it was analysed (tracks with their IDs, numbers, UIDs, codecs, names, languages, flags and tags; tags and
    /// chapters of the file – <see cref="FileProcessor.StructureOf"/>): the decisions are valid only for this file.
    /// </summary>
    public required string Structure { get; init; }

    public required TitleInfo Title { get; init; }

    public EpisodeNumber? Episode { get; init; }

    public Dictionary<int, LanguageTag> Languages { get; init; } = [];

    public Dictionary<int, bool> Forced { get; init; } = [];

    public List<int> Removed { get; init; } = [];

    public List<int> Kept { get; init; } = [];

    public List<int> Order { get; init; } = [];

    /// <summary>New times of subtitle tracks (by track ID, also the external files' – their IDs are kept in <see cref="Externals"/>).</summary>
    public Dictionary<int, Subtitles.SubtitleTiming> Timings { get; init; } = [];

    /// <summary>Track names written by the user, by track ID (empty = no name).</summary>
    public Dictionary<int, string> Names { get; init; } = [];

    /// <summary>The edits of Úprava titulkostopy, to be opened again in Hromadné spracovanie; null = none.</summary>
    public StudioEdits? Studio { get; init; }

    /// <summary>
    /// The rules' profile with the template the file was planned with, when it differs from the batch's (a file added to
    /// or changed in the batch later); null = <see cref="MegaBatch.Profile"/>.
    /// </summary>
    public RuleProfile? Profile { get; init; }

    /// <summary>
    /// External subtitle files added to the video (owner, 30. 10. 2026): their tracks as analysed, with the size and time
    /// of each file – a file changed or missing at the run is not written with decisions made for another one.
    /// </summary>
    public List<MegaExternal> Externals { get; init; } = [];

    /// <summary>The user chose to write it although it waited for a check (Aj tak) – kept when the batch is edited.</summary>
    public bool ProcessAnyway { get; init; }

    /// <summary>Doplnok k názvu of the file (null: none).</summary>
    public string? Addition { get; init; }

    /// <summary>Where the result was planned when the batch was made (shown on the page).</summary>
    public string PlannedPath { get; init; } = "";

    public MegaFileState State { get; set; }

    /// <summary>Why it was skipped, the tool's message, or where the result is.</summary>
    public string Note { get; set; } = "";

    /// <summary>The result in the library after the run (written now, or the same stream found there).</summary>
    public string? ResultPath { get; set; }

    /// <summary>The original went to the Recycle Bin (during the run or with Zmazať originály).</summary>
    public bool OriginalRemoved { get; set; }

    /// <summary>The decisions for the rules.</summary>
    public TrackDecisions Decisions() => new()
    {
        Languages = Languages,
        Forced = Forced,
        Removed = Removed.ToHashSet(),
        Kept = Kept.ToHashSet(),
        Order = Order,
        Timings = Timings,
        Names = Names,
    };

    /// <summary>A prepared file of Hromadné spracovanie (it has its place in the library and something to do).</summary>
    public static MegaFile From(BatchItem item, RuleProfile? profile = null)
    {
        if (!item.NeedsWork || item.Title is null)
        {
            throw new ArgumentException("Only a prepared file goes into a batch.", nameof(item));
        }

        var info = new FileInfo(item.File.Path);
        return new MegaFile
        {
            Path = item.File.Path,
            Size = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            Structure = FileProcessor.StructureOf(item.File),
            Title = item.Title,
            Episode = item.Episode,
            Languages = item.Decisions.Languages.ToDictionary(),
            Forced = item.Decisions.Forced.ToDictionary(),
            Removed = [.. item.Decisions.Removed],
            Kept = [.. item.Decisions.Kept],
            Order = [.. item.Decisions.Order],
            Timings = item.Decisions.Timings.ToDictionary(),
            Names = item.Decisions.Names.ToDictionary(),
            Studio = item.Group.Decisions.Studio.GetValueOrDefault(item.File.Path),
            PlannedPath = item.PlannedPath ?? "",
            ProcessAnyway = item.Group.Decisions.ProcessAnyway.Contains(item.File.Path),
            Addition = item.Group.Decisions.Additions.GetValueOrDefault(item.File.Path),
            Profile = profile,
            Externals = item.File.Tracks.Where(t => t.ExternalFile is not null).Select(MegaExternal.Of).ToList(),
        };
    }
}

/// <summary>
/// A track of a MEGA file that comes from another file – an external subtitle file, a sound added or written anew (owner,
/// 30. 10. 2026): its track and the file's size and time. <paramref name="Track"/> is the whole track (older batches: none).
/// </summary>
public sealed record MegaExternal(
    int Id, string Path, long Size, DateTime ModifiedUtc, string CodecId, string Codec, bool IsText, LanguageTag Language, string? Name, bool HearingImpaired, long? Frames,
    MediaTrack? Track = null)
{
    public static MegaExternal Of(MediaTrack track)
    {
        var info = new FileInfo(track.ExternalFile!);
        return new MegaExternal(track.Id, track.ExternalFile!, info.Exists ? info.Length : -1, info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
            track.CodecId ?? "", track.Codec, track.IsTextSubtitles, track.Language, track.Name, track.Flags.HearingImpaired, track.StatisticsFrames, track);
    }

    /// <summary>The file is as it was when the batch was made.</summary>
    public bool Unchanged
    {
        get
        {
            var info = new FileInfo(Path);
            return info.Exists && info.Length == Size && info.LastWriteTimeUtc == ModifiedUtc;
        }
    }

    public MediaTrack ToTrack() => Track is { } track ? track with { Id = Id } : new()
    {
        Id = Id, Type = TrackType.Subtitles, CodecId = CodecId, Codec = Codec, IsTextSubtitles = IsText, Language = Language, Name = Name,
        Flags = new TrackFlags(Default: false, Forced: false, HearingImpaired: HearingImpaired), StatisticsFrames = Frames, ExternalFile = Path,
    };
}

/// <summary>
/// A batch of MEGA spracovanie (owner, 29. 10. 2026): files prepared in Hromadné spracovanie, kept to be written later –
/// after the batch above it, at a time, or when the user starts it. It holds the exact list of its files; files put into
/// the input folder later never join it. The rules' profile (with the template) and the library are those of the moment
/// the batch was made.
/// </summary>
public sealed class MegaBatch
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>"Dávka 3" – numbers are not reused while the list exists.</summary>
    public int Number { get; init; }

    /// <summary>What the batch is, e.g. the input folder and its titles.</summary>
    public string Name { get; set; } = "";

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    public required string LibraryRoot { get; init; }

    public required RuleProfile Profile { get; init; }

    /// <summary>
    /// The originals as chosen in Hromadné spracovanie when the batch was sent (owner, 30. 10. 2026: the batch takes the
    /// setting over) – kept, deleted right after each result, or deleted with Zmazať originály after the batch.
    /// </summary>
    public OriginalsMode Originals { get; init; } = OriginalsMode.Keep;

    /// <summary>The template of Hromadné spracovanie (JSON), so the batch opens again with it; null = rules only.</summary>
    public string? TemplateJson { get; set; }

    /// <summary>Opened in Hromadné spracovanie to be changed: it does not start meanwhile (not kept in the file).</summary>
    [JsonIgnore]
    public bool IsBeingEdited { get; set; }

    public List<MegaFile> Files { get; init; } = [];

    public MegaStart Start { get; set; } = MegaStart.Manual;

    /// <summary>Local date and time of the start (with <see cref="MegaStart.At"/>).</summary>
    public DateTime? StartAt { get; set; }

    public MegaBatchState State { get; set; } = MegaBatchState.Waiting;

    /// <summary>The outcome of the run in one sentence.</summary>
    public string Outcome { get; set; } = "";

    public DateTime? FinishedUtc { get; set; }

    /// <summary>Waiting, queued or running: its files belong to it.</summary>
    [JsonIgnore]
    public bool IsOpen => State is MegaBatchState.Waiting or MegaBatchState.Queued or MegaBatchState.Missed;

    [JsonIgnore]
    public string Label => $"Dávka {Number}";
}

/// <summary>
/// The list of MEGA batches, kept in a JSON file (written atomically). A file belongs to at most one open batch: files of
/// open batches are reserved – Hromadné spracovanie shows them as "v dávke N" and does not write them itself.
/// </summary>
public sealed class MegaStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;
    private readonly List<MegaBatch> _batches;

    public MegaStore(string file)
    {
        _file = file;
        _batches = Load(file);
    }

    public IReadOnlyList<MegaBatch> Batches => _batches;

    /// <summary>Files of open batches with the batch they belong to (<paramref name="except"/> left out: it is being edited).</summary>
    public IReadOnlyDictionary<string, MegaBatch> Reserved(MegaBatch? except = null)
    {
        var reserved = new Dictionary<string, MegaBatch>(Paths.Comparer);
        foreach (var batch in _batches.Where(b => b.IsOpen && b != except))
        {
            foreach (var file in batch.Files.Where(f => f.State == MegaFileState.Waiting))
            {
                reserved.TryAdd(System.IO.Path.GetFullPath(file.Path), batch);
            }
        }

        return reserved;
    }

    /// <summary>A new batch of prepared files; files already in an open batch are left out (returns null when none is left).</summary>
    public MegaBatch? Add(
        string name, string libraryRoot, RuleProfile profile, OriginalsMode originals, IEnumerable<BatchItem> items, string? templateJson = null)
    {
        var reserved = Reserved();
        var files = items.Where(i => i.NeedsWork && i.Title is not null && !reserved.ContainsKey(System.IO.Path.GetFullPath(i.File.Path)))
            .Select(i => MegaFile.From(i)).ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var batch = new MegaBatch
        {
            Number = _batches.Count == 0 ? 1 : _batches.Max(b => b.Number) + 1,
            Name = name,
            LibraryRoot = libraryRoot,
            Profile = profile,
            Originals = originals,
            Files = files,
            TemplateJson = templateJson,
        };
        _batches.Add(batch);
        Save();
        return batch;
    }

    /// <summary>
    /// Puts prepared files into a waiting batch (owner, 29. 10. 2026: a batch is completed or corrected): a file the batch
    /// has is replaced with its new decisions, a new one is added; files of another open batch stay out.
    /// </summary>
    /// <returns>The files added and the files changed.</returns>
    /// <param name="profile">The rules' profile with the template the items were planned with (kept with each file).</param>
    /// <param name="templateJson">The template, so the batch opens with it next time.</param>
    public (int Added, int Changed) Put(MegaBatch batch, IEnumerable<BatchItem> items, RuleProfile profile, string? templateJson)
    {
        if (!batch.IsOpen || batch.State == MegaBatchState.Queued)
        {
            throw new InvalidOperationException("Only a waiting batch can be changed.");
        }

        var reserved = Reserved(batch);
        var (added, changed) = (0, 0);
        foreach (var item in items.Where(i => i.NeedsWork && i.Title is not null && !reserved.ContainsKey(System.IO.Path.GetFullPath(i.File.Path))))
        {
            var file = MegaFile.From(item, profile);
            var at = batch.Files.FindIndex(f => Paths.Same(f.Path, file.Path));
            if (at >= 0)
            {
                batch.Files[at] = file;
                changed++;
            }
            else
            {
                batch.Files.Add(file);
                added++;
            }
        }

        batch.TemplateJson = templateJson;
        Save();
        return (added, changed);
    }

    public void Remove(MegaBatch batch)
    {
        if (batch.State != MegaBatchState.Queued && _batches.Remove(batch))
        {
            Save();
        }
    }

    /// <summary>Moves a batch up (-1) or down (+1) in the list.</summary>
    public bool Move(MegaBatch batch, int step)
    {
        var index = _batches.IndexOf(batch);
        var other = index + step;
        if (index < 0 || other < 0 || other >= _batches.Count)
        {
            return false;
        }

        (_batches[index], _batches[other]) = (_batches[other], _batches[index]);
        Save();
        return true;
    }

    /// <summary>The batch after <paramref name="batch"/> that waits to start after it, or null.</summary>
    public MegaBatch? Follower(MegaBatch batch)
    {
        var index = _batches.IndexOf(batch);
        return index >= 0 && index + 1 < _batches.Count && _batches[index + 1] is { Start: MegaStart.AfterPrevious, State: MegaBatchState.Waiting } next
            ? next
            : null;
    }

    /// <summary>How late a batch may still start: a later one waits for the user (owner, 29. 10. 2026).</summary>
    public static readonly TimeSpan Lateness = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Batches whose time has come. One whose time passed more than <see cref="Lateness"/> ago – UNIVERSUM was not running,
    /// the computer slept, or the time was set into the past – is marked missed and waits for the user: a night run is
    /// never started in the morning by itself.
    /// </summary>
    public IReadOnlyList<MegaBatch> Due(DateTime now)
    {
        var due = new List<MegaBatch>();
        var missed = false;
        foreach (var batch in _batches.Where(b => b is { State: MegaBatchState.Waiting, Start: MegaStart.At, StartAt: not null, IsBeingEdited: false }))
        {
            if (batch.StartAt!.Value > now)
            {
                continue;
            }

            if (now - batch.StartAt.Value > Lateness)
            {
                batch.State = MegaBatchState.Missed;
                missed = true;
                continue;
            }

            due.Add(batch);
        }

        if (missed)
        {
            Save();
        }

        return due;
    }

    /// <summary>Removes the finished batches.</summary>
    public void ClearFinished()
    {
        if (_batches.RemoveAll(b => !b.IsOpen && b.State != MegaBatchState.Queued) > 0)
        {
            Save();
        }
    }

    /// <summary>Writes the list atomically (a new file renamed over the old one).</summary>
    public void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(_file);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _file + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_batches, Json));
        File.Move(temporary, _file, overwrite: true);
    }

    private static List<MegaBatch> Load(string file)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        var batches = JsonSerializer.Deserialize<List<MegaBatch>>(File.ReadAllText(file), Json) ?? [];

        // a batch queued when UNIVERSUM ended did not finish: it waits again (its written files were recorded)
        foreach (var batch in batches.Where(b => b.State == MegaBatchState.Queued))
        {
            batch.State = MegaBatchState.Waiting;
            batch.Start = MegaStart.Manual;
        }

        return batches;
    }
}
