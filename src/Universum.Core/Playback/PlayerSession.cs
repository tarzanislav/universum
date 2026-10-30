using Universum.Core.Media;

namespace Universum.Core.Playback;

/// <summary>What the player plays: a film and what the player needs to know of it.</summary>
/// <param name="Origin">The first timestamp of the file (a Matroska file may start later than 0; subtitles count from it).</param>
/// <param name="AudioTracks">How many audio tracks the file has (0 = none).</param>
public sealed record PlayerSource(string Path, TimeSpan Duration, TimeSpan Origin, bool HasVideo, double? FramesPerSecond, int AudioTracks)
{
    /// <summary>Width ÷ height of the picture as shown (the decoder fits it so; picture subtitles lie on it), or null.</summary>
    public double? DisplayAspect { get; init; }

    /// <summary>The source of a film read by MKVToolNix, with its first timestamp.</summary>
    public static PlayerSource Of(MediaFile file, TimeSpan origin) =>
        new(file.Path, file.Duration ?? TimeSpan.Zero, origin, file.TracksOf(TrackType.Video).Any(),
            file.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond, file.TracksOf(TrackType.Audio).Count())
        {
            DisplayAspect = file.TracksOf(TrackType.Video).FirstOrDefault()?.DisplayAspect,
        };
}

