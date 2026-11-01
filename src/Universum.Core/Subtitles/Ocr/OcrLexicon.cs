using System.Text.Json;
using Universum.Core.Text;

namespace Universum.Core.Subtitles.Ocr;

/// <summary>
/// The words of one language and how often they were seen, with a model of which letters follow which (letter triples)
/// and which words stand next to which (pairs of words – owner, 1. 11. 2026: "sme" or "sem" by the word before):
/// what the checking of read text knows of the language. It is learned only from text – the user's own subtitle files
/// and the words a film's reading is sure of – and kept in UNIVERSUM's folder; nothing is sent anywhere.
/// </summary>
public sealed class OcrLexicon
{
    /// <summary>At most this many different words are kept (the rarest go first).</summary>
    public const int MaximumWords = 300_000;

    /// <summary>At most this many different pairs of words are kept (the rarest go first).</summary>
    public const int MaximumPairs = 500_000;

    private const char Start = '^';
    private const char End = '$';
    private readonly Dictionary<string, int> _words = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _grams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pairs = new(StringComparer.Ordinal);
    private long _letters;

    /// <summary>How many different words it knows.</summary>
    public int Count => _words.Count;

    /// <summary>How many words it has seen.</summary>
    public long Seen { get; private set; }

    /// <summary>How often the word was seen (any case).</summary>
    public int Frequency(string word) => _words.GetValueOrDefault(word.ToLowerInvariant());

    /// <summary>How many different pairs of words it knows.</summary>
    public int PairCount => _pairs.Count;

    /// <summary>How often the two words stood next to each other in this order (any case).</summary>
    public int Pair(string first, string second) =>
        first.Length == 0 || second.Length == 0 ? 0 : _pairs.GetValueOrDefault(first.ToLowerInvariant() + " " + second.ToLowerInvariant());

    /// <summary>The words of a text (subtitles: tags left out) and the pairs of words next to each other in a sentence.</summary>
    public void Learn(string text)
    {
        foreach (var sentence in Sentences(text))
        {
            string? before = null;
            foreach (var word in sentence)
            {
                Add(word, 1);
                if (before is not null)
                {
                    AddPair(before, word, 1);
                }

                before = word;
            }
        }
    }

    /// <summary>Two words seen next to each other <paramref name="times"/> times.</summary>
    public void AddPair(string first, string second, int times)
    {
        var a = first.ToLowerInvariant();
        var b = second.ToLowerInvariant();
        if (times > 0 && a.Length > 0 && b.Length > 0 && a.All(char.IsLetter) && b.All(char.IsLetter))
        {
            var key = a + " " + b;
            _pairs[key] = _pairs.GetValueOrDefault(key) + times;
        }
    }

    /// <summary>
    /// The sentences of a text as runs of words: a sentence – and with it a pair – ends at . ! ? … : ; a dash, a line of
    /// its own (a new speaker) or a tag; a comma or quotes do not end it.
    /// </summary>
    public static IEnumerable<List<string>> Sentences(string text)
    {
        foreach (var line in SubtitleCleanup.WithoutTags(text).Split('\n'))
        {
            var sentence = new List<string>();
            var start = -1;
            for (var i = 0; i <= line.Length; i++)
            {
                var c = i < line.Length ? line[i] : '.';
                if (char.IsLetter(c))
                {
                    if (start < 0)
                    {
                        start = i;
                    }

                    continue;
                }

                if (start >= 0)
                {
                    sentence.Add(line[start..i]);
                    start = -1;
                }

                // an apostrophe inside a word ("don't", "ď" written as d') does not split the pair
                if (c is '.' or '!' or '?' or '…' or ':' or ';' or '–' or '—' or '(' or ')' || (c == '-' && (i == 0 || line[i - 1] == ' ')) || char.IsDigit(c))
                {
                    if (sentence.Count > 0)
                    {
                        yield return sentence;
                    }

                    sentence = [];
                }
            }

            if (sentence.Count > 0)
            {
                yield return sentence;
            }
        }
    }

