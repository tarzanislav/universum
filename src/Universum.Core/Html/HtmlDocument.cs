using System.Net;
using System.Text;

namespace Universum.Core.Html;

/// <summary>A node of a parsed HTML page: an element or a run of text.</summary>
public abstract class HtmlNode
{
    public HtmlElement? Parent { get; internal set; }
}

/// <summary>Text of a page (entities already decoded).</summary>
public sealed class HtmlText(string text) : HtmlNode
{
    public string Text { get; } = text;
}

/// <summary>An element of a parsed HTML page; its name is in lower case.</summary>
public sealed class HtmlElement : HtmlNode
{
    private readonly Dictionary<string, string> _attributes;

    internal HtmlElement(string name, Dictionary<string, string> attributes)
    {
        Name = name;
        _attributes = attributes;
    }

    public string Name { get; }

    public List<HtmlNode> Children { get; } = [];

    public IReadOnlyDictionary<string, string> Attributes => _attributes;

    /// <summary>An attribute's value (names are in lower case); null when the element does not have it.</summary>
    public string? Attribute(string name) => _attributes.TryGetValue(name, out var value) ? value : null;

    public bool HasClass(string name) =>
        Attribute("class") is { } classes && classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(name, StringComparer.Ordinal);

    /// <summary>All text inside the element, white space collapsed (a line break counts as a space).</summary>
    public string TextContent => Collapse(Texts(this, own: false));

    /// <summary>Only the element's own text, without the text of the elements inside it.</summary>
    public string OwnText => Collapse(Texts(this, own: true));

    /// <summary>The elements inside this one in document order.</summary>
    public IEnumerable<HtmlElement> Descendants()
    {
        var stack = new Stack<(HtmlElement Element, int Index)>();
        stack.Push((this, 0));
        while (stack.Count > 0)
        {
            var (element, index) = stack.Pop();
            if (index >= element.Children.Count)
            {
                continue;
            }

            stack.Push((element, index + 1));
            if (element.Children[index] is HtmlElement child)
            {
                yield return child;
                stack.Push((child, 0));
            }
        }
    }

    /// <summary>The elements inside this one that match a selector, in document order.</summary>
    public IReadOnlyList<HtmlElement> QueryAll(string selector)
    {
        var parsed = HtmlSelector.Parse(selector);
        return Descendants().Where(e => parsed.Matches(e, this)).ToList();
    }

    /// <summary>The first element inside this one that matches a selector; null when none does.</summary>
    public HtmlElement? Query(string selector)
    {
        var parsed = HtmlSelector.Parse(selector);
        return Descendants().FirstOrDefault(e => parsed.Matches(e, this));
    }

    private static IEnumerable<string> Texts(HtmlElement element, bool own)
    {
        foreach (var child in element.Children)
        {
            switch (child)
            {
                case HtmlText text:
                    yield return text.Text;
                    break;
                case HtmlElement { Name: "br" }:
                    yield return " ";
                    break;
                case HtmlElement inner when !own:
                    foreach (var part in Texts(inner, own: false))
                    {
                        yield return part;
                    }

                    yield return inner.Name is "p" or "div" or "li" or "td" or "th" or "tr" ? " " : "";
                    break;
            }
        }
    }

