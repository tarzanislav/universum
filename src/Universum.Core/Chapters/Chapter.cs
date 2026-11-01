using System.Globalization;
using Universum.Core.Media;
using Universum.Core.Text;

namespace Universum.Core.Chapters;

/// <summary>One chapter: where it starts, its name and the name's language.</summary>
public sealed record Chapter(TimeSpan Start, string Name, LanguageTag Language);

/// <summary>Chapters read from a file, and whether the chapter editor can edit them without losing anything.</summary>
/// <param name="Complex">Several editions, nested chapters or several names per chapter: kept as they are, not edited here.</param>
public sealed record ChapterSet(IReadOnlyList<Chapter> Chapters, bool Complex = false)
{
    public static ChapterSet None { get; } = new([]);
}

/// <summary>Chapter times as "HH:MM:SS.mmm".</summary>
public static class ChapterTime
{
    public static string Format(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";

    /// <summary>Accepts "1:02:03.5", "02:03", "02:03.250", "123.5" (seconds); null when the text is no time.</summary>
    public static TimeSpan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3 || parts.Any(p => p.Length == 0))
        {
            return null;
        }

        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
                value < 0 || (!last && parts[i].Contains('.')) || (i > 0 && value >= 60))
            {
                return null;
            }

            total = total * 60 + value;
        }

        return TimeSpan.FromMilliseconds(Math.Round(total * 1000));
    }
}

/// <summary>What is wrong with a chapter list (in Slovak, for the user).</summary>
public static class ChapterChecks
{
    public static IReadOnlyList<string> Problems(IReadOnlyList<Chapter> chapters, TimeSpan? duration)
    {
        var problems = new List<string>();
        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            if (duration is { } length && chapter.Start >= length)
            {
                problems.Add(CoreText.F("Kapitola {0} začína po konci súboru ({1} ≥ {2}).", i + 1, ChapterTime.Format(chapter.Start), ChapterTime.Format(length)));
            }
        }

        foreach (var same in chapters.GroupBy(c => c.Start).Where(g => g.Count() > 1))
        {
            problems.Add(CoreText.F("Viac kapitol začína v rovnakom čase {0}.", ChapterTime.Format(same.Key)));
        }

        return problems;
    }

    /// <summary>Chapters every <paramref name="interval"/> from the start to the end of the file: "Kapitola 01", "Kapitola 02", …</summary>
    public static IReadOnlyList<Chapter> Every(TimeSpan interval, TimeSpan duration, LanguageTag language, string name = "Kapitola")
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        var chapters = new List<Chapter>();
        for (var start = TimeSpan.Zero; start < duration || chapters.Count == 0; start += interval)
        {
            chapters.Add(new Chapter(start, $"{name} {chapters.Count + 1:00}", language));
        }

        return chapters;
    }
}
