namespace Universum.Core.Text;

/// <summary>
/// Czech or Slovak by the short words only one of them uses (se / sa, jsem / som, pro / pre, když / keď…). Meant for
/// speech transcribed by whisper: its spelling leans to Czech (ů, ě also in Slovak speech), the words it hears less so.
/// </summary>
public static class CzechOrSlovak
{
    private static readonly HashSet<string> Czech = new(
        [
            "se", "jsem", "jsi", "jsme", "jste", "jsou", "ne", "pro", "co", "jak", "když", "jen", "také", "teď", "ještě", "ve", "který", "která",
            "které", "kteří", "byl", "byla", "bylo", "byli", "být", "může", "můžu", "můžeš", "ano", "děkuji", "děkuju", "nic", "něco", "všechno",
            "proč", "chci", "vím", "nevím", "řekl", "tedy", "všichni", "člověk", "nebo", "protože", "mě", "mně", "tady",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> Slovak = new(
        [
            "sa", "som", "sme", "ste", "sú", "nie", "pre", "čo", "ako", "keď", "aj", "len", "tiež", "teraz", "ešte", "vo", "ktorý", "ktorá",
            "ktoré", "ktorí", "bol", "bola", "bolo", "boli", "byť", "môže", "môžem", "môžeš", "áno", "ďakujem", "nič", "niečo", "všetko",
            "prečo", "chcem", "viem", "neviem", "povedal", "všetci", "človek", "alebo", "pretože", "ma", "iba", "lebo", "so",
        ],
        StringComparer.Ordinal);

    /// <summary>How many Czech-only and Slovak-only words the texts hold.</summary>
    public static (int Czech, int Slovak) Count(params string?[] texts) => CountWithout(new HashSet<string>(), texts);

    /// <summary>How many Czech-only and Slovak-only words the texts hold, leaving out the words in <paramref name="skip"/>.</summary>
    public static (int Czech, int Slovak) CountWithout(IReadOnlySet<string> skip, params string?[] texts)
    {
        int czech = 0, slovak = 0;
        foreach (var word in texts.SelectMany(Words).Where(w => !skip.Contains(w)))
        {
            if (Czech.Contains(word))
            {
                czech++;
            }
            else if (Slovak.Contains(word))
            {
                slovak++;
            }
        }

        return (czech, slovak);
    }

    /// <summary>"cs" or "sk" when the texts hold at least three words of one and at least twice as many as of the other; else null.</summary>
    public static string? Decide(params string?[] texts) => By(Count(texts), 2.0);

    /// <summary>The language the texts lean to: at least three of its words and half as many again as of the other; else null.</summary>
    public static string? Lean(params string?[] texts) => By(Count(texts), 1.5);

    private static string? By((int Czech, int Slovak) count, double times) =>
        count.Slovak >= 3 && count.Slovak >= times * count.Czech ? "sk"
        : count.Czech >= 3 && count.Czech >= times * count.Slovak ? "cs"
        : null;

    private static IEnumerable<string> Words(string? text) =>
        (text ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '„', '“', '(', ')', '-', '–', '…'))
            .Where(w => w.Length > 0);
}
