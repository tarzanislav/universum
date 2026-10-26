using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.Media;
using Universum.Core.Muxing;

namespace Universum.App.Pages;

/// <summary>An input file of the multiplexer.</summary>
public sealed class MuxInputRow(MediaFile file, int number, Action changed) : ObservableObject
{
    private int _number = number;
    private bool _keepChapters = true;
    private bool _keepAttachments = true;
    private bool _keepTags = true;

    public MediaFile File { get; } = file;

    public int Number { get => _number; set => SetProperty(ref _number, value); }

    public string FileName => System.IO.Path.GetFileName(File.Path);

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

    public MuxSource ToSource() => new(File, KeepChapters, KeepAttachments, KeepTags);
}

/// <summary>A track of one of the inputs: taken or not, and the values it gets in the output.</summary>
public sealed class MuxTrackRow : ObservableObject
{
    private readonly Action _changed;
    private bool _include = true;
    private string _name;
    private LanguageChoice _language;
    private bool _default;
    private bool _enabled;
    private bool _forced;
    private int _position;

    public MuxTrackRow(MuxInputRow input, MediaTrack track, IReadOnlyList<LanguageChoice> choices, Action changed)
    {
        Input = input;
        Track = track;
        _changed = changed;
        _name = track.Name ?? "";
        LanguageChoices = choices.Any(c => c.Same(track.Language)) ? choices : [LanguageChoice.Of(track.Language), .. choices];
        _language = LanguageChoices.First(c => c.Same(track.Language));
        _default = track.Flags.Default;
        _enabled = track.Flags.Enabled;
        _forced = track.Flags.Forced;
    }

    public MuxInputRow Input { get; }

    public MediaTrack Track { get; }

    /// <summary>The input's number changed (an input was removed).</summary>
    public void RefreshSource() => OnPropertyChanged(nameof(Source));

    public string Source => $"{Input.Number}: {Input.FileName}";

    public string Type => Track.Type switch
    {
        TrackType.Video => "Video",
        TrackType.Audio => "Zvuk",
        TrackType.Subtitles => "Titulky",
        _ => "Iné",
    };

    public string Codec => Track.Codec;

    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }

    /// <summary>Position in the output (1-based); 0 when the track is not taken.</summary>
    public int Position { get => _position; set => SetProperty(ref _position, value); }

    public bool Include { get => _include; set => Set(ref _include, value); }

    public string Name { get => _name; set => Set(ref _name, value); }

    public LanguageChoice Language { get => _language; set => Set(ref _language, value); }

    public bool IsDefault { get => _default; set => Set(ref _default, value); }

    public bool IsEnabled { get => _enabled; set => Set(ref _enabled, value); }

    public bool IsForced { get => _forced; set => Set(ref _forced, value); }

    public MuxTrack ToTrack(int source) =>
        new(source, Track.Id, Name.Length == 0 ? null : Name, Language.Tag, Track.Flags with { Default = IsDefault, Enabled = IsEnabled, Forced = IsForced });

    /// <summary>Takes the values the rules give without a change notification per value.</summary>
    public void Take(MuxTrack track)
    {
        _name = track.Name ?? "";
        _language = LanguageChoices.FirstOrDefault(c => c.Same(track.Language)) ?? LanguageChoice.Of(track.Language);
        _default = track.Flags.Default;
        _enabled = track.Flags.Enabled;
        _forced = track.Flags.Forced;
        OnPropertyChanged(string.Empty);
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(property);
            _changed();
        }
    }
}
