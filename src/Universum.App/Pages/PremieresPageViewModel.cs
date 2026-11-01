using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Library;
using Universum.Core.News;
using Universum.Core.Text;

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
/// episodes come and how many the season has). Everything read is kept: the page shows it at once, the sources are asked
/// once a day (or on Obnoviť) and only for what is new; a premiere moves to the history the day after it. The user
/// marks titles (Vybrané, a note) and can show only the chosen ones.
/// </summary>
public sealed class PremieresPageViewModel : ObservableObject
{
    /// <summary>Films: days back and ahead of today.</summary>
    public const int FilmDaysBack = 7;

    public const int FilmDaysAhead = 60;

    private readonly AppServices _services;
    private readonly TitleOpener _opener;
    private PremiereTabItem _selected;
    private PremiereArchive? _archive;
    private string _status = "";
    private string _problems = "";
    private string _filter = "";
    private NewsView _view = NewsView.Upcoming;
    private bool _onlyChosen;
    private bool _busy;
    private CancellationTokenSource? _work;

    public PremieresPageViewModel(AppServices services)
    {
        _services = services;
        _opener = new TitleOpener(services);
        Tabs =
        [
            new(PremiereTab.FilmDomestic, Loc.T("FILM – SK/CZ premiéry"), Color.FromRgb(0xF2, 0x8C, 0x7C)),
            new(PremiereTab.FilmWorld, Loc.T("FILM – SVET premiéry"), Color.FromRgb(0x6F, 0xA8, 0xE8)),
            new(PremiereTab.SeriesDomestic, Loc.T("SERIÁL – SK/CZ premiéry"), Color.FromRgb(0x63, 0xC4, 0x8F)),
            new(PremiereTab.SeriesWorld, Loc.T("SERIÁL – SVET premiéry"), Color.FromRgb(0xA7, 0x8B, 0xE6)),
        ];
        _selected = Tabs[0];
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(refresh: true), () => !Busy);
        StopCommand = new RelayCommand(() => _work?.Cancel(), () => Busy);
        services.NewsSourcesChanged += () =>
        {
            OnPropertyChanged(nameof(Attribution));
            Show();
        };
        services.NewsCleared += () =>
        {
            _archive = null;
            Status = "";
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
    }

    public IReadOnlyList<PremiereTabItem> Tabs { get; }

    /// <summary>The input folder of the settings (the same on every page).</summary>
    public InputFolderViewModel InputFolder => _services.InputFolder;

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
                OnPropertyChanged(nameof(EmptyText));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsFilmTab => SelectedTab.Tab is PremiereTab.FilmDomestic or PremiereTab.FilmWorld;

    public bool IsSeriesTab => !IsFilmTab;

    /// <summary>The premieres of the last week (the latest first).</summary>
    public bool ShowRecent
    {
        get => _view == NewsView.Recent;
        set => SetView(value, NewsView.Recent);
    }

    /// <summary>The coming premieres (from today, the nearest first).</summary>
    public bool ShowUpcoming
    {
        get => _view == NewsView.Upcoming;
        set => SetView(value, NewsView.Upcoming);
    }

