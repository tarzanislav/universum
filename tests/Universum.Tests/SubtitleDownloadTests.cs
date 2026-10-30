using System.IO.Compression;
using System.Net;
using System.Text;
using Universum.Core.Subtitles.Download;

namespace Universum.Tests;

/// <summary>OpenSubtitles.com, svsub.net and files from the browser (owner, 30. 10. 2026) – invented answers, nothing goes to the internet.</summary>
public sealed class SubtitleDownloadTests
{
    [Test]
    public async Task OpenSubtitlesSearchAndDownload()
    {
        var handler = new Fake(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/login" => Json("""{"token":"jwt-123","base_url":"vip-api.opensubtitles.com","user":{"allowed_downloads":1000}}"""),
            "/api/v1/subtitles" => Json("""
                {"data":[{"id":"1","attributes":{"language":"en","download_count":12345,"hearing_impaired":true,"foreign_parts_only":false,"fps":23.976,
                 "release":"Vymysleny.Film.2026.1080p","files":[{"file_id":777,"file_name":"vymysleny.srt"}]}},
                 {"id":"2","attributes":{"language":"en","download_count":10,"files":[]}}]}
                """),
            "/api/v1/download" => Json("""{"link":"https://dl.example/file/777","file_name":"vymysleny.srt","remaining":9}"""),
            "/file/777" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1\r\n00:00:01,000 --> 00:00:02,000\r\nHello.\r\n") },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var client = new OpenSubtitlesClient(new HttpClient(handler), "kluc-123", "UNIVERSUM/0.3");
        var results = await client.SearchAsync(new OpenSubtitlesQuery("en", 12345, null, null, HearingImpaired: "only"));
        var search = handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/subtitles", StringComparison.Ordinal));
        Assert.Equal("?hearing_impaired=only&languages=en&order_by=download_count&tmdb_id=12345&type=movie", search.Uri.Query, "parameters in alphabetical order");
        Assert.True(search.Headers.Contains("Api-Key: kluc-123") && search.Headers.Contains("User-Agent: UNIVERSUM/0.3"), "the key and the name of the program: " + search.Headers);
        Assert.True(results is [{ FileId: 777, Language: "en", HearingImpaired: true, Downloads: 12345 }], "one result with a file: " + string.Join(" | ", results));

        await Assert.ThrowsAsync<SubtitleServiceException>(() => client.DownloadAsync(777), "a download needs the login");
        await client.LoginAsync("meno", "heslo");
        var (name, content) = await client.DownloadAsync(777);
        var download = handler.Requests.Single(r => r.Uri.AbsolutePath == "/api/v1/download");
        Assert.True(download.Uri.Host == "vip-api.opensubtitles.com" && download.Headers.Contains("Authorization: Bearer jwt-123") && download.Body == """{"file_id":777}""",
            "the account's server, the token, the file: " + download.Uri + " " + download.Body);
        var file = handler.Requests.Single(r => r.Uri.Host == "dl.example");
        Assert.True(!file.Headers.Contains("Api-Key") && !file.Headers.Contains("Authorization"), "the temporary link gets no key and no token");
        Assert.True(name == "vymysleny.srt" && Encoding.UTF8.GetString(content).Contains("Hello.", StringComparison.Ordinal) && client.Remaining == 9, "the file and the downloads left");

        var denied = new OpenSubtitlesClient(new HttpClient(new Fake(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"message":"Invalid API key"}""") })), "zly", "UNIVERSUM/0.3");
        var error = await Assert.ThrowsAsync<SubtitleServiceException>(() => denied.SearchAsync(new OpenSubtitlesQuery("en", null, "Vymyslený film", 2026)), "a wrong key");
        Assert.True(error.Message.Contains("neplatný API kľúč", StringComparison.Ordinal) && error.Message.Contains("Invalid API key", StringComparison.Ordinal), error.Message);
    }

    [Test]
    public async Task SvSubTranslatesAndDeletesTheJob()
    {
        var polls = 0;
        var handler = new Fake(request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("POST", "/api/v1/jobs") => Json("""{"id":"job-42","status":"queued"}"""),
            ("GET", "/api/v1/jobs/job-42") => Json(++polls < 2 ? """{"id":"job-42","status":"translating","progress":40}""" : """{"id":"job-42","status":"done","progress":100}"""),
            ("GET", "/api/v1/jobs/job-42/download") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1\r\n00:00:01,000 --> 00:00:02,000\r\nAhoj.\r\n") },
            ("DELETE", "/api/v1/jobs/job-42") => Json("{}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var states = new List<string>();
        var svsub = new SvSubClient(new HttpClient(handler), "svs_token123") { PollInterval = TimeSpan.Zero };
        var translated = await svsub.TranslateAsync("1\r\n00:00:01,000 --> 00:00:02,000\r\nHello.\r\n", "cs", "openai",
            new SyncProgress<string>(states.Add));
        Assert.True(translated.Contains("Ahoj.", StringComparison.Ordinal), "the translation");
        var create = handler.Requests.First();
        Assert.True(create.Headers.Contains("Authorization: Bearer svs_token123") && create.Body.Contains("\"fileName\":\"titulky.srt\"", StringComparison.Ordinal) &&
                    create.Body.Contains("\"provider\":\"openai\"", StringComparison.Ordinal) && create.Body.Contains("\"targetLang\":\"cs\"", StringComparison.Ordinal),
            "the token, a neutral file name, the provider and the language: " + create.Body);
        Assert.True(handler.Requests.Last().Method == "DELETE" && svsub.JobDeleted, "the job is deleted afterwards");
        Assert.True(states.Any(s => s.Contains("40 %", StringComparison.Ordinal)), "progress: " + string.Join(" | ", states));

        var noKey = new SvSubClient(new HttpClient(new Fake(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"no_key_for_provider"}""") })), "svs_token123");
        var error = await Assert.ThrowsAsync<SubtitleServiceException>(() => noKey.TranslateAsync("1\r\n00:00:01,000 --> 00:00:02,000\r\nHi.\r\n", "cs", "openai"), "no AI key at the service");
        Assert.True(error.Message.Contains("AI kľúč", StringComparison.Ordinal), error.Message);
        Assert.True(SvSubClient.IsValidToken("svs_abc123def") && !SvSubClient.IsValidToken("abc") && !SvSubClient.IsValidToken("svs_ab cd"), "token form");
    }

    [Test]
    public void FilesFromTheBrowserAndNamesBesideTheVideo()
    {
        var folder = Directory.CreateTempSubdirectory("universum-downloads-").FullName;
        try
        {
            var since = DateTime.UtcNow.AddSeconds(-5);
            File.WriteAllText(Path.Combine(folder, "stare.srt"), "x");
            File.SetLastWriteTimeUtc(Path.Combine(folder, "stare.srt"), DateTime.UtcNow.AddDays(-1));
            File.WriteAllText(Path.Combine(folder, "nove.srt"), "1\r\n00:00:01,000 --> 00:00:02,000\r\nNové.\r\n");
            File.WriteAllText(Path.Combine(folder, "film.mkv.crdownload"), "x");
            var zip = Path.Combine(folder, "titulky.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("Vymysleny.Film.cze.srt").Open()))
                {
                    writer.Write("1\r\n00:00:01,000 --> 00:00:02,000\r\nČeský.\r\n");
                }

                using (var writer = new StreamWriter(archive.CreateEntry("../../mimo.srt").Open()))
                {
                    writer.Write("1\r\n00:00:01,000 --> 00:00:02,000\r\nNie mimo.\r\n");
                }

                using (var writer = new StreamWriter(archive.CreateEntry("info.nfo").Open()))
                {
                    writer.Write("x");
                }
            }

            Assert.Sequence(["nove.srt", "titulky.zip"], SubtitleDownloads.NewFiles(folder, since).Select(Path.GetFileName).Order(), "new subtitle files and archives only");
            var unpacked = Path.Combine(folder, "rozbalene");
            var files = SubtitleDownloads.Unpack(zip, unpacked);
            Assert.True(files.Count == 2 && files.All(f => Path.GetDirectoryName(f) == unpacked) && !File.Exists(Path.Combine(folder, "..", "mimo.srt")),
                "only subtitle files, only into the folder: " + string.Join(", ", files));

            var video = Path.Combine(folder, "Vymyslený film (2026).mkv");
            Assert.Equal(Path.Combine(folder, "Vymyslený film (2026).cs.srt"), SubtitleDownloads.BesideVideo(video, "cs"), "the video's name and the language");
            File.WriteAllText(Path.Combine(folder, "Vymyslený film (2026).en.sdh.srt"), "x");
            Assert.Equal(Path.Combine(folder, "Vymyslený film (2026).en.sdh (2).srt"), SubtitleDownloads.BesideVideo(video, "en", hearingImpaired: true), "never over an existing file");
            Assert.Equal("https://www.titulky.com/?Fulltext=Vymyslen%C3%BD%20film", SubtitleDownloads.TitulkyCom("Vymyslený film").AbsoluteUri, "the search on titulky.com");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Method, Uri Uri, string Headers, string Body);

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = string.Join("\n", request.Headers.Select(h => $"{h.Key}: {string.Join(" ", h.Value)}"));
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Sent(request.Method.Method, request.RequestUri!, headers, body));
            return answer(request);
        }
    }
}
