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

// the self-test: the work in the background and the Zásobník TMDB
internal static partial class SelfTest
{
    /// <summary>
    /// Zásobník TMDB (owner, 8. 11. 2026): a page sent by UNIVERSUM's script to 127.0.0.1 with the key (and refused without
    /// it), the address universum:// (only TMDB pages taken), the pinned note, the choice beside a title field, reading the
    /// browser windows (a stand-in, then the real ones – they must only not fail). Invented titles only.
    /// </summary>
    // 14. 11. 2026 (step 1 of the review of the code): an error of work nobody waits for is written and told, not lost
    private static async Task TestBackgroundWorkAsync()
    {
        var logBefore = BackgroundWork.Log;
        var folder = Directory.CreateTempSubdirectory("universum-samotest-chyby-").FullName;
        BackgroundWork.Log = new Core.IO.ErrorLog(Path.Combine(folder, "chyby.log"));
        var told = new List<string>();
        void Told(string text)
        {
            lock (told)
            {
                told.Add(text);
            }
        }

        BackgroundWork.Failed += Told;
        try
        {
            static async Task FailingAsync()
            {
                await Task.Delay(20);
                throw new InvalidOperationException("Vymyslená chyba na pozadí");
            }

            static async Task CancelledAsync()
            {
                await Task.Delay(20);
                throw new OperationCanceledException();
            }

            BackgroundWork.Run(FailingAsync());
            BackgroundWork.Run(CancelledAsync());
            BackgroundWork.Run(Task.CompletedTask);
            for (var i = 0; i < 50 && told.Count == 0; i++)
            {
                await Task.Delay(50);
            }

            await Task.Delay(200);
            var log = File.Exists(BackgroundWork.Log.File) ? await File.ReadAllTextAsync(BackgroundWork.Log.File) : "";
            Check(told.Count == 1 && told[0].Contains("Vymyslená chyba na pozadí", StringComparison.Ordinal) && log.Contains("FailingAsync()", StringComparison.Ordinal) &&
                  log.Contains("Vymyslená chyba na pozadí", StringComparison.Ordinal) && !log.Contains("OperationCanceledException", StringComparison.Ordinal) &&
                  TrayToast.Shown.Any(t => t.Contains("Vymyslená chyba na pozadí", StringComparison.Ordinal)),
                "background: an error written to chyby.log and told, a cancellation not – " + string.Join(" | ", told));
            Check(Browser.IsWeb(new Uri("https://www.themoviedb.org/movie/111")) && Browser.IsWeb(new Uri("http://127.0.0.1:47219/universum.user.js")) &&
                  !Browser.IsWeb(new Uri("file:///C:/Windows/System32/vymyslene.exe")) && !Browser.IsWeb(new Uri("ms-settings:display")),
                "browser: only addresses of the web are opened (no file, no program)");
        }
        finally
        {
            BackgroundWork.Failed -= Told;
            BackgroundWork.Log = logBefore;
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task TestTmdbTrayAsync(MainViewModel model, AppServices services)
    {
        var tray = services.TmdbTray;
        services.Settings.TmdbTrayPort = 0;
        tray.StartReceiving();
        Check(tray.Receiving && tray.Port > 0, "tray: the receiver on 127.0.0.1 runs – " + tray.Status);
        var automaticBefore = tray.Automatic;
        tray.Automatic = true;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<System.Net.HttpStatusCode> Send(string key, string page, bool byItself = true)
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{tray.Port}/tmdb")
            {
                Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new { u = page, t = "Vymyslený film zo skriptu (2026) — The Movie Database (TMDB)", a = byItself }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Universum-Key", key);
            using var answer = await http.SendAsync(request);
            return answer.StatusCode;
        }

        var sent = await Send(tray.Key, "https://www.themoviedb.org/movie/111-vymysleny-film-zo-skriptu");
        for (var i = 0; i < 50 && !tray.Entries.Any(e => e.Id == 111); i++)
        {
            await Task.Delay(100);
        }

        Check(sent == System.Net.HttpStatusCode.NoContent && tray.Entries.Any(e => e is { Kind: MediaKind.Movie, Id: 111 }) &&
              TrayToast.Shown.Any(t => t.Contains("Vymyslený film zo skriptu", StringComparison.Ordinal)),
            $"tray: a film from the script, with a notice – {sent} | {string.Join(", ", tray.Entries.Select(e => e.Display))}");
        Check(await Send("0123456789abcdef0123456789abcdef", "https://www.themoviedb.org/movie/112") == System.Net.HttpStatusCode.Forbidden, "tray: another key is refused");

        // owner, 8. 11. 2026: the automatic gathering switched off at the top of a page – pages sent by themselves are not
        // taken, the button of the page still adds; the switch shows the count
        tray.Automatic = false;
        var offSent = await Send(tray.Key, "https://www.themoviedb.org/movie/113");
        var buttonSent = await Send(tray.Key, "https://www.themoviedb.org/movie/114", byItself: false);
        for (var i = 0; i < 50 && !tray.Entries.Any(e => e.Id == 114); i++)
        {
            await Task.Delay(100);
        }

        Check(offSent == System.Net.HttpStatusCode.Accepted && buttonSent == System.Net.HttpStatusCode.NoContent &&
              tray.Entries.All(e => e.Id != 113) && tray.Entries.Any(e => e.Id == 114) && !services.Settings.TmdbTrayAuto && model.TrayService.SwitchText.EndsWith(" · " + tray.Entries.Count, StringComparison.Ordinal),
            $"tray: gathering switched off – only the button adds ({offSent}, {buttonSent}) – {model.TrayService.SwitchText}");
        tray.Automatic = true;

        // one film or series – one send: a season of the series already in the tray, sent by itself, changes nothing
        var order = string.Join(' ', tray.Entries.Select(e => e.Id));
        var toasts = TrayToast.Shown.Count;
        var again = await Send(tray.Key, "https://www.themoviedb.org/movie/111-vymysleny-film-zo-skriptu/cast");
        await Task.Delay(300);
        Check(again == System.Net.HttpStatusCode.NoContent && string.Join(' ', tray.Entries.Select(e => e.Id)) == order && TrayToast.Shown.Count == toasts,
            "tray: a title already there, sent by itself again, is left alone – " + order);
        // posters picked in the browser (owner, 8. 11. 2026: ČSFD too): a stand-in downloads, a stand-in finds the ČSFD page on
        // TMDB; only the picture servers of TMDB and ČSFD are taken
        var downloadBefore = tray.Download;
        var findBefore = tray.FindCsfd;
        var downloaded = new List<Uri>();
        tray.Download = (address, _) =>
        {
            lock (downloaded)
            {
                downloaded.Add(address);
            }

            return Task.FromResult<byte[]?>(InventedPoster());
        };
        tray.FindCsfd = page => Task.FromResult<(Core.Library.Tmdb.TmdbTrayEntry Entry, bool Sure)?>((new Core.Library.Tmdb.TmdbTrayEntry(MediaKind.Movie, 555, page.Display), true));
        async Task<System.Net.HttpStatusCode> Post(string path, object body)
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{tray.Port}{path}")
            {
                Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Universum-Key", tray.Key);
            using var answer = await http.SendAsync(request);
            return answer.StatusCode;
        }

        const string csfdPage = "https://www.csfd.cz/film/12345-vymysleny-film-z-csfd/prehled/";
        const string csfdTitle = "Vymyslený film z ČSFD (2026) | ČSFD.cz";
        var tmdbPoster = await Post("/poster", new { u = "https://www.themoviedb.org/movie/111-vymysleny-film-zo-skriptu/images/posters", i = "https://media.themoviedb.org/t/p/w220_and_h330_face/VymyslenyPlagat1.jpg", a = false });
        var csfdFilm = await Post("/csfd", new { u = csfdPage, t = csfdTitle, m = "tt0000001", a = false });
        var csfdPoster = await Post("/poster", new { u = csfdPage, t = csfdTitle, m = "tt0000001", i = "//image.pmgstatic.com/cache/resized/w420/files/images/film/posters/111/222/111222333_ab12cd.jpg", a = false });
        var foreign = await Post("/poster", new { u = csfdPage, i = "https://evil.example/vymysleny-plagat.jpg", a = false });
        for (var i = 0; i < 100 && (tray.PostersOf(MediaKind.Movie, 111).Count == 0 || tray.PostersOf(MediaKind.Movie, 555).Count == 0); i++)
        {
            await Task.Delay(50);
        }

        Check(tmdbPoster == System.Net.HttpStatusCode.Accepted && csfdFilm == System.Net.HttpStatusCode.Accepted && csfdPoster == System.Net.HttpStatusCode.Accepted && (int)foreign == 422 &&
              tray.PostersOf(MediaKind.Movie, 111).Count == 1 && tray.PostersOf(MediaKind.Movie, 555) is [{ Source: Core.Library.Tmdb.TrayPosterSource.Csfd }] &&
              tray.Entries.Any(e => e.Id == 555) && downloaded.Any(u => u.AbsoluteUri == "https://image.tmdb.org/t/p/original/VymyslenyPlagat1.jpg") &&
              downloaded.All(u => u.Host is "image.tmdb.org" or "image.pmgstatic.com") && downloaded.Any(u => u.AbsoluteUri.StartsWith("https://image.pmgstatic.com/files/images/film/posters/", StringComparison.Ordinal)) &&
              TrayToast.Shown.Any(t => t.Contains("ČSFD", StringComparison.Ordinal)),
            $"tray: posters from TMDB and ČSFD kept with their titles, a foreign picture refused ({tmdbPoster}, {csfdFilm}, {csfdPoster}, {(int)foreign}) – {string.Join(" ", downloaded.Select(u => u.Host))}");
        Check(model.HeaderEditor.Library.Tray.Rows.Any(r => r.Entry.Id == 555 && r.Posters == 1 && r.Label.Contains("1", StringComparison.Ordinal)),
            "tray: the choice beside a title tells the posters – " + string.Join(" | ", model.HeaderEditor.Library.Tray.Rows.Select(r => r.Label)));
        tray.Download = downloadBefore;
        tray.FindCsfd = findBefore;
        Check(services.Settings.TmdbTrayAuto && model.TrayService.SwitchText.StartsWith(Localization.Loc.T("Zásobník TMDB zbiera"), StringComparison.Ordinal), "tray: gathering switched on – " + model.TrayService.SwitchText);
        using (var script = await http.GetAsync(tray.ScriptAddress))
        {
            var text = await script.Content.ReadAsStringAsync();
            Check(script.IsSuccessStatusCode && text.Contains("// ==UserScript==", StringComparison.Ordinal) && text.Contains(tray.Key, StringComparison.Ordinal),
                "tray: the script to install, with the key");
        }

        await model.HandleAsync(Core.Shell.ShellRequest.Parse(["--tmdb", "universum://tmdb?u=" + Uri.EscapeDataString("https://www.themoviedb.org/tv/222-vymysleny-serial")]));
        await model.HandleAsync(Core.Shell.ShellRequest.Parse(["--tmdb", "universum://tmdb?u=" + Uri.EscapeDataString("https://evil.example/movie/333")]));
        Check(tray.Entries.Any(e => e is { Kind: MediaKind.Series, Id: 222 }) && tray.Entries.All(e => e.Id != 333),
            "tray: the address universum:// – a TMDB series taken, another page not");
        // owner, 14. 11. 2026: two notes of the system – the series in Zásobník seriálov TMDB, the films in Zásobník filmov TMDB
        var seriesNote = tray.Note(MediaKind.Series);
        var filmsNote = tray.Note(MediaKind.Movie);
        Check(seriesNote is { Note.Pinned: true } && filmsNote is { Note.Pinned: true } &&
              seriesNote.Title == TmdbTrayService.NoteTitle(MediaKind.Series) && filmsNote.Title == TmdbTrayService.NoteTitle(MediaKind.Movie) &&
              Core.Library.Tmdb.TmdbTray.Read(seriesNote.Text).Any(e => e.Id == 222) && Core.Library.Tmdb.TmdbTray.Read(seriesNote.Text).All(e => e.Kind == MediaKind.Series) &&
              Core.Library.Tmdb.TmdbTray.Read(filmsNote.Text).Any(e => e.Id == 111) && Core.Library.Tmdb.TmdbTray.Read(filmsNote.Text).All(e => e.Kind == MediaKind.Movie),
            "tray: the series and the films each in a pinned note of their own – " + seriesNote?.Title + " | " + filmsNote?.Title);
        // owner, 21. 11. 2026: no Z prehliadača in Hromadné spracovanie, the reading of the browser stays elsewhere
        Check(!model.Batch.Tray.ShowsBrowser && model.Rules.Tray.ShowsBrowser && model.HeaderEditor.Library.Tray.ShowsBrowser,
            "tray: Z prehliadača not in Hromadné spracovanie, still in Single and Editácia hlavičky");
        var trayRows = model.HeaderEditor.Library.Tray.Rows;
        var slovak = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("sk-SK"), true);
        var seriesNames = trayRows.Where(r => r.Entry.Kind == MediaKind.Series).Select(r => r.Entry.Name).OfType<string>().ToList();
        var filmNames = trayRows.Where(r => r.Entry.Kind == MediaKind.Movie).Select(r => r.Entry.Name).OfType<string>().ToList();
        Check(trayRows.SkipWhile(r => r.Entry.Kind == MediaKind.Series).All(r => r.Entry.Kind == MediaKind.Movie) &&
              seriesNames.SequenceEqual(seriesNames.Order(slovak)) && filmNames.SequenceEqual(filmNames.Order(slovak)) && trayRows.Count(r => r.StartsFilms) == 1,
            "tray: the choice – the series by the alphabet, a line, then the films (owner, 14. 11. 2026) – " + string.Join(" | ", trayRows.Select(r => r.Label)));

        // a series left in the note of films (the one tray before) moves to the note of series at the start
        filmsNote!.Text += "\nhttps://www.themoviedb.org/tv/777";
        services.Agenda.Save(filmsNote);
        tray.EnsureNotes();
        Check(Core.Library.Tmdb.TmdbTray.Read(tray.Note(MediaKind.Series)?.Text).Any(e => e.Id == 777) && Core.Library.Tmdb.TmdbTray.Read(tray.Note(MediaKind.Movie)?.Text).All(e => e.Id != 777),
            "tray: a series in the note of films moves to the note of series");
        tray.Remove(new Core.Library.Tmdb.TmdbTrayEntry(MediaKind.Series, 777));

        // one click beside a title field: the address in the field, the kind with it
        var library = model.HeaderEditor.Library;
        library.Tray.Selected = library.Tray.Rows.First(r => r.Entry.Id == 222);
        Check(library.TitleText == "https://www.themoviedb.org/tv/222" && library.IsSeries, "tray: chosen beside a title field – " + library.TitleText);

        // owner, 8. 11. 2026: the button on an episode page adds the episode – chosen, it fills the series and S01E03; an
        // episode page sent by itself adds only its series
        var episodesBefore = services.Settings.TmdbTrayEpisodes;
        services.Settings.TmdbTrayEpisodes = false;
        await Send(tray.Key, "https://www.themoviedb.org/tv/444-vymysleny-serial-bez-epizod/season/1/episode/1", byItself: false);
        for (var i = 0; i < 50 && !tray.Entries.Any(e => e.Id == 444); i++)
        {
            await Task.Delay(100);
        }

        Check(tray.Entries.Any(e => e is { Id: 444, IsEpisode: false }) && !tray.Entries.Any(e => e is { Id: 444, IsEpisode: true }),
            "tray: episodes off (as by default) – the button on an episode page adds its series");
        services.Settings.TmdbTrayEpisodes = true;
        var episodeSent = await Send(tray.Key, "https://www.themoviedb.org/tv/222-vymysleny-serial/season/1/episode/3", byItself: false);
        var seriesSent = await Send(tray.Key, "https://www.themoviedb.org/tv/333-iny-vymysleny-serial/season/2/episode/5");
        for (var i = 0; i < 50 && !(tray.Entries.Any(e => e is { Id: 222, IsEpisode: true }) && tray.Entries.Any(e => e.Id == 333)); i++)
        {
            await Task.Delay(100);
        }

        Check(episodeSent == System.Net.HttpStatusCode.NoContent && tray.Entries.Any(e => e is { Id: 222, Season: 1, Episode: 3 }) &&
              tray.Entries.Any(e => e is { Id: 333, IsEpisode: false }) && !tray.Entries.Any(e => e is { Id: 333, IsEpisode: true }),
            "tray: an episode by the button, only its series by itself – " + string.Join(" | ", tray.Entries.Select(e => e.Display)));
        library.Tray.Selected = library.Tray.Rows.First(r => r.Entry is { Id: 222, IsEpisode: true });
        await Task.Delay(300);
        Check(library.TitleText == "https://www.themoviedb.org/tv/222" && library.EpisodeText == "S01E03",
            $"tray: an episode chosen fills the series and the episode – {library.TitleText} {library.EpisodeText}");
        services.Settings.TmdbTrayEpisodes = episodesBefore;

        // the browser windows: a stand-in with a TMDB film in front, then the real windows (there may be none)
        var found = await tray.ReadBrowserAsync(() => [new BrowserWindow("themoviedb.org/movie/444-iny-vymysleny-film", ["Nejaká iná stránka – Vymyslený portál"])]);
        Check(tray.Entries.Any(e => e is { Kind: MediaKind.Movie, Id: 444 }), "tray: the tab in front of a browser window – " + found);
        Write("tray: the browser windows of this computer – " + await tray.ReadBrowserAsync());

        // Poznámky: the tray widest, any note wider with a click, the note opened wider; the tray emptied from its card and
        // from inside it, Vrátiť puts it back (owner, 8. 11. 2026)
        var notes = model.AgendaNotes;
        notes.Build();
        var trayCards = notes.Cards.Where(c => c.IsTray).ToList();
        Check(trayCards.Count == 2 && notes.Cards[0].Record?.Uid == Core.Library.Tmdb.TmdbTray.SeriesNoteUid && notes.Cards[1].Record?.Uid == Core.Library.Tmdb.TmdbTray.NoteUid &&
              trayCards.All(c => c.CardWidth == NotesPageViewModel.CardWidths[^1] && c.ClearTrayCommand is not null && !c.CanDelete),
            "tray: the note of series on top, the note of films under it, both the widest, neither can be deleted – " + string.Join(" | ", notes.Cards.Take(3).Select(c => c.Title)));
        trayCards[1].DeleteCommand.Execute(null);
        Check(tray.Note(MediaKind.Movie) is not null && services.Agenda.Bin.All(i => !Core.Library.Tmdb.TmdbTray.IsNote(i.Uid)), "tray: a note of the system does not go to the bin");

        // the note of films emptied from its card – the series stay, the posters of the films go aside; Vrátiť puts it back
        var count = tray.Entries.Count;
        var seriesCount = tray.Entries.Count(e => e.Kind == MediaKind.Series);
        trayCards[1].ClearTrayCommand!.Execute(null);
        Check(tray.Entries.Count == seriesCount && tray.Entries.All(e => e.Kind == MediaKind.Series) && notes.CanUndoTray && notes.Cards.Count(c => c.IsTray) == 2 &&
              tray.PostersOf(MediaKind.Movie, 555).Count == 0,
            "tray: the films emptied from their card, their posters too, the series stay – " + notes.Status);
        notes.UndoTrayCommand.Execute(null);
        Check(tray.Entries.Count == count && !notes.CanUndoTray && tray.PostersOf(MediaKind.Movie, 555).Count == 1, "tray: Vrátiť puts it back, the posters too – " + notes.Status);

        // owner, 12. 11. 2026: one title removed with its × on the card – the others stay; Vrátiť brings it back
        notes.Build();
        var oneOut = notes.Cards.First(c => c.IsTray && c.Record?.Uid == Core.Library.Tmdb.TmdbTray.NoteUid).Rows.Select(r => r.Link).OfType<NoteLinkView>().First(l => l.CanRemove);
        var before = tray.Entries.Count;
        oneOut.RemoveCommand!.Execute(null);
        Check(tray.Entries.Count == before - 1 && notes.CanUndoTray, $"tray: one title removed with its × – {before} → {tray.Entries.Count} – {notes.Status}");
        notes.UndoTrayCommand.Execute(null);
        Check(tray.Entries.Count == before, "tray: … Vrátiť brings it back");
        notes.Edit(tray.Note(MediaKind.Series));
        notes.ClearTrayCommand.Execute(null);
        Check(notes.EditingTray && tray.Entries.Count == count - seriesCount && tray.Entries.All(e => e.Kind == MediaKind.Movie) && notes.Editor is AgendaEditor { CanDelete: false, CanChangeKind: false },
            "tray: the series emptied from inside their note, the films stay; no Do koša in it – " + notes.Status);
        notes.UndoTrayCommand.Execute(null);
        Check(tray.Entries.Count == count, "tray: … the series back");
        var widthBefore = services.Settings.NotesEditorWidth;
        notes.WidenEditorCommand.Execute(null);
        Check(notes.EditorWidth > widthBefore || widthBefore >= 820, $"notes: the note opened wider – {widthBefore} → {notes.EditorWidth}");
        services.Settings.NotesEditorWidth = widthBefore;
        ((AgendaEditor)notes.Editor!).CancelCommand.Execute(null);
        services.Agenda.Save(new Core.Agenda.AgendaItem { Title = "Vymyslená široká poznámka", Text = string.Join('\n', Enumerable.Range(1, 20).Select(n => $"https://video.example/vymyslene-video-{n}")), Note = new Core.Agenda.AgendaNote() });
        var plain = notes.Cards.Single(c => c.Title == "Vymyslená široká poznámka");
        plain.WidenCommand!.Execute(null);
        var wide = notes.Cards.Single(c => c.Title == "Vymyslená široká poznámka");
        Check(plain.CardWidth == NotesPageViewModel.CardWidths[0] && wide.CardWidth == NotesPageViewModel.CardWidths[1] && wide.Rows.Count > plain.Rows.Count,
            $"notes: a card wider with a click, more rows – {plain.Rows.Count} → {wide.Rows.Count}");
        services.Agenda.Delete(wide.Record!.Id);
        services.Agenda.EmptyBin();
        foreach (var poster in tray.PostersOf(MediaKind.Movie, 111).Concat(tray.PostersOf(MediaKind.Movie, 555)))
        {
            tray.Posters.Remove(poster);
        }

        tray.Automatic = automaticBefore;
        tray.StopReceiving();
        Check(!tray.Receiving, "tray: the receiver stopped");
    }
}
