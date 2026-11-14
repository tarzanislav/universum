using Universum.Core.IO;

namespace Universum.Tests;

/// <summary>The log of errors of work in the background (14. 11. 2026): written, kept small, no cancellation in it.</summary>
public sealed class ErrorLogTests
{
    [Test]
    public void ErrorsAreWrittenAndTheLogStaysSmall()
    {
        var folder = Directory.CreateTempSubdirectory("universum-chyby-").FullName;
        try
        {
            var log = new ErrorLog(Path.Combine(folder, "podpriecinok", "chyby.log"), limit: 4000);
            var at = new DateTimeOffset(2026, 11, 14, 20, 0, 0, TimeSpan.FromHours(1));
            Assert.Equal(1, log.Write("Vymyslená práca", new InvalidOperationException("Vymyslená chyba"), at).Count, "one error");
            var text = File.ReadAllText(log.File);
            Assert.True(text.StartsWith("2026-11-14 20:00:00 +01:00 · Vymyslená práca\n", StringComparison.Ordinal) && text.Contains("Vymyslená chyba", StringComparison.Ordinal),
                "when, where and what – " + text[..Math.Min(80, text.Length)]);

            Assert.Equal(0, log.Write("zrušené", new OperationCanceledException()).Count, "a cancellation is no error");
            Assert.Equal(0, log.Write("zrušené", new AggregateException(new TaskCanceledException())).Count, "nor inside an aggregate");
            Assert.Equal(2, log.Write("dve", new AggregateException(new IOException("Prvá"), new AggregateException(new FormatException("Druhá")), new OperationCanceledException())).Count,
                "the errors of an aggregate each, without the cancellation");

            for (var i = 0; i < 40; i++)
            {
                log.Write("opakovaná práca", new InvalidOperationException("Chyba číslo " + i));
            }

            var kept = File.ReadAllText(log.File);
            Assert.True(new FileInfo(log.File).Length <= 4000 && kept.Contains("Chyba číslo 39", StringComparison.Ordinal) && !kept.Contains("Vymyslená chyba", StringComparison.Ordinal) &&
                        System.Text.RegularExpressions.Regex.IsMatch(kept, @"^\d{4}-\d{2}-\d{2} "), "small: the newest kept, from the start of a block");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
