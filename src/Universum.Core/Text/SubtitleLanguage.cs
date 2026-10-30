using System.Globalization;

namespace Universum.Core.Text;

/// <summary>The language of a subtitle text: certain, or the most probable one (the user is told).</summary>
/// <param name="Language">Primary language subtag ("cs", "sk", "en" …).</param>
/// <param name="Detail">What decided it, in Slovak ("ř, ě, ů 14×, české slová 9×").</param>
public sealed record SubtitleLanguageResult(string Language, bool Certain, string Detail);

/// <summary>
/// The language of subtitles from their text (owner, 30. 10. 2026). Czech and Slovak – often mixed up in files – are told
/// apart by their own letters and words:
/// <list type="bullet">
/// <item>Czech: the letters ě, ř, ů and the owner's list of Czech words (mě, tě, něco, ještě …);</item>
/// <item>Slovak: the letters ä, ĺ, ľ, ô, ŕ and the owner's list of Slovak words (môj, päť, veľmi, ľudia …); "dz" and "dž"
/// are only a weak sign (Czech has them too: podzim, džus).</item>
/// </list>
/// Signs of one language only make it certain; signs of both decide by their count (a Czech name in Slovak subtitles
/// does not make them Czech). German, Polish and Hungarian texts (ä is German too) are recognised first.
/// </summary>
public static class SubtitleLanguage
{
    private const string CzechLetters = "ěřů";
    private const string SlovakLetters = "äĺľôŕ";

    // letters of languages with ä, ó … that Czech and Slovak do not use: such a text is not Czech or Slovak
    private const string OtherLetters = "öüßłąęśźżńőű";

    private static readonly HashSet<string> CzechWords = new(
        [
            "mě", "tě", "mně", "něm", "těch", "těm", "dvě", "pět", "měl", "chtěl", "směl", "měj", "běž", "věř", "věc", "věk", "věž", "svět",
            "směr", "spěch", "děj", "běh", "směs", "zpět", "tobě", "sobě", "něho", "němu", "těmi", "obě", "něco", "někdo", "někde", "něčem",
            "ještě", "prostě", "klidně", "pěkně", "skvěle", "špatně", "správně", "opět", "děti", "město", "země", "oběd", "peněz", "věci",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> SlovakWords = new(
        [
            "môj", "skôr", "päť", "späť", "môcť", "dôjsť", "žiaľ", "veľmi", "veľa", "podľa", "koľko", "toľko", "vôbec", "môžem", "môžeš", "môže",
            "môžu", "kvôli", "zatiaľ", "odtiaľ", "pokiaľ", "ľudia", "ľudí", "veľký", "veľká", "veľké", "rôzne", "najmä",
        ],
        StringComparer.Ordinal);

    /// <summary>The language of the text, or null when it cannot be told (too short, no signs).</summary>
    public static SubtitleLanguageResult? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lower = text.Normalize(System.Text.NormalizationForm.FormC).ToLower(CultureInfo.InvariantCulture);
        var other = TextLanguageDetector.Detect(lower);
        var foreign = lower.Count(c => OtherLetters.Contains(c));
        if (other is not null and not "cs" and not "sk")
        {
            return new SubtitleLanguageResult(other, true, "podľa častých slov a písmen");
        }

        // a short text with German, Polish or Hungarian letters is none of the two – nothing to say
        if (foreign > 0 && foreign * 4 >= lower.Count(c => CzechLetters.Contains(c) || SlovakLetters.Contains(c)))
        {
            return other is null ? null : new SubtitleLanguageResult(other, false, "podľa častých slov");
        }

        var words = Words(lower).ToList();
        var czechLetters = lower.Count(c => CzechLetters.Contains(c));
        var slovakLetters = lower.Count(c => SlovakLetters.Contains(c));
        var czechWords = words.Count(CzechWords.Contains);
        var slovakWords = words.Count(SlovakWords.Contains);
        var czech = czechLetters + czechWords;
        var slovak = slovakLetters + slovakWords;
        var weak = Occurrences(lower, "dz") + Occurrences(lower, "dž");

        if (czech > 0 || slovak > 0)
        {
            var detail = Detail(czechLetters, czechWords, slovakLetters, slovakWords);
            if (slovak == 0)
            {
                return new SubtitleLanguageResult("cs", true, detail);
            }

            if (czech == 0)
            {
                return new SubtitleLanguageResult("sk", true, detail);
            }

            // signs of both: the clear majority decides (a name, a quotation in the other language)
            var (winner, more, fewer) = czech >= slovak ? ("cs", czech, slovak) : ("sk", slovak, czech);
            return more >= 5 * fewer ? new SubtitleLanguageResult(winner, true, detail)
                : more > fewer ? new SubtitleLanguageResult(winner, false, detail + " – overte")
                : new SubtitleLanguageResult(other ?? "cs", false, detail + " – nerozhodné, overte");
        }

        // no letter or word of the lists: the short words of each (se / sa, jsem / som …) and the weak dz, dž
        var (czechShort, slovakShort) = CzechOrSlovak.Count(lower);
        if (czechShort + slovakShort + weak > 0 && (other is "cs" or "sk" || czechShort + slovakShort >= 3))
        {
            var slovakScore = slovakShort + (weak * 0.5);
            var language = slovakScore > czechShort ? "sk" : "cs";
            return new SubtitleLanguageResult(language, false,
                $"bez českých a slovenských písmen – podľa krátkych slov (české {czechShort}×, slovenské {slovakShort}×" +
                (weak > 0 ? $", dz/dž {weak}×" : "") + ") – overte");
        }

        return other is null ? null : new SubtitleLanguageResult(other, false, "podľa častých slov – overte");
    }

    private static string Detail(int czechLetters, int czechWords, int slovakLetters, int slovakWords)
    {
        var parts = new List<string>();
        if (czechLetters > 0)
        {
            parts.Add($"ě, ř, ů {czechLetters}×");
        }

        if (czechWords > 0)
        {
            parts.Add($"české slová {czechWords}×");
        }

        if (slovakLetters > 0)
        {
            parts.Add($"ä, ĺ, ľ, ô, ŕ {slovakLetters}×");
        }

        if (slovakWords > 0)
        {
            parts.Add($"slovenské slová {slovakWords}×");
        }

        return string.Join(", ", parts);
    }

    private static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static IEnumerable<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '„', '“', '”', '(', ')', '-', '–', '…', '*', '<', '>'))
            .Where(w => w.Length > 0);
}
