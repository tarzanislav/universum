using Universum.Core.Media;

namespace Universum.Core.Batch;

/// <summary>Decisions of the user that apply to every file of one structure group.</summary>
public sealed class GroupDecisions
{
    public Dictionary<SlotKey, LanguageTag> Languages { get; } = [];

    /// <summary>Confirmed forced state of subtitle slots.</summary>
    public Dictionary<SlotKey, bool> Forced { get; } = [];

    /// <summary>
    /// Languages chosen for one file (full path) of the group – the films of one group may be spoken in different
    /// languages; they win over the group's language.
    /// </summary>
    public Dictionary<string, Dictionary<SlotKey, LanguageTag>> FileLanguages { get; } =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The language chosen for a track of one file, or null.</summary>
    public LanguageTag? FileLanguage(string path, SlotKey key) =>
        FileLanguages.TryGetValue(path, out var languages) && languages.TryGetValue(key, out var language) ? language : (LanguageTag?)null;

    /// <summary>Chooses (or with null removes) the language of a track of one file.</summary>
    public void SetFileLanguage(string path, SlotKey key, LanguageTag? language)
    {
        if (language is not { } chosenLanguage)
        {
            if (FileLanguages.TryGetValue(path, out var languages))
            {
                languages.Remove(key);
            }

            return;
        }

        if (!FileLanguages.TryGetValue(path, out var chosen))
        {
            FileLanguages[path] = chosen = [];
        }

        chosen[key] = chosenLanguage;
    }

    /// <summary>Names of audio and subtitle slots written by the user (owner, 30. 10. 2026); empty = no name.</summary>
    public Dictionary<SlotKey, string> Names { get; } = [];

    /// <summary>Subtitle slots kept although their language is not among the profile's subtitle languages.</summary>
    public HashSet<SlotKey> Kept { get; } = [];

    /// <summary>
    /// The order of slots the user gave (owner, 29. 10. 2026: dragged up or down); the slots not listed follow in the
    /// rules' order.
    /// </summary>
    public List<SlotKey> Order { get; } = [];

    /// <summary>
    /// Úprava titulkostopy of single files (full path → its edits; owner, 30. 10. 2026: Upraviť v štúdiu → Uložiť do úlohy).
    /// </summary>
    public Dictionary<string, StudioEdits> Studio { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Doplnok k názvu of single files (full path → the text put into the name before the size class; owner, 31. 10. 2026).</summary>
    public Dictionary<string, string> Additions { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Revízia (owner, 31. 10. 2026): files (full paths) written again although the same result of them is in the library;
    /// the earlier result goes to the Recycle Bin once the new one is verified.
    /// </summary>
    public HashSet<string> Revisions { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Files (full paths) to process even though required template slots are missing.</summary>
    public HashSet<string> ProcessAnyway { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
