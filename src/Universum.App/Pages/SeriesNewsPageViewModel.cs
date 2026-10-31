using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Library;
using Universum.Core.News;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>
/// Novinky – Seriály: the episodes of running series aired in the last week, Czech and Slovak channels and the world,
/// from the sources of the settings; older episodes are in the history (kept for good). What is kept is shown at once,
/// the sources are asked only for days not kept or too old. Episodes are ticked and sent to processing: the input folder
/// is searched at once – one file goes to Single spracovanie, several to Hromadné spracovanie (one series at a time; the
/// next series wait here), episodes not there yet wait in the list "Čaká na súbor" until checked again. Series are
/// marked (Vybrané, a note) as in Premiéry. Nothing starts by itself.
/// </summary>
public sealed class SeriesNewsPageViewModel : ObservableObject
{
    /// <summary>The episodes of this many days back (and today) are the news; older ones are the history.</summary>
    public const int RecentDays = 7;

    private readonly AppServices _services;
    private readonly TitleOpener _opener;
    private List<SeriesEpisode> _all = [];
    private List<SeriesEpisode>? _history;
    private NewsTabItem _selected;
    private string _filter = "";
    private NewsView _view = NewsView.Recent;
    private bool _onlyChosen;
    private bool _onlyMyLists;
    private string _status = "";
    private string _problems = "";
    private bool _busy;
    private bool _shownKept;
    private CancellationTokenSource? _work;
    private DateTime? _readAt;

