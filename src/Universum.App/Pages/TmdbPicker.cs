using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;

namespace Universum.App.Pages;

/// <summary>
/// Looks up the text of a title field on TMDB (an ID or address, or "Name (Year)") and offers the results; the chosen
/// result is handed back to the page. Without a TMDB key in the settings the button is disabled.
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
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !_busy && _services.Tmdb is not null);
    }

    public AsyncRelayCommand SearchCommand { get; }

    public ObservableCollection<TmdbTitle> Results { get; } = [];

    public bool HasResults => Results.Count > 1;

    public bool IsOpen { get => _isOpen; set => SetProperty(ref _isOpen, value); }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public string Tooltip => _services.Tmdb is null
        ? "Vyhľadávanie na TMDB: zadajte svoj kľúč TMDB v Nastaveniach"
        : "Nájde titul na TMDB podľa ID, adresy alebo názvu a roku";

    public TmdbTitle? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value) && value is not null)
            {
                _chosen(value);
            }
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
        if (_services.Tmdb is not { } tmdb)
        {
            return;
        }

        _busy = true;
        SearchCommand.NotifyCanExecuteChanged();
        Results.Clear();
        _selected = null;
        OnPropertyChanged(nameof(Selected));
        Message = "Hľadám na TMDB…";
        try
        {
            var results = await tmdb.FindAsync(_kind(), _text());
            TrySaveCache(tmdb);
            foreach (var result in results.Take(12))
            {
                Results.Add(result);
            }

            Message = results.Count switch
            {
                0 => "TMDB nič nenašlo – skúste iný názov alebo zadajte ID.",
                1 => "",
                _ => $"TMDB našlo {results.Count} titulov – vyberte správny.",
            };
            if (results.Count == 1)
            {
                Selected = results[0];
            }
        }
        catch (TmdbException ex)
        {
            Message = ex.Message;
        }
        finally
        {
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

    internal static void TrySaveCache(TmdbClient tmdb)
    {
        try
        {
            tmdb.SaveCache();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the cache is only an optimisation
        }
    }
}
