using System.Globalization;
using System.Reflection;

namespace Universum.Core.Calendar;

/// <summary>
/// The Slovak calendar of names (who has the name day, "meniny") and the days without a name. The list is the current
/// Slovak calendar as kept by the name-day-calendar project (MIT licence, Peter Knezek; see THIRD-PARTY-NOTICES.md),
/// stored in the program: no internet needed.
/// </summary>
public static class SlovakCalendar
{
    private static readonly Lazy<Dictionary<string, IReadOnlyList<string>>> Names = new(Load);

    // the days of the calendar that carry no name
    private static readonly Dictionary<string, string> DaysWithoutName = new()
    {
        ["01-01"] = "Nový rok – Deň vzniku Slovenskej republiky",
        ["05-01"] = "Sviatok práce",
        ["07-17"] = "Výročie Deklarácie o zvrchovanosti Slovenskej republiky",
        ["11-02"] = "Pamiatka zosnulých",
        ["12-25"] = "Prvý sviatok vianočný",
    };

    private static readonly CultureInfo Slovak = CultureInfo.GetCultureInfo("sk-SK");

    /// <summary>Who has the name day on a date ("Sabína"); empty on a day without a name.</summary>
    public static IReadOnlyList<string> NameDay(DateOnly date) => Names.Value.GetValueOrDefault(Key(date)) ?? [];

    /// <summary>What the day is when it has no name ("Pamiatka zosnulých"); null otherwise.</summary>
    public static string? DayWithoutName(DateOnly date) => DaysWithoutName.GetValueOrDefault(Key(date));

    /// <summary>"Meniny má Sabína", "Meniny majú Adam, Eva", or the day without a name.</summary>
    public static string NameDayText(DateOnly date)
    {
        var names = NameDay(date);
        return names.Count switch
        {
            0 => DayWithoutName(date) ?? "",
            1 => "Meniny má " + names[0],
            _ => "Meniny majú " + string.Join(", ", names),
        };
    }

    /// <summary>"utorok 27. októbra 2026".</summary>
    public static string LongDate(DateOnly date) => date.ToString("dddd d. MMMM yyyy", Slovak);

    private static string Key(DateOnly date) => date.ToString("MM-dd", CultureInfo.InvariantCulture);

    private static Dictionary<string, IReadOnlyList<string>> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Universum.Core.Calendar.sk-namedays.txt")
                           ?? throw new InvalidOperationException("The Slovak calendar of names is missing.");
        using var reader = new StreamReader(stream);
        var names = new Dictionary<string, IReadOnlyList<string>>();
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Normalize(System.Text.NormalizationForm.FormC).Split('|', 2);
            if (parts.Length == 2)
            {
                names[parts[0]] = parts[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }
        }

        return names;
    }
}
