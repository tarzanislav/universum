using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Universum.Core.Subtitles.Download;

/// <summary>A problem with a subtitle service, in Slovak, for the user.</summary>
public sealed class SubtitleServiceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What to look for on OpenSubtitles.com: a film or an episode by its TMDB ID, else by its name and year.</summary>
/// <param name="Languages">Language codes, e.g. "en" or "en,cs".</param>
/// <param name="HearingImpaired">"include", "exclude" or "only".</param>
public sealed record OpenSubtitlesQuery(string Languages, int? TmdbId, string? Name, int? Year, int? Season = null, int? Episode = null, string HearingImpaired = "include")
{
    public bool IsEpisode => Season is not null && Episode is not null;
}

/// <summary>One subtitle file found on OpenSubtitles.com.</summary>
public sealed record OpenSubtitlesResult(
    int FileId, string FileName, string Language, string Release, int Downloads, bool HearingImpaired, bool ForeignPartsOnly, bool MachineTranslated, double? Fps)
{
    /// <summary>How well it fits the video (0–1, <see cref="ReleaseMatch"/>) – set by the page.</summary>
    public double Match { get; init; }

    /// <summary>The comparison with the video: the frame rate and the release ("FPS sedí · release 80 %").</summary>
    public string MatchNote { get; init; } = "";

    /// <summary>"EN SDH · Release.Name · 12 345 stiahnutí".</summary>
    public string Display => (MatchNote.Length > 0 ? MatchNote + " · " : "") +
        Language.ToUpperInvariant() + (HearingImpaired ? " SDH" : "") + (ForeignPartsOnly ? " forced" : "") + (MachineTranslated ? " (strojový preklad)" : "") +
        " · " + (Release.Length > 0 ? Release : FileName) + " · " + Downloads.ToString("#,0", CultureInfo.GetCultureInfo("sk-SK")) + " stiahnutí" +
        (Fps is { } fps && fps > 0 ? " · " + fps.ToString("0.###", CultureInfo.GetCultureInfo("sk-SK")) + " FPS" : "");
}

/// <summary>
/// OpenSubtitles.com (owner, 30. 10. 2026: English subtitles, also SDH) through its REST API with the user's own API key
/// and account: searching needs only the key, downloading the login (the daily number of downloads depends on the
/// account). The application introduces itself by its name and version. Nothing else is sent.
/// </summary>
public sealed class OpenSubtitlesClient(HttpClient http, string apiKey, string userAgent)
{
    private const string DefaultHost = "api.opensubtitles.com";
    private string _host = DefaultHost;
    private string? _token;

    /// <summary>Downloads left today (after the last download), when the service says it.</summary>
    public int? Remaining { get; private set; }

    /// <summary>Logs in (needed for downloads); the account may send it to another server (VIP).</summary>
    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var answer = await SendAsync(HttpMethod.Post, "login", new JsonObject { ["username"] = username, ["password"] = password }, cancellationToken).ConfigureAwait(false);
        _token = Text(answer["token"]) ?? throw new SubtitleServiceException("OpenSubtitles: prihlásenie nevrátilo token.");
        if (Text(answer["base_url"]) is { Length: > 0 } host)
        {
            host = host.Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
            if (host.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            {
                host = host[..^7];
            }

            // the key and the token go only to OpenSubtitles' own servers (VIP: vip-api.opensubtitles.com)
            if (Uri.CheckHostName(host) == UriHostNameType.Dns && host.EndsWith(".opensubtitles.com", StringComparison.OrdinalIgnoreCase))
            {
                _host = host.ToLowerInvariant();
            }
        }
    }