/// <summary>
/// A sound played instead of the film's own (Úprava audiostopy): an audio track (<paramref name="Ordinal"/>, 0 = the
/// first) of a file – the film itself or another one – moved and stretched as it will be written.
/// </summary>
public sealed record PlayerSound(string Path, int Ordinal, Subtitles.SubtitleTiming Timing, bool KeepPitch = true);

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
    private IAudioOutput _audio;
    private VideoReader? _video;
    private AudioReader? _sound;
    private CancellationTokenSource? _feeding;
    private Task _fed = Task.CompletedTask;
    private VideoFrame? _current;
    private TimeSpan _start;
    private TimeSpan? _target;
    private int _generation;
    private string? _problem;
    private volatile string? _soundProblem;
    private volatile bool _soundFailed;
    private bool _recovering;

    public PlayerSession(string ffmpeg, PlayerSource source, IAudioOutput audio, VideoFormat? format = null)
    {
        _ffmpeg = ffmpeg;
        Source = source;
        // a film without sound plays silence (a sound from elsewhere can be played with it)
        _audio = audio;
        Format = format ?? VideoFormat.For(source.FramesPerSecond);
    }

    public PlayerSource Source { get; }

    public VideoFormat Format { get; }

    /// <summary>The audio track played (0 = the first).</summary>
    public int AudioTrack { get; private set; }

    /// <summary>A sound played instead of the film's own audio track, or null.</summary>
    public PlayerSound? Sound { get; private set; }

    public bool IsPlaying { get; private set; }

    /// <summary>Why pictures or sound are missing (FFmpeg's message, the sound device), or null.</summary>
    public string? Problem => _problem ?? _soundProblem;

    /// <summary>Where the film is now (the film's time, as subtitles count it); during a seek where it goes.</summary>
    public TimeSpan Position
    {
        get
        {
            if (_target is { } target)
            {
                return target;
            }

            var position = _start + _audio.Played;
            return Source.Duration > TimeSpan.Zero && position > End ? End : position;
        }
    }

    /// <summary>The film is at its end.</summary>
    public bool IsAtEnd => Source.Duration > TimeSpan.Zero && Position >= End - TimeSpan.FromMilliseconds(50);

    public double Volume { get => _audio.Volume; set => _audio.Volume = value; }

    private TimeSpan End => Source.Origin + Source.Duration;

    /// <summary>
    /// Goes to <paramref name="time"/> (the film's time); playing goes on there when it played. Waits until the first
    /// picture (and some sound) is decoded – at most a few seconds. A newer seek wins over one still going on.
    /// </summary>
    public async Task SeekAsync(TimeSpan time)
    {
        var generation = ++_generation;
        time = time < Source.Origin ? Source.Origin : Source.Duration > TimeSpan.Zero && time > End ? End : time;
        _target = time;
        try
        {
            await StopDecodingAsync().ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            _audio.Reset();
            _start = time;
            _current = null;
            _problem = null;
            _soundProblem = null;
            var fileTime = time - Source.Origin;
            _video = Source.HasVideo ? VideoReader.Start(_ffmpeg, Source.Path, fileTime, time, Format) : null;
            {
                // a sound from elsewhere is placed by the film's time as mkvmerge writes it (from 0, not from the file's start)
                var sound = _sound = Sound is { } other ? AudioReader.Start(_ffmpeg, other.Path, time, other.Ordinal, other.Timing, other.KeepPitch)
                    : Source.AudioTracks > 0 ? AudioReader.Start(_ffmpeg, Source.Path, fileTime, Math.Clamp(AudioTrack, 0, Source.AudioTracks - 1))
                    : null;
                var feeding = _feeding = new CancellationTokenSource();
                var (output, length) = (_audio, Source.Duration > TimeSpan.Zero ? End - time : TimeSpan.Zero);
                _fed = Task.Run(() => FeedAsync(sound, output, length, feeding.Token), CancellationToken.None);
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
                    (Source.Duration <= TimeSpan.Zero || time < End - TimeSpan.FromSeconds(1)))
                {
                    _problem = video.Problem;
                }
            }

            if (generation == _generation)
            {
                _target = null;
                if (IsPlaying)
                {
                    _audio.Resume();
                }
            }
        }
        finally
        {
            if (generation == _generation)
            {
                _target = null;
            }
        }
    }

    /// <summary>Plays from the position (from the start again at the end).</summary>
    public async Task PlayAsync()
    {
        IsPlaying = true;
        if (_target is not null)
        {
            // the seek going on plays when its first picture is there
            return;
        }

        if (IsAtEnd)
        {
            await SeekAsync(Source.Origin).ConfigureAwait(true);
            return;
        }

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

    /// <summary>Plays another audio track of the film from the position (or from where a seek goes).</summary>
    public async Task SetAudioTrackAsync(int ordinal)
    {
        AudioTrack = Math.Clamp(ordinal, 0, Math.Max(0, Source.AudioTracks - 1));
        Sound = null;
        await SeekAsync(Position).ConfigureAwait(true);
    }

    /// <summary>Plays <paramref name="sound"/> instead of the film's audio track (null: the film's own again) from the position.</summary>
    public async Task SetSoundAsync(PlayerSound? sound)
    {
        if (sound == Sound)
        {
            return;
        }

        Sound = sound;
        await SeekAsync(Position).ConfigureAwait(true);
    }

    /// <summary>
    /// The picture to show at <paramref name="time"/>: the latest one decoded that is due (the ones before it are given
    /// back); null before the first. It stays valid until the next call. Called for every frame of the window.
    /// </summary>
    public VideoFrame? FrameFor(TimeSpan time)
    {
        if (_soundFailed && !_recovering)
        {
            _ = RecoverAsync();
        }

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
        // what this stop ends – a newer seek starts its own decoders meanwhile
        var (feeding, fed, video, sound) = (_feeding, _fed, _video, _sound);
        (_feeding, _fed, _video, _sound) = (null, Task.CompletedTask, null, null);
        if (feeding is not null)
        {
            await feeding.CancelAsync().ConfigureAwait(true);
        }

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
            await fed.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }

        feeding?.Dispose();
    }

    // the sound to the output (none: silence); when it ends before the picture, silence keeps the clock going to the end
    // of the film
    private async Task FeedAsync(AudioReader? sound, IAudioOutput output, TimeSpan length, CancellationToken cancellationToken)
    {
        try
        {
            long written = 0;
            if (sound is not null)
            {
                await foreach (var block in sound.Blocks.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await output.WriteAsync(block, cancellationToken).ConfigureAwait(false);
                    written += block.Length;
                }
            }

            if (written == 0 && sound?.Problem is { } problem)
            {
                _soundProblem = "zvuk: " + problem;
            }

            var silence = new byte[Pcm.BytesPerSecond / 20];
            for (var left = length - Pcm.Duration(written); left > TimeSpan.Zero; left -= Pcm.Duration(silence.Length))
            {
                await output.WriteAsync(silence, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            // the sound device stopped taking sound: the film goes on by a clock (FrameFor switches)
            _soundProblem = ex.Message;
            _soundFailed = true;
        }
    }

    private async Task RecoverAsync()
    {
        _recovering = true;
        _soundFailed = false;
        var at = Position;
        var failed = _audio;
        _audio = new ClockAudioOutput { Volume = failed.Volume };
        try
        {
            await SeekAsync(at).ConfigureAwait(true);
            failed.Dispose();
            _soundProblem = "Zvukové zariadenie neprijíma zvuk – film ide ďalej bez zvuku.";
        }
        finally
        {
            _recovering = false;
        }
    }
}
