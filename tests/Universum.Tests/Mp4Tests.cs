using System.Security.Cryptography;
using Universum.Core.Media;
using Universum.Core.Mp4;

namespace Universum.Tests;

/// <summary>
/// UNIVERSUM's own MP4 reader and header editor (owner, 7. 11. 2026): the tracks and sample tables of a file, the header
/// changed without touching the media and undone byte for byte, broken files refused, the record without names.
/// (Checked during development against ffprobe sample by sample on files of ffmpeg's MP4 and QuickTime writer.)
/// </summary>
public sealed class Mp4Tests
{
    private static string Copy(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"universum-mp4-{Guid.NewGuid():N}{Path.GetExtension(name)}");
        File.Copy(TestData.Media(name), path);
        return path;
    }

    [Test]
    public void TracksLanguagesAndSamplesOfAnMp4()
    {
        var file = Mp4Reader.Read(TestData.Media("en-cz.mp4"));
        Assert.Equal("isom", file.Brand, "the brand");
        Assert.True(!file.IsFragmented, "not fragmented");
        Assert.Sequence(["vide avc1 und", "soun mp4a eng", "soun ac-3 ces", "sbtl tx3g ces"], file.Tracks.Select(t => $"{t.Handler} {t.Codec} {t.Language}"), "the tracks");
        Assert.Equal(LanguageGroup.Czech, file.Tracks[2].LanguageTag.Group, "ces is Czech");
        Assert.True(file.Tracks[0] is { Width: 64, Height: 36, Kind: Mp4TrackKind.Video } && file.Tracks[1] is { Channels: 1, SampleRate: 8000 }, "picture and sound");
        Assert.True(file.Tracks[1].Enabled && !file.Tracks[2].Enabled, "the enabled (default) sound");

        // every sample lies inside the media data, in decoding order, the first picture a key frame
        var data = file.Boxes.Single(b => b.Type == "mdat");
        foreach (var track in file.Tracks)
        {
            var samples = track.Samples;
            Assert.True(samples.Count > 0, $"track {track.Id} has samples");
            for (var i = 0; i < samples.Count; i++)
            {
                Assert.True(samples.Offsets[i] >= data.ContentStart && samples.Offsets[i] + samples.Sizes[i] <= data.End, $"track {track.Id} sample {i} inside 'mdat'");
                Assert.True(i == 0 || samples.DecodeTimes[i] >= samples.DecodeTimes[i - 1], $"track {track.Id} sample {i} in order");
            }
        }

        Assert.True(file.Tracks[0].Samples.IsKey(0), "the picture begins with a key frame");
    }

    [Test]
    public void HeaderChangedInPlaceMediaUntouchedUndoneExactly()
    {
        var path = Copy("en-cz.mp4");
        try
        {
            var before = File.ReadAllBytes(path);
            var original = Mp4Reader.Read(path);
            var media = original.Tracks.Select(t => (t.Samples.Offsets.ToArray(), t.Samples.Sizes.ToArray(), t.Samples.DecodeTimes.ToArray())).ToList();
            var change = new Mp4Change(
                [
                    new Mp4TrackChange(3) { Language = LanguageTag.FromCode("sk"), Name = "Vymyslený dabing", Enabled = true },
                    new Mp4TrackChange(2) { Name = "", Enabled = false },
                    new Mp4TrackChange(4) { Name = "Vymyslené titulky" },
                ],
                "Vymyslený titul",
                [1, 3, 2, 4]);
            var undo = Mp4HeaderEditor.Apply(path, change);

            var edited = Mp4Reader.Read(path);
            Assert.Sequence([1, 3, 2, 4], edited.Tracks.Select(t => t.Id), "the order of the tracks");
            var dub = edited.Tracks.Single(t => t.Id == 3);
            Assert.True(dub is { Language: "slk", Name: "Vymyslený dabing", HandlerName: "Vymyslený dabing", Enabled: true }, $"the dub: {dub.Language} {dub.Name} {dub.HandlerName} {dub.Flags}");
            Assert.True(edited.Tracks.Single(t => t.Id == 2) is { Enabled: false, Name: null, HandlerName: "SoundHandler", DisplayName: null, Language: "eng" }, "the other sound: no name, not default");
            Assert.Equal("Vymyslené titulky", edited.Tracks.Single(t => t.Id == 4).DisplayName, "the subtitles named");
            Assert.Equal("Vymyslený titul", edited.Title, "the title");
            Assert.True(edited.Length > before.Length && edited.Boxes.Count(b => b.Type == "moov") == 1 && edited.Boxes.Any(b => b.Type == "free" && b.Start == original.Movie.Start),
                "the new movie at the end, the old one free space");
            foreach (var (track, (offsets, sizes, times)) in original.Tracks.Zip(media))
            {
                var now = edited.Tracks.Single(t => t.Id == track.Id).Samples;
                Assert.True(now.Offsets.SequenceEqual(offsets) && now.Sizes.SequenceEqual(sizes) && now.DecodeTimes.SequenceEqual(times), $"track {track.Id}: the media untouched");
            }

            Assert.True(File.ReadAllBytes(path).AsSpan(0, (int)original.Movie.Start).SequenceEqual(before.AsSpan(0, (int)original.Movie.Start)), "nothing before the old movie changed");

            Mp4HeaderEditor.Undo(path, undo);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(before), "undone byte for byte");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void AnUnchangedHeaderIsWrittenAsItWas()
    {
        var file = Mp4Reader.Read(TestData.Media("en-cz.mp4"));
        Assert.True(Mp4HeaderEditor.NewMovie(file, new Mp4Change([])).AsSpan().SequenceEqual(File.ReadAllBytes(TestData.Media("en-cz.mp4")).AsSpan((int)file.Movie.Start, (int)file.Movie.Size)),
            "no change: the movie box byte for byte");
        Assert.Throws<ArgumentException>(() => Mp4HeaderEditor.NewMovie(file, new Mp4Change([new Mp4TrackChange(9) { Name = "x" }])), "an unknown track");
        Assert.Throws<ArgumentException>(() => Mp4HeaderEditor.NewMovie(file, new Mp4Change([], Order: [1, 2])), "an order without every track");
    }

    [Test]
    public void LanguagesOfMp4()
    {
        Assert.Equal("ces", Mp4Languages.Unpack(Mp4Languages.Pack(LanguageTag.FromCode("cze"))), "Czech as ISO 639-2/T");
        Assert.Equal("slk", Mp4Languages.Unpack(Mp4Languages.Pack(LanguageTag.FromCode("sk"))), "Slovak");
        Assert.Equal("und", Mp4Languages.Unpack(Mp4Languages.Pack(LanguageTag.Undetermined)), "none");
        Assert.Equal("cze", Mp4Languages.Unpack(38), "the Macintosh code of Czech (old QuickTime)");
        Assert.Equal("eng", Mp4Languages.Unpack(0), "the Macintosh code of English");
        Assert.Equal("und", Mp4Languages.Unpack(0x7FFF), "unspecified");
    }

    [Test]
    public void BrokenMp4IsRefusedNeverCrashes()
    {
        var good = File.ReadAllBytes(TestData.Media("en-cz.mp4"));
        var movieStart = (int)Mp4Reader.Read(TestData.Media("en-cz.mp4")).Movie.Start;
        var random = new Random(11);
        int read = 0, refused = 0;
        for (var round = 0; round < 1500; round++)
        {
            var bytes = (byte[])good.Clone();
            if (round % 4 == 0)
            {
                bytes = bytes[..random.Next(1, bytes.Length)];
            }
            else
            {
                for (var i = random.Next(1, 8); i > 0; i--)
                {
                    // mostly in the movie box, where the tables are
                    var at = round % 4 == 1 ? random.Next(bytes.Length) : random.Next(movieStart, bytes.Length);
                    bytes[at] = (byte)random.Next(256);
                }
            }

            try
            {
                var file = Mp4Reader.Read(new MemoryStream(bytes));
                foreach (var track in file.Tracks)
                {
                    _ = track.Samples.Count;
                }

                read++;
            }
            catch (FormatException)
            {
                refused++;
            }
        }

        Assert.True(read > 0 && refused > 0, $"damaged files: {read} still readable, {refused} refused – nothing else thrown");
        Assert.Throws<Mp4FormatException>(() => Mp4Reader.Read(new MemoryStream("not a film, just a sentence"u8.ToArray())), "not an MP4");
    }

    [Test]
    public void RecordWithoutNames()
    {
        var path = Copy("en-cz.mp4");
        try
        {
            Mp4HeaderEditor.Apply(path, new Mp4Change([new Mp4TrackChange(2) { Name = "Vymyslené tajné meno" }], "Vymyslený tajný titul"));
            var record = Mp4Record.Describe(path, "kontrola: vymyslený rozdiel", "0.0.0");
            Assert.True(record.Contains("súbor 1.mp4", StringComparison.Ordinal) && record.Contains("mdat @", StringComparison.Ordinal) && record.Contains("soun ac-3", StringComparison.Ordinal) &&
                        record.Contains("kontrola: vymyslený rozdiel", StringComparison.Ordinal), "the structure and the problem – " + record);
            Assert.True(!record.Contains("tajn", StringComparison.Ordinal) && !record.Contains(Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal) &&
                        !record.Contains(Path.GetTempPath(), StringComparison.Ordinal), "no name of the file, the tracks or the film");
            var saved = Mp4Record.Save(Path.Combine(Path.GetTempPath(), "universum-mp4-records-" + Guid.NewGuid().ToString("N")), record);
            Assert.True(File.ReadAllText(saved) == record, "saved");
            Directory.Delete(Path.GetDirectoryName(saved)!, recursive: true);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
