using System.Globalization;
using System.Reflection;
using System.Text;

namespace Universum.Core.Calendar;

/// <summary>A day or period of the school year.</summary>
public enum SchoolDayKind
{
    Start,
    HalfYearEnd,
    End,
    Autumn,
    Christmas,
    HalfYear,
    Spring,
    Easter,
    Summer,
}

/// <summary>A period of a school year in a country, for all its areas or only some (the spring holidays by region or district).</summary>
public sealed record SchoolPeriod(string Country, string SchoolYear, SchoolDayKind Kind, DateOnly From, DateOnly To, IReadOnlyList<string> Areas)
{
    /// <summary>Holidays (not a single day of the school year such as its start).</summary>
    public bool IsHoliday => Kind >= SchoolDayKind.Autumn;

    /// <summary>The period applies to the area (null: any area – the user has not chosen one).</summary>
    public bool For(string? area) => Areas.Count == 0 || area is null || Areas.Contains(area, StringComparer.Ordinal);

    /// <summary>"jarné prázdniny", "první den školního roku", "Easter holidays" …</summary>
    public string Name(string language) => SchoolHolidays.Name(Kind, Country, language);
}

/// <summary>
/// The school holidays and the main days of the school year in Slovakia (by region – kraj) and Czechia (by district –
/// okres), from UNIVERSUM's own list made after the ministries' timetables (school-holidays.txt; 2026/2027 checked on
/// 31. 10. 2026). The next school years come with an update of UNIVERSUM when the ministries publish them.
/// </summary>
public static class SchoolHolidays
{
    private static readonly Lazy<IReadOnlyList<SchoolPeriod>> Periods = new(Load);

    /// <summary>The Slovak regions: code and name.</summary>
    public static IReadOnlyList<(string Code, string Name)> SlovakRegions { get; } =
    [
        ("BA", "Bratislavský kraj"), ("TT", "Trnavský kraj"), ("TN", "Trenčiansky kraj"), ("NR", "Nitriansky kraj"),
        ("ZA", "Žilinský kraj"), ("BB", "Banskobystrický kraj"), ("PO", "Prešovský kraj"), ("KE", "Košický kraj"),
    ];

    /// <summary>All periods known.</summary>
    public static IReadOnlyList<SchoolPeriod> All => Periods.Value;

    /// <summary>The Czech districts as the timetable names them (alphabetically).</summary>
    public static IReadOnlyList<string> CzechDistricts =>
        [.. Periods.Value.Where(p => p.Country == HolidayRegions.Czechia).SelectMany(p => p.Areas).Distinct(StringComparer.Ordinal).Order(StringComparer.Create(CultureInfo.GetCultureInfo("cs-CZ"), false))];

    /// <summary>The periods of the country that cover the date in the area (null: any).</summary>
    public static IReadOnlyList<SchoolPeriod> On(DateOnly date, string country, string? area) =>
        [.. Periods.Value.Where(p => p.Country == country && p.From <= date && date <= p.To && p.For(area))];

    /// <summary>The periods of the country that overlap the days in the area.</summary>
    public static IReadOnlyList<SchoolPeriod> Between(DateOnly from, DateOnly to, string country, string? area) =>
        [.. Periods.Value.Where(p => p.Country == country && p.From <= to && from <= p.To && p.For(area))];

    /// <summary>The name of a kind of period in a language ("sk", "cs", "en").</summary>
    public static string Name(SchoolDayKind kind, string country, string language) => (kind, language is "cs" or "en" ? language : "sk") switch
    {
        (SchoolDayKind.Start, "cs") => "první den školního roku",
        (SchoolDayKind.Start, "en") => "first day of the school year",
        (SchoolDayKind.Start, _) => "prvý deň školského roka",
        (SchoolDayKind.HalfYearEnd, "cs") => "konec 1. pololetí (vysvědčení)",
        (SchoolDayKind.HalfYearEnd, "en") => "end of the first term (reports)",
        (SchoolDayKind.HalfYearEnd, _) => "koniec 1. polroka (vysvedčenie)",
        (SchoolDayKind.End, "cs") => "poslední den vyučování",
        (SchoolDayKind.End, "en") => "last day of teaching",
        (SchoolDayKind.End, _) => "posledný deň vyučovania",
        (SchoolDayKind.Autumn, "cs") => "podzimní prázdniny",
        (SchoolDayKind.Autumn, "en") => "autumn holidays",
        (SchoolDayKind.Autumn, _) => "jesenné prázdniny",
        (SchoolDayKind.Christmas, "cs") => "vánoční prázdniny",
        (SchoolDayKind.Christmas, "en") => "Christmas holidays",
        (SchoolDayKind.Christmas, _) => "vianočné prázdniny",
        (SchoolDayKind.HalfYear, "cs") => "pololetní prázdniny",
        (SchoolDayKind.HalfYear, "en") => "half-year holiday",
        (SchoolDayKind.HalfYear, _) => "polročné prázdniny",
        (SchoolDayKind.Spring, "cs") => "jarní prázdniny",
        (SchoolDayKind.Spring, "en") => "spring holidays",
        (SchoolDayKind.Spring, _) => "jarné prázdniny",
        (SchoolDayKind.Easter, "cs") => "velikonoční prázdniny",
        (SchoolDayKind.Easter, "en") => "Easter holidays",
        (SchoolDayKind.Easter, _) => "veľkonočné prázdniny",
        (SchoolDayKind.Summer, "cs") => "hlavní prázdniny",
        (SchoolDayKind.Summer, "en") => "summer holidays",
        _ => "letné prázdniny",
    };

    private static List<SchoolPeriod> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Universum.Core.Calendar.school-holidays.txt")
                           ?? throw new InvalidOperationException("The school holidays are missing.");
        using var reader = new StreamReader(stream);
        var periods = new List<SchoolPeriod>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var f = line.Normalize(NormalizationForm.FormC).Split('|');
            var kind = f[2] switch
            {
                "start" => SchoolDayKind.Start,
                "halfyear-end" => SchoolDayKind.HalfYearEnd,
                "end" => SchoolDayKind.End,
                "autumn" => SchoolDayKind.Autumn,
                "christmas" => SchoolDayKind.Christmas,
                "halfyear" => SchoolDayKind.HalfYear,
                "spring" => SchoolDayKind.Spring,
                "easter" => SchoolDayKind.Easter,
                "summer" => SchoolDayKind.Summer,
                _ => throw new FormatException("Unknown school period: " + f[2]),
            };
            var areas = f[5] == "*" ? [] : f[5].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            periods.Add(new SchoolPeriod(f[0], f[1], kind, DateOnly.ParseExact(f[3], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly.ParseExact(f[4], "yyyy-MM-dd", CultureInfo.InvariantCulture), areas));
        }

        return periods;
    }
}
