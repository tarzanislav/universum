using Universum.Core.Media;
using Universum.Core.Planning;
using Universum.Core.Rules;

namespace Universum.Tests;

/// <summary>The check of a file written (owner's report, 31. 10. 2026: a removed track reached further than the picture).</summary>
public sealed class VerifierTests
{
    [Test]
    public void ShorterWhenARemovedTrackReachedFurther()
    {
        // 22:00 of picture (33 000 frames of 40 ms), the container 22:46 – the subtitles removed ended later
        var video = new MediaTrack
        {
            Id = 0, Number = 1, Type = TrackType.Video, CodecId = "V_MPEG4/ISO/AVC", Language = LanguageTag.Undetermined,
            StatisticsFrames = 33_000, DefaultDurationNanoseconds = 40_000_000,
        };
        var audio = new MediaTrack { Id = 1, Number = 2, Type = TrackType.Audio, CodecId = "A_AAC", Language = new LanguageTag("cs", null), Flags = new TrackFlags(Default: true) };
        var subtitles = new MediaTrack { Id = 2, Number = 3, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8", Language = new LanguageTag("fr", null) };
        var file = new MediaFile { Path = "vymysleny.mkv", ContainerType = "Matroska", Tracks = [video, audio, subtitles], Duration = TimeSpan.Parse("00:22:46.208") };
        var edits = new[] { new TrackEdit(0, null, LanguageTag.Undetermined, video.Flags), new TrackEdit(1, null, audio.Language, audio.Flags) };
        var removed = ManualTarget.Create(file, edits, new HashSet<int> { 2 });
        MediaFile Written(string duration) => file with { Tracks = [video, audio], Duration = TimeSpan.Parse(duration) };

        Assert.True(TargetVerifier.Compare(removed, Written("00:22:00.386")).Count == 0, "the picture kept: not a fault");
        Assert.True(TargetVerifier.Compare(removed, Written("00:21:30.000")).Any(p => p.StartsWith("duration", StringComparison.Ordinal)), "shorter than the picture: a fault");

        var kept = ManualTarget.Create(file with { Tracks = [video, audio] }, edits);
        Assert.True(TargetVerifier.Compare(kept, Written("00:22:00.386")).Any(p => p.StartsWith("duration", StringComparison.Ordinal)), "nothing removed: still a fault");
        var unknown = ManualTarget.Create(file with { Tracks = [video with { StatisticsFrames = null }, audio, subtitles] }, edits, new HashSet<int> { 2 });
        Assert.True(TargetVerifier.Compare(unknown, Written("00:22:00.386")).Any(p => p.StartsWith("duration", StringComparison.Ordinal)), "the picture's length unknown: still a fault");
    }
}