    /// <summary>A word seen <paramref name="times"/> times.</summary>
    public void Add(string word, int times)
    {
        var lower = word.ToLowerInvariant();
        if (lower.Length == 0 || times <= 0 || !lower.All(char.IsLetter))
        {
            return;
        }

        _words[lower] = _words.GetValueOrDefault(lower) + times;
        Seen += times;
        var padded = Start + lower + End;
        for (var i = 0; i < padded.Length; i++)
        {
            Gram(padded.Substring(i, 1), times);
            if (i + 2 <= padded.Length)
            {
                Gram(padded.Substring(i, 2), times);
            }

            if (i + 3 <= padded.Length)
            {
                Gram(padded.Substring(i, 3), times);
            }
        }

        _letters += (long)lower.Length * times;
    }

    private void Gram(string gram, long times) => _grams[gram] = _grams.GetValueOrDefault(gram) + times;

    /// <summary>
    /// How much the word looks like a word of the language: the mean natural logarithm of the probability of each of its
    /// letters (and its end) after the two before it – triples, backed off to pairs and single letters (interpolated).
    /// About −1 to −2 for a usual word, −4 and less for letters that never follow each other; 0 with nothing learned.
    /// </summary>
    public double Likeness(string word)
    {
        if (_letters == 0)
        {
            return 0;
        }

        var padded = Start + word.ToLowerInvariant() + End;
        double sum = 0;
        for (var i = 1; i < padded.Length; i++)
        {
            var letter = padded.Substring(i, 1);
            var single = (_grams.GetValueOrDefault(letter) + 1.0) / (_letters + Seen + 64.0);
            var pair = Conditional(padded.Substring(i - 1, 2), padded.Substring(i - 1, 1), single);
            var triple = i >= 2 ? Conditional(padded.Substring(i - 2, 3), padded.Substring(i - 2, 2), pair) : pair;
            sum += Math.Log(triple);
        }

        return sum / (padded.Length - 1);
    }

    // P(last letter | the ones before), mixed with the shorter estimate by how often the beginning was seen
    private double Conditional(string gram, string context, double shorter)
    {
        const double weight = 3;
        var seen = _grams.GetValueOrDefault(context);
        return (_grams.GetValueOrDefault(gram) + (weight * shorter)) / (seen + weight);
    }

    /// <summary>The words of a text: runs of letters (subtitle tags and markup left out).</summary>
    public static IEnumerable<string> Words(string text)
    {
        var plain = SubtitleCleanup.WithoutTags(text);
        var start = -1;
        for (var i = 0; i <= plain.Length; i++)
        {
            var letter = i < plain.Length && char.IsLetter(plain[i]);
            if (letter && start < 0)
            {
                start = i;
            }
            else if (!letter && start >= 0)
            {
                yield return plain[start..i];
                start = -1;
            }
        }
    }

    /// <summary>The lexicon kept in a file (missing or damaged: empty).</summary>
    public static OcrLexicon Load(string file)
    {
        var lexicon = new OcrLexicon();
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(file)) is { } words)
            {
                // a pair is kept as its two words with a space (a lexicon of an older UNIVERSUM has none – it learns them anew)
                foreach (var (word, times) in words)
                {
                    if (word.IndexOf(' ', StringComparison.Ordinal) is var space and > 0)
                    {
                        lexicon.AddPair(word[..space], word[(space + 1)..], times);
                    }
                    else
                    {
                        lexicon.Add(word, times);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // a damaged lexicon is learned anew
        }

        return lexicon;
    }

    /// <summary>
    /// Saves the words and the pairs (the rarest left out above <see cref="MaximumWords"/> and <see cref="MaximumPairs"/>);
    /// the triples are counted again on loading.
    /// </summary>
    public void Save(string file)
    {
        var kept = (_words.Count <= MaximumWords ? _words : _words.OrderByDescending(w => w.Value).Take(MaximumWords))
            .Concat(_pairs.Count <= MaximumPairs ? _pairs : _pairs.OrderByDescending(p => p.Value).Take(MaximumPairs))
            .ToDictionary(w => w.Key, w => w.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + ".part";
        File.WriteAllText(temporary, JsonSerializer.Serialize(kept));
        File.Move(temporary, file, overwrite: true);
    }
}