    public SeriesNewsPageViewModel(AppServices services)
    {
        _services = services;
        _opener = new TitleOpener(services);
        Tabs =
        [
            new(NewsRegion.Domestic, "SERIÁL – CZ/SK", Color.FromRgb(0x63, 0xC4, 0x8F)),
            new(NewsRegion.World, "SERIÁL – SVET", Color.FromRgb(0x6F, 0xA8, 0xE8)),
        ];
        _selected = Tabs[0];
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(refresh: true), () => !Busy);
        StopCommand = new RelayCommand(() => _work?.Cancel(), () => Busy);
        SendCommand = new AsyncRelayCommand(SendAsync, () => !Busy && Checked.Any());
        CheckAgainCommand = new AsyncRelayCommand(CheckAgainAsync, () => !Busy && Waiting.Count > 0);
        services.NewsSourcesChanged += () =>
        {
            _readAt = null;
            OnPropertyChanged(nameof(Attribution));
            Show();
        };
        services.NewsCleared += () =>
        {
            _readAt = null;
            _history = null;
            _shownKept = false;
        };
        services.InputDirectoryChanged += () =>
        {
            if (Status == NoInputFolder && services.InputDirectory is not null)
            {
                Status = "Vstupný priečinok je nastavený – epizódy môžete odoslať.";
            }
        };
        services.Marks.Changed += key =>
        {
            foreach (var row in Tabs.SelectMany(t => t.All).Where(r => r.MarkKey == key))
            {
                row.MarkChanged();
            }
        };
        services.PostersChanged += () =>
        {
            foreach (var row in Tabs.SelectMany(t => t.All))
            {
                row.Poster.Reset();
            }
        };
        services.Watchlist.Changed += () =>
        {
            foreach (var row in Tabs.SelectMany(t => t.All))
            {
                row.MarkChanged();
            }
        };
        services.InputWatch.Changed += () =>
        {
            foreach (var row in Tabs.SelectMany(t => t.All))
            {
                row.FolderChanged();
            }
        };
        services.InputDirectoryChanged += () => _ = services.InputWatch.ScanAsync();
    }

    private const string NoInputFolder = "Najprv nastavte vstupný priečinok – tam UNIVERSUM hľadá epizódy (tlačidlo Zmeniť… hore).";

    public InputFolderViewModel InputFolder => _services.InputFolder;

    public IReadOnlyList<NewsTabItem> Tabs { get; }

    public NewsTabItem SelectedTab
    {
        get => _selected;
        set
        {
            if (value is not null && SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(EmptyText));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>Only the series whose name (Czech or original), channel or note contains this, without diacritics.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? ""))
            {
                Show();
            }
        }
    }

    /// <summary>The news: episodes of the last week.</summary>
    public bool ShowRecent
    {
        get => _view == NewsView.Recent;
        set => SetView(value, NewsView.Recent);
    }

    /// <summary>Episodes to come (as far as the sources tell, up to two months).</summary>
    public bool ShowUpcoming
    {
        get => _view == NewsView.Upcoming;
        set => SetView(value, NewsView.Upcoming);
    }

    /// <summary>The history: older episodes (kept for good).</summary>
    public bool ShowHistory
    {
        get => _view == NewsView.History;
        set => SetView(value, NewsView.History);
    }

    /// <summary>Everything: the history, the last week and what is to come (owner, 31. 10. 2026; the latest first).</summary>
    public bool ShowAll
    {
        get => _view == NewsView.All;
        set => SetView(value, NewsView.All);
    }

    private void SetView(bool chosen, NewsView view)
    {
        if (!chosen || _view == view)
        {
            return;
        }

        _view = view;
        OnPropertyChanged(nameof(ShowRecent));
        OnPropertyChanged(nameof(ShowUpcoming));
        OnPropertyChanged(nameof(ShowHistory));
        OnPropertyChanged(nameof(ShowAll));
        if (view is NewsView.History or NewsView.All && _history is null)
        {
            _ = LoadHistoryAsync();
        }

        Build();
    }

    /// <summary>Only the series the user chose (Vybrané).</summary>
    public bool OnlyChosen
    {
        get => _onlyChosen;
        set
        {
            if (SetProperty(ref _onlyChosen, value))
            {
                Show();
            }
        }
    }

    /// <summary>The size of the posters, 1× to 2× (owner, 29. 10. 2026; kept in the settings).</summary>
    public double PosterScale
    {
        get => Math.Clamp(_services.Settings.NewsPosterScale, 1, 2);
        set
        {
            value = Math.Round(Math.Clamp(value, 1, 2), 2);
            if (Math.Abs(PosterScale - value) > 0.001)
            {
                _services.Settings.NewsPosterScale = value;
                _services.Settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(PosterWidth));
                OnPropertyChanged(nameof(PosterHeight));
            }
        }
    }

    /// <summary>The width and height of a poster (2:3) at <see cref="PosterScale"/>.</summary>
    public double PosterWidth => 38 * PosterScale;

    public double PosterHeight => 57 * PosterScale;

    /// <summary>Posters of the series beside the titles (kept in the settings).</summary>
    public bool ShowPosters
    {
        get => _services.Settings.NewsPosters;
        set
        {
            if (_services.Settings.NewsPosters != value)
            {
                _services.Settings.NewsPosters = value;
                _services.Settings.Save();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Only series in the imported HISTRA lists.</summary>
    public bool OnlyMyLists
    {
        get => _onlyMyLists;
        set
        {
            if (SetProperty(ref _onlyMyLists, value))
            {
                Show();
            }
        }
    }

    public bool HasMyLists => _services.HistraListStore.Load().Any(l => l.Items.Any(i => i.Kind == MediaKind.Series));

    /// <summary>A web page with Czech and Slovak channels is among the sources (else the CZ/SK tab stays empty).</summary>
    public bool HasDomesticSource => _services.NewsSources.Any(s => s.Enabled && s.Kind == NewsSourceKind.Page);

    /// <summary>Why the chosen table is empty.</summary>
    public string EmptyText =>
        SelectedTab.Region == NewsRegion.Domestic && !HasDomesticSource
            ? "České a slovenské seriály potrebujú zdroj so stanicami CZ/SK.\nPridajte ho v Nastaveniach → Zdroje noviniek → Importovať zdroj… (súbor pravidiel zdroja)."
            : OnlyChosen ? "Žiadne vybrané seriály – výber zaškrtnete v stĺpci Vybrané."
            : Filter.Length > 0 || OnlyMyLists ? "Nič nezodpovedá hľadaniu."
            : _view == NewsView.Upcoming ? "Žiadne nadchádzajúce epizódy – skúste Obnoviť."
            : ShowHistory ? (_history is null ? "Načítavam históriu…" : "História je zatiaľ prázdna – epizódy sem prechádzajú po týždni.")
            : ShowAll && _history is null ? "Načítavam históriu…"
            : Busy ? "Načítavam…" : "Žiadne nové epizódy – skúste Obnoviť.";

    public bool IsEmpty => SelectedTab.Rows.Count == 0;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public string Problems
    {
        get => _problems;
        private set
        {
            if (SetProperty(ref _problems, value))
            {
                OnPropertyChanged(nameof(HasProblems));
            }
        }
    }

    public bool HasProblems => Problems.Length > 0;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                RefreshCommands();
                OnPropertyChanged(nameof(EmptyText));
            }
        }
    }

    /// <summary>Episodes of the input folder not found yet.</summary>
    public ObservableCollection<WaitingEpisodeRow> Waiting { get; } = [];

    public bool HasWaiting => Waiting.Count > 0;

    /// <summary>Further series found in the input folder, each to be sent to Hromadné spracovanie when it is free.</summary>
    public ObservableCollection<ReadySeriesRow> Ready { get; } = [];

    public bool HasReady => Ready.Count > 0;

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand StopCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public AsyncRelayCommand CheckAgainCommand { get; }

    public string SendText => $"Odoslať na spracovanie ({Checked.Count()})";

    public string Attribution =>
        "Zdroje: " + string.Join(" · ", _services.NewsSources.Where(s => s.Enabled && s.Kind != NewsSourceKind.TmdbFilms)
            .Select(s => s.Kind == NewsSourceKind.TvMaze ? TvMazeClient.Attribution : s.Name)) + ".";

    /// <summary>Opens Single spracovanie with a file and the series' title; false when it is busy. Set by the main window.</summary>
    public Func<string, string, string, Task<bool>> SendToSingle { get; set; } = (_, _, _) => Task.FromResult(false);

    /// <summary>Opens Hromadné spracovanie with the files of one series and its title; false when it is busy. Set by the main window.</summary>
    public Func<IReadOnlyList<(string Title, IReadOnlyList<string> Files)>, Task<bool>> SendToBatch { get; set; } = _ => Task.FromResult(false);

    /// <summary>Reading when the page is shown (the self-test switches it off).</summary>
    public static bool AutoLoad { get; set; } = true;

    private IEnumerable<NewsEpisodeRow> Checked => Tabs.SelectMany(t => t.All).SelectMany(s => s.Episodes).Where(r => r.IsChecked);

    /// <summary>The page was opened: what is kept at once (the sources are asked at the first start of a day or on Obnoviť).</summary>
    public async void Shown()
    {
        OnPropertyChanged(nameof(HasMyLists));
        _ = _services.InputWatch.ScanAsync();
        if (!_shownKept && !Busy)
        {
            _shownKept = true;
            var kept = await _services.SeriesNews.ReadAsync(_services.NewsSources, DateOnly.FromDateTime(DateTime.Now), offline: true);
            if (_readAt is null && !Busy)
            {
                _all = kept.Episodes.ToList();
                Build();
            }
        }
    }

    public async Task LoadAsync(bool refresh)
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        var work = _work = new CancellationTokenSource();
        var progress = new Progress<string>(text =>
        {
            if (Busy)
            {
                Status = text + "…";
            }
        });
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var schedule = await _services.SeriesNews.ReadAsync(_services.NewsSources, today, refresh, progress, work.Token);
            _all = schedule.Episodes.ToList();
            Problems = string.Join("\n", schedule.Problems);
            Build();
            _readAt = DateTime.Now;
            var recent = _all.Count(e => e.AirDate <= today && e.AirDate >= today.AddDays(-RecentDays));
            Status = $"Aktuálne k {DateTime.Now:d. M. yyyy H:mm}: {Texts.Count(recent, "epizóda", "epizódy", "epizód")} za posledný týždeň · staršie sú v Histórii.";
        }
        catch (OperationCanceledException)
        {
            Status = "Zastavené.";
        }
        finally
        {
            OnPropertyChanged(nameof(Attribution));
            work.Dispose();
            _work = null;
            Busy = false;
        }
    }

    /// <summary>Reads the history (all kept days before the last week) – once, when it is first shown.</summary>
    public async Task LoadHistoryAsync()
    {
        var before = DateOnly.FromDateTime(DateTime.Now).AddDays(-RecentDays);
        var news = _services.SeriesNews;
        _history = (await Task.Run(() => news.History(before))).ToList();
        Build();
    }

    /// <summary>
    /// Odoslať na spracovanie: the ticked episodes are looked for in the input folder; one file goes to Single
    /// spracovanie, several to Hromadné spracovanie (the first series; the next ones wait in "Pripravené"), the missing
    /// ones wait in "Čaká na súbor".
    /// </summary>
    public async Task SendAsync()
    {
        var chosen = Checked.ToList();
        if (chosen.Count == 0 || await FindAsync(chosen.Select(r => r.Episode).ToList()) is not { } found)
        {
            return;
        }

        foreach (var row in chosen)
        {
            row.IsChecked = false;
        }

        await HandOverAsync(found);
    }

    private async Task CheckAgainAsync()
    {
        var waiting = Waiting.Select(w => w.Episode).ToList();
        if (await FindAsync(waiting) is not { } found)
        {
            return;
        }

        await HandOverAsync(found);
    }

    // the episodes' files in the input folder; null (with the reason shown) when the folder is missing
    private async Task<IReadOnlyList<EpisodeFiles>?> FindAsync(IReadOnlyList<SeriesEpisode> episodes)
    {
        if (_services.InputDirectory is not { } input || !System.IO.Directory.Exists(input))
        {
            Status = NoInputFolder;
            return null;
        }

        Busy = true;
        Status = $"Hľadám {Texts.Count(episodes.Count, "epizódu", "epizódy", "epizód")} vo vstupnom priečinku…";
        try
        {
            var excluded = _services.Library?.ResultFolders;
            return await Task.Run(() => EpisodeFinder.Find(episodes, input, excluded));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = "Vstupný priečinok sa nedá prečítať: " + ex.Message;
            return null;
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task HandOverAsync(IReadOnlyList<EpisodeFiles> found)
    {
        var missing = found.Where(f => f.Files.Count == 0).Select(f => f.Episode).ToList();
        foreach (var episode in found.Where(f => f.Files.Count > 0).Select(f => f.Episode))
        {
            if (Waiting.FirstOrDefault(w => w.Episode == episode) is { } row)
            {
                Waiting.Remove(row);
            }
        }

        foreach (var episode in missing.Where(m => Waiting.All(w => w.Episode != m)))
        {
            Waiting.Add(new WaitingEpisodeRow(episode, this));
        }

        var bySeries = found.Where(f => f.Files.Count > 0).GroupBy(f => f.Episode.SeriesKey)
            .Select(g => (Title: g.First().Episode.Caption, Episode: g.First().Episode.Code, Files: g.SelectMany(f => f.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
        var files = bySeries.Sum(s => s.Files.Count);
        var waitingNote = missing.Count > 0 ? $" Ešte nie sú vo vstupnom priečinku: {missing.Count} (čakajú nižšie)." : "";
        if (files == 0)
        {
            Status = "Žiadna z epizód zatiaľ nie je vo vstupnom priečinku." + waitingNote;
        }
        else if (files == 1)
        {
            var only = bySeries[0];
            Status = await SendToSingle(only.Files[0], only.Title, only.Episode)
                ? $"Odoslané do Single spracovania: {System.IO.Path.GetFileName(only.Files[0])}." + waitingNote
                : "Single spracovanie práve pracuje – súbor pošlite znova po jeho skončení." + waitingNote;
        }
        else
        {
            // owner, 29. 10. 2026: all series in one batch (each episode into the folder of its series)
            var first = bySeries[0];
            if (await SendToBatch([.. bySeries.Select(b => (b.Title, (IReadOnlyList<string>)b.Files))]))
            {
                foreach (var series in bySeries)
                {
                    if (Ready.FirstOrDefault(r => r.Title == series.Title) is { } sent)
                    {
                        Ready.Remove(sent);
                    }
                }

                Status = bySeries.Count == 1
                    ? $"Odoslané do Hromadného spracovania: {first.Title} ({Texts.Count(first.Files.Count, "súbor", "súbory", "súborov")})." + waitingNote
                    : $"Odoslané do Hromadného spracovania: {Texts.Count(bySeries.Count, "seriál", "seriály", "seriálov")}, {Texts.Count(files, "súbor", "súbory", "súborov")} " +
                      $"({string.Join(", ", bySeries.Select(b => b.Title))})." + waitingNote;
            }
            else
            {
                foreach (var series in bySeries.Where(s => Ready.All(r => r.Title != s.Title)))
                {
                    Ready.Add(new ReadySeriesRow(series.Title, series.Files, this));
                }

                Status = "Hromadné spracovanie práve pracuje – seriály sú pripravené nižšie, odošlite ich po jeho skončení." + waitingNote;
            }
        }

        RefreshCommands();
    }

    internal async Task SendReadyAsync(ReadySeriesRow row)
    {
        if (await SendToBatch([(row.Title, row.Files)]))
        {
            Ready.Remove(row);
            Status = $"Odoslané do Hromadného spracovania: {row.Title}.";
        }
        else
        {
            Status = "Hromadné spracovanie práve pracuje – odošlite seriál po jeho skončení.";
        }

        RefreshCommands();
    }

    internal void RemoveWaiting(WaitingEpisodeRow row)
    {
        Waiting.Remove(row);
        RefreshCommands();
    }

    internal void CheckedChanged(NewsEpisodeRow row)
    {
        row.Series?.CheckedChanged();
        OnPropertyChanged(nameof(SendText));
        SendCommand.NotifyCanExecuteChanged();
    }

    internal AppServices Services => _services;

    internal async Task OpenAsync(Func<TitleOpener, Task<string?>> open)
    {
        if (await open(_opener) is { } problem)
        {
            Status = problem;
        }
    }

    // the series of the tabs (alphabetically) with their episodes of the view: the last week and the coming days, or the
    // history; ticks and opened series stay
    private void Build()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-RecentDays);
        var episodes = SeriesNews.Merge(_view switch
        {
            NewsView.History => (_history ?? []).Concat(_all.Where(e => e.AirDate < from)),
            NewsView.Upcoming => _all.Where(e => e.AirDate >= today),
            NewsView.All => (_history ?? []).Concat(_all),
            _ => _all.Where(e => e.AirDate >= from && e.AirDate <= today),
        });
        var rhythms = (_history ?? []).Concat(_all).GroupBy(MarkStore.Series).ToDictionary(g => g.Key, g => Periodicity.Describe(g.SelectMany(e =>
            Enumerable.Repeat(e.AirDate, e.LastNumber is { } last && e.Number is { } first && last > first ? last - first + 1 : 1))), StringComparer.Ordinal);
        foreach (var tab in Tabs)
        {
            var oldSeries = tab.All.ToDictionary(r => r.MarkKey, StringComparer.Ordinal);
            var oldEpisodes = tab.All.SelectMany(r => r.Episodes).DistinctBy(r => r.Episode).ToDictionary(r => r.Episode);
            tab.Set(episodes.Where(e => e.Region == tab.Region)
                .GroupBy(MarkStore.Series)
                .Select(g => new NewsSeriesRow(g.Key, g.OrderBy(e => e.AirDate).Select(e => oldEpisodes.GetValueOrDefault(e) ?? new NewsEpisodeRow(e, this)).ToList(),
                    rhythms.GetValueOrDefault(g.Key) ?? "", this)
                {
                    IsExpanded = oldSeries.TryGetValue(g.Key, out var old) && old.IsExpanded,
                })
                .OrderBy(r => r.Caption, StringComparer.Create(Texts.Slovak, ignoreCase: true)));
        }

        Show();
        RefreshCommands();
    }

    // the rows of the tabs by the filters
    private void Show()
    {
        var wanted = Words.Normalize(Filter);
        HashSet<string>? mine = null;
        if (OnlyMyLists)
        {
            mine = _services.HistraListStore.Load().SelectMany(l => l.Items).Where(i => i.Kind == MediaKind.Series)
                .SelectMany(i => new[] { Words.Normalize(i.OriginalTitle), Words.Normalize(i.Title) }).ToHashSet(StringComparer.Ordinal);
        }

        foreach (var tab in Tabs)
        {
            tab.Show(r => (!OnlyChosen || r.IsChosen) &&
                          (wanted.Length == 0 || r.Search.Contains(wanted, StringComparison.Ordinal)) &&
                          (mine is null || mine.Contains(Words.Normalize(r.First.OriginalTitle)) || mine.Contains(Words.Normalize(r.First.Title))));
        }

        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasDomesticSource));
    }

    private void RefreshCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        CheckAgainCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SendText));
        OnPropertyChanged(nameof(HasWaiting));
        OnPropertyChanged(nameof(HasReady));
    }
}

