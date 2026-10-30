using Universum.Core.Playback;
using Universum.Core.Processes;
using Universum.Core.Subtitles;

namespace Universum.Tests;

/// <summary>The player of UNIVERSUM (owner, 30. 10. 2026): FFmpeg decodes, UNIVERSUM times the pictures and the sound.</summary>
public sealed class PlayerTests
{
    private static TimeSpan T(double seconds) => TimeSpan.FromSeconds(seconds);

    [Test]
    public void SubtitlesAtATime()
    {
        var timeline = new SubtitleTimeline(
        [
            new SubtitleCue(T(10), T(12), "prvý"), new SubtitleCue(T(3), T(5), "skôr"), new SubtitleCue(T(11), T(40), "dlhý"),
            new SubtitleCue(T(50), T(52), "posledný"), new SubtitleCue(T(60), T(60), "prázdny čas"),
        ]);
        Assert.Equal("skôr,prvý,dlhý,posledný", string.Join(",", timeline.Cues.Select(c => c.Text)), "in order of time, without empty times");
        Assert.Equal("prvý,dlhý", string.Join(",", timeline.At(T(11.5)).Select(c => c.Text)), "overlapping subtitles both shown");
        Assert.Equal("dlhý", string.Join(",", timeline.At(T(30)).Select(c => c.Text)), "a long subtitle found far after its start");
        Assert.True(timeline.At(T(45)).Count == 0 && timeline.At(T(2)).Count == 0 && timeline.At(T(52)).Count == 0, "nothing between, before, at the end");
        Assert.True(timeline.Next(T(12))?.Text == "posledný" && timeline.Previous(T(10.1))?.Text == "skôr" && timeline.Previous(T(11))?.Text == "prvý",
            "stepping to the next and back");
        Assert.True(timeline.Nearest(T(9))?.Text == "prvý" && timeline.Nearest(T(6))?.Text == "skôr" && SubtitleTimeline.Empty.Nearest(T(1)) is null, "the nearest start");
    }

    [Test]
    public async Task PicturesAndSoundOfAFilm()
    {
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            Assert.Skip("FFmpeg not on PATH");
        }

        var folder = Directory.CreateTempSubdirectory("universum-player-").FullName;
        try
        {
            // six seconds, 4:3, 25 pictures a second, each brighter than the one before; a tone
            var film = Path.Combine(folder, "film.mkv");
            await new ProcessRunner().RunAsync(new ProcessRequest(ffmpeg!,
            [
                "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=25:d=6,format=yuv420p,geq=lum='16+mod(N\\,70)*3':cb=128:cr=128",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=6", "-c:v", "libx264", "-g", "25", "-c:a", "aac", "-shortest", film,
            ]));
            var audio = new CountingAudioOutput();
            await using var session = new PlayerSession(ffmpeg!, new PlayerSource(film, T(6), TimeSpan.Zero, true, 25, 1), audio, VideoFormat.For(25, 640, 360));

            await session.SeekAsync(T(2));
            var first = session.FrameFor(session.Position);
            Assert.True(first is { Time.TotalMilliseconds: 2000 } && session.Problem is null, $"the first picture after the seek: {first?.Time} {session.Problem}");
            var center = Brightness(first!, 320, 180);
            Assert.True(Math.Abs(center - Full(16 + (50 * 3))) <= 12 && Brightness(first!, 20, 180) < 5, $"picture 50 in the middle ({center}), black bars at the sides (4:3 in 16:9)");

            // the clock moves on: the picture of 2,5 s, the ones before it given back
            audio.Played = T(0.5);
            var later = session.FrameFor(session.Position);
            for (var wait = 0; wait < 100 && later?.Time < T(2.47); wait++)
            {
                await Task.Delay(20);
                later = session.FrameFor(session.Position);
            }

            Assert.True(later is { Time.TotalMilliseconds: 2480 or 2520 or 2500 } && Math.Abs(Brightness(later, 320, 180) - Full(16 + (62 * 3))) <= 12,
                $"2,5 s: {later?.Time}, {Brightness(later!, 320, 180)}");
            for (var wait = 0; wait < 100 && audio.Bytes < Pcm.BytesPerSecond / 2; wait++)
            {
                await Task.Delay(20);
            }

            Assert.True(audio.Bytes >= Pcm.BytesPerSecond / 2, $"sound decoded for the output ({audio.Bytes} bytes)");

            // back, and a file that is not there
            await session.SeekAsync(T(1));
            Assert.True(session.FrameFor(session.Position) is { Time.TotalMilliseconds: 1000 } back && Math.Abs(Brightness(back, 320, 180) - Full(16 + (25 * 3))) <= 12, "seek back");
            await using var missing = new PlayerSession(ffmpeg!, new PlayerSource(Path.Combine(folder, "nie je.mkv"), T(6), TimeSpan.Zero, true, 25, 0), new CountingAudioOutput());
            await missing.SeekAsync(T(1));
            Assert.True(missing.Problem is { Length: > 0 } && missing.FrameFor(T(1)) is null, "a missing file: FFmpeg's message, no picture – " + missing.Problem);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        // limited range (16–235) shown as full range (0–255)
        static double Full(int luma) => (luma - 16) * 255.0 / 219;

        static double Brightness(VideoFrame frame, int x, int y)
        {
            var at = (y * frame.Stride) + (x * 4);
            return (frame.Pixels[at] + frame.Pixels[at + 1] + frame.Pixels[at + 2]) / 3.0;
        }
    }

