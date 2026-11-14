using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Universum.App.Pages;
using Universum.App.Setup;
using Universum.App.Shell;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Platform.Windows;

namespace Universum.App.SelfTesting;

// the self-test: the titles – HISTRA, the lists, the posters
internal static partial class SelfTest
{
    /// <summary>
    /// TMDB checked in HISTRA (synchronisation, 27. 10. 2026) on the rules page, with invented answers of both services
    /// (nothing goes to the internet): another title in HISTRA waits for confirmation; the same title is confirmed; a name
    /// found nowhere waits as a suggestion; a typed title is the user's decision. Only the name goes to HISTRA.
    /// </summary>
    private static async Task TestTitleSyncAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var fake = new FakeTitleServices();
        var http = new System.Net.Http.HttpClient(fake);
        TitleSearch Fresh() => new(
            new Core.Library.Tmdb.TmdbClient(http, "selftest-key", pacer: new RequestPacer(100)),
            new Core.Library.Histra.HistraCatalog(new Core.Library.Histra.HistraClient(http, "hst_selftest-0123456789", new RequestPacer(100))));
        var rules = model.Rules;
        model.SelectedTool = model.Tools.First(t => t.Page == rules);
        rules.IsMovie = true;

        // TMDB finds one title, HISTRA another of the same name and year: the title waits for confirmation
        fake.Tmdb = """{"id":9500,"title":"Vymyslený vlak","original_title":"Vymyslený vlak","release_date":"2019-02-01"}""";
        fake.Histra = """{"id":77,"title":"Vymyslený vlak","original_title":"Vymyslený vlak","release_date":"2019-05-30","external_ids":{"tmdb":"9001"}}""";
        services.TitlesForSelfTest = Fresh();
        rules.Tmdb.Refresh();
        rules.TitleText = "Vymyslený vlak (2019)";
        await rules.Tmdb.SearchAsync();
        Check(rules.Tmdb.NeedsConfirmation && rules.Tmdb.Check == TitleCheck.Different && rules.Target == rules.Tmdb.Warning &&
              rules.Tmdb.Warning.Contains("9001", StringComparison.Ordinal) && !rules.ApplyCommand.CanExecute(null),
            "titles: HISTRA found another title – waits for confirmation: " + rules.Target);
        await Screenshot(window, "title-unconfirmed.png");
        rules.Tmdb.ConfirmCommand.Execute(null);
        Check(!rules.Tmdb.NeedsConfirmation && rules.Target.StartsWith(Path.Combine("MOVIES", "Vymyslený vlak (2019)"), StringComparison.Ordinal),
            "titles: confirmed by the user – " + rules.Target);
        Check(fake.Requests.Where(r => r.Contains("histra.net", StringComparison.Ordinal)).All(r => r == "https://histra.net/api/v1/catalog/movies/search?q=Vymyslený vlak"),
            "titles: only the name goes to HISTRA – " + string.Join(" | ", fake.Requests));

        // the same title in HISTRA: confirmed
        fake.Tmdb = """{"id":9002,"title":"Vymyslený vlak 2","original_title":"Vymyslený vlak 2","release_date":"2021-03-01"}""";
        fake.Histra = """{"id":12,"title":"Vymyslený vlak 2","original_title":"Vymyslený vlak 2","release_date":"2021-03-01","external_ids":{"tmdb":"9002"}}""";
        services.TitlesForSelfTest = Fresh();
        rules.TitleText = "Vymyslený vlak 2 (2021)";
        await rules.Tmdb.SearchAsync();
        Check(!rules.Tmdb.NeedsConfirmation && rules.Tmdb.Check == TitleCheck.Confirmed && rules.Tmdb.Message.Contains("potvrdila", StringComparison.Ordinal) &&
              rules.Target.StartsWith(Path.Combine("MOVIES", "Vymyslený vlak 2 (2021)"), StringComparison.Ordinal),
            "titles: HISTRA confirmed the title – " + rules.Tmdb.Message);

