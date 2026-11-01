using System.Text.RegularExpressions;
using Universum.Core.Text;

namespace Universum.Core.Subtitles.Ocr;

/// <summary>
/// Punctuation and spelling of read text (owner, 1. 11. 2026: the last phase of the checking): what the reading of
/// pictures typically gets wrong and the language tells – a dialogue dash without its space, a sentence or a comma glued
/// to the next word, a dot read where a comma stands (a small word goes on after it), doubled marks, spaced dots of an
/// ellipsis, the English pronoun "I" read as "l". Each change is noted with the words around it, so that it can be taken
/// back.
/// </summary>
public static partial class OcrPunctuation
{
    // words after which a dot is an abbreviation, not the end of a sentence
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "napr", "tzv", "atď", "resp", "tj", "t.j", "apod", "např", "atd", "aj", "pozn", "str", "kap", "č", "sv", "st", "ing", "mgr", "bc", "dr", "doc",
        "prof", "pán", "pí", "mr", "mrs", "ms", "dr", "jr", "sr", "vs", "etc", "e.g", "i.e", "approx", "no", "min", "max", "hod", "sek",
    };

    /// <summary>The line mended; each change (the words around it before and after) added to <paramref name="changes"/>.</summary>
    public static string Mend(string line, string? language, List<(string Before, string After, string Why)> changes)
    {
        var punctuation = CoreText.T("interpunkcia");
        var text = line;

        // "-Ahoj." → "- Ahoj." (a dialogue dash at the start of a line, also after an italic tag)
        text = Apply(text, DashWithoutSpace(), m => m.Groups["dash"].Value + " ", punctuation, changes);

        // spaced or doubled marks: ". . ." → "…", "?." → "?", "!." → "!", "word ." → "word."
        text = Apply(text, SpacedEllipsis(), _ => "…", punctuation, changes);
        text = Apply(text, DoubledMark(), m => m.Groups["mark"].Value, punctuation, changes);

        // "áno.Nie" → "áno. Nie"; "áno,ale" → "áno, ale" (never inside a number: 1,5 or 3.14)
        text = Apply(text, GluedSentence(), m => m.Groups["mark"].Value + " ", punctuation, changes);
        text = Apply(text, GluedComma(), _ => ", ", punctuation, changes);

        // "Áno. ale" → "Áno, ale": a dot before a word in small letters, after a word that is no abbreviation, is a comma
        // read wrongly (their shapes differ only by the comma's tail)
        text = ApplyWhen(text, DotBeforeSmallLetter(), m => !Abbreviations.Contains(m.Groups["word"].Value) && m.Groups["word"].Value.Length > 1,
            m => m.Groups["word"].Value + ",", punctuation, changes);

        // English: "l" alone, "l'm", "l'll", "l've", "l'd" are the pronoun I
        if (language is "en" or "eng")
        {
            var spelling = CoreText.T("pravopis");
            text = Apply(text, PronounI(), m => "I" + m.Groups["rest"].Value, spelling, changes);
        }

        return text;
    }

    private static string Apply(string text, Regex pattern, Func<Match, string> replacement, string why, List<(string Before, string After, string Why)> changes) =>
        ApplyWhen(text, pattern, _ => true, replacement, why, changes);

    // the matches replaced from the end (the places before stay valid); each change noted with whole words around it
    private static string ApplyWhen(string text, Regex pattern, Func<Match, bool> wanted, Func<Match, string> replacement, string why,
        List<(string Before, string After, string Why)> changes)
    {
        var matches = pattern.Matches(text).Where(wanted).ToList();
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var start = match.Index;
            var end = match.Index + match.Length;
            var replaced = replacement(match);
            var result = text[..start] + replaced + text[end..];

            // the change with the words around it (what Vrátiť finds in the subtitle again)
            var (from, to) = (start, end);
            while (from > 0 && char.IsLetter(text[from - 1]))
            {
                from--;
            }

            while (to < text.Length && char.IsLetter(text[to]))
            {
                to++;
            }

            var before = text[from..to];
            var after = result[from..(to + replaced.Length - match.Length)];
            if (before != after)
            {
                changes.Add((before, after, why));
            }

            text = result;
        }

        return text;
    }

    // a dash at the start of the line (or after <i>) glued to a letter
    [GeneratedRegex(@"(?<=^(?:<i>)?)(?<dash>-)(?=\p{L})", RegexOptions.CultureInvariant)]
    private static partial Regex DashWithoutSpace();

    [GeneratedRegex(@"\.\s\.\s\.", RegexOptions.CultureInvariant)]
    private static partial Regex SpacedEllipsis();

    // a mark followed by a dot that belongs to nothing; a space before a closing mark
    [GeneratedRegex(@"(?<mark>[!?])\.(?!\.)|\s(?<mark>[,.!?:;])(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DoubledMark();

    [GeneratedRegex(@"(?<=\p{Ll})(?<mark>[.!?])(?=\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex GluedSentence();

    [GeneratedRegex(@"(?<=\p{L}),(?=\p{L})", RegexOptions.CultureInvariant)]
    private static partial Regex GluedComma();

    [GeneratedRegex(@"(?<=(?:^|[\s„""“(]))(?<word>\p{L}+)\.(?=\s\p{Ll})", RegexOptions.CultureInvariant)]
    private static partial Regex DotBeforeSmallLetter();

    [GeneratedRegex(@"(?<=(?:^|[\s""“„(-]))l(?<rest>'(?:m|ll|ve|d)|’(?:m|ll|ve|d))?(?=[\s,.!?…]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PronounI();
}
