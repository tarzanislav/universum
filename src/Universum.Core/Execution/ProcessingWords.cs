using System.Text.RegularExpressions;
using Universum.Core.Jobs;
using Universum.Core.Planning;

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
                    WriteMethod.Remux => "Zapísaný nový súbor (mkvmerge) a skontrolovaný: stopy, jazyky, príznaky, názvy a dĺžka sedia.",
                    WriteMethod.HeaderEdit => "Upravená len hlavička súboru (mkvpropedit) a skontrolovaná.",
                    _ => "Súbor sa nemusel meniť – len sa uložil do knižnice.",
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
            ProcessingStatus.Done when plan?.Method == WriteMethod.Remux => "Hotovo – nový súbor v knižnici",
            ProcessingStatus.Done when plan?.Method == WriteMethod.HeaderEdit => "Hotovo – upravená hlavička, súbor v knižnici",
            ProcessingStatus.Done => "Hotovo – súbor je v knižnici",
            ProcessingStatus.AlreadyInLibrary => "Preskočené – rovnaký výsledok z tohto originálu už v knižnici je",
            ProcessingStatus.Unchanged => "Bez zmeny – súbor už je v knižnici na svojom mieste",
            ProcessingStatus.NeedsReview => "Nespracované – súbor treba najprv skontrolovať v Hromadnom alebo Single spracovaní",
            ProcessingStatus.Cancelled => "Zrušené – originál ostal nezmenený",
            _ when outcome == LoggedOutcome.Attention => "POZOR – nepodarilo sa a originál sa nepodarilo úplne vrátiť",
            _ when result.Messages.Contains(FileProcessor.RestoredMessage) => "Chyba – originál je vrátený do pôvodného stavu",
            _ => "Chyba – originál ostal nezmenený",
        };

        if (outcome == LoggedOutcome.AlreadyInLibrary && result.OutputPath is { } found)
        {
            done.Add($"Výsledok z rovnakého originálu s rovnakými rozhodnutiami už je v knižnici: {Path.GetFileName(found)} – nič sa nezapísalo.");
        }

        if (outcome is LoggedOutcome.Failed or LoggedOutcome.Attention && problems.Count == 0)
        {
            problems.Add(new LoggedProblem("Spracovanie sa nepodarilo (podrobnosti sú v technických riadkoch).", "Skúste súbor spracovať znova; ak sa chyba opakuje, otvorte ho v Informáciách alebo v MKVToolNix GUI."));
        }

        return new LoggedFile(source, result.OutputPath, outcome, headline, done, problems, technical);
    }

    /// <summary>A file not processed: skipped before the run (a MEGA batch's check), with the reason in words.</summary>
    public static LoggedFile Skipped(string source, string reason, bool alreadyInLibrary = false) => alreadyInLibrary
        ? new LoggedFile(source, null, LoggedOutcome.AlreadyInLibrary, "Preskočené – už je v knižnici", [], [], [])
        : new LoggedFile(source, null, LoggedOutcome.Skipped, "Preskočené pri kontrole pred spustením", [],
            [new LoggedProblem(Capital(reason) + ".", "Otvorte súbor znova v Hromadnom spracovaní, skontrolujte ho a pošlite ho do dávky znova.")], []);

    // one planned change as a sentence
    private static string Change(PlannedChange change)
    {
        var track = change.TrackId is { } id ? $"Stopa {id}: " : "";
        return change.Kind switch
        {
            ChangeKind.Container => $"Kontajner {change.Before} sa zmení na MKV.",
            ChangeKind.Added => $"{track}pridaná zo súboru {change.After}.",
            ChangeKind.Position => $"{track}presunutá z {change.Before}. na {change.After}. miesto.",
            ChangeKind.Timing => $"{track}nové časy titulkov – {change.After}.",
            ChangeKind.Name => $"{track}názov „{change.Before}“ → „{change.After}“.",
            ChangeKind.Language => $"{track}jazyk {change.Before} → {change.After}.",
            ChangeKind.Default => $"{track}predvolená (Default) {YesNo(change.Before)} → {YesNo(change.After)}.",
            ChangeKind.Enabled => $"{track}zapnutá (Enabled) {YesNo(change.Before)} → {YesNo(change.After)}.",
            ChangeKind.Forced => $"{track}vynútená (Forced) {YesNo(change.Before)} → {YesNo(change.After)}.",
            ChangeKind.OtherFlags => $"{track}ďalšie príznaky {change.Before} → {change.After}.",
            ChangeKind.Removed => $"{track}odstránená (bola {change.Before}.).",
            ChangeKind.Index => "Doplnený index (Cues) – posúvanie v prehrávači bude rýchle.",
            ChangeKind.Tags => "Odstránené tagy (údaje, ktoré pridali iné programy).",
            ChangeKind.Title => $"Názov v hlavičke „{change.Before}“ → „{change.After}“.",
            ChangeKind.Chapters => $"Kapitoly: {change.Before} → {change.After}.",
            _ => $"{track}{change.Kind}: {change.Before} → {change.After}.",
        };
    }

    private static string YesNo(string? flag) => flag switch
    {
        "True" or "true" or "1" => "áno",
        "False" or "false" or "0" => "nie",
        _ => flag ?? "–",
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// A message of the processing in words: a step that was done (problem null), or a problem with what to do; null for a
    /// tool's line that says nothing more (kept among the technical lines). A message already in Slovak is a step – or a
    /// problem when the processing <paramref name="failed"/>.
    /// </summary>
    public static (string? Step, LoggedProblem? Problem)? Explain(string message, bool failed = false)
    {
        string Tail(string prefix) => message[prefix.Length..];
        if (message == FileProcessor.OriginalRecycledMessage)
        {
            return ("Originál je v Koši (výsledok je zapísaný a skontrolovaný).", null);
        }

        if (message == FileProcessor.RestoredMessage)
        {
            return ("Originál je vrátený do pôvodného stavu.", null);
        }

        if (message.StartsWith(FileProcessor.ReplacedPreviousPrefix, StringComparison.Ordinal))
        {
            return ($"Starší výsledok toho istého originálu (s inými rozhodnutiami) je v Koši: {Tail(FileProcessor.ReplacedPreviousPrefix)}.", null);
        }

        if (message.StartsWith(FileProcessor.OriginalKeptPrefix, StringComparison.Ordinal))
        {
            return ($"Originál ostal na mieste – {Kept(Tail(FileProcessor.OriginalKeptPrefix))}.", null);
        }

        if (message.Contains(FileProcessor.RestoreFailedPrefix, StringComparison.Ordinal))
        {
            return (null, new LoggedProblem(
                "Originál sa po chybe nepodarilo úplne vrátiť do pôvodného stavu: " + message[(message.IndexOf(FileProcessor.RestoreFailedPrefix, StringComparison.Ordinal) + FileProcessor.RestoreFailedPrefix.Length)..],
                "Nič nemažte. Vo Fronte úloh je pri úlohe Vrátiť späť; ak nepomôže, pozrite priečinok originálu – súbor s príponou .universum-old alebo .universum-moving je pôvodný."));
        }

        foreach (var (pattern, explain) in Known)
        {
            if (pattern.Match(message) is { Success: true } match)
            {
                return explain(match);
            }
        }

        if (message.Any(c => "áäčďéíĺľňóôŕšťúýž".Contains(char.ToLowerInvariant(c), StringComparison.Ordinal)))
        {
            var sentence = Capital(message.TrimEnd('.')) + ".";
            return failed ? (null, new LoggedProblem(sentence, "Skúste súbor spracovať znova; ak sa to opakuje, otvorte ho v Informáciách.")) : (sentence, null);
        }

        // the tools' own lines (warnings of mkvmerge) say nothing the user has to do
        return null;
    }

    private static string Kept(string reason) => reason switch
    {
        "it is a result of a library" => "je to výsledok knižnice, nie originál",
        _ when reason.StartsWith("it cannot go to the Recycle Bin", StringComparison.Ordinal) => "nedá sa presunúť do Koša" + reason["it cannot go to the Recycle Bin".Length..],
        _ => reason,
    };

    private static readonly (Regex Pattern, Func<Match, (string?, LoggedProblem?)?> Explain)[] Known =
    [
        (Rx("^the file no longer exists"), _ => (null, new LoggedProblem("Súbor už na svojom mieste nie je (presunul sa alebo zmazal).", "Analyzujte priečinok znova."))),
        (Rx("^the file changed since the analysis"), _ => (null, new LoggedProblem("Súbor sa od analýzy zmenil (iná veľkosť, čas alebo stopy).", "Analyzujte ho znova a spustite spracovanie."))),
        (Rx("^the added file (?<name>.+) no longer exists"), m => (null, new LoggedProblem($"Pridaný súbor {m.Groups["name"].Value} (titulky alebo zvuk) už neexistuje.", "Pridajte ho znova alebo analyzujte priečinok znova."))),
        (Rx("^done, but the library index was not updated: (?<why>.*)"), m => (null, new LoggedProblem($"Hotovo, ale register knižnice sa nepodarilo zapísať ({m.Groups["why"].Value}).", "Výsledok je v poriadku; pri ďalšom spracovaní toho istého originálu ho UNIVERSUM nemusí spoznať a zapíše novú verziu."))),
        (Rx("^done, but: (?<why>.*)"), m => (null, new LoggedProblem($"Hotovo, ale: {m.Groups["why"].Value}.", "Výsledok je v knižnici; skontrolujte uvedenú vec."))),
        (Rx("^not enough free space on (?<disk>.+?): (?<free>\\d+) MB free, (?<need>\\d+) MB needed"), m => (null, new LoggedProblem($"Na disku {m.Groups["disk"].Value} je voľných {m.Groups["free"].Value} MB, treba {m.Groups["need"].Value} MB.", "Uvoľnite miesto na disku alebo zvoľte knižnicu na inom disku a spustite spracovanie znova."))),
        (Rx("^the copy of (?<name>.+) is incomplete"), _ => (null, new LoggedProblem("Kópia súboru do knižnice je neúplná.", "Skontrolujte disk (miesto, chyby) a spustite spracovanie znova."))),
        (Rx("^(?<tool>mkvmerge|mkvpropedit) failed \\((?<code>-?\\d+)\\): (?<detail>.*)"), m => (null, new LoggedProblem(
            $"Nástroj {(m.Groups["tool"].Value == "mkvmerge" ? "mkvmerge (zápis nového súboru)" : "mkvpropedit (úprava hlavičky)")} skončil chybou {m.Groups["code"].Value}: {m.Groups["detail"].Value}",
            "Súbor môže byť poškodený alebo neúplný – otvorte ho v MKVToolNix GUI alebo v Informáciách; skontrolujte aj voľné miesto na disku."))),
        (Rx("^verification: (?<what>.*)"), m => (null, new LoggedProblem("Kontrola zapísaného súboru našla rozdiel, výsledok sa nepoužil: " + Verification(m.Groups["what"].Value) + ".",
            "Originál ostal. Skúste súbor spracovať znova; ak sa to opakuje, otvorte originál v Informáciách – môže byť poškodený alebo mať chybný údaj v hlavičke."))),
        (Rx("^the library file cannot be replaced: (?<why>.*)"), m => (null, new LoggedProblem($"Súbor v knižnici sa nedá nahradiť: {m.Groups["why"].Value}.", "Zatvorte program, ktorý ho má otvorený (prehrávač, Jellyfin), a skúste znova."))),
        (Rx("^the replaced file stays as (?<name>.+?): (?<why>.*)"), m => (null, new LoggedProblem($"Nahradený súbor ostal ako {m.Groups["name"].Value} ({m.Groups["why"].Value}).", "Po kontrole výsledku ho môžete zmazať sami."))),
        (Rx("^a sound is to be written anew, but FFmpeg is not available"), _ => (null, new LoggedProblem("Zvuk sa mal zapísať nanovo, ale chýba FFmpeg.", "Preinštalujte UNIVERSUM – FFmpeg je súčasťou inštalácie."))),
        (Rx("^the picture of (?<name>.+?) cannot be written anew: (?<why>.*)"), m => (null, new LoggedProblem($"Obraz súboru {m.Groups["name"].Value} sa nepodarilo zapísať nanovo: {m.Groups["why"].Value}.", "Skúste iný kodek alebo kvalitu v Úprave videostopy; pri hardvérovom kodeku skúste softvérový."))),
        (Rx("^the sound (?<n>\\d+) of (?<name>.+?) cannot be written anew: (?<why>.*)"), m => (null, new LoggedProblem($"Zvuk {m.Groups["n"].Value} súboru {m.Groups["name"].Value} sa nepodarilo zapísať nanovo: {m.Groups["why"].Value}.", "Skúste iný kodek v Úprave audiostopy alebo zvuk ponechajte bez prekódovania."))),
        (Rx("^the subtitles of track (?<id>\\d+) cannot be read to cut them"), m => (null, new LoggedProblem($"Titulky stopy {m.Groups["id"].Value} sa nepodarilo prečítať na strih.", "Strihajte bez týchto titulkov, alebo ich najprv uložte ako SRT v Úprave titulkostopy."))),
        (Rx("^the earlier result of this original stays \\((?<why>.*?)\\): (?<path>.*)"), m => ($"Starší výsledok toho istého originálu ostal ({m.Groups["why"].Value}): {Path.GetFileName(m.Groups["path"].Value)} – po kontrole ho môžete zmazať sami.", null)),
        (Rx("^the earlier result of this original may still be there: (?<why>.*)"), m => ($"Starší výsledok toho istého originálu možno ešte ostal v knižnici ({m.Groups["why"].Value}).", null)),
        (Rx("^the undo record cannot be read: (?<why>.*)"), m => (null, new LoggedProblem($"Záznam na vrátenie späť sa nedá prečítať ({m.Groups["why"].Value}).", "Vrátenie späť nie je možné; originál je v Koši, ak bol zmazaný."))),
        (Rx("^Error: (?<what>.*)"), m => (null, new LoggedProblem($"Nástroj MKVToolNix hlási chybu: {m.Groups["what"].Value}", "Súbor môže byť poškodený – otvorte ho v MKVToolNix GUI."))),
        (Rx("^#GUI#error (?<what>.*)"), m => (null, new LoggedProblem($"Nástroj MKVToolNix hlási chybu: {m.Groups["what"].Value}", "Súbor môže byť poškodený – otvorte ho v MKVToolNix GUI."))),
        (Rx("^(Warning:|#GUI#warning)"), _ => null),
    ];

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline);

    // the verifier's findings ("track 2: language en, expected cs; duration …") in words
    private static string Verification(string text) => string.Join("; ", text.Split("; ").Select(part =>
    {
        var track = TrackPrefix().Match(part);
        var where = track.Success ? $"stopa {track.Groups["n"].Value}: " : "";
        var rest = track.Success ? part[track.Length..] : part;
        return rest switch
        {
            _ when rest.StartsWith("track count ", StringComparison.Ordinal) => "iný počet stôp (" + rest["track count ".Length..].Replace(", expected ", " namiesto ", StringComparison.Ordinal) + ")",
            _ when rest.StartsWith("duration ", StringComparison.Ordinal) => "iná dĺžka (" + rest["duration ".Length..].Replace(", expected ", " namiesto ", StringComparison.Ordinal) + ")",
            _ when rest.StartsWith("language ", StringComparison.Ordinal) => where + "iný jazyk (" + rest["language ".Length..].Replace(", expected ", " namiesto ", StringComparison.Ordinal) + ")",
            _ when rest.StartsWith("name ", StringComparison.Ordinal) => where + "iný názov (" + rest["name ".Length..].Replace(", expected ", " namiesto ", StringComparison.Ordinal) + ")",
            _ when rest.StartsWith("codec ", StringComparison.Ordinal) => where + "iný kodek (" + rest["codec ".Length..].Replace(", expected ", " namiesto ", StringComparison.Ordinal) + ")",
            _ when rest.StartsWith("type ", StringComparison.Ordinal) => where + "iný druh stopy",
            _ when rest.StartsWith("chapters ", StringComparison.Ordinal) => "iný počet kapitol (" + rest["chapters ".Length..].Replace(", expected ", " namiesto ", StringComparison.Ordinal) + ")",
            _ when rest.StartsWith("attachments ", StringComparison.Ordinal) => "iný počet príloh (" + rest["attachments ".Length..].Replace(", expected ", " namiesto ", StringComparison.Ordinal) + ")",
            "video index still missing" => "index obrazu stále chýba",
            "tags are still present" => "tagy ostali",
            _ => where + rest,
        };
    }));

    [GeneratedRegex("^track (?<n>\\d+): ", RegexOptions.CultureInvariant)]
    private static partial Regex TrackPrefix();
}
