using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Universum.Core.Html;
using Universum.Core.Text;

namespace Universum.Core.News;

/// <summary>What a section heading of a page means for the episodes under it ("Nové série" = season premieres).</summary>
public sealed record SectionRule(string Contains, EpisodeKind Kind);

/// <summary>
/// Where a value of an item is on the page: a selector inside the item (empty = the item itself) and what is taken –
/// "text" (all text), "own" (the element's own text, without the elements inside it) or "attr:NAME".
/// </summary>
public sealed record FieldRule(string Select, string Take = "text");

/// <summary>
/// The rules for reading a schedule of series episodes from a web page, one page per day. They are not part of the
/// program: the user imports them into the settings with the source (file format <see cref="FormatName"/>), so the
/// program only knows how to apply rules, not which pages exist. Fields: title, original, years, episode, channel,
/// channelId, genres, link.
/// </summary>
public sealed partial record PageRules
{
    /// <summary>The first line of a rules file.</summary>
    public const string FormatName = "UNIVERSUM-zdroj 1";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        WriteIndented = true,
    };

    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "title", "original", "years", "episode", "channel", "channelId", "genres", "link",
    };

    public string Format { get; init; } = FormatName;

    public required string Name { get; init; }

    /// <summary>The address of a day's page: {d} {dd} {M} {MM} {yyyy} are replaced by the day.</summary>
    public required string Address { get; init; }

    /// <summary>The selector of one episode on the page.</summary>
    public required string Item { get; init; }

    /// <summary>The selector of section headings; the last heading before an item tells its kind.</summary>
    public string? Section { get; init; }

    public IReadOnlyList<SectionRule> Sections { get; init; } = [];

    public required IReadOnlyDictionary<string, FieldRule> Fields { get; init; }

    /// <summary>Channel IDs or names of Czech and Slovak broadcasters and services (the rest is "svet").</summary>
    public IReadOnlyList<string> Domestic { get; init; } = [];

    public int DaysBack { get; init; } = 7;

    public int DaysAhead { get; init; } = 14;

    /// <summary>Reads rules from a file's text; <see cref="FormatException"/> with the reason when they cannot be used.</summary>
    public static PageRules Load(string json)
    {
        PageRules? rules;
        try
        {
            rules = JsonSerializer.Deserialize<PageRules>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Súbor pravidiel nie je platný JSON: " + ex.Message, ex);
        }

        if (rules is null)
        {
            throw new FormatException("Súbor pravidiel je prázdny.");
        }

        rules.Validate();
        return rules;
    }

    /// <summary>Checks the rules; <see cref="FormatException"/> says what is wrong.</summary>
    public void Validate()
    {
        if (Format != FormatName)
        {
            throw new FormatException($"Súbor nie je v tvare „{FormatName}“.");
        }

        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100)
        {
            throw new FormatException("Zdroj nemá názov.");
        }

        Uri? sample = null;
        try
        {
            sample = string.IsNullOrWhiteSpace(Address) ? null : PageAddress(new DateOnly(2026, 1, 2));
        }
        catch (UriFormatException)
        {
            // refused below
        }

        if (sample is null || !sample.IsAbsoluteUri || sample.Scheme != Uri.UriSchemeHttps || sample.UserInfo.Length > 0 ||
            Placeholder().Replace(Address, "").Contains('{', StringComparison.Ordinal))
        {
            throw new FormatException("Adresa zdroja musí byť https://… (deň sa doplní za {d}, {dd}, {M}, {MM}, {yyyy}).");
        }

        HtmlSelector.Parse(Item);
        if (Section is { Length: > 0 } section)
        {
            HtmlSelector.Parse(section);
        }

        foreach (var (name, field) in Fields)
        {
            if (!KnownFields.Contains(name))
            {
                throw new FormatException($"Neznáme pole „{name}“ (známe sú: {string.Join(", ", KnownFields)}).");
            }

            if (field.Select is { Length: > 0 } select && select != ":self")
            {
                HtmlSelector.Parse(select);
            }

            if (field.Take is not ("text" or "own") && !field.Take.StartsWith("attr:", StringComparison.Ordinal))
            {
                throw new FormatException($"Pole „{name}“: neznáme „take“ – {field.Take}.");
            }
        }

        if (!Fields.ContainsKey("title") || !Fields.ContainsKey("episode"))
        {
            throw new FormatException("Pravidlá musia určiť aspoň polia title a episode.");
        }

        if (DaysBack is < 0 or > 30 || DaysAhead is < 0 or > 60)
        {
            throw new FormatException("Počet dní dozadu môže byť 0–30, dopredu 0–60.");
        }
    }

    /// <summary>The address of the page of a day.</summary>
    public Uri PageAddress(DateOnly day) => new(Placeholder().Replace(Address, m => m.Value switch
    {
        "{d}" => day.Day.ToString(CultureInfo.InvariantCulture),
        "{dd}" => day.Day.ToString("00", CultureInfo.InvariantCulture),
        "{M}" => day.Month.ToString(CultureInfo.InvariantCulture),
        "{MM}" => day.Month.ToString("00", CultureInfo.InvariantCulture),
        _ => day.Year.ToString("0000", CultureInfo.InvariantCulture),
    }));

    /// <summary>The episodes on a day's page.</summary>
    public IReadOnlyList<SeriesEpisode> Read(string html, DateOnly day, string source)
    {
        var document = HtmlParser.Parse(html);
        var item = HtmlSelector.Parse(Item);
        var section = Section is { Length: > 0 } s ? HtmlSelector.Parse(s) : null;
        var kind = EpisodeKind.Episode;
        var episodes = new List<SeriesEpisode>();
        foreach (var element in document.Descendants())
        {
            if (section is not null && section.Matches(element))
            {
                var heading = Words.Normalize(element.TextContent);
                kind = Sections.FirstOrDefault(r => heading.Contains(Words.Normalize(r.Contains), StringComparison.Ordinal))?.Kind ?? EpisodeKind.Episode;
                continue;
            }

            if (item.Matches(element) && ReadItem(element, day, source, kind) is { } episode)
            {
                episodes.Add(episode);
            }
        }

        return episodes;
    }

    private SeriesEpisode? ReadItem(HtmlElement item, DateOnly day, string source, EpisodeKind kind)
    {
        var title = WebText.Clean(Value(item, "title"), 300);
        var original = WebText.Clean(Value(item, "original"), 300);
        if (title.Length == 0 && original.Length == 0)
        {
            return null;
        }

        var marker = Value(item, "episode");
        int? season = null, number = null, last = null;
        if (EpisodeMarker().Match(marker) is { Success: true } match)
        {
            season = int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture);
            number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
            if (match.Groups["more"].Success)
            {
                last = number + int.Parse(match.Groups["more"].Value, CultureInfo.InvariantCulture);
            }

            // a section of new series or seasons says so; a first episode elsewhere is a premiere too
            if (kind is EpisodeKind.Episode or EpisodeKind.Streaming && number == 1)
            {
                kind = season == 1 ? EpisodeKind.SeriesPremiere : EpisodeKind.SeasonPremiere;
            }
        }
        else if (kind != EpisodeKind.Streaming)
        {
            return null;
        }

        var channel = WebText.Clean(Value(item, "channel"), 100);
        var channelId = WebText.Clean(Value(item, "channelId"), 100);
        var domestic = Domestic.Any(d =>
            string.Equals(d, channelId, StringComparison.OrdinalIgnoreCase) || Words.Normalize(d) == Words.Normalize(channel));
        var link = Value(item, "link");
        return new SeriesEpisode
        {
            Source = source,
            Region = domestic ? NewsRegion.Domestic : NewsRegion.World,
            Title = title.Length > 0 ? title : original,
            OriginalTitle = original.Length > 0 ? original : title,
            Year = YearOf().Match(Value(item, "years")) is { Success: true } year ? int.Parse(year.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            Season = season,
            Number = number,
            LastNumber = last,
            AirDate = day,
            Channel = channel,
            Genres = WebText.Clean(Value(item, "genres"), 200).Replace(" / ", ", ", StringComparison.Ordinal),
            Kind = kind,
            Link = Uri.TryCreate(link, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttps ? address.AbsoluteUri : null,
        };
    }

    private string Value(HtmlElement item, string field)
    {
        if (!Fields.TryGetValue(field, out var rule))
        {
            return "";
        }

        var element = rule.Select is { Length: > 0 } select && select != ":self" ? Scoped(item, select) : item;
        if (element is null)
        {
            return "";
        }

        return rule.Take switch
        {
            "own" => element.OwnText,
            "text" => element.TextContent,
            var take => element.Attribute(take["attr:".Length..].ToLowerInvariant()) ?? "",
        };
    }

    private static HtmlElement? Scoped(HtmlElement item, string select)
    {
        var selector = HtmlSelector.Parse(select);
        return item.Descendants().FirstOrDefault(e => selector.Matches(e, item));
    }

    [GeneratedRegex(@"\{(d|dd|M|MM|yyyy)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    // "01×01", "2026×127", "1x05", "01×09 + 1 další"
    [GeneratedRegex(@"(?<season>\d{1,4})\s*[×xX]\s*(?<number>\d{1,4})(?:\s*\+\s*(?<more>\d{1,2}))?", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeMarker();

    [GeneratedRegex(@"((?:19|20)\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex YearOf();
}
