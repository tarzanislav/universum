using System.IO;
using System.Net.Sockets;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Universum.App.Localization;
using Universum.App.Shell;
using Universum.Core.Agenda;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>
/// Zásobník TMDB (owner, 8. 11. 2026): the films and series picked in the browser – sent by UNIVERSUM's script (by itself
/// on every TMDB page opened, or by its button), by the address universum:// (the bookmark "Do UNIVERSUM"), or read from
/// the open browser windows – kept in a pinned note of Poznámky, named by TMDB as the library names them; Hromadné, Single,
/// Zlučovanie and Editácia hlavičky take a title from it with one click.
/// </summary>
public sealed class TmdbTrayService : ObservableObject
{
    private readonly AppServices _services;
    private TmdbTrayListener? _listener;
    private TrayPosters? _posters;

    public TmdbTrayService(AppServices services)
    {
        _services = services;
        services.Agenda.Changed += () =>
        {
            Changed?.Invoke();
            OnPropertyChanged(nameof(SwitchText));
        };
    }

    /// <summary>
    /// The automatic gathering (owner, 8. 11. 2026: switched on at the top of Hromadné, Single and the other pages): on,
    /// every TMDB film or series page opened in the browser is added; off, only the button "Do UNIVERSUM" of the page.
    /// </summary>
    public bool Automatic
    {
        get => _services.Settings.TmdbTrayAuto;
        set
        {
            if (_services.Settings.TmdbTrayAuto != value)
            {
                _services.Settings.TmdbTrayAuto = value;
                _services.Settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SwitchText));
            }
        }
    }

    /// <summary>"Zásobník TMDB zbiera · 3" or "Zásobník TMDB · 3" – the switch at the top of the pages.</summary>
    public string SwitchText => (Automatic ? Loc.T("Zásobník TMDB zbiera") : Loc.T("Zásobník TMDB")) + " · " + Entries.Count;

    public string SwitchToolTip => Loc.T("Zapnuté: každý film a seriál, ktorý otvoríte na TMDB, sa sám uloží do Zásobníka TMDB (potrebuje skript UNIVERSUM v prehliadači – Nastavenia). Vypnuté: len tlačidlom Do UNIVERSUM na stránke.");

    /// <summary>Raised when the titles change (on the thread of the change – the UI's).</summary>
    public event Action? Changed;

    /// <summary>The titles, the newest first.</summary>
    public IReadOnlyList<TmdbTrayEntry> Entries => TmdbTray.Read(Note()?.Text);

    /// <summary>"Prijíma na 127.0.0.1:47219", why not, or "vypnuté".</summary>
    public string Status { get; private set; } = "";

    public bool Receiving => _listener is not null;

    /// <summary>The secret key of this installation (made once).</summary>
    public string Key
    {
        get
        {
            if (_services.Settings.TmdbTrayKey is not { Length: >= 16 } key)
            {
                key = _services.Settings.TmdbTrayKey = TmdbTrayListener.NewKey();
                _services.Settings.Save();
            }

            return key;
        }
    }

    public int Port => _listener?.Port ?? _services.Settings.TmdbTrayPort;

    /// <summary>The address that installs the script in Tampermonkey or Violentmonkey (opened in the browser).</summary>
    public string ScriptAddress => $"http://127.0.0.1:{Port}/universum.user.js?k={Key}";

    /// <summary>The script itself (to copy it into the extension by hand).</summary>
    public string Script => TmdbUserScript.Text(Port, Key);

    private AgendaItem? Note() => _services.Agenda.Items.FirstOrDefault(i => i.Uid == TmdbTray.NoteUid);

    /// <summary>Adds a title (at the top; one already there moves up) and asks TMDB for its name as the library names it.</summary>
    public void Add(TmdbTrayEntry entry, bool fromBrowser = false, bool byItself = false)
    {
        // pages sent by themselves add the film or series (an episode only by the button of its page)
        entry = byItself ? entry.WithoutEpisode() : entry;

        // a title already in the tray sent by itself again (a season or an episode of it opened): nothing to do
        // (owner, 8. 11. 2026: one film or series – one send); the button of the page still moves it up
        if (byItself && Entries.Any(e => e.Kind == entry.Kind && e.Id == entry.Id))
        {
            return;
        }

        var note = Note() ?? new AgendaItem
        {
            Uid = TmdbTray.NoteUid,
            Title = Loc.T("Zásobník TMDB"),
            Tags = ["TMDB"],
            Note = new AgendaNote { Pinned = true, Width = 2 },
        };
        note.Text = TmdbTray.Add(note.Text, entry);
        _services.Agenda.Save(note);
        if (fromBrowser)
        {
            // a title from the browser: a short notice at the corner of the screen (UNIVERSUM stays where it is)
            TrayToast.Show(Loc.F("Do zásobníka TMDB: {0}", entry.Display));
        }

        _ = NameAsync(entry);
    }

    /// <summary>
    /// Empties the tray (owner, 8. 11. 2026: "reštart – nebude tam žiaden údaj z TMDB"); the owner's own lines stay. The
    /// text before, to take it back; null when there was nothing to empty.
    /// </summary>
    public string? Clear()
    {
        if (Note() is not { } note || TmdbTray.Read(note.Text).Count == 0)
        {
            return null;
        }

        var before = note.Text;
        note.Text = TmdbTray.Clear(note.Text);
        _services.Agenda.Save(note);
        try
        {
            // the posters picked go aside with the titles
            Posters.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a poster open elsewhere: the posters stay
        }

        return before;
    }

    /// <summary>The tray as it was before <see cref="Clear"/> (titles added since stay at the top).</summary>
    public void Restore(string before)
    {
        if (Note() is { } note)
        {
            var added = TmdbTray.Read(note.Text);
            try
            {
                Posters.Restore();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the posters put aside cannot come back now
            }

            note.Text = before;
            foreach (var entry in added.Reverse())
            {
                note.Text = TmdbTray.Add(note.Text, entry);
            }

            _services.Agenda.Save(note);
        }
    }

    /// <summary>The posters picked into the tray (ČSFD, TMDB), kept per title.</summary>
    public TrayPosters Posters => _posters ??= new TrayPosters(AppSettings.TrayPosterDirectory);

    /// <summary>How UNIVERSUM downloads a poster (the self-test puts a stand-in here).</summary>
    public Func<Uri, CancellationToken, Task<byte[]?>> Download { get; set; } = DownloadAsync;

    /// <summary>How UNIVERSUM finds a page of ČSFD on TMDB (the self-test puts a stand-in here).</summary>
    public Func<CsfdPage, Task<(TmdbTrayEntry Entry, bool Sure)?>>? FindCsfd { get; set; }

    /// <summary>The posters of a title picked into the tray, the newest first.</summary>
    public IReadOnlyList<TrayPoster> PostersOf(MediaKind kind, int id)
    {
        try
        {
            return Posters.Of(kind, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// A film or series page of ČSFD from the script (owner, 8. 11. 2026): found on TMDB by its link to IMDb, else by its
    /// name and year (not clearly one – the likeliest, marked "over"), and added to the tray.
    /// </summary>
    public async Task ReceiveCsfdAsync(CsfdPage page, bool byItself)
    {
        if (await FindAsync(page) is not { } found)
        {
            if (!byItself)
            {
                TrayToast.Show(Loc.F("{0} z ČSFD sa na TMDB nenašiel – zadajte kľúč TMDB v Nastaveniach alebo pridajte titul cez TMDB.", page.Display));
            }

            return;
        }

        if (found.Sure)
        {
            Add(found.Entry, fromBrowser: true, byItself);
        }
        else if (!(byItself && Entries.Any(e => e.Kind == found.Entry.Kind && e.Id == found.Entry.Id)))
        {
            AddUncertain(found.Entry);
            TrayToast.Show(Loc.F("Do zásobníka TMDB: {0}", found.Entry.Display));
        }
    }

    /// <summary>
    /// A poster picked in the browser (owner, 8. 11. 2026: posters of ČSFD too): its title found (TMDB says it, ČSFD by
    /// IMDb or name and year), the picture downloaded – only from the picture servers of ČSFD and TMDB – and kept with
    /// the title; the title joins the tray. What happened is told by a notice.
    /// </summary>
    public async Task ReceivePosterAsync(TrayPosterRequest poster)
    {
        var found = poster.Entry is { } exact ? (exact.WithoutEpisode(), true) : poster.Csfd is { } page ? await FindAsync(page) : null;
        if (found is not { } title)
        {
            TrayToast.Show(Loc.F("Plagát z {0}: titul sa na TMDB nenašiel – zadajte kľúč TMDB v Nastaveniach.", poster.Source == TrayPosterSource.Csfd ? "ČSFD" : "TMDB"));
            return;
        }

        byte[]? picture = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        foreach (var address in poster.Images)
        {
            try
            {
                picture = await Download(address, timeout.Token);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
            {
                picture = null;
            }

            if (picture is not null && TrayPosters.Extension(picture) is not null)
            {
                break;
            }

            picture = null;
        }

        string? kept = null;
        try
        {
            kept = picture is null ? null : Posters.Add(title.Entry.Kind, title.Entry.Id, picture, poster.Source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            kept = null;
        }

        if (kept is null)
        {
            TrayToast.Show(Loc.F("Plagát k titulu {0} sa nepodarilo stiahnuť.", title.Entry.Display));
            return;
        }

        if (title.Sure)
        {
            Add(title.Entry);
        }
        else
        {
            AddUncertain(title.Entry);
        }

        TrayToast.Show(Loc.F("Plagát ({0}) do zásobníka: {1}", poster.Source == TrayPosterSource.Csfd ? "ČSFD" : "TMDB",
            Entries.FirstOrDefault(e => e.Kind == title.Entry.Kind && e.Id == title.Entry.Id)?.Display ?? title.Entry.Display));
        Changed?.Invoke();
    }

    // a ČSFD page on TMDB: by IMDb exactly, else by the name and year (sure only when one title fits)
    private async Task<(TmdbTrayEntry Entry, bool Sure)?> FindAsync(CsfdPage page)
    {
        if (FindCsfd is { } standIn)
        {
            return await standIn(page);
        }

        try
        {
            if (page.Imdb is { } imdb && _services.Finder is { } finder && (await finder.FindAsync(imdb, null)).FirstOrDefault() is { } byImdb)
            {
                return (new TmdbTrayEntry(byImdb.Kind, byImdb.Id, page.Title?.Display), true);
            }

            if (page.Title is { } title && _services.Titles is { } search)
            {
                var found = await search.SearchAsync(title.Kind, title.Name, title.Year);
                var candidates = found.Titles.Where(t => t.Kind == title.Kind).ToList();
                var exact = candidates.Where(t => t.Year == title.Year && t.IsNamed(title.Name)).ToList();
                if (exact.Count == 1)
                {
                    return (new TmdbTrayEntry(exact[0].Kind, exact[0].Id, title.Display), true);
                }

                if (candidates.FirstOrDefault() is { } likeliest)
                {
                    return (new TmdbTrayEntry(likeliest.Kind, likeliest.Id, NoteRows.TitleName(likeliest) + " – " + Loc.T("over")), false);
                }
            }
        }
        catch (Exception ex) when (ex is TitleServiceException or System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            // without TMDB nothing is found
        }

        return null;
    }

    // a poster downloaded (at most 15 MB)
    private static async Task<byte[]?> DownloadAsync(Uri address, CancellationToken cancellationToken)
    {
        using var answer = await AppServices.WebClient.GetAsync(address, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!answer.IsSuccessStatusCode || answer.Content.Headers.ContentLength > TrayPosters.MaxBytes)
        {
            return null;
        }

        await using var stream = await answer.Content.ReadAsStreamAsync(cancellationToken);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            copy.Write(buffer, 0, read);
            if (copy.Length > TrayPosters.MaxBytes)
            {
                return null;
            }
        }

        return copy.ToArray();
    }

    public void Remove(TmdbTrayEntry entry)
    {
        if (Note() is { } note)
        {
            note.Text = TmdbTray.Remove(note.Text, entry);
            _services.Agenda.Save(note);
        }
    }

    // the name of the title by the rules of the library (the original in Latin script, else the English one), with the year
    private async Task NameAsync(TmdbTrayEntry entry)
    {
        if ((_services.Titles?.Tmdb ?? _services.Tmdb) is not { } tmdb)
        {
            return;
        }

        try
        {
            if (await tmdb.GetAsync(entry.Kind, entry.Id) is { } title && Note() is { } note)
            {
                var name = NoteRows.TitleName(title);
                var named = TmdbTray.Name(note.Text, entry.Kind, entry.Id, name);
                if (named != note.Text)
                {
                    note.Text = named;
                    _services.Agenda.Save(note);
                }
            }
        }
        catch (Exception ex) when (ex is TitleServiceException or System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            // without TMDB the name from the page (or the address) stays
        }
    }

    /// <summary>Starts the receiver on 127.0.0.1 when it is switched on (at the start of UNIVERSUM and when switched on).</summary>
    public void StartReceiving()
    {
        StopReceiving();
        if (!_services.Settings.TmdbTrayReceive)
        {
            Status = Loc.T("Príjem zo skriptu je vypnutý.");
            return;
        }

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            _listener = TmdbTrayListener.Start(_services.Settings.TmdbTrayPort, Key,
                (entry, byItself) => dispatcher is null ? Task.CompletedTask : dispatcher.InvokeAsync(() => Add(entry, fromBrowser: true, byItself)).Task,
                () => TmdbUserScript.Text(Port, Key),
                () => _services.Settings.TmdbTrayAuto,
                (page, byItself) => dispatcher is null ? Task.CompletedTask : dispatcher.InvokeAsync(() => ReceiveCsfdAsync(page, byItself)).Task.Unwrap(),
                poster => dispatcher is null ? Task.CompletedTask : dispatcher.InvokeAsync(() => ReceivePosterAsync(poster)).Task.Unwrap());
            Status = Loc.F("Prijíma zo skriptu v prehliadači (len z tohto počítača, 127.0.0.1:{0}).", Port);
        }
        catch (SocketException ex)
        {
            Status = Loc.F("Príjem zo skriptu nebeží – port {0} je obsadený iným programom ({1}).", _services.Settings.TmdbTrayPort, ex.SocketErrorCode);
        }
    }

    public void StopReceiving()
    {
        _listener?.Dispose();
        _listener = null;
    }

    /// <summary>
    /// Z prehliadača: the films and series open in the browser windows into the tray – the tab in front of each window
    /// exactly by its address, the other TMDB tabs by their title (the name, year and kind found on TMDB; a title that is
    /// not clearly one film or series gets "over" and the likeliest one). What was found, in words.
    /// </summary>
    public async Task<string> ReadBrowserAsync(Func<IReadOnlyList<BrowserWindow>>? read = null)
    {
        var windows = await Task.Run(read ?? BrowserTabs.Read);
        var known = Entries.Select(e => (e.Kind, e.Id)).ToHashSet();
        int added = 0, uncertain = 0;
        foreach (var window in windows)
        {
            if (window.Address is { Length: > 0 } address &&
                TmdbTray.FromPage(address.Contains("://", StringComparison.Ordinal) ? address : "https://" + address, null) is { } entry)
            {
                Add(entry);
                added += known.Add((entry.Kind, entry.Id)) ? 1 : 0;
            }
        }

        var titles = windows.SelectMany(w => w.TabTitles).Select(TmdbTray.FromTabTitle).OfType<TabTitle>().Distinct().ToList();
        if (titles.Count > 0 && _services.Titles is { } search)
        {
            foreach (var title in titles)
            {
                TitleResults found;
                try
                {
                    found = await search.SearchAsync(title.Kind, title.Name, title.Year);
                }
                catch (Exception ex) when (ex is TitleServiceException or System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
                {
                    continue;
                }

                var candidates = found.Titles.Where(t => t.Kind == title.Kind).ToList();
                var exact = candidates.Where(t => t.Year == title.Year && t.IsNamed(title.Name)).ToList();
                if ((exact.Count == 1 ? exact[0] : candidates.FirstOrDefault()) is not { } pick || !known.Add((pick.Kind, pick.Id)))
                {
                    continue;
                }

                if (exact.Count == 1)
                {
                    Add(new TmdbTrayEntry(pick.Kind, pick.Id, title.Display));
                }
                else
                {
                    // not clearly this one: the likeliest, marked to be checked (its link shows which it is)
                    AddUncertain(new TmdbTrayEntry(pick.Kind, pick.Id, NoteRows.TitleName(pick) + " – " + Loc.T("over")));
                    uncertain++;
                }

                added++;
            }
        }

        return windows.Count == 0 ? Loc.T("Nenašlo sa žiadne okno prehliadača (Chrome, Edge, Firefox…).")
            : added == 0 ? Loc.T("V prehliadači nie je otvorený žiadny nový film ani seriál z TMDB.")
            : uncertain == 0 ? Loc.F("Do zásobníka pribudlo: {0}.", added)
            : Loc.F("Do zásobníka pribudlo: {0}, z toho treba overiť: {1}.", added, uncertain);
    }

    // a title not clearly found: kept with its mark (TMDB's name would hide that it is to be checked)
    private void AddUncertain(TmdbTrayEntry entry)
    {
        var note = Note() ?? new AgendaItem { Uid = TmdbTray.NoteUid, Title = Loc.T("Zásobník TMDB"), Tags = ["TMDB"], Note = new AgendaNote { Pinned = true } };
        note.Text = TmdbTray.Add(note.Text, entry);
        _services.Agenda.Save(note);
    }

    /// <summary>A new secret key: the script installed so far stops working (install it again).</summary>
    public void NewKey()
    {
        _services.Settings.TmdbTrayKey = TmdbTrayListener.NewKey();
        _services.Settings.Save();
        StartReceiving();
    }
}
