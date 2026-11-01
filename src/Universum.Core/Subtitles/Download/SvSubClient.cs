using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Universum.Core.Text;

namespace Universum.Core.Subtitles.Download;

/// <summary>
/// Translation of subtitles by svsub.net with the user's own token (owner, 30. 10. 2026: when no subtitles exist; the
/// translation runs on the AI key the user keeps in the service's settings). Only the subtitles are sent – under a
/// neutral file name, never the name of the film – and the job is deleted once the translation is downloaded.
/// </summary>
public sealed class SvSubClient(HttpClient http, string token)
{
    /// <summary>The job was deleted on the service after the last translation (false: it may still be there).</summary>
    public bool JobDeleted { get; private set; }

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
    /// <param name="model">The AI model ("gpt-6-astra"); null = the service's default (owner, 30. 10. 2026: verified – the service uses it).</param>
    /// <param name="sourceLanguage">The language of the subtitles ("en"); null = the service finds it.</param>
    public async Task<string> TranslateAsync(string srt, string targetLanguage, string provider, IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        string? model = null, string? sourceLanguage = null)
    {
        if (Encoding.UTF8.GetByteCount(srt) > MaximumBytes)
        {
            throw new SubtitleServiceException(CoreText.T("svsub.net: titulky sú väčšie ako 5 MB."));
        }

        var body = new JsonObject
        {
            ["fileName"] = "titulky.srt",
            ["content"] = srt,
            ["provider"] = provider,
            ["targetLang"] = targetLanguage,
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            body["model"] = model.Trim();
        }

        if (sourceLanguage is { Length: 2 or 3 } source && source.All(char.IsAsciiLetterLower) && source != targetLanguage)
        {
            body["sourceLang"] = source;
        }

        var created = await SendAsync(HttpMethod.Post, "jobs", body, cancellationToken).ConfigureAwait(false);
        var id = Id(created) ?? throw new SubtitleServiceException(CoreText.T("svsub.net: služba nevrátila číslo úlohy."));
        try
        {
            progress?.Report(CoreText.T("Preklad beží…"));
            var started = DateTime.UtcNow;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = await SendAsync(HttpMethod.Get, "jobs/" + Uri.EscapeDataString(id), null, cancellationToken).ConfigureAwait(false);
                var state = State(job);
                if (state.Failed)
                {
                    throw new SubtitleServiceException(CoreText.T("svsub.net: preklad sa nepodaril – ") + (state.Message ?? state.Status ?? CoreText.T("neznáma chyba")));
                }

                if (state.Done)
                {
                    break;
                }

                progress?.Report(state.Progress is { } done ? CoreText.F("Preklad beží… {0:0} %", done) : CoreText.T("Preklad beží…"));
                if (DateTime.UtcNow - started > TimeSpan.FromHours(2))
                {
                    throw new SubtitleServiceException(CoreText.T("svsub.net: preklad trvá viac ako dve hodiny – skúste ho stiahnuť neskôr na stránke služby."));
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(CoreText.T("Sťahujem preklad…"));
            string text;
            try
            {
                using var request = Request(HttpMethod.Get, "jobs/" + Uri.EscapeDataString(id) + "/download");
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new SubtitleServiceException(CoreText.F("svsub.net: preklad sa nepodarilo stiahnuť ({0}) – {1}", (int)response.StatusCode, Message(text) ?? response.ReasonPhrase));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new SubtitleServiceException(CoreText.T("svsub.net: preklad sa nepodarilo stiahnuť – ") + ex.Message, ex);
            }

            // the file itself, or JSON with it
            if (text.TrimStart().StartsWith('{'))
            {
                try
                {
                    var json = JsonNode.Parse(text);
                    if ((json?["content"] ?? json?["result"] ?? json?["data"]?["content"]) is JsonValue value && value.TryGetValue<string>(out var content))
                    {
                        text = content;
                    }
                }
                catch (JsonException)
                {
                    // not JSON after all: the text as it is
                }
            }

            return text;
        }
        finally
        {
            // nothing stays on the service (owner: the project is strictly private); a failed delete is said, never hidden
            JobDeleted = false;
            try
            {
                using var delete = Request(HttpMethod.Delete, "jobs/" + Uri.EscapeDataString(id));
                using var answer = await http.SendAsync(delete, CancellationToken.None).ConfigureAwait(false);
                JobDeleted = answer.IsSuccessStatusCode || answer.StatusCode == HttpStatusCode.NotFound;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SubtitleServiceException(CoreText.T("svsub.net nie je dostupný: ") + ex.Message, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var message = Message(text) ?? response.ReasonPhrase;
                throw new SubtitleServiceException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => CoreText.T("svsub.net: neplatný token – ") + message,
                    (HttpStatusCode)429 => CoreText.T("svsub.net: príliš veľa požiadaviek (60 za minútu) – ") + message,
                    _ when text.Contains("no_key_for_provider", StringComparison.Ordinal) =>
                        CoreText.T("svsub.net: v nastaveniach služby chýba váš AI kľúč pre zvoleného poskytovateľa (no_key_for_provider)."),
                    _ => CoreText.F("svsub.net: chyba {0} – {1}", (int)response.StatusCode, message),
                });
            }

            try
            {
                return JsonNode.Parse(text) ?? new JsonObject();
            }
            catch (JsonException)
            {
                throw new SubtitleServiceException(CoreText.T("svsub.net: nečitateľná odpoveď."));
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
        // the service says a failure by "errorMessage" (verified with the owner's token, 30. 10. 2026)
        var error = (node["errorMessage"] ?? node["error"])?.ToString();
        var running = node["running"] is JsonValue flag && flag.TryGetValue<bool>(out var isRunning) && isRunning;
        var failed = status is "failed" or "error" or "cancelled" or "canceled" || (!done && !running && !string.IsNullOrEmpty(error));
        return (done, failed, status, progress, error ?? node["message"]?.ToString());
    }

    private static string? Message(string text)
    {
        try
        {
            var json = JsonNode.Parse(text);
            var message = (json?["message"] ?? json?["error"])?.ToString();
            var code = json?["code"]?.ToString();
            return message is null ? code : code is null ? message : $"{message} ({code})";
        }
        catch (JsonException)
        {
            return text.Length is > 0 and < 300 ? text : null;
        }
    }
}
