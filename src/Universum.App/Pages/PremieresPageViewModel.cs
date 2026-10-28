using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Library;
using Universum.Core.News;

namespace Universum.App.Pages;

/// <summary>The four tables of the Premiéry page.</summary>
public enum PremiereTab
{
    FilmDomestic,
    FilmWorld,
    SeriesDomestic,
    SeriesWorld,
}

/// <summary>
/// Premiéry: four tables – films in Czech and Slovak cinemas and in the world (TMDB, with VOD and TV dates), new series
/// and seasons on Czech and Slovak channels and in the world (the web pages of the settings and TVmaze, with how often
/// episodes come and how many the season has). Read when the page is first shown and then on Obnoviť; the sources
/// are asked only for days not kept yet.
/// </summary>
public sealed class PremieresPageViewModel : ObservableObject
{
    /// <summary>Films: days back and ahead of today.</summary>
    public const int FilmDaysBack = 7;

    public const int FilmDaysAhead = 60;

    private readonly AppServices _services;
    private readonly TitleOpener _opener;
    private PremiereTabItem _selected;
    private string _status = "";
    private string _problems = "";
    private bool _busy;
    private CancellationTokenSource? _work;
    private DateTime? _readAt;

    public PremieresPageViewModel(AppServices services)
    {
        _services = services;
        _opener = new TitleOpener(services);
        Tabs =
        [
            new(PremiereTab.FilmDomestic, "FILM – SK/CZ premiéry", Color.FromRgb(0xF2, 0x8C, 0x7C)),
            new(PremiereTab.FilmWorld, "FILM – SVET premiéry", Color.FromRgb(0x6F, 0xA8, 0xE8)),
            new(PremiereTab.SeriesDomestic, "SERIÁL – SK/CZ premiéry", Color.FromRgb(0x63, 0xC4, 0x8F)),
            new(PremiereTab.SeriesWorld, "SERIÁL – SVET premiéry", Color.FromRgb(0xA7, 0x8B, 0xE6)),
        ];
        _selected = Tabs[0];
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(refresh: true), () => !Busy);
        StopCommand = new RelayCommand(() => _work?.Cancel(), () => Busy);
        services.NewsSourcesChanged += () => _readAt = null;
        services.TmdbChanged += () => _readAt = null;
    }

    public IReadOnlyList<PremiereTabItem> Tabs { get; }

    public PremiereTabItem SelectedTab
    {
        get => _selected;
        set
        {
            if (value is not null && SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(IsFilmTab));
                OnPropertyChanged(nameof(IsSeriesTab));
                OnPropertyChanged(nameof(Note));
            }
        }
    }

    public bool IsFilmTab => SelectedTab.Tab is PremiereTab.FilmDomestic or PremiereTab.FilmWorld;

    public bool IsSeriesTab => !IsFilmTab;

    /// <summary>What the chosen table contains and where it comes from.</summary>
    public string Note => SelectedTab.Tab switch
    {
        PremiereTab.FilmDomestic => $"Premiéry v kinách na Slovensku a v Česku od {FilmDaysBack} dní dozadu do {FilmDaysAhead} dní dopredu, s dátumom na VOD a v TV. Zdroj: TMDB.",
        PremiereTab.FilmWorld => $"Najpopulárnejšie filmy, ktoré majú prvú premiéru vo svete od {FilmDaysBack} dní dozadu do {FilmDaysAhead} dní dopredu. Zdroj: TMDB.",
        PremiereTab.SeriesDomestic => "Nové seriály a nové série na českých a slovenských staniciach a službách (podľa zdrojov v Nastaveniach).",
        _ => "Nové seriály a nové série vo svete – stanice a streamovacie služby (TVmaze a zdroje v Nastaveniach).",
    };

    /// <summary>The sources and their attribution under the tables.</summary>
    public string Attribution =>
        "Zdroje: " + string.Join(" · ", _services.NewsSources.Where(s => s.Enabled).Select(s => s.Kind switch
        {
            NewsSourceKind.TvMaze => TvMazeClient.Attribution,
            NewsSourceKind.TmdbFilms => "TMDB (filmy)",
            _ => s.Name,
        })) + ". " + Universum.Core.Library.Tmdb.TmdbClient.Attribution;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>What could not be read (a source unavailable, a page changed); empty when all went well.</summary>
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
                RefreshCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand StopCommand { get; }

    /// <summary>Reading when the page is shown (the self-test switches it off and reads its invented sources itself).</summary>
    public static bool AutoLoad { get; set; } = true;

    /// <summary>The page was opened: read when nothing was read yet or it is older than six hours.</summary>
    public void Shown()
    {
        if (AutoLoad && !Busy && (_readAt is null || DateTime.Now - _readAt > TimeSpan.FromHours(6)))
        {
            _ = LoadAsync(refresh: false);
        }
    }

    /// <summary>Reads all four tables (the kept days of the sources are used unless <paramref name="refresh"/>).</summary>
    public async Task LoadAsync(bool refresh)
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        var work = _work = new CancellationTokenSource();
        var problems = new List<string>();
        var progress = new Progress<string>(text =>
        {
            if (Busy)
            {
                Status = text + "…";
            }
        });
        var today = DateOnly.FromDateTime(DateTime.Now);
        try
        {
            if (_services.NewsSources.Any(s => s.Enabled && s.Kind == NewsSourceKind.TmdbFilms))
            {
                if (_services.Films is { } films)
                {
                    try
                    {
                        Fill(Tab(PremiereTab.FilmDomestic), (await films.DomesticAsync(today.AddDays(-FilmDaysBack), today.AddDays(FilmDaysAhead), progress, work.Token))
                            .Select(f => new PremiereFilmRow(f, this, today)));
                        Fill(Tab(PremiereTab.FilmWorld), (await films.WorldAsync(today.AddDays(-FilmDaysBack), today.AddDays(FilmDaysAhead), 3, progress, work.Token))
                            .Select(f => new PremiereFilmRow(f, this, today)));
                    }
                    catch (TitleServiceException ex)
                    {
                        problems.Add("TMDB: " + ex.Message);
                    }
                }
                else
                {
                    problems.Add("Filmové premiéry čítajú z TMDB – uložte v Nastaveniach kľúč TMDB (a zapnite pripájanie).");
                }
            }

            var schedule = await _services.SeriesNews.ReadAsync(_services.NewsSources, today, refresh, progress, work.Token);
            problems.AddRange(schedule.Problems);
            var premieres = await _services.SeriesNews.PremieresAsync(schedule.Episodes, progress: progress, cancellationToken: work.Token);
            Fill(Tab(PremiereTab.SeriesDomestic), premieres.Where(p => p.First.Region == NewsRegion.Domestic).OrderBy(p => p.First.AirDate).Select(p => new PremiereSeriesRow(p, this, today)));
            Fill(Tab(PremiereTab.SeriesWorld), premieres.Where(p => p.First.Region == NewsRegion.World).OrderBy(p => p.First.AirDate).Select(p => new PremiereSeriesRow(p, this, today)));
            _readAt = DateTime.Now;
            Status = $"Načítané {DateTime.Now:d. M. yyyy H:mm}.";
        }
        catch (OperationCanceledException)
        {
            Status = "Zastavené – zobrazené je, čo sa stihlo načítať.";
        }
        finally
        {
            Problems = string.Join("\n", problems);
            OnPropertyChanged(nameof(Attribution));
            work.Dispose();
            _work = null;
            Busy = false;
        }
    }

    internal async Task OpenAsync(Func<TitleOpener, Task<string?>> open)
    {
        if (await open(_opener) is { } problem)
        {
            Status = problem;
        }
    }

    /// <summary>The tab of a table.</summary>
    public PremiereTabItem Tab(PremiereTab tab) => Tabs.First(t => t.Tab == tab);

    private static void Fill(PremiereTabItem tab, IEnumerable<object> rows)
    {
        tab.Rows.Clear();
        foreach (var row in rows)
        {
            tab.Rows.Add(row);
        }

        tab.CountChanged();
    }

    internal AppServices Services => _services;
}

