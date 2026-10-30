using Universum.Core.Audio;
using Universum.Core.Processes;

namespace Universum.Tests;

/// <summary>
/// Automatické napasovanie zvuku (owner, 30. 10. 2026): an invented film – shared music and effects, its own speech –
/// and another sound of it with other speech, moved and from a PAL release.
/// </summary>
public sealed class AudioSyncTests
{
    private const int Rate = AudioEnvelope.SampleRate;

    [Test]
    public void DubMovedOnly()
    {
        var events = Effects(seed: 7, minutes: 8);
        var film = Render(events, t => t, Speech(seed: 1, minutes: 8), 8 * 60);
        var dub = Render(events, t => t + 1.5, Speech(seed: 2, minutes: 8), (8 * 60) + 2);
        var result = AudioSync.Align(AudioEnvelope.FromSamples(film), AudioEnvelope.FromSamples(dub), 25);
        Assert.True(result is { Reliable: true, Rates: null } && result.Timing.Numerator == result.Timing.Denominator && Math.Abs(result.Timing.DelayMilliseconds + 1500) <= 20,
            "the dub plays 1,5 s earlier – " + result?.Detail);
    }

    [Test]
    public void DubOfAPalRelease()
    {
        // the PAL release runs 25 / 23,976 faster and starts with 3,21 s of its own: film time = (other − 3,21) × 25 / 23,976
        const double pal = 23.976 / 25;
        var events = Effects(seed: 11, minutes: 10);
        var film = Render(events, t => t, Speech(seed: 3, minutes: 10), 10 * 60);
        var dub = Render(events, t => (t * pal) + 3.21, Speech(seed: 4, minutes: 10), (10 * 60 * pal) + 4, stretch: pal);
        var result = AudioSync.Align(AudioEnvelope.FromSamples(film), AudioEnvelope.FromSamples(dub), 24000 / 1001.0);
        var expected = -3.21 * (25 / (24000 / 1001.0)) * 1000;
        Assert.True(result is { Reliable: true, Rates: { From.Label: "25", To.Label: "23,976" } } && Math.Abs(result.Timing.DelayMilliseconds - expected) <= 25,
            $"25 → 23,976 and the shift ({expected:0} ms) – {result?.Detail}");
    }

    [Test]
    public void UnrelatedSoundIsNotReliable()
    {
        var film = Render(Effects(seed: 21, minutes: 6), t => t, Speech(seed: 5, minutes: 6), 6 * 60);
        var other = Render(Effects(seed: 22, minutes: 6), t => t, Speech(seed: 6, minutes: 6), 6 * 60);
        var result = AudioSync.Align(AudioEnvelope.FromSamples(film), AudioEnvelope.FromSamples(other), 25);
        Assert.True(result is null or { Reliable: false }, "another film's sound fits nowhere – " + result?.Detail);
        Assert.True(AudioSync.Align(AudioEnvelope.FromSamples(new short[Rate * 5]), AudioEnvelope.FromSamples(other), 25) is null, "silence");
    }

    [Test]
    public async Task DubReadByFfmpeg()
    {
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            Assert.Skip("FFmpeg not on PATH");
        }

