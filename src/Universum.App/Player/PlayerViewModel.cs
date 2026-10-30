using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Media;
using Universum.Core.Playback;
using Universum.Core.Subtitles.Pictures;
using Universum.Core.Subtitles.Sync;
using Universum.Platform.Windows;

namespace Universum.App.Player;

/// <summary>
/// The player of the studios (owner, 30. 10. 2026: playing the film with the subtitles is the top priority): the
/// picture, the sound of a chosen audio track, the subtitles edited (bottom) and the ones compared with (top), all
/// drawn by UNIVERSUM. The window calls <see cref="Tick"/> for every frame it draws.
/// </summary>
public sealed class PlayerViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _seekDelay;
    private PlayerSession? _session;
    private WriteableBitmap? _picture;
    private VideoFrame? _shown;
    private TimeSpan _shownTime = TimeSpan.MinValue;
    private SubtitleTimeline _primary = SubtitleTimeline.Empty;
    private SubtitleTimeline _secondary = SubtitleTimeline.Empty;
    private PictureTimeline _primaryPictures = PictureTimeline.Empty;
    private PictureTimeline _secondaryPictures = PictureTimeline.Empty;
    private (PictureCue? Cue, ImageSource? Image, Rect Place) _primaryPicture;
    private (PictureCue? Cue, ImageSource? Image, Rect Place) _secondaryPicture;
    private string _primaryText = "";
    private string _secondaryText = "";
    private string _message = "Otvorte video – prehrá sa tu s titulkami.";
    private string _time = "";
    private double _position;
    private double _start;
    private double _end = 1;
    private double _volume = 0.8;
    private int _audioTrack;
    private bool _updatingPosition;
    private int _opening;
    private int _seeking;
    private TimeSpan _lastNow = TimeSpan.MinValue;

    public PlayerViewModel(AppServices services)
    {
        _services = services;
        _seekDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _seekDelay.Tick += (_, _) =>
        {
            _seekDelay.Stop();
            _ = SeekAsync(TimeSpan.FromSeconds(_position));
        };
        PlayPauseCommand = new AsyncRelayCommand(PlayPauseAsync, () => IsOpen);
        SeekByCommand = new AsyncRelayCommand<string>(by => double.TryParse(by, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? SeekAsync(Now + TimeSpan.FromSeconds(seconds)) : Task.CompletedTask, _ => IsOpen);
        PreviousSubtitleCommand = new AsyncRelayCommand(() => (_primary.Previous(Now)?.Start ?? _primaryPictures.Previous(Now)?.Start) is { } at ? SeekAsync(at) : Task.CompletedTask,
            () => IsOpen);
        NextSubtitleCommand = new AsyncRelayCommand(() => (_primary.Next(Now)?.Start ?? _primaryPictures.Next(Now)?.Start) is { } at ? SeekAsync(at) : Task.CompletedTask,
            () => IsOpen);
    }

    /// <summary>The picture (BGRA from FFmpeg, 1280 × 720 with black bars).</summary>
    public WriteableBitmap? Picture { get => _picture; private set => SetProperty(ref _picture, value); }

    public int FrameWidth => _session?.Format.Width ?? 1280;

    public int FrameHeight => _session?.Format.Height ?? 720;

    public bool IsOpen => _session is not null;

    public bool IsPlaying => _session?.IsPlaying == true;

    /// <summary>Segoe Fluent Icons: play or pause.</summary>
    public string PlayGlyph => IsPlaying ? "" : "";

    /// <summary>Where the film is (seconds; the slider – moving it goes there).</summary>
    public double Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value) && !_updatingPosition)
            {
                // the slider is dragged: go there when it rests for a moment
                _seekDelay.Stop();
                _seekDelay.Start();
            }
        }
    }

    public double Start { get => _start; private set => SetProperty(ref _start, value); }

    public double End { get => _end; private set => SetProperty(ref _end, value); }

    /// <summary>"0:12:34,5 / 1:45:00".</summary>
    public string Time { get => _time; private set => SetProperty(ref _time, value); }

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0, 1)) && _session is { } session)
            {
                session.Volume = _volume;
            }
        }
    }

    /// <summary>The audio tracks of the film ("1: čeština, AC-3 5.1").</summary>
    public IReadOnlyList<string> AudioTracks { get; private set; } = [];

    public int AudioTrack
    {
        get => _audioTrack;
        set
        {
            if (value >= 0 && SetProperty(ref _audioTrack, value) && _session is { } session && session.AudioTrack != value)
            {
                _ = session.SetAudioTrackAsync(value);
            }
        }
    }

    /// <summary>The subtitles edited, now (bottom of the picture).</summary>
    public string PrimaryText { get => _primaryText; private set => SetProperty(ref _primaryText, value); }

    /// <summary>The subtitles compared with, now (top of the picture).</summary>
    public string SecondaryText { get => _secondaryText; private set => SetProperty(ref _secondaryText, value); }

    /// <summary>Picture subtitles edited (PGS), now, and where they stand on the picture.</summary>
    public ImageSource? PrimaryPicture => _primaryPicture.Image;

    public Rect PrimaryPicturePlace => _primaryPicture.Place;

    /// <summary>Picture subtitles compared with, now.</summary>
    public ImageSource? SecondaryPicture => _secondaryPicture.Image;

    public Rect SecondaryPicturePlace => _secondaryPicture.Place;

    /// <summary>Over the picture: nothing open, FFmpeg's problem …</summary>
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>The time of the film now.</summary>
    public TimeSpan Now => _session?.Position ?? TimeSpan.FromSeconds(_position);

    /// <summary>The subtitles edited, as they are shown now.</summary>
    public SubtitleTimeline Primary => _primary;

    /// <summary>Pictures shown since the film was opened (the self-test).</summary>
    public int FramesShown { get; private set; }

    public AsyncRelayCommand PlayPauseCommand { get; }

    public AsyncRelayCommand<string> SeekByCommand { get; }

    public AsyncRelayCommand PreviousSubtitleCommand { get; }

    public AsyncRelayCommand NextSubtitleCommand { get; }

    /// <summary>Opens a film (paused at its start, the first picture shown); a newer open or Close wins.</summary>
    public async Task OpenAsync(MediaFile file)
    {
        await CloseAsync();
        var opening = ++_opening;
        Message = "Otváram video…";
        try
        {
            var ffmpeg = (await _services.SpeechAsync()).Ffmpeg;
            var origin = file.IsMatroska ? await SpeechActivity.StartAsync(_services.Runner, ffmpeg, file.Path) : TimeSpan.Zero;
            if (opening != _opening)
            {
                return;
            }

            // the computer's sound device; without one the film runs by a clock
            IAudioOutput audio = WaveOutAudioOutput.TryOpen() is { } device ? device : new ClockAudioOutput();
            var session = new PlayerSession(ffmpeg, PlayerSource.Of(file, origin), audio) { Volume = _volume };
            _session = session;
            Picture = new WriteableBitmap(session.Format.Width, session.Format.Height, 96, 96, PixelFormats.Bgr32, null);
            AudioTracks = file.TracksOf(TrackType.Audio).Select((t, i) => $"{i + 1}: {Pages.Texts.Language(t.Language)}, {t.Codec}" +
                                                                         (t.AudioChannels is { } channels ? $" {channels} k." : "")).ToList();
            _audioTrack = Math.Max(0, file.TracksOf(TrackType.Audio).ToList().FindIndex(t => t.Flags.Default));
            OnPropertyChanged(nameof(AudioTracks));
            OnPropertyChanged(nameof(AudioTrack));
            // the slider's range changes: its value is the film's start, not a seek
            _updatingPosition = true;
            Start = origin.TotalSeconds;
            End = (origin + (file.Duration ?? TimeSpan.FromSeconds(1))).TotalSeconds;
            Position = origin.TotalSeconds;
            _updatingPosition = false;
            OnPropertyChanged(nameof(FrameWidth));
            OnPropertyChanged(nameof(FrameHeight));
            OnPropertyChanged(nameof(IsOpen));
            Refresh();
            _seeking++;
            try
            {
                if (_audioTrack > 0)
                {
                    await session.SetAudioTrackAsync(_audioTrack);
                }
                else
                {
                    await session.SeekAsync(origin);
                }
            }
            finally
            {
                _seeking--;
            }

            if (opening == _opening)
            {
                Message = !session.Source.HasVideo ? "Súbor nemá obraz – hrá sa zvuk a titulky." : session.Problem is { } problem ? "FFmpeg: " + problem : "";
            }
        }
        catch (Exception ex) when (ex is Core.Speech.SpeechException or System.IO.IOException or UnauthorizedAccessException or Core.Processes.ProcessStartException)
        {
            if (opening == _opening)
            {
                Message = "Prehrávač potrebuje FFmpeg z inštalácie UNIVERSUM: " + ex.Message;
            }
        }
    }

    /// <summary>Stops playing and forgets the film (an open still going on is given up).</summary>
    public async Task CloseAsync()
    {
        _opening++;
        _seekDelay.Stop();
        if (_session is { } session)
        {
            _session = null;
            Picture = null;
            _shown = null;
            _shownTime = TimeSpan.MinValue;
            _lastNow = TimeSpan.MinValue;
            FramesShown = 0;
            PrimaryText = "";
            SecondaryText = "";
            _primaryPicture = (null, null, Rect.Empty);
            _secondaryPicture = (null, null, Rect.Empty);
            OnPropertyChanged(nameof(PrimaryPicture));
            OnPropertyChanged(nameof(SecondaryPicture));
            AudioTracks = [];
            OnPropertyChanged(nameof(AudioTracks));
            OnPropertyChanged(nameof(IsOpen));
            Refresh();
            await session.DisposeAsync();
        }

        Message = "Otvorte video – prehrá sa tu s titulkami.";
    }

    /// <summary>The subtitles to show (as they are after their changes): edited, and compared with.</summary>
    public void SetSubtitles(SubtitleTimeline primary, SubtitleTimeline secondary)
    {
        _primary = primary;
        _secondary = secondary;
        ShowSubtitles(Now);
    }

    /// <summary>The picture subtitles to show (PGS, with their changes of time): edited, and compared with.</summary>
    public void SetPictures(PictureTimeline primary, PictureTimeline secondary)
    {
        _primaryPictures = primary;
        _secondaryPictures = secondary;
        ShowSubtitles(Now);
    }

    public async Task SeekAsync(TimeSpan time)
    {
        if (_session is not { } session)
        {
            return;
        }

        _seekDelay.Stop();
        _seeking++;
        try
        {
            await session.SeekAsync(time);
        }
        finally
        {
            _seeking--;
        }

        if (ReferenceEquals(session, _session))
        {
            Message = session.Problem is { } problem ? "FFmpeg: " + problem : Message.StartsWith("FFmpeg: ", StringComparison.Ordinal) ? "" : Message;
            Tick();
        }
    }

    public void Pause()
    {
        if (_session is { IsPlaying: true } session)
        {
            session.Pause();
            Refresh();
        }
    }

    /// <summary>One frame of the window: the picture due now, the subtitles shown now, the slider.</summary>
    public void Tick()
    {
        if (_session is not { } session)
        {
            return;
        }

        var now = session.Position;
        var moved = now != _lastNow;
        _lastNow = now;
        if (session.FrameFor(now) is { } frame && (!ReferenceEquals(frame, _shown) || frame.Time != _shownTime) && _picture is { } picture &&
            picture.PixelWidth == frame.Format.Width && picture.PixelHeight == frame.Format.Height)
        {
            picture.WritePixels(new Int32Rect(0, 0, frame.Format.Width, frame.Format.Height), frame.Pixels, frame.Stride, 0);
            _shown = frame;
            _shownTime = frame.Time;
            FramesShown++;
        }

        if (session.IsPlaying && session.IsAtEnd)
        {
            session.Pause();
            Refresh();
        }

        if (!moved)
        {
            return;
        }

        ShowSubtitles(now);
        // the slider follows the film – not while it is dragged or a seek goes on (it would jump back)
        if (!_seekDelay.IsEnabled && _seeking == 0)
        {
            _updatingPosition = true;
            Position = now.TotalSeconds;
            _updatingPosition = false;
        }

        var time = $"{Clock(now)} / {Clock(TimeSpan.FromSeconds(End))}";
        if (time != _time)
        {
            Time = time;
        }
    }

    private void ShowSubtitles(TimeSpan now)
    {
        ShowPicture(ref _primaryPicture, _primaryPictures, now, nameof(PrimaryPicture), nameof(PrimaryPicturePlace));
        ShowPicture(ref _secondaryPicture, _secondaryPictures, now, nameof(SecondaryPicture), nameof(SecondaryPicturePlace));
        var primary = string.Join("\n", _primary.At(now).Select(c => c.Text));
        if (primary != _primaryText)
        {
            PrimaryText = primary;
        }

        var secondary = string.Join("\n", _secondary.At(now).Select(c => c.Text));
        if (secondary != _secondaryText)
        {
            SecondaryText = secondary;
        }
    }

    // the picture subtitle shown now, drawn once (UNIVERSUM decodes it) and placed on the picture: its canvas (the video's
    // size) fitted into the frame as the film is
    private void ShowPicture(ref (PictureCue? Cue, ImageSource? Image, Rect Place) shown, PictureTimeline pictures, TimeSpan now, string image, string place)
    {
        var cue = pictures.Cues.Count == 0 ? null : pictures.At(now).LastOrDefault();
        if (ReferenceEquals(cue, shown.Cue))
        {
            return;
        }

        shown = (cue, null, Rect.Empty);
        if (cue is not null && PgsReader.Render(cue) is { } drawn && cue.CanvasWidth > 0 && cue.CanvasHeight > 0)
        {
            var bitmap = BitmapSource.Create(drawn.Width, drawn.Height, 96, 96, PixelFormats.Bgra32, null, drawn.Bgra, drawn.Width * 4);
            bitmap.Freeze();
            var scale = Math.Min((double)FrameWidth / cue.CanvasWidth, (double)FrameHeight / cue.CanvasHeight);
            var left = (FrameWidth - (cue.CanvasWidth * scale)) / 2;
            var top = (FrameHeight - (cue.CanvasHeight * scale)) / 2;
            shown = (cue, bitmap, new Rect(left + (drawn.X * scale), top + (drawn.Y * scale), drawn.Width * scale, drawn.Height * scale));
        }

        OnPropertyChanged(image);
        OnPropertyChanged(place);
    }

    private async Task PlayPauseAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        if (session.IsPlaying)
        {
            session.Pause();
        }
        else
        {
            await session.PlayAsync();
        }

        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayGlyph));
        PlayPauseCommand.NotifyCanExecuteChanged();
        SeekByCommand.NotifyCanExecuteChanged();
        PreviousSubtitleCommand.NotifyCanExecuteChanged();
        NextSubtitleCommand.NotifyCanExecuteChanged();
    }

    // "1:02:33,5"
    private static string Clock(TimeSpan time) =>
        time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) + "," + (time.Milliseconds / 100).ToString(CultureInfo.InvariantCulture);
}
