using Universum.Core.Batch;
using Universum.Core.Library;
using Universum.Core.Media;

namespace Universum.Cli;

internal static class Output
{
    public static void Print(BatchAnalysis analysis, IReadOnlyList<BatchItem> items, LibraryJob library, string journalDirectory)
    {
        var leftovers = analysis.LeftoverTemporaryFiles.Concat(library.Layout.FindLeftovers()).ToList();
        Console.WriteLine($"{analysis.FileCount} súborov v {analysis.Groups.Count} skupinách" +
                          (analysis.Failed.Count > 0 ? $", nečitateľné: {analysis.Failed.Count}" : "") +
                          (leftovers.Count > 0 ? $", zvyšky po prerušení: {leftovers.Count}" : ""));
        foreach (var leftover in leftovers)
        {
            Console.WriteLine("  zvyšok (možno vymazať): " + leftover);
        }

        foreach (var interrupted in library.Layout.FindInterrupted())
        {
            var home = Universum.Core.Execution.FileProcessor.OriginalPlaceOf(journalDirectory, interrupted);
            Console.WriteLine("  POZOR – nedokončený presun, možno jediná kópia (nemažte): " + interrupted +
                              (home is null ? "" : " – patrí do: " + home));
        }

        Console.WriteLine($"Knižnica: {library.Layout.Root}");
        if (library.Kind == MediaKind.Series)
        {
            Console.WriteLine(library.Series is { } series
                ? $"Seriál: {series.FolderName}"
                : $"Seriál: NEURČENÝ{(library.SeriesSuggestion is { } s ? $" – návrh „{s.FolderName}“ (--confirm-suggestions)" : "")} – zadajte --nazov a --rok");
        }
        else
        {
            Console.WriteLine("Filmy: názov každého súboru je uvedený pri ňom; jeden film zadajte --nazov a --rok");
        }
        foreach (var group in analysis.Groups)
        {
            Console.WriteLine();
            Console.WriteLine($"Skupina {group.Label} ({group.Files.Count}): {group.Signature}");
            foreach (var (key, track) in SlotKey.Of(group.Representative))
            {
                var evidence = BatchPlanner.Evidence(track, key, group);
                var type = key.Type == TrackType.Audio ? "audio" : "subtitles";
                var language = evidence is null ? "?" : evidence.Language.Primary;
                var source = evidence?.Source switch
                {
                    null => "NEURČENÝ – zadajte --language " + $"{group.Label}:{type}:{key.Ordinal + 1}=jazyk",
                    EvidenceSource.File => "zo súboru",
                    EvidenceSource.TrackName => "z názvu stopy",
                    EvidenceSource.GroupDecision => "zadaný",
                    _ => "návrh – potvrďte --confirm-suggestions alebo --language",
                };
                var forced = group.ForcedSuggestions.Contains(key) ? $", návrh forced: --forced {group.Label}:{type}:{key.Ordinal + 1}" : "";
                Console.WriteLine($"  {group.Label}:{type}:{key.Ordinal + 1}  {track.Codec,-14} {language,-4} {source}{forced}  {track.Name}");
            }

            foreach (var item in items.Where(i => i.Group == group))
            {
                var reasons = item.Reasons.Count == 0 ? "" : " – " + string.Join("; ", item.Reasons.Select(r => Reason(r, item, library)));
                var target = item.PlannedPath is { } planned ? " → " + Path.GetRelativePath(library.Layout.Root, planned) : "";
                Console.WriteLine($"    {Status(item.Status),-13} {Path.GetRelativePath(analysis.Folder, item.File.Path)}{target}{reasons}");
            }
        }

        Console.WriteLine();
    }

    private static string Reason(ReviewReason reason, BatchItem item, LibraryJob library) => reason.Kind switch
    {
        ReviewReasonKind.MissingTitle when reason.Detail is { } problem => $"názov {library.TitleFor(item.File.Path)?.FolderName}: {problem} (--nazov, --rok)",
        ReviewReasonKind.MissingTitle => library.SuggestedTitleFor(item.File.Path) is { } suggested
            ? $"názov: návrh „{suggested.FolderName}“ (--confirm-suggestions)"
            : "chýba názov (--nazov, --rok)",
        ReviewReasonKind.MissingEpisode => item.Episode is { } episode
            ? $"epizóda: návrh {episode.Tag}{(reason.Detail is { } doubt ? " – " + doubt : "")} (--confirm-suggestions alebo --epizoda)"
            : "chýba číslo epizódy (--epizoda)",
        ReviewReasonKind.DuplicateEpisode => $"rovnaká epizóda s rovnakým popisom ako {reason.Detail} (--epizoda, alebo --aj-tak: ďalšia verzia dostane číslo)",
        ReviewReasonKind.UnknownEpisode => "TMDB takú sezónu alebo epizódu nepozná (--epizoda alebo --aj-tak)",
        ReviewReasonKind.OtherSeries => $"meno súboru patrí inému titulu: {reason.Detail} (skontrolujte --nazov, alebo --aj-tak)",
        ReviewReasonKind.KindNotConfirmed => $"druh je len návrh ({reason.Detail}) – zadajte --film alebo --serial (alebo --confirm-suggestions)",
        ReviewReasonKind.ExtraFile => "doplnok (trailer, ukážka, bonus) – nie je film ani epizóda (--aj-tak)",
        ReviewReasonKind.SeasonQuestion => (reason.Detail ?? "OTÁZKA: Season 00 alebo bežná sezóna?") + " (--epizoda, alebo --specialy: všetky do Season 00)",
        ReviewReasonKind.EpisodeAmongFilms => "meno vyzerá ako epizóda seriálu – medzi filmami by bol verziou filmu (--serial alebo --aj-tak)",
        ReviewReasonKind.PathTooLong => "cesta v knižnici je príliš dlhá",
        ReviewReasonKind.NotSorted => "súbor nie je v MOVIES ani v TVSHOWS – presuňte ho do jedného z nich a dajte Analyzovať",
        _ => reason.Kind + (reason.Slot is { } s ? " " + s : ""),
    };

    private static string Status(BatchStatus status) => status switch
    {
        BatchStatus.Unchanged => "bez zmeny",
        BatchStatus.Place => "do knižnice",
        BatchStatus.HeaderEdit => "len hlavička",
        BatchStatus.Remux => "nový súbor",
        _ => "NA KONTROLU",
    };
}