    [Test]
    public async Task PlayingByTheClock()
    {
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            Assert.Skip("FFmpeg not on PATH");
        }

        // without a sound device: the clock runs with the real time, as far as the sound of the film goes
        await using var session = new PlayerSession(ffmpeg!, new PlayerSource(TestData.Media("en-und-cz-forced.mkv"), T(2.176), TimeSpan.Zero, true, 10, 2),
            new ClockAudioOutput());
        await session.SeekAsync(T(0.8));
        Assert.True(session.Position == T(0.8) && session.FrameFor(session.Position) is { Time.TotalMilliseconds: 800 }, "paused at the picture of 0,8 s");
        await session.PlayAsync();
        await Task.Delay(700);
        var position = session.Position;
        var frame = session.FrameFor(position);
        Assert.True(position > T(1.3) && position < T(1.8) && frame?.Time > T(1.2), $"playing: {position}, picture {frame?.Time}");
        await Task.Delay(1200);
        Assert.True(session.IsAtEnd && session.Position <= T(2.176), $"the end of the film: {session.Position}");
        await session.SetAudioTrackAsync(1);
        Assert.True(session.AudioTrack == 1 && session.Problem is null, "the second audio track");
    }

    [Test]
    public async Task SeeksThatOverlapAndSoundShorterThanThePicture()
    {
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            Assert.Skip("FFmpeg not on PATH");
        }

        var folder = Directory.CreateTempSubdirectory("universum-player-").FullName;
        try
        {
            // three seconds of picture, the sound starts 0,5 s late and ends after 2 s
            var film = Path.Combine(folder, "film.mkv");
            await new ProcessRunner().RunAsync(new ProcessRequest(ffmpeg!,
            [
                "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc=s=160x90:r=10:d=3", "-itsoffset", "0.5", "-f", "lavfi", "-i", "sine=duration=1.5",
                "-c:v", "libx264", "-g", "10", "-c:a", "aac", film,
            ]));
            var counted = new CountingAudioOutput();
            await using (var session = new PlayerSession(ffmpeg!, new PlayerSource(film, T(3), TimeSpan.Zero, true, 10, 1), counted))
            {
                // the delayed sound starts with silence: the clock stays with the picture
                await session.SeekAsync(TimeSpan.Zero);
                for (var wait = 0; wait < 100 && counted.Bytes < Pcm.BytesPerSecond * 29 / 10; wait++)
                {
                    await Task.Delay(20);
                }

                Assert.True(counted.Bytes >= Pcm.BytesPerSecond * 29 / 10, $"sound for the whole film, silence before and after it ({Pcm.Duration(counted.Bytes)})");

                // a second seek while the first goes on: both end, the second wins; Play meanwhile plays from there
                var first = session.SeekAsync(T(1));
                var second = session.SeekAsync(T(2));
                await session.PlayAsync();
                var both = Task.WhenAll(first, second);
                Assert.True(await Task.WhenAny(both, Task.Delay(5000)) == both, "the first seek does not wait for the second one's sound");
                Assert.True(session.Position == T(2) && session.FrameFor(session.Position) is { Time.TotalMilliseconds: 2000 } && session.IsPlaying,
                    $"at the second seek, playing: {session.Position}");
            }

            // without a sound device: the clock goes on through the silence to the end of the picture
            await using var clock = new PlayerSession(ffmpeg!, new PlayerSource(film, T(3), TimeSpan.Zero, true, 10, 1), new ClockAudioOutput());
            await clock.SeekAsync(T(1.5));
            await clock.PlayAsync();
            for (var wait = 0; wait < 150 && !clock.IsAtEnd; wait++)
            {
                await Task.Delay(20);
            }

            Assert.True(clock.IsAtEnd, $"the end of the film is reached ({clock.Position})");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // a clock the test moves; the sound is only counted
    private sealed class CountingAudioOutput : IAudioOutput
    {
        private long _bytes;

        public long Bytes => Interlocked.Read(ref _bytes);

        public TimeSpan Played { get; set; }

        public double Volume { get; set; } = 1;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref _bytes, pcm.Length);
            return ValueTask.CompletedTask;
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Reset() => Played = TimeSpan.Zero;

        public void Dispose()
        {
        }
    }
}
