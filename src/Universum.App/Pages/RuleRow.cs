using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>One track in the rules preview: the file now and after applying the rules.</summary>
public sealed class RuleRow : ObservableObject
{
    private readonly Action<RuleRow> _languageChosen;
    private Language? _chosenLanguage;

    public RuleRow(TrackTarget target, int position, Language? chosenLanguage, Action<RuleRow> languageChosen)
    {
        Target = target;
        Position = position;
        _chosenLanguage = chosenLanguage;
        _languageChosen = languageChosen;
    }

    public TrackTarget Target { get; }

    public int TrackId => Target.TrackId;

    /// <summary>Position after applying the rules (1-based).</summary>
    public int Position { get; }

    public string Type => Target.Type switch
    {
        TrackType.Video => "Video",
        TrackType.Audio => "Zvuk",
        TrackType.Subtitles => "Titulky",
        _ => "Iné",
    };

    public string Role => Target.Role switch
    {
        TrackRole.Forced => "forced",
        TrackRole.HearingImpaired => "pre nepočujúcich",
        TrackRole.Commentary => "komentár",
        _ => "",
    };

    public string Codec => Target.Source.Codec;

    /// <summary>The track has no language in the file; the user picks one.</summary>
    public bool NeedsLanguage => Target.Source.Language.IsUndetermined && Target.Type is TrackType.Audio or TrackType.Subtitles;

    public IReadOnlyList<Language> LanguageChoices => Languages.All;

    public Language? ChosenLanguage
    {
        get => _chosenLanguage;
        set
        {
            if (SetProperty(ref _chosenLanguage, value))
            {
                _languageChosen(this);
            }
        }
    }

    public string LanguageBefore => Describe(Target.Source.Language);

    public string LanguageAfter => Describe(Target.Language);

    public string NameBefore => Target.Source.Name ?? "";

    public string NameAfter => Target.Name ?? "";

    public bool LanguageChanged => Target.LanguageChanged;

    public bool NameChanged => Target.NameChanged;

    public string DefaultAfter => Flag(Target.Source.Flags.Default, Target.Flags.Default);

    public string EnabledAfter => Flag(Target.Source.Flags.Enabled, Target.Flags.Enabled);

    public string ForcedAfter => Flag(Target.Source.Flags.Forced, Target.Flags.Forced);

    public bool FlagsChanged => Target.FlagsChanged;

    private static string Flag(bool before, bool after) =>
        before == after ? (after ? "áno" : "nie") : (after ? "nie → ÁNO" : "áno → NIE");

    private static string Describe(LanguageTag tag)
    {
        if (tag.IsUndetermined)
        {
            return "neurčený (und)";
        }

        var language = Languages.Find(tag.Primary);
        return language is null ? tag.ToString() : $"{language.SlovakName} ({tag.Primary})";
    }
}
