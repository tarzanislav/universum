using System.Net;
using System.Text;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Posters;

namespace Universum.Tests;

/// <summary>TMDB poster (owner, 31. 10. 2026): finding a title, choosing its poster, where folder.jpg goes – invented titles only.</summary>
public sealed class PosterTests
{
    private static readonly RequestPacer Fast = new(1000);

    [Test]
    public void PosterWithTheTitleCzechThenEnglishThenOriginal()
    {
        var posters = new List<TmdbPicture>
        {
            new("/bez.jpg", null, 2000, 3000, 9.5, 20),
            new("/en.jpg", "en", 2000, 3000, 8.0, 10),
            new("/ja.jpg", "ja", 2000, 3000, 7.0, 3),
            new("/cs-maly.jpg", "cs", 300, 450, 9.0, 5),
            new("/cs.jpg", "cs", 1000, 1500, 5.2, 1),
            new("/sk.jpg", "sk", 1000, 1500, 6.0, 2),
            new("/cs-zly.jpg", "cs", 1400, 2100, 2.0, 6),
        };
        var offer = PosterChoice.Offer(posters, new Dictionary<string, PictureLook>(), [35], "ja", latinOriginal: false);
        Assert.Equal("/cs.jpg", offer.Recommended?.Picture.Path, "Czech before English, a small one and one rated down are not recommended");
        Assert.True(offer.Opinion.StartsWith("Odporúčam český plagát", StringComparison.Ordinal), "the opinion – " + offer.Opinion);
        Assert.Sequence(["EN", "CZ", "SK"], offer.Groups.Select(g => g.Name), "groups: no original in another script, then EN, CZ, SK");
        Assert.True(offer.Groups.Single(g => g.Name == "CZ").Posters[0].Picture.Path == "/cs.jpg", "the acceptable Czech one first in its group");

        var english = PosterChoice.Offer(posters.Where(p => p.Language != "cs").ToList(), new Dictionary<string, PictureLook>(), [27], "ja", latinOriginal: false);
        Assert.Equal("/en.jpg", english.Recommended?.Picture.Path, "no Czech: English");
        var original = PosterChoice.Offer(posters.Where(p => p.Language is "ja" or null).ToList(), new Dictionary<string, PictureLook>(), [], "ja", latinOriginal: false);
        Assert.Equal("/ja.jpg", original.Recommended?.Picture.Path, "neither Czech nor English: the original language before a picture without text");
        var latin = PosterChoice.Offer([new("/fr.jpg", "fr", 1000, 1500, 6, 2), new("/en2.jpg", "en", 1000, 1500, 6, 2)], new Dictionary<string, PictureLook>(), [], "fr", latinOriginal: true);
        Assert.Sequence(["Originál (fr)", "EN"], latin.Groups.Select(g => g.Name), "an original in Latin script is offered first");
        Assert.True(PosterChoice.Offer([], new Dictionary<string, PictureLook>(), [], "en", true) is { Recommended: null, Groups.Count: 0 }, "no poster at all");
        var slovak = PosterChoice.Offer([new("/sk2.jpg", "sk", 1000, 1500, 6, 2)], new Dictionary<string, PictureLook>(), [], "en", true);
        Assert.True(slovak.Recommended?.Picture.Path == "/sk2.jpg" && slovak.Opinion.Contains("(sk)", StringComparison.Ordinal), "only a Slovak one: recommended – " + slovak.Opinion);
    }

    [Test]
    public void ColoursAsTheGenreSuits()
    {
        var colourful = new PictureLook(95, 0.6, 0.2);
        var dark = new PictureLook(40, 0.2, 0.3);
        Assert.True(PosterChoice.MoodOf([27, 35]) == PosterMood.Dark && PosterChoice.MoodOf([10749]) == PosterMood.Bright && PosterChoice.MoodOf([18]) == PosterMood.Neutral,
            "a horror comedy is dark, a romance bright, a drama neutral");
        Assert.True(PosterChoice.Fit(colourful, PosterMood.Bright) > PosterChoice.Fit(dark, PosterMood.Bright), "a comedy: the colourful one");
        Assert.True(PosterChoice.Fit(dark, PosterMood.Dark) > PosterChoice.Fit(colourful, PosterMood.Dark), "a horror: the dark, hard one");
        var posters = new List<TmdbPicture> { new("/a.jpg", "en", 2000, 3000, 6, 4), new("/b.jpg", "en", 2000, 3000, 6, 4) };
        var looks = new Dictionary<string, PictureLook> { ["/a.jpg"] = dark, ["/b.jpg"] = colourful };
        Assert.Equal("/b.jpg", PosterChoice.Offer(posters, looks, [35], "en", true).Recommended?.Picture.Path, "comedy");
        Assert.Equal("/a.jpg", PosterChoice.Offer(posters, looks, [27], "en", true).Recommended?.Picture.Path, "horror");

        // grey, then a picture of two strong colours
        var grey = Enumerable.Repeat((byte)128, 16 * 4).ToArray();
        var vivid = Enumerable.Range(0, 16).SelectMany(i => i % 2 == 0 ? new byte[] { 0, 0, 255, 255 } : [255, 0, 0, 255]).ToArray();
        Assert.True(PictureLooks.Measure(grey, 4, 4, 16).Colorfulness < 1 && PictureLooks.Measure(vivid, 4, 4, 16).Colorfulness > 90, "colourfulness measured");
    }

