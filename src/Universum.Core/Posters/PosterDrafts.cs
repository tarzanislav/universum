using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Text;

namespace Universum.Core.Posters;

/// <summary>The keys of the services that draw a poster: the paid one (OpenAI) wins; without it the free one (Cloudflare).</summary>
public sealed partial record DraftKeys(string? CloudflareAccount, string? CloudflareToken, string? OpenAiKey)
{
    /// <summary>An OpenAI API key ("sk-…").</summary>
    public static bool IsOpenAiKey(string? key) => key is not null && OpenAi().IsMatch(key.Trim());

    /// <summary>A Cloudflare account ID (32 hexadecimal digits).</summary>
    public static bool IsCloudflareAccount(string? id) => id is not null && Account().IsMatch(id.Trim());

    /// <summary>A Cloudflare API token.</summary>
    public static bool IsCloudflareToken(string? token) => token is not null && Token().IsMatch(token.Trim());

    /// <summary>The service that draws: OpenAI with its key, else Cloudflare with an account and a token, else none.</summary>
    public string? Service => IsOpenAiKey(OpenAiKey) ? "OpenAI" : IsCloudflareAccount(CloudflareAccount) && IsCloudflareToken(CloudflareToken) ? "Cloudflare" : null;

    [GeneratedRegex(@"^sk-[A-Za-z0-9_\-]{20,300}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAi();

    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Account();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{30,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}

/// <summary>A poster drawn by AI: the picture (JPEG or PNG) or why it was not drawn.</summary>
public sealed record PosterDraft(byte[]? Picture, string Service, string? Problem);

/// <summary>The description of a poster written by AI (shown and edited before it is drawn) or why it was not written.</summary>
public sealed record PosterBrief(string? Text, string Service, string? Problem);

/// <summary>
/// A draft of a poster drawn by AI when TMDB has none (owner, 31. 10. 2026): from the plot, the genres, the year and the
/// title (Czech, else English, written in the picture), in the proportions TMDB asks of posters (2:3, 1024 × 1536 – within
/// 500 × 750 and 2000 × 3000; written as JPEG). Paid OpenAI (gpt-image-2) with the user's key, else free Cloudflare Workers
/// AI (FLUX.2 klein) with the user's account. Only the title, year, genres and plot are sent; no real person is drawn.
/// The draft is UNIVERSUM's own picture for the user's library – TMDB does not accept fan art.
/// </summary>
public sealed class PosterDrafts(HttpClient http)
{
    /// <summary>The size drawn (2:3).</summary>
    public const int Width = 1024;

    public const int Height = 1536;

    public const string OpenAiModel = "gpt-image-2";

    public const string CloudflareModel = "@cf/black-forest-labs/flux-2-klein-4b";

    /// <summary>The model that writes the description of the poster (OpenAI).</summary>
    public const string OpenAiTextModel = "gpt-4o-mini";

    /// <summary>The model that writes the description of the poster (Cloudflare Workers AI, free).</summary>
    public const string CloudflareTextModel = "@cf/meta/llama-3.3-70b-instruct-fp8-fast";

    /// <summary>
    /// What is asked of the AI that writes the description (owner, 22. 11. 2026: the description seen – and edited – before the
    /// poster is drawn, with the user's own wishes): the kind, the name, the year, the countries, the genres and the plot
    /// (the plot of ČSFD in Czech or Slovak too); never actors.
    /// </summary>
    public static (string System, string User) DescribePrompt(TmdbDetails details, string? countries, string? wishes, string language)
    {
        var plot = (details.EnglishOverview.Length > 0 ? details.EnglishOverview : details.Overview).Trim();
        if (plot.Length > 1500)
        {
            plot = plot[..1500].TrimEnd() + "…";
        }

        var system = string.Join(" ",
            "You design posters of films and TV series.",
            "Write the visual concept of one theatrical poster for the title below: the main motif or scene, the characters (anonymous – never a real or recognizable person, no actor's name or likeness), the setting and era, the colours and light, the mood, the composition and the lettering of the title.",
            "3 to 5 sentences of plain text – no list, no heading, no introduction.",
            $"Write in {language}.",
            "When the user gives wishes for the poster, follow them; they come before your own ideas.");
        var user = string.Join("\n", new[]
        {
            $"Kind: {(details.Kind == MediaKind.Movie ? "film" : "TV series")}",
            $"Title: {(details.CzechTitle.Length > 0 ? details.CzechTitle : details.EnglishTitle)}" + (details.OriginalTitle.Length > 0 && details.OriginalTitle != details.CzechTitle ? $" (original: {details.OriginalTitle})" : ""),
            details.Year is { } year ? $"Year: {year}" : "",
            countries is { Length: > 0 } ? $"Countries: {countries}" : "",
            TmdbGenres.JoinEnglish(details.Genres) is { Length: > 0 } genres ? $"Genres: {genres}" : "",
            plot.Length > 0 ? $"Plot: {plot}" : "",
            wishes?.Trim() is { Length: > 0 } wanted ? $"Wishes for the poster: {wanted}" : "",
        }.Where(l => l.Length > 0));
        return (system, user);
    }

    /// <summary>
    /// The description of the poster written by AI in <paramref name="language"/> ("Slovak", "Czech", "English") – with the
    /// service of the keys (OpenAI, else Cloudflare).
    /// </summary>
    public async Task<PosterBrief> DescribeAsync(TmdbDetails details, string? countries, string? wishes, string language, DraftKeys keys, CancellationToken cancellationToken = default)
    {
        var (system, user) = DescribePrompt(details, countries, wishes, language);
        var (text, service, problem) = await WriteAsync(system, user, keys, cancellationToken).ConfigureAwait(false);
        return new PosterBrief(text, service, problem);
    }

    /// <summary>
    /// Draws the poster of a description (seen and edited by the user): the free service (Cloudflare) follows English, so a
    /// description in another language is first put into English by its text model.
    /// </summary>
    public async Task<PosterDraft> CreateAsync(TmdbDetails details, string brief, string language, DraftKeys keys, CancellationToken cancellationToken = default)
    {
        if (keys.Service == "Cloudflare" && language != "English" && brief.Trim().Length > 0)
        {
            var (english, _, problem) = await WriteAsync("Translate the description of a poster into English. Output only the translation.", brief.Trim(), keys, cancellationToken).ConfigureAwait(false);
            if (english is null)
            {
                return new PosterDraft(null, "Cloudflare", problem);
            }

            brief = english;
        }

        return await CreateAsync(details, keys, Prompt(details, brief), cancellationToken).ConfigureAwait(false);
    }

    // a text written by the text model of the service: the text, the service, why there is none
    private async Task<(string? Text, string Service, string? Problem)> WriteAsync(string system, string user, DraftKeys keys, CancellationToken cancellationToken)
    {
        var service = keys.Service;
        if (service is null)
        {
            return (null, "", CoreText.T("Na návrh plagátu cez AI nastavte v Nastaveniach účet Cloudflare (zadarmo) alebo kľúč OpenAI (platené)."));
        }

        try
        {
            object[] messages = [new { role = "system", content = system }, new { role = "user", content = user }];
            using var request = service == "OpenAI"
                ? new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
                {
                    Content = JsonContent.Create(new { model = OpenAiTextModel, messages, max_tokens = 700 }),
                }
                : new HttpRequestMessage(HttpMethod.Post, $"https://api.cloudflare.com/client/v4/accounts/{keys.CloudflareAccount!.Trim()}/ai/run/{CloudflareTextModel}")
                {
                    Content = JsonContent.Create(new { messages, max_tokens = 700 }),
                };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", service == "OpenAI" ? keys.OpenAiKey!.Trim() : keys.CloudflareToken!.Trim());
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, service, Problem(service, response.StatusCode, body));
            }

            // OpenAI: { "choices": [ { "message": { "content": "…" } } ] }; Cloudflare: { "result": { "response": "…" } }
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var text = service == "OpenAI"
                ? root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first &&
                  first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null
                : root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("response", out var answer) &&
                  answer.ValueKind == JsonValueKind.String ? answer.GetString() : null;
            text = text?.Trim().Trim('"').Trim();
            return text is { Length: > 0 } ? (text.Length > 2000 ? text[..2000] : text, service, null) : (null, service, CoreText.F("{0} nevrátilo text.", service));
        }
        catch (HttpRequestException ex)
        {
            return (null, service, CoreText.F("{0} nie je dostupné: {1}", service, ex.Message));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, service, CoreText.F("{0} neodpovedá – skúste to neskôr.", service));
        }
        catch (JsonException)
        {
            return (null, service, CoreText.F("{0} vrátilo nečitateľnú odpoveď.", service));
        }
    }

