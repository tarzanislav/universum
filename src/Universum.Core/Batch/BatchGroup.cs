using Universum.Core.Media;

namespace Universum.Core.Batch;

/// <summary>Files with the same structure (<see cref="StructureSignature"/>), handled with the same decisions.</summary>
public sealed class BatchGroup(string signature, int index)
{
    public string Signature { get; } = signature;

    /// <summary>Label shown to the user: A, B, C … Z, AA, AB …</summary>
    public string Label { get; } = LabelFor(index);

    public List<MediaFile> Files { get; } = [];

    /// <summary>The first file; its tracks are shown when the user decides for the group.</summary>
    public MediaFile Representative => Files[0];

    public GroupDecisions Decisions { get; } = new();

    /// <summary>Suggestions waiting for confirmation (specification, section 7.3, medium confidence).</summary>
    public Dictionary<SlotKey, LanguageEvidence> Suggestions { get; } = [];

    /// <summary>
    /// Suggestions for one file (full path) – the spoken language heard in that file's own audio (owner, 28. 10.: each
    /// film of a group is listened to on its own); they win over the group's suggestions.
    /// </summary>
    public Dictionary<string, Dictionary<SlotKey, LanguageEvidence>> FileSuggestions { get; } =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The suggestion for a track of one file, or null.</summary>
    public LanguageEvidence? FileSuggestion(string path, SlotKey key) =>
        FileSuggestions.TryGetValue(path, out var suggestions) && suggestions.TryGetValue(key, out var suggestion) ? suggestion : null;

    /// <summary>Sets the suggestion for a track of one file.</summary>
    public void SuggestForFile(string path, SlotKey key, LanguageEvidence suggestion)
    {
        if (!FileSuggestions.TryGetValue(path, out var suggestions))
        {
            FileSuggestions[path] = suggestions = [];
        }

        suggestions[key] = suggestion;
    }

    /// <summary>Subtitle slots that are probably forced (few subtitles compared with a full track of the same language).</summary>
    public HashSet<SlotKey> ForcedSuggestions { get; } = [];

    private static string LabelFor(int index)
    {
        var label = "";
        index++;
        while (index > 0)
        {
            index--;
            label = (char)('A' + (index % 26)) + label;
            index /= 26;
        }

        return label;
    }
}
