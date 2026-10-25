using System.Text.Json;
using System.Text.Json.Serialization;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.Core.Templates;

/// <summary>What happens with tracks that match no slot of a template.</summary>
public enum UnlistedTracks
{
    /// <summary>They stay, after the template's tracks of the same type, in their original order.</summary>
    Keep,

    /// <summary>They are left out of the result (only when the user explicitly chooses it).</summary>
    Remove,
}

/// <summary>
/// Tolerant batch template (specification, section 7): the order of language groups and which of them are required.
/// Files with fewer tracks still match as long as all required slots are present; the track flags always come
/// from the rules, so a template can never contradict them.
/// </summary>
public sealed record BatchTemplate
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The default: no slots – every file is processed by the rules alone.</summary>
    public static BatchTemplate RulesOnly { get; } = new() { Name = "Len pravidlá UNIVERSUM" };

    public required string Name { get; init; }

    public IReadOnlyList<TemplateSlot> Slots { get; init; } = [];

    public UnlistedTracks Unlisted { get; init; } = UnlistedTracks.Keep;

    /// <summary>The rules profile with the template's language order.</summary>
    public RuleProfile Apply(RuleProfile profile)
    {
        var order = Slots.Select(s => s.Language.ToLowerInvariant())
            .Concat(profile.PreferredLanguages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Slots.Count == 0 ? profile : profile with { OrderLanguages = order };
    }

    /// <summary>Compares a target state with the slots.</summary>
    public TemplateMatch Evaluate(TargetState target)
    {
        var missingRequired = new List<TemplateSlot>();
        var missingOptional = new List<TemplateSlot>();
        foreach (var slot in Slots)
        {
            if (!target.Tracks.Any(slot.Matches))
            {
                (slot.Required ? missingRequired : missingOptional).Add(slot);
            }
        }

        var unlisted = Slots.Count == 0
            ? []
            : target.Tracks.Where(t => t.Type is TrackType.Audio or TrackType.Subtitles && !Slots.Any(s => s.Matches(t))).ToList();
        return new TemplateMatch(missingRequired, missingOptional, unlisted);
    }

    /// <summary>Builds a template from a file processed by the rules (all present groups, the first audio required).</summary>
    public static BatchTemplate FromTarget(string name, TargetState target)
    {
        var slots = new List<TemplateSlot>();
        foreach (var track in target.Tracks.Where(t => t.Type is TrackType.Audio or TrackType.Subtitles && !t.Language.IsUndetermined))
        {
            var slot = new TemplateSlot(track.Type, track.Language.Primary, track.Role, Required: false);
            if (!slots.Contains(slot))
            {
                slots.Add(slot);
            }
        }

        var firstAudio = slots.FindIndex(s => s.Type == TrackType.Audio);
        if (firstAudio >= 0)
        {
            slots[firstAudio] = slots[firstAudio] with { Required = true };
        }

        return new BatchTemplate { Name = name, Slots = slots };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <exception cref="JsonException">The text is not a valid template.</exception>
    public static BatchTemplate FromJson(string json) =>
        JsonSerializer.Deserialize<BatchTemplate>(json, Json) ?? throw new JsonException("Empty template.");
}

/// <summary>Result of comparing a file with a template.</summary>
/// <param name="Unlisted">Tracks that match no slot.</param>
public sealed record TemplateMatch(
    IReadOnlyList<TemplateSlot> MissingRequired, IReadOnlyList<TemplateSlot> MissingOptional, IReadOnlyList<TrackTarget> Unlisted)
{
    public bool IsComplete => MissingRequired.Count == 0 && MissingOptional.Count == 0 && Unlisted.Count == 0;

    public bool IsAcceptable => MissingRequired.Count == 0;
}
