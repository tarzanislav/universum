using Universum.App.Localization;
using Universum.Core.Batch;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>Slovak texts for values of the core.</summary>
public static class Texts
{
    /// <summary>Slovak number and date formats (thousands separated by a space).</summary>
    public static System.Globalization.CultureInfo Slovak { get; } = System.Globalization.CultureInfo.GetCultureInfo("sk-SK");

    /// <summary>A count with the Slovak form of the noun: "1 list", "3 listy", "5 listov".</summary>
    public static string Count(int count, string one, string few, string many) =>
        $"{count.ToString("N0", Slovak)} {(count == 1 ? one : count is >= 2 and <= 4 ? few : many)}";

    public static IReadOnlyList<Choice<TrackType>> TrackTypes { get; } =
        [new(TrackType.Audio, Loc.T("Zvuk")), new(TrackType.Subtitles, Loc.T("Titulky"))];

    public static IReadOnlyList<Choice<TrackRole>> Roles { get; } =
        [new(TrackRole.Main, Loc.T("bežná")), new(TrackRole.Forced, Loc.T("forced")), new(TrackRole.HearingImpaired, Loc.T("pre nepočujúcich")), new(TrackRole.Commentary, Loc.T("komentár"))];

    public static string Type(TrackType type) => type switch
    {
        TrackType.Video => Loc.T("Video"),
        TrackType.Audio => Loc.T("Zvuk"),
        TrackType.Subtitles => Loc.T("Titulky"),
        _ => Loc.T("Iné"),
    };

    /// <summary>One line about a file: container, length, tracks, chapters, tags, attachments.</summary>
    public static string FileSummary(MediaFile file) =>
        Loc.F("{0}, trvanie {1:hh\\:mm\\:ss}, stopy: {2}, kapitoly: {3}, " +
              "tagy: {4}, prílohy: {5}", file.ContainerType, file.Duration, file.Tracks.Count, file.ChapterEntries,
              file.GlobalTagEntries + file.Tracks.Sum(t => t.TagEntries), file.AttachmentCount);

    public static string Role(TrackRole role) => Roles.First(r => r.Value == role).Text;

    public static string Slot(SlotKey key) => $"{Type(key.Type)} {key.Ordinal + 1}";

    public static string Language(LanguageTag tag)
    {
        if (tag.IsUndetermined)
        {
            return Loc.T("neurčený (und)");
        }

        var language = Languages.Find(tag.Primary);
        return language is null ? tag.ToString() : $"{language.Name} ({tag.Primary})";
    }

    /// <summary>
    /// What a language detection found: "Titulky: 2 rozpoznané z textu (návrh pri stope – potvrďte ho), 1 sa rozpoznať
    /// nepodarilo, 1 má jazyk z názvu stopy."
    /// </summary>
    /// <param name="cannot">Tracks that cannot be read at all ("1 obrazové sa čítať nedajú"), or null.</param>
    public static string Detection(string what, int recognised, int failed, int named, string? cannot = null)
    {
        var parts = new List<string>();
        if (recognised > 0)
        {
            parts.Add(Loc.F("{0} rozpoznané (návrh pri stope – potvrďte ho tlačidlom Potvrdiť návrhy)", recognised));
        }

        if (failed > 0)
        {
            parts.Add(Loc.F("{0} sa rozpoznať nepodarilo – jazyk vyberte pri stope", failed));
        }

        if (named > 0)
        {
            parts.Add(Loc.F("{0} má jazyk z názvu stopy (už je vybraný)", named));
        }

        if (cannot is not null)
        {
            parts.Add(cannot);
        }

        return parts.Count == 0 ? Loc.F("{0}: nič na rozpoznanie – všetky majú jazyk.", what) : $"{what}: {string.Join(", ", parts)}.";
    }

