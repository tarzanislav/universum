using Universum.Core.Media;

namespace Universum.Core.Batch;

/// <summary>Decisions of the user that apply to every file of one structure group.</summary>
public sealed class GroupDecisions
{
    public Dictionary<SlotKey, LanguageTag> Languages { get; } = [];

    /// <summary>Confirmed forced state of subtitle slots.</summary>
    public Dictionary<SlotKey, bool> Forced { get; } = [];

    /// <summary>Files (full paths) to process even though required template slots are missing.</summary>
    public HashSet<string> ProcessAnyway { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
