using System.Text.RegularExpressions;

namespace Universum.Core.Text;

/// <summary>A piece of a note: plain text, or a web address (http or https only) with the text shown for it.</summary>
public sealed record TextPiece(string Text, Uri? Link = null, bool Named = false);

/// <summary>A link of a note: the word shown and the address hidden in it.</summary>
public sealed record NamedLink(string Name, Uri Address);

/// <summary>
/// A note as the user sees it (owner, 29. 10. 2026): its text without any addresses, and its links as words – the
/// address only hides in the word. Kept in one string: the text, then each link as "[word](address)".
/// </summary>
public sealed record NoteParts(string Text, IReadOnlyList<NamedLink> Links)
{
    /// <summary>The text and the links of a note (a bare address becomes a link named by its site).</summary>
    public static NoteParts Parse(string? note)
    {
        var text = new System.Text.StringBuilder();
        var links = new List<NamedLink>();
        foreach (var piece in WebLinks.Split(note))
        {
            if (piece.Link is { } address)
            {
                var name = piece.Named ? piece.Text : WebLinks.SuggestName(address);
                if (links.All(l => l.Address != address))
                {
                    links.Add(new NamedLink(name, address));
                }
            }
            else
            {
                text.Append(piece.Text);
            }
        }

        return new NoteParts(string.Join(' ', text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), links);
    }

    /// <summary>The note to keep: the text, then the links.</summary>
    public string Compose() =>
        string.Join(' ', new[] { Text.Trim() }.Concat(Links.Select(l => WebLinks.Named(l.Name, l.Address))).Where(t => t.Length > 0));

    public NoteParts WithText(string? text) => this with { Text = text ?? "" };

    /// <summary>Adds a link (one with the same address is replaced, keeping its place).</summary>
    public NoteParts WithLink(NamedLink link) =>
        this with
        {
            Links = Links.Any(l => l.Address == link.Address) ? [.. Links.Select(l => l.Address == link.Address ? link : l)] : [.. Links, link],
        };

    public NoteParts WithoutLink(Uri address) => this with { Links = [.. Links.Where(l => l.Address != address)] };
}

/// <summary>
/// The web addresses in a note (owner, 29. 10. 2026: an address in Poznámka becomes a link that opens the page). A link
/// can have a short name – written "[name](address)" – which is shown instead of the long address. Only http and https
/// addresses become links; a full stop, comma or bracket right after a bare address is not part of it.
/// </summary>
public static partial class WebLinks
{
    /// <summary>The longest text shown for an address without a name (the rest is "…"; the whole address is in the tooltip).</summary>
    public const int ShownLength = 48;

    /// <summary>The longest name of a link.</summary>
    public const int MaxNameLength = 80;

    public static bool HasLinks(string? text) => !string.IsNullOrEmpty(text) && Split(text).Any(p => p.Link is not null);

    /// <summary>The note in pieces: text and links, in order.</summary>
    public static IReadOnlyList<TextPiece> Split(string? text)
    {
        var pieces = new List<TextPiece>();
        if (string.IsNullOrEmpty(text))
        {
            return pieces;
        }

        var at = 0;
        foreach (Match match in Link().Matches(text))
        {
            var named = match.Groups["name"].Success;
            var value = named ? match.Groups["url"].Value : match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '"', '\'', '»', '“');
            if (ToUri(value) is not { } uri)
            {
                continue;
            }

            if (match.Index > at)
            {
                pieces.Add(new TextPiece(text[at..match.Index]));
            }

            pieces.Add(named ? new TextPiece(match.Groups["name"].Value.Trim(), uri, Named: true) : new TextPiece(Shown(value), uri));
            at = match.Index + (named ? match.Length : value.Length);
        }

        if (at < text.Length)
        {
            pieces.Add(new TextPiece(text[at..]));
        }

        return pieces;
    }

    /// <summary>The address when the text is one web address (pasted into a note), else null.</summary>
    public static Uri? AsAddress(string? text) =>
        text?.Trim() is { Length: > 0 } trimmed && !trimmed.Any(char.IsWhiteSpace) && Bare().Match(trimmed) is { Success: true, Index: 0 } match && match.Length == trimmed.Length
            ? ToUri(trimmed)
            : null;

    /// <summary>A short name suggested for an address: the site without "www." ("serialzone.cz").</summary>
    public static string SuggestName(Uri address) =>
        address.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? address.Host[4..] : address.Host;

    /// <summary>A link with its name as the note keeps it ("[name](address)"); without a name the bare address.</summary>
    public static string Named(string? name, Uri address)
    {
        var clean = new string((name ?? "").Where(c => c is not ('[' or ']' or '\r' or '\n')).ToArray()).Trim();
        if (clean.Length > MaxNameLength)
        {
            clean = clean[..MaxNameLength].TrimEnd();
        }

        return clean.Length == 0 ? address.AbsoluteUri : $"[{clean}]({address.AbsoluteUri})";
    }

    private static Uri? ToUri(string value) =>
        Uri.TryCreate(value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + value : value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0
            ? uri
            : null;

    // without the scheme and "www.", shortened
    private static string Shown(string address)
    {
        var shown = Scheme().Replace(address, "");
        return shown.Length <= ShownLength ? shown.TrimEnd('/') : shown[..(ShownLength - 1)] + "…";
    }

    [GeneratedRegex(@"\[(?<name>[^\[\]\r\n]{1,80})\]\((?<url>(?:https?://|www\.)[^\s()<>""]+)\)|(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bare();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();
}
