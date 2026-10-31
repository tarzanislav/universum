using System.Globalization;
using System.Text;

namespace Universum.Core.Agenda;

/// <summary>A task written in one line, understood: its title, due day and hour, tags and priority.</summary>
public sealed record QuickTask(string Title, DateOnly? Due, TimeOnly? Time, IReadOnlyList<string> Tags, int Priority);

/// <summary>
/// Rýchle pridanie (owner, 31. 10. 2026): a task written as one would say it – "zajtra o 20:00 spracovať film #kino !",
/// "do piatku zavolať", "za 3 dni …", "15. 11. …", "zítra v 8:30 …", "tomorrow 9:15 …" – in Slovak, Czech or English.
/// The words of the day and hour are taken out of the title; "#slovo" is a tag, "!" a high priority, "!nízka" a low one.
/// </summary>
public static class QuickEntry
{
    private static readonly string[][] Weekdays =
    [
        // Monday … Sunday: Slovak, Czech (also after "do" and "v") and English, without diacritics
        ["pondelok", "pondelka", "pondeli", "pondeli", "monday"],
        ["utorok", "utorka", "utery", "utery", "tuesday"],
        ["streda", "stredy", "stredu", "wednesday"],
        ["stvrtok", "stvrtka", "ctvrtek", "ctvrtka", "thursday"],
        ["piatok", "piatku", "patek", "patku", "friday"],
        ["sobota", "soboty", "sobotu", "saturday"],
        ["nedela", "nedele", "nedelu", "nedeli", "sunday"],
    ];

    private static readonly HashSet<string> Prepositions = ["o", "v", "vo", "na", "za", "do", "ve", "at", "on", "in", "by", "next", "buduci", "buducu", "budouci", "pristi"];

    // the words before "týždeň" and "mesiac" that make them a day ("za týždeň", "o mesiac", "next week", "příští týden")
    private static readonly HashSet<string> Ahead = ["o", "za", "next", "buduci", "buducu", "budouci", "pristi"];

    /// <summary>Understands the line (relative days counted from <paramref name="today"/>).</summary>
    public static QuickTask Parse(string text, DateOnly today)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var used = new bool[words.Count];
        var tags = new List<string>();
        var priority = 1;
        DateOnly? due = null;
        TimeOnly? time = null;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var plain = Plain(word);
            if (word.Length > 1 && word[0] == '#')
            {
                tags.Add(word[1..].TrimEnd(',', ';', '.'));
                used[i] = true;
            }
            else if (word[0] == '!' && plain.Trim('!') is "" or "vysoka" or "high")
            {
                priority = 2;
                used[i] = true;
            }
            else if (word[0] == '!' && plain.Trim('!') is "nizka" or "low")
            {
                priority = 0;
                used[i] = true;
            }
            else if (due is null && Day(words, i, today, out var day, out var taken))
            {
                due = day;
                Use(used, Preposition(words, i) ? i - 1 : i, taken + (Preposition(words, i) ? 1 : 0));
                i += taken - 1;
            }
            else if (time is null && Hour(plain, out var hour))
            {
                time = hour;
                Use(used, Preposition(words, i) ? i - 1 : i, Preposition(words, i) ? 2 : 1);
            }
        }

        var title = string.Join(' ', words.Where((_, i) => !used[i])).Trim(' ', ',', ';', '-', '–');
        return new QuickTask(title, due ?? (time is not null ? today : null), time, tags, priority);
    }

    private static void Use(bool[] used, int from, int count)
    {
        for (var i = Math.Max(0, from); i < Math.Min(used.Length, from + count); i++)
        {
            used[i] = true;
        }
    }

    // a preposition before the words of the day or hour ("o 20:00", "do piatku", "za týždeň") is taken with them
    private static bool Preposition(List<string> words, int i) => i > 0 && Prepositions.Contains(Plain(words[i - 1]));

    // the day in the words from i on; how many words it took
    private static bool Day(List<string> words, int i, DateOnly today, out DateOnly day, out int taken)
    {
        var w = Plain(words[i]);
        var next = i + 1 < words.Count ? Plain(words[i + 1]) : "";
        taken = 1;
        day = default;
        switch (w)
        {
            case "dnes" or "today":
                day = today;
                return true;
            case "zajtra" or "zitra" or "tomorrow":
                day = today.AddDays(1);
                return true;
            case "pozajtra" or "pozitri":
                day = today.AddDays(2);
                return true;
            case "tyzden" or "tyden" or "week":
                day = today.AddDays(7);
                return i > 0 && Ahead.Contains(Plain(words[i - 1]));
            case "mesiac" or "mesic" or "month":
                day = today.AddMonths(1);
                return i > 0 && Ahead.Contains(Plain(words[i - 1]));
        }

        for (var d = 0; d < 7; d++)
        {
            if (Weekdays[d].Contains(w))
            {
                var target = (DayOfWeek)((d + 1) % 7);
                var ahead = ((int)target - (int)today.DayOfWeek + 7) % 7;
                day = today.AddDays(ahead == 0 ? 7 : ahead);
                return true;
            }
        }

        // "3 dni", "3 dny", "3 days", "2 týždne", "2 weeks" after "za", "o", "in"
        if (int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is > 0 and < 1000 && i > 0 &&
            Plain(words[i - 1]) is "za" or "o" or "in")
        {
            taken = 2;
            switch (next)
            {
                case "den" or "dni" or "dny" or "dnu" or "day" or "days":
                    day = today.AddDays(count);
                    return true;
                case "tyzden" or "tyzdne" or "tyzdnov" or "tyden" or "tydny" or "tydnu" or "week" or "weeks":
                    day = today.AddDays(7 * count);
                    return true;
                case "mesiac" or "mesiace" or "mesiacov" or "mesic" or "mesice" or "mesicu" or "month" or "months":
                    day = today.AddMonths(count);
                    return true;
            }

            taken = 1;
        }

        // "15.11.", "15.11.2026", "2026-11-15", or "15." "11." as two words
        var date = w;
        if (w.EndsWith('.') && w.Count(c => c == '.') == 1 && next.EndsWith('.') && next.Length <= 3)
        {
            date = w + next;
            taken = 2;
        }

        if (DateOnly.TryParseExact(date, ["d.M.yyyy", "d.M.", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            if (date.EndsWith('.'))
            {
                // without a year: this year, or the next when the day is over
                parsed = new DateOnly(today.Year, parsed.Month, parsed.Day);
                if (parsed < today)
                {
                    parsed = parsed.AddYears(1);
                }
            }

            day = parsed;
            return true;
        }

        taken = 1;
        return false;
    }

    // "20:00", "8:30", "20h", "8.30h"
    private static bool Hour(string word, out TimeOnly time)
    {
        var w = word.TrimEnd('h');
        if (w.Length is >= 1 and <= 2 && word.EndsWith('h') && int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h < 24)
        {
            time = new TimeOnly(h, 0);
            return true;
        }

        return TimeOnly.TryParseExact(w.Replace('.', ':'), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    // lower case without diacritics
    private static string Plain(string word)
    {
        var decomposed = word.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(c);
            }
        }

        return plain.ToString().Normalize(NormalizationForm.FormC).TrimEnd(',', ';');
    }
}
