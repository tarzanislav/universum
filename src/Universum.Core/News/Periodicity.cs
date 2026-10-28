namespace Universum.Core.News;

/// <summary>How often the episodes of a season come out, from their air dates – in Slovak.</summary>
public static class Periodicity
{
    /// <summary>
    /// How the episodes come (owner, 29. 10. 2026): "každý deň", "každý pracovný deň (po–pi)", "raz do týždňa – pondelok",
    /// "2 epizódy týždenne – streda" (two at once on one day), "dvakrát do týždňa – pondelok, štvrtok", "trikrát do
    /// týždňa – …", "všetky naraz", "každé 2 týždne", "mesačne", "nepravidelne"; empty when fewer than two episodes
    /// are known. Every episode's day is given (two episodes on one day are two dates).
    /// </summary>
    public static string Describe(IEnumerable<DateOnly> airDates)
    {
        var all = airDates.Where(d => d != DateOnly.MinValue).ToList();
        var dates = all.Distinct().Order().ToList();
        if (dates.Count == 0)
        {
            return "";
        }

        if (dates.Count == 1)
        {
            return all.Count > 1 ? "všetky naraz" : "";
        }

        var gaps = dates.Zip(dates.Skip(1), (a, b) => b.DayNumber - a.DayNumber).ToList();
        var weekdays = dates.Select(d => (int)d.DayOfWeek).Distinct().OrderBy(d => (d + 6) % 7).ToList();
        var typical = gaps.Order().ElementAt(gaps.Count / 2);
        var regular = gaps.Count(g => g == typical) * 3 >= gaps.Count * 2;
        if (typical <= 3 && weekdays.Count >= 4)
        {
            return weekdays.All(d => d is >= 1 and <= 5) ? "každý pracovný deň (po–pi)" : "každý deň";
        }

        if (typical is >= 6 and <= 8 && weekdays.Count == 1 && regular)
        {
            // two episodes on most of the days
            var doubles = dates.Count(d => all.Count(a => a == d) >= 2);
            return (doubles * 2 > dates.Count ? "2 epizódy týždenne – " : "raz do týždňa – ") + DayName(dates[0]);
        }

        if (weekdays.Count is 2 or 3 && dates.Count >= 4 && gaps.All(g => g <= 6))
        {
            return (weekdays.Count == 2 ? "dvakrát do týždňa – " : "trikrát do týždňa – ") +
                   string.Join(", ", weekdays.Select(d => DayName(DateOnly.FromDayNumber(DateOnly.MinValue.DayNumber + (d + 6) % 7))));
        }

        return typical switch
        {
            >= 6 and <= 8 when regular => "raz do týždňa",
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
