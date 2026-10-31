using System.Text.Json;
using System.Text.Json.Serialization;

namespace Universum.Core.Jobs;

/// <summary>How the processing of one file ended.</summary>
public enum LoggedOutcome
{
    Done,
    AlreadyInLibrary,
    Unchanged,
    Skipped,
    Cancelled,
    Failed,

    /// <summary>Failed and the original could not be put back completely: the user has to look.</summary>
    Attention,
}

/// <summary>Something that went wrong, in words: what happened and what to do about it.</summary>
public sealed record LoggedProblem(string What, string Advice);

/// <summary>
/// One file of a processing log, in words (owner, 31. 10. 2026: understandable, named what happens): the original, the
/// result, the headline, what was done, the problems with what to do, and the tools' own lines for the curious.
/// </summary>
public sealed record LoggedFile(
    string Source, string? Result, LoggedOutcome Outcome, string Headline, IReadOnlyList<string> Done, IReadOnlyList<LoggedProblem> Problems,
    IReadOnlyList<string> Technical);

/// <summary>A run of processing kept for the user: what kind of run, when, how it ended and every file.</summary>
public sealed record ProcessingLog(string Kind, DateTime StartedUtc, DateTime FinishedUtc, string Outcome, IReadOnlyList<LoggedFile> Files)
{
    /// <summary>The name of its file in the folder of logs (set when it is read or saved).</summary>
    [JsonIgnore]
    public string Id { get; init; } = "";

    public int Count(LoggedOutcome outcome) => Files.Count(f => f.Outcome == outcome);

    /// <summary>Something failed (the run needs a look).</summary>
    public bool HasProblems => Files.Any(f => f.Outcome is LoggedOutcome.Failed or LoggedOutcome.Attention or LoggedOutcome.Skipped);

    /// <summary>"12 súborov: 10 hotovo, 1 už v knižnici, 1 chyba".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            void Add(LoggedOutcome outcome, string text)
            {
                if (Count(outcome) is > 0 and var count)
                {
                    parts.Add($"{count} {text}");
                }
            }

            Add(LoggedOutcome.Done, "hotovo");
            Add(LoggedOutcome.AlreadyInLibrary, "už v knižnici");
            Add(LoggedOutcome.Unchanged, "bez zmeny");
            Add(LoggedOutcome.Skipped, "preskočené");
            Add(LoggedOutcome.Cancelled, "zrušené");
            Add(LoggedOutcome.Failed, Count(LoggedOutcome.Failed) == 1 ? "chyba" : "chyby");
            Add(LoggedOutcome.Attention, "POZOR");
            var files = Files.Count == 1 ? "1 súbor" : Files.Count is >= 2 and <= 4 ? $"{Files.Count} súbory" : $"{Files.Count} súborov";
            return parts.Count == 0 ? files : $"{files}: {string.Join(", ", parts)}";
        }
    }
}

/// <summary>A job whose files go into the logs of processing.</summary>
public interface ILoggedJob
{
    /// <summary>"Hromadné spracovanie", "Single spracovanie" …</summary>
    string LogKind { get; }

    /// <summary>The files of its last run, in words.</summary>
    IReadOnlyList<LoggedFile> LoggedFiles { get; }
}

/// <summary>
/// The logs of processing in a folder of UNIVERSUM (one JSON file a run), kept until the user deletes them; nothing of
/// them leaves the computer.
/// </summary>
public sealed class ProcessingLogStore(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string Folder { get; } = folder;

    /// <summary>Raised when a log was added or deleted.</summary>
    public event Action? Changed;

    /// <summary>Keeps a run (a run without files is not kept).</summary>
    public ProcessingLog? Add(ProcessingLog log)
    {
        if (log.Files.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(Folder);
        var id = $"{log.FinishedUtc:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}";
        var file = Path.Combine(Folder, id + ".json");
        File.WriteAllText(file + ".part", JsonSerializer.Serialize(log, Json));
        File.Move(file + ".part", file, overwrite: true);
        Changed?.Invoke();
        return log with { Id = id };
    }

    /// <summary>All the logs, the newest first (a damaged file is left out).</summary>
    public IReadOnlyList<ProcessingLog> All()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        var logs = new List<ProcessingLog>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").OrderDescending(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<ProcessingLog>(File.ReadAllText(file), Json) is { Files: not null } log)
                {
                    logs.Add(log with { Id = Path.GetFileNameWithoutExtension(file) });
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                // a damaged log is left out
            }
        }

        return logs;
    }

    /// <summary>Deletes the logs (their files in the folder of logs only).</summary>
    public void Delete(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                File.Delete(Path.Combine(Folder, id + ".json"));
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Deletes every log (also a damaged one).</summary>
    public void DeleteAll()
    {
        if (Directory.Exists(Folder))
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Concat(Directory.EnumerateFiles(Folder, "*.json.part")).ToList())
            {
                File.Delete(file);
            }
        }

        Changed?.Invoke();
    }
}
