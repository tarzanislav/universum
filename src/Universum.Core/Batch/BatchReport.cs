using System.Net;
using System.Text;
using Universum.Core.Execution;

namespace Universum.Core.Batch;

/// <summary>Report of a batch for the user (Slovak): CSV for spreadsheets and a self-contained HTML page.</summary>
public static class BatchReport
{
    public static string Csv(IReadOnlyList<BatchItem> items, IReadOnlyList<ProcessingResult> results)
    {
        var builder = new StringBuilder("Súbor;Skupina;Výsledok;Výstup;Poznámky\n");
        foreach (var row in Rows(items, results))
        {
            builder.AppendLine(string.Join(";", row.Select(Quote)));
        }

        return builder.ToString();
    }

    public static string Html(string title, IReadOnlyList<BatchItem> items, IReadOnlyList<ProcessingResult> results)
    {
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html lang=\"sk\"><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(title)).Append("</title>");
        builder.Append("<style>body{font-family:Segoe UI,sans-serif;margin:24px}table{border-collapse:collapse}td,th{border-bottom:1px solid #ddd;padding:4px 10px;text-align:left;vertical-align:top}th{background:#f3f3f3}</style>");
        builder.Append("<h1>").Append(WebUtility.HtmlEncode(title)).Append("</h1><p>").Append(DateTime.Now.ToString("d. M. yyyy HH:mm")).Append("</p>");
        builder.Append("<table><tr><th>Súbor</th><th>Skupina</th><th>Výsledok</th><th>Výstup</th><th>Poznámky</th></tr>");
        foreach (var row in Rows(items, results))
        {
            builder.Append("<tr>");
            foreach (var cell in row)
            {
                builder.Append("<td>").Append(WebUtility.HtmlEncode(cell)).Append("</td>");
            }

            builder.Append("</tr>");
        }

        return builder.Append("</table></html>").ToString();
    }

    public static string Describe(BatchItem item, ProcessingResult? result) => result?.Status switch
    {
        ProcessingStatus.Done => result.Method switch
        {
            Planning.WriteMethod.Remux => "hotovo – nový súbor",
            Planning.WriteMethod.HeaderEdit => "hotovo – upravená hlavička",
            _ => "hotovo – v knižnici",
        },
        ProcessingStatus.AlreadyInLibrary => "už je v knižnici",
        ProcessingStatus.Failed when FileProcessor.NeedsAttention(result) => "CHYBA – niečo sa nepodarilo úplne vrátiť, pozri poznámky",
        ProcessingStatus.Failed when result.Messages.Contains(FileProcessor.RestoredMessage) => "chyba – originál vrátený do pôvodného stavu",
        ProcessingStatus.Failed => "chyba – originál nezmenený",
        ProcessingStatus.Cancelled => "zrušené",
        ProcessingStatus.NeedsReview => "na kontrolu",
        ProcessingStatus.Unchanged => "bez zmeny",
        _ => item.Status switch
        {
            BatchStatus.Unchanged => "bez zmeny",
            BatchStatus.NeedsReview => "na kontrolu",
            BatchStatus.Place => "do knižnice",
            _ => "nespracované",
        },
    };

    private static IEnumerable<string[]> Rows(IReadOnlyList<BatchItem> items, IReadOnlyList<ProcessingResult> results)
    {
        var byPath = results.GroupBy(r => r.SourcePath).ToDictionary(g => g.Key, g => g.Last());
        foreach (var item in items)
        {
            byPath.TryGetValue(item.File.Path, out var result);
            var notes = result?.Messages.Count > 0
                ? string.Join(" | ", result.Messages)
                : string.Join(" | ", item.Reasons.Select(r => r.Kind + (r.Slot is { } s ? " " + s : "")));
            yield return [item.File.Path, item.Group.Label, Describe(item, result), result?.OutputPath ?? item.PlannedPath ?? "", notes];
        }
    }

    private static string Quote(string value) => value.Contains(';') || value.Contains('"') || value.Contains('\n')
        ? "\"" + value.Replace("\"", "\"\"") + "\""
        : value;
}