        // found nowhere: the suggestion waits; a title typed by the user is the user's decision
        fake.Tmdb = "";
        fake.Histra = "";
        services.TitlesForSelfTest = Fresh();
        rules.TitleText = "Neexistujúci vlak (2020)";
        await rules.Tmdb.SearchAsync();
        Check(rules.Tmdb.NeedsConfirmation && rules.Tmdb.Warning.Contains("nenašli", StringComparison.Ordinal) && rules.Target == rules.Tmdb.Warning,
            "titles: found nowhere – the suggestion waits: " + rules.Tmdb.Warning);
        rules.TitleText = "Neexistujúci vlak (2021)";
        Check(!rules.Tmdb.NeedsConfirmation && rules.Target.StartsWith(Path.Combine("MOVIES", "Neexistujúci vlak (2021)"), StringComparison.Ordinal),
            "titles: a typed title needs no confirmation – " + rules.Target);
        rules.TitleText = "Neexistujúci vlak (2020)";
        Check(rules.Tmdb.NeedsConfirmation && rules.Target == rules.Tmdb.Warning, "titles: the text that waited waits again when typed back");

        // HISTRA set up but not reachable: TMDB's title is not checked and waits for confirmation
        fake.Tmdb = """{"id":9003,"title":"Vymyslený vlak 3","original_title":"Vymyslený vlak 3","release_date":"2023-03-01"}""";
        fake.HistraStatus = System.Net.HttpStatusCode.Unauthorized;
        services.TitlesForSelfTest = Fresh();
        rules.TitleText = "Vymyslený vlak 3 (2023)";
        await rules.Tmdb.SearchAsync();
        Check(rules.Tmdb.NeedsConfirmation && rules.Tmdb.Check == TitleCheck.Unavailable && rules.Tmdb.Warning.Contains("nedala opýtať", StringComparison.Ordinal),
            "titles: HISTRA not reachable – waits for confirmation: " + rules.Tmdb.Warning);
        fake.HistraStatus = System.Net.HttpStatusCode.OK;

