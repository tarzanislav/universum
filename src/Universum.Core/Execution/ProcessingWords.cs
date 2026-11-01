using System.Text.RegularExpressions;
using Universum.Core.Jobs;
using Universum.Core.Planning;
using Universum.Core.Text;

namespace Universum.Core.Execution;

/// <summary>
/// The processing of a file in words for the logs (owner, 31. 10. 2026: "z toho HTML som nič nevedel vyčítať"): what was
/// to be changed, how it was written, how it ended; every message of the processing and of the tools turned into a
/// sentence with what to do. A message it does not know is kept among the technical lines.
/// </summary>
public static partial class ProcessingWords
{
    /// <summary>One file processed (<paramref name="plan"/> null: what was to be done is not known).</summary>
    public static LoggedFile Describe(string source, EditPlan? plan, ProcessingResult result)
    {
        var done = new List<string>();
        var problems = new List<LoggedProblem>();
        var technical = new List<string>();
        var outcome = result.Status switch
        {
            ProcessingStatus.Done => LoggedOutcome.Done,
            ProcessingStatus.AlreadyInLibrary => LoggedOutcome.AlreadyInLibrary,
            ProcessingStatus.Unchanged => LoggedOutcome.Unchanged,
            ProcessingStatus.NeedsReview => LoggedOutcome.Skipped,
            ProcessingStatus.Cancelled => LoggedOutcome.Cancelled,
            _ when FileProcessor.NeedsAttention(result) => LoggedOutcome.Attention,
            _ => LoggedOutcome.Failed,
        };

        if (plan is not null && result.Status is ProcessingStatus.Done or ProcessingStatus.Failed or ProcessingStatus.Cancelled)
        {
            done.AddRange(plan.Changes.Select(Change));
            if (result.Status == ProcessingStatus.Done)
            {
                done.Add(plan.Method switch
                {
                    WriteMethod.Remux => CoreText.T("Zapísaný nový súbor (mkvmerge) a skontrolovaný: stopy, jazyky, príznaky, názvy a dĺžka sedia."),
                    WriteMethod.HeaderEdit => CoreText.T("Upravená len hlavička súboru (mkvpropedit) a skontrolovaná."),
                    _ => CoreText.T("Súbor sa nemusel meniť – len sa uložil do knižnice."),
                });
            }
        }

        foreach (var message in result.Messages)
        {
            switch (Explain(message, result.Status == ProcessingStatus.Failed))
            {
                case (string step, null):
                    done.Add(step);
                    break;
                case (_, LoggedProblem problem):
                    problems.Add(problem);
                    technical.Add(message);
                    break;
                default:
                    technical.Add(message);
                    break;
            }
        }

        var headline = result.Status switch
        {
            ProcessingStatus.Done when plan?.Method == WriteMethod.Remux => CoreText.T("Hotovo – nový súbor v knižnici"),
            ProcessingStatus.Done when plan?.Method == WriteMethod.HeaderEdit => CoreText.T("Hotovo – upravená hlavička, súbor v knižnici"),
            ProcessingStatus.Done => CoreText.T("Hotovo – súbor je v knižnici"),
            ProcessingStatus.AlreadyInLibrary => CoreText.T("Preskočené – rovnaký výsledok z tohto originálu už v knižnici je"),
            ProcessingStatus.Unchanged => CoreText.T("Bez zmeny – súbor už je v knižnici na svojom mieste"),
            ProcessingStatus.NeedsReview => CoreText.T("Nespracované – súbor treba najprv skontrolovať v Hromadnom alebo Single spracovaní"),
            ProcessingStatus.Cancelled => CoreText.T("Zrušené – originál ostal nezmenený"),
            _ when outcome == LoggedOutcome.Attention => CoreText.T("POZOR – nepodarilo sa a originál sa nepodarilo úplne vrátiť"),
            _ when result.Messages.Contains(FileProcessor.RestoredMessage) => CoreText.T("Chyba – originál je vrátený do pôvodného stavu"),
            _ => CoreText.T("Chyba – originál ostal nezmenený"),
        };

        if (outcome == LoggedOutcome.AlreadyInLibrary && result.OutputPath is { } found)
        {
            done.Add(CoreText.F("Výsledok z rovnakého originálu s rovnakými rozhodnutiami už je v knižnici: {0} – nič sa nezapísalo.", Path.GetFileName(found)));
        }

        if (outcome is LoggedOutcome.Failed or LoggedOutcome.Attention && problems.Count == 0)
        {
            problems.Add(new LoggedProblem(CoreText.T("Spracovanie sa nepodarilo (podrobnosti sú v technických riadkoch)."), CoreText.T("Skúste súbor spracovať znova; ak sa chyba opakuje, otvorte ho v Informáciách alebo v MKVToolNix GUI.")));
        }

        return new LoggedFile(source, result.OutputPath, outcome, headline, done, problems, technical);
    }