/// <summary>A tab of Novinky – Seriály.</summary>
public sealed class NewsTabItem : ObservableObject
{
    private List<NewsSeriesRow> _all = [];

    public NewsTabItem(NewsRegion region, string title, Color color)
    {
        Region = region;
        Title = title;
        Accent = PremiereTabItem.Frozen(color);
        Tint = PremiereTabItem.Frozen(Color.FromArgb(0x30, color.R, color.G, color.B));
        Stripe = PremiereTabItem.Frozen(Color.FromArgb(0x10, color.R, color.G, color.B));
    }

    public NewsRegion Region { get; }

    public string Title { get; }

    public Brush Accent { get; }

    public Brush Tint { get; }

    public Brush Stripe { get; }

    /// <summary>All series of the view.</summary>
    public IReadOnlyList<NewsSeriesRow> All => _all;

    /// <summary>The series shown.</summary>
    public ObservableCollection<NewsSeriesRow> Rows { get; } = [];

    public string CountText => Rows.Count.ToString(Texts.Slovak);

    internal void Set(IEnumerable<NewsSeriesRow> rows) => _all = rows.ToList();

    internal void Show(Func<NewsSeriesRow, bool> visible)
    {
        Rows.Clear();
        foreach (var row in _all.Where(visible))
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(CountText));
    }
}

