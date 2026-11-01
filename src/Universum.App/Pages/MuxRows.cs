using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Universum.App.Localization;
using Universum.Core.Media;
using Universum.Core.Muxing;

namespace Universum.App.Pages;

/// <summary>An input file of the multiplexer, with its additional parts and the files appended to it.</summary>
public sealed class MuxInputRow(MediaFile file, int number, Action changed) : ObservableObject
{
    private int _number = number;
    private bool _keepChapters = true;
    private bool _keepAttachments = true;
    private bool _keepTags = true;
    private int _colorIndex = number - 1;

    public MediaFile File { get; } = file;

    /// <summary>Colour of the file (the source list and its tracks); appended files get the next colours.</summary>
    public int ColorIndex { get => _colorIndex; set => SetProperty(ref _colorIndex, value); }

    public int Number { get => _number; set => SetProperty(ref _number, value); }

    public string FileName => System.IO.Path.GetFileName(File.Path);

    /// <summary>Further parts read as one with this file ("VTS_01_1.VOB VTS_01_2.VOB").</summary>
    public ObservableCollection<string> Parts { get; } = [];

    /// <summary>Files appended to this one (their tracks continue this file's tracks).</summary>
    public ObservableCollection<MediaFile> Appended { get; } = [];

    public string Details =>
        Loc.F("{0}, stopy: {1}", File.ContainerType, File.Tracks.Count) +
        (File.HasChapters ? Loc.F(", kapitoly: {0}", File.ChapterEntries) : "") +
        (File.AttachmentCount > 0 ? Loc.F(", prílohy: {0}", File.AttachmentCount) : "") +
        (File.HasTags ? Loc.T(", tagy") : "");

    public bool HasChapters => File.HasChapters;

    public bool HasAttachments => File.AttachmentCount > 0;

    public bool HasTags => File.HasTags;

    public bool KeepChapters { get => _keepChapters; set { if (SetProperty(ref _keepChapters, value)) { changed(); } } }

    public bool KeepAttachments { get => _keepAttachments; set { if (SetProperty(ref _keepAttachments, value)) { changed(); } } }

    public bool KeepTags { get => _keepTags; set { if (SetProperty(ref _keepTags, value)) { changed(); } } }

    public MuxSource ToSource() => new(File, KeepChapters, KeepAttachments, KeepTags) { AdditionalParts = Parts.ToList(), Appended = Appended.ToList() };
}

/// <summary>What a row of the source list is: an input, an additional part of it or a file appended to it.</summary>
public enum MuxSourceKind
{
    File,
    Part,
    Appended,
}

/// <summary>A row of the source list (MKVToolNix GUI: Source files): name, container, size and folder.</summary>
public sealed class MuxSourceRow(MuxInputRow input, MuxSourceKind kind, string path, string container, int colorIndex)
{
    public MuxInputRow Input { get; } = input;

    public MuxSourceKind Kind { get; } = kind;

    public string Path { get; } = path;

    /// <summary>"Film.mkv"; under it "( Film.part2.vob )" and "+ Film-2.mkv" (shown indented as its children).</summary>
    public string Name => Kind switch
    {
        MuxSourceKind.Part => "( " + System.IO.Path.GetFileName(Path) + " )",
        MuxSourceKind.Appended => "+ " + System.IO.Path.GetFileName(Path),
        _ => System.IO.Path.GetFileName(Path),
    };

    /// <summary>A part or an appended file: shown under its file, indented.</summary>
    public bool IsChild => Kind != MuxSourceKind.File;

    public string Container { get; } = container;

    public string Size => FileSizes.Text(System.IO.File.Exists(Path) ? new System.IO.FileInfo(Path).Length : 0);

    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>Colour of the file: an input and its parts share one, an appended file has its own (its tracks too).</summary>
    public int ColorIndex { get; } = colorIndex;

    public string Role => Kind switch
    {
        MuxSourceKind.Part => Loc.T("ďalšia časť súboru"),
        MuxSourceKind.Appended => Loc.T("pripojený súbor"),
        _ => Loc.T("vstupný súbor"),
    };
}

/// <summary>File sizes as Windows shows them (1 KB = 1024 B).</summary>
public static class FileSizes
{
    public static string Text(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>A track of one of the inputs: taken or not, and the values it gets in the output.</summary>
public sealed class MuxTrackRow : ObservableObject
{
    private readonly Action _changed;
    private bool _include = true;
    private string _name;
    private LanguageChoice _language;
    private TrackFlags _flags;
    private int _position;
    private string _delay = "";
    private string _defaultDuration = "";
    private bool _fixBitstreamTiming;
    private MuxCompression _compression;
    private string _aspectRatio = "";
    private string _cropping = "";
    private string _subtitleCharset = "";

    public MuxTrackRow(MuxInputRow input, MediaTrack track, IReadOnlyList<LanguageChoice> choices, Action changed)
    {
        Input = input;
        Track = track;
        _changed = changed;
        _name = track.Name ?? "";
        LanguageChoices = choices.Any(c => c.Same(track.Language)) ? choices : [LanguageChoice.Of(track.Language), .. choices];
        _language = LanguageChoices.First(c => c.Same(track.Language));
        _flags = track.Flags;
    }

    public MuxInputRow Input { get; }

    public MediaTrack Track { get; }

    /// <summary>The input's number changed (an input was removed).</summary>
    public void RefreshSource()
    {
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(ColorIndex));
    }

    /// <summary>Colour of the file the track comes from (the same as in the source list).</summary>
    public int ColorIndex => Input.ColorIndex;

    /// <summary>A track of an input (editable); the tracks of appended files are its children.</summary>
    public bool IsMain => true;

    /// <summary>The tracks of the appended files that continue this one (shown under it).</summary>
    internal List<MuxAppendedTrackRow> Children { get; set; } = [];

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        foreach (var child in Children)
        {
            child.ParentChanged();
        }
    }

