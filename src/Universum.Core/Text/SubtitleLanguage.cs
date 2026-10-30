using System.Globalization;

namespace Universum.Core.Text;

/// <summary>The language of a subtitle text: certain, or the most probable one (the user is told).</summary>
/// <param name="Language">Primary language subtag ("cs", "sk", "en" …).</param>
/// <param name="Certain">The language is certain (else only the most probable one – "overte").</param>
/// <param name="Detail">What decided it, in Slovak ("ř, ě, ů 14×, české slová 9×").</param>
/// <param name="CzechOrSlovak">
/// The text is certainly Czech or Slovak (their letters and words) – only which of the two may be uncertain.
/// </param>
public sealed record SubtitleLanguageResult(string Language, bool Certain, string Detail, bool CzechOrSlovak = false)
{
    /// <summary>
    /// The result replaces the language the file has for the track: an undetermined one always; Czech or Slovak read
    /// from their letters and words any language (owner, 29. 10.: "EN" that is Czech), a mere guess between them only
    /// Czech or Slovak. Another language (English, German …, told by frequent words) replaces only a language the
    /// detector knows – never Dutch, Spanish or Slovenian written in the file, which it cannot tell.
    /// </summary>
    public bool Replaces(Media.LanguageTag inFile) =>
        inFile.IsUndetermined
        || (Language is "cs" or "sk" ? Certain || CzechOrSlovak || inFile.IsCzechOrSlovak
            : inFile.IsCzechOrSlovak || SubtitleLanguage.Detectable.Contains(inFile.Primary));
}

/// <summary>
/// The language of subtitles from their text (owner, 30. 10. 2026). Czech and Slovak – often mixed up in files – are told
/// apart by their own letters and words:
/// <list type="bullet">
/// <item>Czech: the letters ě, ř, ů and the owner's list of Czech words (mě, tě, něco, ještě …);</item>
/// <item>Slovak: the letters ä, ĺ, ľ, ô, ŕ and the owner's list of Slovak words (môj, päť, veľmi, ľudia …); "dz" and "dž"
/// are only a weak sign (Czech has them too: podzim, džus).</item>
/// </list>
/// Signs of one language only make it certain; signs of both decide by their count (a Czech name in Slovak subtitles
/// does not make them Czech). A text with letters Czech and Slovak never use (German ö, French è, Spanish ñ, Croatian ć,
/// Romanian ș …) is neither; ä and ô alone (German, French, Finnish have them too) make Slovak only probable; without any
/// diacritics (old subtitles) the short words decide – only as the most probable language.
/// </summary>
public static class SubtitleLanguage
{
    private const string CzechLetters = "ěřů";

    // the letters only Slovak has; ä and ô are Slovak too, but also German, French, Finnish – alone only a weak sign
    private const string SlovakLetters = "ĺľŕ";
    private const string SlovakWeakLetters = "äô";

    // the whole Czech and Slovak alphabet beyond a–z: any other letter (ö, è, ñ, ć, ș, ł, Cyrillic …) is another language
    private const string Alphabet = "áäčďéěíĺľňóôŕřšťúůýž";

    // the letters with a háček only Czech and Slovak (of the languages in films) write this often
    private const string Hacek = "čďňšťž";

    // short words of the Czech and Slovak lists other languages write as well (Romanian, Italian, Spanish, Croatian,
    // Slovenian, Polish, Swedish): they do not tell a text without diacritics is Czech or Slovak
    private static readonly HashSet<string> Shared = new(
        ["se", "ne", "co", "sa", "ste", "so", "ma", "ako", "nie", "som", "ano", "pro", "jak", "nic", "ve", "vo"], StringComparer.Ordinal);

    /// <summary>Signs of Czech or Slovak count only when there are this many words (a name in English subtitles is not Czech).</summary>
    private const int ManyWords = 100;

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
        if (other is not null and not "cs" and not "sk")
        {
            // Turkish taken for German (ö, ü), Dutch for English …: letters the language does not write say it is another one
            return Strange(lower, other) ? null : new SubtitleLanguageResult(other, true, "podľa častých slov a písmen");
        }

        // letters Czech and Slovak never use (and the Spanish ¿ ¡): another language – unless one or a few in a long text (names)
        var diacritics = lower.Count(c => Alphabet.Contains(c));
        var foreign = lower.Count(c => (char.IsLetter(c) && c > '\u007f' && !Alphabet.Contains(c)) || c is '¿' or '¡');
        if (foreign > 1 && foreign * 20 >= diacritics)
        {
            return null;
        }

        var words = Words(lower).ToList();
        var czechLetters = lower.Count(c => CzechLetters.Contains(c));
        var slovakLetters = lower.Count(c => SlovakLetters.Contains(c));
        var slovakWeak = lower.Count(c => SlovakWeakLetters.Contains(c));
        var czechWords = words.Count(CzechWords.Contains);
        var slovakWords = words.Count(SlovakWords.Contains);
        var czech = czechLetters + czechWords;
        var slovak = slovakLetters + slovakWords;
        var (czechShort, slovakShort) = CzechOrSlovak.Count(lower);
        var (czechOwn, slovakOwn) = CzechOrSlovak.CountWithout(Shared, lower);
        var dz = Occurrences(lower, "dz") + Occurrences(lower, "dž");
        var hacek = lower.Count(c => Hacek.Contains(c));

