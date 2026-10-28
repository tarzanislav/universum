using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Universum.Core.Html;
using Universum.Core.Text;

namespace Universum.Core.News;

/// <summary>
/// A source of Zaujímavosti: a web page or its RSS/Atom feed, kept in the settings (the addresses of pages live only
/// there, never in the program), with the language its articles are written in.
/// </summary>
public sealed record FeedSource
{
    public required string Name { get; init; }

    /// <summary>The feed, or a page that names its feed (&lt;link rel="alternate"&gt;).</summary>
    public required string Url { get; init; }

    /// <summary>"sk", "cs", "en" …</summary>
    public string Language { get; init; } = "en";

    public bool Enabled { get; init; } = true;

    /// <summary>The address when it is a safe https address.</summary>
    [JsonIgnore]
    public Uri? Address => Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    /// <summary>The sources in a file the user imports (format "UNIVERSUM-zaujimavosti 1").</summary>
    public static IReadOnlyList<FeedSource> Load(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format) || format.GetString() != "UNIVERSUM-zaujimavosti 1" ||
                !root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("súbor nie je zoznam zdrojov Zaujímavostí (UNIVERSUM-zaujimavosti 1).");
            }

            var list = new List<FeedSource>();
            foreach (var item in sources.EnumerateArray())
            {
                var source = new FeedSource
                {
                    Name = WebText.Clean(item.TryGetProperty("name", out var n) ? n.GetString() : null, 80),
                    Url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    Language = item.TryGetProperty("language", out var l) && l.GetString() is { Length: 2 } language ? language.ToLowerInvariant() : "en",
                };
                if (source.Name.Length == 0 || source.Address is null)
                {
                    throw new FormatException($"zdroj „{source.Name}“ nemá meno alebo bezpečnú adresu https.");
                }

                list.Add(source);
            }

            return list;
        }
        catch (JsonException ex)
        {
            throw new FormatException("súbor sa nedá prečítať: " + ex.Message, ex);
        }
    }
}

/// <summary>A translation of an article's title and summary.</summary>
public sealed record FeedTranslation(string Title, string Summary);

/// <summary>An article of a source: its title, a short summary, the link to the whole article and its picture.</summary>
public sealed record FeedItem
{
    /// <summary>The address of the article (it also tells the same article apart).</summary>
    public required string Link { get; init; }

    public required string Source { get; init; }

    public required string Language { get; init; }

    public required string Title { get; init; }

    public string Summary { get; init; } = "";

    public string? Image { get; init; }

    public required DateTimeOffset Published { get; init; }

    /// <summary>Translations by language ("sk" …).</summary>
    public Dictionary<string, FeedTranslation> Translations { get; init; } = [];
}

/// <summary>
/// Reads RSS 2.0 and Atom feeds safely (no DTD, no external entities, limited size): the title, a short plain-text
/// summary (never the whole article), the link, the date and the picture of each article.
/// </summary>
public static partial class FeedParser
{
    /// <summary>The summary is cut to about this many characters, at a sentence or a word.</summary>
    public const int SummaryLength = 420;

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";

