using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>One track in the rules preview: the file now and after applying the rules.</summary>
public sealed class RuleRow : ObservableObject
{
    private readonly Action<RuleRow> _languageChosen;
    private readonly Action<RuleRow, int>? _move;
    private readonly Action<RuleRow, bool>? _keep;
    private Language? _chosenLanguage;

    /// <param name="removedByLanguage">Subtitles left out because their language is not in the list (owner, 29. 10. 2026).</param>
    /// <param name="kept">Subtitles out of the list the user keeps.</param>
    public RuleRow(
        TrackTarget target, int position, Language? chosenLanguage, Action<RuleRow> languageChosen,
        Action<RuleRow, int>? move = null, Action<RuleRow, bool>? keep = null, bool removedByLanguage = false, bool kept = false)
    {
        Target = target;
        Position = position;
        _languageChosen = languageChosen;
        _move = move;
        _keep = keep;
        IsRemovedByLanguage = removedByLanguage;
        Kept = kept;

        // owner, 29. 10. 2026: also a language written in the file can be corrected – the list shows the file's language
        _chosenLanguage = chosenLanguage ?? (CanChooseLanguage && !NeedsLanguage ? Languages.Find(target.Source.Language.Primary) : null);
        MoveUpCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _move?.Invoke(this, -1), () => CanMove);
        MoveDownCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _move?.Invoke(this, 1), () => CanMove);
    }

    public TrackTarget Target { get; }

    /// <summary>The language is UNIVERSUM's suggestion read from the subtitles' text ("návrh UNIVERSUM: isté – ř, ě 12×").</summary>
    public string LanguageNote { get; init; } = "";

    public bool HasLanguageNote => LanguageNote.Length > 0;

    /// <summary>The subtitles are forced by their small amount ("návrh UNIVERSUM: forced – 85 titulkov oproti 1 240 …").</summary>
    public string ForcedNote { get; init; } = "";

    /// <summary>Called when the user ticks or unticks Forced.</summary>
    public Action<RuleRow, bool>? ForcedChanged { get; init; }

    public bool IsSubtitles => Target.Type == TrackType.Subtitles && !IsRemovedByLanguage;

    /// <summary>Forced as the rules have it; the user can change it (owner, 30. 10. 2026).</summary>
    public bool IsForced
    {
        get => Target.Role == TrackRole.Forced;
        set
        {
            if (value != IsForced)
            {
                ForcedChanged?.Invoke(this, value);
            }
        }
    }

    public int TrackId => Target.TrackId;

    /// <summary>Position after applying the rules (1-based).</summary>
    public int Position { get; }

    /// <summary>"3", or "–" for subtitles left out.</summary>
    public string PositionText => IsRemovedByLanguage ? "–" : Position.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The subtitles are left out: their language is not among the subtitle languages (Nastavenia) nor the audio's.</summary>
    public bool IsRemovedByLanguage { get; }

    /// <summary>Subtitles out of the language list the user keeps.</summary>
    public bool Kept { get; }

    /// <summary>The user may keep these subtitles (or leave them out again).</summary>
    public bool CanKeep => Target.Type == TrackType.Subtitles && (IsRemovedByLanguage || Kept);

    /// <summary>The check box "ponechať": keeps the subtitles although their language is not in the list.</summary>
    public bool KeepChecked
    {
        get => Kept;
        set
        {
            if (value != Kept)
            {
                _keep?.Invoke(this, value);
            }
        }
    }

    public string ResultNote => IsRemovedByLanguage ? "odstráni sa – jazyk nie je v zozname titulkov (Nastavenia)"
        : Kept ? "ponechané – jazyk mimo zoznamu titulkov"
        : "";

    /// <summary>Audio and subtitles can be moved within their type (owner, 29. 10. 2026: dragged up or down).</summary>
    public bool CanMove => Target.Type is TrackType.Audio or TrackType.Subtitles && !IsRemovedByLanguage;

    public CommunityToolkit.Mvvm.Input.RelayCommand MoveUpCommand { get; }

    public CommunityToolkit.Mvvm.Input.RelayCommand MoveDownCommand { get; }

    /// <summary>The language of audio and subtitles can be chosen (an undetermined one, or a wrong one written in the file).</summary>
    public bool CanChooseLanguage => Target.Type is TrackType.Audio or TrackType.Subtitles && !IsRemovedByLanguage;

    /// <summary>The flags beyond default, enabled and forced that change, e.g. "original: áno → NIE".</summary>
    public string OtherFlagsAfter
    {
        get
        {
            var (before, after) = (Target.Source.Flags, Target.Flags);
            var changes = new List<string>();
            void Add(string name, bool was, bool will)
            {
                if (was != will)
                {
                    changes.Add($"{name}: {Flag(was, will)}");
                }
            }

            Add("pre nepočujúcich", before.HearingImpaired, after.HearingImpaired);
            Add("pre nevidiacich", before.VisualImpaired, after.VisualImpaired);
            Add("textový opis", before.TextDescriptions, after.TextDescriptions);
            Add("original", before.Original, after.Original);
            Add("komentár", before.Commentary, after.Commentary);
            return string.Join("\n", changes);
        }
    }

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