    /// <summary>A file not processed: skipped before the run (a MEGA batch's check), with the reason in words.</summary>
    public static LoggedFile Skipped(string source, string reason, bool alreadyInLibrary = false) => alreadyInLibrary
        ? new LoggedFile(source, null, LoggedOutcome.AlreadyInLibrary, CoreText.T("Preskočené – už je v knižnici"), [], [], [])
        : new LoggedFile(source, null, LoggedOutcome.Skipped, CoreText.T("Preskočené pri kontrole pred spustením"), [],
            [new LoggedProblem(Capital(reason) + ".", CoreText.T("Otvorte súbor znova v Hromadnom spracovaní, skontrolujte ho a pošlite ho do dávky znova."))], []);

    // one planned change as a sentence
    private static string Change(PlannedChange change)
    {
        var track = change.TrackId is { } id ? CoreText.F("Stopa {0}: ", id) : "";
        return change.Kind switch
        {
            ChangeKind.Container => CoreText.F("Kontajner {0} sa zmení na MKV.", change.Before),
            ChangeKind.Added => track + CoreText.F("pridaná zo súboru {0}.", change.After),
            ChangeKind.Position => track + CoreText.F("presunutá z {0}. na {1}. miesto.", change.Before, change.After),
            ChangeKind.Timing => track + CoreText.F("nové časy titulkov – {0}.", change.After),
            ChangeKind.Name => track + CoreText.F("názov „{0}“ → „{1}“.", change.Before, change.After),
            ChangeKind.Language => track + CoreText.F("jazyk {0} → {1}.", change.Before, change.After),
            ChangeKind.Default => track + CoreText.F("predvolená (Default) {0} → {1}.", YesNo(change.Before), YesNo(change.After)),
            ChangeKind.Enabled => track + CoreText.F("zapnutá (Enabled) {0} → {1}.", YesNo(change.Before), YesNo(change.After)),
            ChangeKind.Forced => track + CoreText.F("vynútená (Forced) {0} → {1}.", YesNo(change.Before), YesNo(change.After)),
            ChangeKind.OtherFlags => track + CoreText.F("ďalšie príznaky {0} → {1}.", change.Before, change.After),
            ChangeKind.Removed => track + CoreText.F("odstránená (bola {0}.).", change.Before),
            ChangeKind.Index => CoreText.T("Doplnený index (Cues) – posúvanie v prehrávači bude rýchle."),
            ChangeKind.Tags => CoreText.T("Odstránené tagy (údaje, ktoré pridali iné programy)."),
            ChangeKind.Title => CoreText.F("Názov v hlavičke „{0}“ → „{1}“.", change.Before, change.After),
            ChangeKind.Chapters => CoreText.F("Kapitoly: {0} → {1}.", change.Before, change.After),
            _ => $"{track}{change.Kind}: {change.Before} → {change.After}.",
        };
    }