    [Test]
    public async Task TitlesFoundByNameYearIdAndTypo()
    {
        var server = new Server();
        server.Answers["/3/search/multi"] = """
            { "results": [
              { "media_type": "movie", "id": 101, "title": "Vymyslený kapor", "original_title": "Invented Carp", "release_date": "2021-05-01", "popularity": 5 },
              { "media_type": "tv", "id": 202, "name": "Vymyslený kapor", "original_name": "Invented Carp", "first_air_date": "1999-01-01", "popularity": 50 },
              { "media_type": "person", "id": 9, "name": "Niekto" } ] }
            """;
        server.Answers["/3/movie/303"] = """{ "id": 303, "title": "Tri", "original_title": "Three", "release_date": "2003-01-01" }""";
        server.Answers["/3/find/tt0000303"] = """{ "movie_results": [ { "id": 303, "title": "Tri", "original_title": "Three", "release_date": "2003-01-01" } ], "tv_results": [] }""";
        var finder = new TitleFinder(new TmdbClient(server.Http, "abc123", pacer: Fast));

        var both = await finder.FindAsync("Vymyslený kapor", null);
        Assert.True(both.Count == 2 && both.All(f => f.OriginalTitle == "Invented Carp"), "a film and a series, no person – " + string.Join(", ", both.Select(f => f.Caption)));
        var film = await finder.FindAsync("Invented Carp (2021)", null);
        Assert.True(film is [{ Kind: MediaKind.Movie, Id: 101, Year: 2021 }], "the year chooses – " + string.Join(", ", film.Select(f => f.Caption)));
        Assert.True(await finder.FindAsync("tmdbid-303", MediaKind.Movie) is [{ Id: 303 }], "by TMDB ID");
        await finder.FindAsync("Vymyslený kapor 2049", MediaKind.Movie);
        Assert.True(server.Requests.Any(r => Uri.UnescapeDataString(r).Contains("query=Vymyslený kapor 2049", StringComparison.Ordinal)),
            "a number at the end that is not the year: the whole name searched too");
        Assert.True(await finder.FindAsync("https://www.imdb.com/title/tt0000303/", null) is [{ Id: 303, Kind: MediaKind.Movie }], "by IMDb ID");

        // a typo: nothing for the name as written, found by the beginning of the word and the similarity of the names
        server.Answers.Remove("/3/search/multi");
        server.ByAddress = uri => uri.AbsolutePath == "/3/search/multi" && Uri.UnescapeDataString(uri.Query).Contains("query=inv", StringComparison.Ordinal)
            ? """
              { "results": [ { "media_type": "movie", "id": 404, "title": "Inventor", "original_title": "Inventor", "release_date": "2010-01-01" },
                             { "media_type": "movie", "id": 101, "title": "Invented", "original_title": "Invented", "release_date": "2021-01-01" } ] }
              """
            : uri.AbsolutePath.StartsWith("/3/search/", StringComparison.Ordinal) ? """{ "results": [] }""" : null;
        var typo = await finder.FindAsync("Invetned", null);
        Assert.True(typo.Count > 0 && typo[0].Id == 101, "a swapped pair of letters – " + string.Join(", ", typo.Select(f => f.Caption)));
        Assert.True(TitleFinder.Similarity("vymsyleny", "Ten Vymyslený") < TitleFinder.Similarity("vymsyleny", "Vymyslený") && TitleFinder.Similarity("Vymyslený", "vymysleny") == 1, "similarity");
        Assert.True(TitleFinder.NameAndYear("Vymyslený kapor (2021)") == ("Vymyslený kapor", 2021) && TitleFinder.NameAndYear("2077") == ("2077", null), "name and year");
    }

