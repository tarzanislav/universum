using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;

namespace Universum.App.Pages;

/// <summary>
/// Looks up the text of a title field (an ID or address, or "Name (Year)") on TMDB – without a TMDB key, or when TMDB
/// finds nothing, in the HISTRA catalog – and checks TMDB's title in HISTRA (synchronisation, 27. 10. 2026). The chosen
/// title is handed back to the page. A title HISTRA does not confirm (it finds nothing or another title), or a name
/// found nowhere, waits for the user's confirmation: <see cref="NeedsConfirmation"/> stops writing until
/// <see cref="ConfirmCommand"/> or a title typed by the user.
/// </summary>
public sealed class TmdbPicker : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<MediaKind> _kind;
    private readonly Func<string> _text;
    private readonly Action<TmdbTitle> _chosen;
    private TmdbTitle? _selected;
    private TitleResults? _found;
    private bool _isOpen;
    private bool _busy;
    private bool _needsConfirmation;
    private string _message = "";
    private string _warning = "";

    public TmdbPicker(AppServices services, Func<MediaKind> kind, Func<string> text, Action<TmdbTitle> chosen)
    {
        _services = services;
        _kind = kind;
        _text = text;
        _chosen = chosen;
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !_busy && _services.Titles is not null);
        ConfirmCommand = new RelayCommand(Confirm, () => _needsConfirmation);
    }

    public AsyncRelayCommand SearchCommand { get; }

    /// <summary>The user confirms the title HISTRA did not confirm (or the suggestion found nowhere).</summary>
    public RelayCommand ConfirmCommand { get; }

    /// <summary>Raised when a title starts or stops waiting for confirmation (the page recomputes its target).</summary>
    public event Action? ConfirmationChanged;

    public ObservableCollection<TmdbTitle> Results { get; } = [];

    public bool HasResults => Results.Count > 1;

    public bool IsOpen { get => _isOpen; set => SetProperty(ref _isOpen, value); }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>How HISTRA confirmed the chosen title.</summary>
    public TitleCheck Check { get; private set; }

    /// <summary>The title waits for the user's confirmation; nothing is written with it until then.</summary>
    public bool NeedsConfirmation { get => _needsConfirmation; private set { if (SetProperty(ref _needsConfirmation, value)) { ConfirmCommand.NotifyCanExecuteChanged(); } } }

    /// <summary>Why the title waits for confirmation.</summary>
    public string Warning { get => _warning; private set => SetProperty(ref _warning, value); }

    public string Tooltip =>
        _services.Tmdb is not null
            ? "Nájde titul na TMDB podľa ID, adresy alebo názvu a roku" +
              (_services.Histra is not null ? "; overí ho v katalógu HISTRA (čo TMDB nenájde, hľadá v HISTRA)" : "")
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
                Judge(value);
                if (value is { Kind: MediaKind.Series, Seasons.Count: 0 } && _services.Tmdb is { } tmdb)
                {
                    // a search result has no seasons: the details do, for the check that the episode exists
                    _ = ChooseWithSeasonsAsync(tmdb, value);
                }
                else
                {
                    _chosen(value);
                    if (value is { Kind: MediaKind.Series, Seasons.Count: 0 } && !NeedsConfirmation)
                    {
                        Message = Join(Message, "Bez kľúča TMDB sa neoverí, či sezóna a epizóda existujú.");
                    }
                }
            }
        }
    }

    /// <summary>The user typed a title or another file was opened: nothing waits for confirmation.</summary>
    public void ClearConfirmation()
    {
        Check = TitleCheck.NotChecked;
        Warning = "";
        if (NeedsConfirmation)
        {
            NeedsConfirmation = false;
            Message = "";
            ConfirmationChanged?.Invoke();
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
        _found = null;
        OnPropertyChanged(nameof(Selected));
        ClearConfirmation();
        var text = _text();
        Message = titles.Tmdb is not null ? "Hľadám na TMDB…" : "Hľadám v katalógu HISTRA…";
        void OnWaiting(TimeSpan delay) => Message = $"Služba žiada spomaliť – čakám {Math.Ceiling(delay.TotalSeconds):0} s…";
        titles.Waiting += OnWaiting;
        try
        {
            var found = await titles.FindAsync(_kind(), text);
            _found = found;
            TrySaveCache(titles);
            var results = found.Titles;
            foreach (var result in results.Take(12))
            {
                Results.Add(result);
            }

            var fromTmdb = found.Source == "TMDB";
            var typed = TitleGuess.Typed(_kind(), text);
            var best = results.Count == 1 ? results[0] : typed is null ? null : found.Best(typed.Name, typed.Year);
            Message = results.Count switch
            {
                0 when found.FoundNowhere => "",
                0 when found.Note.Length > 0 => found.Note,
                0 => fromTmdb ? "TMDB nič nenašlo – skúste iný názov alebo zadajte ID." : "Katalóg HISTRA nič nenašiel – skúste iný názov alebo rok.",
                1 => found.Note,
                _ when best is not null => Join($"{(fromTmdb ? "TMDB našlo" : "Katalóg HISTRA našiel")} {results.Count} titulov, vybraný je ten, ktorý potvrdzuje HISTRA.", found.Note),
                _ => Join($"{(fromTmdb ? "TMDB našlo" : "Katalóg HISTRA našiel")} {results.Count} titulov – vyberte správny.", found.Note),
            };

            // found nowhere: the suggestion in the field (from the file's name, or typed) waits for confirmation
            if (found.FoundNowhere && text.Trim().Length > 0)
            {
                Wait($"{(titles.Tmdb is not null ? "TMDB ani HISTRA titul nenašli" : "Katalóg HISTRA titul nenašiel")} – ponechaný je návrh „{text.Trim()}“. " +
                     "Skontrolujte ho a potvrďte (alebo ho opravte).");
            }

            if (best is not null)
            {
                Selected = best;
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
            IsOpen = HasResults && _selected is null;
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

    /// <summary>How the HISTRA check of a title reads (for messages of the batch).</summary>
    public static string Describe(TitleResults found, TmdbTitle title) => found.Check(title) switch
    {
        TitleCheck.Confirmed => "HISTRA titul potvrdila.",
        TitleCheck.NotFound => "HISTRA tento titul nenašla – skontrolujte ho a potvrďte.",
        TitleCheck.Different when found.Alternative(title) is { } other => $"HISTRA našla iný titul: {other.Display} (TMDB {other.Id}) – skontrolujte a potvrďte.",
        TitleCheck.Different => "HISTRA našla iný titul – skontrolujte a potvrďte.",
        _ => "",
    };

    private void Judge(TmdbTitle title)
    {
        Check = _found?.Check(title) ?? TitleCheck.NotChecked;
        var description = _found is null ? "" : Describe(_found, title);
        if (Check is TitleCheck.NotFound or TitleCheck.Different)
        {
            Wait($"Titul z TMDB {title.Display} (TMDB {title.Id}): {description}");
        }
        else
        {
            Warning = "";
            if (NeedsConfirmation)
            {
                NeedsConfirmation = false;
                ConfirmationChanged?.Invoke();
            }

            if (Check == TitleCheck.Confirmed)
            {
                Message = Join(_found?.Note ?? "", description);
            }
        }
    }

    private void Wait(string warning)
    {
        Warning = warning;
        Message = "⚠ " + warning;
        NeedsConfirmation = true;
        ConfirmationChanged?.Invoke();
    }

    private void Confirm()
    {
        if (!NeedsConfirmation)
        {
            return;
        }

        NeedsConfirmation = false;
        Warning = "";
        Message = "Titul je potvrdený.";
        ConfirmationChanged?.Invoke();
    }

    private async Task ChooseWithSeasonsAsync(TmdbClient tmdb, TmdbTitle title)
    {
        var chosen = title;
        var before = Message;
        try
        {
            Message = "Načítavam sezóny z TMDB…";
            chosen = await tmdb.GetAsync(MediaKind.Series, title.Id) ?? title;
            TrySaveCache(tmdb);
            Message = before;
        }
        catch (TmdbException ex)
        {
            Message = Join(before, "Sezóny seriálu sa nenačítali (epizódy sa nedajú overiť): " + ex.Message);
        }

        if (ReferenceEquals(_selected, title))
        {
            _chosen(chosen);
        }
    }

    private static string Join(string first, string second) =>
        first.Length == 0 ? second : second.Length == 0 ? first : first + " " + second;

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
