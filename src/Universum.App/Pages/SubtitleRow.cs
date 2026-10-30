using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Media;
using Universum.Core.Subtitles;

namespace Universum.App.Pages;

/// <summary>
/// One subtitle track on the Titulky page – of the video or an external file – with what the user changes on it
/// (owner, 30. 10. 2026): Zmena FPS titulkov, Zmena rýchlosti titulkov (percentá), Posun (ms), conversion to SRT and
/// repairs of the text, its language, name and flags.
/// </summary>
public sealed class SubtitleRow : ObservableObject
{
    private static readonly CultureInfo Slovak = CultureInfo.GetCultureInfo("sk-SK");
    private readonly Action _changed;
    private bool _include = true;
    private LanguageChoice _language;
    private string _name;
    private readonly string _initialName;
    private bool _nameWritten;
    private string _defaultName;
    private bool _forced;
    private bool _hearingImpaired;
    private FrameRate? _fpsFrom;
    private FrameRate? _fpsTo;
    private double _speed;
    private string _speedText = "0";
    private string _delayText = "0";
    private bool _toSrt;
    private bool _removeHearingImpaired;
    private bool _removeFormatting;
    private bool _fixTimes;
    private string _firstNow = "";
    private string _firstTarget = "";
    private string _secondNow = "";
    private string _secondTarget = "";
    private string _syncNote = "";
    private SubtitleDocument? _document;
    private readonly Dictionary<int, string?> _cueEdits = [];

    // the text each changed subtitle had (the edits of a saved work fit only the same subtitles)
    private readonly Dictionary<int, string> _cueOriginals = [];
    private int _droppedEdits;

    public SubtitleRow(MediaTrack track, SubtitleDocument? document, string? externalPath, LanguageTag language, string? name, bool forced, bool hearingImpaired,
        string note, Action changed)
    {
        Track = track;
        Document = document;
        ExternalPath = externalPath;
        _changed = changed;
        LanguageChoices = LanguageChoice.Common.Any(c => c.Same(language)) ? LanguageChoice.Common : [LanguageChoice.Of(language), .. LanguageChoice.Common];
        _language = LanguageChoices.First(c => c.Same(language));
        _name = name ?? "";
        _initialName = _name;
        _defaultName = _name;
        _forced = forced;
        _hearingImpaired = hearingImpaired;
        InitialForced = forced;
        InitialHearingImpaired = hearingImpaired;
        Note = note;
        SpeedStepCommand = new RelayCommand<string>(step =>
        {
            if (double.TryParse(step, NumberStyles.Float, CultureInfo.InvariantCulture, out var by))
            {
                SpeedPercent += by;
            }
        });
        DelayStepCommand = new RelayCommand<string>(step =>
        {
            if (long.TryParse(step, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var by))
            {
                DelayText = (DelayMilliseconds + by).ToString(CultureInfo.InvariantCulture);
            }
        });
        ResetTimingCommand = new RelayCommand(() =>
        {
            FpsFrom = null;
            FpsTo = null;
            SpeedPercent = 0;
            DelayText = "0";
            SyncNote = "";
        });
        TwoPointsCommand = new RelayCommand(TwoPoints);
        if (document is { Cues.Count: > 1 })
        {
            _firstNow = Clock(document.Cues[0].Start);
            _firstTarget = _firstNow;
            _secondNow = Clock(document.Cues[^1].Start);
            _secondTarget = _secondNow;
        }
    }

    /// <summary>The track: of the video, or UNIVERSUM's own for an external file (its ID from 1000).</summary>
    public MediaTrack Track { get; }

    /// <summary>The subtitles with their times: of an external text file; of the video's own track only once read for a change of the text.</summary>
    public SubtitleDocument? Document
    {
        get => _document;
        set
        {
            if (SetProperty(ref _document, value))
            {
                OnPropertyChanged(nameof(Format));
                OnPropertyChanged(nameof(Count));
                if (_cueEdits.Count > 0)
                {
                    // a track read later: the saved changes checked against it
                    CheckCueEdits();
                    Changed();
                    OnPropertyChanged(nameof(TextEdited));
                }
            }
        }
    }