    private static string YesNo(string? flag) => flag switch
    {
        "True" or "true" or "1" => CoreText.T("áno"),
        "False" or "false" or "0" => CoreText.T("nie"),
        _ => flag ?? "–",
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// A message of the processing in words: a step that was done (problem null), or a problem with what to do; null for a
    /// tool's line that says nothing more (kept among the technical lines). A message already in Slovak is a step – or a
    /// problem when the processing <paramref name="failed"/> (in Czech and English: a translation of UNIVERSUM's own text).
    /// </summary>
    public static (string? Step, LoggedProblem? Problem)? Explain(string message, bool failed = false)
    {
        string Tail(string prefix) => message[prefix.Length..];
        if (message == FileProcessor.OriginalRecycledMessage)
        {
            return (CoreText.T("Originál je v Koši (výsledok je zapísaný a skontrolovaný)."), null);
        }

        if (message == FileProcessor.RestoredMessage)
        {
            return (CoreText.T("Originál je vrátený do pôvodného stavu."), null);
        }

        if (message.StartsWith(FileProcessor.ReplacedPreviousPrefix, StringComparison.Ordinal))
        {
            return (CoreText.F("Starší výsledok toho istého originálu (s inými rozhodnutiami) je v Koši: {0}.", Tail(FileProcessor.ReplacedPreviousPrefix)), null);
        }

        if (message.StartsWith(FileProcessor.OriginalKeptPrefix, StringComparison.Ordinal))
        {
            return (CoreText.F("Originál ostal na mieste – {0}.", Kept(Tail(FileProcessor.OriginalKeptPrefix))), null);
        }

        if (message.Contains(FileProcessor.RestoreFailedPrefix, StringComparison.Ordinal))
        {
            return (null, new LoggedProblem(
                CoreText.T("Originál sa po chybe nepodarilo úplne vrátiť do pôvodného stavu: ") + message[(message.IndexOf(FileProcessor.RestoreFailedPrefix, StringComparison.Ordinal) + FileProcessor.RestoreFailedPrefix.Length)..],
                CoreText.T("Nič nemažte. Vo Fronte úloh je pri úlohe Vrátiť späť; ak nepomôže, pozrite priečinok originálu – súbor s príponou .universum-old alebo .universum-moving je pôvodný.")));
        }

        foreach (var (pattern, explain) in Known)
        {
            if (pattern.Match(message) is { Success: true } match)
            {
                return explain(match);
            }
        }

        if (message.Any(c => "áäčďéíĺľňóôŕšťúýž".Contains(char.ToLowerInvariant(c), StringComparison.Ordinal)) || CoreText.IsOwn(message))
        {
            var sentence = Capital(message.TrimEnd('.')) + ".";
            return failed ? (null, new LoggedProblem(sentence, CoreText.T("Skúste súbor spracovať znova; ak sa to opakuje, otvorte ho v Informáciách."))) : (sentence, null);
        }

        // the tools' own lines (warnings of mkvmerge) say nothing the user has to do
        return null;
    }

    private static string Kept(string reason) => reason switch
    {
        "it is a result of a library" => CoreText.T("je to výsledok knižnice, nie originál"),
        _ when reason.StartsWith("it cannot go to the Recycle Bin", StringComparison.Ordinal) => CoreText.T("nedá sa presunúť do Koša") + reason["it cannot go to the Recycle Bin".Length..],
        _ => reason,
    };

    private static readonly (Regex Pattern, Func<Match, (string?, LoggedProblem?)?> Explain)[] Known =
    [
        (Rx("^the file no longer exists"), _ => (null, new LoggedProblem(CoreText.T("Súbor už na svojom mieste nie je (presunul sa alebo zmazal)."), CoreText.T("Analyzujte priečinok znova.")))),
        (Rx("^the file changed since the analysis"), _ => (null, new LoggedProblem(CoreText.T("Súbor sa od analýzy zmenil (iná veľkosť, čas alebo stopy)."), CoreText.T("Analyzujte ho znova a spustite spracovanie.")))),
        (Rx("^the added file (?<name>.+) no longer exists"), m => (null, new LoggedProblem(CoreText.F("Pridaný súbor {0} (titulky alebo zvuk) už neexistuje.", m.Groups["name"].Value), CoreText.T("Pridajte ho znova alebo analyzujte priečinok znova.")))),
        (Rx("^done, but the library index was not updated: (?<why>.*)"), m => (null, new LoggedProblem(CoreText.F("Hotovo, ale register knižnice sa nepodarilo zapísať ({0}).", m.Groups["why"].Value), CoreText.T("Výsledok je v poriadku; pri ďalšom spracovaní toho istého originálu ho UNIVERSUM nemusí spoznať a zapíše novú verziu.")))),
        (Rx("^done, but: (?<why>.*)"), m => (null, new LoggedProblem(CoreText.F("Hotovo, ale: {0}.", m.Groups["why"].Value), CoreText.T("Výsledok je v knižnici; skontrolujte uvedenú vec.")))),
        (Rx("^not enough free space on (?<disk>.+?): (?<free>\\d+) MB free, (?<need>\\d+) MB needed"), m => (null, new LoggedProblem(CoreText.F("Na disku {0} je voľných {1} MB, treba {2} MB.", m.Groups["disk"].Value, m.Groups["free"].Value, m.Groups["need"].Value), CoreText.T("Uvoľnite miesto na disku alebo zvoľte knižnicu na inom disku a spustite spracovanie znova.")))),
        (Rx("^the copy of (?<name>.+) is incomplete"), _ => (null, new LoggedProblem(CoreText.T("Kópia súboru do knižnice je neúplná."), CoreText.T("Skontrolujte disk (miesto, chyby) a spustite spracovanie znova.")))),
        (Rx("^(?<tool>mkvmerge|mkvpropedit) failed \\((?<code>-?\\d+)\\): (?<detail>.*)"), m => (null, new LoggedProblem(
            CoreText.F("Nástroj {0} skončil chybou {1}: {2}", m.Groups["tool"].Value == "mkvmerge" ? CoreText.T("mkvmerge (zápis nového súboru)") : CoreText.T("mkvpropedit (úprava hlavičky)"), m.Groups["code"].Value, m.Groups["detail"].Value),
            CoreText.T("Súbor môže byť poškodený alebo neúplný – otvorte ho v MKVToolNix GUI alebo v Informáciách; skontrolujte aj voľné miesto na disku.")))),
        (Rx("^verification: (?<what>.*)"), m => (null, new LoggedProblem(CoreText.T("Kontrola zapísaného súboru našla rozdiel, výsledok sa nepoužil: ") + Verification(m.Groups["what"].Value) + ".",
            CoreText.T("Originál ostal. Skúste súbor spracovať znova; ak sa to opakuje, otvorte originál v Informáciách – môže byť poškodený alebo mať chybný údaj v hlavičke.")))),
        (Rx("^the library file cannot be replaced: (?<why>.*)"), m => (null, new LoggedProblem(CoreText.F("Súbor v knižnici sa nedá nahradiť: {0}.", m.Groups["why"].Value), CoreText.T("Zatvorte program, ktorý ho má otvorený (prehrávač, Jellyfin), a skúste znova.")))),
        (Rx("^the replaced file stays as (?<name>.+?): (?<why>.*)"), m => (null, new LoggedProblem(CoreText.F("Nahradený súbor ostal ako {0} ({1}).", m.Groups["name"].Value, m.Groups["why"].Value), CoreText.T("Po kontrole výsledku ho môžete zmazať sami.")))),
        (Rx("^a sound is to be written anew, but FFmpeg is not available"), _ => (null, new LoggedProblem(CoreText.T("Zvuk sa mal zapísať nanovo, ale chýba FFmpeg."), CoreText.T("Preinštalujte UNIVERSUM – FFmpeg je súčasťou inštalácie.")))),
        (Rx("^the picture of (?<name>.+?) cannot be written anew: (?<why>.*)"), m => (null, new LoggedProblem(CoreText.F("Obraz súboru {0} sa nepodarilo zapísať nanovo: {1}.", m.Groups["name"].Value, m.Groups["why"].Value), CoreText.T("Skúste iný kodek alebo kvalitu v Úprave videostopy; pri hardvérovom kodeku skúste softvérový.")))),
        (Rx("^the sound (?<n>\\d+) of (?<name>.+?) cannot be written anew: (?<why>.*)"), m => (null, new LoggedProblem(CoreText.F("Zvuk {0} súboru {1} sa nepodarilo zapísať nanovo: {2}.", m.Groups["n"].Value, m.Groups["name"].Value, m.Groups["why"].Value), CoreText.T("Skúste iný kodek v Úprave audiostopy alebo zvuk ponechajte bez prekódovania.")))),
        (Rx("^the subtitles of track (?<id>\\d+) cannot be read to cut them"), m => (null, new LoggedProblem(CoreText.F("Titulky stopy {0} sa nepodarilo prečítať na strih.", m.Groups["id"].Value), CoreText.T("Strihajte bez týchto titulkov, alebo ich najprv uložte ako SRT v Úprave titulkostopy.")))),
        (Rx("^the earlier result of this original stays \\((?<why>.*?)\\): (?<path>.*)"), m => (CoreText.F("Starší výsledok toho istého originálu ostal ({0}): {1} – po kontrole ho môžete zmazať sami.", m.Groups["why"].Value, Path.GetFileName(m.Groups["path"].Value)), null)),
        (Rx("^the earlier result of this original may still be there: (?<why>.*)"), m => (CoreText.F("Starší výsledok toho istého originálu možno ešte ostal v knižnici ({0}).", m.Groups["why"].Value), null)),
        (Rx("^the undo record cannot be read: (?<why>.*)"), m => (null, new LoggedProblem(CoreText.F("Záznam na vrátenie späť sa nedá prečítať ({0}).", m.Groups["why"].Value), CoreText.T("Vrátenie späť nie je možné; originál je v Koši, ak bol zmazaný.")))),
        (Rx("^Error: (?<what>.*)"), m => (null, new LoggedProblem(CoreText.F("Nástroj MKVToolNix hlási chybu: {0}", m.Groups["what"].Value), CoreText.T("Súbor môže byť poškodený – otvorte ho v MKVToolNix GUI.")))),
        (Rx("^#GUI#error (?<what>.*)"), m => (null, new LoggedProblem(CoreText.F("Nástroj MKVToolNix hlási chybu: {0}", m.Groups["what"].Value), CoreText.T("Súbor môže byť poškodený – otvorte ho v MKVToolNix GUI.")))),
        (Rx("^(Warning:|#GUI#warning)"), _ => null),
    ];

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline);

