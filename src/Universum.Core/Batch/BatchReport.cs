using System.Net;
using System.Text;
using Universum.Core.Execution;
using Universum.Core.Text;

namespace Universum.Core.Batch;

/// <summary>Report of a batch for the user (Slovak): CSV for spreadsheets and a self-contained HTML page.</summary>
public static class BatchReport
{
    public static string Csv(IReadOnlyList<BatchItem> items, IReadOnlyList<ProcessingResult> results)
    {
        var builder = new StringBuilder(CoreText.T("Súbor;Skupina;Výsledok;Výstup;Poznámky") + "\n");
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
        builder.Append("<table><tr><th>" + CoreText.T("Súbor") + "</th><th>" + CoreText.T("Skupina") + "</th><th>" + CoreText.T("Výsledok") +
                       "</th><th>" + CoreText.T("Výstup") + "</th><th>" + CoreText.T("Poznámky") + "</th></tr>");
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
            Planning.WriteMethod.Remux => CoreText.T("hotovo – nový súbor"),
            Planning.WriteMethod.HeaderEdit => CoreText.T("hotovo – upravená hlavička"),
            _ => CoreText.T("hotovo – v knižnici"),
        },
        ProcessingStatus.AlreadyInLibrary => CoreText.T("už je v knižnici"),
        ProcessingStatus.Failed when FileProcessor.NeedsAttention(result) => CoreText.T("CHYBA – niečo sa nepodarilo úplne vrátiť, pozri poznámky"),
        ProcessingStatus.Failed when result.Messages.Contains(FileProcessor.RestoredMessage) => CoreText.T("chyba – originál vrátený do pôvodného stavu"),
        ProcessingStatus.Failed => CoreText.T("chyba – originál nezmenený"),
        ProcessingStatus.Cancelled => CoreText.T("zrušené"),
        ProcessingStatus.NeedsReview => CoreText.T("na kontrolu"),
        ProcessingStatus.Unchanged => CoreText.T("bez zmeny"),
        _ => item.Status switch
        {
            BatchStatus.Unchanged => CoreText.T("bez zmeny"),
            BatchStatus.NeedsReview => CoreText.T("na kontrolu"),
            BatchStatus.Place => CoreText.T("do knižnice"),
            _ => CoreText.T("nespracované"),
        },
    };

    private static IEnumerable<string[]> Rows(IReadOnlyList<BatchItem> items, IReadOnlyList<ProcessingResult> results)
    {
        var byPath = results.GroupBy(r => r.SourcePath).ToDictionary(g => g.Key, g => g.Last());
        foreach (var item in items)
        {
            byPath.TryGetValue(item.File.Path, out var result);

            // in words (owner, 31. 10. 2026: the tools' messages said nothing): the problems with what to do, then what was done
            string notes;
            if (result is not null)
            {
                var words = ProcessingWords.Describe(item.File.Path, item.Plan, result);
                notes = string.Join(" | ", words.Problems.Select(p => CoreText.F("{0} Čo s tým: {1}", p.What, p.Advice)).Concat(words.Done));
            }
            else
            {
                notes = string.Join(" | ", item.Reasons.Select(r => r.Kind + (r.Slot is { } s ? " " + s : "")));
            }
            yield return [item.File.Path, item.Group.Label, Describe(item, result), result?.OutputPath ?? item.PlannedPath ?? "", notes];
        }
    }

    private static string Quote(string value) => value.Contains(';') || value.Contains('"') || value.Contains('\n')
        ? "\"" + value.Replace("\"", "\"\"") + "\""
        : value;
}
