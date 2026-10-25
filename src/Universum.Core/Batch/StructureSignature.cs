using Universum.Core.Media;

namespace Universum.Core.Batch;

/// <summary>
/// Structure of a file for grouping (specification, section 7.1): video count, audio and subtitle languages in order,
/// forced flags, chapters and tags. Files with the same signature are handled together.
/// </summary>
public static class StructureSignature
{
    public static string Of(MediaFile file)
    {
        var video = file.TracksOf(TrackType.Video).Count();
        var audio = string.Join(", ", file.TracksOf(TrackType.Audio).Select(t => t.Language.Primary));
        var subtitles = string.Join(", ", file.TracksOf(TrackType.Subtitles).Select(t => t.Language.Primary + (t.Flags.Forced ? " (forced)" : "")));
        return $"V {video} | A: {Empty(audio)} | T: {Empty(subtitles)} | kapitoly: {(file.HasChapters ? "áno" : "nie")} | tagy: {(file.HasTags ? "áno" : "nie")}";
    }

    private static string Empty(string list) => list.Length == 0 ? "–" : list;
}
