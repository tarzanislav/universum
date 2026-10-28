using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Universum.Core.Text;

namespace Universum.Core.Library.Histra;

/// <summary>A list of the user in HISTRA as its overview shows it (five posters as a preview).</summary>
public sealed record HistraListInfo(
    int Id,
    string Name,
    string Description,
    string Visibility,
    int ItemCount,
    DateTimeOffset? UpdatedAt,
    string OwnerName,
    bool IsMine,
    IReadOnlyList<string> PreviewPosters);

/// <summary>A further value HISTRA gives for a title (shown in its detail as it came).</summary>
public sealed record TitleDetail(string Name, string Value);

/// <summary>A film or series in a HISTRA list with what HISTRA tells about it.</summary>
public sealed record HistraListItem(
    MediaKind Kind,
    string Title,
    string OriginalTitle,
    int? Year,
    string Overview,
    double? Rating,
    int? Runtime,
    string Status,
    string? PosterPath,
    TitleIds Ids,
    IReadOnlyList<TitleDetail> Other)
{
    /// <summary>The title as the library names it: the original title and the year.</summary>
    [JsonIgnore]
    public string Caption => Year is { } year ? $"{OriginalTitle} ({year})" : OriginalTitle;
}

/// <summary>A HISTRA list as UNIVERSUM keeps it: its overview, all its titles and when it was read.</summary>
public sealed record HistraList(HistraListInfo Info, IReadOnlyList<HistraListItem> Items, DateTimeOffset ReadAt)
{
    /// <summary>The list was not found in HISTRA at the last synchronisation (the copy in UNIVERSUM stays).</summary>
    public bool MissingInHistra { get; init; }
}

/// <summary>
/// Reads the user's lists from HISTRA – only reads: nothing is written to HISTRA (owner's decision, 28. 10. 2026).
/// The overview gives the lists with a few posters; the detail of a list gives all its titles with names, posters and
/// external IDs, translated to Czech where HISTRA has it. HISTRA answers in pages ("limit" cut to its cap, "offset"):
/// pages are read until the list's item count is reached or a page brings nothing new.
/// </summary>
public sealed class HistraLists(HistraClient client)
{
    /// <summary>The page size asked for; HISTRA cuts a larger limit to its own cap, which the paging allows for.</summary>
    public const int PageSize = 500;

    /// <summary>Protection against an endless answer: at most this many titles of one list.</summary>
    public const int MaxItems = 50_000;

    /// <summary>Protection: at most this many lists.</summary>
    public const int MaxLists = 5_000;

    /// <summary>The language of titles and descriptions (Czech – Slovak HISTRA does not offer).</summary>
    public const string Language = "cs-CZ";

    /// <summary>The user's lists (their overview, without the titles).</summary>
    public async Task<IReadOnlyList<HistraListInfo>> OverviewAsync(CancellationToken cancellationToken = default)
    {
        var lists = new List<HistraListInfo>();
        var seen = new HashSet<int>();
        for (var offset = 0; lists.Count < MaxLists;)
        {
            using var document = await client.GetAsync($"api/v1/lists?limit={PageSize}&offset={offset}", cancellationToken).ConfigureAwait(false);
            var page = ParseOverview(document);
            var fresh = page.Where(l => seen.Add(l.Id)).ToList();
            lists.AddRange(fresh);
            if (fresh.Count == 0)
            {
                break;
            }

            offset += page.Count;
        }

        return lists;
    }

    /// <summary>
    /// A list with all its titles; null when HISTRA does not know the list (deleted there). <paramref name="progress"/>
    /// gets the titles read so far and the list's item count.
    /// </summary>
    public async Task<HistraList?> ReadAsync(int id, IProgress<(int Read, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        HistraListInfo? info = null;
        var items = new List<HistraListItem>();
        var seen = new HashSet<(MediaKind, int)>();
        for (var offset = 0; items.Count < MaxItems;)
        {
            using var document = await client.GetAsync(
                $"api/v1/lists/{id}?extended=full&lang={Language}&limit={PageSize}&offset={offset}", cancellationToken).ConfigureAwait(false);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return info is null ? null : new HistraList(info, items, DateTimeOffset.Now);
            }

            var root = document.RootElement;
            if (info is null)
            {
                info = root.TryGetProperty("list", out var list) ? ParseInfo(list) : null;
                if (info is null)
                {
                    return null;
                }
            }

            var raw = root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array ? array : default;
            var count = raw.ValueKind == JsonValueKind.Array ? raw.GetArrayLength() : 0;
            var added = 0;
            if (count > 0)
            {
                foreach (var element in raw.EnumerateArray())
                {
                    // a title without HISTRA's number is still shown; one twice (in two pages) only once
                    if (ParseItem(element) is { } item && (item.Ids.Histra is not { } number || seen.Add((item.Kind, number))))
                    {
                        items.Add(item);
                        added++;
                    }
                }
            }

            progress?.Report((items.Count, Math.Max(info.ItemCount, items.Count)));
            if (count == 0 || added == 0 || items.Count >= info.ItemCount)
            {
                break;
            }

            offset += count;
        }

        return info is null ? null : new HistraList(info, items, DateTimeOffset.Now);
    }

