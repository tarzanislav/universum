using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Library;
using Universum.Core.Library.Histra;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>
/// Listy z HISTRA: the user's lists imported from HISTRA – only read from there (nothing is written to HISTRA), kept
/// in a file of this Windows account and refreshed only with the Synchronizovať button. Each list shows its name and
/// the posters of its titles; a click on a title shows everything HISTRA tells about it with its IDs and links.
/// </summary>
public sealed class HistraListsPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _filter = "";
    private string _status = "";
    private bool _busy;
    private HistraItemRow? _selected;
    private TitleCardViewModel? _card;
    private CancellationTokenSource? _work;

    public HistraListsPageViewModel(AppServices services)
    {
        _services = services;
        ImportCommand = new AsyncRelayCommand(ImportFromWindowAsync, () => !Busy && CanUse);
        SyncCommand = new AsyncRelayCommand(SyncAsync, () => !Busy && CanUse && Lists.Count > 0);
        StopCommand = new RelayCommand(() => _work?.Cancel(), () => Busy);
        CloseDetailCommand = new RelayCommand(() => Selected = null);
        ToPosterCommand = new AsyncRelayCommand(async () =>
        {
            if (Card is { TmdbId: { } id } card)
            {
                await OpenInPoster(card.Kind, id);
            }
        }, () => Card?.TmdbId is not null);
        ToSingleCommand = new AsyncRelayCommand(ToSingleAsync, () => Card is not null);
        services.HistraChanged += Refresh;
        services.PostersChanged += () =>
        {
            foreach (var row in Lists.SelectMany(l => l.All))
            {
                row.ResetPosters();
            }
        };

        foreach (var list in services.HistraListStore.Load())
        {
            Lists.Add(new HistraListRow(list, this));
        }

        _status = Lists.Count == 0 ? "" : $"Importované listy: {Lists.Count}.";
    }

    public ObservableCollection<HistraListRow> Lists { get; } = [];

    /// <summary>HISTRA can be asked: a token is stored and connecting is switched on.</summary>
    public bool CanUse => _services.HistraLists is not null;

    /// <summary>What to do before lists can be imported; empty when they can.</summary>
    public string Requirement => CanUse ? ""
        : _services.HistraToken is null ? "Na import listov uložte v Nastaveniach svoj token HISTRA."
        : "Pripájanie k HISTRA je v Nastaveniach vypnuté – importované listy sa dajú prezerať, synchronizovať nie.";

    public bool IsEmpty => Lists.Count == 0;

    public AsyncRelayCommand ImportCommand { get; }

    public AsyncRelayCommand SyncCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand CloseDetailCommand { get; }

    /// <summary>Otvoriť v TMDB Poster (owner, 31. 10. 2026): the title shown opens there (it needs its TMDB ID).</summary>
    public AsyncRelayCommand ToPosterCommand { get; }

    /// <summary>Do Single spracovania (owner, 31. 10. 2026): the title is looked for in the input folder and goes to processing.</summary>
    public AsyncRelayCommand ToSingleCommand { get; }

    /// <summary>Opens a title in TMDB poster (set by the main window).</summary>
    public Func<MediaKind, int, Task> OpenInPoster { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>
    /// Sends a title to processing (set by the main window): its kind, names, year and caption; what happened in words when
    /// it was not sent, null when it was.
    /// </summary>
    public Func<MediaKind, IReadOnlyList<string>, int?, string, Task<string?>> SendToSingle { get; set; } = (_, _, _, _) => Task.FromResult<string?>("Nedá sa.");

    // the names of the title shown: TMDB's when its detail came, else HISTRA's
    private async Task ToSingleAsync()
    {
        if (Card is not { } card)
        {
            return;
        }

        var item = Selected?.Item;
        var (names, year, caption) = card.Details is { } details
            ? (details.Names, details.Year, card.MainName)
            : item is not null
                ? ((IReadOnlyList<string>)new[] { item.OriginalTitle, item.Title }.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList(), item.Year,
                    item.Year is { } y ? $"{item.OriginalTitle} ({y})" : item.OriginalTitle)
                : ([], null, card.MainName);
        if (names.Count == 0)
        {
            return;
        }

        Status = "Hľadám titul vo vstupnom priečinku…";
        Status = await SendToSingle(card.Kind, names, year, caption) ?? "";
    }
    /// <summary>Shows the import window; the self-test answers instead.</summary>
    public Func<HistraImportViewModel, bool> ShowImport { get; set; } = model =>
        new HistraImportWindow(model) { Owner = Application.Current.MainWindow }.ShowDialog() == true;

    /// <summary>Asks before a list is removed from UNIVERSUM; the self-test answers instead.</summary>
    public Func<string, bool> Confirm { get; set; } = question =>
        MessageBox.Show(Application.Current.MainWindow!, question, "UNIVERSUM", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Titles shown only when their name (Czech or original) or year contains this, without diacritics.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                foreach (var list in Lists)
                {
                    list.ApplyFilter(value);
                }
            }
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                ImportCommand.NotifyCanExecuteChanged();
                SyncCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The title shown in the detail panel; null hides it.</summary>
    public HistraItemRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelected));
                Card = value is null ? null : CardOf(value);
            }
        }
    }

    public bool HasSelected => Selected is not null;

    /// <summary>
    /// The card of the title chosen (owner, 31. 10. 2026: as in TMDB poster – HISTRA's data at once, TMDB's detail when it
    /// comes); a film of its collection opens in it.
    /// </summary>
    public TitleCardViewModel? Card
    {
        get => _card;
        private set
        {
            if (SetProperty(ref _card, value))
            {
                ToPosterCommand.NotifyCanExecuteChanged();
                ToSingleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private TitleCardViewModel CardOf(HistraItemRow row)
    {
        var item = row.Item;
        var card = new TitleCardViewModel(_services, item.Kind, item.Ids.Tmdb,
            new TitleSeed(item.OriginalTitle, item.Title != item.OriginalTitle ? item.Title : "", item.Year, item.Overview, item.Rating, item.Runtime, item.PosterPath, item.Ids));
        card.OpenRequested += Open;
        return card;
    }

    private void Open(MediaKind kind, int id)
    {
        var card = new TitleCardViewModel(_services, kind, id);
        card.OpenRequested += Open;
        Card = card;
    }

    /// <summary>
    /// The user's lists from HISTRA for the import window (imported ones ticked); null when HISTRA could not be asked
    /// (the reason is in <see cref="Status"/>).
    /// </summary>
    public async Task<HistraImportViewModel?> PrepareImportAsync()
    {
        if (_services.HistraLists is not { } histra)
        {
            Status = Requirement;
            return null;
        }

        Busy = true;
        Status = "Načítavam vaše listy z HISTRA…";
        try
        {
            var overview = await histra.OverviewAsync();
            Status = overview.Count == 0 ? "V HISTRA nemáte žiadne listy." : "";
            return overview.Count == 0 ? null : new HistraImportViewModel(_services, overview, Lists.Select(l => l.List.Info.Id).ToHashSet());
        }
        catch (TitleServiceException ex)
        {
            Status = ex.Message;
            return null;
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ImportFromWindowAsync()
    {
        if (await PrepareImportAsync() is { } model && ShowImport(model))
        {
            await ImportAsync(model.Chosen);
        }
    }

    /// <summary>Reads the chosen lists with all their titles and keeps them (a list imported again is refreshed).</summary>
    public async Task ImportAsync(IReadOnlyList<HistraListInfo> chosen)
    {
        if (chosen.Count == 0)
        {
            return;
        }

        var read = await ReadAsync(chosen, "Importujem");
        foreach (var list in read.Lists)
        {
            Replace(list);
        }

        Save();
        Status = read.Problem ?? $"Importované: {Texts.Count(read.Lists.Count, "list", "listy", "listov")} " +
            $"({Texts.Count(read.Lists.Sum(l => l.Items.Count), "titul", "tituly", "titulov")}).";
    }

    /// <summary>
    /// Synchronizovať: every imported list is read again from HISTRA (only on this button, never by itself). A list
    /// HISTRA no longer has keeps its titles in UNIVERSUM and is marked; it can be removed by hand.
    /// </summary>
    public async Task SyncAsync()
    {
        var infos = Lists.Select(l => l.List.Info).ToList();
        var read = await ReadAsync(infos, "Synchronizujem");
        foreach (var list in read.Lists)
        {
            Replace(list);
        }

        foreach (var id in read.Missing)
        {
            if (Lists.FirstOrDefault(l => l.List.Info.Id == id) is { } row)
            {
                row.Update(row.List with { MissingInHistra = true });
            }
        }

        Save();
        Status = read.Problem ?? $"Synchronizované {DateTime.Now:d. M. yyyy H:mm}: {Texts.Count(read.Lists.Count, "list", "listy", "listov")}" +
            (read.Missing.Count > 0 ? $"; v HISTRA už nie je: {read.Missing.Count} (ostávajú tu, odstrániť ich môžete sami)." : ".");
    }

    private async Task<(List<HistraList> Lists, List<int> Missing, string? Problem)> ReadAsync(IReadOnlyList<HistraListInfo> infos, string verb)
    {
        var lists = new List<HistraList>();
        var missing = new List<int>();
        if (_services.HistraLists is not { } histra)
        {
            return (lists, missing, Requirement);
        }

        Busy = true;
        var work = _work = new CancellationTokenSource();
        try
        {
            for (var i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                var position = $"{verb} {i + 1}/{infos.Count}: {info.Name}";
                Status = position + "…";
                // a report that arrives after the reading ended does not cover the result
                var progress = new Progress<(int Read, int Total)>(p =>
                {
                    if (Busy)
                    {
                        Status = $"{position} – {p.Read} z {p.Total} titulov…";
                    }
                });
                if (await histra.ReadAsync(info.Id, progress, work.Token) is { } list)
                {
                    lists.Add(list);
                }
                else
                {
                    missing.Add(info.Id);
                }
            }

            return (lists, missing, null);
        }
        catch (OperationCanceledException)
        {
            return (lists, missing, $"Zastavené – uložené je, čo sa stihlo načítať ({Texts.Count(lists.Count, "list", "listy", "listov")}).");
        }
        catch (TitleServiceException ex)
        {
            return (lists, missing, ex.Message + (lists.Count > 0 ? $" Uložené je, čo sa stihlo načítať ({lists.Count})." : ""));
        }
        finally
        {
            work.Dispose();
            _work = null;
            Busy = false;
        }
    }

    internal void Show(HistraItemRow row) => Selected = row;

    internal void Remove(HistraListRow row)
    {
        if (!Confirm($"Odstrániť list „{row.Name}“ z UNIVERSUM?\n\nV HISTRA ostane bez zmeny, dá sa kedykoľvek importovať znova."))
        {
            return;
        }

        if (Selected is { } selected && row.All.Contains(selected))
        {
            Selected = null;
        }

        Lists.Remove(row);
        Save();
        Status = $"List „{row.Name}“ je odstránený z UNIVERSUM (v HISTRA ostal).";
        Refresh();
    }

    private void Replace(HistraList list)
    {
        if (Lists.FirstOrDefault(l => l.List.Info.Id == list.Info.Id) is { } existing)
        {
            if (Selected is { } selected && existing.All.Contains(selected))
            {
                Selected = null;
            }

            existing.Update(list);
            existing.ApplyFilter(Filter);
        }
        else
        {
            var row = new HistraListRow(list, this);
            row.ApplyFilter(Filter);
            Lists.Add(row);
        }

        Refresh();
    }

    private void Save() => _services.HistraListStore.Save(Lists.Select(l => l.List));

    /// <summary>Moves a list to another place (arrows or dragging with the mouse); the order is kept.</summary>
    public void MoveTo(HistraListRow row, int index)
    {
        var from = Lists.IndexOf(row);
        index = Math.Clamp(index, 0, Lists.Count - 1);
        if (from < 0 || from == index)
        {
            return;
        }

        Lists.Move(from, index);
        Save();
        foreach (var list in Lists)
        {
            list.PlaceChanged();
        }

        Status = $"List „{row.Name}“ je na {index + 1}. mieste.";
    }

    internal bool IsFirst(HistraListRow row) => Lists.IndexOf(row) <= 0;

    internal bool IsLast(HistraListRow row) => Lists.IndexOf(row) == Lists.Count - 1;

    /// <summary>Updates what depends on HISTRA being usable (token, switch).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(CanUse));
        OnPropertyChanged(nameof(Requirement));
        OnPropertyChanged(nameof(IsEmpty));
        ImportCommand.NotifyCanExecuteChanged();
        SyncCommand.NotifyCanExecuteChanged();
        foreach (var list in Lists)
        {
            list.PlaceChanged();
        }
    }

    internal AppServices Services => _services;
}

/// <summary>An imported list: its name and summary, then the posters of its titles (the first ones, or all).</summary>
public sealed class HistraListRow : ObservableObject
{
    /// <summary>Titles shown before "Zobraziť všetky".</summary>
    public const int Collapsed = 24;

    private static readonly Color[] Tints =
    [
        Color.FromRgb(0x9B, 0x7B, 0xE0), Color.FromRgb(0x5B, 0xA8, 0xE0), Color.FromRgb(0x5C, 0xC0, 0x8E),
        Color.FromRgb(0xE8, 0x9A, 0x5A), Color.FromRgb(0xE0, 0x72, 0x9A), Color.FromRgb(0x4F, 0xB8, 0xB8),
    ];

    private readonly HistraListsPageViewModel _page;
    private List<HistraItemRow> _matched = [];
    private bool _expanded;

    public HistraListRow(HistraList list, HistraListsPageViewModel page)
    {
        _page = page;
        List = list;
        All = Rows(list);
        _matched = All.ToList();
        ToggleCommand = new RelayCommand(() => Expanded = !Expanded);
        RemoveCommand = new RelayCommand(() => _page.Remove(this));
        MoveUpCommand = new RelayCommand(() => _page.MoveTo(this, _page.Lists.IndexOf(this) - 1), () => !_page.IsFirst(this));
        MoveDownCommand = new RelayCommand(() => _page.MoveTo(this, _page.Lists.IndexOf(this) + 1), () => !_page.IsLast(this));
        var tint = Tints[(int)((uint)list.Info.Id % Tints.Length)];
        Accent = Frozen(tint);
        Tint = Frozen(Color.FromArgb(0x26, tint.R, tint.G, tint.B));
        ShowVisible();
    }

    public HistraList List { get; private set; }

    public IReadOnlyList<HistraItemRow> All { get; private set; }

    /// <summary>Moves the list one place up.</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>Moves the list one place down.</summary>
    public RelayCommand MoveDownCommand { get; }

    /// <summary>The order of the lists changed (the arrows of the first and last list).</summary>
    internal void PlaceChanged()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<HistraItemRow> Visible { get; } = [];

    public string Name => List.Info.Name;

    public string Description => List.Info.Description;

    public bool HasDescription => Description.Length > 0;

    /// <summary>"1 051 titulov · 1 043 filmov · 8 seriálov · v HISTRA zmenený 20. 10. 2026 · načítaný …".</summary>
    public string Summary
    {
        get
        {
            var films = All.Count(r => r.Item.Kind == MediaKind.Movie);
            var parts = new List<string> { Texts.Count(All.Count, "titul", "tituly", "titulov") };
            if (films > 0)
            {
                parts.Add(Texts.Count(films, "film", "filmy", "filmov"));
            }

            if (All.Count - films > 0)
            {
                parts.Add(Texts.Count(All.Count - films, "seriál", "seriály", "seriálov"));
            }

            if (List.Info.UpdatedAt is { } updated)
            {
                parts.Add($"v HISTRA zmenený {updated.ToLocalTime():d. M. yyyy}");
            }

            parts.Add($"načítaný {List.ReadAt.ToLocalTime():d. M. yyyy H:mm}");
            return string.Join(" · ", parts);
        }
    }

    public bool Missing => List.MissingInHistra;

    /// <summary>The list's own colour (a strip at its name and the placeholders of its posters).</summary>
    public Brush Accent { get; }

    public Brush Tint { get; }

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (SetProperty(ref _expanded, value))
            {
                ShowVisible();
            }
        }
    }

    public bool CanExpand => _matched.Count > Collapsed;

    public string ExpandText => Expanded ? "Zobraziť menej" : $"Zobraziť všetky ({_matched.Count.ToString("N0", Texts.Slovak)})";

    /// <summary>With a filter: how many titles of the list match it.</summary>
    public string FilterResult { get; private set; } = "";

    public RelayCommand ToggleCommand { get; }

    public RelayCommand RemoveCommand { get; }

    internal void Update(HistraList list)
    {
        List = list;
        All = Rows(list);
        _matched = All.ToList();
        ShowVisible();
        OnPropertyChanged("");
    }

    internal void ApplyFilter(string filter)
    {
        var wanted = Words.Normalize(filter);
        _matched = wanted.Length == 0 ? All.ToList() : All.Where(r => r.Matches(wanted)).ToList();
        FilterResult = wanted.Length == 0 ? "" : $"Zhoda: {_matched.Count}";
        OnPropertyChanged(nameof(FilterResult));
        ShowVisible();
    }

    private void ShowVisible()
    {
        Visible.Clear();
        foreach (var row in Expanded ? _matched : _matched.Take(Collapsed))
        {
            Visible.Add(row);
        }

        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(ExpandText));
    }

    private List<HistraItemRow> Rows(HistraList list) => list.Items.Select(i => new HistraItemRow(i, _page.Services, _page.Show, this)).ToList();

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>A title of a list: its poster in the list, and in the detail everything HISTRA tells about it.</summary>
public sealed class HistraItemRow : ObservableObject
{
    private readonly string _search;

    public HistraItemRow(HistraListItem item, AppServices services, Action<HistraItemRow> show, HistraListRow list)
    {
        Item = item;
        List = list;
        SmallPoster = new PosterThumb(services, item.PosterPath, PosterSize.Small);
        LargePoster = new PosterThumb(services, item.PosterPath, PosterSize.Large);
        OpenCommand = new RelayCommand(() => show(this));
        _search = Words.Normalize($"{item.Title} {item.OriginalTitle} {item.Year}");
        Links = new[]
        {
            ("HISTRA", TitleLinks.Histra(item.Kind, item.Ids.Histra)),
            ("TMDB", TitleLinks.Tmdb(item.Kind, item.Ids.Tmdb)),
            ("IMDb", TitleLinks.Imdb(item.Ids.Imdb)),
            ("ČSFD (hľadať)", TitleLinks.CsfdSearch(LibraryNames.IsLatinScript(item.OriginalTitle) ? item.OriginalTitle : item.Title)),
        }.Where(l => l.Item2 is not null).Select(l => new TitleLink(l.Item1, l.Item2!)).ToList();
        Ids = new[]
        {
            ("HISTRA", item.Ids.Histra?.ToString(CultureInfo.InvariantCulture)),
            ("TMDB", item.Ids.Tmdb?.ToString(CultureInfo.InvariantCulture)),
            ("IMDb", item.Ids.Imdb),
            ("Trakt", item.Ids.Trakt?.ToString(CultureInfo.InvariantCulture)),
            ("TVDB", item.Ids.Tvdb?.ToString(CultureInfo.InvariantCulture)),
        }.Where(i => i.Item2 is not null).Select(i => new TitleDetail(i.Item1, i.Item2!)).ToList();
    }

    public HistraListItem Item { get; }

    public HistraListRow List { get; }

    /// <summary>Original title and year – the name of the title in the library.</summary>
    public string Caption => Item.Caption;

    /// <summary>The Czech title when it differs from the original.</summary>
    public string LocalTitle => Item.Title != Item.OriginalTitle ? Item.Title : "";

    public bool HasLocalTitle => LocalTitle.Length > 0;

    public string ToolTip => LocalTitle.Length > 0 ? $"{Caption}\n{LocalTitle}" : Caption;

    public string Facts => string.Join(" · ", new[]
    {
        Item.Kind == MediaKind.Movie ? "Film" : "Seriál",
        Item.Runtime is { } minutes ? $"{minutes} min" : null,
        Item.Rating is { } rating ? $"hodnotenie {rating.ToString("0.0", Texts.Slovak)}" : null,
        Item.Status.Length > 0 ? Status(Item.Status) : null,
    }.OfType<string>());

    public string Overview => Item.Overview.Length > 0 ? Item.Overview : "HISTRA k titulu nemá popis.";

    public PosterThumb SmallPoster { get; }

    public PosterThumb LargePoster { get; }

    public IReadOnlyList<TitleLink> Links { get; }

    public IReadOnlyList<TitleDetail> Ids { get; }

    public IReadOnlyList<TitleDetail> Other => Item.Other;

    public bool HasOther => Item.Other.Count > 0;

    public RelayCommand OpenCommand { get; }

    internal bool Matches(string normalized) => _search.Contains(normalized, StringComparison.Ordinal);

    internal void ResetPosters()
    {
        SmallPoster.Reset();
        LargePoster.Reset();
    }

    private static string Status(string status) => status switch
    {
        "Ended" => "ukončený",
        "Canceled" or "Cancelled" => "zrušený",
        "Returning Series" => "pokračuje",
        "In Production" => "vo výrobe",
        "Planned" => "plánovaný",
        "Released" => "vydaný",
        _ => status,
    };
}

/// <summary>A link of a title, opened in the browser.</summary>
public sealed class TitleLink(string text, Uri address)
{
    public string Text { get; } = text;

    public Uri Address { get; } = address;

    public string ToolTip => Address.AbsoluteUri;

    public RelayCommand OpenCommand => _open ??= new RelayCommand(() => Browser.Open(Address));

    private RelayCommand? _open;
}

/// <summary>The import window: the user's lists in HISTRA, each with its name and preview posters, to tick.</summary>
public sealed class HistraImportViewModel : ObservableObject
{
    private string _filter = "";

    public HistraImportViewModel(AppServices services, IReadOnlyList<HistraListInfo> lists, IReadOnlySet<int> imported)
    {
        Rows = lists.Select(l => new HistraImportRow(services, l, imported.Contains(l.Id), () => OnPropertyChanged(nameof(ImportText)))).ToList();
        Visible = new ObservableCollection<HistraImportRow>(Rows);
        AllCommand = new RelayCommand(() => SetAll(true));
        NoneCommand = new RelayCommand(() => SetAll(false));
    }

    public IReadOnlyList<HistraImportRow> Rows { get; }

    public ObservableCollection<HistraImportRow> Visible { get; }

    public IReadOnlyList<HistraListInfo> Chosen => Rows.Where(r => r.IsChecked).Select(r => r.Info).ToList();

    public string ImportText => $"Importovať vybrané ({Rows.Count(r => r.IsChecked)})";

    public string Summary => $"V HISTRA máte {Texts.Count(Rows.Count, "list", "listy", "listov")}. Vyberte, ktoré má mať UNIVERSUM – " +
                             "importuje sa ich obsah s plagátmi, názvami a ID. Do HISTRA sa nič nezapisuje.";

    public RelayCommand AllCommand { get; }

    public RelayCommand NoneCommand { get; }

    /// <summary>Only lists whose name contains this (without diacritics).</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                var wanted = Words.Normalize(value);
                Visible.Clear();
                foreach (var row in Rows.Where(r => wanted.Length == 0 || Words.Normalize(r.Name).Contains(wanted, StringComparison.Ordinal)))
                {
                    Visible.Add(row);
                }
            }
        }
    }

    private void SetAll(bool value)
    {
        foreach (var row in Visible)
        {
            row.IsChecked = value;
        }
    }
}

