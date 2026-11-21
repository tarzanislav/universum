using Universum.Core.Media;
using Universum.Core.Text;

namespace Universum.Core.Batch;

/// <summary>
/// Structure of a file for grouping (specification, section 7.1): video count, audio and subtitle languages in order and
/// forced flags. Files with the same signature are handled together. Owner, 21. 11. 2026: chapters and tags no longer
/// make a group of their own – they are decided for each file by itself (TargetState, FileProcessor).
/// </summary>
public static class StructureSignature
{
    public static string Of(MediaFile file)
    {
        var video = file.TracksOf(TrackType.Video).Count();
        var audio = string.Join(", ", file.TracksOf(TrackType.Audio).Select(t => t.Language.Primary));
        var subtitles = string.Join(", ", file.TracksOf(TrackType.Subtitles).Select(t =>
            t.Language.Primary + (t.Flags.Forced || (t.ExternalFile is not null && t.Name == Rules.UniversumRules.ForcedName) ? " (forced)" : "") + (t.ExternalFile is not null ? CoreText.T(" (externé)") : "")));
        return CoreText.F("V {0} | A: {1} | T: {2}", video, Empty(audio), Empty(subtitles));
    }

    private static string Empty(string list) => list.Length == 0 ? "–" : list;
}