/// <summary>
/// A series in Novinky – Seriály: its nearest episode (the next one to come, else the last one aired), the mark of the
/// series (Vybrané, a note) and, opened, the tree of its episodes. Its tick ticks all its aired episodes for processing.
/// </summary>
public sealed class NewsSeriesRow : MarkedRow
{
    private readonly string _search;
    private bool _expanded;

    public NewsSeriesRow(string markKey, IReadOnlyList<NewsEpisodeRow> episodes, string rhythm, SeriesNewsPageViewModel page)
        : base(page.Services.Marks, markKey, page.Services.Watchlist)
    {
        Episodes = episodes;
        Rhythm = rhythm;
        foreach (var episode in episodes)
        {
            episode.Series = this;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        Nearest = episodes.FirstOrDefault(e => e.Episode.AirDate >= today) ?? episodes[^1];
        First = Nearest.Episode;
        var first = First;
        _search = Words.Normalize(string.Join(" ", episodes.Select(e => $"{e.Episode.Title} {e.Episode.OriginalTitle} {e.Episode.Channel}").Distinct()));
        Poster = new PosterThumb(page.Services, () => page.Services.SeriesLookup.PosterAsync(first), PosterSize.Small);
        Genre = new GenreCell(page.Services, first);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        OpenTmdbCommand = new AsyncRelayCommand(() => page.OpenAsync(o => o.OpenSeriesTmdbAsync(first)));
        OpenHistraCommand = new AsyncRelayCommand(() => page.OpenAsync(o => o.OpenSeriesHistraAsync(first)));
        OpenSourceCommand = new RelayCommand(() =>
        {
            if (Uri.TryCreate(episodes.Select(e => e.Episode.Link).LastOrDefault(l => l is not null), UriKind.Absolute, out var link))
            {
                Browser.Open(link);
            }
        }, () => HasSource);
    }

    /// <summary>The episodes of the view, the earliest first.</summary>
    public IReadOnlyList<NewsEpisodeRow> Episodes { get; }

    /// <summary>The next episode to come, else the last one aired.</summary>
    public NewsEpisodeRow Nearest { get; }

    /// <summary>How the episodes come ("raz do týždňa – streda" …), from all its known episodes.</summary>
    public string Rhythm { get; }

    /// <summary>The episode the series is named by (the nearest one).</summary>
    public SeriesEpisode First { get; }

    /// <summary>The poster of the whole series (found on TMDB when first shown).</summary>
    public PosterThumb Poster { get; }

    internal string Search => _search + " " + Words.Normalize(Note);

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (SetProperty(ref _expanded, value))
            {
                OnPropertyChanged(nameof(ExpandGlyph));
            }
        }
    }

    /// <summary>▸ closed, ▾ opened (Segoe Fluent Icons).</summary>
    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public string Caption => First.Caption;

    public string LocalTitle => First.Title != First.OriginalTitle ? First.Title : "";

    public string Production => Nearest.Production;

    /// <summary>Žáner (owner, 31. 10. 2026) – the source's genres in Slovak, else TMDB's.</summary>
    public GenreCell Genre { get; }

    public string NearestCode => Nearest.Code;

    public string NearestNote => string.Join(" · ", new[] { Nearest.Mark, Texts.Count(Episodes.Count, "epizóda", "epizódy", "epizód") }.Where(t => t.Length > 0));

    public string Day => Nearest.Day;

    public string Date => Nearest.Date;

    public string When => Nearest.When;

    public DateOnly SortDate => Nearest.Episode.AirDate;

    public bool IsToday => Nearest.IsToday;

    public bool HasSource => Episodes.Any(e => e.Episode.Link is not null);

    /// <summary>Aired episodes with a number – they can be sent to processing.</summary>
    public bool CanCheck => Episodes.Any(e => e.CanCheck);

    /// <summary>All aired episodes are ticked; setting it ticks or unticks them all.</summary>
    public bool IsChecked
    {
        get => CanCheck && Episodes.Where(e => e.CanCheck).All(e => e.IsChecked);
        set
        {
            foreach (var episode in Episodes.Where(e => e.CanCheck))
            {
                episode.IsChecked = value;
            }
        }
    }

    /// <summary>"2/3" ticked of the aired ones; empty when none is ticked.</summary>
    public string CheckedText
    {
        get
        {
            var aired = Episodes.Count(e => e.CanCheck);
            var ticked = Episodes.Count(e => e.IsChecked);
            return ticked == 0 ? "" : $"{ticked}/{aired}";
        }
    }

    public RelayCommand ToggleCommand { get; }

    public AsyncRelayCommand OpenTmdbCommand { get; }

    public AsyncRelayCommand OpenHistraCommand { get; }

    public RelayCommand OpenSourceCommand { get; }

    internal void CheckedChanged()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(CheckedText));
    }

    /// <summary>"✓ v priečinku: 2" – the chosen series' episodes already in the input folder; empty when none.</summary>
    public string FolderText
    {
        get
        {
            var count = Episodes.Count(e => e.InFolder);
            return count == 0 ? "" : $"✓ v priečinku: {count}";
        }
    }

    internal void FolderChanged()
    {
        OnPropertyChanged(nameof(FolderText));
        foreach (var episode in Episodes)
        {
            episode.FolderChanged();
        }
    }
}

