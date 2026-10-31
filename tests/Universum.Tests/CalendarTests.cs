using Universum.Core.Calendar;

namespace Universum.Tests;

/// <summary>Kalendár: Easter, the holidays of SK, CZ, UK and US with their exceptions and substitute days, the school holidays.</summary>
public sealed class CalendarTests
{
    [Test]
    public void EasterSundays()
    {
        Assert.Equal(new DateOnly(2024, 3, 31), Easter.Sunday(2024));
        Assert.Equal(new DateOnly(2025, 4, 20), Easter.Sunday(2025));
        Assert.Equal(new DateOnly(2026, 4, 5), Easter.Sunday(2026));
        Assert.Equal(new DateOnly(2027, 3, 28), Easter.Sunday(2027));
        Assert.Equal(new DateOnly(2038, 4, 25), Easter.Sunday(2038));
        Assert.Equal(new DateOnly(2285, 3, 22), Easter.Sunday(2285));
    }

    [Test]
    public void SlovakHolidaysWithTheYearsOfTheLaw()
    {
        var y2026 = Holidays.Of(2026, HolidayRegions.Slovakia);
        bool Off(int year, int month, int day) => Holidays.IsDayOff(new DateOnly(year, month, day), HolidayRegions.Slovakia);
        Assert.True(Off(2026, 1, 1) && Off(2026, 1, 6) && Off(2026, 4, 3) && Off(2026, 4, 6) && Off(2026, 8, 29) && Off(2026, 12, 26), "the days of rest of 2026");
        Assert.True(!Off(2026, 5, 8) && !Off(2026, 9, 15) && Off(2027, 5, 8) && Off(2027, 9, 15) && Off(2025, 5, 8), "§ 4b: 8 May and 15 September are working days only in 2026");
        Assert.True(!Off(2026, 9, 1) && Off(2023, 9, 1) && !Off(2025, 11, 17) && Off(2024, 11, 17) && !Off(2026, 10, 28), "1 September from 2024, 17 November from 2025 working days");
        Assert.True(y2026.Count(h => h.Kind == HolidayKind.Memorial) == 24 && y2026.All(h => h.Kind != HolidayKind.Memorial || !h.DayOff), "24 memorial days, working days");
        Assert.Equal("Veľký piatok", y2026.Single(h => h.Date == new DateOnly(2026, 4, 3)).Name("sk"));
        Assert.Equal("Our Lady of Sorrows", y2026.Single(h => h.Date == new DateOnly(2026, 9, 15)).Name("en"));
    }

    [Test]
    public void CzechHolidaysAndTheNewFlagDay()
    {
        var y2026 = Holidays.Of(2026, HolidayRegions.Czechia);
        Assert.True(y2026.Count(h => h.DayOff) == 14, "7 state and 7 other holidays (1 January twice) – " + y2026.Count(h => h.DayOff));
        Assert.True(Holidays.IsDayOff(new DateOnly(2026, 7, 6), HolidayRegions.Czechia) && Holidays.IsDayOff(new DateOnly(2026, 9, 28), HolidayRegions.Czechia), "Hus, statehood");
        Assert.True(y2026.Any(h => h.Date == new DateOnly(2026, 3, 30) && h.Name("cs") == "Den české vlajky") &&
                    !Holidays.Of(2025, HolidayRegions.Czechia).Any(h => h.Date == new DateOnly(2025, 3, 30)), "Den české vlajky from 2026 (59/2026 Sb.)");
        Assert.True(!Holidays.IsDayOff(new DateOnly(2015, 4, 3), HolidayRegions.Czechia) && Holidays.IsDayOff(new DateOnly(2016, 3, 25), HolidayRegions.Czechia), "Good Friday from 2016");
        Assert.Equal(20, y2026.Count(h => h.Kind == HolidayKind.Memorial));
    }

