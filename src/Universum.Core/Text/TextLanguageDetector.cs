using System.Text;

namespace Universum.Core.Text;

/// <summary>
/// Recognises the language of subtitle text from frequent short words and characteristic letters. Tuned to tell
/// Czech from Slovak reliably (jsem/som, se/sa, ř ě ů / ä ô ľ ĺ ŕ); also English, German, Polish, Hungarian, Russian.
/// </summary>
public static class TextLanguageDetector
{
    private const int MinimumWords = 25;

    private static readonly (string Language, HashSet<string> Words, string Letters)[] Profiles =
    [
        ("cs", Set("je se jsem jsi jsme jste není nejsem co proč když jak tady teď taky ještě už mám máš musím můžu chci nevím vím děkuji prosím ano ne jo tak ale že to na v ve s z do pro který která které jenom jen tohle tohle"), "řěů"),
        ("sk", Set("je sa som si sme ste nie nie som čo prečo keď ako tu teraz tiež ešte už mám máš musím môžem chcem neviem viem ďakujem prosím áno nie hej tak ale že to na v vo s z do pre ktorý ktorá ktoré iba len toto"), "äôľĺŕ"),
        ("en", Set("the you i to a and it is that what of in me my we this don't i'm it's your be have do not are was for on with he she they can know just all no yes"), ""),
        ("de", Set("ich du der die das und ist nicht es sie wir ein eine zu was mit den mir dich mich ja nein auf ist hier wie aber bin hast"), "äöüß"),
        ("pl", Set("się nie to jest że w na co jak tak ale ja ty czy mnie jestem być mam wiem może tylko już tu dobrze proszę dziękuję"), "łąęśźżń"),
        ("hu", Set("a az és hogy nem egy van ez meg de mi ki már csak még igen nincs vagyok vagy itt most jó köszönöm kérem"), "őű"),
    ];

    /// <summary>The recognised language (primary subtag) or null when the text is too short or ambiguous.</summary>
    public static string? Detect(string text)
    {
        var words = Tokens(text).ToList();
        if (words.Count < MinimumWords)
        {
            return null;
        }

        var cyrillic = text.Count(c => c is >= 'Ѐ' and <= 'ӿ');
        if (cyrillic > text.Count(char.IsLetter) / 2)
        {
            return text.Any(c => c is 'і' or 'ї' or 'є' or 'ґ') ? "uk" : "ru";
        }

        var scores = Profiles
            .Select(p => (p.Language, Score: Score(words, text, p.Words, p.Letters)))
            .OrderByDescending(s => s.Score)
            .ToList();
        var best = scores[0];
        var second = scores[1];
        return best.Score >= 0.15 && best.Score >= second.Score * 1.4 ? best.Language : null;
    }

    private static double Score(List<string> words, string text, HashSet<string> frequent, string letters)
    {
        var hits = words.Count(frequent.Contains) / (double)words.Count;
        if (letters.Length == 0)
        {
            return hits;
        }

        var letterCount = text.Count(c => letters.Contains(char.ToLowerInvariant(c)));
        var letterShare = letterCount / (double)Math.Max(1, text.Count(char.IsLetter));
        return hits + Math.Min(0.3, letterShare * 15);
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetter(c) || c == '\'')
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static HashSet<string> Set(string words) => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
}