    /// <summary>The subtitles of a film or an episode, the most downloaded first.</summary>
    public async Task<IReadOnlyList<OpenSubtitlesResult>> SearchAsync(OpenSubtitlesQuery query, CancellationToken cancellationToken = default)
    {
        // the service wants the parameters in alphabetical order, lower case
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["hearing_impaired"] = query.HearingImpaired,
            ["languages"] = query.Languages.ToLowerInvariant(),
            ["order_by"] = "download_count",
        };
        if (query.IsEpisode)
        {
            parameters["episode_number"] = query.Episode!.Value.ToString(CultureInfo.InvariantCulture);
            parameters["season_number"] = query.Season!.Value.ToString(CultureInfo.InvariantCulture);
            parameters["type"] = "episode";
            if (query.TmdbId is { } series)
            {
                parameters["parent_tmdb_id"] = series.ToString(CultureInfo.InvariantCulture);
            }
        }
        else
        {
            parameters["type"] = "movie";
            if (query.TmdbId is { } film)
            {
                parameters["tmdb_id"] = film.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (query.TmdbId is null)
        {
            if (string.IsNullOrWhiteSpace(query.Name))
            {
                throw new SubtitleServiceException("OpenSubtitles: chýba titul (názov alebo TMDB ID).");
            }

            parameters["query"] = query.Name.Trim().ToLowerInvariant();
            if (query.Year is { } year)
            {
                parameters["year"] = year.ToString(CultureInfo.InvariantCulture);
            }
        }

        var path = "subtitles?" + string.Join("&", parameters.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value).Replace("%20", "+", StringComparison.Ordinal)));
        var answer = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        var results = new List<OpenSubtitlesResult>();
        foreach (var item in answer["data"]?.AsArray() ?? [])
        {
            var attributes = item?["attributes"];
            var file = attributes?["files"]?.AsArray().FirstOrDefault();
            if (attributes is null || file?["file_id"] is not { } id)
            {
                continue;
            }

            var language = Text(attributes["language"])?.ToLowerInvariant() ?? "";
            results.Add(new OpenSubtitlesResult(
                Int(id), Text(file["file_name"]) ?? "", language.Length is >= 2 and <= 3 && language.All(char.IsAsciiLetterLower) ? language : "",
                Text(attributes["release"]) ?? "", Int(attributes["download_count"]), Bool(attributes["hearing_impaired"]),
                Bool(attributes["foreign_parts_only"]), Bool(attributes["machine_translated"]) || Bool(attributes["ai_translated"]), Double(attributes["fps"])));
        }

        return results;
    }

    /// <summary>Downloads one subtitle file (after <see cref="LoginAsync"/>): its name and content.</summary>
    public async Task<(string FileName, byte[] Content)> DownloadAsync(int fileId, CancellationToken cancellationToken = default)
    {
        if (_token is null)
        {
            throw new SubtitleServiceException("OpenSubtitles: na stiahnutie treba prihlásenie (meno a heslo v Nastaveniach).");
        }

        var answer = await SendAsync(HttpMethod.Post, "download", new JsonObject { ["file_id"] = fileId }, cancellationToken).ConfigureAwait(false);
        Remaining = answer["remaining"] is { } remaining ? Int(remaining) : null;
        var link = Text(answer["link"]) ?? throw new SubtitleServiceException("OpenSubtitles: " + (Text(answer["message"]) ?? "odkaz na stiahnutie chýba."));
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new SubtitleServiceException("OpenSubtitles: neplatný odkaz na stiahnutie.");
        }

        // the link is temporary and plain: no key, no login
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(userAgent);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SubtitleServiceException($"OpenSubtitles: súbor sa nepodarilo stiahnuť ({(int)response.StatusCode}).");
            }

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return (Text(answer["file_name"]) ?? $"{fileId}.srt", content);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SubtitleServiceException("OpenSubtitles: súbor sa nepodarilo stiahnuť – " + ex.Message, ex);
        }
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"https://{_host}/api/v1/{path}");
        request.Headers.Add("Api-Key", apiKey);
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_token is not null && path != "login")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

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
            throw new SubtitleServiceException("OpenSubtitles nie je dostupný: " + ex.Message, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? json = null;
            try
            {
                json = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // not JSON: the status says it
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = Text(json?["message"]) ?? json?["errors"]?.ToJsonString() ?? response.ReasonPhrase;
                throw new SubtitleServiceException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "OpenSubtitles: neplatný API kľúč alebo prihlásenie – " + message,
                    HttpStatusCode.NotAcceptable or (HttpStatusCode)429 => "OpenSubtitles: dnešný limit stiahnutí je vyčerpaný alebo príliš veľa požiadaviek – " + message,
                    _ => $"OpenSubtitles: chyba {(int)response.StatusCode} – {message}",
                });
            }

            return json ?? throw new SubtitleServiceException("OpenSubtitles: nečitateľná odpoveď.");
        }
    }

    // a string value, or null (never an exception on another type)
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number
        : node is JsonValue other && other.TryGetValue<double>(out var real) ? (int)real : 0;

    private static double? Double(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