    [Test]
    public void UnitedKingdomBankHolidaysWithSubstituteDays()
    {
        var england2026 = Holidays.Of(2026, HolidayRegions.England).Where(h => h.DayOff && h.Date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Select(h => h.Date).ToList();
        Assert.True(england2026.SequenceEqual([new(2026, 1, 1), new(2026, 4, 3), new(2026, 4, 6), new(2026, 5, 4), new(2026, 5, 25), new(2026, 8, 31), new(2026, 12, 25), new(2026, 12, 28)]),
            "England 2026: Boxing Day on Saturday → Monday 28 December – " + string.Join(", ", england2026));
        var england2027 = Holidays.Of(2027, HolidayRegions.England).Where(h => h.Substitute).Select(h => h.Date).ToList();
        Assert.True(england2027.SequenceEqual([new(2027, 12, 27), new(2027, 12, 28)]), "Christmas on Saturday, Boxing Day on Sunday → Monday, Tuesday");
        var scotland2022 = Holidays.Of(2022, HolidayRegions.Scotland).Where(h => h.Substitute).Select(h => h.Date).ToList();
        Assert.True(scotland2022.Take(2).SequenceEqual([new(2022, 1, 3), new(2022, 1, 4)]), "Scotland: 1 and 2 January on a weekend → Monday and Tuesday");
        Assert.True(Holidays.IsDayOff(new DateOnly(2026, 11, 30), HolidayRegions.Scotland) && !Holidays.IsDayOff(new DateOnly(2026, 4, 6), HolidayRegions.Scotland), "St Andrew's Day, no Easter Monday");
        Assert.True(Holidays.IsDayOff(new DateOnly(2026, 3, 17), HolidayRegions.NorthernIreland) && Holidays.IsDayOff(new DateOnly(2027, 7, 12), HolidayRegions.NorthernIreland), "St Patrick, the Boyne");
    }

    [Test]
    public void UnitedStatesFederalHolidaysObserved()
    {
        var y2026 = Holidays.Of(2026, HolidayRegions.UnitedStates);
        Assert.True(y2026.Any(h => h.Date == new DateOnly(2026, 1, 19) && h.NameEn.StartsWith("Birthday of Martin", StringComparison.Ordinal)), "MLK: third Monday of January");
        Assert.True(y2026.Any(h => h.Date == new DateOnly(2026, 11, 26) && h.NameEn == "Thanksgiving Day"), "Thanksgiving: fourth Thursday");
        Assert.True(y2026.Any(h => h.Date == new DateOnly(2026, 7, 3) && h.Substitute) && y2026.Any(h => h.Date == new DateOnly(2026, 5, 25)), "4 July on Saturday → Friday 3 July; Memorial Day");
        Assert.True(Holidays.Of(2021, HolidayRegions.UnitedStates).Any(h => h.Date == new DateOnly(2021, 12, 31) && h.Substitute && h.NameEn == "New Year's Day"),
            "1 January 2022 on Saturday → observed Friday 31 December 2021");
        Assert.Equal("Independence Day (observed)", y2026.Single(h => h.Date == new DateOnly(2026, 7, 3)).Name("en"));
    }

    [Test]
    public void SchoolHolidaysByRegionAndDistrict()
    {
        Assert.True(SchoolHolidays.On(new DateOnly(2027, 2, 16), HolidayRegions.Slovakia, "BB") is [{ Kind: SchoolDayKind.Spring }], "Banskobystrický kraj: the first week");
        Assert.True(SchoolHolidays.On(new DateOnly(2027, 2, 16), HolidayRegions.Slovakia, "BA").Count == 0, "Bratislavský kraj: not yet");
        Assert.True(SchoolHolidays.On(new DateOnly(2027, 3, 3), HolidayRegions.Slovakia, "BA") is [{ Kind: SchoolDayKind.Spring }], "Bratislavský kraj: 1. – 5. 3.");
        Assert.True(SchoolHolidays.On(new DateOnly(2027, 1, 5), HolidayRegions.Slovakia, null) is [{ Kind: SchoolDayKind.Christmas }], "SK Christmas holidays until 7 January");
        Assert.True(SchoolHolidays.On(new DateOnly(2027, 1, 5), HolidayRegions.Czechia, "Zlín").Count == 0, "CZ Christmas holidays end on 3 January");
        Assert.True(SchoolHolidays.On(new DateOnly(2027, 3, 10), HolidayRegions.Czechia, "Zlín") is [{ Kind: SchoolDayKind.Spring }], "Zlín: 8. – 14. 3.");
        Assert.Equal(78, SchoolHolidays.CzechDistricts.Count);
        Assert.Equal("jarné prázdniny", SchoolHolidays.On(new DateOnly(2027, 3, 3), HolidayRegions.Slovakia, "TT")[0].Name("sk"));
        Assert.Equal("jarní prázdniny", SchoolHolidays.On(new DateOnly(2027, 3, 10), HolidayRegions.Czechia, "Zlín")[0].Name("cs"));
        Assert.True(SchoolHolidays.Between(new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31), HolidayRegions.Slovakia, "KE").Count == 9, "a whole school year in Košice");
    }
}
