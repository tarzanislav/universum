namespace Universum.Core.Calendar;

/// <summary>
/// The Easter Sunday of the Gregorian calendar (the Western churches – the movable holidays of Slovakia, Czechia, the
/// United Kingdom): the first Sunday after the ecclesiastical full moon on or after 21 March, worked out by whole-number
/// arithmetic from the year alone.
/// </summary>
public static class Easter
{
    /// <summary>The Easter Sunday of the year (1583 – 9999).</summary>
    public static DateOnly Sunday(int year)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1583);
        var cycle = year % 19;                                // the year's place in the 19-year cycle of the moon
        var century = year / 100;
        var inCentury = year % 100;
        var skippedLeaps = century / 4;                       // century years that stay leap years
        var centuryRest = century % 4;
        var moonDrift = (century - ((century + 8) / 25) + 1) / 3;
        var moonAge = ((19 * cycle) + century - skippedLeaps - moonDrift + 15) % 30;   // days from the full moon to 21 March
        var leaps = inCentury / 4;
        var yearRest = inCentury % 4;
        var toSunday = (32 + (2 * centuryRest) + (2 * leaps) - moonAge - yearRest) % 7;
        var late = (cycle + (11 * moonAge) + (22 * toSunday)) / 451;                     // the two exceptional dates
        var days = moonAge + toSunday - (7 * late) + 114;
        return new DateOnly(year, days / 31, (days % 31) + 1);
    }
}
