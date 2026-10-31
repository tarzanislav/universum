using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace Universum.Core.Calendar;

/// <summary>What kind of day a holiday is.</summary>
public enum HolidayKind
{
    /// <summary>A state holiday (štátny sviatok, státní svátek).</summary>
    State,

    /// <summary>Another holiday – a day of rest by law (ďalší deň pracovného pokoja, ostatní svátek).</summary>
    Other,

    /// <summary>A memorial day – a working day (pamätný deň, významný den).</summary>
    Memorial,

    /// <summary>A bank holiday of the United Kingdom.</summary>
    Bank,

    /// <summary>A federal holiday of the United States.</summary>
    Federal,
}

/// <summary>A holiday on a date in a region (as UNIVERSUM's list of holidays has it).</summary>
/// <param name="DayOff">A day of rest that year (8 May in Slovakia 2026: no).</param>
/// <param name="Substitute">The day off given instead of a holiday on a weekend (UK substitute day, US observed day).</param>
public sealed record Holiday(DateOnly Date, string Region, HolidayKind Kind, bool DayOff, string NameSk, string NameCs, string NameEn, string Source, bool Substitute = false)
{
    /// <summary>The name in the language ("sk", "cs", "en"); a substitute day says so.</summary>
    public string Name(string language)
    {
        var name = language switch { "cs" => NameCs, "en" => NameEn, _ => NameSk };
        return !Substitute ? name : language switch
        {
            "cs" => name + " (náhradní volno)",
            "en" => name + (Region == HolidayRegions.UnitedStates ? " (observed)" : " (substitute day)"),
            _ => name + " (náhradné voľno)",
        };
    }
}

/// <summary>The regions UNIVERSUM knows the holidays of.</summary>
public static class HolidayRegions
{
    public const string Slovakia = "SK";
    public const string Czechia = "CZ";
    public const string England = "GB-ENG";
    public const string Scotland = "GB-SCT";
    public const string NorthernIreland = "GB-NIR";
    public const string UnitedStates = "US";

    /// <summary>All of them in the order of the settings.</summary>
    public static IReadOnlyList<string> All { get; } = [Slovakia, Czechia, England, Scotland, NorthernIreland, UnitedStates];

    /// <summary>The region's name in the language.</summary>
    public static string Name(string region, string language) => (region, language) switch
    {
        (Slovakia, "cs") => "Slovensko",
        (Slovakia, "en") => "Slovakia",
        (Slovakia, _) => "Slovensko",
        (Czechia, "cs") => "Česko",
        (Czechia, "en") => "Czechia",
        (Czechia, _) => "Česko",
        (England, "cs") => "Anglie a Wales",
        (England, "en") => "England and Wales",
        (England, _) => "Anglicko a Wales",
        (Scotland, "cs") => "Skotsko",
        (Scotland, "en") => "Scotland",
        (Scotland, _) => "Škótsko",
        (NorthernIreland, "cs") => "Severní Irsko",
        (NorthernIreland, "en") => "Northern Ireland",
        (NorthernIreland, _) => "Severné Írsko",
        (UnitedStates, "cs") => "USA",
        (UnitedStates, "en") => "United States",
        (UnitedStates, _) => "USA",
        _ => region,
    };
}

/// <summary>
/// The holidays, days of rest and memorial days of Slovakia, Czechia, the United Kingdom and the United States, worked
/// out for any year from UNIVERSUM's own list of rules (holidays.txt, built into the program; checked against the laws on
/// 31. 10. 2026): fixed dates, days counted from Easter, the n-th weekday of a month; each rule valid from – to a year,
/// days of rest with exceptions in single years, and the substitute days of the UK and the US.
/// </summary>
public static class Holidays
{
    private static readonly Lazy<IReadOnlyList<Rule>> Rules = new(Load);
    private static readonly ConcurrentDictionary<(int, string), IReadOnlyList<Holiday>> Years = new();

    /// <summary>The holidays of a region in a year, by date.</summary>
    public static IReadOnlyList<Holiday> Of(int year, string region) => Years.GetOrAdd((year, region), key => Compute(key.Item1, key.Item2));

    /// <summary>The holidays of the regions on a date.</summary>
    public static IReadOnlyList<Holiday> On(DateOnly date, IEnumerable<string> regions) =>
        [.. regions.SelectMany(r => Of(date.Year, r)).Where(h => h.Date == date)];

    /// <summary>The date is a day of rest of the region (a holiday that is a day off; weekends are not counted).</summary>
    public static bool IsDayOff(DateOnly date, string region) => Of(date.Year, region).Any(h => h.Date == date && h.DayOff);

