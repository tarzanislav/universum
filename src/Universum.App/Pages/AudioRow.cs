using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Audio;
using Universum.Core.Media;
using Universum.Core.Subtitles;

namespace Universum.App.Pages;

/// <summary>A codec to choose for a sound written anew.</summary>
public sealed record AudioCodecChoice(AudioCodec Codec, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A bitrate to choose (null: by the codec and the channels).</summary>
public sealed record BitrateChoice(int? Kbps, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One sound in Úprava audiostopy (owner, 30. 10. 2026): an audio track of the video or of another file (a dub, another
/// release) – kept or not, its language and name, its times (the shift; a change of frame rate or speed), the codec, the
/// channels and the loudness. Anything but the shift writes the sound anew.
/// </summary>
public sealed class AudioRow : ObservableObject
{
    private static readonly CultureInfo Slovak = CultureInfo.GetCultureInfo("sk-SK");
    private readonly Action _changed;
    private readonly string _initialName;
    private bool _include = true;
    private LanguageChoice _language;
    private string _name;
    private bool _nameWritten;
    private string _defaultName;
    private FrameRate? _fpsFrom;
    private FrameRate? _fpsTo;
    private (long Numerator, long Denominator)? _measured;
    private string _delayText = "0";
    private bool _keepPitch = true;
    private AudioCodecChoice _codec;
    private BitrateChoice _bitrate;
    private bool _stereo;
    private bool _normalize;
    private string _gainText = "0";

    /// <param name="ordinal">The track's place among the audio tracks of its file (FFmpeg's "0:a:N").</param>
    public AudioRow(MediaTrack track, int ordinal, string? externalPath, Action changed)
    {
        Track = track;
        Ordinal = ordinal;
        ExternalPath = externalPath;
        _changed = changed;
        LanguageChoices = LanguageChoice.Common.Any(c => c.Same(track.Language)) ? LanguageChoice.Common : [LanguageChoice.Of(track.Language), .. LanguageChoice.Common];
        _language = LanguageChoices.First(c => c.Same(track.Language));
        _name = track.Name ?? "";
        _initialName = _name;
        _defaultName = _name;
        Codecs = [new(AudioCodec.Keep, $"Ponechať ({track.Codec})"), new(AudioCodec.Ac3, "AC-3"), new(AudioCodec.Eac3, "E-AC-3"), new(AudioCodec.Aac, "AAC"),
            new(AudioCodec.Opus, "Opus"), new(AudioCodec.Flac, "FLAC (bezstratovo)")];
        _codec = Codecs[0];
        _bitrate = Bitrates[0];
        DelayStepCommand = new RelayCommand<string>(step =>
        {
            if (long.TryParse(step, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var by))
            {
                DelayText = (DelayMilliseconds + by).ToString(CultureInfo.InvariantCulture);
            }
        });
        ResetCommand = new RelayCommand(() =>
        {
            _measured = null;
            FpsFrom = null;
            FpsTo = null;
            DelayText = "0";
            KeepPitch = true;
            Codec = Codecs[0];
            Bitrate = Bitrates[0];
            Stereo = false;
            Normalize = false;
            GainText = "0";
        });
    }

    public MediaTrack Track { get; }

    /// <summary>The track's place among the audio tracks of its file (0 = the first).</summary>
    public int Ordinal { get; }

    /// <summary>The other file the sound comes from; null for a track of the video.</summary>
    public string? ExternalPath { get; }

    public bool IsExternal => ExternalPath is not null;

    public string Source => IsExternal ? $"{Path.GetFileName(ExternalPath)}, zvuk {Ordinal + 1}" : $"stopa {Track.Id} vo videu";

    /// <summary>"AC-3 6 k. 48 kHz".</summary>
    public string Format => Track.Codec + (Track.AudioChannels is { } channels ? $" {channels} k." : "") +
                            (Track.AudioSamplingFrequency is { } rate ? $" {(rate / 1000.0).ToString("0.#", Slovak)} kHz" : "");

    public bool Include { get => _include; set => Set(ref _include, value); }

    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }

    public LanguageChoice Language { get => _language; set => Set(ref _language, value); }

    /// <summary>
    /// Track name: the name the result gets – the rules' or the file's until the user writes his own, which wins over
    /// them; empty = no name.
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (value != _name)
            {
                // the name the result would have anyway again: no name of the user's
                _nameWritten = value.Trim() != _defaultName;
                Set(ref _name, value);
            }
        }
    }

    /// <summary>A name the user wrote before (a saved work, a task): it counts as written, also when it is the default one.</summary>
    public void WriteName(string name)
    {
        _nameWritten = true;
        SetProperty(ref _name, name, nameof(Name));
        Changed();
    }

    /// <summary>The name the user wrote (trimmed), or null.</summary>
    public string? WrittenName => _nameWritten ? _name.Trim() : null;

    public string InitialName => _initialName;

    /// <summary>The name the result would get without the user's (the rules' or the batch's); shown while he has written none.</summary>
    public void ShowName(string? name)
    {
        _defaultName = name ?? "";
        if (!_nameWritten && _name != _defaultName)
        {
            _name = _defaultName;
            OnPropertyChanged(nameof(Name));
        }
    }

    public IReadOnlyList<FrameRate> Rates => FrameRate.All;

    /// <summary>Zmena FPS: the sound was made for this frame rate…</summary>
    public FrameRate? FpsFrom
    {
        get => _fpsFrom;
        set
        {
            if (value is not null)
            {
                _measured = null;
            }

            Set(ref _fpsFrom, value);
        }
    }

    /// <summary>…and plays with a video of this one (25 → 23,976: the PAL sound 4,27 % slower).</summary>
    public FrameRate? FpsTo
    {
        get => _fpsTo;
        set
        {
            if (value is not null)
            {
                _measured = null;
            }

            Set(ref _fpsTo, value);
        }
    }

    /// <summary>Posun in ms (+ = later).</summary>
    public string DelayText { get => _delayText; set => Set(ref _delayText, value); }

    public long DelayMilliseconds => long.TryParse(DelayText.Trim().Replace(" ", "", StringComparison.Ordinal).Replace('−', '-'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms) ? ms : 0;

    public RelayCommand<string> DelayStepCommand { get; }

    /// <summary>Every change of the sound undone.</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>A change of speed keeps the pitch (else the pitch changes with it, as the PAL release had it).</summary>
    public bool KeepPitch { get => _keepPitch; set => Set(ref _keepPitch, value); }

    public IReadOnlyList<AudioCodecChoice> Codecs { get; }

    public AudioCodecChoice Codec { get => _codec; set => Set(ref _codec, value); }

    public IReadOnlyList<BitrateChoice> Bitrates { get; } =
        [new(null, "automaticky"), .. new[] { 128, 160, 192, 224, 256, 320, 384, 448, 640, 768 }.Select(k => new BitrateChoice(k, $"{k} kbit/s"))];

    public BitrateChoice Bitrate { get => _bitrate; set => Set(ref _bitrate, value); }

    /// <summary>Written as stereo (a downmix of 5.1).</summary>
    public bool Stereo { get => _stereo; set => Set(ref _stereo, value); }

    /// <summary>Loudness −23 LUFS (EBU R128).</summary>
    public bool Normalize { get => _normalize; set => Set(ref _normalize, value); }

    /// <summary>Louder (+) or quieter (−) in dB.</summary>
    public string GainText { get => _gainText; set => Set(ref _gainText, value); }

    private double? Gain => double.TryParse(GainText.Trim().Replace('−', '-').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var db) ? db : null;

    /// <summary>The times as they will be: the frame rates (or the speed measured by Napasovať) and the shift.</summary>
    public SubtitleTiming Timing
    {
        get
        {
            var timing = _measured is { } measured ? new SubtitleTiming(0, measured.Numerator, measured.Denominator)
                : FpsFrom is { } from && FpsTo is { } to && from != to ? SubtitleTiming.Fps(from, to)
                : SubtitleTiming.None;
            return timing with { DelayMilliseconds = DelayMilliseconds };
        }
    }

    /// <summary>What becomes of the sound.</summary>
    public AudioRecipe Recipe => new(Timing, KeepPitch, Codec.Codec, Bitrate.Kbps, Stereo, Normalize, Gain ?? 0);

    /// <summary>What is wrong with the values (null when nothing).</summary>
    public string? Problem => !Include ? null
        : Gain is not { } gain || gain is < -30 or > 30 ? "Hlasitosť v dB: číslo od −30 do +30."
        : Recipe.Problem(Track);

    /// <summary>The changes in words, for the list.</summary>
    public string Summary
    {
        get
        {
            var recipe = Recipe;
            var changes = recipe.Describe(Track);
            return (IsExternal ? "pridá sa" : "") + (changes == "bez zmeny" ? "" : (IsExternal ? ", " : "") + changes) +
                   (recipe.NeedsEncoding ? " – zvuk sa zapíše znova" : "");
        }
    }

    /// <summary>The values of the row (Uložiť prácu, Uložiť do úlohy).</summary>
    public AudioTrackState State() => new()
    {
        TrackId = IsExternal ? null : Track.Id,
        ExternalFile = ExternalPath,
        ExternalTrack = Track.ExternalTrack,
        Include = Include,
        Language = Language.Tag.Ietf ?? Language.Tag.Legacy,
        Name = WrittenName,
        FpsFrom = FpsFrom?.Label,
        FpsTo = FpsTo?.Label,
        MeasuredNumerator = _measured?.Numerator,
        MeasuredDenominator = _measured?.Denominator,
        Delay = DelayText,
        KeepPitch = KeepPitch,
        Codec = Codec.Codec,
        Bitrate = Bitrate.Kbps,
        Stereo = Stereo,
        Normalize = Normalize,
        Gain = GainText,
    };

    /// <summary>The values saved before.</summary>
    public void Restore(AudioTrackState state)
    {
        Include = state.Include;
        if (state.Language is { Length: > 0 } code && LanguageChoices.FirstOrDefault(c => c.Same(LanguageTag.FromCode(code))) is { } language)
        {
            Language = language;
        }

        if (state.Name is { } name)
        {
            WriteName(name);
        }

        FpsFrom = FrameRate.All.FirstOrDefault(r => r.Label == state.FpsFrom);
        FpsTo = FrameRate.All.FirstOrDefault(r => r.Label == state.FpsTo);
        _measured = state.MeasuredNumerator is { } numerator && state.MeasuredDenominator is { } denominator && denominator > 0 ? (numerator, denominator) : null;
        DelayText = state.Delay;
        KeepPitch = state.KeepPitch;
        Codec = Codecs.FirstOrDefault(c => c.Codec == state.Codec) ?? Codecs[0];
        Bitrate = Bitrates.FirstOrDefault(b => b.Kbps == state.Bitrate) ?? Bitrates[0];
        Stereo = state.Stereo;
        Normalize = state.Normalize;
        GainText = state.Gain;
        Changed();
    }

    /// <summary>What becomes of the sound as a task of Hromadné spracovanie has it (without the values as typed).</summary>
    public void SetRecipe(AudioRecipe recipe)
    {
        SetTiming(recipe.Timing, null);
        if (FrameRate.All.SelectMany(from => FrameRate.All.Where(to => to != from).Select(to => (from, to)))
                .FirstOrDefault(p => SubtitleTiming.Fps(p.from, p.to) is var fps && fps.Numerator == recipe.Timing.Numerator && fps.Denominator == recipe.Timing.Denominator) is { from: not null } rates)
        {
            SetTiming(recipe.Timing, rates);
        }

        KeepPitch = recipe.KeepPitch;
        Codec = Codecs.FirstOrDefault(c => c.Codec == recipe.Codec) ?? Codecs[0];
        Bitrate = Bitrates.FirstOrDefault(b => b.Kbps == recipe.Bitrate) ?? Bitrates[0];
        Stereo = recipe.Stereo;
        Normalize = recipe.Normalize;
        GainText = recipe.Gain.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The row is the sound saved as <paramref name="state"/>.</summary>
    public bool Is(AudioTrackState state) => state.ExternalFile is { } file
        ? ExternalPath is { } path && Core.IO.Paths.Same(path, file) && Track.ExternalTrack == state.ExternalTrack
        : !IsExternal && state.TrackId == Track.Id;

    /// <summary>The times found by Napasovať: a change of frame rate, or the speed measured, and the shift.</summary>
    public void SetTiming(SubtitleTiming timing, (FrameRate From, FrameRate To)? rates)
    {
        if (rates is { } pair)
        {
            FpsFrom = pair.From;
            FpsTo = pair.To;
        }
        else
        {
            FpsFrom = null;
            FpsTo = null;
            _measured = timing.Numerator == timing.Denominator ? null : (timing.Numerator, timing.Denominator);
        }

        DelayText = timing.DelayMilliseconds.ToString(CultureInfo.InvariantCulture);
        Changed();
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            Changed();
        }
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Problem));
        _changed();
    }
}