        var folder = Directory.CreateTempSubdirectory("universum-audiosync-").FullName;
        try
        {
            // the film as a WAV file; the dub in AC-3 in Matroska, 2,5 s later (FFmpeg moves it)
            var events = Effects(seed: 31, minutes: 4);
            var film = Path.Combine(folder, "film.wav");
            await File.WriteAllBytesAsync(film, Wav(Render(events, t => t, Speech(seed: 8, minutes: 4), 4 * 60)));
            var plain = Path.Combine(folder, "dabing.wav");
            await File.WriteAllBytesAsync(plain, Wav(Render(events, t => t, Speech(seed: 9, minutes: 4), 4 * 60)));
            var dub = Path.Combine(folder, "dabing.mka");
            var made = await new ProcessRunner().RunAsync(new ProcessRequest(ffmpeg!,
                ["-nostdin", "-hide_banner", "-loglevel", "error", "-i", plain, "-af", "adelay=2500:all=1", "-c:a", "ac3", "-b:a", "192k", dub]));
            Assert.True(made.ExitCode == 0, made.StandardError);

            var (filmEnvelope, filmProblem) = await AudioEnvelope.ReadAsync(ffmpeg!, film, 0, TimeSpan.FromMinutes(4));
            var (dubEnvelope, _) = await AudioEnvelope.ReadAsync(ffmpeg!, dub, 0, TimeSpan.FromMinutes(4));
            Assert.True(filmEnvelope is not null && dubEnvelope is not null && Math.Abs(filmEnvelope.Length.TotalSeconds - 240) < 0.1, "both read – " + filmProblem);
            var result = AudioSync.Align(filmEnvelope!, dubEnvelope!, 25);
            Assert.True(result is { Reliable: true } && result.Timing.Numerator == result.Timing.Denominator && Math.Abs(result.Timing.DelayMilliseconds + 2500) <= 40,
                "the dub 2,5 s earlier – " + result?.Detail);

            var (missing, problem) = await AudioEnvelope.ReadAsync(ffmpeg!, Path.Combine(folder, "nie je.mka"), 0, null);
            Assert.True(missing is null && problem is { Length: > 0 }, "a missing file: FFmpeg's message");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // music and effects: blows, hits and swells at random times (seconds), amplitudes, lengths and bands
    private static List<(double Time, double Amplitude, double Length, bool Low)> Effects(int seed, int minutes)
    {
        var random = new Random(seed);
        var events = new List<(double, double, double, bool)>();
        for (var t = 0.5; t < minutes * 60; t += 0.3 + (random.NextDouble() * 2.5))
        {
            events.Add((t, 0.05 + (random.NextDouble() * 0.5), 0.05 + (random.NextDouble() * 0.6), random.Next(2) == 0));
        }

        return events;
    }

    // speech: syllables of band noise, most of the time
    private static List<(double Time, double Amplitude, double Length, bool Low)> Speech(int seed, int minutes)
    {
        var random = new Random(seed);
        var syllables = new List<(double, double, double, bool)>();
        for (var t = 0.2; t < minutes * 60; t += 0.15 + (random.NextDouble() * (random.Next(6) == 0 ? 3 : 0.3)))
        {
            syllables.Add((t, 0.05 + (random.NextDouble() * 0.25), 0.08 + (random.NextDouble() * 0.2), false));
        }

        return syllables;
    }

    // the sound: shared events at their times in this version (stretched lengths), this version's speech, some noise
    private static short[] Render(List<(double Time, double Amplitude, double Length, bool Low)> effects, Func<double, double> at,
        List<(double Time, double Amplitude, double Length, bool Low)> speech, double seconds, double stretch = 1)
    {
        var mix = new float[(int)(seconds * Rate)];
        var noise = new Random(99);
        foreach (var (time, amplitude, length, low) in effects)
        {
            Add(mix, at(time), amplitude, length * stretch, low, new Random((int)(time * 1000)));
        }

        foreach (var (time, amplitude, length, _) in speech)
        {
            Add(mix, time, amplitude, length, low: false, noise);
        }

        var samples = new short[mix.Length];
        for (var i = 0; i < mix.Length; i++)
        {
            samples[i] = (short)Math.Clamp((mix[i] + ((noise.NextDouble() - 0.5) * 0.004)) * 32767, short.MinValue, short.MaxValue);
        }

        return samples;
    }

    private static void Add(float[] mix, double time, double amplitude, double length, bool low, Random random)
    {
        var start = (int)(time * Rate);
        var count = (int)(length * Rate);
        var filtered = 0.0;
        for (var i = 0; i < count && start + i < mix.Length; i++)
        {
            if (start + i < 0)
            {
                continue;
            }

            var white = (random.NextDouble() * 2) - 1;
            filtered += (low ? 0.05 : 0.6) * (white - filtered);
            var value = low ? filtered * 4 : white - filtered;
            mix[start + i] += (float)(amplitude * value * Math.Exp(-3.0 * i / count));
        }
    }

    private static byte[] Wav(short[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples.Length * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
