using System.Globalization;
using System.Text;

namespace Universum.Core.Text;

/// <summary>Word matching that ignores case and diacritics ("Vynútené" matches "vynutene").</summary>
public static class Words
{
    /// <summary>Lower-case words without diacritics, separated by single spaces.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>True when <paramref name="text"/> contains <paramref name="phrase"/> as whole word(s).</summary>
    public static bool ContainsPhrase(string? text, string phrase)
    {
        var normalizedPhrase = Normalize(phrase);
        return normalizedPhrase.Length > 0 && (" " + Normalize(text) + " ").Contains(" " + normalizedPhrase + " ", StringComparison.Ordinal);
    }

    public static bool ContainsAny(string? text, IEnumerable<string> phrases) => phrases.Any(p => ContainsPhrase(text, p));
}
