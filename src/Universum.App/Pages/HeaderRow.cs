using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>A language in the header editor's list.</summary>
public sealed record LanguageChoice(LanguageTag Tag, string Display)
{
    /// <summary>"neurčený" and the built-in languages.</summary>
    public static IReadOnlyList<LanguageChoice> Common { get; } =
        [Of(LanguageTag.Undetermined), .. Languages.All.Select(l => Of(LanguageTag.FromCode(l.Ietf)))];

    public static LanguageChoice Of(LanguageTag tag)
    {
        if (tag.IsUndetermined)
        {
            return new LanguageChoice(LanguageTag.Undetermined, "neurčený (und)");
        }

        var language = Languages.Find(tag.Primary);
        return new LanguageChoice(tag, language is null ? tag.ToString() : $"{language.SlovakName} ({tag.Primary})");
    }

    public bool Same(LanguageTag other) => string.Equals(Tag.Primary, other.Primary, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One track in the header editor: what the file has and what the user sets.</summary>
public sealed class HeaderRow : ObservableObject
{
    private readonly Action _changed;
    private string _name;
    private LanguageChoice _language;
    private bool _default;
    private bool _enabled;
    private bool _forced;
    private bool _removed;
    private int _position;

    public HeaderRow(MediaTrack source, IReadOnlyList<LanguageChoice> choices, Action changed)
    {
        Source = source;
        _changed = changed;
        _name = source.Name ?? "";
        LanguageChoices = choices.Any(c => c.Same(source.Language)) ? choices : [LanguageChoice.Of(source.Language), .. choices];
        _language = LanguageChoices.First(c => c.Same(source.Language));
        _default = source.Flags.Default;
        _enabled = source.Flags.Enabled;
        _forced = source.Flags.Forced;
    }

    public MediaTrack Source { get; }

    public int TrackId => Source.Id;

    public TrackType TrackType => Source.Type;

    /// <summary>Position in the result (1-based); removed tracks have none.</summary>
    public int Position { get => _position; set => SetProperty(ref _position, value); }

    public string Type => Source.Type switch
    {
        TrackType.Video => "Video",
        TrackType.Audio => "Zvuk",
        TrackType.Subtitles => "Titulky",
        _ => "Iné",
    };

    public string Codec => Source.Codec;

    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }

    /// <summary>Only audio and subtitles can be left out and moved.</summary>
    public bool CanRemove => Source.Type is TrackType.Audio or TrackType.Subtitles;

    public string Name { get => _name; set => Set(ref _name, value); }

    public LanguageChoice Language { get => _language; set => Set(ref _language, value); }

    public bool IsDefault { get => _default; set => Set(ref _default, value); }

    public bool IsEnabled { get => _enabled; set => Set(ref _enabled, value); }

    public bool IsForced { get => _forced; set => Set(ref _forced, value); }

    public bool IsRemoved { get => _removed; set => Set(ref _removed, value && CanRemove); }

    /// <summary>What changes on this track, e.g. "názov, jazyk"; empty when nothing.</summary>
    public string Changes
    {
        get
        {
            if (IsRemoved)
            {
                return "vynechá sa";
            }

            var changes = new List<string>();
            if (!string.Equals(Name, Source.Name ?? "", StringComparison.Ordinal))
            {
                changes.Add("názov");
            }

            if (!Language.Same(Source.Language))
            {
                changes.Add("jazyk");
            }

            if (IsDefault != Source.Flags.Default)
            {
                changes.Add("default");
            }

            if (IsEnabled != Source.Flags.Enabled)
            {
                changes.Add("enabled");
            }

            if (IsForced != Source.Flags.Forced)
            {
                changes.Add("forced");
            }

            return string.Join(", ", changes);
        }
    }

    /// <summary>The file's values, shown as a tooltip.</summary>
    public string Before =>
        $"V súbore: názov „{Source.Name}“, jazyk {LanguageChoice.Of(Source.Language).Display}, " +
        $"default {YesNo(Source.Flags.Default)}, enabled {YesNo(Source.Flags.Enabled)}, forced {YesNo(Source.Flags.Forced)}";

    public TrackEdit ToEdit() => new(TrackId, Name.Length == 0 ? null : Name, Language.Tag, Source.Flags with { Default = IsDefault, Enabled = IsEnabled, Forced = IsForced });

    /// <summary>Takes the values of a target track (the UNIVERSUM rules) without raising a change per value.</summary>
    public void Take(TrackTarget target)
    {
        _name = target.Name ?? "";
        _language = LanguageChoices.FirstOrDefault(c => c.Same(target.Language)) ?? LanguageChoice.Of(target.Language);
        _default = target.Flags.Default;
        _enabled = target.Flags.Enabled;
        _forced = target.Flags.Forced;
        _removed = false;
        OnPropertyChanged(string.Empty);
    }

    private static string YesNo(bool value) => value ? "áno" : "nie";

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(property);
            OnPropertyChanged(nameof(Changes));
            _changed();
        }
    }
}
