using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public MediaFile File { get; } = file;

    public int Number { get => _number; set => SetProperty(ref _number, value); }

    public string FileName => System.IO.Path.GetFileName(File.Path);

    /// <summary>Further parts read as one with this file ("VTS_01_1.VOB VTS_01_2.VOB").</summary>
    public ObservableCollection<string> Parts { get; } = [];

    /// <summary>Files appended to this one (their tracks continue this file's tracks).</summary>
    public ObservableCollection<MediaFile> Appended { get; } = [];

    public string Details =>
        $"{File.ContainerType}, stopy: {File.Tracks.Count}" +
        (File.HasChapters ? $", kapitoly: {File.ChapterEntries}" : "") +
        (File.AttachmentCount > 0 ? $", prílohy: {File.AttachmentCount}" : "") +
        (File.HasTags ? ", tagy" : "");

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
public sealed class MuxSourceRow(MuxInputRow input, MuxSourceKind kind, string path, string container)
{
    public MuxInputRow Input { get; } = input;

    public MuxSourceKind Kind { get; } = kind;

    public string Path { get; } = path;

    /// <summary>"Film.mkv", "  ( Film.part2.vob", "  + Film-2.mkv".</summary>
    public string Name => Kind switch
    {
        MuxSourceKind.Part => "   ( " + System.IO.Path.GetFileName(Path),
        MuxSourceKind.Appended => "   + " + System.IO.Path.GetFileName(Path),
        _ => System.IO.Path.GetFileName(Path),
    };

    public string Container { get; } = container;

    public string Size => FileSizes.Text(System.IO.File.Exists(Path) ? new System.IO.FileInfo(Path).Length : 0);

    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>Colour of the input this row belongs to (the same colour marks its tracks).</summary>
    public int ColorIndex => (Input.Number - 1) % 6;

    public string Role => Kind switch
    {
        MuxSourceKind.Part => "ďalšia časť súboru",
        MuxSourceKind.Appended => "pripojený súbor",
        _ => "vstupný súbor",
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
    public int ColorIndex => (Input.Number - 1) % 6;

    public string Source => $"{Input.Number}: {Input.FileName}";

    /// <summary>For the colours of the track types.</summary>
    public TrackType Kind => Track.Type;

    public string Type => TrackTypes.Text(Track.Type);

    public string Codec => Track.Codec;

    public bool IsVideo => Track.Type == TrackType.Video;

    public bool IsTextSubtitles => Track.Type == TrackType.Subtitles && Track.IsTextSubtitles;

    /// <summary>Video size, audio channels and rate, subtitle kind.</summary>
    public string Properties => Track.Type switch
    {
        TrackType.Video => Track.PixelDimensions ?? "",
        TrackType.Audio => Track.AudioChannels is { } channels ? $"{channels} kan., {Track.AudioSamplingFrequency} Hz" : "",
        TrackType.Subtitles => Track.IsTextSubtitles ? "textové" : "obrázkové",
        _ => "",
    };

    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }

    public static IReadOnlyList<Choice<MuxCompression>> CompressionChoices { get; } =
    [
        new(MuxCompression.Automatic, "Určiť automaticky"),
        new(MuxCompression.None, "Bez kompresie"),
        new(MuxCompression.Zlib, "zlib"),
    ];

    /// <summary>Position in the output (1-based); 0 when the track is not taken.</summary>
    public int Position { get => _position; set => SetProperty(ref _position, value); }

    public bool Include { get => _include; set => Set(ref _include, value); }

    public string Name { get => _name; set => Set(ref _name, value); }

    public LanguageChoice Language { get => _language; set => Set(ref _language, value); }

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
        _flags.HearingImpaired ? "nepočujúci" : null,
        _flags.VisualImpaired ? "nevidiaci" : null,
        _flags.TextDescriptions ? "popis textom" : null,
        _flags.Original ? "pôvodný" : null,
        _flags.Commentary ? "komentár" : null,
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

    /// <summary>True when a less common option is set (the list marks it, the advanced section opens for it).</summary>
    public bool HasAdvanced => Options().IsEmpty is false;

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
        DelayMs = int.TryParse(_delay.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : null,
        DefaultDuration = Empty(_defaultDuration),
        FixBitstreamTiming = _fixBitstreamTiming,
        Compression = _compression,
        AspectRatio = Empty(_aspectRatio),
        Cropping = Empty(_cropping),
        SubtitleCharset = Empty(_subtitleCharset),
    };

    private static string? Empty(string text) => text.Trim().Length == 0 ? null : text.Trim();

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

/// <summary>Slovak names of track types.</summary>
public static class TrackTypes
{
    public static string Text(TrackType type) => type switch
    {
        TrackType.Video => "Video",
        TrackType.Audio => "Zvuk",
        TrackType.Subtitles => "Titulky",
        _ => "Iné",
    };
}