/// <summary>A tab of the Premiéry page: its title, colour and rows.</summary>
public sealed class PremiereTabItem : ObservableObject
{
    public PremiereTabItem(PremiereTab tab, string title, Color color)
    {
        Tab = tab;
        Title = title;
        Accent = Frozen(color);
        Tint = Frozen(Color.FromArgb(0x30, color.R, color.G, color.B));
        Stripe = Frozen(Color.FromArgb(0x14, color.R, color.G, color.B));
    }

    public PremiereTab Tab { get; }

    public string Title { get; }

    public Brush Accent { get; }

    public Brush Tint { get; }

    /// <summary>Every other row of the table.</summary>
    public Brush Stripe { get; }

    public ObservableCollection<object> Rows { get; } = [];

    public string CountText => Rows.Count.ToString(Texts.Slovak);

    internal void CountChanged() => OnPropertyChanged(nameof(CountText));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>A row of a film table.</summary>
public sealed class PremiereFilmRow
{
    public PremiereFilmRow(FilmPremiere film, PremieresPageViewModel page, DateOnly today)
    {
        Film = film;
        Poster = new PosterThumb(page.Services, film.PosterPath, PosterSize.Small);
        IsToday = film.Premiere == today;
        IsPast = film.Premiere < today;
        OpenTmdbCommand = new RelayCommand(() => Browser.Open(TitleLinks.Tmdb(MediaKind.Movie, film.TmdbId)!));
        OpenHistraCommand = new AsyncRelayCommand(() => page.OpenAsync(o => o.OpenHistraAsync(MediaKind.Movie, film.TmdbId, film.OriginalTitle, film.Year)));
    }

