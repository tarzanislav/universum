using Universum.Core.Video;

namespace Universum.Core.Muxing;

/// <summary>How much of a merged film a preview writes.</summary>
public enum PreviewRange
{
    /// <summary>Three pieces of two minutes – at the beginning, in the middle and near the end – joined in one short file.</summary>
    Samples,

    /// <summary>The whole film (as large as the result).</summary>
    Whole,
}

/// <summary>
/// A preview of Zlučovanie (owner, 1. 11. 2026: several merged results are made and looked at in a player before the final
/// one goes to MEGA spracovanie). It is written into UNIVERSUM's own folder of previews, never into the output folders.
/// </summary>
public static class MuxPreview
{
    /// <summary>The length of one piece of a preview of samples.</summary>
    public static readonly TimeSpan Piece = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The spec of a preview: the same tracks, names, languages and flags, written to <paramref name="outputPath"/> (no
    /// placement in the library); a film shorter than three pieces and their gaps is written whole.
    /// </summary>
    public static MuxSpec Of(MuxSpec spec, string outputPath, PreviewRange range) =>
        spec with { OutputPath = outputPath, Placement = null, PreviewParts = range == PreviewRange.Samples ? Parts(spec.Sources.FirstOrDefault()?.File.Duration) : null };

    /// <summary>"parts:00:00:00.000-00:02:00.000,+00:44:00.000-…" (the start, the middle and 90 % of the film); null: whole.</summary>
    public static string? Parts(TimeSpan? duration)
    {
        if (duration is not { } length || length < Piece * 5)
        {
            return null;
        }

        var parts = new[] { 0.0, 0.5, 0.9 }.Select(at =>
        {
            var start = TimeSpan.FromSeconds(Math.Min(Math.Floor(length.TotalSeconds * at), (length - Piece).TotalSeconds));
            return new CutPart(start, start + Piece);
        }).ToList();
        return Cut.MkvMergeValue(Cut.Normalized(parts));
    }

    /// <summary>A file name of a preview: "Vymyslený film (2026) – náhľad 2 – 1. 11. 14-05.mkv" (letters Windows forbids replaced).</summary>
    public static string FileName(string title, int number, DateTime when)
    {
        var name = $"{(title.Trim().Length > 0 ? title.Trim() : "Zlučovanie")} – náhľad {number} – {when:d. M. HH-mm-ss}.mkv";
        // the letters Windows forbids in a name, on every system the same
        return string.Concat(name.Select(c => c < ' ' || "<>:\"/\\|?*".Contains(c) ? '-' : c));
    }

    /// <summary>The previews older than <paramref name="age"/> in the folder of previews (only UNIVERSUM's own: "… – náhľad …").</summary>
    public static IReadOnlyList<string> Old(string folder, TimeSpan age, DateTime now)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, "* – náhľad *.mkv").Where(f => now - File.GetLastWriteTime(f) > age)];
    }
}
