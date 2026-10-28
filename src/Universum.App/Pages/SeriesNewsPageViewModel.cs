using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Library;
using Universum.Core.News;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>
/// Novinky – Seriály: the episodes of running series, Czech and Slovak channels and the world, from the sources of the
/// settings. Episodes are ticked and sent to processing: the input folder is searched at once – one file goes to Single
/// spracovanie, several to Hromadné spracovanie (one series at a time; the next series wait here), episodes not there
/// yet wait in the list "Čaká na súbor" until checked again. Nothing starts by itself.
/// </summary>
public sealed class SeriesNewsPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly TitleOpener _opener;
    private List<SeriesEpisode> _all = [];
    private NewsTabItem _selected;
    private string _filter = "";
    private bool _showUpcoming;
    private bool _onlyMyLists;
    private string _status = "";
    private string _problems = "";
    private bool _busy;
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
        services.NewsSourcesChanged += () => _readAt = null;
    }

    public InputFolderViewModel InputFolder => _services.InputFolder;

    public IReadOnlyList<NewsTabItem> Tabs { get; }

    public NewsTabItem SelectedTab
    {
        get => _selected;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selected, value);
            }
        }
    }

    /// <summary>Only the series whose name (Czech or original) contains this, without diacritics.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                Show();
            }
        }
    }

    /// <summary>Also the episodes still to come (by default only aired ones – they can be in the input folder).</summary>
    public bool ShowUpcoming
    {
        get => _showUpcoming;
        set
        {
            if (SetProperty(ref _showUpcoming, value))
            {
                Show();
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
    public Func<IReadOnlyList<string>, string, Task<bool>> SendToBatch { get; set; } = (_, _) => Task.FromResult(false);

    /// <summary>Reading when the page is shown (the self-test switches it off).</summary>
    public static bool AutoLoad { get; set; } = true;

    private IEnumerable<NewsEpisodeRow> Checked => Tabs.SelectMany(t => t.All).Where(r => r.IsChecked);

    public void Shown()
    {
        OnPropertyChanged(nameof(HasMyLists));
        if (AutoLoad && !Busy && (_readAt is null || DateTime.Now - _readAt > TimeSpan.FromHours(6)))
        {
            _ = LoadAsync(refresh: false);
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
            foreach (var tab in Tabs)
            {
                tab.Set(_all.Where(e => e.Region == tab.Region).OrderByDescending(e => e.AirDate).ThenBy(e => e.Caption, StringComparer.CurrentCulture)
                    .Select(e => new NewsEpisodeRow(e, this, today)));
            }

            Show();
            _readAt = DateTime.Now;
            Status = $"Načítané {DateTime.Now:d. M. yyyy H:mm}: {Texts.Count(_all.Count, "epizóda", "epizódy", "epizód")}.";
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
            Status = "Najprv nastavte vstupný priečinok – tam UNIVERSUM hľadá epizódy (tlačidlo Zmeniť… hore).";
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
            foreach (var series in bySeries.Skip(1).Where(s => Ready.All(r => r.Title != s.Title)))
            {
                Ready.Add(new ReadySeriesRow(series.Title, series.Files, this));
            }

            var first = bySeries[0];
            Status = await SendToBatch(first.Files, first.Title)
                ? $"Odoslané do Hromadného spracovania: {first.Title} ({Texts.Count(first.Files.Count, "súbor", "súbory", "súborov")})." +
                  (bySeries.Count > 1 ? $" Ďalšie seriály ({bySeries.Count - 1}) sú pripravené nižšie – Hromadné spracovanie spracuje jeden seriál naraz." : "") + waitingNote
                : "Hromadné spracovanie práve pracuje – seriály sú pripravené nižšie, odošlite ich po jeho skončení." + waitingNote;
            if (Status.StartsWith("Hromadné spracovanie práve", StringComparison.Ordinal) && Ready.All(r => r.Title != first.Title))
            {
                Ready.Insert(0, new ReadySeriesRow(first.Title, first.Files, this));
            }
        }

        RefreshCommands();
    }

    internal async Task SendReadyAsync(ReadySeriesRow row)
    {
        if (await SendToBatch(row.Files, row.Title))
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

    internal void CheckedChanged()
    {
        OnPropertyChanged(nameof(SendText));
        SendCommand.NotifyCanExecuteChanged();
    }

    internal async Task OpenAsync(Func<TitleOpener, Task<string?>> open)
    {
        if (await open(_opener) is { } problem)
        {
            Status = problem;
        }
    }

    // the rows of the tabs by the filters
    private void Show()
    {
        var wanted = Words.Normalize(Filter);
        var today = DateOnly.FromDateTime(DateTime.Now);
        HashSet<string>? mine = null;
        if (OnlyMyLists)
        {
            mine = _services.HistraListStore.Load().SelectMany(l => l.Items).Where(i => i.Kind == MediaKind.Series)
                .SelectMany(i => new[] { Words.Normalize(i.OriginalTitle), Words.Normalize(i.Title) }).ToHashSet(StringComparer.Ordinal);
        }

        foreach (var tab in Tabs)
        {
            tab.Show(r => (ShowUpcoming || r.Episode.AirDate <= today) &&
                          (wanted.Length == 0 || r.Search.Contains(wanted, StringComparison.Ordinal)) &&
                          (mine is null || mine.Contains(Words.Normalize(r.Episode.OriginalTitle)) || mine.Contains(Words.Normalize(r.Episode.Title))));
        }
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
    private List<NewsEpisodeRow> _all = [];

    public NewsTabItem(NewsRegion region, string title, Color color)
    {
        Region = region;
        Title = title;
        Accent = Frozen(color);
        Tint = Frozen(Color.FromArgb(0x30, color.R, color.G, color.B));
        Stripe = Frozen(Color.FromArgb(0x14, color.R, color.G, color.B));
    }

    public NewsRegion Region { get; }

    public string Title { get; }

    public Brush Accent { get; }

    public Brush Tint { get; }

    public Brush Stripe { get; }

    public IReadOnlyList<NewsEpisodeRow> All => _all;

    public ObservableCollection<NewsEpisodeRow> Rows { get; } = [];

    public string CountText => Rows.Count.ToString(Texts.Slovak);

    internal void Set(IEnumerable<NewsEpisodeRow> rows) => _all = rows.ToList();

    internal void Show(Func<NewsEpisodeRow, bool> visible)
    {
        Rows.Clear();
        foreach (var row in _all.Where(visible))
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(CountText));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>An episode in Novinky – Seriály, with its tick for processing.</summary>
public sealed class NewsEpisodeRow : ObservableObject
{
    private readonly SeriesNewsPageViewModel _page;
    private bool _isChecked;

    public NewsEpisodeRow(SeriesEpisode episode, SeriesNewsPageViewModel page, DateOnly today)
    {
        Episode = episode;
        _page = page;
        IsToday = episode.AirDate == today;
        IsFuture = episode.AirDate > today;
        Search = Words.Normalize($"{episode.Title} {episode.OriginalTitle} {episode.Channel}");
        OpenTmdbCommand = new AsyncRelayCommand(() => page.OpenAsync(o => o.OpenTmdbAsync(MediaKind.Series, null, episode.OriginalTitle, episode.Year)));
        OpenHistraCommand = new AsyncRelayCommand(() => page.OpenAsync(o => o.OpenHistraAsync(MediaKind.Series, null, episode.OriginalTitle, episode.Year)));
        OpenSourceCommand = new RelayCommand(() =>
        {
            if (Uri.TryCreate(episode.Link, UriKind.Absolute, out var link))
            {
                Browser.Open(link);
            }
        }, () => episode.Link is not null);
    }

    public SeriesEpisode Episode { get; }

    internal string Search { get; }

    /// <summary>Only an episode with a number can be found in the input folder.</summary>
    public bool CanCheck => Episode.Code.Length > 0;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value && CanCheck))
            {
                _page.CheckedChanged();
            }
        }
    }

    public string Caption => Episode.Caption;

    public string LocalTitle => Episode.Title != Episode.OriginalTitle ? Episode.Title : "";

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

    public bool IsToday { get; }

    public bool IsFuture { get; }

    public AsyncRelayCommand OpenTmdbCommand { get; }

    public AsyncRelayCommand OpenHistraCommand { get; }

    public RelayCommand OpenSourceCommand { get; }
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
