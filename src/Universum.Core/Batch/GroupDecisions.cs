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

    /// <summary>Files (full paths) to process even though required template slots are missing.</summary>
    public HashSet<string> ProcessAnyway { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
