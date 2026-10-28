namespace Universum.Core.News;

/// <summary>How often the episodes of a season come out, from their air dates – in Slovak.</summary>
public static class Periodicity
{
    private static readonly string[] Days = ["ne", "po", "ut", "st", "št", "pi", "so"];
    private static readonly string[] DaysLong = ["v nedeľu", "v pondelok", "v utorok", "v stredu", "vo štvrtok", "v piatok", "v sobotu"];

    /// <summary>
    /// "všetky naraz", "denne (po–pi)", "denne", "týždenne v pondelok", "2× týždenne (po, ut)", "každé 2 týždne",
    /// "mesačne", "nepravidelne"; empty when there are fewer than two known dates.
    /// </summary>
    public static string Describe(IEnumerable<DateOnly> airDates)
    {
        var dates = airDates.Where(d => d != DateOnly.MinValue).Distinct().Order().ToList();
        if (dates.Count == 0)
        {
            return "";
        }

        if (dates.Count == 1)
        {
            return airDates.Count(d => d != DateOnly.MinValue) > 1 ? "všetky naraz" : "";
        }

        var gaps = dates.Zip(dates.Skip(1), (a, b) => b.DayNumber - a.DayNumber).ToList();
        var weekdays = dates.Select(d => (int)d.DayOfWeek).Distinct().Order().ToList();
        var typical = gaps.Order().ElementAt(gaps.Count / 2);
        var regular = gaps.Count(g => g == typical) * 3 >= gaps.Count * 2;
        if (typical <= 3 && weekdays.Count >= 4)
        {
            return weekdays.All(d => d is >= 1 and <= 5) ? "denne (po–pi)" : "denne";
        }

        if (typical is >= 6 and <= 8 && weekdays.Count == 1 && regular)
        {
            return "týždenne " + DaysLong[weekdays[0]];
        }

        if (typical <= 4 && weekdays.Count is 2 or 3 && dates.Count >= 4)
        {
            return $"{weekdays.Count}× týždenne ({string.Join(", ", weekdays.OrderBy(d => (d + 6) % 7).Select(d => Days[d]))})";
        }

        return typical switch
        {
            >= 6 and <= 8 when regular => "týždenne",
            >= 13 and <= 15 when regular => "každé 2 týždne",
            >= 27 and <= 32 when regular => "mesačne",
            _ => "nepravidelne",
        };
    }

    /// <summary>"pondelok", "utorok" … of a day.</summary>
    public static string DayName(DateOnly day) => day.DayOfWeek switch
    {
        DayOfWeek.Monday => "pondelok",
        DayOfWeek.Tuesday => "utorok",
        DayOfWeek.Wednesday => "streda",
        DayOfWeek.Thursday => "štvrtok",
        DayOfWeek.Friday => "piatok",
        DayOfWeek.Saturday => "sobota",
        _ => "nedeľa",
    };
}