    [Test]
    public async Task DetailNamesPeopleCollectionAndPosters()
    {
        var server = new Server();
        server.Answers["/3/movie/101"] = """
            { "id": 101, "title": "Invented Carp", "original_title": "Vymyšlený kapr", "original_language": "cs", "release_date": "2021-05-01", "runtime": 136,
              "vote_average": 7.4, "vote_count": 1200, "genres": [ { "id": 35 }, { "id": 18 } ], "overview": "An invented carp.", "poster_path": "/p.jpg",
              "belongs_to_collection": { "id": 77, "name": "Vymyslené kapre" },
              "credits": { "cast": [ { "name": "Herec Prvý" }, { "name": "Herečka Druhá" } ], "crew": [ { "job": "Director", "name": "Režisér Jeden" }, { "job": "Writer", "name": "Iný" } ] },
              "external_ids": { "imdb_id": "tt0000101" },
              "translations": { "translations": [
                { "iso_639_1": "cs", "iso_3166_1": "CZ", "data": { "title": "Vymyšlený kapr", "overview": "Vymyšlený kapr plave." } },
                { "iso_639_1": "sk", "iso_3166_1": "SK", "data": { "title": "Vymyslený kapor", "overview": "" } } ] } }
            """;
        server.Answers["/3/movie/101/images"] = """
            { "posters": [ { "file_path": "/cs.jpg", "iso_639_1": "cs", "width": 1000, "height": 1500, "vote_average": 5.3, "vote_count": 2 },
                           { "file_path": "zly", "iso_639_1": "en", "width": 1, "height": 1 } ] }
            """;
        server.Answers["/3/collection/77"] = """
            { "name": "Vymyslené kapre", "parts": [ { "id": 102, "title": "Carp 2", "original_title": "Kapr 2", "release_date": "2024-01-01" },
                                                    { "id": 101, "title": "Invented Carp", "original_title": "Vymyšlený kapr", "release_date": "2021-05-01" } ] }
            """;
        var details = new TmdbTitleDetails(new TmdbClient(server.Http, "abc123", pacer: Fast));
        var d = await details.GetAsync(MediaKind.Movie, 101);
        Assert.True(d is { OriginalTitle: "Vymyšlený kapr", EnglishTitle: "Invented Carp", CzechTitle: "Vymyšlený kapr", SlovakTitle: "Vymyslený kapor", Runtime: 136, Year: 2021,
                    Imdb: "tt0000101", CollectionId: 77, Overview: "Vymyšlený kapr plave.", EnglishOverview: "An invented carp." }, "the detail");
        Assert.Sequence(["Režisér Jeden"], d!.Directors, "directors");
        Assert.Sequence(["Herec Prvý", "Herečka Druhá"], d.Cast, "actors");
        Assert.Equal("komédia, dráma", TmdbGenres.Join(d.Genres), "genres in Slovak");
        Assert.True(server.Requests.Single().Contains("append_to_response=credits,external_ids,translations", StringComparison.Ordinal), "one request");
        var posters = await details.PostersAsync(MediaKind.Movie, 101, d.OriginalLanguage);
        Assert.True(posters is [{ Path: "/cs.jpg", Language: "cs", Width: 1000 }], "posters, a bad path left out");
        Assert.True(server.Requests[^1].Contains("include_image_language=cs,sk,en,null", StringComparison.Ordinal), "the languages asked – " + server.Requests[^1]);
        var collection = await details.CollectionAsync(77);
        Assert.Sequence(["Vymyšlený kapr (2021)", "Kapr 2 (2024)"], collection!.Parts.Select(p => p.Caption), "the collection by years");
        Assert.True(d.Title?.FolderName == "Vymyšlený kapr (2021)", "the library name");
    }

    [Test]
    public void GenresInSlovak()
    {
        Assert.Equal("dráma, sci-fi", TmdbGenres.Translate("Drama, Science-Fiction"), "TVmaze");
        Assert.Equal("komédia, dráma", TmdbGenres.Translate("Komedie / Drama"), "a Czech page");
        Assert.Equal("akčný, dobrodružný, sci-fi, fantasy", TmdbGenres.Join([10759, 10765]), "TMDB series genres, each name once");
        Assert.Equal("", TmdbGenres.Translate(null), "none");
    }

