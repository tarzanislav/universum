using Universum.Core.Batch;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>Slovak texts for values of the core.</summary>
public static class Texts
{
    public static IReadOnlyList<Choice<TrackType>> TrackTypes { get; } =
        [new(TrackType.Audio, "Zvuk"), new(TrackType.Subtitles, "Titulky")];

    public static IReadOnlyList<Choice<TrackRole>> Roles { get; } =
        [new(TrackRole.Main, "bežná"), new(TrackRole.Forced, "forced"), new(TrackRole.HearingImpaired, "pre nepočujúcich"), new(TrackRole.Commentary, "komentár")];

    public static string Type(TrackType type) => type switch
    {
        TrackType.Video => "Video",
        TrackType.Audio => "Zvuk",
        TrackType.Subtitles => "Titulky",
        _ => "Iné",
    };

    /// <summary>One line about a file: container, length, tracks, chapters, tags, attachments.</summary>
    public static string FileSummary(MediaFile file) =>
        $"{file.ContainerType}, trvanie {file.Duration:hh\\:mm\\:ss}, stopy: {file.Tracks.Count}, kapitoly: {file.ChapterEntries}, " +
        $"tagy: {file.GlobalTagEntries + file.Tracks.Sum(t => t.TagEntries)}, prílohy: {file.AttachmentCount}";

    public static string Role(TrackRole role) => Roles.First(r => r.Value == role).Text;

    public static string Slot(SlotKey key) => $"{Type(key.Type)} {key.Ordinal + 1}";

    public static string Language(LanguageTag tag)
    {
        if (tag.IsUndetermined)
        {
            return "neurčený (und)";
        }

        var language = Languages.Find(tag.Primary);
        return language is null ? tag.ToString() : $"{language.SlovakName} ({tag.Primary})";
    }

    public static string Evidence(LanguageEvidence? evidence) => evidence?.Source switch
    {
        null => "neznámy – vyberte jazyk",
        EvidenceSource.File => "zo súboru",
        EvidenceSource.TrackName => "z názvu stopy",
        EvidenceSource.GroupDecision => "vaša voľba",
        EvidenceSource.Similarity => "NÁVRH podľa podobnej stopy – potvrďte",
        EvidenceSource.SubtitleText => "NÁVRH podľa textu titulkov – potvrďte",
        _ => "",
    };

    public static string Status(BatchStatus status) => status switch
    {
        BatchStatus.Unchanged => "bez zmeny",
        BatchStatus.Place => "do knižnice",
        BatchStatus.HeaderEdit => "len hlavička",
        BatchStatus.Remux => "nový súbor",
        _ => "na kontrolu",
    };

    public static string Reason(ReviewReason reason) => reason.Kind switch
    {
        ReviewReasonKind.UndeterminedLanguage => $"neurčený jazyk: {(reason.Slot is { } slot ? Slot(slot) : "stopa")}",
        ReviewReasonKind.MissingRequiredSlot when reason.TemplateSlot is { } missing =>
            $"chýba povinná stopa: {Type(missing.Type)} {Language(LanguageTag.FromCode(missing.Language))}, {Role(missing.Role)}",
        ReviewReasonKind.MultipleVideoTracks => "viac video stôp",
        ReviewReasonKind.NoAudio => "bez zvuku",
        ReviewReasonKind.MissingTitle when reason.Detail is { } problem => "názov titulu: " + problem,
        ReviewReasonKind.MissingTitle => "chýba názov titulu",
        ReviewReasonKind.MissingEpisode when reason.Detail is { } doubt => "číslo epizódy treba potvrdiť: " + doubt,
        ReviewReasonKind.MissingEpisode => "chýba číslo epizódy",
        ReviewReasonKind.DuplicateEpisode => $"rovnaká epizóda s rovnakým popisom ako {reason.Detail} – opravte číslo, alebo „spracovať aj tak“ (ďalšia verzia dostane číslo)",
        ReviewReasonKind.UnknownEpisode => "TMDB takú sezónu alebo epizódu nepozná – opravte číslo, alebo „spracovať aj tak“",
        ReviewReasonKind.OtherSeries => $"meno súboru patrí inému titulu ({reason.Detail}) – skontrolujte názov, alebo „spracovať aj tak“",
        ReviewReasonKind.KindNotConfirmed => $"druh ({reason.Detail}) je len návrh – potvrďte ho alebo zvoľte druhý",
        ReviewReasonKind.ExtraFile => "doplnok (trailer, ukážka, bonus) – nie je film ani epizóda",
        ReviewReasonKind.SeasonQuestion => (reason.Detail ?? "OTÁZKA: Season 00 alebo bežná sezóna?") + " – alebo tlačidlo „Špeciály do Season 00“",
        ReviewReasonKind.EpisodeAmongFilms => "meno vyzerá ako epizóda seriálu – medzi filmami by bol verziou filmu; zvoľte seriál alebo „spracovať aj tak“",
        ReviewReasonKind.PathTooLong => "cesta v knižnici je príliš dlhá",
        _ => reason.Kind.ToString(),
    };
}
