using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Universum.Core.Subtitles.Download;

/// <summary>
/// Translation of subtitles by svsub.net with the user's own token (owner, 30. 10. 2026: when no subtitles exist; the
/// translation runs on the AI key the user keeps in the service's settings). Only the subtitles are sent – under a
/// neutral file name, never the name of the film – and the job is deleted once the translation is downloaded.
/// </summary>
public sealed class SvSubClient(HttpClient http, string token)
{
    private const string Api = "https://svsub.net/api/v1/";

    /// <summary>The service takes at most this many bytes of subtitles.</summary>
    public const int MaximumBytes = 5 * 1024 * 1024;

    /// <summary>How often the state of the job is asked (the service allows 60 requests a minute).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A token of the service ("svs_…").</summary>
    public static bool IsValidToken(string? token) => token is { Length: > 8 } && token.StartsWith("svs_", StringComparison.Ordinal) && !token.Any(char.IsWhiteSpace);

    /// <summary>
    /// Translates SRT subtitles into <paramref name="targetLanguage"/> ("cs", "sk", "en") with the AI
    /// <paramref name="provider"/> ("openai" …) and returns the translated SRT.
    /// </summary>
    /// <param name="progress">The state of the job, in Slovak.</param>
    public async Task<string> TranslateAsync(string srt, string targetLanguage, string provider, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(srt) > MaximumBytes)
        {
            throw new SubtitleServiceException("svsub.net: titulky sú väčšie ako 5 MB.");
        }

        var created = await SendAsync(HttpMethod.Post, "jobs", new JsonObject
        {
            ["fileName"] = "titulky.srt",
            ["content"] = srt,
            ["provider"] = provider,
            ["targetLang"] = targetLanguage,
        }, cancellationToken).ConfigureAwait(false);
        var id = Id(created) ?? throw new SubtitleServiceException("svsub.net: služba nevrátila číslo úlohy.");
        try
        {
            progress?.Report("Preklad beží…");
            var started = DateTime.UtcNow;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = await SendAsync(HttpMethod.Get, "jobs/" + Uri.EscapeDataString(id), null, cancellationToken).ConfigureAwait(false);
                var state = State(job);
                if (state.Failed)
                {
                    throw new SubtitleServiceException("svsub.net: preklad sa nepodaril – " + (state.Message ?? state.Status ?? "neznáma chyba"));
                }

                if (state.Done)
                {
                    break;
                }

                progress?.Report(state.Progress is { } done ? $"Preklad beží… {done:0} %" : "Preklad beží…");
                if (DateTime.UtcNow - started > TimeSpan.FromHours(2))
                {
                    throw new SubtitleServiceException("svsub.net: preklad trvá viac ako dve hodiny – skúste ho stiahnuť neskôr na stránke služby.");
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report("Sťahujem preklad…");
            using var request = Request(HttpMethod.Get, "jobs/" + Uri.EscapeDataString(id) + "/download");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SubtitleServiceException($"svsub.net: preklad sa nepodarilo stiahnuť ({(int)response.StatusCode}) – {Message(text) ?? response.ReasonPhrase}");
            }

            // the file itself, or JSON with it
            if (text.TrimStart().StartsWith('{') && JsonNode.Parse(text) is { } json &&
                (json["content"] ?? json["result"] ?? json["data"]?["content"])?.GetValue<string>() is { } content)
            {
                text = content;
            }

            return text;
        }
        finally
        {
            // nothing stays on the service (owner: the project is strictly private)
            try
            {
                using var delete = Request(HttpMethod.Delete, "jobs/" + Uri.EscapeDataString(id));
                using var _ = await http.SendAsync(delete, CancellationToken.None).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // left on the service: the user can delete it there
            }
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("UNIVERSUM/" + (typeof(SvSubClient).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return request;
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = Request(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SubtitleServiceException("svsub.net nie je dostupný: " + ex.Message, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var message = Message(text) ?? response.ReasonPhrase;
                throw new SubtitleServiceException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "svsub.net: neplatný token – " + message,
                    (HttpStatusCode)429 => "svsub.net: príliš veľa požiadaviek (60 za minútu) – " + message,
                    _ when message?.Contains("no_key_for_provider", StringComparison.Ordinal) == true =>
                        "svsub.net: v nastaveniach služby chýba váš AI kľúč pre zvoleného poskytovateľa (no_key_for_provider).",
                    _ => $"svsub.net: chyba {(int)response.StatusCode} – {message}",
                });
            }

            try
            {
                return JsonNode.Parse(text) ?? new JsonObject();
            }
            catch (JsonException)
            {
                throw new SubtitleServiceException("svsub.net: nečitateľná odpoveď.");
            }
        }
    }

    // the job's ID: "id" of the answer or of its "job" / "data"
    private static string? Id(JsonNode? node) =>
        (node?["id"] ?? node?["jobId"] ?? node?["job"]?["id"] ?? node?["data"]?["id"]) is JsonValue value ? value.ToString() : null;

    private static (bool Done, bool Failed, string? Status, double? Progress, string? Message) State(JsonNode job)
    {
        var node = job["job"] ?? job["data"] ?? job;
        var status = (node["status"] ?? node["state"])?.ToString()?.ToLowerInvariant();
        double? progress = node["progress"] is JsonValue value && value.TryGetValue<double>(out var number) ? number <= 1 && number > 0 ? number * 100 : number : null;
        var done = status is "done" or "completed" or "complete" or "finished" or "success" or "succeeded" or "translated" or "ready";
        var failed = status is "failed" or "error" or "cancelled" or "canceled";
        return (done, failed, status, progress, (node["error"] ?? node["message"])?.ToString());
    }

    private static string? Message(string text)
    {
        try
        {
            var json = JsonNode.Parse(text);
            return (json?["error"] ?? json?["message"] ?? json?["code"])?.ToString();
        }
        catch (JsonException)
        {
            return text.Length is > 0 and < 300 ? text : null;
        }
    }
}