        services.TitlesForSelfTest = null;
        rules.Tmdb.Refresh();
        rules.TitleText = "Vymyslený film: Návrat (2026)";
    }

    /// <summary>
    /// Listy z HISTRA with invented lists (HISTRA and the poster server answer here, nothing goes to the internet): the
    /// import window with preview posters, a list read in pages, only GET requests, the detail with IDs and links,
    /// synchronisation keeping a list HISTRA no longer has, removal, and the lists kept for the next start.
    /// </summary>
    private static async Task TestHistraListsAsync(MainViewModel model, MainWindow window, AppServices services)
    {
        var page = model.HistraLists;
        model.Open(page);
        Check(!page.CanUse && page.Requirement.Contains("token HISTRA", StringComparison.Ordinal) && page.IsEmpty, "lists: a HISTRA token is needed – " + page.Requirement);

        var histra = new FakeHistra();
        var http = new System.Net.Http.HttpClient(histra);
        services.HistraListsForSelfTest = new Core.Library.Histra.HistraLists(new Core.Library.Histra.HistraClient(http, "hst_selftest-0123456789", new RequestPacer(1000)));
        services.PostersForSelfTest = new PosterCache(http, Path.Combine(_outputDirectory, "posters"), PosterCache.DefaultServer);
        page.Refresh();
        Check(page.CanUse && page.ImportCommand.CanExecute(null), "lists: import possible with HISTRA");

        var import = await page.PrepareImportAsync();
        Check(import is { Rows.Count: 3 } && import.Rows.All(r => !r.IsChecked) && import.Rows[0].Posters.Count == 5,
            "lists: the overview of the lists with five preview posters – " + page.Status);
        if (import is null)
        {
            return;
        }

        var importWindow = new HistraImportWindow(import) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        importWindow.Show();
        await WaitFor(() => import.Rows.SelectMany(r => r.Posters).All(p => p.Image is not null));
        await Screenshot(importWindow, "histra-import.png");
        importWindow.Close();

        import.Rows[0].IsChecked = true;
        import.Rows[1].IsChecked = true;
        Check(import.ImportText == "Importovať vybrané (2)", "lists: two lists chosen – " + import.ImportText);
        await page.ImportAsync(import.Chosen);
        Check(page.Lists.Count == 2 && page.Lists[0].All.Count == 30 && page.Lists[1].All.Count == 3,
            $"lists: imported with all titles, read in pages – {page.Status}");
        Check(histra.Requests.All(r => r.Method == "GET") && histra.Requests.Count(r => r.Address.Contains("/api/v1/lists/1?", StringComparison.Ordinal)) == 2,
            "lists: only read from HISTRA, the long list in pages – " + string.Join(" | ", histra.Requests.Select(r => $"{r.Method} {r.Address}")));
        Check(page.Lists[0].Visible.Count == HistraListRow.Collapsed && page.Lists[0].ExpandText.StartsWith("Zobraziť všetky (30)", StringComparison.Ordinal),
            "lists: the first posters, then Zobraziť všetky – " + page.Lists[0].ExpandText);
        Check(File.Exists(AppSettings.HistraListsFile), "lists: kept in a file of this account");
        var second = page.Lists[1];
        Check(!page.Lists[0].MoveUpCommand.CanExecute(null) && page.Lists[0].MoveDownCommand.CanExecute(null) && !second.MoveDownCommand.CanExecute(null),
            "lists: the arrows at the ends of the order");
        second.MoveUpCommand.Execute(null);
        Check(page.Lists[0] == second && new HistraListsPageViewModel(services).Lists[0].Name == second.Name && second.MoveDownCommand.CanExecute(null),
            "lists: moved up, the order kept for the next start – " + page.Status);
        page.MoveTo(second, 1);
        Check(page.Lists[1] == second, "lists: moved back (as when dragged with the mouse)");

        page.Filter = "pribeh 2";
        Check(page.Lists[0].Visible.Count == 9 && page.Lists[0].FilterResult == "Zhoda: 9", "lists: search without diacritics – " + page.Lists[0].FilterResult);
        page.Filter = "";

        var opened = new List<Uri>();
        var browser = Browser.Open;
        Browser.Open = opened.Add;
        var film = page.Lists[0].All[0];
        film.OpenCommand.Execute(null);
        Check(page.Selected == film && film.Caption == "Invented Story 1 (1991)" && film.LocalTitle == "Vymyslený príbeh 1",
            $"lists: the detail of a title – {film.Caption} / {film.LocalTitle}");
        Check(page.Card is { MainName: "Invented Story 1 (1991)" } card && card.OtherNames.SequenceEqual(["Vymyslený príbeh 1 (1991)"]) &&
              card.Links.Any(l => l.Text == "Trakt" && l.Address.AbsoluteUri == "https://trakt.tv/search/tmdb/9001?id_type=movie") && card.Ids.Count == film.Ids.Count,
            $"lists: the card of the title (names, Trakt, IDs) – {page.Card?.MainName} | {string.Join(", ", page.Card?.Links.Select(l => l.Text) ?? [])}");
        Check(string.Join(", ", film.Links.Select(l => $"{l.Text} {l.Address.AbsoluteUri}")) ==
              "HISTRA https://histra.net/movie/1001, TMDB https://www.themoviedb.org/movie/9001, IMDb https://www.imdb.com/title/tt0009001/, " +
              "ČSFD (hľadať) https://www.csfd.cz/hledat/?q=Invented%20Story%201",
            "lists: links of the title – " + string.Join(", ", film.Links.Select(l => $"{l.Text} {l.Address.AbsoluteUri}")));
        Check(string.Join(", ", film.Ids.Select(i => $"{i.Name} {i.Value}")) == "HISTRA 1001, TMDB 9001, IMDb tt0009001, Trakt 5001",
            "lists: IDs of the title – " + string.Join(", ", film.Ids.Select(i => $"{i.Name} {i.Value}")));
        film.Links[0].OpenCommand.Execute(null);
        Check(opened.Count == 1 && opened[0].AbsoluteUri == "https://histra.net/movie/1001", "lists: the HISTRA link opens the title's page");
        Browser.Open = browser;

        // the title shown goes on (owner, 31. 10. 2026): to TMDB poster by its TMDB ID, to processing by its names and year
        var toPoster = page.OpenInPoster;
        var toSingle = page.SendToSingle;
        (MediaKind Kind, int Id)? posterAsked = null;
        (IReadOnlyList<string> Names, int? Year, string Caption)? singleAsked = null;
        page.OpenInPoster = (kind, id) => { posterAsked = (kind, id); return Task.CompletedTask; };
        page.SendToSingle = (kind, names, year, caption) => { singleAsked = (names, year, caption); return Task.FromResult<string?>("vymyslené: nenašiel sa"); };
        await page.ToPosterCommand.ExecuteAsync(null);
        await page.ToSingleCommand.ExecuteAsync(null);
        Check(posterAsked == (MediaKind.Movie, 9001) && singleAsked is { } asked && asked.Names.Contains("Invented Story 1") && asked.Year == 1991 &&
              page.Status == "vymyslené: nenašiel sa",
            $"lists: Otvoriť v TMDB Poster and Do Single spracovania – {posterAsked} | {(singleAsked is { } a ? string.Join("/", a.Names) + " " + a.Year + " " + a.Caption : "")} | {page.Status}");
        page.OpenInPoster = toPoster;
        page.SendToSingle = toSingle;
        await WaitFor(() => page.Lists.SelectMany(l => l.Visible).All(r => r.SmallPoster.Image is not null) && film.LargePoster.Image is not null);
        await Screenshot(window, "histra-lists.png");

        histra.Deleted.Add(2);
        await page.SyncAsync();
        Check(page.Lists.Count == 2 && page.Lists[1].Missing && page.Lists[1].All.Count == 3 && page.Status.Contains("v HISTRA už nie je: 1", StringComparison.Ordinal),
            "lists: a list HISTRA no longer has stays, marked – " + page.Status);
        page.Confirm = _ => true;
        page.Lists[1].RemoveCommand.Execute(null);
        Check(page.Lists.Count == 1, "lists: removed from UNIVERSUM");
        var again = new HistraListsPageViewModel(services);
        Check(again.Lists.Count == 1 && again.Lists[0].All.Count == 30 && again.Lists[0].All[0].Item.Ids.Tmdb == 9001, "lists: kept for the next start");

        services.HistraListsForSelfTest = null;
        services.PostersForSelfTest = null;
        page.Refresh();
        page.Selected = null;
    }

    /// <summary>
    /// HISTRA and its poster server with invented lists: three lists; the first has 30 titles, given at most 20 per page
    /// (as HISTRA cuts the limit), the second three; a deleted list answers 404. Posters are drawn pictures.
    /// </summary>
    // owner, 31. 10. 2026: TMDB poster – an invented film found, its card, the poster recommended, folder.jpg in POSTER (an
    // older one renamed), a draft by AI, merged into the film processed; invented answers, nothing goes to the internet
    private static async Task TestPostersAsync(MainViewModel model, MainWindow window, AppServices services, string library)
    {
        var fake = new FakePosters();
        var http = new System.Net.Http.HttpClient(fake);
        services.PosterTmdbForSelfTest = new Core.Library.Tmdb.TmdbClient(http, "selftest-key", null, new RequestPacer(1000));
        services.PostersForSelfTest = new PosterCache(http, Path.Combine(_outputDirectory, "posters-tmdb"), PosterCache.DefaultServer);
        services.DraftsForSelfTest = new Core.Posters.PosterDrafts(http);
        services.DraftKeysForSelfTest = new Core.Posters.DraftKeys(new string('a', 32), new string('t', 40), null);
        var page = model.Posters;
        model.SelectedTool = model.Tools.First(t => t.Page == page);
        page.Merge = false;
        page.Query = "Vymyslený plagát 2026";
        await page.SearchCommand.ExecuteAsync(null);
        await page.OfferReady;
        Check(page.Results.Count == 1 && page.Card?.Details is { OriginalTitle: "Vymyslený plagát" }, "posters: the film found and opened – " + page.SearchStatus);
        var card = page.Card!;
        Check(card.MainName == "Vymyslený plagát (2026)" && card.OtherNames.SequenceEqual(["Invented Poster (2026)", "Vymyšlený plakát (2026)"]) && card.Genre == "komédia" &&
              card.Facts == "Film · 1 h 40 min (100 min)" && card.Directors == "Vymyslená Režisérka" && card.HasCollection,
            $"posters: the card – {card.MainName} | {string.Join(" / ", card.OtherNames)} | {card.Facts} | {card.Genre} | {card.Directors} | {card.Collection.Count}");
        Check(page.Recommended?.Candidate?.Picture.Path == "/cz-plagat.png" && page.Selected == page.Recommended && page.Opinion.StartsWith("Odporúčam český plagát", StringComparison.Ordinal) &&
              page.Groups.Select(g => g.Name).SequenceEqual(["EN", "CZ"]), $"posters: the Czech one recommended, the groups – {page.Opinion} | {string.Join(", ", page.Groups.Select(g => g.Name))}");
        await Screenshot(window, "tmdb-poster.png");

        await page.ProcessCommand.ExecuteAsync(null);
        var folder = Path.Combine(library, "POSTER", "Vymyslený plagát (2026)");
        var jpeg = Path.Combine(folder, "folder.jpg");
        Check(File.Exists(jpeg) && File.ReadAllBytes(jpeg) is [0xFF, 0xD8, ..], "posters: folder.jpg (JPEG) in POSTER – " + page.Result);
        page.Groups.Single(g => g.Name == "EN").Posters[0].SelectCommand.Execute(null);
        await page.ProcessCommand.ExecuteAsync(null);
        Check(File.Exists(Path.Combine(folder, "folder-povodny.jpg")) && page.Result.Contains("folder-povodny.jpg", StringComparison.Ordinal), "posters: the older one renamed – " + page.Result);

        // owner, 7. 11. 2026: the English poster came as WebP – converted by UNIVERSUM's own decoder into a real JPEG, never
        // only renamed; a WebP saved earlier as folder.jpg is kept as folder-povodny.webp
        Check(File.ReadAllBytes(jpeg) is [0xFF, 0xD8, 0xFF, ..] && PictureTools.Show(File.ReadAllBytes(jpeg), 40) is not null, "posters: a WebP poster written as a real JPEG – " + page.Result);
        var converted = PictureTools.ToJpeg(FakePosters.WebP, 30, 90);
        using (var stream = new MemoryStream(converted))
        {
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Check(converted is [0xFF, 0xD8, 0xFF, ..] && frame.PixelHeight == 30 && frame.PixelWidth == 40, $"posters: WebP to JPEG, shrunk ({frame.PixelWidth} × {frame.PixelHeight})");
        }

        Check(PictureTools.ToPng(FakePosters.WebP) is [0x89, 0x50, 0x4E, 0x47, ..] && PictureTools.Show(FakePosters.WebP, 50) is not null, "posters: WebP to PNG (a logo) and on the screen");
        var thumbnail = Path.Combine(_outputDirectory, "webp-thumbnail.jpg");
        File.WriteAllBytes(thumbnail, FakePosters.WebP);
        Check(PictureTools.Thumbnail(thumbnail, 40) is not null && PictureTools.Look(thumbnail) is not null, "posters: a WebP under a .jpg name shown and measured by its content");
        File.WriteAllBytes(jpeg, FakePosters.WebP);
        await page.ProcessCommand.ExecuteAsync(null);
        Check(File.ReadAllBytes(jpeg) is [0xFF, 0xD8, 0xFF, ..] && File.Exists(Path.Combine(folder, "folder-povodny.webp")) && page.Result.Contains("folder-povodny.webp", StringComparison.Ordinal),
            "posters: an older WebP folder.jpg kept as folder-povodny.webp – " + page.Result);

        // Ďalšie TMDB obrázky (owner, 31. 10. 2026): off by default, nothing offered; then a block of their own – the background
        // and the logo chosen with the mouse, written by their own button (Spracovať poster writes only the poster)
        Check(!page.Extras && !File.Exists(Path.Combine(folder, "fanart.jpg")) && page.Backdrops.Count == 0 && !page.ProcessExtrasCommand.CanExecute(null),
            "posters: the other pictures are off by default, nothing offered");
        page.Extras = true;
        Check(page.Backdrops.Count > 0 && page.Logos.Count > 0 && page.SelectedBackdrop is { IsRecommended: true, IsSelected: true } && page.SelectedLogo is not null,
            $"posters: the backgrounds and logos offered, the best chosen ({page.Backdrops.Count}, {page.Logos.Count})");
        await page.ProcessCommand.ExecuteAsync(null);
        Check(!File.Exists(Path.Combine(folder, "fanart.jpg")), "posters: Spracovať poster writes only the poster – " + page.Result);
        page.Backdrops[^1].SelectCommand.Execute(null);
        Check(page.SelectedBackdrop == page.Backdrops[^1] && page.Backdrops.Count(b => b.IsSelected) == 1, "posters: another background chosen with the mouse");
        await Screenshot(window, "tmdb-poster-extras.png");
        await page.ProcessExtrasCommand.ExecuteAsync(null);
        Check(File.ReadAllBytes(Path.Combine(folder, "fanart.jpg")) is [0xFF, 0xD8, ..] && File.Exists(Path.Combine(folder, "clearlogo.png")) && page.ExtrasText.Contains("pozadie", StringComparison.Ordinal) &&
              page.ExtrasSizeText.Contains("1920 × 1080", StringComparison.Ordinal),
            $"posters: Spracovať ďalšie TMDB obrázky – fanart.jpg and clearlogo.png in POSTER – {page.ExtrasResult} | {page.ExtrasText} | {page.ExtrasSizeText}");

        await page.DraftCommand.ExecuteAsync(null);
        Check(page.Groups[0].Name == "Návrhy AI" && page.Selected?.Draft is not null && fake.Requests.Any(r => r.Contains("api.cloudflare.com", StringComparison.Ordinal)),
            "posters: a draft by AI (Cloudflare) – " + page.DraftStatus);

        // owner, 8. 11. 2026: a poster picked in the browser (ČSFD) offered first and chosen; processed like any other
        var picked = services.TmdbTray.Posters.Add(MediaKind.Movie, page.Card!.Details!.Id, InventedPoster(), Core.Library.Tmdb.TrayPosterSource.Csfd);
        await page.OpenAsync(MediaKind.Movie, page.Card!.Details!.Id);
        Check(page.Groups[0].Name == Localization.Loc.T("Zo zásobníka") && page.Selected?.Draft is not null && page.Selected.Label.StartsWith("ČSFD", StringComparison.Ordinal) &&
              page.Groups.Any(g => g.Name == "CZ"), $"posters: the poster from the tray first and chosen – {page.Opinion} | {string.Join(", ", page.Groups.Select(g => g.Name))}");
        await page.ProcessCommand.ExecuteAsync(null);
        Check(File.ReadAllBytes(jpeg) is [0xFF, 0xD8, 0xFF, ..] && page.Result.Length > 0, "posters: the poster from the tray processed – " + page.Result);
        foreach (var poster in services.TmdbTray.PostersOf(MediaKind.Movie, page.Card!.Details!.Id))
        {
            services.TmdbTray.Posters.Remove(poster);
        }

        Check(picked is not null, "posters: the tray kept the picked poster");

        // merged: the film processed (its folder in MOVIES) gets the poster
        var movie = Path.Combine(library, "MOVIES", "Vymyslený plagát (2026)");
        Directory.CreateDirectory(movie);
        page.Merge = true;
        await page.ProcessCommand.ExecuteAsync(null);
        Check(File.Exists(Path.Combine(movie, "folder.jpg")) && !File.Exists(Path.Combine(movie, "fanart.jpg")), "posters: merged into the film's folder – " + page.Result);
        page.Merge = false;
        page.ExtrasMerge = true;
        await page.ProcessExtrasCommand.ExecuteAsync(null);
        Check(File.Exists(Path.Combine(movie, "fanart.jpg")) && File.Exists(Path.Combine(movie, "clearlogo.png")),
            "posters: Zlúčiť ďalšie obrázky z TMDB s filmom/seriálom – to the film processed – " + page.ExtrasResult);
        page.ExtrasMerge = false;
        page.Extras = false;

        services.PosterTmdbForSelfTest = null;
        services.PostersForSelfTest = null;
        services.DraftsForSelfTest = null;
        services.DraftKeysForSelfTest = null;
    }

    // an invented poster (a JPEG of 200 × 300 in shades of blue)
    private static byte[] InventedPoster()
    {
        var bitmap = new WriteableBitmap(200, 300, 96, 96, PixelFormats.Bgr32, null);
        var pixels = new int[200 * 300];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = 0x203060 + (i / 200 % 150);
        }

        bitmap.WritePixels(new Int32Rect(0, 0, 200, 300), pixels, 200 * 4, 0);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
