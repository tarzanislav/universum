using Universum.Core.Processes;
using Universum.Core.Subtitles;
using Universum.Core.Subtitles.Download;
using Universum.Core.Subtitles.Sync;

namespace Universum.Tests;

/// <summary>Automatic sync of subtitles, WebVTT, releases and frame rates (owner, 30. 10. 2026) – invented signals only.</summary>
public sealed class SubtitleSyncTests
{
    private static TimeSpan T(double seconds) => TimeSpan.FromSeconds(seconds);

    // invented dialogue: speech of 1–4 s with pauses of 0,5–6 s for the whole film
    private static List<(TimeSpan Start, TimeSpan End)> Dialogue(double minutes, int seed = 7)
    {
        var random = new Random(seed);
        var cues = new List<(TimeSpan, TimeSpan)>();
        var t = 5.0;
        while (t < minutes * 60)
        {
            var length = 1 + (random.NextDouble() * 3);
            cues.Add((T(t), T(t + length)));
            t += length + 0.5 + (random.NextDouble() * 5.5);
        }

        return cues;
    }

    // what the audio says: the true speech, with music and noise (a tenth of the samples wrong, some music as "speech")
    private static List<ActivityWindow> Audio(List<(TimeSpan Start, TimeSpan End)> speech, TimeSpan duration, int seed = 3)
    {
        var random = new Random(seed);
        var windows = new List<ActivityWindow>();
        foreach (var start in SpeechActivity.Parts(duration))
        {
            var window = ActivityWindow.FromCues(speech, start, TimeSpan.FromMinutes(2.5));
            for (var i = 0; i < window.Values.Length; i++)
            {
                if (random.NextDouble() < 0.1)
                {
                    window.Values[i] = (sbyte)-window.Values[i];
                }
            }

            var music = random.Next(0, window.Values.Length - 1500);
            Array.Fill(window.Values, (sbyte)1, music, 1500);
            windows.Add(window);
        }

        return windows;
    }

    [Test]
    public void LateSubtitlesAreFoundByTheAudio()
    {
        var speech = Dialogue(100);
        var late = speech.Select(c => (c.Start + T(2.5), c.End + T(2.5))).ToList();
        var result = SubtitleSync.Align(late, Audio(speech, TimeSpan.FromMinutes(100)), "podľa zvuku")!;
        Assert.True(result.Reliable, result.Detail);
        foreach (var at in new[] { 60.0, 3000, 5900 })
        {
            Assert.True(Math.Abs((result.Timing.Apply(T(at + 2.5)) - T(at)).TotalMilliseconds) <= 60, $"2,5 s late – at {at} s: {result.Timing.Apply(T(at + 2.5))} ({result.Detail})");
        }
    }

    [Test]
    public void SubtitlesOfAnotherFrameRateAreFound()
    {
        // subtitles made for a 25 fps video, the film runs at 23,976 (and they start 1,2 s early)
        var speech = Dialogue(110, seed: 11);
        var ratio = 25.0 / (24000.0 / 1001);
        var wrong = speech.Select(c => (T(c.Start.TotalSeconds / ratio) - T(1.2), T(c.End.TotalSeconds / ratio) - T(1.2))).ToList();
        var result = SubtitleSync.Align(wrong, Audio(speech, TimeSpan.FromMinutes(110), seed: 5), "podľa zvuku")!;
        Assert.True(result.Reliable && result.Detail.Contains("FPS sa líšia", StringComparison.Ordinal), result.Detail);
        foreach (var cue in new[] { speech[3], speech[speech.Count / 2], speech[^2] })
        {
            var shown = T(cue.Start.TotalSeconds / ratio) - T(1.2);
            Assert.True(Math.Abs((result.Timing.Apply(shown) - cue.Start).TotalMilliseconds) <= 80, $"at {cue.Start}: {result.Timing.Apply(shown)} ({result.Detail})");
        }
    }

    [Test]
    public void ReferenceSubtitlesInAnotherLanguageAndBothTogether()
    {
        // English subtitles that fit the film (a little different timing, some lines merged), Czech ones 3 s early
        var speech = Dialogue(90, seed: 21);
        var reference = speech.Where((_, i) => i % 7 != 0).Select(c => (c.Start + T(0.1), c.End - T(0.1))).ToList();
        var czech = speech.Select(c => (c.Start - T(3), c.End - T(3))).ToList();
        var duration = TimeSpan.FromMinutes(90);
        var windows = SpeechActivity.Parts(duration).Select(s => ActivityWindow.FromCues(reference, s, TimeSpan.FromMinutes(2.5))).ToList();
        var bySubtitles = SubtitleSync.Align(czech, windows, "podľa titulkov")!;
        var byAudio = SubtitleSync.Align(czech, Audio(speech, duration, seed: 9), "podľa zvuku")!;
        Assert.True(bySubtitles.Reliable && Math.Abs((bySubtitles.Timing.Apply(T(997)) - T(1000)).TotalMilliseconds) <= 150, bySubtitles.Detail);
        var both = SubtitleSync.Combine(byAudio, bySubtitles, duration)!;
        Assert.True(both.Reliable && both.Detail.Contains("isté", StringComparison.Ordinal), both.Detail);

        var disagreeing = SubtitleSync.Combine(byAudio, bySubtitles with { Timing = SubtitleTiming.Delay(5000) }, duration)!;
        Assert.True(!disagreeing.Reliable && disagreeing.Detail.Contains("POZOR", StringComparison.Ordinal), disagreeing.Detail);
    }

