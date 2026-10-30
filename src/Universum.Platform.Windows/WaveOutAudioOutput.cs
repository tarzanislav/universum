using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Universum.Core.Playback;

namespace Universum.Platform.Windows;

/// <summary>
/// The player's sound on the computer's sound device (Windows' own waveOut, no library): eight buffers of up to 100 ms
/// go round – one is filled while the others play – and the device's position of playing is the player's clock.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WaveOutAudioOutput : IAudioOutput
{
    private const int BufferCount = 8;
    private const int BufferBytes = Pcm.BytesPerSecond / 10;
    private const uint WaveMapper = uint.MaxValue;
    private const uint DoneFlag = 0x01;
    private const uint InQueueFlag = 0x10;
    private const uint TimeBytes = 0x04;
    private const uint TimeSamples = 0x02;

    private readonly nint _device;
    private readonly nint[] _headers = new nint[BufferCount];
    private readonly object _gate = new();
    private uint _lastPosition;
    private long _positionWraps;
    private bool _disposed;

    private unsafe WaveOutAudioOutput(nint device)
    {
        _device = device;
        for (var i = 0; i < BufferCount; i++)
        {
            var header = (WaveHeader*)NativeMemory.AllocZeroed((nuint)sizeof(WaveHeader));
            header->Data = (byte*)NativeMemory.AllocZeroed(BufferBytes);
            header->BufferLength = BufferBytes;
            _ = waveOutPrepareHeader(device, header, (uint)sizeof(WaveHeader));
            header->Flags |= DoneFlag;
            _headers[i] = (nint)header;
        }

        // nothing plays until the player says so
        _ = waveOutPause(device);
    }

    public double Volume { get; set; } = 1;

    public unsafe TimeSpan Played
    {
        get
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return TimeSpan.Zero;
                }

                var time = new MmTime { Type = TimeBytes };
                if (waveOutGetPosition(_device, &time, (uint)sizeof(MmTime)) != 0)
                {
                    return Pcm.Duration((_positionWraps << 32) + _lastPosition);
                }

                var bytes = time.Type == TimeSamples ? time.Value * (uint)Pcm.BytesPerFrame : time.Value;
                // the device counts in 32 bits (about six hours of sound)
                if (bytes < _lastPosition && _lastPosition - bytes > uint.MaxValue / 2)
                {
                    _positionWraps++;
                }

                _lastPosition = bytes;
                return Pcm.Duration((_positionWraps << 32) + bytes);
            }
        }
    }

    /// <summary>The default sound device, or null when the computer has none (the player then runs by a clock).</summary>
    public static unsafe WaveOutAudioOutput? TryOpen()
    {
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = Pcm.Channels,
            SamplesPerSecond = Pcm.SampleRate,
            AverageBytesPerSecond = Pcm.BytesPerSecond,
            BlockAlign = Pcm.BytesPerFrame,
            BitsPerSample = 16,
        };
        try
        {
            return waveOutOpen(out var device, WaveMapper, &format, 0, 0, 0) == 0 ? new WaveOutAudioOutput(device) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        while (pcm.Length > 0)
        {
            var index = await FreeBufferAsync(cancellationToken).ConfigureAwait(false);
            var length = Math.Min(pcm.Length, BufferBytes);
            if (!Queue(index, pcm.Span[..length]))
            {
                return;
            }

            pcm = pcm[length..];
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _ = waveOutPause(_device);
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _ = waveOutRestart(_device);
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // every buffer comes back, the position starts at 0; paused until the player plays again
            _ = waveOutReset(_device);
            _ = waveOutPause(_device);
            _lastPosition = 0;
            _positionWraps = 0;
        }
    }

    public unsafe void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = waveOutReset(_device);
            foreach (var pointer in _headers)
            {
                var header = (WaveHeader*)pointer;
                _ = waveOutUnprepareHeader(_device, header, (uint)sizeof(WaveHeader));
                NativeMemory.Free(header->Data);
                NativeMemory.Free(header);
            }

            _ = waveOutClose(_device);
        }
    }

    // the sound into a free buffer, louder or quieter, and to the device; false when the device is closed
    private unsafe bool Queue(int index, ReadOnlySpan<byte> pcm)
    {
        var header = (WaveHeader*)_headers[index];
        var target = new Span<byte>(header->Data, pcm.Length);
        pcm.CopyTo(target);
        Pcm.Scale(target, Volume);
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            header->BufferLength = (uint)pcm.Length;
            header->Flags &= ~DoneFlag;
            _ = waveOutWrite(_device, header, (uint)sizeof(WaveHeader));
            return true;
        }
    }

    // a buffer the device has played (or never got)
    private async ValueTask<int> FreeBufferAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            for (var i = 0; i < BufferCount; i++)
            {
                if (IsFree(_headers[i]))
                {
                    return i;
                }
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static unsafe bool IsFree(nint pointer)
    {
        var flags = Volatile.Read(ref ((WaveHeader*)pointer)->Flags);
        return (flags & InQueueFlag) == 0 && (flags & DoneFlag) != 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct WaveHeader
    {
        public byte* Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public WaveHeader* Next;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MmTime
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(4)]
        public uint Value;
    }

    [LibraryImport("winmm.dll")]
    private static unsafe partial uint waveOutOpen(out nint device, uint deviceId, WaveFormat* format, nint callback, nint instance, uint flags);

    [LibraryImport("winmm.dll")]
    private static unsafe partial uint waveOutPrepareHeader(nint device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll")]
    private static unsafe partial uint waveOutUnprepareHeader(nint device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll")]
    private static unsafe partial uint waveOutWrite(nint device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutPause(nint device);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutRestart(nint device);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutReset(nint device);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutClose(nint device);

    [LibraryImport("winmm.dll")]
    private static unsafe partial uint waveOutGetPosition(nint device, MmTime* time, uint size);
}