    /// <summary>What is asked of the AI (in English, which the models follow best).</summary>
    public static string Prompt(TmdbDetails details) => Prompt(details, null);

    /// <summary>
    /// What is asked of the AI that draws: the description of the poster (seen by the user) in place of the plot when there
    /// is one.
    /// </summary>
    public static string Prompt(TmdbDetails details, string? brief)
    {
        var posterTitle = details.CzechTitle.Length > 0 ? details.CzechTitle : details.EnglishTitle;
        var plot = (details.EnglishOverview.Length > 0 ? details.EnglishOverview : details.Overview).Trim();
        if (plot.Length > 700)
        {
            plot = plot[..700].TrimEnd() + "…";
        }

        var genres = TmdbGenres.JoinEnglish(details.Genres);
        var mood = PosterChoice.MoodOf(details.Genres) switch
        {
            PosterMood.Dark => "dark, moody, high-contrast lighting, suspenseful atmosphere",
            PosterMood.Bright => "vibrant, colourful, bright and warm, inviting atmosphere",
            _ => "cinematic lighting, rich balanced colours",
        };
        return string.Join(" ", new[]
        {
            $"A professional theatrical {(details.Kind == MediaKind.Movie ? "movie" : "TV series")} poster in vertical 2:3 format.",
            $"The title \"{posterTitle}\" is written clearly in large, well-designed letters as part of the poster.",
            genres.Length > 0 ? $"Genre: {genres}." : "",
            brief?.Trim() is { Length: > 0 } concept ? $"Poster concept: {concept}" : plot.Length > 0 ? $"Story: {plot}" : "",
            details.Year is { } year ? $"Released in {year}; show the era and setting the story takes place in." : "Show the era and setting the story takes place in.",
            $"Style: {mood}, striking composition with a clear focal point, suitable for presentation.",
            "Do not depict any real, recognizable person; characters are anonymous or seen from a distance. No credits block, no watermark, no logos.",
        }.Where(p => p.Length > 0));
    }