    public static IReadOnlyList<FeedItem> Parse(string xml, string source, string language)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿', ' ', '\r', '\n', '\t')), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = 8_000_000,
                IgnoreComments = true,
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("zdroj nie je čitateľný kanál RSS ani Atom: " + ex.Message, ex);
        }

        var root = document.Root ?? throw new FormatException("prázdny kanál.");
        var items = root.Name == Atom + "feed"
            ? root.Elements(Atom + "entry").Select(e => Entry(e, source, language))
            : root.Descendants().Where(e => e.Name.LocalName == "item").Select(e => Item(e, source, language));
        return items.OfType<FeedItem>().DistinctBy(i => i.Link).Take(200).ToList();
    }

    private static FeedItem? Item(XElement item, string source, string language)
    {
        var link = Value(item, "link") ?? Value(item, "guid");
        var description = Value(item, "description") ?? item.Element(Content + "encoded")?.Value;
        var image = item.Elements("enclosure").FirstOrDefault(e => ((string?)e.Attribute("type"))?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)?.Attribute("url")?.Value
                    ?? MediaImage(item) ?? FirstImage(item.Element(Content + "encoded")?.Value) ?? FirstImage(description);
        return Make(link, Value(item, "title"), description, image, Date(Value(item, "pubDate") ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "date")?.Value), source, language);
    }

    private static FeedItem? Entry(XElement entry, string source, string language)
    {
        var link = entry.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value;
        var summary = entry.Element(Atom + "summary")?.Value ?? entry.Element(Atom + "content")?.Value;
        var image = entry.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") == "enclosure" &&
                                                                     ((string?)l.Attribute("type"))?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)?.Attribute("href")?.Value
                    ?? MediaImage(entry) ?? FirstImage(entry.Element(Atom + "content")?.Value);
        return Make(link, entry.Element(Atom + "title")?.Value, summary, image,
            Date(entry.Element(Atom + "published")?.Value ?? entry.Element(Atom + "updated")?.Value), source, language);
    }

    private static FeedItem? Make(string? link, string? title, string? summary, string? image, DateTimeOffset? published, string source, string language)
    {
        if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http"))
        {
            return null;
        }

        var heading = WebText.Clean(PlainText(title), 300);
        if (heading.Length == 0)
        {
            return null;
        }

        return new FeedItem
        {
            Link = address.AbsoluteUri,
            Source = source,
            Language = language,
            Title = heading,
            Summary = Shorten(WebText.Clean(PlainText(summary), 4000)),
            Image = Uri.TryCreate(image?.Trim(), UriKind.Absolute, out var picture) && picture.Scheme == Uri.UriSchemeHttps ? picture.AbsoluteUri : null,
            Published = published ?? DateTimeOffset.Now,
        };
    }

    /// <summary>The text of an HTML snippet (feeds often put HTML into their summaries).</summary>
    public static string PlainText(string? html) =>
        string.IsNullOrWhiteSpace(html) ? "" : html.Contains('<') ? HtmlParser.Parse(html).TextContent : System.Net.WebUtility.HtmlDecode(html);

    /// <summary>A summary cut at the end of a sentence (else a word) near <see cref="SummaryLength"/>.</summary>
    public static string Shorten(string text)
    {
        if (text.Length <= SummaryLength)
        {
            return text;
        }

        var cut = text[..SummaryLength];
        var sentence = cut.LastIndexOfAny(['.', '!', '?']);
        if (sentence > SummaryLength / 2)
        {
            return cut[..(sentence + 1)];
        }

        var word = cut.LastIndexOf(' ');
        return (word > 0 ? cut[..word] : cut).TrimEnd(',', ';', ':', '-', '–') + " …";
    }

    /// <summary>The address of the feed a page names (&lt;link rel="alternate" type="application/rss+xml"&gt;), or null.</summary>
    public static Uri? Discover(string html, Uri page)
    {
        foreach (var link in HtmlParser.Parse(html).QueryAll("link[rel=alternate]"))
        {
            var type = link.Attribute("type") ?? "";
            if ((type.Contains("rss", StringComparison.OrdinalIgnoreCase) || type.Contains("atom", StringComparison.OrdinalIgnoreCase)) &&
                Uri.TryCreate(page, link.Attribute("href"), out var feed) && feed.Scheme == Uri.UriSchemeHttps)
            {
                return feed;
            }
        }

        return null;
    }

    /// <summary>
    /// The articles a page lists when it has no feed: each &lt;article&gt; (else each heading with a link) gives its
    /// title, link (on the same site), first picture and first paragraph; at most 40, the date is when first seen.
    /// </summary>
    public static IReadOnlyList<FeedItem> FromPage(string html, Uri page, string source, string language)
    {
        var root = HtmlParser.Parse(html);
        var blocks = root.QueryAll("article").ToList();
        if (blocks.Count == 0)
        {
            blocks = root.QueryAll("h2 a[href], h3 a[href]").Select(a => a.Parent?.Parent ?? a).ToList();
        }

        var items = new List<FeedItem>();
        foreach (var block in blocks)
        {
            var heading = block.Query("h1, h2, h3, h4") ?? block.Query("a[href]");
            var anchor = heading?.Name == "a" ? heading : heading?.Query("a[href]") ?? block.Query("a[href]");
            if (heading is null || anchor is null || !Uri.TryCreate(page, anchor.Attribute("href"), out var link) ||
                !string.Equals(link.Host, page.Host, StringComparison.OrdinalIgnoreCase) || heading.TextContent.Length < 12)
            {
                continue;
            }

            var picture = block.Query("img")?.Let(i => i.Attribute("data-src") ?? i.Attribute("src"));
            var image = picture is not null && Uri.TryCreate(page, picture, out var full) && full.Scheme == Uri.UriSchemeHttps ? full.AbsoluteUri : null;
            if (Make(link.AbsoluteUri, heading.TextContent, block.Query("p")?.TextContent, image, null, source, language) is { } item)
            {
                items.Add(item);
            }
        }

        return items.DistinctBy(i => i.Link).Take(40).ToList();
    }

    private static TResult Let<T, TResult>(this T value, Func<T, TResult> map) => map(value);

    /// <summary>True when the text looks like a feed rather than a page.</summary>
    public static bool IsFeed(string text)
    {
        var start = text.AsSpan().TrimStart();
        var head = start[..Math.Min(start.Length, 600)].ToString();
        return head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || head.Contains("<rss", StringComparison.OrdinalIgnoreCase) ||
               head.Contains("<feed", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Value(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name && e.Name.Namespace == XNamespace.None)?.Value;

    private static string? MediaImage(XElement element) =>
        element.Descendants(Media + "content").FirstOrDefault(m => (string?)m.Attribute("medium") is null or "image" &&
                                                                  ((string?)m.Attribute("type"))?.StartsWith("video", StringComparison.OrdinalIgnoreCase) != true)?.Attribute("url")?.Value
        ?? element.Descendants(Media + "thumbnail").FirstOrDefault()?.Attribute("url")?.Value;

    private static string? FirstImage(string? html) =>
        string.IsNullOrEmpty(html) || !html.Contains("<img", StringComparison.OrdinalIgnoreCase) ? null : HtmlParser.Parse(html).Query("img[src]")?.Attribute("src");

    private static DateTimeOffset? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var iso))
        {
            return iso;
        }

        // RFC 822 ("Wed, 28 Oct 2026 10:00:00 GMT", "… +0100", "… EST")
        var match = Rfc822().Match(text);
        var months = new[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
        var month = match.Success ? Array.IndexOf(months, match.Groups[2].Value.ToLowerInvariant()) + 1 : 0;
        if (month == 0)
        {
            return null;
        }

        var offset = match.Groups[7].Value switch
        {
            "" or "GMT" or "UT" or "UTC" or "Z" => TimeSpan.Zero,
            "EST" or "CDT" => TimeSpan.FromHours(-5),
            "EDT" => TimeSpan.FromHours(-4),
            "CST" or "MDT" => TimeSpan.FromHours(-6),
            "MST" or "PDT" => TimeSpan.FromHours(-7),
            "PST" => TimeSpan.FromHours(-8),
            "CET" => TimeSpan.FromHours(1),
            "CEST" => TimeSpan.FromHours(2),
            [var sign and ('+' or '-'), .. var digits] when digits.Length == 4 =>
                (sign == '-' ? -1 : 1) * new TimeSpan(int.Parse(digits[..2], CultureInfo.InvariantCulture), int.Parse(digits[2..], CultureInfo.InvariantCulture), 0),
            _ => TimeSpan.Zero,
        };
        var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        try
        {
            return new DateTimeOffset(year < 100 ? 2000 + year : year, month, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                match.Groups[6].Success ? int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture) : 0, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:[A-Za-z]{3},\s*)?(\d{1,2})\s+([A-Za-z]{3})\s+(\d{2,4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*([+-]\d{4}|[A-Z]{1,4})?$")]
    private static partial System.Text.RegularExpressions.Regex Rfc822();
}

