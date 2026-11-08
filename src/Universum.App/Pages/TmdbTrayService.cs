using System.IO;
using System.Net.Sockets;
using System.Windows;
using Universum.App.Localization;
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
public sealed class TmdbTrayService
{
    private readonly AppServices _services;
    private TmdbTrayListener? _listener;

    public TmdbTrayService(AppServices services)
    {
        _services = services;
        services.Agenda.Changed += () => Changed?.Invoke();
    }

    /// <summary>Raised when the titles change (on the thread of the change – the UI's).</summary>
    public event Action? Changed;

    /// <summary>Raised when a title comes from the browser (the window shows a short notice).</summary>
    public event Action<TmdbTrayEntry>? Received;

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
    public void Add(TmdbTrayEntry entry, bool fromBrowser = false)
    {
        var note = Note() ?? new AgendaItem
        {
            Uid = TmdbTray.NoteUid,
            Title = Loc.T("Zásobník TMDB"),
            Tags = ["TMDB"],
            Note = new AgendaNote { Pinned = true },
        };
        note.Text = TmdbTray.Add(note.Text, entry);
        _services.Agenda.Save(note);
        if (fromBrowser)
        {
            Received?.Invoke(entry);
        }

        _ = NameAsync(entry);
    }

    public void Remove(TmdbTrayEntry entry)
    {
        if (Note() is { } note)
        {
            note.Text = TmdbTray.Remove(note.Text, entry.Kind, entry.Id);
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
                entry => dispatcher is null ? Task.CompletedTask : dispatcher.InvokeAsync(() => Add(entry, fromBrowser: true)).Task,
                () => TmdbUserScript.Text(Port, Key));
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