    /// <summary>Draws a poster with the service of the keys; the picture or the problem in words.</summary>
    public Task<PosterDraft> CreateAsync(TmdbDetails details, DraftKeys keys, CancellationToken cancellationToken = default) =>
        CreateAsync(details, keys, Prompt(details), cancellationToken);

    private async Task<PosterDraft> CreateAsync(TmdbDetails details, DraftKeys keys, string prompt, CancellationToken cancellationToken)
    {
        var service = keys.Service;
        if (service is null)
        {
            return new PosterDraft(null, "", CoreText.T("Na návrh plagátu cez AI nastavte v Nastaveniach účet Cloudflare (zadarmo) alebo kľúč OpenAI (platené)."));
        }

        try
        {
            return service == "OpenAI"
                ? await OpenAiAsync(prompt, keys.OpenAiKey!.Trim(), cancellationToken).ConfigureAwait(false)
                : await CloudflareAsync(prompt, keys.CloudflareAccount!.Trim(), keys.CloudflareToken!.Trim(), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new PosterDraft(null, service, CoreText.F("{0} nie je dostupné: {1}", service, ex.Message));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PosterDraft(null, service, CoreText.F("{0} neodpovedá – skúste to neskôr.", service));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return new PosterDraft(null, service, CoreText.F("{0} vrátilo nečitateľnú odpoveď.", service));
        }
    }

    private async Task<PosterDraft> OpenAiAsync(string prompt, string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations")
        {
            Content = JsonContent.Create(new { model = OpenAiModel, prompt, size = $"{Width}x{Height}", quality = "medium", output_format = "jpeg", n = 1 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new PosterDraft(null, "OpenAI", Problem("OpenAI", response.StatusCode, body));
        }

        using var document = JsonDocument.Parse(body);
        var picture = document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array &&
                      data.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first && first.TryGetProperty("b64_json", out var b64)
            ? Convert.FromBase64String(b64.GetString() ?? "")
            : null;
        return Checked(picture, "OpenAI");
    }

    private async Task<PosterDraft> CloudflareAsync(string prompt, string account, string token, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(prompt), "prompt" },
            { new StringContent(Width.ToString(System.Globalization.CultureInfo.InvariantCulture)), "width" },
            { new StringContent(Height.ToString(System.Globalization.CultureInfo.InvariantCulture)), "height" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.cloudflare.com/client/v4/accounts/{account}/ai/run/{CloudflareModel}") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new PosterDraft(null, "Cloudflare", Problem("Cloudflare", response.StatusCode, body));
        }

        // { "result": { "image": "<base64>" }, "success": true }
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var holder = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ? result : root;
        var picture = holder.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.String ? Convert.FromBase64String(image.GetString()!) : null;
        return Checked(picture, "Cloudflare");
    }

    private static PosterDraft Checked(byte[]? picture, string service) =>
        picture is { Length: > 0 } && PosterCache.IsPicture(picture)
            ? new PosterDraft(picture, service, null)
            : new PosterDraft(null, service, CoreText.F("{0} nevrátilo obrázok JPEG ani PNG.", service));

    // the service's own message (OpenAI: error.message; Cloudflare: errors[].message), shortened
    private static string Problem(string service, HttpStatusCode status, string body)
    {
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString()
                : root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array &&
                  errors.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first && first.TryGetProperty("message", out var e) ? e.GetString()
                : null;
        }
        catch (JsonException)
        {
            // not JSON
        }

        var reason = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => CoreText.T("kľúč alebo token nie je platný alebo nemá povolenie"),
            HttpStatusCode.TooManyRequests => CoreText.T("dnešný limit je vyčerpaný alebo prišlo priveľa požiadaviek – skúste to neskôr"),
            HttpStatusCode.PaymentRequired => CoreText.T("účet nemá kredit"),
            _ => CoreText.F("chyba {0}", (int)status),
        };
        message = message is { Length: > 0 } ? (message.Length > 240 ? message[..240] + "…" : message) : null;
        return $"{service}: {reason}" + (message is null ? "." : $" ({message}).");
    }
}