/// <summary>A list in the import window.</summary>
public sealed class HistraImportRow : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    public HistraImportRow(AppServices services, HistraListInfo info, bool imported, Action changed)
    {
        Info = info;
        Imported = imported;
        _isChecked = imported;
        _changed = changed;
        Posters = info.PreviewPosters.Take(5).Select(p => new PosterThumb(services, p, PosterSize.Small)).ToList();
    }

    public HistraListInfo Info { get; }

    public string Name => Info.Name;

    public bool Imported { get; }

    public string Summary => string.Join(" · ", new[]
    {
        Texts.Count(Info.ItemCount, "titul", "tituly", "titulov"),
        Info.Visibility switch { "public" => "verejný", "private" => "súkromný", "" => null, var other => other },
        Info.UpdatedAt is { } updated ? $"zmenený {updated.ToLocalTime():d. M. yyyy}" : null,
        Info.IsMine ? null : Info.OwnerName.Length > 0 ? $"od {Info.OwnerName}" : "cudzí list",
        Imported ? "už je v UNIVERSUM – načíta sa znova" : null,
    }.OfType<string>());

    public IReadOnlyList<PosterThumb> Posters { get; }

    public string MoreText => Info.ItemCount > Posters.Count && Posters.Count > 0 ? $"+ {(Info.ItemCount - Posters.Count).ToString("N0", Texts.Slovak)}" : "";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _changed();
            }
        }
    }
}
