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

    /// <summary>
    /// The tracks at a place of the files of the group that can be written as SRT (ASS, SSA, WebVTT …). The group is made by
    /// the languages of the tracks, not by their formats (owner, 15. 11. 2026: one film of a group kept its ASS because the
    /// first film had SRT at that place and Previesť na SRT was not offered) – so every file is asked, not the first only.
    /// </summary>
    public IReadOnlyList<MediaTrack> ToSrtCandidates(SlotKey key) =>
        [.. Files.Select(f => SlotKey.Of(f).FirstOrDefault(x => x.Key == key).Track).OfType<MediaTrack>().Where(Subtitles.SrtConversion.Offers)];

    /// <summary>
    /// The languages UNIVERSUM read from the text of the subtitles of the group's first file (owner, 30. 10. 2026), applied
    /// to the whole group as its suggestion; a choice of the user wins.
    /// </summary>
    public Dictionary<SlotKey, LanguageEvidence> Detected { get; } = [];

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