    [Test]
    public void FittingSubtitlesAndNothingToTell()
    {
        var speech = Dialogue(60, seed: 31);
        var fine = SubtitleSync.Align(speech, Audio(speech, TimeSpan.FromMinutes(60), seed: 2), "podľa zvuku", tryScales: false)!;
        Assert.True(fine.Reliable && fine.Timing.IsNone && fine.Detail.Contains("titulky sedia", StringComparison.Ordinal), fine.Detail);

        var silence = SpeechActivity.Parts(TimeSpan.FromMinutes(60)).Select(s => new ActivityWindow(s, Enumerable.Repeat((sbyte)-1, 15000).ToArray())).ToList();
        var nothing = SubtitleSync.Align(speech, silence, "podľa zvuku")!;
        Assert.True(!nothing.Reliable, "a silent film tells nothing: " + nothing.Detail);
    }

    [Test]
    public void SpeechIsFoundInSamples()
    {
        // 8 kHz: a quiet hum, and a loud tone from 1 to 3 s and from 4,5 to 5 s
        var samples = new short[8000 * 6];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / 8000.0;
            var loud = t is >= 1 and < 3 or >= 4.5 and < 5;
            samples[i] = (short)((loud ? 9000 : 150) * Math.Sin(2 * Math.PI * 440 * t));
        }

        var values = SpeechActivity.FromSamples(samples);
        Assert.True(values.Length == 600 && values[150] == 1 && values[250] == 1 && values[470] == 1 && values[50] == -1 && values[380] == -1 && values[560] == -1,
            "speech where it is loud: " + string.Concat(values.Where((_, i) => i % 10 == 0).Select(v => v > 0 ? '#' : '.')));
    }

    [Test]
    public async Task RealAudioThroughFFmpeg()
    {
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            Assert.Skip("FFmpeg not on PATH");
        }

        // a tone in irregular seconds for 10 minutes; the subtitles are the same seconds, 2 s late
        bool Speaks(int second) => ((second * second * 7) + 3) % 97 < 35;
        var folder = Directory.CreateTempSubdirectory("universum-sync-").FullName;
        try
        {
            var film = Path.Combine(folder, "film.mka");
            await new ProcessRunner().RunAsync(new ProcessRequest(ffmpeg!,
            [
                "-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=600",
                "-af", "volume='if(lt(mod(floor(t)*floor(t)*7+3,97),35),1,0.01)':eval=frame", "-c:a", "pcm_s16le", film,
            ]));
            var cues = Enumerable.Range(0, 600).Where(Speaks).Select(s => (T(s + 2), T(s + 3))).ToList();
            var windows = await SpeechActivity.ReadAsync(new ProcessRunner(), ffmpeg!, film, 0, SpeechActivity.Parts(TimeSpan.FromMinutes(10), 3, 2), TimeSpan.FromMinutes(2));
            var result = SubtitleSync.Align(cues, windows, "podľa zvuku")!;
            Assert.True(windows.Count == 3 && result.Reliable && Math.Abs((result.Timing.Apply(T(302)) - T(300)).TotalMilliseconds) <= 60, result.Detail);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void WebVttReleasesAndFrameRates()
    {
        var document = new SubtitleDocument([new SubtitleCue(T(1), T(2.5), "<i>Tom & Jerry</i>\n2 < 3 --> koniec")], "SRT");
        var vtt = VttWriter.Write(document);
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\n<i>Tom &amp; Jerry</i>\n2 &lt; 3 --&gt; koniec\n\n", vtt, "WebVTT with entities, tags kept");
        Assert.True(SubtitleReader.Read(vtt).Cues is [{ Text: "<i>Tom & Jerry</i>\n2 < 3 --> koniec" }], "read back: " + SubtitleReader.Read(vtt).Cues[0].Text);

        var video = "Vymysleny.Film.2026.1080p.WEB-DL.x264-SKUPINA.mkv";
        Assert.True(ReleaseMatch.Score("Vymysleny.Film.2026.1080p.WEB-DL.x264-SKUPINA", video) > 0.9, "the same release");
        Assert.True(ReleaseMatch.Score("Vymysleny.Film.2026.720p.BluRay.x264-INA", video) < ReleaseMatch.Score("Vymysleny.Film.2026.1080p.WEBRip.x264-INA", video), "closer release scores higher");
        Assert.Equal("FPS sedí", ReleaseMatch.FpsNote(23.976, 24000.0 / 1001), "same frame rate");
        Assert.True(ReleaseMatch.FpsNote(25, 23.976).StartsWith("FPS 25 ≠ film 23,976", StringComparison.Ordinal) && ReleaseMatch.FpsNote(null, 25) == "FPS neuvedené", "different, unknown");
    }
}
