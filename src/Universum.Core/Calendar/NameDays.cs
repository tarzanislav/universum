using System.Globalization;
using System.Reflection;
using System.Text;

namespace Universum.Core.Calendar;

/// <summary>
/// The calendar of names – who has the name day (meniny, jmeniny) – in Slovakia and Czechia, and the English equivalents
/// of those names (English name days are not a tradition; "Ján" is shown as "John"). UNIVERSUM's own lists, compiled for
/// it on 31. 10. 2026 and checked against public calendars (namedays.txt, english-names.txt), built into the program.
/// </summary>
public static class NameDays
{
    private static readonly Lazy<Dictionary<string, (string[] Sk, string? SkLabel, string[] Cs, string? CsLabel)>> Days = new(LoadDays);
    private static readonly Lazy<Dictionary<string, string>> English = new(LoadEnglish);

    /// <summary>The Slovak names of the date (empty on a day without a name).</summary>
    public static IReadOnlyList<string> Slovak(DateOnly date) => Day(date).Sk;

    /// <summary>The Czech names of the date.</summary>
    public static IReadOnlyList<string> Czech(DateOnly date) => Day(date).Cs;

    /// <summary>The names of the date in the language: "sk", "cs", or "en" – the English equivalents of the Slovak and Czech names.</summary>
    public static IReadOnlyList<string> Of(DateOnly date, string language) => language switch
    {
        "cs" => Czech(date),
        "en" => EnglishOf(date),
        _ => Slovak(date),
    };

    /// <summary>The English equivalents of the date's Slovak and Czech names (each once, in their order).</summary>
    public static IReadOnlyList<string> EnglishOf(DateOnly date) =>
        [.. Slovak(date).Concat(Czech(date)).Select(n => English.Value.GetValueOrDefault(n)).OfType<string>().Distinct(StringComparer.Ordinal)];

    /// <summary>What the day is when it has no name in the language's calendar ("Pamiatka zosnulých"); null otherwise.</summary>
    public static string? DayWithoutName(DateOnly date, string language) => language == "cs" ? Day(date).CsLabel : Day(date).SkLabel;

    /// <summary>"Meniny má Sabína" / "Svátek má Sabina" / "Name day: Sabina", or the day without a name.</summary>
    public static string Text(DateOnly date, string language)
    {
        var names = Of(date, language);
        if (names.Count == 0)
        {
            return language == "en" ? "" : DayWithoutName(date, language) ?? "";
        }

        var list = string.Join(", ", names);
        return language switch
        {
            "cs" => (names.Count == 1 ? "Svátek má " : "Svátek mají ") + list,
            "en" => (names.Count == 1 ? "Name day: " : "Name days: ") + list,
            _ => (names.Count == 1 ? "Meniny má " : "Meniny majú ") + list,
        };
    }

    private static (string[] Sk, string? SkLabel, string[] Cs, string? CsLabel) Day(DateOnly date) =>
        Days.Value.GetValueOrDefault(date.ToString("MM-dd", CultureInfo.InvariantCulture));

    private static IEnumerable<string> Lines(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException("The calendar of names is missing: " + resource);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0 && line[0] != '#')
            {
                yield return line.Normalize(NormalizationForm.FormC);
            }
        }
    }

    private static Dictionary<string, (string[], string?, string[], string?)> LoadDays()
    {
        static (string[], string?) Column(string text) =>
            text.StartsWith('=') ? ([], text[1..]) : (text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), null);

        var days = new Dictionary<string, (string[], string?, string[], string?)>(StringComparer.Ordinal);
        foreach (var line in Lines("Universum.Core.Calendar.namedays.txt"))
        {
            var f = line.Split('|');
            var (sk, skLabel) = Column(f[1]);
            var (cs, csLabel) = Column(f[2]);
            days[f[0]] = (sk, skLabel, cs, csLabel);
        }

        return days;
    }

    private static Dictionary<string, string> LoadEnglish()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines("Universum.Core.Calendar.english-names.txt"))
        {
            var (english, forms) = (line[..line.IndexOf('=')].Trim(), line[(line.IndexOf('=') + 1)..]);
            foreach (var form in forms.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                names[form] = english;
            }
        }

        return names;
    }
}

/// <summary>Dates in words in the languages of UNIVERSUM.</summary>
public static class CalendarTexts
{
    /// <summary>The culture of a language code ("sk", "cs", "en").</summary>
    public static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(language switch { "cs" => "cs-CZ", "en" => "en-GB", _ => "sk-SK" });

    /// <summary>"utorok 27. októbra 2026" · "úterý 27. října 2026" · "Tuesday 27 October 2026".</summary>
    public static string LongDate(DateOnly date, string language) =>
        date.ToString(language == "en" ? "dddd d MMMM yyyy" : "dddd d. MMMM yyyy", Culture(language));

    /// <summary>The ISO week number (weeks start on Monday; week 1 holds the first Thursday).</summary>
    public static int Week(DateOnly date) => ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue));
}