/// <summary>An episode in the tree of a series, with its tick for processing (only aired episodes can be ticked).</summary>
public sealed class NewsEpisodeRow : ObservableObject
{
    private readonly SeriesNewsPageViewModel _page;
    private bool _isChecked;

    public NewsEpisodeRow(SeriesEpisode episode, SeriesNewsPageViewModel page)
    {
        Episode = episode;
        _page = page;
    }

    public SeriesEpisode Episode { get; }

    /// <summary>The series the episode is shown under.</summary>
    public NewsSeriesRow? Series { get; internal set; }

    /// <summary>Only an aired episode with a number can be found in the input folder.</summary>
    public bool CanCheck => Episode.Code.Length > 0 && Episode.AirDate <= DateOnly.FromDateTime(DateTime.Now);

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value && CanCheck))
            {
                _page.CheckedChanged(this);
            }
        }
    }

    public string Caption => Episode.Caption;

    public string Production => string.Join(" · ", new[] { Episode.Channel, Episode.Country.Length > 0 ? CountryNames.Name(Episode.Country) : "" }.Where(p => p.Length > 0));

    public string Code => Episode.Code.Length > 0 ? Episode.Code : "nová epizóda";

    public string Mark => Episode.Kind switch
    {
        EpisodeKind.SeriesPremiere => "nový seriál",
        EpisodeKind.SeasonPremiere => "nová séria",
        _ => "",
    };

    public string Day => Periodicity.DayName(Episode.AirDate);

    public string Date => $"{Episode.AirDate.Day}. {Episode.AirDate.Month}. {Episode.AirDate.Year}";

    public DateOnly SortDate => Episode.AirDate;

    public bool IsToday => Episode.AirDate == DateOnly.FromDateTime(DateTime.Now);

    /// <summary>"DNES", "zajtra", "o 3 dni", "včera", "pred 3 dňami" …</summary>
    public string When => NewsTexts.When(Episode.AirDate);

    /// <summary>The episode is already in the input folder (a chosen series, as last checked).</summary>
    public bool InFolder => _page.Services.InputWatch.Contains(Episode);

    internal void FolderChanged()
    {
        OnPropertyChanged(nameof(InFolder));
        OnPropertyChanged(nameof(State));
    }

    /// <summary>"odvysielaná" or "nadchádzajúca".</summary>
    public string State => InFolder ? "✓ vo vstupnom priečinku" : Episode.AirDate <= DateOnly.FromDateTime(DateTime.Now) ? "odvysielaná" : "nadchádzajúca";
}

/// <summary>An episode sent to processing that is not in the input folder yet.</summary>
public sealed class WaitingEpisodeRow(SeriesEpisode episode, SeriesNewsPageViewModel page)
{
    public SeriesEpisode Episode { get; } = episode;

    public string Text => $"{Episode.Caption} {Episode.Code} · {Episode.AirDate.Day}. {Episode.AirDate.Month}. · {Episode.Channel}";

    public RelayCommand RemoveCommand => _remove ??= new RelayCommand(() => page.RemoveWaiting(this));

    private RelayCommand? _remove;
}

/// <summary>A further series found in the input folder, ready for Hromadné spracovanie.</summary>
public sealed class ReadySeriesRow(string title, IReadOnlyList<string> files, SeriesNewsPageViewModel page)
{
    public string Title { get; } = title;

    public IReadOnlyList<string> Files { get; } = files;

    public string Text => $"{Title} · {Texts.Count(Files.Count, "súbor", "súbory", "súborov")}";

    public AsyncRelayCommand SendCommand => _send ??= new AsyncRelayCommand(() => page.SendReadyAsync(this));

    private AsyncRelayCommand? _send;
}
