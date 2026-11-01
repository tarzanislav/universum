using System.Buffers.Binary;
using System.Globalization;
using Universum.Core.Processes;
using Universum.Core.Text;

namespace Universum.Core.Audio;

/// <summary>
/// What two sounds of one film have in common – music and effects, also under a dub in another language – as a course
/// in time: how much the loudness rises (onsets) in a low band (drums, blows, explosions) and in the rest, 100 values a
/// second (owner, 30. 10. 2026: UNIVERSUM's own measure). Rises do not depend on how loud a mix is, so a quieter dub
/// matches its film.
/// </summary>
public sealed class AudioEnvelope(float[] values)
{
    /// <summary>Values a second.</summary>
    public const int Rate = 100;

    /// <summary>The sound is read at this rate (mono): enough for rises up to the speech band.</summary>
    public const int SampleRate = 8000;

    private const int FrameSamples = SampleRate / Rate;

    public float[] Values { get; } = values;

    public TimeSpan Length => TimeSpan.FromSeconds((double)Values.Length / Rate);

    /// <summary>
    /// The whole audio track <paramref name="ordinal"/> (0 = the first) of a file, from the start of the file (a later
    /// track starts with silence) – FFmpeg decodes, UNIVERSUM measures as the sound comes. Null with FFmpeg's message
    /// when there is no sound.
    /// </summary>
    public static async Task<(AudioEnvelope? Envelope, string? Problem)> ReadAsync(
        string ffmpeg, string path, int ordinal, TimeSpan? duration, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var process = StreamingProcess.Start(ffmpeg,
        [
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-i", path, "-map", "0:a:" + ordinal.ToString(CultureInfo.InvariantCulture), "-vn", "-sn", "-dn",
            "-af", "aresample=async=1:first_pts=0", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1",
        ]);
        var builder = new Builder();
        var buffer = new byte[1 << 16];
        var kept = 0;
        long samples = 0;
        var reported = 0L;
        try
        {
            while (true)
            {
                var read = await process.Output.ReadAsync(buffer.AsMemory(kept), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var whole = (kept + read) & ~1;
                builder.Add(buffer.AsSpan(0, whole));
                samples += whole / 2;
                kept = kept + read - whole;
                if (kept > 0)
                {
                    buffer[0] = buffer[whole];
                }

                if (progress is not null && duration is { TotalSeconds: > 0 } length && samples - reported >= SampleRate * 30)
                {
                    reported = samples;
                    progress.Report(Math.Min(1, samples / (double)SampleRate / length.TotalSeconds));
                }
            }
        }
        catch (IOException)
        {
            // FFmpeg ended: what came is measured
        }

        if (samples < SampleRate)
        {
            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            return (null, process.ErrorText is { Length: > 0 } error ? error.Split('\n')[^1].Trim() : CoreText.T("FFmpeg nevrátil žiadny zvuk."));
        }

        return (builder.Build(), null);
    }

    /// <summary>The envelope of samples (<see cref="SampleRate"/> a second, mono).</summary>
    public static AudioEnvelope FromSamples(ReadOnlySpan<short> samples)
    {
        var builder = new Builder();
        builder.Add(samples);
        return builder.Build();
    }

    /// <summary>The envelope at a lower rate (sums of <paramref name="factor"/> values): for the search over the whole film.</summary>
    public float[] Coarse(int factor)
    {
        var coarse = new float[Values.Length / factor];
        for (var i = 0; i < coarse.Length; i++)
        {
            var sum = 0f;
            for (var j = 0; j < factor; j++)
            {
                sum += Values[(i * factor) + j];
            }

            coarse[i] = sum;
        }

        return coarse;
    }

    // the measure as the samples come: 10 ms frames, the energy of each band in decibels (a floor of −60 dB, so the
    // noise of quiet places makes no rises), the rises summed
    private sealed class Builder
    {
        // a low-pass of one pole at about 300 Hz splits the bands
        private static readonly float Alpha = (float)(1 - Math.Exp(-2 * Math.PI * 300 / SampleRate));

        private readonly List<float> _values = [];
        private float _low;
        private double _lowEnergy;
        private double _highEnergy;
        private int _inFrame;
        private double _lastLow = double.NaN;
        private double _lastHigh = double.NaN;

        public void Add(ReadOnlySpan<byte> pcm)
        {
            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                Sample(BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]));
            }
        }

        public void Add(ReadOnlySpan<short> samples)
        {
            foreach (var sample in samples)
            {
                Sample(sample);
            }
        }

        public AudioEnvelope Build() => new([.. _values]);

        private void Sample(short sample)
        {
            var x = sample / 32768f;
            _low += Alpha * (x - _low);
            var high = x - _low;
            _lowEnergy += _low * _low;
            _highEnergy += high * high;
            if (++_inFrame < FrameSamples)
            {
                return;
            }

            var low = Decibels(_lowEnergy / FrameSamples);
            var rest = Decibels(_highEnergy / FrameSamples);
            _values.Add(double.IsNaN(_lastLow) ? 0 : (float)(Math.Max(0, low - _lastLow) + Math.Max(0, rest - _lastHigh)));
            (_lastLow, _lastHigh) = (low, rest);
            (_lowEnergy, _highEnergy, _inFrame) = (0, 0, 0);
        }

        private static double Decibels(double power) => 10 * Math.Log10(Math.Max(power, 1e-6));
    }
}
