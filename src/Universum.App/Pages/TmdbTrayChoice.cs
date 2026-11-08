using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;

namespace Universum.App.Pages;

/// <summary>A title of the Zásobník TMDB as a list shows it: "Vymyslený film (2026) · film".</summary>
public sealed record TmdbTrayRow(TmdbTrayEntry Entry, int Posters = 0)
{
    public string Label => Entry.Display + " · " + (Entry.Kind == MediaKind.Series ? Loc.T("seriál") : Loc.T("film")) +
                           (Posters > 0 ? " · " + Loc.F("plagáty: {0}", Posters) : "");

    public string ToolTip => Entry.Address.AbsoluteUri;
}

/// <summary>
/// The choice from the Zásobník TMDB beside a title field (owner, 8. 11. 2026: one click instead of copying the TMDB ID
/// from the browser): the titles gathered, the newest first; a title chosen is handed to the page (which fills its field
/// and loads the title), then the choice is empty again. "Z prehliadača" reads the open browser windows.
/// </summary>
public sealed class TmdbTrayChoice : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<TmdbTrayEntry, Task> _chosen;
    private string _note = "";

    public TmdbTrayChoice(AppServices services, Func<TmdbTrayEntry, Task> chosen)
    {
        _services = services;
        _chosen = chosen;
        services.TmdbTray.Changed += () =>
        {
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(HasRows));
        };
        ReadBrowserCommand = new AsyncRelayCommand(ReadBrowserAsync);
    }

    public IReadOnlyList<TmdbTrayRow> Rows => [.. _services.TmdbTray.Entries.Select(e => new TmdbTrayRow(e, _services.TmdbTray.PostersOf(e.Kind, e.Id).Count))];

    public bool HasRows => _services.TmdbTray.Entries.Count > 0;

    /// <summary>Always empty: choosing a title hands it over.</summary>
    public TmdbTrayRow? Selected
    {
        get => null;
        set
        {
            if (value is not null)
            {
                _ = _chosen(value.Entry);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Reads the films and series open in the browser windows into the tray.</summary>
    public AsyncRelayCommand ReadBrowserCommand { get; }

    /// <summary>What reading the browser found ("3 nové, 1 treba overiť").</summary>
    public string Note { get => _note; private set => SetProperty(ref _note, value); }

    public string ToolTip => Loc.T("Zásobník TMDB – filmy a seriály z prehliadača (skript UNIVERSUM, záložka Do UNIVERSUM alebo Z prehliadača). Výber vyplní titul a načíta ho.");

    private async Task ReadBrowserAsync() => Note = await _services.TmdbTray.ReadBrowserAsync();
}
