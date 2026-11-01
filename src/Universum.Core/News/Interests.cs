using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Universum.Core.Text;

namespace Universum.Core.News;

/// <summary>A translation could not be made (wrong key, the month's characters used up, DeepL unavailable).</summary>
public sealed class TranslationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Translations by DeepL with the user's own key (owner, 29. 10. 2026): only the titles and short summaries of articles
/// are sent; a free key (ending ":fx") goes to DeepL API Free.
/// </summary>
public sealed class DeepLClient(HttpClient http, string key)
{
    public const string Attribution = "Preklady: DeepL";

    public Uri Endpoint { get; } = new(key.EndsWith(":fx", StringComparison.Ordinal) ? "https://api-free.deepl.com/v2/translate" : "https://api.deepl.com/v2/translate");

    /// <summary>A key looks like a DeepL key (no spaces, sensible length).</summary>
    public static bool IsValidKey(string? key) => key is { Length: >= 20 and <= 100 } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ':');

    /// <summary>The texts in the target language ("sk", "cs", "en"), in the same order.</summary>
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string target, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                text = texts,
                target_lang = target.ToUpperInvariant() == "EN" ? "EN-GB" : target.ToUpperInvariant(),
            }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException(CoreText.T("DeepL nie je dostupný: ") + ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationException(CoreText.T("DeepL neodpovedá."), ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized:
                    throw new TranslationException(CoreText.T("DeepL neprijal kľúč – skontrolujte ho v Nastaveniach."));
                case (HttpStatusCode)456:
                    throw new TranslationException(CoreText.T("Mesačný počet znakov kľúča DeepL je vyčerpaný – ďalšie články ostanú v origináli."));
                case HttpStatusCode.TooManyRequests:
                    throw new TranslationException(CoreText.T("DeepL žiada spomaliť – zvyšok sa preloží pri ďalšej aktualizácii."));
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationException(CoreText.F("DeepL odpovedal chybou {0}.", (int)response.StatusCode));
            }

            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var translated = document.RootElement.GetProperty("translations").EnumerateArray().Select(t => t.GetProperty("text").GetString() ?? "").ToList();
                return translated.Count == texts.Count ? translated : throw new TranslationException(CoreText.T("DeepL vrátil neúplný preklad."));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new TranslationException(CoreText.T("Odpoveď DeepL sa nedá prečítať."), ex);
            }
        }
    }
}

/// <summary>What is kept of Zaujímavosti: every article read (the history) and the characters translated per month.</summary>
public sealed record InterestArchive
{
    public List<FeedItem> Items { get; init; } = [];

    public DateTime? ReadAt { get; init; }

    /// <summary>Characters sent to DeepL by month ("2026-10").</summary>
    public Dictionary<string, int> Translated { get; init; } = [];

    /// <summary>At most this many articles are kept (the oldest go first).</summary>
    public const int MaxItems = 10_000;

    /// <summary>The articles read now added to the kept ones (one per link; kept translations stay).</summary>
    public static List<FeedItem> Merge(IEnumerable<FeedItem> kept, IEnumerable<FeedItem> read)
    {
        var items = kept.ToDictionary(i => i.Link, StringComparer.Ordinal);
        foreach (var item in read)
        {
            items[item.Link] = items.TryGetValue(item.Link, out var old)
                ? item with { Translations = old.Title == item.Title && old.Summary == item.Summary ? old.Translations : [], Published = old.Published }
                : item;
        }

        return items.Values.OrderByDescending(i => i.Published).Take(MaxItems).ToList();
    }
}

/// <summary>The kept articles in a file of this Windows account.</summary>
public sealed class InterestStore(string file)
{
    public InterestArchive Load()
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<InterestArchive>(File.ReadAllBytes(file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new();
        }
    }