    private static List<Holiday> Compute(int year, string region)
    {
        var days = new List<Holiday>();
        var substitutes = new List<(Holiday Holiday, bool Observe)>();
        foreach (var rule in Rules.Value.Where(r => r.Region == region))
        {
            // a US day observed on Friday 31 December belongs to the year before: the next year's rule is asked too
            foreach (var ruleYear in rule.Observed ? new[] { year, year + 1 } : [year])
            {
                if (!rule.ValidIn(ruleYear) || rule.Date(ruleYear) is not { } date)
                {
                    continue;
                }

                var holiday = new Holiday(date, region, rule.Kind, rule.OffIn(ruleYear), rule.Sk, rule.Cs, rule.En, rule.Source);
                if (ruleYear == year)
                {
                    days.Add(holiday);
                }

                if (holiday.DayOff && (rule.Substituted || rule.Observed) && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    substitutes.Add((holiday, rule.Observed));
                }
            }
        }

        // UK: the next weekday not yet a day off (Christmas and Boxing Day on a weekend: Monday and Tuesday);
        // US: Saturday → the Friday before, Sunday → the Monday after
        var taken = days.Where(d => d.DayOff).Select(d => d.Date).ToHashSet();
        foreach (var (holiday, observe) in substitutes.OrderBy(s => s.Holiday.Date))
        {
            var date = observe
                ? holiday.Date.AddDays(holiday.Date.DayOfWeek == DayOfWeek.Saturday ? -1 : 1)
                : NextFreeWeekday(holiday.Date, taken);
            if (date.Year != year)
            {
                continue;
            }

            taken.Add(date);
            days.Add(holiday with { Date = date, Substitute = true });
        }

        return [.. days.OrderBy(d => d.Date).ThenBy(d => d.Kind)];
    }

    private static DateOnly NextFreeWeekday(DateOnly date, HashSet<DateOnly> taken)
    {
        var day = date.AddDays(1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || taken.Contains(day))
        {
            day = day.AddDays(1);
        }

        return day;
    }

    private static List<Rule> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Universum.Core.Calendar.holidays.txt")
                           ?? throw new InvalidOperationException("The list of holidays is missing.");
        using var reader = new StreamReader(stream);
        var rules = new List<Rule>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0 && line[0] != '#')
            {
                rules.Add(Rule.Parse(line.Normalize(System.Text.NormalizationForm.FormC)));
            }
        }

        return rules;
    }

    // one line of holidays.txt
    private sealed record Rule(string Region, HolidayKind Kind, Func<int, DateOnly?> Date, int? From, int? To, bool Off, IReadOnlySet<int> NotOffIn,
        bool Substituted, bool Observed, string Sk, string Cs, string En, string Source)
    {
        public bool ValidIn(int year) => (From is null || year >= From) && (To is null || year <= To);

        public bool OffIn(int year) => Off && !NotOffIn.Contains(year);

        public static Rule Parse(string line)
        {
            var f = line.Split('|');
            if (f.Length != 10)
            {
                throw new FormatException("A rule of holidays.txt has not 10 columns: " + line);
            }

            var kind = f[1] switch
            {
                "state" => HolidayKind.State,
                "other" => HolidayKind.Other,
                "memorial" => HolidayKind.Memorial,
                "bank" => HolidayKind.Bank,
                "federal" => HolidayKind.Federal,
                _ => throw new FormatException("Unknown kind of holiday: " + f[1]),
            };
            var off = f[5];
            var exceptions = off.Contains('!') ? off[(off.IndexOf('!') + 1)..].Split(',').Select(int.Parse).ToHashSet() : [];
            return new Rule(f[0], kind, DateRule(f[2]), Year(f[3]), Year(f[4]), off.StartsWith("off", StringComparison.Ordinal), exceptions,
                off.EndsWith("+sub", StringComparison.Ordinal), off.EndsWith("+obs", StringComparison.Ordinal), f[6], f[7], f[8], f[9]);
        }

        private static int? Year(string text) => text.Length == 0 ? null : int.Parse(text, CultureInfo.InvariantCulture);

        // "MM-DD", "easter±N", "nth:MM:N:D" (N = -1: the last; D = 1 Monday … 7 Sunday)
        private static Func<int, DateOnly?> DateRule(string text)
        {
            if (text.StartsWith("easter", StringComparison.Ordinal))
            {
                var offset = int.Parse(text[6..], CultureInfo.InvariantCulture);
                return year => Easter.Sunday(year).AddDays(offset);
            }

            if (text.StartsWith("nth:", StringComparison.Ordinal))
            {
                var p = text[4..].Split(':').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                return year => NthWeekday(year, p[0], p[1], (DayOfWeek)(p[2] % 7));
            }

            var month = int.Parse(text[..2], CultureInfo.InvariantCulture);
            var day = int.Parse(text[3..], CultureInfo.InvariantCulture);
            return year => day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
        }

        private static DateOnly NthWeekday(int year, int month, int n, DayOfWeek weekday)
        {
            if (n > 0)
            {
                var first = new DateOnly(year, month, 1);
                return first.AddDays((((int)weekday - (int)first.DayOfWeek + 7) % 7) + (7 * (n - 1)));
            }

            var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            return last.AddDays(-(((int)last.DayOfWeek - (int)weekday + 7) % 7) - (7 * (-n - 1)));
        }
    }
}