        if (czech > 0 || slovak > 0)
        {
            var detail = Detail(czechLetters, czechWords, slovakLetters + slovakWeak, slovakWords);
            if (slovak == 0)
            {
                // ä, ô beside Czech signs: Slovak signs too (Czech never writes them)
                return slovakWeak == 0 ? new SubtitleLanguageResult("cs", true, detail, true) : Both("cs", czech, slovakWeak, detail, other);
            }

            if (czech == 0)
            {
                return new SubtitleLanguageResult("sk", true, detail, true);
            }

            return Both(czech >= slovak + slovakWeak ? "cs" : "sk", czech, slovak + slovakWeak, detail, other);
        }

        // ä and ô only (German, French, Finnish have them too): Slovak only when Czech and Slovak are there besides them
        if (slovakWeak > 0)
        {
            return hacek > 0 || slovakOwn > czechOwn
                ? new SubtitleLanguageResult("sk", false, $"len ä, ô {slovakWeak}× – overte")
                : null;
        }

        // no letter or word of the lists – subtitles without diacritics: the short words of each (jsem / som, tady / teraz …)
        // and the weak dz, dž; the words other languages write too (se, ne, ste, ako …: Romanian, Italian, Croatian
        // subtitles are often without diacritics as well) do not count; never a text with other diacritics (a long Czech or
        // Slovak text always has ě or ľ, ô …)
        var plain = diacritics <= Math.Max(2, lower.Count(char.IsLetter) / 500);
        var share = words.Count == 0 ? 0 : (czechShort + slovakShort) / (double)words.Count;
        if (plain && czechOwn + slovakOwn >= 3 && share >= 0.06)
        {
            var language = slovakOwn + (dz * 0.5) > czechOwn ? "sk" : "cs";
            return new SubtitleLanguageResult(language, false,
                $"bez diakritiky – podľa krátkych slov (české {czechOwn}×, slovenské {slovakOwn}×" + (dz > 0 ? $", dz/dž {dz}×" : "") + ") – overte");
        }

        // a short text with háčky but none of the letters above (forced subtitles: "Ako sa máš?"): all the short words
        // and dz, dž – at least one of them only Czech or Slovak writes
        if (hacek > 0 && words.Count < ManyWords && czechOwn + slovakOwn >= 1 && czechShort + slovakShort >= 3)
        {
            var language = slovakShort + (dz * 0.5) > czechShort ? "sk" : "cs";
            return new SubtitleLanguageResult(language, false,
                $"krátky text bez ě, ř, ů, ľ, ô – podľa krátkych slov (české {czechShort}×, slovenské {slovakShort}×" + (dz > 0 ? $", dz/dž {dz}×" : "") + ") – overte");
        }

        // a short text with háčky but without ě, ř, ů, ľ … (forced subtitles): the most probable by the frequent words; in
        // a long text it is Slovenian, Croatian, Lithuanian – none of the two; without háčky nothing to say
        return other is null || words.Count >= ManyWords || hacek == 0 ? null
            : new SubtitleLanguageResult(other, false, "podľa častých slov – overte");
    }

    /// <summary>The languages <see cref="TextLanguageDetector"/> tells apart (any other one it cannot recognise).</summary>
    public static readonly IReadOnlySet<string> Detectable = new HashSet<string>(["cs", "sk", "en", "de", "pl", "hu", "ru", "uk"], StringComparer.Ordinal);

    // the letters beyond a–z each language of the detector writes
    private static readonly Dictionary<string, string> Letters = new(StringComparer.Ordinal)
    {
        ["en"] = "", ["de"] = "äöüß", ["pl"] = "ąćęłńóśźż", ["hu"] = "áéíóöőúüű",
    };

    // more than one letter in fifty the language never writes (a name, a loan word "café" is fewer; Turkish ı, ş, ğ many more)
    private static bool Strange(string text, string language)
    {
        if (!Letters.TryGetValue(language, out var own))
        {
            return false;
        }

        var letters = text.Count(char.IsLetter);
        var strange = text.Count(c => char.IsLetter(c) && c > '\u007f' && !own.Contains(c));
        return strange > 2 && strange * 50 > letters;
    }

    // signs of both languages: the clear majority decides (a name, a quotation in the other language)
    private static SubtitleLanguageResult Both(string winner, int czech, int slovak, string detail, string? other)
    {
        var (more, fewer) = winner == "cs" ? (czech, slovak) : (slovak, czech);
        return more >= 5 * fewer ? new SubtitleLanguageResult(winner, true, detail, true)
            : more > fewer ? new SubtitleLanguageResult(winner, false, detail + " – overte", true)
            : new SubtitleLanguageResult(other ?? winner, false, detail + " – nerozhodné, overte", true);
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