    /// <summary>The history: premieres that are over (the latest first).</summary>
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
        OnPropertyChanged(nameof(Note));
        Show();
    }

    /// <summary>Only the titles the user chose (Vybrané).</summary>
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
        get => Math.Clamp(_services.Settings.PremierePosterScale, 1, 2);
        set
        {
            value = Math.Round(Math.Clamp(value, 1, 2), 2);
            if (Math.Abs(PosterScale - value) > 0.001)
            {
                _services.Settings.PremierePosterScale = value;
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

    /// <summary>Posters beside the titles (kept in the settings).</summary>
    public bool ShowPosters
    {
        get => _services.Settings.PremierePosters;
        set
        {
            if (_services.Settings.PremierePosters != value)
            {
                _services.Settings.PremierePosters = value;
                _services.Settings.Save();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Only titles whose name (original or Czech), production or note contains this, without diacritics.</summary>
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

    /// <summary>A web page with Czech and Slovak channels is among the sources (else the SK/CZ series stay empty).</summary>
    public bool HasDomesticSource => _services.NewsSources.Any(s => s.Enabled && s.Kind == NewsSourceKind.Page);

    /// <summary>What the chosen table contains and where it comes from.</summary>
    public string Note => _view switch
    {
        NewsView.History => Loc.T("História – premiéry, ktoré už boli (najnovšie hore). "),
        NewsView.Recent => Loc.T("Premiéry posledného týždňa (najnovšie hore). "),
        NewsView.All => Loc.T("Všetko – história aj nadchádzajúce premiéry (najneskoršie hore). "),
        _ => "",
    } + SelectedTab.Tab switch
    {
        PremiereTab.FilmDomestic => Loc.F("Len české a slovenské filmy (aj koprodukcie) – premiéry v kinách na Slovensku a v Česku (sledované {0} dní dopredu), s dátumom na VOD a v TV. Zdroj: TMDB.", FilmDaysAhead),
        PremiereTab.FilmWorld => Loc.F("Filmy sveta okrem českých a slovenských – najpopulárnejšie s prvou premiérou vo svete (sledované {0} dní dopredu); ak idú aj do našich kín, dátum je pri premiére. Zdroj: TMDB.", FilmDaysAhead),
        PremiereTab.SeriesDomestic => Loc.F("Len české a slovenské seriály – nové seriály a nové série (sledované {0} dní dopredu, podľa zdrojov v Nastaveniach).", SeriesNews.PageDaysAhead),
        _ => Loc.F("Seriály sveta okrem českých a slovenských – nové seriály a nové série na staniciach a streamovacích službách (sledované {0} dní dopredu; TVmaze a zdroje v Nastaveniach).", SeriesNews.TvMazeDaysAhead),
    };

    /// <summary>Why the chosen table is empty.</summary>
    public string EmptyText =>
        SelectedTab.Tab == PremiereTab.SeriesDomestic && !HasDomesticSource
            ? Loc.T("České a slovenské seriály potrebujú zdroj so stanicami CZ/SK.\nPridajte ho v Nastaveniach → Zdroje noviniek → Importovať zdroj… (súbor pravidiel zdroja).")
            : OnlyChosen ? Loc.T("Žiadne vybrané tituly – výber zaškrtnete v stĺpci Vybrané.")
            : Filter.Length > 0 ? Loc.T("Nič nezodpovedá hľadaniu.")
            : ShowHistory ? Loc.T("História je zatiaľ prázdna – premiéra sem prejde deň po svojom dátume.")
            : ShowAll && !Busy ? Loc.T("Žiadne premiéry ani história – skúste Obnoviť.")
            : Busy ? Loc.T("Načítavam…") : Loc.T("Žiadne premiéry – skúste Obnoviť.");

    public bool IsEmpty => SelectedTab.Rows.Count == 0;

    /// <summary>The sources and their attribution under the tables.</summary>
    public string Attribution =>
        Loc.T("Zdroje: ") + string.Join(" · ", _services.NewsSources.Where(s => s.Enabled).Select(s => s.Kind switch
        {
            NewsSourceKind.TvMaze => TvMazeClient.Attribution,
            NewsSourceKind.TmdbFilms => Loc.T("TMDB (filmy)"),
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
                OnPropertyChanged(nameof(EmptyText));
            }
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand StopCommand { get; }

    /// <summary>The daily reading at the start (the self-test switches it off and reads its invented sources itself).</summary>
    public static bool AutoLoad { get; set; } = true;

    /// <summary>
    /// The page was opened: the kept premieres at once (the sources are asked at the first start of a day or on Obnoviť).
    /// </summary>
    public void Shown()
    {
        EnsureLoaded();
        Show();
    }

    /// <summary>
    /// Asks the sources and adds what is new to the kept premieres (<paramref name="refresh"/>: today and the coming days
    /// of the series sources are asked again even when kept). The history stays.
    /// </summary>
    public async Task LoadAsync(bool refresh)
    {
        if (Busy)
        {
            return;
        }

        EnsureLoaded();
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
        var archive = _archive!;
        try
        {
            if (_services.NewsSources.Any(s => s.Enabled && s.Kind == NewsSourceKind.TmdbFilms))
            {
                if (_services.Films is { } films)
                {
                    try
                    {
                        var from = today.AddDays(-FilmDaysBack);
                        var read = (await films.DomesticAsync(from, today.AddDays(FilmDaysAhead), progress, work.Token))
                            .Concat(await films.WorldAsync(from, today.AddDays(FilmDaysAhead), 3, progress, work.Token));
                        var merged = PremiereArchive.MergeFilms(archive.Films, read, today, out var recheck);
                        for (var i = 0; i < recheck.Count; i++)
                        {
                            Status = Loc.F("Filmy – kontrola uložených: {0} z {1}…", i + 1, recheck.Count);
                            var film = recheck[i];
                            merged.RemoveAll(f => f.Region == film.Region && f.TmdbId == film.TmdbId);
                            if (await films.RecheckAsync(film, from, work.Token) is { } now)
                            {
                                merged.Add(now);
                            }
                        }

                        archive = archive with { Films = merged };
                        Keep(archive);
                    }
                    catch (TitleServiceException ex)
                    {
                        problems.Add("TMDB: " + ex.Message);
                    }
                }
                else
                {
                    problems.Add(Loc.T("Filmové premiéry čítajú z TMDB – uložte v Nastaveniach kľúč TMDB (a zapnite pripájanie)."));
                }
            }

            var sources = _services.NewsSources;
            var schedule = await _services.SeriesNews.ReadAsync(sources, today, refresh, progress, work.Token);
            problems.AddRange(schedule.Problems);
            var kept = archive.Series.ToDictionary(PremiereArchive.Key, StringComparer.Ordinal);
            var premieres = await _services.SeriesNews.PremieresAsync(schedule.Episodes, progress: progress, cancellationToken: work.Token,
                known: e => kept.GetValueOrDefault(PremiereArchive.Key(new SeriesPremiereRow(e, "", null))));
            archive = archive with
            {
                Series = PremiereArchive.MergeSeries(archive.Series, premieres, today, SeriesNews.ReadTo(sources, today), complete: schedule.Problems.Count == 0),
                ReadAt = DateTime.Now,
            };
            Keep(archive);
            Status = Loc.F("Aktuálne k {0:d. M. yyyy H:mm} · zdroje sa pýtajú raz denne alebo tlačidlom Obnoviť.", DateTime.Now);
        }
        catch (OperationCanceledException)
        {
            Status = Loc.T("Zastavené – zobrazené je, čo sa stihlo načítať.");
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

    internal AppServices Services => _services;

    // the kept premieres, read once
    private void EnsureLoaded()
    {
        if (_archive is not null)
        {
            return;
        }

        _archive = _services.PremiereStore.Load().Classified(_services.SeriesNews.Classify);
        Build();
        if (_archive.ReadAt is { } read)
        {
            Status = Loc.F("Uložené premiéry z {0:d. M. yyyy H:mm}.", read);
        }
    }

    // keeps what was read and shows it
    private void Keep(PremiereArchive archive)
    {
        _archive = archive;
        if (!_services.PremiereStore.Save(archive))
        {
            Status = Loc.T("Premiéry sa nedajú uložiť – ostanú len do zatvorenia programu.");
        }

        Build();
    }

    // the rows of all tables from the kept premieres (once per change; the filters only choose among them)
    private void Build()
    {
        var archive = _archive ?? new PremiereArchive();
        Tab(PremiereTab.FilmDomestic).Set(archive.Films.Where(f => f.Region == NewsRegion.Domestic).Select(f => new PremiereFilmRow(f, this)));
        Tab(PremiereTab.FilmWorld).Set(archive.Films.Where(f => f.Region == NewsRegion.World).Select(f => new PremiereFilmRow(f, this)));
        Tab(PremiereTab.SeriesDomestic).Set(archive.Series.Where(s => s.First.Region == NewsRegion.Domestic).Select(s => new PremiereSeriesRow(s, this)));
        Tab(PremiereTab.SeriesWorld).Set(archive.Series.Where(s => s.First.Region == NewsRegion.World).Select(s => new PremiereSeriesRow(s, this)));
        Show();
    }

    // the rows of the tabs by the view (coming or history) and the filters
    private void Show()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var wanted = Words.Normalize(Filter);
        foreach (var tab in Tabs)
        {
            var rows = tab.All.Where(r => _view switch
                                          {
                                              NewsView.Recent => r.SortDate >= today.AddDays(-7) && r.SortDate <= today,
                                              NewsView.History => r.SortDate < today,
                                              NewsView.All => true,
                                              _ => r.SortDate >= today,
                                          } &&
                                          (!OnlyChosen || r.IsChosen) &&
                                          (wanted.Length == 0 || r.Search.Contains(wanted, StringComparison.Ordinal)));
            tab.Show(_view != NewsView.Upcoming
                ? rows.OrderByDescending(r => r.SortDate).ThenByDescending(r => r.Popularity)
                : rows.OrderBy(r => r.SortDate).ThenByDescending(r => r.Popularity));
        }

        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasDomesticSource));
    }
}

/// <summary>A tab of the Premiéry page: its title, colour and rows.</summary>
public sealed class PremiereTabItem : ObservableObject
{
    private List<PremiereRow> _all = [];

    public PremiereTabItem(PremiereTab tab, string title, Color color)
    {
        Tab = tab;
        Title = title;
        Accent = Frozen(color);
        Tint = Frozen(Color.FromArgb(0x30, color.R, color.G, color.B));
        Stripe = Frozen(Color.FromArgb(0x10, color.R, color.G, color.B));
    }

    public PremiereTab Tab { get; }

    public string Title { get; }

    public Brush Accent { get; }

    public Brush Tint { get; }

    /// <summary>Every other row of the table.</summary>
    public Brush Stripe { get; }

    /// <summary>All kept premieres of the table.</summary>
    public IReadOnlyList<PremiereRow> All => _all;

    /// <summary>The rows shown.</summary>
    public ObservableCollection<PremiereRow> Rows { get; } = [];

    public string CountText => Rows.Count.ToString(Texts.Slovak);

    internal void Set(IEnumerable<PremiereRow> rows) => _all = rows.ToList();

    internal void Show(IEnumerable<PremiereRow> rows)
    {
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(CountText));
    }

    internal static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>A row of a Premiéry table: a film or a series premiere with its mark.</summary>
public abstract class PremiereRow(MarkStore marks, string markKey, Watchlist watch) : MarkedRow(marks, markKey, watch)
{
    /// <summary>The day of the premiere.</summary>
    public abstract DateOnly SortDate { get; }

    internal abstract double Popularity { get; }

    internal abstract string Search { get; }

    public abstract PosterThumb Poster { get; }

    /// <summary>"Original (Year)" as the library names the title.</summary>
    public abstract string Caption { get; }

    /// <summary>The source has a page of the title.</summary>
    public virtual bool HasSource => false;

    /// <summary>How the episodes come (series only), shown under the title.</summary>
    public virtual string Rhythm => "";

    public string Date => $"{SortDate.Day}. {SortDate.Month}. {SortDate.Year}";

    public string Day => Universum.Core.News.Periodicity.DayName(SortDate);

    /// <summary>The premiere is today.</summary>
    public bool IsToday => SortDate == DateOnly.FromDateTime(DateTime.Now);

    /// <summary>The premiere is within the next week.</summary>
    public bool IsSoon => SortDate > DateOnly.FromDateTime(DateTime.Now) && SortDate <= DateOnly.FromDateTime(DateTime.Now).AddDays(7);

    /// <summary>"DNES", "zajtra", "o 3 dni", "o týždeň", "včera", "pred 3 dňami" …</summary>
    public string When => NewsTexts.When(SortDate);
}

/// <summary>A row of a film table.</summary>
public sealed class PremiereFilmRow : PremiereRow
{
    private readonly string _search;

    public PremiereFilmRow(FilmPremiere film, PremieresPageViewModel page)
        : base(page.Services.Marks, MarkStore.Film(film.TmdbId), page.Services.Watchlist)
    {
        Film = film;
        Poster = new PosterThumb(page.Services, film.PosterPath, PosterSize.Small);
        _search = Words.Normalize($"{film.Caption} {film.LocalTitle} {film.EnglishTitle} {film.Production}");
        OpenTmdbCommand = new RelayCommand(() => Browser.Open(TitleLinks.Tmdb(MediaKind.Movie, film.TmdbId)!));
        OpenHistraCommand = new AsyncRelayCommand(() => page.OpenAsync(o => o.OpenHistraAsync(MediaKind.Movie, film.TmdbId, film.OriginalTitle, film.Year)));
    }

    public FilmPremiere Film { get; }

    public override PosterThumb Poster { get; }

    public override DateOnly SortDate => Film.Premiere;

    internal override double Popularity => Film.Popularity;

    internal override string Search => _search + " " + Words.Normalize(Note);

    public override string Caption => Film.Caption;

    public string LocalTitle => Film.LocalTitle;

    public string Production => Film.Production;

    /// <summary>Žáner (owner, 31. 10. 2026) – TMDB's genres in Slovak.</summary>
    public string Genre => Film.Genre;

    public string Where => Film.PremiereNote;

    public string Availability => Film.Availability.Length > 0 ? Film.Availability : "–";

    public RelayCommand OpenTmdbCommand { get; }

    public AsyncRelayCommand OpenHistraCommand { get; }
}

/// <summary>A row of a series table.</summary>
public sealed class PremiereSeriesRow : PremiereRow
{
    private readonly string _search;

    public PremiereSeriesRow(SeriesPremiereRow premiere, PremieresPageViewModel page)
        : base(page.Services.Marks, MarkStore.Series(premiere.First), page.Services.Watchlist)
    {
        var first = premiere.First;
        Premiere = premiere;
        Poster = new PosterThumb(page.Services, () => page.Services.SeriesLookup.PosterAsync(first), PosterSize.Small);
        Genre = new GenreCell(page.Services, premiere.First);
        _search = Words.Normalize($"{first.Caption} {first.Title} {first.Channel}");
        OpenTmdbCommand = new AsyncRelayCommand(() => page.OpenAsync(o => o.OpenSeriesTmdbAsync(first)));
        OpenHistraCommand = new AsyncRelayCommand(() => page.OpenAsync(o => o.OpenSeriesHistraAsync(first)));
        OpenSourceCommand = new RelayCommand(() =>
        {
            if (Uri.TryCreate(first.Link, UriKind.Absolute, out var link))
            {
                Browser.Open(link);
            }
        }, () => first.Link is not null);
    }

    public SeriesPremiereRow Premiere { get; }

    public override PosterThumb Poster { get; }

    public override DateOnly SortDate => Premiere.First.AirDate;

    internal override double Popularity => 0;

    internal override string Search => _search + " " + Words.Normalize(Note);

    public override string Caption => Premiere.First.Caption;

    public string LocalTitle => Premiere.First.Title != Premiere.First.OriginalTitle ? Premiere.First.Title : "";

    public string Production => string.Join(" · ", new[] { Premiere.First.Channel, Premiere.First.Country.Length > 0 ? CountryNames.Name(Premiere.First.Country) : "" }.Where(p => p.Length > 0));

    public string Genres => Premiere.First.Genres;

    /// <summary>Žáner (owner, 31. 10. 2026) – the source's genres in Slovak, else TMDB's.</summary>
    public GenreCell Genre { get; }

    public string What => Premiere.First.Kind == EpisodeKind.SeriesPremiere ? Loc.F("nový seriál · {0}", Premiere.First.Code) : Loc.F("{0}. séria · {1}", Premiere.First.Season, Premiere.First.Code);

    public string Periodicity => Premiere.Periodicity.Length > 0 ? Premiere.Periodicity : "–";

    public override string Rhythm => Premiere.Periodicity;

    public string Episodes => Premiere.EpisodeCount is { } count ? Texts.Count(count, Loc.T("epizóda"), Loc.T("epizódy"), Loc.T("epizód")) : "–";

    public string SourceText => Loc.F("Otvoriť v zdroji ({0})", Premiere.First.Source);

    public override bool HasSource => Premiere.First.Link is not null;

    public AsyncRelayCommand OpenTmdbCommand { get; }

    public AsyncRelayCommand OpenHistraCommand { get; }

    public RelayCommand OpenSourceCommand { get; }
}