    public static string Evidence(LanguageEvidence? evidence) => evidence?.Source switch
    {
        null => Loc.T("neznámy – vyberte jazyk"),
        EvidenceSource.File => Loc.T("zo súboru"),
        EvidenceSource.TrackName => Loc.T("z názvu stopy"),
        EvidenceSource.GroupDecision => Loc.T("vaša voľba"),
        EvidenceSource.FileDecision => Loc.T("vaša voľba pre súbor"),
        EvidenceSource.Similarity => Loc.T("NÁVRH podľa podobnej stopy – potvrďte"),
        EvidenceSource.SubtitleText => Loc.T("NÁVRH podľa textu titulkov – potvrďte"),
        EvidenceSource.SubtitleAnalysis => Loc.T("NÁVRH UNIVERSUM podľa textu titulkov") + (evidence?.Detail is { } read ? $" ({read})" : "") + Loc.T(" – iný jazyk vyberte"),
        EvidenceSource.Speech => Loc.T("NÁVRH podľa reči – potvrďte") + (evidence?.Detail is { } detail ? $" ({detail})" : ""),
        _ => "",
    };

    public static string Status(BatchStatus status) => status switch
    {
        BatchStatus.Unchanged => Loc.T("bez zmeny"),
        BatchStatus.Place => Loc.T("do knižnice"),
        BatchStatus.HeaderEdit => Loc.T("len hlavička"),
        BatchStatus.Remux => Loc.T("nový súbor"),
        _ => Loc.T("na kontrolu"),
    };

    public static string Reason(ReviewReason reason) => reason.Kind switch
    {
        ReviewReasonKind.UndeterminedLanguage => Loc.F("neurčený jazyk: {0}", reason.Slot is { } slot ? Slot(slot) : Loc.T("stopa")),
        ReviewReasonKind.MissingRequiredSlot when reason.TemplateSlot is { } missing =>
            Loc.F("chýba povinná stopa: {0} {1}, {2}", Type(missing.Type), Language(LanguageTag.FromCode(missing.Language)), Role(missing.Role)),
        ReviewReasonKind.MultipleVideoTracks => Loc.T("viac video stôp"),
        ReviewReasonKind.NoAudio => Loc.T("bez zvuku"),
        ReviewReasonKind.MissingTitle when reason.Detail is { } problem => Loc.T("názov titulu: ") + problem,
        ReviewReasonKind.MissingTitle => Loc.T("chýba názov titulu"),
        ReviewReasonKind.MissingEpisode when reason.Detail is { } doubt => Loc.T("číslo epizódy treba potvrdiť: ") + doubt,
        ReviewReasonKind.MissingEpisode => Loc.T("chýba číslo epizódy"),
        ReviewReasonKind.DuplicateEpisode => Loc.F("rovnaká epizóda s rovnakým popisom ako {0} – opravte číslo, alebo „spracovať aj tak“ (ďalšia verzia dostane číslo)", reason.Detail),
        ReviewReasonKind.UnknownEpisode => Loc.T("TMDB takú sezónu alebo epizódu nepozná – opravte číslo, alebo „spracovať aj tak“"),
        ReviewReasonKind.OtherSeries => Loc.F("meno súboru patrí inému titulu ({0}) – skontrolujte názov, alebo „spracovať aj tak“", reason.Detail),
        ReviewReasonKind.KindNotConfirmed => Loc.F("druh ({0}) je len návrh – potvrďte ho alebo zvoľte druhý", reason.Detail),
        ReviewReasonKind.ExtraFile => Loc.T("doplnok (trailer, ukážka, bonus) – nie je film ani epizóda"),
        ReviewReasonKind.SeasonQuestion => (reason.Detail ?? Loc.T("OTÁZKA: Season 00 alebo bežná sezóna?")) + Loc.T(" – alebo tlačidlo „Špeciály do Season 00“"),
        ReviewReasonKind.EpisodeAmongFilms => Loc.T("meno vyzerá ako epizóda seriálu – medzi filmami by bol verziou filmu; zvoľte seriál alebo „spracovať aj tak“"),
        ReviewReasonKind.PathTooLong => Loc.T("cesta v knižnici je príliš dlhá"),
        ReviewReasonKind.NotSorted => Loc.T("súbor nie je v MOVIES ani v TVSHOWS – presuňte ho do jedného z nich a dajte Analyzovať"),
        ReviewReasonKind.InMegaBatch => Loc.F("v MEGA spracovaní ({0}) – zapíše ho tá dávka", reason.Detail),
        _ => reason.Kind.ToString(),
    };
}