    public string Source => $"{Input.Number}: {Input.FileName}";

    /// <summary>For the colours of the track types.</summary>
    public TrackType Kind => Track.Type;

    public string Type => TrackTypes.Text(Track.Type);

    public string Codec => Track.Codec;

    public bool IsVideo => Track.Type == TrackType.Video;

    public bool IsTextSubtitles => Track.Type == TrackType.Subtitles && Track.IsTextSubtitles;

    /// <summary>Video size, audio channels and rate, subtitle kind.</summary>
    public string Properties => new MuxTrackRowText(Track).Properties;

    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }

    public static IReadOnlyList<Choice<MuxCompression>> CompressionChoices { get; } =
    [
        new(MuxCompression.Automatic, Loc.T("Určiť automaticky")),
        new(MuxCompression.None, Loc.T("Bez kompresie")),
        new(MuxCompression.Zlib, "zlib"),
    ];

    /// <summary>Position in the output (1-based); 0 when the track is not taken.</summary>
    public int Position { get => _position; set => SetProperty(ref _position, value); }

    public bool Include { get => _include; set => Set(ref _include, value); }

    public string Name { get => _name; set => Set(ref _name, value); }

    public LanguageChoice Language
    {
        get => _language;
        set
        {
            if (_language != value)
            {
                _language = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAdvanced));

                // owner, 1. 11. 2026: the order follows the language (CZ at the top, SK under it)
                LanguageSet?.Invoke(this);
                _changed();
            }
        }
    }

    /// <summary>Called when the user (or a recognition) sets the language – before the settings are recalculated.</summary>
    public Action<MuxTrackRow>? LanguageSet { get; set; }

    public bool IsDefault { get => _flags.Default; set => SetFlags(_flags with { Default = value }); }

    public bool IsEnabled { get => _flags.Enabled; set => SetFlags(_flags with { Enabled = value }); }

    public bool IsForced { get => _flags.Forced; set => SetFlags(_flags with { Forced = value }); }

    public bool IsHearingImpaired { get => _flags.HearingImpaired; set => SetFlags(_flags with { HearingImpaired = value }); }

    public bool IsVisualImpaired { get => _flags.VisualImpaired; set => SetFlags(_flags with { VisualImpaired = value }); }

    public bool IsTextDescriptions { get => _flags.TextDescriptions; set => SetFlags(_flags with { TextDescriptions = value }); }

    public bool IsOriginal { get => _flags.Original; set => SetFlags(_flags with { Original = value }); }

    public bool IsCommentary { get => _flags.Commentary; set => SetFlags(_flags with { Commentary = value }); }

    /// <summary>Short marks of the less common flags for the track list: "nepočujúci, komentár".</summary>
    public string FlagMarks => string.Join(", ", new[]
    {
        _flags.HearingImpaired ? Loc.T("nepočujúci") : null,
        _flags.VisualImpaired ? Loc.T("nevidiaci") : null,
        _flags.TextDescriptions ? Loc.T("popis textom") : null,
        _flags.Original ? Loc.T("pôvodný") : null,
        _flags.Commentary ? Loc.T("komentár") : null,
    }.OfType<string>());

    /// <summary>Delay in milliseconds as typed ("", "-120").</summary>
    public string Delay { get => _delay; set => Set(ref _delay, value); }

    public string DefaultDuration { get => _defaultDuration; set => Set(ref _defaultDuration, value); }

    public bool FixBitstreamTiming { get => _fixBitstreamTiming; set => Set(ref _fixBitstreamTiming, value); }

    public Choice<MuxCompression> Compression
    {
        get => CompressionChoices.First(c => c.Value == _compression);
        set => Set(ref _compression, value.Value);
    }

    public string AspectRatio { get => _aspectRatio; set => Set(ref _aspectRatio, value); }

    public string Cropping { get => _cropping; set => Set(ref _cropping, value); }

    public string SubtitleCharset { get => _subtitleCharset; set => Set(ref _subtitleCharset, value); }

    /// <summary>A delay that is not a whole number of milliseconds ("1,5", "abc"); writing waits until it is fixed.</summary>
    public bool HasInvalidDelay => _delay.Trim().Length > 0 && ParseDelay(_delay) is null;

    /// <summary>True when a less common flag or option is set (the advanced section opens for it).</summary>
    public bool HasAdvanced => FlagMarks.Length > 0 || Options().IsEmpty is false;

    public MuxTrack ToTrack(int source) =>
        new(source, Track.Id, Name.Length == 0 ? null : Name, Language.Tag, _flags) { Options = Options() };

    /// <summary>Takes the values the rules give without a change notification per value.</summary>
    public void Take(MuxTrack track)
    {
        _name = track.Name ?? "";
        _language = LanguageChoices.FirstOrDefault(c => c.Same(track.Language)) ?? LanguageChoice.Of(track.Language);
        _flags = track.Flags;
        OnPropertyChanged(string.Empty);
    }

    private MuxTrackOptions Options() => new()
    {
        DelayMs = ParseDelay(_delay),
        DefaultDuration = Empty(_defaultDuration),
        FixBitstreamTiming = _fixBitstreamTiming,
        Compression = _compression,
        AspectRatio = Empty(_aspectRatio),
        Cropping = Empty(_cropping),
        SubtitleCharset = Empty(_subtitleCharset),
    };

    private static string? Empty(string text) => text.Trim().Length == 0 ? null : text.Trim();

    // "-120", "+40", "250 ms"
    private static int? ParseDelay(string text)
    {
        var value = text.Trim();
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^2].TrimEnd();
        }

        return int.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : null;
    }

    private void SetFlags(TrackFlags flags)
    {
        if (_flags != flags)
        {
            _flags = flags;
            OnPropertyChanged(string.Empty);
            _changed();
        }
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(property);
            OnPropertyChanged(nameof(HasAdvanced));
            _changed();
        }
    }
}

