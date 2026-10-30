using System.Globalization;
using System.Threading.Channels;
using Universum.Core.Processes;

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
/// time – limited to the sound written, so the clock stops where the sound of the film ends.
/// </summary>
public sealed class ClockAudioOutput : IAudioOutput
{
    private readonly System.Diagnostics.Stopwatch _watch = new();
    private readonly object _gate = new();
    private long _written;
    private bool _limited;

    public double Volume { get; set; } = 1;

    public TimeSpan Played
    {
        get
        {
            lock (_gate)
            {
                var elapsed = _watch.Elapsed;
                return _limited && elapsed > Pcm.Duration(_written) ? Pcm.Duration(_written) : elapsed;
            }
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _written += pcm.Length;
            _limited = true;
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
            _limited = false;
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

    /// <summary>Starts at <paramref name="fileTime"/> (from the start of the file) with the audio track <paramref name="ordinal"/> (0 = the first).</summary>
    public static AudioReader Start(string ffmpeg, string path, TimeSpan fileTime, int ordinal)
    {
        var process = StreamingProcess.Start(ffmpeg,
        [
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-ss", VideoReader.Seconds(fileTime), "-i", path,
            "-map", "0:a:" + ordinal.ToString(CultureInfo.InvariantCulture), "-vn", "-sn", "-dn",
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
            _blocks.Writer.TryComplete();
        }
    }
}
