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
            Mp4HeaderEditor.Undo(path, undo);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(before), "undone again: a file as it was is left alone");

            // owner, 8. 11. 2026 (review): a file another program wrote since is never cut – here the edited file written
            // again with its movie box moved to the front and padding after it, as long as before (a rewrite by another tool)
            var undoAgain = Mp4HeaderEditor.Apply(path, change);
            var edited2 = File.ReadAllBytes(path);
            var movie = Mp4Reader.Read(path).Movie;
            var rewritten = edited2.AsSpan((int)movie.Start, (int)movie.Size).ToArray().Concat(edited2.AsSpan(0, (int)movie.Start).ToArray()).ToArray();
            File.WriteAllBytes(path, rewritten);
            Assert.Throws<Mp4UndoRefusedException>(() => Mp4HeaderEditor.Undo(path, undoAgain), "the movie box is not where UNIVERSUM wrote it");
            Assert.True(File.ReadAllBytes(path).SequenceEqual(rewritten), "and the file is not touched");
            File.WriteAllBytes(path, [.. edited2, 0, 0, 0, 8, (byte)'f', (byte)'r', (byte)'e', (byte)'e']);
            Assert.Throws<Mp4UndoRefusedException>(() => Mp4HeaderEditor.Undo(path, undoAgain), "something written after UNIVERSUM's movie box");

            // the edit stopped while the new movie box was being written (a full disk, a crash): the unfinished tail is cut
            var newMovie = Mp4HeaderEditor.NewMovie(Mp4Reader.Read(new MemoryStream(before)), change);
            foreach (var written in new[] { newMovie.Length / 2, 5 })
            {
                File.WriteAllBytes(path, [.. before, .. newMovie.AsSpan(0, written).ToArray()]);
                Mp4HeaderEditor.Undo(path, undoAgain);
                Assert.True(File.ReadAllBytes(path).SequenceEqual(before), $"{written} bytes of the new movie box: cut away, the file as it was");
            }
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
        Assert.Throws<ArgumentException>(() => Mp4HeaderEditor.NewMovie(file, new Mp4Change([], Order: [1, 1])), "a track twice in the order");
        Assert.Throws<ArgumentException>(() => Mp4HeaderEditor.NewMovie(file, new Mp4Change([], Order: [4, 1]) { Removed = [4] }), "a track left out in the order");
        Assert.Throws<ArgumentException>(() => Mp4HeaderEditor.NewMovie(file, new Mp4Change([]) { Removed = [1, 2, 3, 4] }), "every track left out");
        Assert.Sequence([1, 3, 2, 4], Mp4HeaderEditor.ResultOrder(file, new Mp4Change([], Order: [3, 2])), "the tracks not named keep their places");
    }

    [Test]
    public async Task ChangedAndCheckedByUniversumAndFfprobe()
    {
        // the edit used by the processing and Zlučovanie: changed, read back, and seen the same by ffprobe
        var ffprobe = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe")).FirstOrDefault(File.Exists);
        if (ffprobe is null)
        {
            Assert.Skip("ffprobe not on PATH");
        }

        var path = Copy("en-cz.mp4");
        try
        {
            var change = new Mp4Change([new Mp4TrackChange(3) { Language = LanguageTag.FromCode("sk"), Name = "Vymyslený dabing", Enabled = true }, new Mp4TrackChange(2) { Enabled = false }],
                "Vymyslený titul", [1, 3, 2, 4]);
            var (problems, probed) = await Mp4Editing.ApplyCheckedAsync(path, null, change, new Universum.Core.Processes.ProcessRunner(), () => Task.FromResult<string?>(ffprobe), null, "0.0.0");
            Assert.True(problems.Count == 0 && probed, "changed, and ffprobe sees the same – " + string.Join("; ", problems));

            // a check that finds a difference: ffprobe asked about a title that is not there
            var after = Mp4Reader.Read(path);
            var seen = await Mp4Targets.ProbeAsync(new Universum.Core.Processes.ProcessRunner(), ffprobe!, path, change with { Title = "Iný vymyslený titul" }, after);
            Assert.True(seen is [var only] && only.StartsWith("ffprobe: title", StringComparison.Ordinal), "a difference is found – " + string.Join("; ", seen ?? []));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task RolesOfTracksKeptTheWayFfmpegKeepsThem()
    {
        // owner, 8. 11. 2026: forced subtitles, subtitles for the hearing impaired and commentary in an MP4 – 'kind' boxes
        // with the roles of MPEG-DASH, read and written as FFmpeg does (ffprobe and Jellyfin see them)
        var path = Copy("en-cz.mp4");
        try
        {
            Assert.True(Mp4Reader.Read(path).Tracks.All(t => t.Roles.Count == 0), "no roles in the test file");
            Mp4HeaderEditor.Apply(path, new Mp4Change(
            [
                new Mp4TrackChange(4) { Roles = Mp4Roles.Of(new TrackFlags(Forced: true, HearingImpaired: true)) },
                new Mp4TrackChange(3) { Roles = [Mp4Roles.Commentary], Name = "Vymyslený komentár" },
            ]));
            var edited = Mp4Reader.Read(path);
            Assert.Sequence(["forced-subtitle", "caption"], edited.Tracks.Single(t => t.Id == 4).Roles, "the subtitles: forced, for the hearing impaired");
            Assert.True(edited.Tracks.Single(t => t.Id == 3) is { Roles: ["commentary"], Name: "Vymyslený komentár" }, "the commentary, named");
            Assert.True(Mp4Roles.Apply(new TrackFlags(), edited.Tracks.Single(t => t.Id == 4).Roles) is { Forced: true, HearingImpaired: true, Commentary: false }, "as flags");

            // roles taken away again, the name stays
            Mp4HeaderEditor.Apply(path, new Mp4Change([new Mp4TrackChange(3) { Roles = [] }]));
            Assert.True(Mp4Reader.Read(path).Tracks.Single(t => t.Id == 3) is { Roles.Count: 0, Name: "Vymyslený komentár" }, "no roles, the name kept");

            if (Tool("ffprobe") is { } ffprobe && Tool("ffmpeg") is { } ffmpeg)
            {
                Mp4HeaderEditor.Apply(path, new Mp4Change([new Mp4TrackChange(4) { Roles = [Mp4Roles.Forced] }, new Mp4TrackChange(2) { Roles = [Mp4Roles.Commentary] }]));
                using var seen = System.Text.Json.JsonDocument.Parse(await Run(ffprobe, "-v", "error", "-show_entries", "stream=index:stream_disposition", "-of", "json", path));
                var dispositions = seen.RootElement.GetProperty("streams").EnumerateArray().Select(stream => stream.GetProperty("disposition")).Select(d =>
                    $"{d.GetProperty("comment").GetInt32()}{d.GetProperty("hearing_impaired").GetInt32()}{d.GetProperty("forced").GetInt32()}").ToList();
                Assert.Sequence(["000", "100", "000", "001"], dispositions, "ffprobe sees the commentary and the forced subtitles (comment, hearing impaired, forced)");

                // and what FFmpeg writes UNIVERSUM reads
                var written = Path.Combine(Path.GetTempPath(), $"universum-mp4-kind-{Guid.NewGuid():N}.mp4");
                try
                {
                    await Run(ffmpeg, "-v", "error", "-y", "-i", TestData.Media("en-cz.mp4"), "-map", "0", "-c", "copy", "-disposition:s:0", "forced+hearing_impaired", "-disposition:a:1", "comment", written);
                    var read = Mp4Reader.Read(written);
                    Assert.True(read.Tracks[2].Roles.Contains(Mp4Roles.Commentary) && read.Tracks[3].Roles.Contains(Mp4Roles.Forced) && read.Tracks[3].Roles.Contains(Mp4Roles.Caption),
                        "the roles FFmpeg wrote – " + string.Join(" | ", read.Tracks.Select(t => string.Join(",", t.Roles))));
                }
                finally
                {
                    File.Delete(written);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task SubtitlesLeftOutAndChaptersKept()
    {
        // owner, 8. 11. 2026: subtitles left out go from the header only (their bytes stay unused); an MP4 with chapters of
        // QuickTime (a text track MKVToolNix does not show, referenced by the picture) keeps them in their place
        if (Tool("ffmpeg") is not { } ffmpeg || Tool("ffprobe") is not { } ffprobe)
        {
            Assert.Skip("FFmpeg not on PATH");
            return;
        }

        var work = Directory.CreateTempSubdirectory("universum-mp4-chapters-");
        try
        {
            var metadata = Path.Combine(work.FullName, "kapitoly.txt");
            File.WriteAllText(metadata, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=Prvá\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=2000\ntitle=Druhá\n");
            var path = Path.Combine(work.FullName, "Vymyslený film s kapitolami.mp4");
            await Run(ffmpeg, "-v", "error", "-y", "-i", TestData.Media("en-cz.mp4"), "-i", metadata, "-map", "0", "-map_chapters", "1", "-c", "copy", path);
            var before = File.ReadAllBytes(path);
            var original = Mp4Reader.Read(path);
            var chapters = original.Tracks.Single(t => original.Tracks.Any(o => o.References.Any(r => r.Type == "chap" && r.Ids.Contains(t.Id)))).Id;
            Assert.Equal(5, chapters, "the chapters are the fifth track, referenced by the picture");

            // the Czech sound first, the subtitles left out – what MKVToolNix shows (the chapters are not named)
            var change = new Mp4Change([new Mp4TrackChange(3) { Enabled = true }, new Mp4TrackChange(2) { Enabled = false }], Order: [1, 3, 2]) { Removed = [4] };
            var (problems, probed) = await Mp4Editing.ApplyCheckedAsync(path, null, change, new Universum.Core.Processes.ProcessRunner(), () => Task.FromResult<string?>(ffprobe), null, "0.0.0");
            Assert.True(problems.Count == 0 && probed, "changed and checked – " + string.Join("; ", problems));
            var edited = Mp4Reader.Read(path);
            Assert.Sequence([1, 3, 2, 5], edited.Tracks.Select(t => t.Id), "the subtitles gone, the chapters in their place");
            Assert.True(edited.Tracks[0].References.Single(r => r.Type == "chap").Ids.SequenceEqual([5]), "the chapters still referenced");

            // a referenced track left out takes its reference with it; undone byte for byte
            var beforeRemoval = File.ReadAllBytes(path);
            var undo = Mp4HeaderEditor.Apply(path, new Mp4Change([]) { Removed = [5] });
            Assert.True(Mp4Reader.Read(path).Tracks is { Count: 3 } left && left[0].References.Count == 0, "no reference to a track that is gone");
            Mp4HeaderEditor.Undo(path, undo);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(beforeRemoval) && before.Length < beforeRemoval.Length, "undone exactly");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    [Test]
    public async Task UnusualHeadersOfReviewEditedRight()
    {
        // owner, 8. 11. 2026 – the review's cases: a 'udta' that is only QuickTime's end of a list, an iTunes 'meta' without
        // a list, QuickTime's keyed title, a handler's name that looks like a length, a language with a region
        var ffprobe = Tool("ffprobe");
        var runner = new Universum.Core.Processes.ProcessRunner();
        Func<Task<string?>>? probe = ffprobe is null ? null : () => Task.FromResult<string?>(ffprobe);
        var path = Copy("en-cz.mp4");
        try
        {
            // a track whose 'udta' holds only four zero bytes gets its name and roles before them
            File.WriteAllBytes(path, WithTrackBox(File.ReadAllBytes(path), 1, Box("udta", new byte[4])));
            Assert.True(Mp4Reader.Read(path).Tracks[1].Name is null, "the padding is no box");
            var (problems, _) = await Mp4Editing.ApplyCheckedAsync(path, null,
                new Mp4Change([new Mp4TrackChange(2) { Name = "Vymyslený zvuk", Roles = [Mp4Roles.Commentary], Language = LanguageTag.FromCode("pt-BR") }]), runner, probe, null, "0.0.0");
            var track = Mp4Reader.Read(path).Tracks[1];
            Assert.True(problems.Count == 0 && track is { Name: "Vymyslený zvuk", Roles: ["commentary"], Language: "por", ExtendedLanguage: "pt-BR" },
                $"named, a commentary, Portuguese of Brazil – {string.Join("; ", problems)} {track.Name} {track.ExtendedLanguage}");

            // a handler's name whose first letter looks like its length (an ISO file: a name ended by zero, as FFmpeg reads it)
            var odd = "(" + new string('x', 39);
            (problems, _) = await Mp4Editing.ApplyCheckedAsync(path, null, new Mp4Change([new Mp4TrackChange(3) { Name = odd }]), runner, probe, null, "0.0.0");
            Assert.True(problems.Count == 0 && Mp4Reader.Read(path).Tracks[2].HandlerName == odd, "the name as it is – " + string.Join("; ", problems));

            // an iTunes title in a 'meta' right in 'moov' (no 'udta'): removed, and a new one kept in 'udta'
            var withTitle = File.ReadAllBytes(path);
            File.WriteAllBytes(path, withTitle);
            Mp4HeaderEditor.Apply(path, new Mp4Change([], "Vymyslený starý titul"));
            var titledFile = Mp4Reader.Read(path);
            var metaBox = titledFile.Movie.Find("udta")!.Children.First(c => c.Type == "meta");
            var metaBytes = File.ReadAllBytes(path).AsSpan((int)metaBox.Start, (int)metaBox.Size).ToArray();
            var fresh = File.ReadAllBytes(Copy("en-cz.mp4"));
            File.WriteAllBytes(path, WithMovieUserData(fresh, metaBytes));
            Assert.True(Mp4Reader.Read(path) is { Title: "Vymyslený starý titul" } read && read.Movie.Find("udta") is null, "the title in 'moov/meta'");
            (problems, _) = await Mp4Editing.ApplyCheckedAsync(path, null, new Mp4Change([], ""), runner, probe, null, "0.0.0");
            Assert.True(problems.Count == 0 && Mp4Reader.Read(path).Title is null, "removed – " + string.Join("; ", problems));
            File.WriteAllBytes(path, WithMovieUserData(fresh, metaBytes));
            (problems, _) = await Mp4Editing.ApplyCheckedAsync(path, null, new Mp4Change([], "Vymyslený nový titul"), runner, probe, null, "0.0.0");
            Assert.True(problems.Count == 0 && Mp4Reader.Read(path).Title == "Vymyslený nový titul", "replaced – " + string.Join("; ", problems));

            // an iTunes 'meta' without a list: the title goes into it, not into a second 'meta'
            var mdir = Box("meta", [0, 0, 0, 0, .. Box("hdlr", [.. new byte[8], .. "mdirappl"u8.ToArray(), .. new byte[9]])]);
            File.WriteAllBytes(path, WithMovieUserData(File.ReadAllBytes(path), Box("udta", mdir)));
            (problems, _) = await Mp4Editing.ApplyCheckedAsync(path, null, new Mp4Change([], "Vymyslený titul"), runner, probe, null, "0.0.0");
            var titled = Mp4Reader.Read(path);
            Assert.True(problems.Count == 0 && titled.Title == "Vymyslený titul" && titled.Movie.Find("udta")!.Children.Count(c => c.Type == "meta") == 1,
                "the title in the one 'meta' – " + string.Join("; ", problems));
        }
        finally
        {
            File.Delete(path);
        }

        // QuickTime's keyed title (FFmpeg with use_metadata_tags): replaced, then removed
        if (Tool("ffmpeg") is { } ffmpeg)
        {
            var keyed = Path.Combine(Path.GetTempPath(), $"universum-mp4-keyed-{Guid.NewGuid():N}.mp4");
            try
            {
                await Run(ffmpeg, "-v", "error", "-y", "-i", TestData.Media("en-cz.mp4"), "-map", "0", "-c", "copy", "-movflags", "use_metadata_tags", "-metadata", "title=Starý vymyslený titul", keyed);
                Assert.Equal("Starý vymyslený titul", Mp4Reader.Read(keyed).Title, "the keyed title read");
                var (problems, _) = await Mp4Editing.ApplyCheckedAsync(keyed, null, new Mp4Change([], "Nový vymyslený titul"), runner, probe, null, "0.0.0");
                Assert.True(problems.Count == 0 && Mp4Reader.Read(keyed).Title == "Nový vymyslený titul", "replaced – " + string.Join("; ", problems));
                (problems, _) = await Mp4Editing.ApplyCheckedAsync(keyed, null, new Mp4Change([], ""), runner, probe, null, "0.0.0");
                Assert.True(problems.Count == 0 && Mp4Reader.Read(keyed).Title is null, "removed – " + string.Join("; ", problems));
            }
            finally
            {
                File.Delete(keyed);
            }
        }
    }

    // a box of a type and content
    private static byte[] Box(string type, byte[] content)
    {
        var box = new byte[8 + content.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        content.CopyTo(box, 8);
        return box;
    }

    // the file with a box added at the end of a track (its movie box last in the file: the media data do not move)
    private static byte[] WithTrackBox(byte[] file, int track, byte[] box)
    {
        var read = Mp4Reader.Read(new MemoryStream(file));
        var trak = read.Tracks[track].Box;
        Assert.True(read.Movie.End == file.Length && trak.HeaderSize == 8 && read.Movie.HeaderSize == 8, "the movie box last, small sizes");
        var result = file.AsSpan(0, (int)trak.End).ToArray().Concat(box).Concat(file.AsSpan((int)trak.End).ToArray()).ToArray();
        Grow(result, trak.Start, box.Length);
        Grow(result, read.Movie.Start, box.Length);
        return result;
    }

    // the file with the movie's 'udta' replaced
    private static byte[] WithMovieUserData(byte[] file, byte[] udta)
    {
        var read = Mp4Reader.Read(new MemoryStream(file));
        Assert.True(read.Movie.End == file.Length, "the movie box last");
        var old = read.Movie.Children.FirstOrDefault(c => c.Type == "udta");
        var cutAt = old?.Start ?? read.Movie.End;
        var cutLength = old?.Size ?? 0;
        var result = file.AsSpan(0, (int)cutAt).ToArray().Concat(udta).Concat(file.AsSpan((int)(cutAt + cutLength)).ToArray()).ToArray();
        Grow(result, read.Movie.Start, (int)(udta.Length - cutLength));
        return result;
    }

    private static void Grow(byte[] file, long at, int by) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan((int)at), System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)at)) + (uint)by);

    private static string? Tool(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".exe" : name)).FirstOrDefault(File.Exists);

    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var output = new System.Text.StringBuilder();
        var result = await new Universum.Core.Processes.ProcessRunner().RunAsync(new Universum.Core.Processes.ProcessRequest(tool, arguments) { OnOutputLine = line => output.AppendLine(line) });
        Assert.Equal(0, result.ExitCode, $"{Path.GetFileName(tool)}: {result.StandardError}");
        return output.ToString();
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
