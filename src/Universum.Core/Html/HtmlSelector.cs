namespace Universum.Core.Html;

/// <summary>
/// A CSS selector for reading lists from pages – the part of CSS such rules need: element names, "*", .class, #id,
/// [attr], [attr=value], [attr^=value], [attr*=value], [attr$=value], descendant (space) and child (&gt;) combinators,
/// and groups separated by commas. Anything else is refused (<see cref="FormatException"/>), so a rule never silently
/// matches something else.
/// </summary>
public sealed class HtmlSelector
{
    private readonly List<List<(Compound Part, char Combinator)>> _groups;

    private HtmlSelector(List<List<(Compound, char)>> groups) => _groups = groups;

    /// <summary>A selector from its text; <see cref="FormatException"/> when it uses something unsupported.</summary>
    public static HtmlSelector Parse(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector.Length > 500)
        {
            throw new FormatException("Prázdny alebo príliš dlhý selektor.");
        }

        var groups = new List<List<(Compound, char)>>();
        foreach (var group in selector.Split(','))
        {
            var parts = new List<(Compound, char)>();
            var i = 0;
            var text = group.Trim();
            var combinator = ' ';
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    i++;
                    continue;
                }

                if (text[i] == '>')
                {
                    if (parts.Count == 0)
                    {
                        throw new FormatException($"Selektor „{selector}“ nemôže začínať znakom >.");
                    }

                    combinator = '>';
                    i++;
                    continue;
                }

                var (compound, end) = Compound.Read(text, i, selector);
                parts.Add((compound, parts.Count == 0 ? ' ' : combinator));
                combinator = ' ';
                i = end;
            }

            if (parts.Count == 0 || combinator == '>')
            {
                throw new FormatException($"Neúplný selektor „{selector}“.");
            }

            groups.Add(parts);
        }

        return new HtmlSelector(groups);
    }

    /// <summary>True when the element matches (ancestors are looked for only below <paramref name="scope"/>).</summary>
    public bool Matches(HtmlElement element, HtmlElement? scope = null) => _groups.Any(g => Matches(g, g.Count - 1, element, scope));

    private static bool Matches(List<(Compound Part, char Combinator)> parts, int index, HtmlElement element, HtmlElement? scope)
    {
        if (!parts[index].Part.Matches(element))
        {
            return false;
        }

        if (index == 0)
        {
            return true;
        }

        var combinator = parts[index].Combinator;
        for (var ancestor = element.Parent; ancestor is not null && ancestor != scope && ancestor.Name != "#document"; ancestor = ancestor.Parent)
        {
            if (Matches(parts, index - 1, ancestor, scope))
            {
                return true;
            }

            if (combinator == '>')
            {
                return false;
            }
        }

        return false;
    }

    private sealed class Compound
    {
        private string? _name;
        private readonly List<string> _classes = [];
        private string? _id;
        private readonly List<(string Name, char Operator, string? Value)> _attributes = [];

        public bool Matches(HtmlElement element) =>
            (_name is null || _name == element.Name) &&
            (_id is null || element.Attribute("id") == _id) &&
            _classes.All(element.HasClass) &&
            _attributes.All(a => element.Attribute(a.Name) is { } value && a.Operator switch
            {
                '\0' => true,
                '=' => value == a.Value,
                '^' => value.StartsWith(a.Value!, StringComparison.Ordinal),
                '*' => value.Contains(a.Value!, StringComparison.Ordinal),
                '$' => value.EndsWith(a.Value!, StringComparison.Ordinal),
                _ => false,
            });

        public static (Compound Part, int End) Read(string text, int i, string selector)
        {
            var compound = new Compound();
            var any = false;
            if (i < text.Length && text[i] == '*')
            {
                i++;
                any = true;
            }
            else if (i < text.Length && IsIdentifier(text[i]))
            {
                var start = i;
                i = Identifier(text, i);
                compound._name = text[start..i].ToLowerInvariant();
                any = true;
            }

            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '>')
            {
                switch (text[i])
                {
                    case '.' or '#':
                        var kind = text[i];
                        var start = i + 1;
                        i = Identifier(text, start);
                        if (i == start)
                        {
                            throw new FormatException($"Chýba názov za „{kind}“ v selektore „{selector}“.");
                        }

                        if (kind == '.')
                        {
                            compound._classes.Add(text[start..i]);
                        }
                        else
                        {
                            compound._id = text[start..i];
                        }

                        break;
                    case '[':
                        var close = text.IndexOf(']', i);
                        if (close < 0)
                        {
                            throw new FormatException($"Chýba ] v selektore „{selector}“.");
                        }

                        compound._attributes.Add(Attribute(text[(i + 1)..close], selector));
                        i = close + 1;
                        break;
                    default:
                        throw new FormatException($"Selektor „{selector}“ obsahuje nepodporovanú časť pri „{text[i..]}“.");
                }

                any = true;
            }

            if (!any)
            {
                throw new FormatException($"Neplatný selektor „{selector}“.");
            }

            return (compound, i);
        }

        private static (string, char, string?) Attribute(string inside, string selector)
        {
            var at = inside.IndexOf('=');
            if (at < 0)
            {
                return (Check(inside.Trim(), selector), '\0', null);
            }

            var op = at > 0 && inside[at - 1] is '^' or '*' or '$' ? inside[at - 1] : '=';
            var name = inside[..(op == '=' ? at : at - 1)].Trim();
            var value = inside[(at + 1)..].Trim();
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            return (Check(name, selector), op, value);
        }

        private static string Check(string name, string selector) =>
            name.Length > 0 && IsIdentifier(name[0]) && name.All(c => IsIdentifier(c) || char.IsAsciiDigit(c)) ? name.ToLowerInvariant()
                : throw new FormatException($"Neplatný atribút v selektore „{selector}“.");

        private static bool IsIdentifier(char c) => char.IsAsciiLetter(c) || c is '_' or '-';

        private static int Identifier(string text, int i)
        {
            while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '-'))
            {
                i++;
            }

            return i;
        }
    }
}