/// <summary>
/// A track of an appended file under the track it continues (MKVToolNix GUI shows them as children): not editable –
/// name, language and flags come from the main track; its file has its own colour.
/// </summary>
public sealed class MuxAppendedTrackRow(MuxTrackRow parent, MediaFile file, MediaTrack? track, int colorIndex) : ObservableObject
{
    public MuxTrackRow Parent { get; } = parent;

    public MediaFile File { get; } = file;

    /// <summary>The continuing track (the same ID); null when the appended file lacks it.</summary>
    public MediaTrack? Track { get; } = track;

    public string FileName => System.IO.Path.GetFileName(File.Path);

    public bool IsMain => false;

    /// <summary>The track is missing in the appended file or is of another type or codec (writing is then refused).</summary>
    public bool IsMismatch => Track is null || Track.Type != Parent.Track.Type ||
                              !string.Equals(Track.CodecId ?? Track.Codec, Parent.Track.CodecId ?? Parent.Track.Codec, StringComparison.Ordinal);

    public int ColorIndex { get; } = colorIndex;

    /// <summary>Taken with the main track (the box is greyed out; setting it does nothing).</summary>
    public bool Include { get => Parent.Include; set { } }

    public string Position => "";

    public string Source => FileName;

    public TrackType Kind => Parent.Kind;

    public string Type => Parent.Type;

    public string Codec => Track?.Codec ?? Loc.T("chýba");

    public LanguageChoice Language => Parent.Language;

    public string Name => IsMismatch ? Loc.F("+ {0}: stopa {1} chýba alebo je iná", FileName, Parent.Track.Id) : "+ " + FileName;

    public bool IsDefault => Parent.IsDefault;

    public bool IsForced => Parent.IsForced;

    public string FlagMarks => "";

    public string Properties => Track is null ? "" : new MuxTrackRowText(Track).Properties;

    /// <summary>What the row is, for its tooltip and the properties panel.</summary>
    public string Explanation => IsMismatch
        ? Loc.F("Súbor {0} nemá stopu {1} rovnakého typu a kodeku ako hlavný súbor – takto sa pripojiť nedá.", FileName, Parent.Track.Id)
        : Loc.F("Pripojená časť zo súboru {0}: pokračuje stopou {1} ({2}, {3}) zo súboru {4}. Názov, jazyk a príznaky má od nej – upravte hlavnú stopu.",
            FileName, Parent.Position, Parent.Type, Parent.Language.Display, Parent.Input.FileName);

    internal void ParentChanged() => OnPropertyChanged(string.Empty);
}

/// <summary>Short properties of a track (video size, audio channels, subtitle kind).</summary>
internal readonly record struct MuxTrackRowText(MediaTrack Track)
{
    public string Properties => Track.Type switch
    {
        TrackType.Video => Track.PixelDimensions ?? "",
        TrackType.Audio => Track.AudioChannels is { } channels ? Loc.F("{0} kan., {1} Hz", channels, Track.AudioSamplingFrequency) : "",
        TrackType.Subtitles => Track.IsTextSubtitles ? Loc.T("textové") : Loc.T("obrázkové"),
        _ => "",
    };
}

/// <summary>Slovak names of track types.</summary>
public static class TrackTypes
{
    public static string Text(TrackType type) => type switch
    {
        TrackType.Video => Loc.T("Video"),
        TrackType.Audio => Loc.T("Zvuk"),
        TrackType.Subtitles => Loc.T("Titulky"),
        _ => Loc.T("Iné"),
    };
}
