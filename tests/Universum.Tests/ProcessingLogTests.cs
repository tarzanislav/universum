using Universum.Core.Execution;
using Universum.Core.Jobs;
using Universum.Core.Media;
using Universum.Core.Planning;
using Universum.Core.Rules;

namespace Universum.Tests;

/// <summary>
/// Záznamy spracovania (owner, 31. 10. 2026: "z toho HTML som nič nevedel vyčítať"): what was done and what went wrong,
/// in words with what to do; kept until deleted. Invented names only.
/// </summary>
public sealed class ProcessingLogTests
{
    [Test]
    public void DoneFileIsDescribedInWords()
    {
        var plan = Plan(WriteMethod.Remux, new PlannedChange(1, ChangeKind.Language, "en", "cs"), new PlannedChange(3, ChangeKind.Removed, "4", null),
            new PlannedChange(null, ChangeKind.Index, "0", null));
        var result = new ProcessingResult("D:\\Vstup\\Vymyslený film.mkv", ProcessingStatus.Done, WriteMethod.Remux, "E:\\Knižnica\\MOVIES\\Vymyslený film (2026)\\x.mkv", null,
            [FileProcessor.OriginalRecycledMessage, FileProcessor.ReplacedPreviousPrefix + "starší.mkv", "Warning: a tool's warning"]);
        var file = ProcessingWords.Describe(result.SourcePath, plan, result);

        Assert.True(file.Outcome == LoggedOutcome.Done && file.Headline.StartsWith("Hotovo – nový súbor", StringComparison.Ordinal), file.Headline);
        Assert.True(file.Done.Contains("Stopa 1: jazyk en → cs.") && file.Done.Contains("Stopa 3: odstránená (bola 4.).") &&
                    file.Done.Any(d => d.StartsWith("Doplnený index", StringComparison.Ordinal)), "the changes: " + string.Join(" | ", file.Done));
        Assert.True(file.Done.Any(d => d.StartsWith("Zapísaný nový súbor", StringComparison.Ordinal)) && file.Done.Contains("Originál je v Koši (výsledok je zapísaný a skontrolovaný).") &&
                    file.Done.Any(d => d.Contains("starší.mkv", StringComparison.Ordinal)), "how it was written: " + string.Join(" | ", file.Done));
        Assert.True(file.Problems.Count == 0 && file.Technical.SequenceEqual(["Warning: a tool's warning"]), "a tool's warning only among the technical lines");
    }

    [Test]
    public void ProblemsSayWhatToDo()
    {
        var plan = Plan(WriteMethod.Remux);
        var result = new ProcessingResult("D:\\Vstup\\Vymyslený film.mkv", ProcessingStatus.Failed, WriteMethod.Remux, null, null,
        [
            "not enough free space on E:\\: 10 MB free, 2000 MB needed",
            "verification: track 2: language en, expected cs",
            "verification: duration 00:22:46, expected 00:22:00",
            "mkvmerge failed (2): Error: the file is damaged",
            "the file changed since the analysis; analyse it again",
            "something no one knows",
        ]);
        var file = ProcessingWords.Describe(result.SourcePath, plan, result);

        Assert.True(file.Outcome == LoggedOutcome.Failed && file.Headline == "Chyba – originál ostal nezmenený", file.Headline);
        Assert.Equal(5, file.Problems.Count, "five problems explained: " + string.Join(" | ", file.Problems.Select(p => p.What)));
        Assert.True(file.Problems[0].What.Contains("voľných 10 MB, treba 2000 MB", StringComparison.Ordinal) && file.Problems[0].Advice.Contains("Uvoľnite", StringComparison.Ordinal), file.Problems[0].What);
        Assert.True(file.Problems[1].What.Contains("stopa 2: iný jazyk (en namiesto cs)", StringComparison.Ordinal), file.Problems[1].What);
        Assert.True(file.Problems[2].What.Contains("iná dĺžka (00:22:46 namiesto 00:22:00)", StringComparison.Ordinal), file.Problems[2].What);
        Assert.True(file.Problems[3].What.Contains("mkvmerge (zápis nového súboru) skončil chybou 2", StringComparison.Ordinal), file.Problems[3].What);
        Assert.True(file.Problems[4].Advice.StartsWith("Analyzujte", StringComparison.Ordinal), file.Problems[4].Advice);
        Assert.True(file.Technical.Contains("something no one knows") && file.Technical.Count == 6, "every message kept among the technical lines");

        var attention = ProcessingWords.Describe("x.mkv", plan, result with { Messages = [FileProcessor.RestoreFailedPrefix + "the file could not be moved back"] });
        Assert.True(attention.Outcome == LoggedOutcome.Attention && attention.Headline.StartsWith("POZOR", StringComparison.Ordinal) &&
                    attention.Problems[0].Advice.StartsWith("Nič nemažte", StringComparison.Ordinal), "the original not put back: a warning with what to do");
        var skipped = ProcessingWords.Skipped("x.mkv", "súbor sa medzičasom zmenil – treba ho znova pripraviť v Hromadnom spracovaní");
        Assert.True(skipped.Outcome == LoggedOutcome.Skipped && skipped.Problems[0].What.StartsWith("Súbor sa medzičasom zmenil", StringComparison.Ordinal), "a MEGA file skipped");
    }

    [Test]
    public void LogsAreKeptUntilDeleted()
    {
        var folder = Directory.CreateTempSubdirectory("universum-zaznamy-").FullName;
        try
        {
            var store = new ProcessingLogStore(folder);
            var changes = 0;
            store.Changed += () => changes++;
            var done = new LoggedFile("a.mkv", "b.mkv", LoggedOutcome.Done, "Hotovo", [], [], []);
            var failed = new LoggedFile("c.mkv", null, LoggedOutcome.Failed, "Chyba", [], [new LoggedProblem("Čo sa stalo.", "Čo s tým.")], ["tool line"]);
            var first = store.Add(new ProcessingLog("Hromadné spracovanie", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-4), "1 z 2", [done, failed]));
            var second = store.Add(new ProcessingLog("Single spracovanie", DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow, "Hotovo", [done]));
            Assert.True(store.Add(new ProcessingLog("Zlučovanie", DateTime.UtcNow, DateTime.UtcNow, "", [])) is null, "a run without files is not kept");
            File.WriteAllText(Path.Combine(folder, "20000101-000000-000-poskodeny.json"), "{ nie je to záznam");

            var all = store.All();
            Assert.True(all.Count == 2 && all[0].Kind == "Single spracovanie" && all[1].Files[1].Problems[0].Advice == "Čo s tým.", "the newest first, read back whole");
            Assert.Equal("2 súbory: 1 hotovo, 1 chyba", all[1].Summary);
            Assert.True(all[1].HasProblems && !all[0].HasProblems, "a run with a problem");

            store.Delete([first!.Id]);
            Assert.True(store.All().Select(l => l.Id).SequenceEqual([second!.Id]), "one deleted");
            store.Delete(["..\\iny-subor"]);
            store.DeleteAll();
            Assert.True(store.All().Count == 0 && Directory.GetFiles(folder).Length == 0 && changes == 5, "all deleted, the damaged one too");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static EditPlan Plan(WriteMethod method, params PlannedChange[] changes)
    {
        var file = new MediaFile { Path = "x.mkv", Tracks = [new MediaTrack { Id = 0, Type = TrackType.Video }, new MediaTrack { Id = 1, Type = TrackType.Audio }] };
        return new EditPlan(UniversumRules.Apply(file, decisions: new TrackDecisions()), method, changes);
    }
}
