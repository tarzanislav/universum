using Universum.Core.Audio;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;
using Universum.Core.Subtitles;

namespace Universum.Tests;

/// <summary>Úprava audiostopy (owner, 30. 10. 2026): a sound written anew – speed, codec, stereo, loudness.</summary>
public sealed class AudioEncodingTests
{
    private static readonly FrameRate Pal = FrameRate.All.Single(r => r.Label == "25");
    private static readonly FrameRate Film = FrameRate.All.Single(r => r.Label == "23,976");

    [Test]
    public void RecipeInWords()
    {
        var ac3 = new MediaTrack { Id = 1, Type = TrackType.Audio, CodecId = "A_AC3", Codec = "AC-3", AudioChannels = 6, AudioSamplingFrequency = 48000 };
        var dts = ac3 with { CodecId = "A_DTS", Codec = "DTS" };
        Assert.True(!AudioRecipe.None.NeedsEncoding && !new AudioRecipe(SubtitleTiming.Delay(500)).NeedsEncoding, "a shift alone: mkvmerge");
        var pal = new AudioRecipe(SubtitleTiming.Fps(Pal, Film) with { DelayMilliseconds = -120 });
        Assert.True(pal.NeedsEncoding && pal.Shift == SubtitleTiming.Delay(-120) && pal.BitrateFor(ac3) == 640 && pal.Problem(ac3) is null, "PAL → film: AC-3 again, 640 kbit/s");
        Assert.True(pal.Problem(dts) is { Length: > 0 } && (pal with { Codec = AudioCodec.Flac }).Problem(dts) is null, "DTS cannot be written: a codec is chosen");
        Assert.Equal("atempo=0.959040959", string.Join(";", AudioEncoder.SpeedFilters(pal, 48000)), "the tempo keeps the pitch");
        Assert.Equal("asetrate=46033.96603,aresample=48000", string.Join(";", AudioEncoder.SpeedFilters(pal with { KeepPitch = false }, 48000)), "the pitch changes with the speed");
        var described = (pal with { Stereo = true, Normalize = true }).Describe(ac3);
        Assert.True(described.Contains("výška tónu ostane", StringComparison.Ordinal) && described.Contains("AC-3 224 kbit/s", StringComparison.Ordinal) &&
                    described.Contains("stereo", StringComparison.Ordinal) && described.Contains("−23 LUFS", StringComparison.Ordinal), described);
        Assert.True(AudioEncoder.SecondPass("x\n{\n\"input_i\" : \"-27.61\",\n\"input_tp\" : \"-4.47\",\n\"input_lra\" : \"18.06\",\n\"input_thresh\" : \"-39.20\",\n\"target_offset\" : \"0.58\"\n}") ==
                    "loudnorm=I=-23:TP=-1:LRA=20:measured_I=-27.61:measured_TP=-4.47:measured_LRA=18.06:measured_thresh=-39.2:offset=0.58:linear=true" &&
                    AudioEncoder.SecondPass("{ \"input_i\" : \"-inf\" }") is null, "the second pass of the loudness");
    }

    [Test]
    public async Task SoundWrittenAnew()
    {
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            Assert.Skip("FFmpeg not on PATH");
        }

        var installation = await TestData.RequireMkvToolNixAsync();
        var folder = Directory.CreateTempSubdirectory("universum-audio-").FullName;
        try
        {
            // ten seconds of a 5.1 tone; written 25 → 23,976 into stereo AAC at −23 LUFS
            var source = Path.Combine(folder, "zvuk.mka");
            var made = await new ProcessRunner().RunAsync(new ProcessRequest(ffmpeg!,
                ["-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=10:sample_rate=48000",
                 "-af", "volume=-12dB,pan=5.1|FL=c0|FR=c0|FC=c0|LFE=c0|BL=c0|BR=c0", "-c:a", "ac3", "-b:a", "384k", source]));
            Assert.True(made.ExitCode == 0, made.StandardError);
            var inspector = new MkvMergeInspector(installation, new ProcessRunner());
            var track = (await inspector.InspectAsync(source)).TracksOf(TrackType.Audio).Single();
            var recipe = new AudioRecipe(SubtitleTiming.Fps(Pal, Film), Codec: AudioCodec.Aac, Stereo: true, Normalize: true);
            var output = Path.Combine(folder, "nový.mka");
            var shares = new List<double>();
            var problem = await AudioEncoder.EncodeAsync(new ProcessRunner(), ffmpeg!, source, 0, track, recipe, output, TimeSpan.FromSeconds(10), new SyncProgress(shares));
            Assert.True(problem is null, problem ?? "");
            var written = await inspector.InspectAsync(output);
            var audio = written.TracksOf(TrackType.Audio).Single();
            Assert.True(audio is { CodecId: "A_AAC", AudioChannels: 2 } && written.Duration is { } length && Math.Abs(length.TotalSeconds - 10.43) < 0.15,
                $"stereo AAC, 4,27 % longer – {audio.CodecId} {audio.AudioChannels} {written.Duration}");
            Assert.True(shares.Count > 0 && shares[^1] == 1 && shares.All(s => s is >= 0 and <= 1), "progress from 0 to 1");

            // the loudness measured again: about −23 LUFS
            var measured = await new ProcessRunner().RunAsync(new ProcessRequest(ffmpeg!,
                ["-nostdin", "-hide_banner", "-i", output, "-af", "ebur128", "-f", "null", "-"]));
            var integrated = measured.StandardError.Split('\n').Reverse().FirstOrDefault(l => l.TrimStart().StartsWith("I:", StringComparison.Ordinal));
            Assert.True(integrated is not null && double.TryParse(integrated.Trim()[2..].Replace("LUFS", "", StringComparison.Ordinal).Trim(), System.Globalization.CultureInfo.InvariantCulture, out var lufs) &&
                        Math.Abs(lufs + 23) < 1.5, "loudness about −23 LUFS – " + integrated);
            Assert.True(File.Exists(source), "the original stays");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // progress reported at once (Progress<T> posts to a context)
    private sealed class SyncProgress(List<double> shares) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (shares)
            {
                shares.Add(value);
            }
        }
    }
}
