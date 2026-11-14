using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Universum.Tests;

/// <summary>
/// The translations of the window (Czech and English, keyed by the Slovak text): every text the window and the code show
/// – <c>{loc:T '…'}</c> in XAML, <c>Loc.T/F("…")</c> and <c>CoreText.T/F("…")</c> in the code – has both translations,
/// no key is there twice (the later one would silently win), and a translation keeps the values {0}, {1} … of its key
/// (otherwise the text could not be filled in). Read from the sources, so the check runs on Linux too.
/// </summary>
public sealed partial class TranslationTests
{
    private static string Source => Path.Combine(TestData.Root, "..", "src");

    [Test]
    public void EveryTextOfTheWindowIsTranslated()
    {
        var table = File.ReadAllText(Path.Combine(Source, "Universum.App", "Localization", "Translations.cs"));
        var split = table.IndexOf("IReadOnlyDictionary<string, string> English", StringComparison.Ordinal);
        Assert.True(split > 0, "the English table found");
        var czech = Entries(table[..split]);
        var english = Entries(table[split..]);
        Assert.True(czech.Count > 1000 && english.Count > 1000, $"the tables read ({czech.Count}, {english.Count})");

        var problems = new List<string>();
        foreach (var (language, entries) in new[] { ("cs", czech), ("en", english) })
        {
            problems.AddRange(entries.GroupBy(e => e.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"{language}: twice – {g.Key}"));
            problems.AddRange(entries.Where(e => !Values(e.Key).SetEquals(Values(e.Value))).Select(e => $"{language}: other values – {e.Key}"));
        }

        var cs = czech.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var en = english.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var used = Used();
        Assert.True(used.Count > 1000, "the texts of the window read – " + used.Count);
        problems.AddRange(used.Where(t => !cs.Contains(t)).Select(t => "cs: missing – " + t));
        problems.AddRange(used.Where(t => !en.Contains(t)).Select(t => "en: missing – " + t));
        Assert.True(problems.Count == 0, $"{problems.Count} problems of the translations:\n" + string.Join('\n', problems.Order(StringComparer.Ordinal).Take(40)));
    }

    // the texts shown: the XAML of the app, the code of the app and of the core (the self-test's invented texts left out)
    private static HashSet<string> Used()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Source, "Universum.App"), "*.xaml", SearchOption.AllDirectories).Where(Own))
        {
            foreach (Match match in XamlText().Matches(File.ReadAllText(file)))
            {
                // the XML entities, then the escapes of a markup extension ("\\" is "\")
                used.Add(MarkupEscape().Replace(WebUtility.HtmlDecode(match.Groups["text"].Value), "$1"));
            }
        }

        foreach (var file in Directory.EnumerateFiles(Source, "*.cs", SearchOption.AllDirectories).Where(Own))
        {
            var name = Path.GetFileName(file);
            if (name == "Translations.cs" || name.StartsWith("SelfTest", StringComparison.Ordinal))
            {
                continue;
            }

            var code = File.ReadAllText(file);
            foreach (Match match in CodeText().Matches(code))
            {
                // only a text written out (a sum of literals); a comment of documentation is no text shown
                var line = code.LastIndexOf('\n', match.Index) + 1;
                if (code.AsSpan(line, match.Index - line).TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Literals(code, match.Index + match.Length) is { } text)
                {
                    used.Add(text);
                }
            }
        }

        return used;
    }

    private static bool Own(string file) =>
        !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
        !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    // ["key"] = "value", in the order of the table
    private static List<(string Key, string Value)> Entries(string table)
    {
        var entries = new List<(string, string)>();
        foreach (Match match in EntryStart().Matches(table))
        {
            if (Literal(table, match.Index + match.Length) is not { } key ||
                EntryEquals().Match(table, key.End) is not { Success: true } equals ||
                Literal(table, equals.Index + equals.Length) is not { } value)
            {
                throw new InvalidOperationException("an entry of the translations that cannot be read: " + table.Substring(match.Index, Math.Min(120, table.Length - match.Index)));
            }

            entries.Add((key.Text, value.Text));
        }

        return entries;
    }

    // "a" + "b" … up to "," or ")"; null when the text is not written out (a value, an interpolated text)
    private static string? Literals(string code, int at)
    {
        var text = new StringBuilder();
        while (true)
        {
            if (Literal(code, at) is not { } part)
            {
                return null;
            }

            text.Append(part.Text);
            at = part.End;
            while (at < code.Length && char.IsWhiteSpace(code[at]))
            {
                at++;
            }

            if (at < code.Length && code[at] == '+')
            {
                at++;
                while (at < code.Length && char.IsWhiteSpace(code[at]))
                {
                    at++;
                }

                continue;
            }

            return at < code.Length && code[at] is ',' or ')' ? text.ToString() : null;
        }
    }

    // a regular C# string literal at the place; its text and where it ends
    private static (string Text, int End)? Literal(string code, int at)
    {
        if (at >= code.Length || code[at] != '"')
        {
            return null;
        }

        var text = new StringBuilder();
        for (var i = at + 1; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '"':
                    return (text.ToString(), i + 1);
                case '\\' when i + 1 < code.Length:
                    i++;
                    text.Append(code[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', var other => other });
                    break;
                case '\n':
                    return null;
                default:
                    text.Append(code[i]);
                    break;
            }
        }

        return null;
    }

    private static HashSet<string> Values(string text) => [.. FormatValue().Matches(text).Select(m => m.Groups[1].Value)];

    [GeneratedRegex(@"\{loc:T '(?<text>(?:[^'\\]|\\.)*)'\}", RegexOptions.CultureInvariant)]
    private static partial Regex XamlText();

    [GeneratedRegex(@"\\(.)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupEscape();

    [GeneratedRegex(@"\b(?:Loc|CoreText)\.[TF]\(\s*", RegexOptions.CultureInvariant)]
    private static partial Regex CodeText();

    [GeneratedRegex(@"^\s*\[(?="")", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EntryStart();

    [GeneratedRegex(@"\G\]\s*=\s*", RegexOptions.CultureInvariant)]
    private static partial Regex EntryEquals();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex FormatValue();
}