    /// <summary>The external file; null for a track of the video.</summary>
    public string? ExternalPath { get; }

    public bool IsExternal => ExternalPath is not null;

    public bool IsText => Track.IsTextSubtitles;

    /// <summary>What it is: "stopa 3 vo videu" or the name of the external file.</summary>
    public string Source => IsExternal ? "externý súbor: " + Path.GetFileName(ExternalPath) : $"stopa {Track.Id} vo videu";

    public string Format => Document?.Format ?? (Track.Codec.Length > 0 ? Track.Codec : Track.CodecId ?? "");

    public string Count => Document is { } document ? document.Cues.Count.ToString(CultureInfo.InvariantCulture) : Track.StatisticsFrames?.ToString(CultureInfo.InvariantCulture) ?? "–";

    /// <summary>The language read from the text, the encoding of the file.</summary>
    public string Note { get; }

    /// <summary>The track is in the result (unchecked: left out).</summary>
    public bool Include { get => _include; set => Set(ref _include, value); }

    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }

    public LanguageChoice Language { get => _language; set => Set(ref _language, value); }

    /// <summary>
    /// Track name: the name the result gets (owner, 30. 10. 2026) – the rules' or the file's until the user writes his own,
    /// which wins over them; empty = no name.
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
        if (!Set(ref _name, name, nameof(Name)))
        {
            Changed();
        }
    }

    /// <summary>The name the user wrote (trimmed), or null.</summary>
    public string? WrittenName => _nameWritten ? _name.Trim() : null;

    /// <summary>The name in the file (or of the external file's track).</summary>
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

    public bool Forced { get => _forced; set => Set(ref _forced, value); }

    public bool HearingImpaired { get => _hearingImpaired; set => Set(ref _hearingImpaired, value); }

    /// <summary>Forced as UNIVERSUM found it (the file, the name, the rules).</summary>
    public bool InitialForced { get; }

    public bool InitialHearingImpaired { get; }

    /// <summary>
    /// The user's choice for the rules: only when changed – or an external file named "forced" (the rules do not see file
    /// names); otherwise the rules decide (also by the small amount of subtitles).
    /// </summary>
    public bool? ForcedDecision => Forced != InitialForced || (IsExternal && Forced) ? Forced : null;

    /// <summary>The frame rates of Zmena FPS titulkov, in the owner's order.</summary>
    public IReadOnlyList<FrameRate> Rates => FrameRate.All;

    /// <summary>The frame rate the subtitles were made for (none: no change of FPS).</summary>
    public FrameRate? FpsFrom { get => _fpsFrom; set => Set(ref _fpsFrom, value); }

    /// <summary>The frame rate of the video they are for.</summary>
    public FrameRate? FpsTo { get => _fpsTo; set => Set(ref _fpsTo, value); }

    /// <summary>Zmena rýchlosti titulkov (percentá): the slider, −99 … +100.</summary>
    public double SpeedPercent
    {
        get => _speed;
        set
        {
            var rounded = Math.Round(Math.Clamp(value, -99, 100), 3);
            if (Set(ref _speed, rounded))
            {
                _speedText = rounded.ToString("0.###", Slovak);
                OnPropertyChanged(nameof(SpeedText));
            }
        }
    }

    /// <summary>The per cent typed ("4,271", "-2.5").</summary>
    public string SpeedText
    {
        get => _speedText;
        set
        {
            if (SetProperty(ref _speedText, value))
            {
                if (Number(value) is { } parsed && parsed is >= -99 and <= 100)
                {
                    _speed = (double)parsed;
                    OnPropertyChanged(nameof(SpeedPercent));
                }

                Changed();
            }
        }
    }

    /// <summary>Posun – Delay (ms): 1000 = 1 second, negative = earlier.</summary>
    public string DelayText { get => _delayText; set => Set(ref _delayText, value); }

    public long DelayMilliseconds => long.TryParse(DelayText.Trim().Replace(" ", "", StringComparison.Ordinal).Replace('−', '-'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms) ? ms : 0;

    public RelayCommand<string> SpeedStepCommand { get; }

    public RelayCommand<string> DelayStepCommand { get; }

    public RelayCommand ResetTimingCommand { get; }

    /// <summary>Synchronizácia podľa dvoch titulkov: the times now and where they belong.</summary>
    public string FirstNow { get => _firstNow; set => SetProperty(ref _firstNow, value); }

    public string FirstTarget { get => _firstTarget; set => SetProperty(ref _firstTarget, value); }

    public string SecondNow { get => _secondNow; set => SetProperty(ref _secondNow, value); }

    public string SecondTarget { get => _secondTarget; set => SetProperty(ref _secondTarget, value); }

    public string SyncNote { get => _syncNote; private set => SetProperty(ref _syncNote, value); }

    public RelayCommand TwoPointsCommand { get; }

    /// <summary>A text track of the video that is not SRT can be converted (an external text file always is).</summary>
    public bool CanConvert => IsText && !IsExternal && Track.CodecId is not ("S_TEXT/UTF8" or "S_TEXT/ASCII");

    /// <summary>Konverzia do SRT (a track of the video in ASS/SSA, WebVTT …).</summary>
    public bool ToSrt { get => _toSrt; set => Set(ref _toSrt, value); }

    public bool RemoveHearingImpaired { get => _removeHearingImpaired; set => Set(ref _removeHearingImpaired, value); }

    public bool RemoveFormatting { get => _removeFormatting; set => Set(ref _removeFormatting, value); }

    public bool FixTimes { get => _fixTimes; set => Set(ref _fixTimes, value); }

    /// <summary>The text has to be read and written anew (an external text file always; a track of the video for a change of the text).</summary>
    public bool NeedsText => IsText && (IsExternal || ToSrt || RemoveHearingImpaired || RemoveFormatting || FixTimes || TextEdited);

    /// <summary>
    /// Texts of single subtitles changed in Časy titulkov (owner, 30. 10. 2026), by their place in the subtitles as read;
    /// null = the subtitle is left out.
    /// </summary>
    public IReadOnlyDictionary<int, string?> CueEdits => _cueEdits;

    /// <summary>Some subtitle's text was changed or left out.</summary>
    public bool TextEdited => _cueEdits.Count > 0;

    /// <summary>Single subtitles can be changed in the list (text read, no repair of the whole text that would renumber them).</summary>
    public bool CanEditCues => IsText && Document is not null && !RemoveHearingImpaired && !RemoveFormatting && !FixTimes;

    /// <summary>Changes the text of the subtitle at <paramref name="index"/> (null or empty: leaves it out; the text as read: no change).</summary>
    public void EditCue(int index, string? text)
    {
        if (Document is not { } document || index < 0 || index >= document.Cues.Count)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = null;
        }

        _cueOriginals[index] = document.Cues[index].Text;
        if (text is not null && text == document.Cues[index].Text)
        {
            _cueEdits.Remove(index);
            _cueOriginals.Remove(index);
        }
        else
        {
            _cueEdits[index] = text;
        }

        Changed();
        OnPropertyChanged(nameof(TextEdited));
    }

    /// <summary>The texts the changed subtitles had (Uložiť prácu keeps them with the changes).</summary>
    public IReadOnlyDictionary<int, string> CueOriginals => _cueOriginals;

    /// <summary>
    /// The changes of single subtitles as saved (Uložiť prácu), or none; with the texts they were made on, a change that
    /// does not fit the subtitles read (another file of the same name) is left out – now or when they are read.
    /// </summary>
    public void SetCueEdits(IReadOnlyDictionary<int, string?>? edits, IReadOnlyDictionary<int, string>? originals = null)
    {
        _cueEdits.Clear();
        _cueOriginals.Clear();
        _droppedEdits = 0;
        foreach (var (index, text) in edits ?? new Dictionary<int, string?>())
        {
            _cueEdits[index] = text;
            if (originals?.TryGetValue(index, out var original) == true)
            {
                _cueOriginals[index] = original;
            }
        }

        CheckCueEdits();
        Changed();
        OnPropertyChanged(nameof(TextEdited));
    }

    // the changes kept only where the subtitles read are the ones they were made on
    private void CheckCueEdits()
    {
        if (_document is not { } document || _cueEdits.Count == 0)
        {
            return;
        }

        foreach (var index in _cueEdits.Keys.ToList())
        {
            if (index < 0 || index >= document.Cues.Count || (_cueOriginals.TryGetValue(index, out var original) && original != document.Cues[index].Text))
            {
                _cueEdits.Remove(index);
                _cueOriginals.Remove(index);
                _droppedEdits++;
            }
        }
    }

    /// <summary>What is wrong with the values typed (null when nothing).</summary>
    public string? Problem =>
        Number(SpeedText) is not { } speed ? "Rýchlosť: zadajte číslo v percentách (napr. 4,271 alebo -2,5)."
        : speed <= -100 || speed > 100 ? "Rýchlosť: od −99 do +100 % (pri −100 % by titulky stáli)."
        : !long.TryParse(DelayText.Trim().Replace(" ", "", StringComparison.Ordinal).Replace('−', '-'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ? "Posun: zadajte celé milisekundy (1000 = 1 sekunda)."
        : (FpsFrom is null) != (FpsTo is null) ? "Zmena FPS: vyberte pôvodné aj nové FPS."
        : null;

    /// <summary>The new times: FPS, then speed, then delay (mkvmerge: --sync).</summary>
    public SubtitleTiming Timing
    {
        get
        {
            var timing = FpsFrom is { } from && FpsTo is { } to && from != to ? SubtitleTiming.Fps(from, to) : SubtitleTiming.None;
            if (Number(SpeedText) is { } speed && speed != 0 && SubtitleTiming.Speed(speed) is { } bySpeed)
            {
                timing = timing.Then(bySpeed);
            }

            return timing.Then(SubtitleTiming.Delay(DelayMilliseconds));
        }
    }

    /// <summary>What changes, in a few words (the list of tracks).</summary>
    public string Summary
    {
        get
        {
            if (!Include)
            {
                return "vynechá sa";
            }

            var parts = new List<string>();
            if (FpsFrom is { } from && FpsTo is { } to && from != to)
            {
                parts.Add($"FPS {from.Label} → {to.Label}");
            }

            if (Number(SpeedText) is { } speed && speed != 0)
            {
                parts.Add($"rýchlosť {(speed > 0 ? "+" : "")}{speed.ToString("0.###", Slovak)} %");
            }

            if (DelayMilliseconds != 0)
            {
                parts.Add($"posun {(DelayMilliseconds > 0 ? "+" : "")}{DelayMilliseconds.ToString("#,0", Slovak)} ms");
            }

            // a track of the video whose text is written anew becomes SRT (its styles, ASS or WebVTT, are left)
            if (IsText && (IsExternal ? Document?.Format != "SRT" || Document?.Encoding != "UTF-8" : ToSrt || (NeedsText && Track.CodecId != "S_TEXT/UTF8")))
            {
                parts.Add("→ SRT");
            }

            if (TextEdited)
            {
                parts.Add($"upravené titulky ({_cueEdits.Count})");
            }

            if (_droppedEdits > 0)
            {
                parts.Add($"{_droppedEdits} uložených úprav textu sa nehodí k titulkom – vynechané");
            }

            if (RemoveHearingImpaired)
            {
                parts.Add("bez prvkov pre nepočujúcich");
            }

            if (RemoveFormatting)
            {
                parts.Add("bez formátovania");
            }

            if (FixTimes)
            {
                parts.Add("opravené časy");
            }

            return parts.Count == 0 ? IsExternal ? "pridá sa" : "" : (IsExternal ? "pridá sa, " : "") + string.Join(", ", parts);
        }
    }

    /// <summary>Where subtitles end up: "titulok v 0:10:00 → 0:10:25,6 · v 1:00:00 → 1:02:33,8".</summary>
    public string TimingExample
    {
        get
        {
            var timing = Timing;
            if (timing.IsNone)
            {
                return "Časy titulkov sa nemenia.";
            }

            var samples = new[] { TimeSpan.FromMinutes(10), TimeSpan.FromHours(1) };
            return timing.Describe() + ". Titulok " + string.Join(" · ", samples.Select(s => $"v {Clock(s)} sa zobrazí v {Clock(timing.Apply(s))}")) + ".";
        }
    }

    /// <summary>The subtitles as they will be written: the repairs of the text (the times change by <see cref="Timing"/>).</summary>
    public SubtitleDocument? Repaired(SubtitleDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        // the texts changed by hand first (by the place of each subtitle as read)
        if (_cueEdits.Count > 0)
        {
            document = document.With(document.Cues.Select((cue, index) => _cueEdits.TryGetValue(index, out var text) ? text is null ? null : cue with { Text = text } : cue)
                .OfType<SubtitleCue>());
        }

        if (RemoveHearingImpaired)
        {
            document = SubtitleCleanup.RemoveHearingImpaired(document);
        }

        if (RemoveFormatting)
        {
            document = SubtitleCleanup.RemoveFormatting(document);
        }

        return FixTimes ? SubtitleCleanup.FixTimes(document) : document;
    }

    /// <summary>"1:02:33,750".</summary>
    public static string Clock(TimeSpan time) =>
        (time < TimeSpan.Zero ? "−" : "") + time.Duration().ToString(@"h\:mm\:ss\,fff", CultureInfo.InvariantCulture);

    /// <summary>"1:02:33,75", "62:33", "3753.75" → time; null when it is no time.</summary>
    public static TimeSpan? ParseClock(string text)
    {
        var value = text.Trim().Replace('−', '-');
        if (value.Length == 0)
        {
            return null;
        }

        try
        {
            var time = SubtitleReader.Clock(value.TrimStart('-'));
            return value.StartsWith('-') ? -time : time;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the times to <paramref name="timing"/> (a task of Hromadné spracovanie opened again): a change of frame rate as
    /// Zmena FPS, any other factor as Zmena rýchlosti, and the shift.
    /// </summary>
    public void SetTiming(SubtitleTiming timing)
    {
        var rates = FrameRate.All.SelectMany(from => FrameRate.All.Where(to => to != from).Select(to => (From: from, To: to)))
            .FirstOrDefault(p => SubtitleTiming.Fps(p.From, p.To) is var fps && fps.Numerator == timing.Numerator && fps.Denominator == timing.Denominator);
        FpsFrom = rates.From;
        FpsTo = rates.To;
        SpeedText = rates.From is not null || timing.Numerator == timing.Denominator ? "0"
            : (((decimal)timing.Denominator / timing.Numerator - 1) * 100).ToString("0.######", Slovak);
        DelayText = timing.DelayMilliseconds.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Chooses a language of the list (one that is not in it leaves the language as it is).</summary>
    public void Choose(LanguageTag language)
    {
        if (LanguageChoices.FirstOrDefault(c => c.Same(language)) is { } choice)
        {
            Language = choice;
        }
    }

    /// <summary>The values of the row (Uložiť prácu).</summary>
    public StudioTrackState State() => new()
    {
        TrackId = IsExternal ? null : Track.Id,
        ExternalFile = ExternalPath,
        Include = Include,
        Language = Language.Tag.Ietf ?? Language.Tag.Legacy,
        Name = WrittenName,
        NameWritten = _nameWritten,
        Forced = Forced,
        HearingImpaired = HearingImpaired,
        FpsFrom = FpsFrom?.Label,
        FpsTo = FpsTo?.Label,
        Speed = SpeedText,
        Delay = DelayText,
        ToSrt = ToSrt,
        RemoveHearingImpaired = RemoveHearingImpaired,
        RemoveFormatting = RemoveFormatting,
        FixTimes = FixTimes,
        Texts = _cueEdits.Count == 0 ? null : new Dictionary<int, string?>(_cueEdits),
        TextsBefore = _cueOriginals.Count == 0 ? null : new Dictionary<int, string>(_cueOriginals),
    };

    /// <summary>The values saved before (the saved work of the video).</summary>
    public void Restore(StudioTrackState state)
    {
        Include = state.Include;
        if (state.Language is { Length: > 0 } code && LanguageChoices.FirstOrDefault(c => c.Same(LanguageTag.FromCode(code))) is { } language)
        {
            Language = language;
        }

        if (state.Name is { } written && (state.NameWritten || written != _initialName))
        {
            WriteName(written);
        }

        Forced = state.Forced;
        HearingImpaired = state.HearingImpaired;
        FpsFrom = FrameRate.All.FirstOrDefault(r => r.Label == state.FpsFrom);
        FpsTo = FrameRate.All.FirstOrDefault(r => r.Label == state.FpsTo);
        SpeedText = state.Speed;
        DelayText = state.Delay;
        ToSrt = state.ToSrt;
        RemoveHearingImpaired = state.RemoveHearingImpaired;
        RemoveFormatting = state.RemoveFormatting;
        FixTimes = state.FixTimes;
        SetCueEdits(state.Texts, state.TextsBefore);
    }

    /// <summary>
    /// The result of automatic sync: a change of the frame rate it found as Zmena FPS, any other change of speed as
    /// Zmena rýchlosti, and the delay.
    /// </summary>
    public void ApplySync(Core.Subtitles.Sync.SyncResult result)
    {
        var timing = result.Timing;
        if (result.Rates is { } rates)
        {
            FpsFrom = rates.From;
            FpsTo = rates.To;
            SpeedText = "0";
        }
        else
        {
            FpsFrom = null;
            FpsTo = null;
            SpeedText = timing.Numerator == timing.Denominator ? "0" : (((decimal)timing.Denominator / timing.Numerator - 1) * 100).ToString("0.######", Slovak);
        }

        DelayText = timing.DelayMilliseconds.ToString(CultureInfo.InvariantCulture);
        SyncNote = result.Detail;
    }

    private void TwoPoints()
    {
        if (ParseClock(FirstNow) is not { } first || ParseClock(FirstTarget) is not { } firstTarget ||
            ParseClock(SecondNow) is not { } second || ParseClock(SecondTarget) is not { } secondTarget)
        {
            SyncNote = "Zadajte štyri časy v tvare h:mm:ss,fff (napr. 0:01:02,500).";
            return;
        }

        if (SubtitleTiming.TwoPoints(first, firstTarget, second, secondTarget) is not { } sync)
        {
            SyncNote = "Druhý titulok musí byť neskôr ako prvý (teraz aj po zmene).";
            return;
        }

        // the speed and the delay of the two points replace the others
        FpsFrom = null;
        FpsTo = null;
        // the exact speed of the two points (millionths of a per cent), not the slider's rounding
        SpeedText = (((decimal)sync.Denominator / sync.Numerator - 1) * 100).ToString("0.######", Slovak);
        DelayText = sync.DelayMilliseconds.ToString(CultureInfo.InvariantCulture);
        SyncNote = $"Vypočítané: rýchlosť {SpeedText} %, posun {DelayText} ms.";
    }

    private static decimal? Number(string text) =>
        decimal.TryParse(text.Trim().Replace(" ", "", StringComparison.Ordinal).Replace('−', '-').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
        {
            return false;
        }

        Changed();
        return true;
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(TimingExample));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(NeedsText));
        _changed();
    }
}
