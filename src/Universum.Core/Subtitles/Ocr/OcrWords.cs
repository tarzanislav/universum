using System.Text;
using System.Text.Json;

namespace Universum.Core.Subtitles.Ocr;

/// <summary>
/// Words mended by their context – letters that look the same in many fonts: "I" and "l" (a capital I inside a word of
/// small letters is an l), "O" and "0" (in a number 0, in a word O or o), "l"/"I" in a number 1; two marks into quotation
/// marks; in Czech and Slovak the apostrophe-like caron after l, t, d, L into ľ, ť, ď, Ľ.
/// </summary>
public static partial class OcrWords
{
    /// <summary>Capitals whose small letter has the same shape (only the size tells them apart).</summary>
    internal const string SameShape = "CJKOPSUVWXYZČŠŽÓÔÚÝŮŤ";

    public static string Mend(string line, string? language)
    {
        var slavic = language is "cs" or "sk" or "cze" or "ces" or "slo" or "slk";
        var text = line.Replace("''", slavic ? "“" : "\"", StringComparison.Ordinal)
            .Replace("’’", slavic ? "“" : "\"", StringComparison.Ordinal)
            .Replace(",,", "„", StringComparison.Ordinal)
            .Replace("..." , "…", StringComparison.Ordinal);
        var builder = new StringBuilder(text.Length);
        foreach (var word in text.Split(' '))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Word(word, slavic));
        }

        // no space before punctuation (a dot after a letter with a wide bearing looks like one)
        return Punctuation().Replace(builder.ToString(), "$1").Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@" ([.,!?:;)])(?=\s|$|[.,!?:;)“\""])", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Punctuation();

    /// <summary>The text with the first <paramref name="word"/> standing as a whole word replaced; null when it is not there.</summary>
    public static string? ReplaceWord(string text, string word, string replacement)
    {
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0 && word.Length > 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            var end = at + word.Length;
            if ((at == 0 || !char.IsLetter(text[at - 1])) && (end == text.Length || !char.IsLetter(text[end])))
            {
                return text[..at] + replacement + text[end..];
            }
        }

        return null;
    }

    /// <summary>One word mended by its context (numbers, I/l, case, the carons of ď ť ľ Ľ).</summary>
    internal static string MendWord(string word, bool slavic) => Word(word, slavic);

    private static string Word(string word, bool slavic)
    {
        if (word.Length == 0)
        {
            return word;
        }

        var chars = word.ToCharArray();
        var digits = chars.Count(char.IsAsciiDigit);
        var letters = chars.Count(char.IsLetter);
        for (var i = 0; i < chars.Length; i++)
        {
            var before = i > 0 ? chars[i - 1] : '\0';
            var after = i + 1 < chars.Length ? chars[i + 1] : '\0';
            switch (chars[i])
            {
                // in a number: 0 and 1
                case 'O' or 'o' when digits > 0 && digits >= letters - 1 && (char.IsAsciiDigit(before) || char.IsAsciiDigit(after)):
                    chars[i] = '0';
                    break;
                case 'l' or 'I' or '|' when digits > 0 && digits >= letters - 1 && (char.IsAsciiDigit(before) || char.IsAsciiDigit(after)):
                    chars[i] = '1';
                    break;

                // in a word: a capital I between small letters is an l; a 0 among letters an o (O at the start of a word)
                case 'I' or '|' when i > 0 && ((char.IsLower(before) && (after == '\0' || char.IsLower(after) || !char.IsLetter(after))) ||
                                               (char.IsLetter(before) && char.IsLower(after))):
                    chars[i] = 'l';
                    break;

                // a capital among small letters whose small one has its shape (C c, J j, O o, S s …)
                case var upper when i > 0 && char.IsLower(before) && (after == '\0' || char.IsLower(after)) && SameShape.Contains(upper):
                    chars[i] = char.ToLowerInvariant(upper);
                    break;
                case '0' when letters > digits && (char.IsLetter(before) || char.IsLetter(after)):
                    chars[i] = i == 0 || char.IsUpper(before) ? 'O' : 'o';
                    break;

                // Czech and Slovak: l' t' d' L' written with the caron beside the letter
                case '\'' or '’' when slavic && i > 0 && before is 'l' or 't' or 'd' or 'L' && (after == '\0' || char.IsLetter(after)):
                    chars[i - 1] = before switch { 'l' => 'ľ', 't' => 'ť', 'd' => 'ď', _ => 'Ľ' };
                    chars[i] = '\0';
                    break;
            }
        }

        return new string(chars.Where(c => c != '\0').ToArray());
    }
}

/// <summary>
/// The letters the user told (a shape no template knew), kept for other films in UNIVERSUM's folder – only shapes and
/// their texts, nothing of the films.
/// </summary>
public sealed class OcrMemory
{
    private readonly string? _file;
    private readonly List<OcrTemplate> _told = [];

    public OcrMemory(string? file)
    {
        _file = file;
        try
        {
            if (file is not null && File.Exists(file) && JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(file)) is { } stored)
            {
                _told.AddRange(stored.Where(s => s.Text is not null).Select(s => new OcrTemplate(s.Text, false, s.Shape)));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // a damaged memory is started anew
        }
    }

    public int Count => _told.Count;

    /// <summary>The nearest letter told and its distance; null when none was told.</summary>
    public (string Text, float Distance)? Nearest(in GlyphShape shape)
    {
        (string, float)? best = null;
        foreach (var told in _told)
        {
            var distance = GlyphShape.Distance(shape, told.Shape);
            if (best is null || distance < best.Value.Item2)
            {
                best = (told.Text, distance);
            }
        }

        return best;
    }

    /// <summary>A shape told by the user (the newest wins over an older one of the same shape).</summary>
    public void Add(GlyphShape shape, string text)
    {
        _told.RemoveAll(t => GlyphShape.Distance(t.Shape, shape) < 0.03f);
        _told.Add(new OcrTemplate(text, false, shape));
        Save();
    }

    private void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            var temporary = _file + ".part";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_told.Select(t => new Stored(t.Text, t.Shape)).ToList()));
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept for this run only
        }
    }

    private sealed record Stored(string Text, GlyphShape Shape);
}
