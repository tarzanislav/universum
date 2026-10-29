using System.Text.RegularExpressions;

namespace Universum.Core.Text;

/// <summary>A piece of a note: plain text, or a web address (http or https only) with a short text to show.</summary>
public sealed record TextPiece(string Text, Uri? Link = null);

/// <summary>
/// The web addresses in a note (owner, 29. 10. 2026: an address typed into Poznámka becomes a link that opens the page).
/// Only http and https addresses become links; a full stop, comma or bracket right after an address is not part of it.
/// </summary>
public static partial class WebLinks
{
    /// <summary>The longest text shown for an address (the rest is "…"; the whole address is in the tooltip).</summary>
    public const int ShownLength = 48;

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
        foreach (Match match in Address().Matches(text))
        {
            var value = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '"', '\'', '»', '“');
            if (!Uri.TryCreate(value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + value : value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0)
            {
                continue;
            }

            if (match.Index > at)
            {
                pieces.Add(new TextPiece(text[at..match.Index]));
            }

            pieces.Add(new TextPiece(Shown(value), uri));
            at = match.Index + value.Length;
        }

        if (at < text.Length)
        {
            pieces.Add(new TextPiece(text[at..]));
        }

        return pieces;
    }

    // without the scheme and "www.", shortened
    private static string Shown(string address)
    {
        var shown = Scheme().Replace(address, "");
        return shown.Length <= ShownLength ? shown.TrimEnd('/') : shown[..(ShownLength - 1)] + "…";
    }

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();
}
