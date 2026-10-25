using Universum.Core.Batch;
using Universum.Core.Media;

namespace Universum.Cli;

internal static class Output
{
    public static void Print(BatchAnalysis analysis, IReadOnlyList<BatchItem> items)
    {
        Console.WriteLine($"{analysis.FileCount} súborov v {analysis.Groups.Count} skupinách" +
                          (analysis.Failed.Count > 0 ? $", nečitateľné: {analysis.Failed.Count}" : "") +
                          (analysis.LeftoverTemporaryFiles.Count > 0 ? $", zvyšky po prerušení: {analysis.LeftoverTemporaryFiles.Count}" : ""));
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
                var reasons = item.Reasons.Count == 0 ? "" : " – " + string.Join("; ", item.Reasons.Select(r => r.Kind + (r.Slot is { } s ? " " + s : "")));
                Console.WriteLine($"    {Status(item.Status),-13} {Path.GetRelativePath(analysis.Folder, item.File.Path)}{reasons}");
            }
        }

        Console.WriteLine();
    }

    private static string Status(BatchStatus status) => status switch
    {
        BatchStatus.Unchanged => "bez zmeny",
        BatchStatus.HeaderEdit => "len hlavička",
        BatchStatus.Remux => "nový súbor",
        _ => "NA KONTROLU",
    };
}
