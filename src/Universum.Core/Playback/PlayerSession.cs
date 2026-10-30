using Universum.Core.Media;

namespace Universum.Core.Playback;

/// <summary>What the player plays: a film and what the player needs to know of it.</summary>
/// <param name="Origin">The first timestamp of the file (a Matroska file may start later than 0; subtitles count from it).</param>
/// <param name="AudioTracks">How many audio tracks the file has (0 = none).</param>
public sealed record PlayerSource(string Path, TimeSpan Duration, TimeSpan Origin, bool HasVideo, double? FramesPerSecond, int AudioTracks)
{
    /// <summary>The source of a film read by MKVToolNix, with its first timestamp.</summary>
    public static PlayerSource Of(MediaFile file, TimeSpan origin) =>
        new(file.Path, file.Duration ?? TimeSpan.Zero, origin, file.TracksOf(TrackType.Video).Any(),
            file.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond, file.TracksOf(TrackType.Audio).Count());
}

/// <summary>
/// The player of UNIVERSUM (owner, 30. 10. 2026: own code, FFmpeg only as a separate decoder): pictures and sound are
/// decoded from the position on by two FFmpeg processes, the sound plays on the <see cref="IAudioOutput"/>, whose clock
/// is the clock of the film – each picture is shown when its time comes, late ones are skipped. A seek starts the
/// decoders again at the new time and plays only when the first picture is there.
/// </summary>
/// <remarks>Its methods are called from one thread (the window's); the decoders work on their own.</remarks>
public sealed class PlayerSession : IAsyncDisposable
{
    private readonly string _ffmpeg;
    private readonly IAudioOutput _audio;
    private VideoReader? _video;
    private AudioReader? _sound;
    private CancellationTokenSource? _feeding;
    private Task _fed = Task.CompletedTask;
    private VideoFrame? _current;
    private TimeSpan _start;
    private int _generation;

    public PlayerSession(string ffmpeg, PlayerSource source, IAudioOutput audio, VideoFormat? format = null)
    {
        _ffmpeg = ffmpeg;
        Source = source;
        // a film without sound runs by the clock alone
        if (source.AudioTracks == 0)
        {
            audio.Dispose();
            audio = new ClockAudioOutput();
        }

        _audio = audio;
        Format = format ?? VideoFormat.For(source.FramesPerSecond);
    }

    public PlayerSource Source { get; }

    public VideoFormat Format { get; }

    /// <summary>The audio track played (0 = the first).</summary>
    public int AudioTrack { get; private set; }

    public bool IsPlaying { get; private set; }

    /// <summary>Why pictures or sound are missing (FFmpeg's message), or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Where the film is now (the film's time, as subtitles count it).</summary>
    public TimeSpan Position
    {
        get
        {
            var position = _start + _audio.Played;
            var end = Source.Origin + Source.Duration;
            return Source.Duration > TimeSpan.Zero && position > end ? end : position;
        }
    }

    /// <summary>The film is at its end.</summary>
    public bool IsAtEnd => Source.Duration > TimeSpan.Zero && Position >= Source.Origin + Source.Duration - TimeSpan.FromMilliseconds(50);

    public double Volume { get => _audio.Volume; set => _audio.Volume = value; }

    /// <summary>
    /// Goes to <paramref name="time"/> (the film's time); playing goes on there when it played. Waits until the first
    /// picture (and some sound) is decoded – at most a few seconds.
    /// </summary>
    public async Task SeekAsync(TimeSpan time)
    {
        var generation = ++_generation;
        var end = Source.Origin + Source.Duration;
        time = time < Source.Origin ? Source.Origin : Source.Duration > TimeSpan.Zero && time > end ? end : time;
        await StopDecodingAsync().ConfigureAwait(true);
        if (generation != _generation)
        {
            return;
        }

        _audio.Reset();
        _start = time;
        _current = null;
        Problem = null;
        var fileTime = time - Source.Origin;
        _video = Source.HasVideo ? VideoReader.Start(_ffmpeg, Source.Path, fileTime, time, Format) : null;
        if (Source.AudioTracks > 0)
        {
            _sound = AudioReader.Start(_ffmpeg, Source.Path, fileTime, Math.Clamp(AudioTrack, 0, Source.AudioTracks - 1));
            _feeding = new CancellationTokenSource();
            var (sound, token) = (_sound, _feeding.Token);
            _fed = Task.Run(() => FeedAsync(sound, token), CancellationToken.None);
        }

        // both decoders start together; the clock runs only when the first picture can be shown
        if (_video is { } video)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await video.Frames.WaitToReadAsync(limit.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // slow: it plays as the pictures come
            }

            // no picture at all – not at the very end of the film, where there is none to show
            if (generation == _generation && !video.Frames.TryPeek(out _) && video.Frames.Completion.IsCompleted &&
                (Source.Duration <= TimeSpan.Zero || time < end - TimeSpan.FromSeconds(1)))
            {
                Problem = video.Problem;
            }
        }

        if (generation == _generation && IsPlaying)
        {
            _audio.Resume();
        }
    }

    /// <summary>Plays from the position (from the start again at the end).</summary>
    public async Task PlayAsync()
    {
        if (IsAtEnd)
        {
            IsPlaying = true;
            await SeekAsync(Source.Origin).ConfigureAwait(true);
            return;
        }

        IsPlaying = true;
        if (_video is null && _sound is null)
        {
            await SeekAsync(Position).ConfigureAwait(true);
            return;
        }

        _audio.Resume();
    }

    public void Pause()
    {
        IsPlaying = false;
        _audio.Pause();
    }

    /// <summary>Plays another audio track of the film from the position.</summary>
    public async Task SetAudioTrackAsync(int ordinal)
    {
        AudioTrack = Math.Clamp(ordinal, 0, Math.Max(0, Source.AudioTracks - 1));
        await SeekAsync(Position).ConfigureAwait(true);
    }

    /// <summary>
    /// The picture to show at <paramref name="time"/>: the latest one decoded that is due (the ones before it are given
    /// back); null before the first. It stays valid until the next call.
    /// </summary>
    public VideoFrame? FrameFor(TimeSpan time)
    {
        if (_video is not { } video)
        {
            return _current;
        }

        var due = time + (Format.FrameLength / 2);
        while (video.Frames.TryPeek(out var next) && next.Time <= due && video.Frames.TryRead(out next))
        {
            if (_current is not null)
            {
                video.Recycle(_current);
            }

            _current = next;
        }

        return _current;
    }

    public async ValueTask DisposeAsync()
    {
        _generation++;
        await StopDecodingAsync().ConfigureAwait(false);
        _audio.Dispose();
    }

    private async Task StopDecodingAsync()
    {
        if (_feeding is { } feeding)
        {
            await feeding.CancelAsync().ConfigureAwait(true);
        }

        var video = _video;
        var sound = _sound;
        _video = null;
        _sound = null;
        if (video is not null)
        {
            await video.DisposeAsync().ConfigureAwait(true);
        }

        if (sound is not null)
        {
            await sound.DisposeAsync().ConfigureAwait(true);
        }

        try
        {
            await _fed.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }

        _feeding?.Dispose();
        _feeding = null;
    }

    private async Task FeedAsync(AudioReader sound, CancellationToken cancellationToken)
    {
        await foreach (var block in sound.Blocks.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await _audio.WriteAsync(block, cancellationToken).ConfigureAwait(false);
        }
    }
}
