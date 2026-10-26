using Universum.Core.Chapters;
using Universum.Core.Media;
using Universum.Core.Processes;

namespace Universum.Tests;

/// <summary>Chapter editor: times, formats, checks and real mkvextract/mkvpropedit with undo.</summary>
public sealed class ChapterTests
{
    private static readonly LanguageTag Czech = LanguageTag.FromCode("cs");

    [Test]
    public void TimesAreReadAndWritten()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(3_723_500), ChapterTime.Parse("1:02:03.5"));
        Assert.Equal(TimeSpan.FromSeconds(123), ChapterTime.Parse("02:03"));
        Assert.Equal(TimeSpan.FromMilliseconds(1250), ChapterTime.Parse("1,25"));
        Assert.Equal<TimeSpan?>(null, ChapterTime.Parse("1:75"), "minutes up to 59");
        Assert.Equal<TimeSpan?>(null, ChapterTime.Parse("abc"));
        Assert.Equal<TimeSpan?>(null, ChapterTime.Parse("-5"));
        Assert.Equal("01:02:03.500", ChapterTime.Format(TimeSpan.FromMilliseconds(3_723_500)));
    }

    [Test]
    public void XmlRoundTripAndComplexFiles()
    {
        var chapters = new[] { new Chapter(TimeSpan.FromSeconds(90), "Druhá", Czech), new Chapter(TimeSpan.Zero, "Úvod & začiatok", Czech) };
        var xml = ChapterFormats.WriteXml(chapters);
        Assert.True(xml.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", StringComparison.Ordinal), "declared as UTF-8: " + xml[..40]);
        var read = ChapterFormats.ParseXml(xml);
        Assert.True(!read.Complex, "simple");
        Assert.Sequence(["Úvod & začiatok", "Druhá"], read.Chapters.Select(c => c.Name), "sorted by start, special characters kept");
        Assert.Equal(TimeSpan.FromSeconds(90), read.Chapters[1].Start);
        Assert.Equal("cs", read.Chapters[0].Language.Primary);

        var nested = "<Chapters><EditionEntry><ChapterAtom><ChapterTimeStart>00:00:00.000000000</ChapterTimeStart><ChapterAtom><ChapterTimeStart>00:00:01.000000000</ChapterTimeStart></ChapterAtom></ChapterAtom></EditionEntry></Chapters>";
        Assert.True(ChapterFormats.ParseXml(nested).Complex, "nested chapters are complex");
        var editions = "<Chapters><EditionEntry/><EditionEntry/></Chapters>";
        Assert.True(ChapterFormats.ParseXml(editions).Complex, "two editions are complex");
    }

    [Test]
    public void OgmGenerateAndChecks()
    {
        var ogm = ChapterFormats.ParseOgm("﻿CHAPTER01=00:00:00.000\nCHAPTER01NAME=Úvod\nCHAPTER02=00:05:00.000\nCHAPTER02NAME=Druhá\n", Czech);
        Assert.Sequence([("Úvod", TimeSpan.Zero), ("Druhá", TimeSpan.FromMinutes(5))], ogm.Select(c => (c.Name, c.Start)), "OGM");

        var every = ChapterChecks.Every(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(25), Czech);
        Assert.Sequence(["Kapitola 01", "Kapitola 02", "Kapitola 03"], every.Select(c => c.Name), "every 10 minutes of 25");
        Assert.Equal(TimeSpan.FromMinutes(20), every[^1].Start);

        var problems = ChapterChecks.Problems([new Chapter(TimeSpan.Zero, "a", Czech), new Chapter(TimeSpan.Zero, "b", Czech), new Chapter(TimeSpan.FromHours(3), "c", Czech)], TimeSpan.FromHours(2));
        Assert.True(problems.Count == 2, string.Join("; ", problems));
    }

    [Test]
    public async Task ChaptersAreWrittenVerifiedAndUndone()
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var work = Directory.CreateTempSubdirectory("universum-chapters-");
        try
        {
            var file = Path.Combine(work.FullName, "film.mkv");
            File.Copy(TestData.Media("en-und-cz-forced.mkv"), file);
            var time = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(file, time);
            var editor = new ChapterEditor(installation, new ProcessRunner(), Path.Combine(work.FullName, "journal"));

            var before = await editor.ReadAsync(file);
            Assert.Sequence(["Chapter 01", "Koniec"], before.Chapters.Select(c => c.Name), "test file's chapters");

            var chapters = new List<Chapter>(before.Chapters) { new(TimeSpan.FromMilliseconds(1500), "Polovica", Czech) };
            chapters[0] = chapters[0] with { Name = "Začiatok", Language = Czech };
            var saved = await editor.SaveAsync(file, chapters);
            Assert.True(saved.Saved && saved.UndoRecordPath is not null, string.Join("; ", saved.Messages));
            var after = await editor.ReadAsync(file);
            Assert.Sequence(["Začiatok", "Koniec", "Polovica"], after.Chapters.Select(c => c.Name), "written, sorted by time (Koniec at 1 s, Polovica at 1.5 s)");
            Assert.Equal(time.AddSeconds(2), File.GetLastWriteTimeUtc(file), "time kept (+2 s)");

            Assert.Equal(0, (await editor.UndoAsync(saved.UndoRecordPath!)).Count, "undo");
            Assert.Sequence(["Chapter 01", "Koniec"], (await editor.ReadAsync(file)).Chapters.Select(c => c.Name), "old chapters back");
            Assert.Equal(time, File.GetLastWriteTimeUtc(file), "time restored");

            var removed = await editor.SaveAsync(file, []);
            Assert.True(removed.Saved && (await editor.ReadAsync(file)).Chapters.Count == 0, "all chapters removed");
            Assert.Equal(0, (await editor.UndoAsync(removed.UndoRecordPath!)).Count, "undo of the removal");
            Assert.Equal(2, (await editor.ReadAsync(file)).Chapters.Count, "back again");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }
}
