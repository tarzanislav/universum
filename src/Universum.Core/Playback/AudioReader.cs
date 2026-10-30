using System.Globalization;
using System.Threading.Channels;
using Universum.Core.Processes;
using Universum.Core.Subtitles;

namespace Universum.Core.Playback;

/// <summary>Where the player's sound goes: Windows (the device of the computer), or only a clock when there is none.</summary>
/// <remarks>Sound is always 48 kHz, two channels, 16-bit little endian (<see cref="Pcm"/>).</remarks>
public interface IAudioOutput : IDisposable
{
    /// <summary>How long the sound written since the last <see cref="Reset"/> has played (the player's clock).</summary>
    TimeSpan Played { get; }

    /// <summary>Loudness 0–1 (the samples are scaled, Windows' own volume stays).</summary>
    double Volume { get; set; }

    /// <summary>Queues sound; waits while enough is queued (a fraction of a second) – also while paused.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken);

    /// <summary>Stops playing; the queued sound waits.</summary>
    void Pause();

    /// <summary>Plays (again).</summary>
    void Resume();

    /// <summary>Drops the queued sound and starts the clock from 0, paused (a seek).</summary>
    void Reset();
}

/// <summary>The format of the player's sound.</summary>
public static class Pcm
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BytesPerFrame = Channels * 2;
    public const int BytesPerSecond = SampleRate * BytesPerFrame;

    public static TimeSpan Duration(long bytes) => TimeSpan.FromTicks(bytes * TimeSpan.TicksPerSecond / BytesPerSecond);

    /// <summary>Scales 16-bit samples in place (the player's volume).</summary>
    public static void Scale(Span<byte> pcm, double volume)
    {
        if (volume >= 0.999)
        {
            return;
        }

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
        var factor = (int)(Math.Clamp(volume, 0, 1) * 65536);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)((samples[i] * factor) >> 16);
        }
    }
}

/// <summary>
/// Without a sound device (or for a film without sound): the sound is only counted and the clock runs with the real
/// time – as a device does, it stands while no sound is there (and where the sound of the film ends), and goes on
/// from there when sound comes.
/// </summary>
public sealed class ClockAudioOutput(bool limitedToSound = true) : IAudioOutput
{
    private readonly System.Diagnostics.Stopwatch _watch = new();
    private readonly object _gate = new();
    private readonly bool _limitedToSound = limitedToSound;
    private long _written;
    private TimeSpan _played;
    private TimeSpan _counted;

    public double Volume { get; set; } = 1;

    public TimeSpan Played
    {
        get
        {
            lock (_gate)
            {
                // the time since the last look, as far as there is sound for it
                var elapsed = _watch.Elapsed;
                _played += elapsed - _counted;
                _counted = elapsed;
                if (_limitedToSound && _played > Pcm.Duration(_written))
                {
                    _played = Pcm.Duration(_written);
                }

                return _played;
            }
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _written += pcm.Length;
        }

        while (Pcm.Duration(Interlocked.Read(ref _written)) - Played > TimeSpan.FromSeconds(0.5))
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _ = Played;
            _watch.Stop();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _watch.Start();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _watch.Reset();
            _written = 0;
            _played = TimeSpan.Zero;
            _counted = TimeSpan.Zero;
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Decodes the sound of one audio track of a film from a time on (FFmpeg as a separate tool) into <see cref="Pcm"/> blocks.</summary>
public sealed class AudioReader : IAsyncDisposable
{
    // 50 ms a block, a second ahead at most
    private const int BlockBytes = Pcm.BytesPerSecond / 20;

    private readonly StreamingProcess _process;
    private readonly Channel<byte[]> _blocks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(20) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reading;

    private AudioReader(StreamingProcess process)
    {
        _process = process;
        _reading = Task.Run(() => ReadAsync(_stop.Token));
    }

    /// <summary>The sound in order; completed when the track (or FFmpeg) ends.</summary>
    public ChannelReader<byte[]> Blocks => _blocks.Reader;

    /// <summary>FFmpeg ended without any sound: its message.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Starts at <paramref name="fileTime"/> (the film's time from the start of its file) with the audio track
    /// <paramref name="ordinal"/> (0 = the first) of <paramref name="path"/> – the film's own sound, or a sound from
    /// elsewhere moved and stretched by <paramref name="timing"/> as it will be written (Úprava audiostopy): the film's
    /// time T plays the sound's time (T − shift) ÷ factor, silence before the sound starts.
    /// </summary>
    public static AudioReader Start(string ffmpeg, string path, TimeSpan fileTime, int ordinal, SubtitleTiming? timing = null, bool keepPitch = true)
    {
        var invariant = CultureInfo.InvariantCulture;
        // sound that starts later than the seek (a delayed track) starts with silence: the clock stays with the picture
        var filters = new List<string> { string.Create(invariant, $"aresample={Pcm.SampleRate}:async=1:first_pts=0") };
        var seek = fileTime;
        if (timing is { IsNone: false } moved)
        {
            var source = (fileTime.TotalSeconds - (moved.DelayMilliseconds / 1000.0)) / moved.Factor;
            seek = TimeSpan.FromSeconds(Math.Max(0, source));
            if (moved.Numerator != moved.Denominator)
            {
                var tempo = 1 / moved.Factor;
                filters.Add(keepPitch ? string.Create(invariant, $"atempo={tempo:0.#########}")
                    : string.Create(invariant, $"asetrate={Pcm.SampleRate * tempo:0.#####},aresample={Pcm.SampleRate}"));
            }

            if (source < 0)
            {
                filters.Add(string.Create(invariant, $"adelay={-source * moved.Factor * 1000:0}:all=1"));
            }
        }

        var process = StreamingProcess.Start(ffmpeg,
        [
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-ss", VideoReader.Seconds(seek), "-i", path,
            "-map", "0:a:" + ordinal.ToString(invariant), "-vn", "-sn", "-dn",
            "-af", string.Join(",", filters),
            "-ac", Pcm.Channels.ToString(CultureInfo.InvariantCulture), "-ar", Pcm.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1",
        ]);
        return new AudioReader(process);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _reading.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }

        _stop.Dispose();
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        long total = 0;
        try
        {
            var output = _process.Output;
            while (true)
            {
                var block = new byte[BlockBytes];
                var read = await output.ReadAtLeastAsync(block, BlockBytes, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                read -= read % Pcm.BytesPerFrame;
                if (read == 0)
                {
                    break;
                }

                total += read;
                await _blocks.Writer.WriteAsync(read == BlockBytes ? block : block[..read], cancellationToken).ConfigureAwait(false);
                if (read < BlockBytes)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // FFmpeg was ended
        }
        finally
        {
            if (total == 0 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                Problem = _process.ErrorText is { Length: > 0 } error ? error.Split('\n')[^1].Trim() : null;
            }

            _blocks.Writer.TryComplete();
        }
    }
}