    [Test]
    public void FolderJpgInTheMainFolderAndTheOldOneKept()
    {
        var root = Directory.CreateTempSubdirectory("universum-poster-").FullName;
        try
        {
            var library = new LibraryLayout(Path.Combine(root, "OUTPUT"));
            library.EnsureCreated();
            Assert.True(Directory.Exists(library.Posters) && Path.GetFileName(library.Posters) == "POSTER", "POSTER beside MOVIES and TVSHOWS");
            var input = Path.Combine(root, "INPUT");
            Directory.CreateDirectory(Path.Combine(input, "nove"));
            File.WriteAllText(Path.Combine(input, "nove", "Vymysleny.Serial.S01E02.1080p.mkv"), "");
            File.WriteAllText(Path.Combine(input, "Iny.Film.2020.mkv"), "");
            var places = new PosterPlaces(library, input);
            var film = new TitleInfo(MediaKind.Movie, "Vymyslený film", 2026);
            var series = new TitleInfo(MediaKind.Series, "Vymyslený seriál", 2025);

            var first = PosterPlaces.Save(places.PosterFolder(film), [1, 2, 3]);
            Assert.True(first is { Done: true, KeptAs: null } && File.ReadAllBytes(Path.Combine(library.Posters, "Vymyslený film (2026)", "folder.jpg")).Length == 3, "POSTER\\Name (year)\\folder.jpg");
            var second = PosterPlaces.Save(places.PosterFolder(film), [4, 5]);
            var third = PosterPlaces.Save(places.PosterFolder(film), [6]);
            var folder = places.PosterFolder(film);
            Assert.True(second.KeptAs == "folder-povodny.jpg" && third.KeptAs == "folder-povodny-2.jpg" && File.ReadAllBytes(Path.Combine(folder, "folder.jpg")).SequenceEqual(new byte[] { 6 }) &&
                        File.ReadAllBytes(Path.Combine(folder, "folder-povodny.jpg")).Length == 3, "an older folder.jpg renamed, never lost");

            Assert.True(places.LibraryFolder(film, 1) is null, "the film is not processed");
            Directory.CreateDirectory(Path.Combine(library.Shows, "Vymyslený seriál (2025) [tmdbid-55]", "Season 01"));
            Assert.True(places.LibraryFolder(series, 55) is { } main && main.EndsWith("[tmdbid-55]", StringComparison.Ordinal), "a series folder found by its TMDB ID (the main folder)");
            Assert.True(places.InInput(MediaKind.Series, ["Vymyslený seriál"], 2025) is { } episode && episode.EndsWith(".mkv", StringComparison.Ordinal), "an episode on the input");
            Assert.True(places.InInput(MediaKind.Movie, ["Iný film"], 2026) is null && places.InInput(MediaKind.Movie, ["Iný film"], 2020) is not null, "a film by its year");
            Assert.True(places.InInput(MediaKind.Movie, ["Iný"], null) is null, "a longer name is another title");

            // waiting until the film is processed: then in its main folder, an older one renamed
            var waiting = new PendingPosters(Path.Combine(root, "waiting"), () => library.Root);
            waiting.Add(film, 7, [9, 9]);
            waiting.Add(series, 55, [8]);
            Assert.True(waiting.All.Count == 2, "two posters wait");
            Assert.True(waiting.Deliver(Path.Combine(library.Movies, "Iný (2020)")) is null, "another film: nothing");
            var originals = Path.Combine(root, "INPUT", "MOVIES", "Vymyslený film (2026)");
            Directory.CreateDirectory(originals);
            Assert.True(waiting.Deliver(originals) is null && !File.Exists(Path.Combine(originals, "folder.jpg")), "never into a folder of originals named like the library");
            var filmFolder = library.TitleDirectory(film);
            Directory.CreateDirectory(filmFolder);
            File.WriteAllBytes(Path.Combine(filmFolder, "folder.jpg"), [1]);
            var done = waiting.Deliver(filmFolder);
            Assert.True(done is not null && done.Contains("folder-povodny.jpg", StringComparison.Ordinal) && File.ReadAllBytes(Path.Combine(filmFolder, "folder.jpg")).Length == 2, "delivered – " + done);
            var seasonFolder = Path.Combine(library.Shows, "Vymyslený seriál (2025) [tmdbid-55]", "Season 01");
            Assert.True(waiting.Deliver(seasonFolder) is not null && File.Exists(Path.Combine(library.Shows, "Vymyslený seriál (2025) [tmdbid-55]", "folder.jpg")) &&
                        !File.Exists(Path.Combine(seasonFolder, "folder.jpg")), "a series: the main folder, never the season folder");
            Assert.True(waiting.All.Count == 0, "nothing waits any more");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task DraftsByOpenAiOrCloudflare()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0 };
        var server = new Server();
        server.ByAddress = uri => uri.Host switch
        {
            "api.openai.com" => $$"""{ "data": [ { "b64_json": "{{Convert.ToBase64String(jpeg)}}" } ] }""",
            "api.cloudflare.com" => $$"""{ "result": { "image": "{{Convert.ToBase64String(jpeg)}}" }, "success": true }""",
            _ => null,
        };
        var details = new TmdbDetails
        {
            Kind = MediaKind.Movie, Id = 1, OriginalTitle = "Vymyšlený kapr", EnglishTitle = "Invented Carp", CzechTitle = "Vymyšlený kapr", Year = 2021, Genres = [27],
            EnglishOverview = "A carp invented for a test.", Cast = ["Herec Prvý"],
        };
        var prompt = PosterDrafts.Prompt(details);
        Assert.True(prompt.Contains("\"Vymyšlený kapr\"", StringComparison.Ordinal) && prompt.Contains("horror", StringComparison.Ordinal) && prompt.Contains("2:3", StringComparison.Ordinal) &&
                    prompt.Contains("A carp invented", StringComparison.Ordinal) && !prompt.Contains("Herec", StringComparison.Ordinal) && prompt.Contains("dark", StringComparison.Ordinal),
            "the prompt: Czech title, genre, plot, mood, no actors – " + prompt);
        var drafts = new PosterDrafts(server.Http);
        var none = await drafts.CreateAsync(details, new DraftKeys(null, null, null));
        Assert.True(none.Picture is null && none.Problem!.Contains("Cloudflare", StringComparison.Ordinal), "no keys: said");
        var paid = await drafts.CreateAsync(details, new DraftKeys(new string('a', 32), new string('t', 40), "sk-" + new string('k', 40)));
        Assert.True(paid is { Service: "OpenAI", Picture.Length: 10 } && server.Requests[^1].Contains("api.openai.com", StringComparison.Ordinal), "the paid key wins");
        Assert.True(server.Bodies[^1].Contains("\"size\":\"1024x1536\"", StringComparison.Ordinal) && server.Authorization == "Bearer sk-" + new string('k', 40), "portrait, the key as a bearer");
        var free = await drafts.CreateAsync(details, new DraftKeys(new string('a', 32), new string('t', 40), null));
        Assert.True(free is { Service: "Cloudflare", Picture.Length: 10 } && server.Requests[^1].EndsWith("/ai/run/@cf/black-forest-labs/flux-2-klein-4b", StringComparison.Ordinal), "free: Cloudflare");
        server.Status = HttpStatusCode.TooManyRequests;
        var limited = await drafts.CreateAsync(details, new DraftKeys(new string('a', 32), new string('t', 40), null));
        Assert.True(limited.Picture is null && limited.Problem!.Contains("limit", StringComparison.Ordinal), "the daily limit said – " + limited.Problem);
    }

