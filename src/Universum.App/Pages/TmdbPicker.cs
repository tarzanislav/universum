using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;

namespace Universum.App.Pages;

/// <summary>
/// Looks up the text of a title field on TMDB (an ID or address, or "Name (Year)") – without a TMDB key, or when TMDB
/// finds nothing, in the HISTRA catalog – and offers the results; the chosen result is handed back to the page.
/// Without TMDB and HISTRA in the settings the button is disabled.
/// </summary>
public sealed class TmdbPicker : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<MediaKind> _kind;
    private readonly Func<string> _text;
    private readonly Action<TmdbTitle> _chosen;
    private TmdbTitle? _selected;
    private bool _isOpen;
    private bool _busy;
    private string _message = "";

    public TmdbPicker(AppServices services, Func<MediaKind> kind, Func<string> text, Action<TmdbTitle> chosen)
    {
        _services = services;
        _kind = kind;
        _text = text;
        _chosen = chosen;
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !_busy && _services.Titles is not null);
    }

    public AsyncRelayCommand SearchCommand { get; }

    public ObservableCollection<TmdbTitle> Results { get; } = [];

    public bool HasResults => Results.Count > 1;

    public bool IsOpen { get => _isOpen; set => SetProperty(ref _isOpen, value); }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public string Tooltip =>
        _services.Tmdb is not null
            ? "Nájde titul na TMDB podľa ID, adresy alebo názvu a roku" + (_services.Histra is not null ? " (čo TMDB nenájde, hľadá v katalógu HISTRA)" : "")
        : _services.Histra is not null ? "Nájde titul v katalógu HISTRA podľa názvu a roku (ID a adresy TMDB načíta len TMDB s kľúčom)"
        : _services.TmdbKey is null ? "Vyhľadávanie titulov: zadajte v Nastaveniach kľúč TMDB alebo token HISTRA"
        : "Pripájanie k TMDB je vypnuté v Nastaveniach";

    public TmdbTitle? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value) && value is not null)
            {
                if (value is { Kind: MediaKind.Series, Seasons.Count: 0 } && _services.Tmdb is { } tmdb)
                {
                    // a search result has no seasons: the details do, for the check that the episode exists
                    _ = ChooseWithSeasonsAsync(tmdb, value);
                }
                else
                {
                    _chosen(value);
                    if (value is { Kind: MediaKind.Series, Seasons.Count: 0 })
                    {
                        Message = "Bez kľúča TMDB sa neoverí, či sezóna a epizóda existujú.";
                    }
                }
            }
        }
    }

    private async Task ChooseWithSeasonsAsync(TmdbClient tmdb, TmdbTitle title)
    {
        var chosen = title;
        try
        {
            Message = "Načítavam sezóny z TMDB…";
            chosen = await tmdb.GetAsync(MediaKind.Series, title.Id) ?? title;
            TrySaveCache(tmdb);
            Message = "";
        }
        catch (TmdbException ex)
        {
            Message = "Sezóny seriálu sa nenačítali (epizódy sa nedajú overiť): " + ex.Message;
        }

        if (ReferenceEquals(_selected, title))
        {
            _chosen(chosen);
        }
    }

    /// <summary>The key or the page changed: the button may become available.</summary>
    public void Refresh()
    {
        SearchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Tooltip));
    }

    public async Task SearchAsync()
    {
        if (_services.Titles is not { } titles)
        {
            return;
        }

        _busy = true;
        SearchCommand.NotifyCanExecuteChanged();
        Results.Clear();
        _selected = null;
        OnPropertyChanged(nameof(Selected));
        Message = titles.Tmdb is not null ? "Hľadám na TMDB…" : "Hľadám v katalógu HISTRA…";
        void OnWaiting(TimeSpan delay) => Message = $"Služba žiada spomaliť – čakám {Math.Ceiling(delay.TotalSeconds):0} s…";
        titles.Waiting += OnWaiting;
        try
        {
            var found = await titles.FindAsync(_kind(), _text());
            TrySaveCache(titles);
            var results = found.Titles;
            foreach (var result in results.Take(12))
            {
                Results.Add(result);
            }

            var fromTmdb = found.Source == "TMDB";
            var note = found.Note.Length > 0 ? " " + found.Note : "";
            Message = results.Count switch
            {
                0 when found.Note.Length > 0 => found.Note,
                0 => fromTmdb ? "TMDB nič nenašlo – skúste iný názov alebo zadajte ID." : "Katalóg HISTRA nič nenašiel – skúste iný názov alebo rok.",
                1 => found.Note,
                _ => (fromTmdb ? $"TMDB našlo {results.Count} titulov" : $"Katalóg HISTRA našiel {results.Count} titulov") + " – vyberte správny." + note,
            };
            if (results.Count == 1)
            {
                Selected = results[0];
            }
        }
        catch (TitleServiceException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            titles.Waiting -= OnWaiting;
            _busy = false;
            SearchCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasResults));
            IsOpen = HasResults;
        }
    }

    /// <summary>Text for the title field from a TMDB title (by the specification) and a note when something is missing.</summary>
    public static (string Text, string Note) TitleText(TmdbTitle title)
    {
        if (title.ToTitleInfo() is { } info)
        {
            return (info.FolderName, "");
        }

        var year = title.Year is { } y ? $"({y}) " : "";
        return ($"{year}[tmdbid-{title.Id}]", "TMDB nemá anglický názov – napíšte ho pred rok.");
    }

    internal static void TrySaveCache(TmdbClient tmdb) => TrySave(tmdb.SaveCache);

    internal static void TrySaveCache(TitleSearch titles) => TrySave(titles.SaveCache);

    private static void TrySave(Action save)
    {
        try
        {
            save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the cache is only an optimisation
        }
    }
}
