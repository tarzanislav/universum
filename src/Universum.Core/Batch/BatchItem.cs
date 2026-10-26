using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.Planning;
using Universum.Core.Rules;
using Universum.Core.Templates;

namespace Universum.Core.Batch;

public enum BatchStatus
{
    /// <summary>Nothing to do: the file matches the rules (and, with a library, is already in its place).</summary>
    Unchanged,

    /// <summary>No changes inside the file; it is only moved or copied into the library.</summary>
    Place,

    HeaderEdit,
    Remux,
    NeedsReview,
}

public enum ReviewReasonKind
{
    UndeterminedLanguage,
    MissingRequiredSlot,
    MultipleVideoTracks,
    NoAudio,

    /// <summary>The film or series title is not decided (or a suggestion waits for confirmation).</summary>
    MissingTitle,

    /// <summary>A series file without a decided episode number (or only a suggested one).</summary>
    MissingEpisode,

    /// <summary>Another file of the batch is the same episode with the same description (specification 13.5).</summary>
    DuplicateEpisode,

    /// <summary>TMDB lists the seasons of the series and does not know this season or episode (specification 13.5).</summary>
    UnknownEpisode,

    /// <summary>The file name names another series than most files of the folder (one folder is one series).</summary>
    OtherSeries,

    /// <summary>Films or series is only guessed from the names; the user has to confirm it (specification 13.5).</summary>
    KindNotConfirmed,

    /// <summary>An extra (trailer, sample, featurette …), not a film or an episode.</summary>
    ExtraFile,

    /// <summary>The path in the library would be too long for Windows.</summary>
    PathTooLong,
}

/// <summary>Why a file is not processed automatically.</summary>
/// <param name="Slot">The track position (undetermined language) or null.</param>
/// <param name="TemplateSlot">The missing template slot, if that is the reason.</param>
/// <param name="Detail">What exactly is missing or wrong (Slovak), e.g. "chýba rok" or the other file's name.</param>
public sealed record ReviewReason(ReviewReasonKind Kind, SlotKey? Slot = null, TemplateSlot? TemplateSlot = null, string? Detail = null)
{
    /// <summary>The user may process the file anyway (then a second version gets a number, K1).</summary>
    public bool CanBeOverridden => Kind is ReviewReasonKind.MissingRequiredSlot or ReviewReasonKind.DuplicateEpisode or ReviewReasonKind.UnknownEpisode or
        ReviewReasonKind.OtherSeries or ReviewReasonKind.ExtraFile;
}

/// <summary>One file of a batch with everything decided for it: the simulation result.</summary>
public sealed record BatchItem(
    MediaFile File,
    BatchGroup Group,
    TrackDecisions Decisions,
    TargetState Target,
    EditPlan Plan,
    TemplateMatch Match,
    IReadOnlyDictionary<SlotKey, LanguageEvidence> Evidence,
    IReadOnlyList<ReviewReason> Reasons)
{
    /// <summary>Where the result goes; null without a library or while the title or episode is not decided.</summary>
    public Placement? Placement { get; init; }

    public TitleInfo? Title { get; init; }

    public EpisodeNumber? Episode { get; init; }

    public BatchStatus Status => Reasons.Count > 0
        ? BatchStatus.NeedsReview
        : Plan.Method switch
        {
            WriteMethod.None => Placement is null || Placement.Holds(File.Path, FileSize) ? BatchStatus.Unchanged : BatchStatus.Place,
            WriteMethod.HeaderEdit => BatchStatus.HeaderEdit,
            _ => BatchStatus.Remux,
        };

    /// <summary>The file is processed by a run: it has a place in the library and something to do.</summary>
    public bool NeedsWork => Placement is not null && Status is BatchStatus.Place or BatchStatus.HeaderEdit or BatchStatus.Remux;

    /// <summary>Planned path in the library, with the size class estimated from the original size.</summary>
    public string? PlannedPath => Placement?.PathFor(FileSize);

    /// <summary>Size of the original (estimate of the result's size class).</summary>
    public long FileSize => _size ??= System.IO.File.Exists(File.Path) ? new FileInfo(File.Path).Length : 0;

    private long? _size;
}