    // the verifier's findings ("track 2: language en, expected cs; duration …") in words
    private static string Verification(string text) => string.Join("; ", text.Split("; ").Select(part =>
    {
        var track = TrackPrefix().Match(part);
        var where = track.Success ? CoreText.F("stopa {0}: ", track.Groups["n"].Value) : "";
        var rest = track.Success ? part[track.Length..] : part;
        return rest switch
        {
            _ when rest.StartsWith("track count ", StringComparison.Ordinal) => CoreText.F("iný počet stôp ({0})", rest["track count ".Length..].Replace(", expected ", CoreText.T(" namiesto "), StringComparison.Ordinal)),
            _ when rest.StartsWith("duration ", StringComparison.Ordinal) => CoreText.F("iná dĺžka ({0})", rest["duration ".Length..].Replace(", expected ", CoreText.T(" namiesto "), StringComparison.Ordinal)),
            _ when rest.StartsWith("language ", StringComparison.Ordinal) => where + CoreText.F("iný jazyk ({0})", rest["language ".Length..].Replace(", expected ", CoreText.T(" namiesto "), StringComparison.Ordinal)),
            _ when rest.StartsWith("name ", StringComparison.Ordinal) => where + CoreText.F("iný názov ({0})", rest["name ".Length..].Replace(", expected ", CoreText.T(" namiesto "), StringComparison.Ordinal)),
            _ when rest.StartsWith("codec ", StringComparison.Ordinal) => where + CoreText.F("iný kodek ({0})", rest["codec ".Length..].Replace(", expected ", CoreText.T(" namiesto "), StringComparison.Ordinal)),
            _ when rest.StartsWith("type ", StringComparison.Ordinal) => where + CoreText.T("iný druh stopy"),
            _ when rest.StartsWith("chapters ", StringComparison.Ordinal) => CoreText.F("iný počet kapitol ({0})", rest["chapters ".Length..].Replace(", expected ", CoreText.T(" namiesto "), StringComparison.Ordinal)),
            _ when rest.StartsWith("attachments ", StringComparison.Ordinal) => CoreText.F("iný počet príloh ({0})", rest["attachments ".Length..].Replace(", expected ", CoreText.T(" namiesto "), StringComparison.Ordinal)),
            "video index still missing" => CoreText.T("index obrazu stále chýba"),
            "tags are still present" => CoreText.T("tagy ostali"),
            _ => where + rest,
        };
    }));

    [GeneratedRegex("^track (?<n>\\d+): ", RegexOptions.CultureInvariant)]
    private static partial Regex TrackPrefix();
}
