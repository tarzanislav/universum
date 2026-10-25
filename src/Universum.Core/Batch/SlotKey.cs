using Universum.Core.Media;

namespace Universum.Core.Batch;

/// <summary>A track position within its type ("second audio track"); stable across files of one structure group.</summary>
public readonly record struct SlotKey(TrackType Type, int Ordinal)
{
    /// <summary>Slot keys of all audio and subtitle tracks of a file, with the tracks.</summary>
    public static IEnumerable<(SlotKey Key, MediaTrack Track)> Of(MediaFile file) =>
        new[] { TrackType.Audio, TrackType.Subtitles }
            .SelectMany(type => file.TracksOf(type).Select((track, ordinal) => (new SlotKey(type, ordinal), track)));

    public override string ToString() => $"{Type} {Ordinal + 1}";
}