    private static string Collapse(IEnumerable<string> parts)
    {
        var builder = new StringBuilder();
        var space = false;
        foreach (var part in parts)
        {
            foreach (var c in part)
            {
                if (char.IsWhiteSpace(c))
                {
                    space = builder.Length > 0;
                }
                else
                {
                    if (space)
                    {
                        builder.Append(' ');
                        space = false;
                    }

                    builder.Append(c);
                }
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// A small, tolerant HTML parser for reading lists from web pages (no scripts run, nothing is loaded): elements,
/// attributes and text with entities decoded; comments, scripts and styles are skipped; elements a browser closes by
/// itself (p, li, td, tr, option…) are closed the same way; an end tag without its start is ignored. Limits on the
/// size of the page, the depth and the number of nodes protect against hostile pages.
/// </summary>
public static class HtmlParser
{
    /// <summary>The largest page parsed (characters).</summary>
    public const int MaxLength = 8 << 20;

    private const int MaxDepth = 512;
    private const int MaxNodes = 400_000;

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal) { "script", "style", "textarea", "title", "noscript", "template" };

    private static readonly HashSet<string> ClosesParagraph = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "div", "dl", "fieldset", "figure", "footer", "form", "h1", "h2", "h3",
        "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul",
    };

    private static readonly Dictionary<string, string[]> ClosesOpen = new(StringComparer.Ordinal)
    {
        ["li"] = ["li"],
        ["dt"] = ["dt", "dd"],
        ["dd"] = ["dt", "dd"],
        ["option"] = ["option"],
        ["td"] = ["td", "th"],
        ["th"] = ["td", "th"],
        ["tr"] = ["td", "th", "tr"],
        ["tbody"] = ["td", "th", "tr", "thead", "tbody"],
    };

    /// <summary>The page as a tree under a root element named "#document".</summary>
    public static HtmlElement Parse(string html)
    {
        if (html.Length > MaxLength)
        {
            throw new FormatException("Stránka je príliš veľká.");
        }

        var root = new HtmlElement("#document", []);
        var open = new List<HtmlElement> { root };
        var nodes = 0;
        var i = 0;
        var text = new StringBuilder();

        void Flush()
        {
            if (text.Length > 0)
            {
                Add(new HtmlText(WebUtility.HtmlDecode(text.ToString())));
                text.Clear();
            }
        }

        void Add(HtmlNode node)
        {
            if (++nodes > MaxNodes)
            {
                return;
            }

            var parent = open[^1];
            node.Parent = parent;
            parent.Children.Add(node);
        }

        while (i < html.Length && nodes <= MaxNodes)
        {
            var c = html[i];
            if (c != '<' || i + 1 >= html.Length)
            {
                text.Append(c);
                i++;
                continue;
            }

            var next = html[i + 1];
            if (next == '!' || next == '?')
            {
                Flush();
                var end = string.CompareOrdinal(html, i, "<!--", 0, 4) == 0 ? IndexOf(html, "-->", i + 4, 3) : IndexOf(html, ">", i + 2, 1);
                i = end;
                continue;
            }

            if (next == '/')
            {
                var nameEnd = i + 2;
                while (nameEnd < html.Length && IsNameChar(html[nameEnd]))
                {
                    nameEnd++;
                }

                if (nameEnd == i + 2)
                {
                    text.Append(c);
                    i++;
                    continue;
                }

                Flush();
                var name = html[(i + 2)..nameEnd].ToLowerInvariant();
                i = IndexOf(html, ">", nameEnd, 1);
                var at = open.FindLastIndex(e => e.Name == name);
                if (at > 0)
                {
                    open.RemoveRange(at, open.Count - at);
                }

                continue;
            }

            if (!char.IsAsciiLetter(next))
            {
                text.Append(c);
                i++;
                continue;
            }

            Flush();
            var (element, selfClosing, after) = ReadStartTag(html, i + 1);
            i = after;
            if (ClosesParagraph.Contains(element.Name) && open[^1].Name == "p")
            {
                open.RemoveAt(open.Count - 1);
            }

            if (ClosesOpen.TryGetValue(element.Name, out var closes))
            {
                while (open.Count > 1 && closes.Contains(open[^1].Name))
                {
                    open.RemoveAt(open.Count - 1);
                }
            }

            Add(element);
            if (RawText.Contains(element.Name) && !selfClosing)
            {
                // the content is text up to the end tag (scripts and styles are left out)
                var close = html.IndexOf("</" + element.Name, i, StringComparison.OrdinalIgnoreCase);
                var content = close < 0 ? html[i..] : html[i..close];
                if (element.Name is "textarea" or "title")
                {
                    element.Children.Add(new HtmlText(WebUtility.HtmlDecode(content)) { Parent = element });
                }

                i = close < 0 ? html.Length : IndexOf(html, ">", close, 1);
                continue;
            }

            if (!selfClosing && !Void.Contains(element.Name) && open.Count < MaxDepth)
            {
                open.Add(element);
            }
        }

        Flush();
        return root;
    }

    // the index after the end of a marker (or the end of the page)
    private static int IndexOf(string html, string marker, int from, int length)
    {
        var at = html.IndexOf(marker, Math.Min(from, html.Length), StringComparison.Ordinal);
        return at < 0 ? html.Length : at + length;
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or ':' or '_';

    private static (HtmlElement Element, bool SelfClosing, int After) ReadStartTag(string html, int start)
    {
        var i = start;
        while (i < html.Length && IsNameChar(html[i]))
        {
            i++;
        }

        var name = html[start..i].ToLowerInvariant();
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var selfClosing = false;
        while (i < html.Length)
        {
            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            if (i >= html.Length)
            {
                break;
            }

            if (html[i] == '>')
            {
                i++;
                break;
            }

            if (html[i] == '/')
            {
                selfClosing = i + 1 < html.Length && html[i + 1] == '>';
                i++;
                continue;
            }

            var nameStart = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not '=' and not '>' and not '/')
            {
                i++;
            }

            var attribute = html[nameStart..i].ToLowerInvariant();
            if (attribute.Length == 0)
            {
                i++;
                continue;
            }

            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            var value = "";
            if (i < html.Length && html[i] == '=')
            {
                i++;
                while (i < html.Length && char.IsWhiteSpace(html[i]))
                {
                    i++;
                }

                if (i < html.Length && html[i] is '"' or '\'')
                {
                    var quote = html[i];
                    var end = html.IndexOf(quote, i + 1);
                    end = end < 0 ? html.Length : end;
                    value = html[(i + 1)..end];
                    i = Math.Min(end + 1, html.Length);
                }
                else
                {
                    var valueStart = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                    {
                        i++;
                    }

                    value = html[valueStart..i];
                }
            }

            attributes.TryAdd(attribute, WebUtility.HtmlDecode(value));
        }

        return (new HtmlElement(name, attributes), selfClosing, i);
    }
}
