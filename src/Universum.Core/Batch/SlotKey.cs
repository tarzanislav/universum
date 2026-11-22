using Universum.Core.Media;

namespace Universum.Core.Batch;

/// <summary>A track position within its type ("second audio track"); stable across files of one structure group.</summary>
public readonly record struct SlotKey(TrackType Type, int Ordinal)
{
    /// <summary>Slot keys of all audio and subtitle tracks of a file, with the tracks.</summary>
    public static IEnumerable<(SlotKey Key, MediaTrack Track)> Of(MediaFile file) =>
        new[] { TrackType.Audio, TrackType.Subtitles }
            .SelectMany(type => file.TracksOf(type).Select((track, ordinal) => (new SlotKey(type, ordinal), track)));

    /// <summary>
    /// Slot keys of the file's own video tracks (owner, 22. 11. 2026: shown in Stopy skupiny – a second one, often a cover,
    /// stops the file, and it can be left out there); a picture written anew is not one of them.
    /// </summary>
    public static IEnumerable<(SlotKey Key, MediaTrack Track)> Videos(MediaFile file) =>
        file.TracksOf(TrackType.Video).Where(t => t.ExternalFile is null && t.VideoEncode is null)
            .Select((track, ordinal) => (new SlotKey(TrackType.Video, ordinal), track));

    /// <summary>The video tracks (<see cref="Videos"/>), then the audio and subtitle tracks (<see cref="Of"/>).</summary>
    public static IEnumerable<(SlotKey Key, MediaTrack Track)> WithVideos(MediaFile file) => Videos(file).Concat(Of(file));

    public override string ToString() => $"{Type} {Ordinal + 1}";
}
