using Universum.Core.Planning;

namespace Universum.Core.Execution;

public enum ProcessingStatus
{
    /// <summary>The file already matched the rules; nothing was written.</summary>
    Unchanged,

    Done,

    /// <summary>Blocking issues (e.g. undetermined language); nothing was written.</summary>
    NeedsReview,

    Failed,

    Cancelled,

    /// <summary>The same stream is already in the library (same duration, tracks and size); nothing was written.</summary>
    AlreadyInLibrary,
}

/// <param name="OutputPath">Path of the result in the library (for <see cref="ProcessingStatus.AlreadyInLibrary"/> the file found there).</param>
/// <param name="UndoRecordPath">Record for reverting a header edit, when one was made.</param>
/// <param name="Messages">Errors, verification problems and tool warnings (technical, English).</param>
public sealed record ProcessingResult(
    string SourcePath,
    ProcessingStatus Status,
    WriteMethod Method,
    string? OutputPath,
    string? UndoRecordPath,
    IReadOnlyList<string> Messages);
