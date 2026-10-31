using Universum.Core.Agenda;

namespace Universum.Tests;

/// <summary>Rýchle pridanie úlohy: the day, the hour, tags and priority understood in Slovak, Czech and English.</summary>
public sealed class QuickEntryTests
{
    private static readonly DateOnly Saturday = new(2026, 10, 31);

    [Test]
    public void DaysAndHoursInWords()
    {
        var task = QuickEntry.Parse("zajtra o 20:00 spracovať vymyslený film #kino !", Saturday);
        Assert.True(task is { Title: "spracovať vymyslený film", Due.Day: 1, Time.Hour: 20, Priority: 2 } && task.Tags.SequenceEqual(["kino"]), "zajtra o 20:00 – " + task);
        Assert.Equal(new DateOnly(2026, 11, 6), QuickEntry.Parse("do piatku zavolať", Saturday).Due);
        Assert.Equal("zavolať", QuickEntry.Parse("do piatku zavolať", Saturday).Title);
        Assert.True(QuickEntry.Parse("v sobotu kino", Saturday) is { Due.Day: 7, Title: "kino" }, "v sobotu: the next Saturday");
        Assert.Equal(new DateOnly(2026, 11, 4), QuickEntry.Parse("v stredu porada", Saturday).Due);
        Assert.Equal(new DateOnly(2026, 11, 1), QuickEntry.Parse("v neděli výlet", Saturday).Due);
        Assert.Equal(new DateOnly(2026, 11, 3), QuickEntry.Parse("za 3 dni vrátiť disk", Saturday).Due);
        Assert.Equal("vrátiť disk", QuickEntry.Parse("za 3 dni vrátiť disk", Saturday).Title);
        Assert.Equal(new DateOnly(2026, 11, 7), QuickEntry.Parse("upratať archív za týždeň", Saturday).Due);
        Assert.Equal(new DateOnly(2026, 11, 15), QuickEntry.Parse("15. 11. premiéra", Saturday).Due);
        Assert.Equal(new DateOnly(2027, 1, 2), QuickEntry.Parse("2.1. nový rok", Saturday).Due);
        Assert.Equal(new DateOnly(2027, 3, 5), QuickEntry.Parse("2027-03-05 jarné prázdniny", Saturday).Due);
    }

    [Test]
    public void CzechEnglishAndPlainLines()
    {
        var czech = QuickEntry.Parse("zítra v 8:30 zavolat do kina", Saturday);
        Assert.True(czech is { Title: "zavolat do kina", Due.Day: 1, Time: { Hour: 8, Minute: 30 } }, "zítra v 8:30 – " + czech);
        Assert.Equal(new DateOnly(2026, 11, 2), QuickEntry.Parse("pondělí porada", Saturday).Due);
        var english = QuickEntry.Parse("tomorrow at 9:15 call the cinema !low", Saturday);
        Assert.True(english is { Title: "call the cinema", Due.Day: 1, Time: { Hour: 9, Minute: 15 }, Priority: 0 }, "tomorrow at 9:15 – " + english);
        Assert.Equal(new DateOnly(2026, 11, 7), QuickEntry.Parse("next week sort the posters", Saturday).Due);
        var plain = QuickEntry.Parse("skontrolovať titulky", Saturday);
        Assert.True(plain is { Title: "skontrolovať titulky", Due: null, Time: null, Priority: 1 } && plain.Tags.Count == 0, "no day: no due day");
        Assert.True(QuickEntry.Parse("o 21:00 seriál", Saturday) is { Due.Day: 31, Time.Hour: 21, Title: "seriál" }, "an hour alone: today");
    }
}
