using System.Buffers;
using System.Globalization;
using System.Text;

namespace Universum.Core.Library;

/// <summary>Folder and file names of the library (specification, section 13; Jellyfin naming).</summary>
public static class LibraryNames
{
    public const string MoviesFolder = "MOVIES";

    public const string ShowsFolder = "TVSHOWS";

    /// <summary>Posters found alone (TMDB poster, owner 31. 10. 2026): "folder.jpg" in a title folder named as in MOVIES and TVSHOWS.</summary>
    public const string PostersFolder = "POSTER";

    /// <summary>Longest full path accepted for a result; longer paths are sent for review.</summary>
    public const int MaxPathLength = 250;

    private static readonly SearchValues<char> Forbidden = SearchValues.Create("<>:\"/\\|?*");

    private static readonly HashSet<string> Reserved = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { "COM" + i, "LPT" + i })],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Doplnok k názvu offered to choose (owner, 31. 10. 2026); any other short text can be written.</summary>
    public static IReadOnlyList<string> AdditionChoices { get; } = ["Directors Cut", "Remaster"];

    /// <summary>The longest Doplnok k názvu.</summary>
    public const int MaxAdditionLength = 30;

    /// <summary>
    /// Doplnok k názvu as it goes into the name (owner, 31. 10. 2026: "Directors Cut", "2"): letters, digits, spaces and
    /// hyphens – never a path or a sign Windows does not allow; white space joined. Null when empty; a problem in words
    /// when the text cannot be used (the size class is UNIVERSUM's own).
    /// </summary>
    public static (string? Addition, string? Problem) Addition(string? text)
    {
        var addition = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (addition.Length == 0)
        {
            return (null, null);
        }

        if (!addition.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-'))
        {
            return (null, "Doplnok k názvu môže mať len písmená, číslice, medzery a pomlčky.");
        }

        if (addition.Length > MaxAdditionLength)
        {
            return (null, $"Doplnok k názvu môže mať najviac {MaxAdditionLength} znakov.");
        }

        return SizeClasses.AllLabels.Any(label => addition.Split(' ').Contains(label, StringComparer.OrdinalIgnoreCase) || label.Equals(addition, StringComparison.OrdinalIgnoreCase))
            ? (null, "Veľkostnú triedu (BIG, SMALL, BluRay …) dáva UNIVERSUM na koniec názvu samo – do doplnku nepatrí.")
            : (addition.Normalize(NormalizationForm.FormC), null);
    }

    /// <summary>
    /// Removes characters Windows does not allow in names (<c>&lt; &gt; : " / \ | ? *</c> and control characters)
    /// without replacement, joins white space into single spaces and drops dots and spaces at the end.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (Forbidden.Contains(c) || char.IsControl(c))
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString().TrimEnd('.', ' ');
    }

    /// <summary>Names Windows reserves for devices (CON, NUL, COM1 …), also with an extension.</summary>
    public static bool IsReserved(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return Reserved.Contains((dot < 0 ? name : name[..dot]).TrimEnd());
    }

    /// <summary>
    /// True when every letter of the text is a Latin letter (with or without diacritics). Digits and punctuation do
    /// not count, so "1917" is Latin; Cyrillic, Greek, Hebrew, Arabic, Indian, Chinese, Japanese or Korean letters are not.
    /// </summary>
    public static bool IsLatinScript(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.IsLetter(rune) && !IsLatinLetter(rune.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>"Name (Year)" plus "[tmdbid-ID]" when the title asks for it.</summary>
    public static string FolderName(TitleInfo title)
    {
        var name = Clean(title.Name);
        if (title.Year is { } year)
        {
            name += " (" + year.ToString(CultureInfo.InvariantCulture) + ")";
        }

        if (title.IncludeId && title.TmdbId is { } id)
        {
            name += " [tmdbid-" + id.ToString(CultureInfo.InvariantCulture) + "]";
        }

        return name;
    }

    /// <summary>
    /// "Season 01", "Season 02" … "Season 10" – always at least two digits, never "Season 1"; specials are
    /// "Season 00" (owner's decision of 26 October 2026: fixed, must not change).
    /// </summary>
    public static string SeasonFolder(int season) => "Season " + season.ToString("00", CultureInfo.InvariantCulture);

    private static bool IsLatinLetter(int c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            or 0xAA or 0xBA
            or >= 0xC0 and <= 0x24F and not (0xD7 or 0xF7) // Latin-1 Supplement, Latin Extended-A and B
            or >= 0x250 and <= 0x2AF // IPA extensions
            or >= 0x1E00 and <= 0x1EFF // Latin Extended Additional
            or >= 0x2C60 and <= 0x2C7F // Latin Extended-C
            or >= 0xA720 and <= 0xA7FF // Latin Extended-D
            or >= 0xAB30 and <= 0xAB6F // Latin Extended-E
            or >= 0xFB00 and <= 0xFB06 // Latin ligatures
            or >= 0xFF21 and <= 0xFF3A or >= 0xFF41 and <= 0xFF5A; // full-width Latin
}
