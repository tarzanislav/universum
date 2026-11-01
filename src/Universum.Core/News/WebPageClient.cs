using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Universum.Core.Library;
using Universum.Core.Text;

namespace Universum.Core.News;

/// <summary>A source of news could not be read; the message is ready to show.</summary>
public sealed class NewsSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads public web pages for the news, politely and safely: only HTTPS, only from the host of the address (a redirect
/// elsewhere is refused), no cookies or sign-in (the HttpClient is made without them), the site's robots.txt is
/// respected, at most one page every two seconds from one site, at most <see cref="MaxBytes"/> per page; the page is
/// only parsed as text, nothing of it runs.
/// </summary>
public sealed partial class WebPageClient(HttpClient http)
{
    /// <summary>The largest page read.</summary>
    public const int MaxBytes = 5 << 20;

    /// <summary>How UNIVERSUM introduces itself to the sites it reads.</summary>
    public static string UserAgent { get; } = "UNIVERSUM/" + (typeof(WebPageClient).Assembly.GetName().Version?.ToString(3) ?? "0") + " (private use)";

    /// <summary>The spacing of pages from one site.</summary>
    public static double PagesPerSecond { get; set; } = 0.5;

    private readonly Dictionary<string, RequestPacer> _pacers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RobotsRules> _robots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A page's text; <see cref="NewsSourceException"/> when it may not or cannot be read.</summary>
    public async Task<string> GetAsync(Uri address, CancellationToken cancellationToken = default)
    {
        if (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length > 0)
        {
            throw new NewsSourceException(CoreText.T("Zdroj sa číta len cez https://."));
        }

        var robots = await RobotsAsync(address, cancellationToken).ConfigureAwait(false);
        if (!robots.Allows(address.PathAndQuery))
        {
            throw new NewsSourceException(CoreText.F("Stránka {0} strojové čítanie tejto adresy nedovoľuje (robots.txt) – zdroj sa nečíta.", address.Host));
        }

        var (status, body, _) = await FetchAsync(address, cancellationToken).ConfigureAwait(false);
        return status switch
        {
            HttpStatusCode.OK => body,
            HttpStatusCode.TooManyRequests => throw new NewsSourceException(CoreText.F("Stránka {0} žiada spomaliť – skúste to neskôr.", address.Host)),
            HttpStatusCode.Forbidden => throw new NewsSourceException(CoreText.F("Stránka {0} prístup odmietla (403).", address.Host)),
            _ => throw new NewsSourceException(CoreText.F("Stránka {0} odpovedala chybou {1}.", address.Host, (int)status)),
        };
    }

    private async Task<RobotsRules> RobotsAsync(Uri address, CancellationToken cancellationToken)
    {
        lock (_robots)
        {
            if (_robots.TryGetValue(address.Host, out var known))
            {
                return known;
            }
        }

        RobotsRules rules;
        try
        {
            var (status, body, _) = await FetchAsync(new Uri(address, "/robots.txt"), cancellationToken).ConfigureAwait(false);
            // no robots.txt (4xx) = everything allowed; a server problem = nothing until the next run
            rules = status == HttpStatusCode.OK ? RobotsRules.Parse(body, "universum")
                : (int)status is >= 400 and < 500 ? RobotsRules.AllowAll
                : RobotsRules.DisallowAll;
        }
        catch (NewsSourceException)
        {
            rules = RobotsRules.DisallowAll;
        }

        lock (_robots)
        {
            _robots[address.Host] = rules;
        }

        return rules;
    }

    private async Task<(HttpStatusCode Status, string Body, Uri Final)> FetchAsync(Uri address, CancellationToken cancellationToken)
    {
        RequestPacer pacer;
        lock (_pacers)
        {
            if (!_pacers.TryGetValue(address.Host, out pacer!))
            {
                _pacers[address.Host] = pacer = new RequestPacer(PagesPerSecond);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml", 0.9));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml", 0.9));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.8));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.8));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("cs"));
        HttpResponseMessage response;
        try
        {
            response = await pacer.RunAsync(() => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new NewsSourceException(CoreText.F("Stránka {0} nie je dostupná: {1}", address.Host, ex.Message), ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NewsSourceException(CoreText.F("Stránka {0} neodpovedá.", address.Host), ex);
        }

        using (response)
        {
            var final = response.RequestMessage?.RequestUri ?? address;
            if (final.Scheme != Uri.UriSchemeHttps || !string.Equals(final.Host, address.Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new NewsSourceException(CoreText.F("Stránka {0} presmerovala na inú adresu ({1}) – nepoužila sa.", address.Host, final.Host));
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new NewsSourceException(CoreText.F("Stránka {0} je neprimerane veľká – nepoužila sa.", address.Host));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    throw new NewsSourceException(CoreText.F("Stránka {0} je neprimerane veľká – nepoužila sa.", address.Host));
                }

                buffer.Write(chunk, 0, read);
            }

            return (response.StatusCode, Decode(buffer.ToArray(), response.Content.Headers.ContentType?.CharSet), final);
        }
    }

    /// <summary>The text of a page in the encoding its header or its meta tag names (UTF-8 otherwise).</summary>
    public static string Decode(byte[] bytes, string? charset)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var name = charset;
        if (string.IsNullOrWhiteSpace(name))
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            name = MetaCharset().Match(head) is { Success: true } meta ? meta.Groups[1].Value : null;
        }

        Encoding encoding;
        try
        {
            encoding = name is null ? Encoding.UTF8 : Encoding.GetEncoding(name.Trim('"', '\'', ' '));
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        var text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    [GeneratedRegex("""<meta[^>]+charset\s*=\s*["']?([A-Za-z0-9_\-]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharset();
}

/// <summary>
/// The rules of a site's robots.txt for UNIVERSUM: the group of its own name, else the group for all ("*"); the longest
/// matching Allow or Disallow decides (Allow on a tie), "*" and "$" in paths are understood.
/// </summary>
public sealed class RobotsRules
{
    private readonly List<(bool Allow, string Pattern)> _rules;

    private RobotsRules(List<(bool, string)> rules) => _rules = rules;

    public static RobotsRules AllowAll { get; } = new([]);

    public static RobotsRules DisallowAll { get; } = new([(false, "/")]);

    public static RobotsRules Parse(string text, string agent)
    {
        var groups = new List<(List<string> Agents, List<(bool, string)> Rules)>();
        List<string>? agents = null;
        List<(bool, string)>? rules = null;
        var lastWasAgent = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "user-agent")
            {
                if (!lastWasAgent || agents is null)
                {
                    agents = [];
                    rules = [];
                    groups.Add((agents, rules));
                }

                agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
            }
            else if (key is "allow" or "disallow" && rules is not null)
            {
                lastWasAgent = false;
                if (value.Length > 0)
                {
                    rules.Add((key == "allow", value));
                }
            }
            else
            {
                lastWasAgent = false;
            }
        }

        var own = groups.Where(g => g.Agents.Any(a => a != "*" && agent.Contains(a, StringComparison.OrdinalIgnoreCase))).SelectMany(g => g.Rules).ToList();
        return new RobotsRules(own.Count > 0 || groups.Any(g => g.Agents.Any(a => a != "*" && agent.Contains(a, StringComparison.OrdinalIgnoreCase)))
            ? own
            : groups.Where(g => g.Agents.Contains("*")).SelectMany(g => g.Rules).ToList());
    }

    /// <summary>True when the path (with its query) may be read.</summary>
    public bool Allows(string pathAndQuery)
    {
        var best = -1;
        var allowed = true;
        foreach (var (allow, pattern) in _rules)
        {
            if (Matches(pattern, pathAndQuery) && (pattern.Length > best || (pattern.Length == best && allow)))
            {
                best = pattern.Length;
                allowed = allow;
            }
        }

        return allowed;
    }

    private static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        var regex = "^" + string.Join(".*", (anchored ? pattern[..^1] : pattern).Split('*').Select(Regex.Escape)) + (anchored ? "$" : "");
        return Regex.IsMatch(path, regex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
