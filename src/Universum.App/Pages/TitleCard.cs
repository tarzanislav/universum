using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Library;
using Universum.Core.Library.Histra;
using Universum.Core.Library.Tmdb;

namespace Universum.App.Pages;

/// <summary>What HISTRA tells about a title before TMDB answers (Listy z HISTRA), or when there is no TMDB.</summary>
public sealed record TitleSeed(string OriginalTitle, string LocalTitle, int? Year, string Overview, double? HistraRating, int? Runtime, string? PosterPath, TitleIds Ids);

/// <summary>A film of the collection in the card: its poster and name; a click shows it.</summary>
public sealed class CollectionRow(TmdbCollectionPart part, PosterThumb poster, bool current, Action open)
{
    public TmdbCollectionPart Part { get; } = part;

    public string Caption => Part.Caption;

    public PosterThumb Poster { get; } = poster;

    /// <summary>The film the card shows.</summary>
    public bool IsCurrent { get; } = current;

    public RelayCommand OpenCommand { get; } = new(open, () => !current);
}

/// <summary>
/// The card of a film or series (owner, 31. 10. 2026 – TMDB poster and Listy z HISTRA alike): the poster at the top, the
/// links (HISTRA, TMDB, IMDb, Trakt, ČSFD), the identifiers in two columns, the names with the year (original, English
/// when the original is not, Czech; a title in another script: English, Czech, then the original), the kind, length and
/// ratings, the genre, the directors and actors (names only), the plot and the other films of its collection. TMDB is
/// asked once for the detail (kept 30 days), OMDb once for its ratings when the user has its key.
/// </summary>
public sealed class TitleCardViewModel : ObservableObject
{
    private static readonly CultureInfo Slovak = CultureInfo.GetCultureInfo("sk-SK");
    private readonly AppServices _services;
    private readonly TitleSeed? _seed;
    private TmdbDetails? _details;
    private OtherRatings? _other;
    private ImageSource? _chosenPoster;
    private string _status = "";
    private bool _loading;

    public TitleCardViewModel(AppServices services, MediaKind kind, int? tmdbId, TitleSeed? seed = null)
    {
        _services = services;
        _seed = seed;
        Kind = kind;
        TmdbId = tmdbId;
        Poster = new PosterThumb(services, () => Task.FromResult(_details?.PosterPath ?? seed?.PosterPath), PosterSize.Large);
        Loading = LoadAsync();
    }

    public MediaKind Kind { get; }

    public int? TmdbId { get; }

    /// <summary>The detail from TMDB, once read.</summary>
    public TmdbDetails? Details => _details;

    /// <summary>The detail is read (awaited by the page and the self-test).</summary>
    public Task Loading { get; }

    /// <summary>Another title of the card asked for (a film of the collection).</summary>
    public event Action<MediaKind, int>? OpenRequested;

    public PosterThumb Poster { get; private set; }

    /// <summary>The poster chosen on TMDB poster, shown over the main poster.</summary>
    public ImageSource? ChosenPoster { get => _chosenPoster; set => SetProperty(ref _chosenPoster, value); }

    public bool IsLoading { get => _loading; private set => SetProperty(ref _loading, value); }

    /// <summary>What is being read, or why something is missing.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => Status.Length > 0;

    public IReadOnlyList<TitleLink> Links { get; private set; } = [];

    /// <summary>The identifiers (shown in two columns).</summary>
    public IReadOnlyList<TitleDetail> Ids { get; private set; } = [];

    /// <summary>The names with the year, the main one first.</summary>
    public IReadOnlyList<string> Names { get; private set; } = [];

    public string MainName => Names.FirstOrDefault() ?? "";

    public IReadOnlyList<string> OtherNames => Names.Skip(1).ToList();

    /// <summary>"Film · 2 h 16 min (136 min)", "Seriál · 3 série · 24 epizód · epizóda 45 min".</summary>
    public string Facts { get; private set; } = "";

