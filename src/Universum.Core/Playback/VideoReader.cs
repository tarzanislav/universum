using System.Globalization;
using System.Threading.Channels;
using Universum.Core.Processes;
using Universum.Core.Text;

namespace Universum.Core.Playback;

/// <summary>
/// The pictures the player shows: always <see cref="Width"/> × <see cref="Height"/> (the film fitted in with black bars,
/// by its display aspect), <see cref="FramesPerSecond"/> pictures a second.
/// </summary>
public sealed record VideoFormat(int Width, int Height, double FramesPerSecond)
{
    /// <summary>1280 × 720 at the film's frame rate (25 when it is not known; 50 or 60 pictures a second are halved for the preview).</summary>
    public static VideoFormat For(double? framesPerSecond, int width = 1280, int height = 720)
    {
        var fps = framesPerSecond is > 1 and < 300 ? framesPerSecond.Value : 25;
        while (fps > 30.5)
        {
            fps /= 2;
        }

        return new VideoFormat(width & ~1, height & ~1, fps);
    }

    /// <summary>Bytes of one picture (BGRA, four bytes a pixel).</summary>
    public int FrameBytes => Width * Height * 4;

    public TimeSpan FrameLength => TimeSpan.FromSeconds(1 / FramesPerSecond);
}

/// <summary>One decoded picture: BGRA pixels, shown from <see cref="Time"/> (the film's time).</summary>
public sealed class VideoFrame(VideoFormat format)
{
    public VideoFormat Format { get; } = format;

    public byte[] Pixels { get; } = new byte[format.FrameBytes];

    public int Stride => Format.Width * 4;

    public TimeSpan Time { get; internal set; }
}

/// <summary>
/// Decodes the pictures of a film from a time on (FFmpeg as a separate tool: it writes raw BGRA pictures to its output,
/// UNIVERSUM reads them). A few picture buffers go round: a buffer is filled again only after the player gave it back,
/// so FFmpeg waits while the player does not need more.
/// </summary>
public sealed class VideoReader : IAsyncDisposable
{
    private readonly StreamingProcess _process;
    private readonly Channel<VideoFrame> _frames;
    private readonly Channel<VideoFrame> _free;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reading;
    private int _read;

    private VideoReader(StreamingProcess process, VideoFormat format, TimeSpan first, int buffers)
    {
        _process = process;
        Format = format;
        _frames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(buffers) { SingleReader = true, SingleWriter = true });
        _free = Channel.CreateUnbounded<VideoFrame>();
        for (var i = 0; i < buffers; i++)
        {
            _free.Writer.TryWrite(new VideoFrame(format));
        }

        _reading = Task.Run(() => ReadAsync(first, _stop.Token));
    }

    public VideoFormat Format { get; }

    /// <summary>The pictures in order of time; completed when the film (or FFmpeg) ends.</summary>
    public ChannelReader<VideoFrame> Frames => _frames.Reader;

    /// <summary>FFmpeg ended without a single picture: its message.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Starts decoding at <paramref name="fileTime"/> (from the start of the file); the first picture is labelled
    /// <paramref name="time"/> (the film's time, with the file's own first timestamp).
    /// </summary>
    public static VideoReader Start(string ffmpeg, string path, TimeSpan fileTime, TimeSpan time, VideoFormat format, int buffers = 8)
    {
        var (w, h) = (format.Width, format.Height);
        var invariant = CultureInfo.InvariantCulture;
        // the film fitted into the box by its display aspect (dar: anamorphic pictures too), the rest black
        var filters = string.Create(invariant,
            $"fps={format.FramesPerSecond:0.######}," +
            $"scale=w='if(gt(dar,{w}/{h}),{w},trunc({h}*dar/2)*2)':h='if(gt(dar,{w}/{h}),trunc({w}/dar/2)*2,{h})':flags=bilinear," +
            $"setsar=1,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,format=bgra");
        var process = StreamingProcess.Start(ffmpeg,
        [
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-ss", Seconds(fileTime), "-i", path,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", filters, "-f", "rawvideo", "pipe:1",
        ]);
        return new VideoReader(process, format, time, buffers);
    }

    /// <summary>Gives a picture back to be filled again (after it was shown).</summary>
    public void Recycle(VideoFrame frame)
    {
        if (frame.Format == Format)
        {
            _free.Writer.TryWrite(frame);
        }
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

    internal static string Seconds(TimeSpan time) => Math.Max(0, time.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    private async Task ReadAsync(TimeSpan first, CancellationToken cancellationToken)
    {
        try
        {
            var output = _process.Output;
            while (true)
            {
                var frame = await _free.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await output.ReadExactlyAsync(frame.Pixels, cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    break;
                }

                frame.Time = first + TimeSpan.FromSeconds(_read++ / Format.FramesPerSecond);
                await _frames.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // FFmpeg was ended
        }
        finally
        {
            if (_read == 0 && !cancellationToken.IsCancellationRequested)
            {
                // let the error output arrive
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                Problem = _process.ErrorText is { Length: > 0 } error ? error.Split('\n')[^1].Trim() : CoreText.T("FFmpeg nevrátil žiadny obraz.");
            }

            _frames.Writer.TryComplete();
        }
    }
}
