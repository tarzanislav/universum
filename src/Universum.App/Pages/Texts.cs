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
            parts.Add($"{recognised} rozpoznané (návrh pri stope – potvrďte ho tlačidlom Potvrdiť návrhy)");
        }

        if (failed > 0)
        {
            parts.Add($"{failed} sa rozpoznať nepodarilo – jazyk vyberte pri stope");
        }

        if (named > 0)
        {
            parts.Add($"{named} má jazyk z názvu stopy (už je vybraný)");
        }

        if (cannot is not null)
        {
            parts.Add(cannot);
        }

        return parts.Count == 0 ? $"{what}: nič na rozpoznanie – všetky majú jazyk." : $"{what}: {string.Join(", ", parts)}.";
    }

    public static string Evidence(LanguageEvidence? evidence) => evidence?.Source switch
    {
        null => "neznámy – vyberte jazyk",
        EvidenceSource.File => "zo súboru",
        EvidenceSource.TrackName => "z názvu stopy",
        EvidenceSource.GroupDecision => "vaša voľba",
        EvidenceSource.FileDecision => "vaša voľba pre súbor",
        EvidenceSource.Similarity => "NÁVRH podľa podobnej stopy – potvrďte",
        EvidenceSource.SubtitleText => "NÁVRH podľa textu titulkov – potvrďte",
        EvidenceSource.Speech => "NÁVRH podľa reči – potvrďte" + (evidence?.Detail is { } detail ? $" ({detail})" : ""),
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
        ReviewReasonKind.NotSorted => "súbor nie je v MOVIES ani v TVSHOWS – presuňte ho do jedného z nich a dajte Analyzovať",
        ReviewReasonKind.InMegaBatch => $"v MEGA spracovaní ({reason.Detail}) – zapíše ho tá dávka",
        _ => reason.Kind.ToString(),
    };
}
