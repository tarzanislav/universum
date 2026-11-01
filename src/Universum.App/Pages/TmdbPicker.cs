using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
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
    private string? _pendingText;
    private int _version;

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
            ? Loc.T("Nájde titul na TMDB podľa ID, adresy alebo názvu a roku") +
              (_services.Histra is not null ? Loc.T("; overí ho v katalógu HISTRA (čo TMDB nenájde, hľadá v HISTRA)") : "")
        : _services.Histra is not null ? Loc.T("Nájde titul v katalógu HISTRA podľa názvu a roku (ID a adresy TMDB načíta len TMDB s kľúčom)")
        : _services.TmdbKey is null ? Loc.T("Vyhľadávanie titulov: zadajte v Nastaveniach kľúč TMDB alebo token HISTRA")
        : Loc.T("Pripájanie k TMDB je vypnuté v Nastaveniach");

    public TmdbTitle? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value) && value is not null)
            {
                var version = ++_version;
                if (value is { Kind: MediaKind.Series, Seasons.Count: 0 } && _services.Tmdb is { } tmdb)
                {
                    // a search result has no seasons: the details do, for the check that the episode exists; until they
                    // come the page keeps what it has (also a title waiting for confirmation)
                    _ = ChooseWithSeasonsAsync(tmdb, value, version);
                }
                else
                {
                    Apply(value, value);
                    if (value is { Kind: MediaKind.Series, Seasons.Count: 0 } && !NeedsConfirmation)
                    {
                        Message = Join(Message, Loc.T("Bez kľúča TMDB sa neoverí, či sezóna a epizóda existujú."));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Another file was opened: a search or a selection still running is forgotten and nothing waits for confirmation.
    /// </summary>
    public void ClearConfirmation()
    {
        _version++;
        _pendingText = null;
        _selected = null;
        OnPropertyChanged(nameof(Selected));
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        Unblock();
    }

    /// <summary>
    /// The user edited the title field: a title typed by the user is the user's decision – but the very text that
    /// waited for confirmation waits again. A search or a selection still running is forgotten.
    /// </summary>
    public void TextEdited(string text)
    {
        _version++;
        if (_pendingText is not null && string.Equals(text.Trim(), _pendingText, StringComparison.Ordinal))
        {
            if (!NeedsConfirmation)
            {
                Message = "⚠ " + Warning;
                NeedsConfirmation = true;
                ConfirmationChanged?.Invoke();
            }

            return;
        }

        Unblock();
    }

    private void Unblock()
    {
        Check = TitleCheck.NotChecked;
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

        // what waits for confirmation keeps waiting until the new result decides
        var version = ++_version;
        _busy = true;
        SearchCommand.NotifyCanExecuteChanged();
        Results.Clear();
        _selected = null;
        _found = null;
        OnPropertyChanged(nameof(Selected));
        var text = _text();
        Message = titles.Tmdb is not null ? Loc.T("Hľadám na TMDB…") : Loc.T("Hľadám v katalógu HISTRA…");
        void OnWaiting(TimeSpan delay) => Message = Loc.F("Služba žiada spomaliť – čakám {0:0} s…", Math.Ceiling(delay.TotalSeconds));
        titles.Waiting += OnWaiting;
        try
        {
            var found = await titles.FindAsync(_kind(), text);

            // the name may be written with a mistake (a file's name): without the year, then shortened – only to choose from
            if (found.Titles.Count == 0 && TitleGuess.ParseTmdb(text) is null && TitleGuess.Typed(_kind(), text) is { } asked &&
                await titles.SearchTolerantAsync(_kind(), asked.Name, asked.Year) is { Titles.Count: > 0 } loosened)
            {
                found = loosened with { Loosened = loosened.Loosened ?? "hľadané voľnejšie" };
            }

            TrySaveCache(titles);
            if (version != _version)
            {
                // another file was opened, the text edited or a newer search started meanwhile: this result is not for it
                return;
            }

            _found = found;
            var results = found.Titles;
            var typed = TitleGuess.Typed(_kind(), text);
            var best = found.Loosened is not null ? null : results.Count == 1 ? results[0] : typed is null ? null : found.Best(typed.Name, typed.Year);
            foreach (var result in results.Take(12))
            {
                Results.Add(result);
            }

            if (best is not null && !Results.Contains(best))
            {
                Results.Insert(0, best);
            }

            var fromTmdb = found.Source == "TMDB";
            var many = fromTmdb ? Loc.F("TMDB našlo {0} titulov", results.Count) : Loc.F("Katalóg HISTRA našiel {0} titulov", results.Count);
            // chosen by its name and year (also a translation), by its name a year off (the release here), or by HISTRA
            var byName = typed is not null && best is not null && ReferenceEquals(TmdbClient.ExactMatch(results, typed.Name, typed.Year), best);
            var yearOff = !byName && typed is not null && best is not null && best.IsNamed(typed.Name) &&
                          typed.Year is { } typedYear && best.Year is { } bestYear && Math.Abs(typedYear - bestYear) == 1;
            Message = found.Loosened is { } how ? Loc.F("{0} našlo tituly ({1}) až keď sa {2} – vyberte správny (názov sa môže líšiť).", fromTmdb ? "TMDB" : Loc.T("Katalóg HISTRA"), results.Count, how.Replace("hľadané", "hľadalo"))
                : results.Count switch
            {
                0 when found.FoundNowhere => "",
                0 when found.Note.Length > 0 => found.Note,
                0 => fromTmdb ? Loc.T("TMDB nič nenašlo – skúste iný názov alebo zadajte ID.") : Loc.T("Katalóg HISTRA nič nenašiel – skúste iný názov alebo rok."),
                1 => found.Note,
                _ when best is not null && yearOff => Join(many + Loc.F(", vybraný je ten s rovnakým názvom, rok {0} (o rok iný – možno rok uvedenia u nás).", best.Year), found.Note),
                _ when best is not null && byName => Join(many + Loc.T(", vybraný je ten s presným názvom a rokom."), found.Note),
                _ when best is not null => Join(many + Loc.T(", vybraný je ten, ktorý potvrdzuje katalóg HISTRA."), found.Note),
                _ => Join(many + Loc.T(" – vyberte správny."), found.Note),
            };

            // found nowhere: the suggestion in the field (from the file's name, or typed) waits for confirmation
            if (found.FoundNowhere && text.Trim().Length > 0)
            {
                var nobody = found.TmdbProblem is { } problem ? problem + Loc.T(" Katalóg HISTRA titul nenašiel")
                    : titles.Tmdb is not null ? Loc.T("TMDB ani HISTRA titul nenašli")
                    : Loc.T("Katalóg HISTRA titul nenašiel");
                Wait(Loc.F("{0} – ponechaný je návrh „{1}“. Skontrolujte ho a potvrďte (alebo ho opravte).", nobody, text.Trim()), text.Trim());
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
        return ($"{year}[tmdbid-{title.Id}]", Loc.T("TMDB nemá anglický názov – napíšte ho pred rok."));
    }

    internal static void TrySaveCache(TmdbClient tmdb) => TrySave(tmdb.SaveCache);

    internal static void TrySaveCache(TitleSearch titles) => TrySave(titles.SaveCache);

    /// <summary>How the HISTRA check of a title reads (for messages of the batch).</summary>
    public static string Describe(TitleResults found, TmdbTitle title) => found.Check(title) switch
    {
        TitleCheck.Confirmed => Loc.T("HISTRA titul potvrdila."),
        TitleCheck.NotFound => Loc.T("HISTRA tento titul nenašla – skontrolujte ho a potvrďte."),
        TitleCheck.Different when found.Alternative(title) is { } other => Loc.F("HISTRA našla iný titul: {0} (TMDB {1}) – skontrolujte a potvrďte.", other.Display, other.Id),
        TitleCheck.Different => Loc.T("HISTRA našla iný titul – skontrolujte a potvrďte."),
        TitleCheck.Unavailable => (found.Note.Length > 0 ? found.Note : Loc.T("HISTRA sa nedala opýtať – titul nie je overený.")) + Loc.T(" Skontrolujte ho a potvrďte."),
        _ => "",
    };

    // the chosen title goes to the page – after its check: waiting for confirmation, or confirmed
    private void Apply(TmdbTitle judged, TmdbTitle chosen)
    {
        Check = _found?.Check(judged) ?? TitleCheck.NotChecked;
        var description = _found is null ? "" : Describe(_found, judged);
        if (Check.NeedsConfirmation())
        {
            var (text, _) = TitleText(chosen);
            Wait(Loc.F("Titul z TMDB {0} (TMDB {1}): {2}", judged.Display, judged.Id, description), text);
        }
        else
        {
            _pendingText = null;
            Warning = "";
            if (NeedsConfirmation)
            {
                NeedsConfirmation = false;
                ConfirmationChanged?.Invoke();
            }

            if (Message.StartsWith('⚠'))
            {
                Message = "";
            }

            if (description.Length > 0)
            {
                Message = Join(_found?.Note ?? "", description);
            }
        }

        _chosen(chosen);
    }

    // the title (as the field will hold it) waits for confirmation
    private void Wait(string warning, string text)
    {
        _pendingText = text;
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

        _pendingText = null;
        NeedsConfirmation = false;
        Warning = "";
        Message = Loc.T("Titul je potvrdený.");
        ConfirmationChanged?.Invoke();
    }

    private async Task ChooseWithSeasonsAsync(TmdbClient tmdb, TmdbTitle title, int version)
    {
        var chosen = title;
        var problem = "";
        try
        {
            Message = Loc.T("Načítavam sezóny z TMDB…");
            chosen = await tmdb.GetAsync(MediaKind.Series, title.Id) ?? title;
            TrySaveCache(tmdb);
        }
        catch (TmdbException ex)
        {
            problem = Loc.T("Sezóny seriálu sa nenačítali (epizódy sa nedajú overiť): ") + ex.Message;
        }

        // the text edited, another file opened or another title chosen meanwhile: this one is not applied
        if (version == _version && ReferenceEquals(_selected, title))
        {
            Apply(title, chosen);
            Message = Join(Message, problem);
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
