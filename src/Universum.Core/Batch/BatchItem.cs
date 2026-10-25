using Universum.Core.Media;
using Universum.Core.Planning;
using Universum.Core.Rules;
using Universum.Core.Templates;

namespace Universum.Core.Batch;

public enum BatchStatus
{
    Unchanged,
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
}

/// <summary>Why a file is not processed automatically.</summary>
/// <param name="Slot">The track position (undetermined language) or null.</param>
/// <param name="TemplateSlot">The missing template slot, if that is the reason.</param>
public sealed record ReviewReason(ReviewReasonKind Kind, SlotKey? Slot = null, TemplateSlot? TemplateSlot = null);

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
    public BatchStatus Status => Reasons.Count > 0
        ? BatchStatus.NeedsReview
        : Plan.Method switch
        {
            WriteMethod.None => BatchStatus.Unchanged,
            WriteMethod.HeaderEdit => BatchStatus.HeaderEdit,
            _ => BatchStatus.Remux,
        };

    public bool NeedsWork => Status is BatchStatus.HeaderEdit or BatchStatus.Remux;
}