    public FilmPremiere Film { get; }

    public PosterThumb Poster { get; }

    public string Caption => Film.Caption;

    public string LocalTitle => Film.LocalTitle;

    public string Production => Film.Production;

    public string Date => $"{Film.Premiere.Day}. {Film.Premiere.Month}. {Film.Premiere.Year}";

    /// <summary>The date for sorting the table.</summary>
    public DateOnly SortDate => Film.Premiere;

    public string Day => Universum.Core.News.Periodicity.DayName(Film.Premiere);

    public string Where => Film.PremiereNote;

    public string Availability => Film.Availability.Length > 0 ? Film.Availability : "–";

    public bool IsToday { get; }

    public bool IsPast { get; }

    public RelayCommand OpenTmdbCommand { get; }

    public AsyncRelayCommand OpenHistraCommand { get; }
}

/// <summary>A row of a series table.</summary>
public sealed class PremiereSeriesRow
{
    public PremiereSeriesRow(SeriesPremiereRow premiere, PremieresPageViewModel page, DateOnly today)
    {
        var first = premiere.First;
        Premiere = premiere;
        IsToday = first.AirDate == today;
        IsPast = first.AirDate < today;
        OpenTmdbCommand = new AsyncRelayCommand(() => page.OpenAsync(o => o.OpenTmdbAsync(MediaKind.Series, null, first.OriginalTitle, first.Year)));
        OpenHistraCommand = new AsyncRelayCommand(() => page.OpenAsync(o => o.OpenHistraAsync(MediaKind.Series, null, first.OriginalTitle, first.Year)));
        OpenSourceCommand = new RelayCommand(() =>
        {
            if (Uri.TryCreate(first.Link, UriKind.Absolute, out var link))
            {
                Browser.Open(link);
            }
        }, () => first.Link is not null);
    }

    public SeriesPremiereRow Premiere { get; }

    public string Caption => Premiere.First.Caption;

    public string LocalTitle => Premiere.First.Title != Premiere.First.OriginalTitle ? Premiere.First.Title : "";

    public string Production => string.Join(" · ", new[] { Premiere.First.Channel, Premiere.First.Country.Length > 0 ? CountryNames.Name(Premiere.First.Country) : "" }.Where(p => p.Length > 0));

    public string Genres => Premiere.First.Genres;

    public string What => Premiere.First.Kind == EpisodeKind.SeriesPremiere ? $"nový seriál · {Premiere.First.Code}" : $"{Premiere.First.Season}. séria · {Premiere.First.Code}";

    public string Date => $"{Premiere.First.AirDate.Day}. {Premiere.First.AirDate.Month}. {Premiere.First.AirDate.Year}";

    public DateOnly SortDate => Premiere.First.AirDate;

    public string Day => Universum.Core.News.Periodicity.DayName(Premiere.First.AirDate);

    public string Periodicity => Premiere.Periodicity.Length > 0 ? Premiere.Periodicity : "–";

    public string Episodes => Premiere.EpisodeCount is { } count ? Texts.Count(count, "epizóda", "epizódy", "epizód") : "–";

    public string SourceText => $"Otvoriť v zdroji ({Premiere.First.Source})";

    public bool IsToday { get; }

    public bool IsPast { get; }

    public AsyncRelayCommand OpenTmdbCommand { get; }

    public AsyncRelayCommand OpenHistraCommand { get; }

    public RelayCommand OpenSourceCommand { get; }
}
