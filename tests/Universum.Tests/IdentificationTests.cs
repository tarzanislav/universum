using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;

namespace Universum.Tests;

public sealed class IdentificationTests
{
    [Test]
    public void ParsesRecordedMkvmergeOutput()
    {
        var file = MkvMergeIdentificationParser.Parse(TestData.Text("identify/en-und-cz-forced.mkvmerge-82.json"), "film.mkv");
        AssertEnUndCzForced(file);
        Assert.Equal(19, file.IdentificationFormatVersion);
    }

    [Test]
    public void MissingPropertiesUseMatroskaDefaults()
    {
        var file = MkvMergeIdentificationParser.Parse("""{"tracks":[{"id":0,"type":"audio","codec":"AAC","properties":{}}]}""", "x.mkv");
        var track = file.Tracks.Single();
        Assert.Equal(new TrackFlags(), track.Flags, "flags");
        Assert.True(track.Language.IsUndetermined, "language");
        Assert.True(!file.HasTags && !file.HasChapters, "no tags or chapters");
    }

    [Test]
    public void StatisticsAndTrackTags()
    {
        var file = MkvMergeIdentificationParser.Parse(
            """{"track_tags":[{"track_id":1,"num_entries":4}],"tracks":[{"id":1,"type":"subtitles","properties":{"tag_number_of_frames":"312","tag_bps":"95"}}]}""",
            "x.mkv");
        var track = file.Tracks.Single();
        Assert.Equal(312L, track.StatisticsFrames);
        Assert.Equal(95L, track.StatisticsBitsPerSecond);
        Assert.Equal(4, track.TagEntries);
        Assert.True(file.HasTags, "track tags count as tags");
    }

    [Test]
    public void BrokenJsonIsReported() =>
        Assert.Throws<MkvToolNixException>(() => MkvMergeIdentificationParser.Parse("{", "x.mkv"), "broken JSON");

    [Test]
    public async Task InspectsAllTestFiles()
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var inspector = new MkvMergeInspector(installation, new ProcessRunner());
        Console.WriteLine($"      using {installation}");

        AssertEnUndCzForced(await inspector.InspectAsync(TestData.Media("en-und-cz-forced.mkv")));

        var czOnly = await inspector.InspectAsync(TestData.Media("cz-only.mkv"));
        Assert.Sequence(["video", "audio", "subtitles"], czOnly.Tracks.Select(t => t.Type.ToString().ToLowerInvariant()), "cz-only types");
        Assert.True(czOnly.Tracks.Skip(1).All(t => t.Language.Group == LanguageGroup.Czech), "cz-only languages");

        var mixed = await inspector.InspectAsync(TestData.Media("sk-cz-mixed.mkv"));
        Assert.Sequence(
            [LanguageGroup.Slovak, LanguageGroup.Czech, LanguageGroup.Other],
            mixed.TracksOf(TrackType.Audio).Select(t => t.Language.Group), "sk-cz-mixed audio");

        var foreign = await inspector.InspectAsync(TestData.Media("en-only-cz-subs.mkv"));
        Assert.True(foreign.TracksOf(TrackType.Subtitles).Single(t => t.Name == "Czech forced").Flags.Forced, "forced flag read");
    }

    [Test]
    public async Task UnicodeFileNameSurvives()
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var folder = Directory.CreateTempSubdirectory("universum-test-");
        try
        {
            var path = Path.Combine(folder.FullName, "Čierny Peter – ľúbostný príbeh (2026) 日本.mkv");
            File.Copy(TestData.Media("cz-only.mkv"), path);
            var file = await new MkvMergeInspector(installation, new ProcessRunner()).InspectAsync(path);
            Assert.Equal(3, file.Tracks.Count, "tracks");
            Assert.Equal(path, file.Path, "path");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static void AssertEnUndCzForced(MediaFile file)
    {
        Assert.Equal("Matroska", file.ContainerType);
        Assert.Equal("Invented.Film.2026.1080p", file.Title);
        Assert.Equal(6, file.Tracks.Count, "tracks");
        Assert.Equal(2, file.ChapterEntries, "chapters");
        Assert.Equal(1, file.GlobalTagEntries, "global tags");
        Assert.True(file.HasTags, "tags present");
        Assert.True(file.Duration is { TotalSeconds: > 1.5 and < 3 }, $"duration {file.Duration}");

        var video = file.TracksOf(TrackType.Video).Single();
        Assert.Equal("x264 by Invented Group", video.Name);

        var audio = file.TracksOf(TrackType.Audio).ToList();
        Assert.Equal("en", audio[0].Language.Primary, "audio 1 language");
        Assert.True(audio[0].Flags.Default, "audio 1 default");
        Assert.True(audio[1].Language.IsUndetermined, "audio 2 undetermined");
        Assert.Equal("CZ Dabing", audio[1].Name);
        Assert.True(!audio[1].Flags.Default, "audio 2 not default");
        Assert.Equal(1, audio[1].AudioChannels, "audio 2 channels");

        var subtitles = file.TracksOf(TrackType.Subtitles).ToList();
        Assert.Equal("Forced", subtitles[0].Name);
        Assert.True(subtitles[0].IsTextSubtitles, "text subtitles");
        Assert.Equal(LanguageGroup.Czech, subtitles[1].Language.Group, "subtitles 2 language");
        Assert.Equal("en", subtitles[2].Language.Primary, "subtitles 3 language");
    }
}
