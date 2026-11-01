using CommunityToolkit.Mvvm.ComponentModel;
using Universum.App.Localization;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>A language in the header editor's list.</summary>
public sealed record LanguageChoice(LanguageTag Tag, string Display)
{
    private static (IReadOnlyList<Language> Source, IReadOnlyList<LanguageChoice> Choices)? _common;

    /// <summary>"neurčený", the common languages, then every other language MKVToolNix knows (built again when they come).</summary>
    public static IReadOnlyList<LanguageChoice> Common
    {
        get
        {
            var all = Languages.All;
            if (_common is not { } built || !ReferenceEquals(built.Source, all))
            {
                built = (all, [Of(LanguageTag.Undetermined), .. all.Where(l => l != Languages.Undetermined).Select(l => Of(LanguageTag.FromCode(l.Ietf)))]);
                _common = built;
            }

            return built.Choices;
        }
    }

    public static LanguageChoice Of(LanguageTag tag)
    {
        if (tag.IsUndetermined)
        {
            return new LanguageChoice(LanguageTag.Undetermined, Loc.T("neurčený (und)"));
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
        TrackType.Video => Loc.T("Video"),
        TrackType.Audio => Loc.T("Zvuk"),
        TrackType.Subtitles => Loc.T("Titulky"),
        _ => Loc.T("Iné"),
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
                return Loc.T("vynechá sa");
            }

            var changes = new List<string>();
            if (!string.Equals(Name, Source.Name ?? "", StringComparison.Ordinal))
            {
                changes.Add(Loc.T("názov"));
            }

            if (!Language.Same(Source.Language))
            {
                changes.Add(Loc.T("jazyk"));
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
        Loc.F("V súbore: názov „{0}“, jazyk {1}, default {2}, enabled {3}, forced {4}", Source.Name, LanguageChoice.Of(Source.Language).Display,
            YesNo(Source.Flags.Default), YesNo(Source.Flags.Enabled), YesNo(Source.Flags.Forced));

    public TrackEdit ToEdit() => new(TrackId, Name.Length == 0 ? null : Name, Language.Tag, (_ruleFlags ?? Source.Flags) with { Default = IsDefault, Enabled = IsEnabled, Forced = IsForced });

    // the other flags the rules set (owner, 29. 10. 2026: the subtitles shown with foreign audio have no other flag)
    private TrackFlags? _ruleFlags;

    /// <summary>Takes the values of a target track (the UNIVERSUM rules) without raising a change per value.</summary>
    public void Take(TrackTarget target)
    {
        _name = target.Name ?? "";
        _language = LanguageChoices.FirstOrDefault(c => c.Same(target.Language)) ?? LanguageChoice.Of(target.Language);
        _default = target.Flags.Default;
        _enabled = target.Flags.Enabled;
        _forced = target.Flags.Forced;
        _ruleFlags = target.Flags;
        _removed = false;
        OnPropertyChanged(string.Empty);
    }

    private static string YesNo(bool value) => value ? Loc.T("áno") : Loc.T("nie");

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