    /// <summary>The lists of an overview answer ({"lists": [...]}, or a bare array).</summary>
    public static IReadOnlyList<HistraListInfo> ParseOverview(JsonDocument? document)
    {
        if (document is null)
        {
            return [];
        }

        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lists", out var lists) && lists.ValueKind == JsonValueKind.Array ? lists
            : default;
        return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Select(ParseInfo).OfType<HistraListInfo>().ToList() : [];
    }

    /// <summary>A list's overview; null without a usable number.</summary>
    public static HistraListInfo? ParseInfo(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || Number(element, "id") is not { } id)
        {
            return null;
        }

        var posters = element.TryGetProperty("preview_posters", out var previews) && previews.ValueKind == JsonValueKind.Array
            ? previews.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()).Where(PosterCache.IsValidPath).OfType<string>().Take(10).ToList()
            : [];
        var name = WebText.Clean(Text(element, "name"), 200);
        return new HistraListInfo(
            id,
            name.Length > 0 ? name : $"List {id}",
            WebText.Clean(Text(element, "description"), 1000),
            WebText.Clean(Text(element, "visibility"), 40),
            Number(element, "item_count") is { } count and >= 0 ? count : 0,
            DateTimeOffset.TryParse(Text(element, "updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated) ? updated : null,
            WebText.Clean(Text(element, "owner_name"), 200),
            element.TryGetProperty("is_mine", out var mine) && mine.ValueKind == JsonValueKind.True,
            posters);
    }

    /// <summary>A title of a list; null for one that is neither a film nor a series or has no name.</summary>
    public static HistraListItem? ParseItem(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        MediaKind? kind = Text(element, "media_type") switch
        {
            "movie" => MediaKind.Movie,
            "show" or "tv" or "series" => MediaKind.Series,
            _ => null,
        };
        var title = WebText.Clean(Text(element, "title"), 300);
        var original = WebText.Clean(Text(element, "original_title"), 300);
        if (kind is null || (title.Length == 0 && original.Length == 0))
        {
            return null;
        }

        var external = element.TryGetProperty("external_ids", out var ids) && ids.ValueKind == JsonValueKind.Object ? ids : default;
        var poster = Text(element, "poster_path");
        return new HistraListItem(
            kind.Value,
            title.Length > 0 ? title : original,
            original.Length > 0 ? original : title,
            Number(element, "year") is { } year and > 1800 and < 3000 ? year : null,
            WebText.Clean(Text(element, "overview")),
            element.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Number && rating.TryGetDouble(out var value) && value is >= 0 and <= 10 ? Math.Round(value, 1, MidpointRounding.AwayFromZero) : null,
            Number(element, "runtime") is { } minutes and > 0 and < 10_000 ? minutes : null,
            WebText.Clean(Text(element, "status"), 60),
            PosterCache.IsValidPath(poster) ? poster : null,
            new TitleIds(
                Number(element, "media_id") is { } histra and > 0 ? histra : null,
                external.ValueKind == JsonValueKind.Object && Number(external, "tmdb") is { } tmdb and > 0 ? tmdb : null,
                external.ValueKind == JsonValueKind.Object ? TitleIds.ValidImdb(Text(external, "imdb")) : null,
                external.ValueKind == JsonValueKind.Object && Number(external, "trakt") is { } trakt and > 0 ? trakt : null,
                external.ValueKind == JsonValueKind.Object && Number(external, "tvdb") is { } tvdb and > 0 ? tvdb : null),
            Others(element));
    }

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "media_type", "media_id", "title", "original_title", "year", "overview", "rating", "runtime", "status",
        "poster_path", "backdrop_path", "external_ids",
    };

    // whatever else HISTRA tells about a title (texts, numbers, lists of texts), as it came
    private static List<TitleDetail> Others(JsonElement element)
    {
        var others = new List<TitleDetail>();
        foreach (var property in element.EnumerateObject())
        {
            if (Known.Contains(property.Name) || others.Count >= 30)
            {
                continue;
            }

            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => WebText.Clean(property.Value.GetString(), 500),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "áno",
                JsonValueKind.False => "nie",
                JsonValueKind.Array => WebText.Clean(string.Join(", ", property.Value.EnumerateArray()
                    .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null)
                    .OfType<string>()), 500),
                _ => "",
            };
            if (value.Length > 0)
            {
                others.Add(new TitleDetail(WebText.Clean(property.Name, 60), value));
            }
        }

        return others;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // a number given as a number or as a text ("603")
    private static int? Number(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) ? null
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number
        : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number
        : null;
}

/// <summary>
/// The HISTRA lists imported into UNIVERSUM, kept in one JSON file of the Windows account (never sent anywhere);
/// written to a temporary file first, so an interrupted save leaves the previous lists.
/// </summary>
public sealed class HistraListStore(string file)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public string File { get; } = file;

    /// <summary>The imported lists in the order the user has them; empty when none or the file is unreadable.</summary>
    public IReadOnlyList<HistraList> Load()
    {
        try
        {
            return System.IO.File.Exists(File)
                ? JsonSerializer.Deserialize<List<HistraList>>(System.IO.File.ReadAllBytes(File), Options) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<HistraList> lists)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        var temporary = File + ".part";
        System.IO.File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(lists.ToList(), Options));
        System.IO.File.Move(temporary, File, overwrite: true);
    }
}