    /// <summary>"TMDB 8,2 (24 315 hlasov) · IMDb 8,7 · Rotten Tomatoes 88 % · Metacritic 73 · HISTRA 8,0".</summary>
    public string Ratings { get; private set; } = "";

    public string Genre { get; private set; } = "";

    public bool HasGenre => Genre.Length > 0;

    /// <summary>"Réžia" of a film, "Tvorcovia" of a series.</summary>
    public string DirectorsLabel => Kind == MediaKind.Movie ? "Réžia" : "Tvorcovia";

    public string Directors { get; private set; } = "";

    public bool HasDirectors => Directors.Length > 0;

    public string Cast { get; private set; } = "";

    public bool HasCast => Cast.Length > 0;

    public string Overview { get; private set; } = "";

    public bool HasOverview => Overview.Length > 0;

    public string CollectionName { get; private set; } = "";

    public ObservableCollection<CollectionRow> Collection { get; } = [];

    public bool HasCollection => Collection.Count > 1;

    private async Task LoadAsync()
    {
        Fill();
        if (TmdbId is not { } id)
        {
            return;
        }

        if (_services.TitleDetails is not { } details)
        {
            Status = _seed is null ? "Bez kľúča TMDB sa detail titulu nedá načítať (Nastavenia → TMDB)." : "";
            return;
        }

        IsLoading = true;
        try
        {
            Status = "Načítavam z TMDB…";
            _details = await details.GetAsync(Kind, id);
            Status = _details is null ? "TMDB titul s týmto ID nemá." : "";
            if (_details is not null)
            {
                Poster.Reset();
                Fill();
                await LoadMoreAsync(details, _details);
            }
        }
        catch (TitleServiceException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // the ratings of OMDb (with the user's key) and the films of the collection
    private async Task LoadMoreAsync(TmdbTitleDetails tmdb, TmdbDetails details)
    {
        if (_services.Omdb is { } omdb && details.Imdb is { } imdb)
        {
            try
            {
                _other = await omdb.RatingsAsync(imdb);
                Fill();
            }
            catch (TitleServiceException ex)
            {
                Status = ex.Message;
            }
        }

        if (details.CollectionId is { } collectionId && await tmdb.CollectionAsync(collectionId) is { } collection)
        {
            CollectionName = collection.Name.Length > 0 ? collection.Name : details.CollectionName;
            Collection.Clear();
            foreach (var part in collection.Parts)
            {
                Collection.Add(new CollectionRow(part, new PosterThumb(_services, part.PosterPath, PosterSize.Small), part.Id == details.Id,
                    () => OpenRequested?.Invoke(MediaKind.Movie, part.Id)));
            }

            OnPropertyChanged(nameof(CollectionName));
            OnPropertyChanged(nameof(HasCollection));
        }
    }

    // everything the card shows, from TMDB's detail when read, else from HISTRA's data
    private void Fill()
    {
        var d = _details;
        var ids = d is null ? _seed?.Ids ?? new TitleIds(Tmdb: TmdbId) : new TitleIds(_seed?.Ids.Histra, d.Id, d.Imdb ?? _seed?.Ids.Imdb, _seed?.Ids.Trakt, d.Tvdb ?? _seed?.Ids.Tvdb);
        var original = d?.OriginalTitle ?? _seed?.OriginalTitle ?? "";
        var latin = LibraryNames.IsLatinScript(original);
        var searchName = latin ? original : d?.CzechTitle is { Length: > 0 } cz ? cz : d?.EnglishTitle ?? _seed?.LocalTitle ?? original;
        Links = new[]
        {
            ("HISTRA", TitleLinks.Histra(Kind, ids.Histra)),
            ("TMDB", TitleLinks.Tmdb(Kind, ids.Tmdb)),
            ("IMDb", TitleLinks.Imdb(ids.Imdb)),
            ("Trakt", TitleLinks.Trakt(Kind, ids.Tmdb)),
            ("ČSFD (hľadať)", TitleLinks.CsfdSearch(searchName)),
        }.Where(l => l.Item2 is not null).Select(l => new TitleLink(l.Item1, l.Item2!)).ToList();
        Ids = new[]
        {
            ("HISTRA", ids.Histra?.ToString(CultureInfo.InvariantCulture)),
            ("TMDB", ids.Tmdb?.ToString(CultureInfo.InvariantCulture)),
            ("IMDb", ids.Imdb),
            ("Trakt", ids.Trakt?.ToString(CultureInfo.InvariantCulture)),
            ("TVDB", ids.Tvdb?.ToString(CultureInfo.InvariantCulture)),
        }.Where(i => i.Item2 is not null).Select(i => new TitleDetail(i.Item1, i.Item2!)).ToList();

        Names = d is null
            ? new[] { original, _seed?.LocalTitle ?? "" }.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Select(n => WithYear(n, _seed?.Year)).ToList()
            : NamesOf(d);

        var runtime = d?.Runtime ?? _seed?.Runtime;
        Facts = string.Join(" · ", new[]
        {
            Kind == MediaKind.Movie ? "Film" : "Seriál",
            Kind == MediaKind.Series && d?.Seasons is { } seasons ? Count(seasons, "séria", "série", "sérií") : null,
            Kind == MediaKind.Series && d?.Episodes is { } episodes ? Count(episodes, "epizóda", "epizódy", "epizód") : null,
            runtime is { } minutes ? (Kind == MediaKind.Series ? "epizóda " : "") + Length(minutes) : null,
        }.OfType<string>());

        Ratings = string.Join(" · ", new[]
        {
            d?.Rating is { } tmdb ? $"TMDB {tmdb.ToString("0.0", Slovak)}" + (d.RatingVotes > 0 ? $" ({d.RatingVotes.ToString("N0", Slovak)} hlasov)" : "") : null,
            _other?.Imdb is { } imdb ? $"IMDb {imdb}" : null,
            _other?.RottenTomatoes is { } tomatoes ? $"Rotten Tomatoes {tomatoes}" : null,
            _other?.Metacritic is { } metacritic ? $"Metacritic {metacritic}" : null,
            _seed?.HistraRating is { } histra ? $"HISTRA {histra.ToString("0.0", Slovak)}" : null,
        }.OfType<string>());
        Genre = d is null ? "" : TmdbGenres.Join(d.Genres);
        Directors = d is null ? "" : string.Join(", ", d.Directors);
        Cast = d is null ? "" : string.Join(", ", d.Cast);
        Overview = d?.Overview is { Length: > 0 } plot ? plot : _seed?.Overview ?? "";
        foreach (var name in new[]
                 {
                     nameof(Links), nameof(Ids), nameof(Names), nameof(MainName), nameof(OtherNames), nameof(Facts), nameof(Ratings), nameof(Genre), nameof(HasGenre),
                     nameof(Directors), nameof(HasDirectors), nameof(Cast), nameof(HasCast), nameof(Overview), nameof(HasOverview), nameof(Details),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>
    /// The names with the year: the original, English when the original is not English, Czech; a title in another script
    /// (Chinese, Japanese …): English, Czech, then the original in its script. Each name once.
    /// </summary>
    public static IReadOnlyList<string> NamesOf(TmdbDetails d)
    {
        var names = LibraryNames.IsLatinScript(d.OriginalTitle)
            ? new[] { d.OriginalTitle, d.OriginalLanguage == "en" ? "" : d.EnglishTitle, d.CzechTitle }
            : new[] { d.EnglishTitle, d.CzechTitle, d.OriginalTitle };
        return names.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Select(n => WithYear(n, d.Year)).ToList();
    }

    /// <summary>"2 h 16 min (136 min)"; "45 min".</summary>
    public static string Length(int minutes) => minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min ({minutes} min)" : $"{minutes} min";

    private static string WithYear(string name, int? year) => year is { } y ? $"{name} ({y.ToString(CultureInfo.InvariantCulture)})" : name;

    private static string Count(int count, string one, string few, string many) => $"{count} {(count == 1 ? one : count is >= 2 and <= 4 ? few : many)}";
}