    [Test]
    public void RatingsOfOmdb()
    {
        var ratings = OmdbClient.Parse("""
            { "Response": "True", "imdbRating": "8.7", "imdbVotes": "2,100,000", "Metascore": "73",
              "Ratings": [ { "Source": "Internet Movie Database", "Value": "8.7/10" }, { "Source": "Rotten Tomatoes", "Value": "88%" } ] }
            """);
        Assert.True(ratings is { Imdb: "8,7", RottenTomatoes: "88 %", Metacritic: "73" }, $"IMDb, Rotten Tomatoes, Metacritic – {ratings}");
        Assert.True(OmdbClient.Parse("""{ "Response": "False", "Error": "Movie not found!" }""") is null && OmdbClient.Parse("""{ "Response": "True", "imdbRating": "N/A" }""")!.IsEmpty, "missing");
        Assert.True(OmdbClient.IsValidKey("a1b2c3d4") && !OmdbClient.IsValidKey("bad key"), "the key");
    }

    /// <summary>Answers requests by path (or by the whole address); records them.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public Server() => Http = new HttpClient(this);

        public HttpClient Http { get; }

        public Dictionary<string, string> Answers { get; } = [];

        public Func<Uri, string?>? ByAddress { get; set; }

        public List<string> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public string? Authorization { get; private set; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Authorization = request.Headers.Authorization?.ToString();
            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status) { Content = new StringContent("""{ "error": { "message": "Rate limit reached" } }""", Encoding.UTF8, "application/json") };
            }

            return (ByAddress?.Invoke(request.RequestUri!) ?? Answers.GetValueOrDefault(request.RequestUri!.AbsolutePath)) is { } body
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