    public bool Save(InterestArchive archive)
    {
        try
        {
            Files.WriteAtomically(file, JsonSerializer.SerializeToUtf8Bytes(archive));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Clear()
    {
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }
}

/// <summary>
/// Zaujímavosti: the articles of the sources of the settings (their feeds, read politely), kept for good, and the
/// newest ones translated into the user's language by DeepL – at most <see cref="MaxTranslations"/> articles per
/// reading and never over the month's budget.
/// </summary>
public sealed class Interests(WebPageClient pages, InterestStore store)
{
    /// <summary>Articles translated in one reading.</summary>
    public const int MaxTranslations = 60;

    /// <summary>Characters a month kept under DeepL API Free's 500 000.</summary>
    public const int MonthBudget = 450_000;

    public InterestArchive Load() => store.Load();

    /// <summary>
    /// Reads the enabled sources, adds what is new to the kept articles and translates the newest untranslated ones;
    /// the archive and what could not be done.
    /// </summary>
    public async Task<(InterestArchive Archive, IReadOnlyList<string> Problems)> RefreshAsync(IReadOnlyList<FeedSource> sources, DeepLClient? translator,
        string language, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var archive = store.Load();
        var problems = new List<string>();
        var read = new List<FeedItem>();
        var enabled = sources.Where(s => s.Enabled).ToList();
        for (var i = 0; i < enabled.Count; i++)
        {
            var source = enabled[i];
            progress?.Report(CoreText.F("Zaujímavosti: {0} ({1}/{2})", source.Name, i + 1, enabled.Count));
            try
            {
                read.AddRange(await ReadAsync(source, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is NewsSourceException or FormatException)
            {
                problems.Add($"{source.Name}: {ex.Message}");
            }
        }

        archive = archive with { Items = InterestArchive.Merge(archive.Items, read), ReadAt = DateTime.Now };
        store.Save(archive);
        if (translator is not null)
        {
            try
            {
                archive = await TranslateAsync(archive, translator, language, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (TranslationException ex)
            {
                problems.Add(ex.Message);
            }
        }

        return (archive, problems);
    }

    /// <summary>The articles of one source: its feed, or the feed its page names.</summary>
    public async Task<IReadOnlyList<FeedItem>> ReadAsync(FeedSource source, CancellationToken cancellationToken = default)
    {
        var address = source.Address ?? throw new FormatException(CoreText.T("zdroj nemá bezpečnú adresu https."));
        var text = await pages.GetAsync(address, cancellationToken).ConfigureAwait(false);
        if (!FeedParser.IsFeed(text))
        {
            // the page's feed, else the articles it lists
            if (FeedParser.Discover(text, address) is not { } feed)
            {
                var listed = FeedParser.FromPage(text, address, source.Name, source.Language);
                return listed.Count > 0 ? listed : throw new FormatException(CoreText.T("stránka nemá kanál RSS ani zoznam článkov – zadajte priamo adresu kanála alebo stránky s článkami."));
            }

            text = await pages.GetAsync(feed, cancellationToken).ConfigureAwait(false);
        }

        return FeedParser.Parse(text, source.Name, source.Language);
    }

    // the newest articles in another language without a translation, a batch of titles and summaries at a time
    private async Task<InterestArchive> TranslateAsync(InterestArchive archive, DeepLClient translator, string language, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var month = DateTime.Now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var used = archive.Translated.GetValueOrDefault(month);
        var waiting = archive.Items.Where(i => i.Language != language && !i.Translations.ContainsKey(language)).Take(MaxTranslations).ToList();
        foreach (var batch in waiting.Chunk(15))
        {
            var characters = batch.Sum(i => i.Title.Length + i.Summary.Length);
            if (used + characters > MonthBudget)
            {
                throw new TranslationException(CoreText.T("Tento mesiac je preložených už takmer 500 000 znakov (limit bezplatného DeepL) – ďalšie články ostanú v origináli."));
            }

            progress?.Report(CoreText.F("Prekladám články ({0}/{1})", waiting.IndexOf(batch[0]) + 1, waiting.Count));
            var texts = await translator.TranslateAsync(batch.SelectMany(i => new[] { i.Title, i.Summary.Length > 0 ? i.Summary : "." }).ToList(), language, cancellationToken).ConfigureAwait(false);
            used += characters;
            var done = batch.Select((item, n) => item with
            {
                Translations = new Dictionary<string, FeedTranslation>(item.Translations) { [language] = new(texts[2 * n], item.Summary.Length > 0 ? texts[2 * n + 1] : "") },
            }).ToDictionary(i => i.Link, StringComparer.Ordinal);
            archive = archive with
            {
                Items = archive.Items.Select(i => done.GetValueOrDefault(i.Link) ?? i).ToList(),
                Translated = new Dictionary<string, int>(archive.Translated) { [month] = used },
            };
            store.Save(archive);
        }

        return archive;
    }
}

/// <summary>
/// The pictures of articles, each downloaded once into a folder of this Windows account: only https, at most 3 MiB,
/// only JPEG, PNG and GIF (checked by their first bytes), at most four at a time.
/// </summary>
public sealed class WebImageCache(HttpClient http, string folder)
{
    public const int MaxBytes = 3 * 1024 * 1024;

    private readonly SemaphoreSlim _parallel = new(4);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    /// <summary>The file of a picture, or null (not a picture, too big, unavailable).</summary>
    public async Task<string?> GetAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var file = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..32] + ".img");
        if (File.Exists(file))
        {
            return file;
        }

        lock (_failed)
        {
            if (_failed.Contains(file))
            {
                return null;
            }
        }

        await _parallel.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(WebPageClient.UserAgent);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes || response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            {
                throw new IOException("not a picture");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaxBytes || !IsPicture(bytes))
            {
                throw new IOException("not a picture");
            }

            Files.WriteAtomically(file, bytes);
            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            lock (_failed)
            {
                _failed.Add(file);
            }

            return null;
        }
        finally
        {
            _parallel.Release();
        }
    }

    /// <summary>JPEG, PNG or GIF by its first bytes.</summary>
    public static bool IsPicture(ReadOnlySpan<byte> bytes) =>
        bytes is [0xFF, 0xD8, 0xFF, ..] or [0x89, 0x50, 0x4E, 0x47, ..] or [0x47, 0x49, 0x46, 0x38, ..];
}
