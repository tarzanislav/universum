using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Posters;

namespace Universum.App.Pages;

/// <summary>A title found on TMDB in the results of TMDB poster.</summary>
public sealed class FoundTitleRow
{
    public FoundTitleRow(FoundTitle title, PosterThumb poster, Action<FoundTitleRow> open)
    {
        Title = title;
        Poster = poster;
        OpenCommand = new RelayCommand(() => open(this));
    }

    public FoundTitle Title { get; }

    public string Caption => Title.Caption;

    public string KindText => Title.KindText;

    public PosterThumb Poster { get; }

    public RelayCommand OpenCommand { get; }
}

/// <summary>A poster offered: from TMDB (its small copy) or drawn by AI; one of them is chosen with the mouse.</summary>
public sealed class PosterOptionRow : ObservableObject
{
    private bool _selected;

    public PosterOptionRow(PosterCandidate? candidate, PosterThumb? thumb, byte[]? draft, string label, string toolTip, Action<PosterOptionRow> select)
    {
        Candidate = candidate;
        Thumb = thumb;
        Draft = draft;
        Label = label;
        ToolTip = toolTip;
        DraftImage = draft is null ? null : PictureTools.Show(draft, 154);
        SelectCommand = new RelayCommand(() => select(this));
    }

    /// <summary>The poster of TMDB; null for a draft.</summary>
    public PosterCandidate? Candidate { get; }

    public PosterThumb? Thumb { get; }

    /// <summary>The picture drawn by AI; null for a poster of TMDB.</summary>
    public byte[]? Draft { get; }

    public ImageSource? DraftImage { get; }

    /// <summary>"CZ · 2000 × 3000 · 8,0".</summary>
    public string Label { get; }

    public string ToolTip { get; }

    public bool IsRecommended { get; init; }

    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }

    public RelayCommand SelectCommand { get; }
}

/// <summary>A background or a logo of TMDB offered in Ďalšie TMDB obrázky; one of each kind is chosen with the mouse.</summary>
public sealed class ExtraPictureRow : ObservableObject
{
    private bool _selected;

    public ExtraPictureRow(TmdbPicture picture, PosterThumb thumb, bool recommended, Action<ExtraPictureRow> select)
    {
        Picture = picture;
        Thumb = thumb;
        IsRecommended = recommended;
        var language = picture.Language is { Length: > 0 } l && l != "xx" ? l.ToUpperInvariant() switch { "CS" => "CZ", var other => other } : Loc.T("bez textu");
        Label = $"{language} · {picture.Width} × {picture.Height}";
        ToolTip = Loc.F("{0}, {1} × {2} px, hodnotenie {3} ({4} hlasov)", language, picture.Width, picture.Height, picture.VoteAverage.ToString("0.0", CultureInfo.GetCultureInfo("sk-SK")), picture.VoteCount) +
                  (recommended ? "\n" + Loc.T("Odporúčaný") : "");
        SelectCommand = new RelayCommand(() => select(this));
    }

    public TmdbPicture Picture { get; }

    public PosterThumb Thumb { get; }

    public bool IsRecommended { get; }

    /// <summary>"bez textu · 3840 × 2160".</summary>
    public string Label { get; }

    public string ToolTip { get; }

    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }

    public RelayCommand SelectCommand { get; }
}

/// <summary>The poster of a season offered in Ďalšie TMDB obrázky (ticked: it is written).</summary>
public sealed class SeasonPictureRow(TmdbSeasonPoster season, PosterThumb thumb) : ObservableObject
{
    private bool _checked = true;

    public TmdbSeasonPoster Season { get; } = season;

    public PosterThumb Thumb { get; } = thumb;

    /// <summary>"1. séria".</summary>
    public string Label => Season.Number == 0 ? Loc.T("Špeciály") : Loc.F("{0}. séria", Season.Number);

    public bool IsChecked { get => _checked; set => SetProperty(ref _checked, value); }
}

/// <summary>A group of posters offered ("CZ", "EN" …).</summary>
public sealed record PosterGroupRow(string Name, IReadOnlyList<PosterOptionRow> Posters);

/// <summary>
/// TMDB poster (owner, 31. 10. 2026): a film or series found by its name (original, Czech, Slovak or English, in any
/// script, with a typo too), with its year or by its TMDB or IMDb ID; its card (as in Listy z HISTRA); the poster UNIVERSUM
/// recommends with its opinion and the others to choose from (the original language in Latin script, English, Czech,
/// Slovak); the poster chosen is written as JPEG (quality and size of the user's choice) as folder.jpg into the main folder
/// of the title in POSTER – or, with Zlúčiť poster s filmom/seriálom, to the film or series processed (now, or once it is
/// processed). Without any poster a draft can be drawn by AI. Ďalšie TMDB obrázky (owner, 31. 10. 2026) are a block of
/// their own: the background and the logo chosen with the mouse, the season posters ticked, the episode stills; the same
/// quality and size step as the poster; written by their own button, into POSTER or – with their own Zlúčiť – to the title.
/// </summary>
public sealed class PosterPageViewModel : ObservableObject
{
    /// <summary>Posters measured for their colours at most (their small copies).</summary>
    public const int MeasuredAtMost = 40;

    /// <summary>Backgrounds and logos offered at most (the best first).</summary>
    public const int ExtrasOffered = 24;

    private readonly AppServices _services;
    private string _query = "";
    private string _searchStatus = "";
    private string _offerStatus = "";
    private string _result = "";
    private string _extrasResult = "";
    private ExtraPictureRow? _backdrop;
    private ExtraPictureRow? _logo;
    private string _draftStatus = "";
    private string _opinion = "";
    private bool _busy;
    private bool _drawing;
    private TitleCardViewModel? _card;
    private PosterOptionRow? _selected;
    private PosterOptionRow? _recommended;
    private VideoChoice<MediaKind?> _kind;
    private VideoChoice<int> _height;
    private CancellationTokenSource _title = new();
    private TmdbPictures? _pictures;

    public PosterPageViewModel(AppServices services)
    {
        _services = services;
        _kind = Kinds[0];
        _height = Heights.FirstOrDefault(h => h.Value == services.Settings.PosterHeight) ?? Heights[1];
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => Query.Trim().Length > 0 && !_busy);
        ProcessCommand = new AsyncRelayCommand(ProcessAsync, () => !_busy && Selected is not null && _card?.Details is not null);
        DraftCommand = new AsyncRelayCommand(DrawAsync, () => !_drawing && _card?.Details is not null);
        ProcessExtrasCommand = new AsyncRelayCommand(ProcessExtrasAsync, () => !_busy && Extras && _pictures is not null && _card?.Details is not null);
        OpenPosterFolderCommand = new RelayCommand(() =>
        {
            if (_services.Library is { } library && Directory.Exists(library.Posters))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(library.Posters) { UseShellExecute = true })?.Dispose();
            }
        });
        services.TmdbChanged += () => OnPropertyChanged(nameof(Requirement));
        services.LibraryChanged += () => OnPropertyChanged(nameof(Requirement));
        try
        {
            // POSTER beside MOVIES and TVSHOWS
            services.Library?.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // created when the first poster is written
        }
    }

    public IReadOnlyList<VideoChoice<MediaKind?>> Kinds { get; } = [new(null, Loc.T("Filmy aj seriály")), new(MediaKind.Movie, Loc.T("Filmy")), new(MediaKind.Series, Loc.T("Seriály"))];

    /// <summary>The height of folder.jpg (2:3): 1000 × 1500 by default – sharp on a TV, not too large.</summary>
    public IReadOnlyList<VideoChoice<int>> Heights { get; } =
        [new(1000, Loc.T("667 × 1000 (menší)")), new(1500, Loc.T("1000 × 1500 (odporúčané)")), new(2250, "1500 × 2250"), new(3000, Loc.T("2000 × 3000 (najväčší podľa TMDB)"))];

    /// <summary>Why the page cannot work yet; empty when it can.</summary>
    public string Requirement =>
        _services.PosterTmdb is null ? Loc.T("Na hľadanie plagátov treba kľúč TMDB (Nastavenia → TMDB).")
        : _services.Posters is null ? Loc.T("Plagáty sú vypnuté v Nastaveniach – zapnite ich.")
        : _services.Library is null ? Loc.T("Nastavte výstupný priečinok (Nastavenia) – plagáty sa ukladajú do jeho priečinka POSTER.")
        : "";

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                SearchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public VideoChoice<MediaKind?> Kind { get => _kind; set => SetProperty(ref _kind, value); }

    public AsyncRelayCommand SearchCommand { get; }

    public ObservableCollection<FoundTitleRow> Results { get; } = [];

    public string SearchStatus { get => _searchStatus; private set => SetProperty(ref _searchStatus, value); }

    /// <summary>The card of the title chosen.</summary>
    public TitleCardViewModel? Card
    {
        get => _card;
        private set
        {
            if (SetProperty(ref _card, value))
            {
                OnPropertyChanged(nameof(HasCard));
                OnPropertyChanged(nameof(IsSeries));
                _pictures = null;
                Backdrops.Clear();
                Logos.Clear();
                Seasons.Clear();
                SelectedBackdrop = null;
                SelectedLogo = null;
                OnPropertyChanged(nameof(HasBackdrops));
                OnPropertyChanged(nameof(HasLogos));
                OnPropertyChanged(nameof(HasSeasons));
                ExtrasResult = "";
                OnPropertyChanged(nameof(ExtrasText));
                Refresh();
            }
        }
    }

    public bool HasCard => Card is not null;

    /// <summary>The poster recommended (also among the groups).</summary>
    public PosterOptionRow? Recommended { get => _recommended; private set => SetProperty(ref _recommended, value); }

    /// <summary>UNIVERSUM's opinion of the recommended poster.</summary>
    public string Opinion { get => _opinion; private set => SetProperty(ref _opinion, value); }

    public ObservableCollection<PosterGroupRow> Groups { get; } = [];

    public string OfferStatus { get => _offerStatus; private set => SetProperty(ref _offerStatus, value); }

    /// <summary>TMDB has no poster of the title: a draft by AI is offered.</summary>
    public bool NoPosters { get; private set; }

    /// <summary>The poster chosen to be processed.</summary>
    public PosterOptionRow? Selected
    {
        get => _selected;
        private set
        {
            if (ReferenceEquals(value, _selected))
            {
                return;
            }

            if (_selected is not null)
            {
                _selected.IsSelected = false;
            }

            if (SetProperty(ref _selected, value))
            {
                if (value is not null)
                {
                    value.IsSelected = true;
                }

                OnPropertyChanged(nameof(SelectedText));
                Refresh();
            }

            if (Card is { } card)
            {
                card.ChosenPoster = value?.DraftImage;
                _ = ShowChosenAsync(card, value);
            }
        }
    }

    public string SelectedText => Selected is null ? Loc.T("Vyberte plagát kliknutím.") : Loc.T("Vybraný: ") + Selected.Label;

    /// <summary>JPEG quality 50–100 (90 by default: hardly any difference from the original, a few hundred kB).</summary>
    public int Quality
    {
        get => _services.Settings.PosterQuality;
        set
        {
            var quality = Math.Clamp(value, 50, 100);
            if (quality != _services.Settings.PosterQuality)
            {
                _services.Settings.PosterQuality = quality;
                _services.Settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExtrasSizeText));
            }
        }
    }

    public VideoChoice<int> Height
    {
        get => _height;
        set
        {
            if (SetProperty(ref _height, value) && value is not null)
            {
                _services.Settings.PosterHeight = value.Value;
                _services.Settings.Save();
                OnPropertyChanged(nameof(ExtrasSizeText));
            }
        }
    }

    /// <summary>Zlúčiť poster s filmom/seriálom: into the folder of the film or series processed (now, or once processed) instead of POSTER.</summary>
    public bool Merge
    {
        get => _services.Settings.PosterMerge;
        set
        {
            if (value != _services.Settings.PosterMerge)
            {
                _services.Settings.PosterMerge = value;
                _services.Settings.Save();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Ďalšie TMDB obrázky (owner, 31. 10. 2026; off by default – the choices below greyed): background, logo, season posters, episode stills.</summary>
    public bool Extras
    {
        get => _services.Settings.PosterExtras;
        set
        {
            Store(value, _services.Settings.PosterExtras, v => _services.Settings.PosterExtras = v);
            OfferExtras();
            Refresh();
        }
    }

    /// <summary>Zlúčiť ďalšie obrázky z TMDB s filmom/seriálom: to the title processed (now, or once processed) instead of POSTER.</summary>
    public bool ExtrasMerge { get => _services.Settings.PosterExtrasMerge; set => Store(value, _services.Settings.PosterExtrasMerge, v => _services.Settings.PosterExtrasMerge = v); }

    /// <summary>The backgrounds of TMDB to choose from (only while Ďalšie TMDB obrázky is ticked: nothing is downloaded otherwise).</summary>
    public ObservableCollection<ExtraPictureRow> Backdrops { get; } = [];

    /// <summary>The logos of TMDB to choose from.</summary>
    public ObservableCollection<ExtraPictureRow> Logos { get; } = [];

    /// <summary>The posters of the seasons (a series), each ticked or not.</summary>
    public ObservableCollection<SeasonPictureRow> Seasons { get; } = [];

    public bool HasBackdrops => Backdrops.Count > 0;

    public bool HasLogos => Logos.Count > 0;

    public bool HasSeasons => Seasons.Count > 0;

    /// <summary>The background chosen (fanart.jpg).</summary>
    public ExtraPictureRow? SelectedBackdrop { get => _backdrop; private set => Choose(ref _backdrop, value); }

    /// <summary>The logo chosen (clearlogo.png).</summary>
    public ExtraPictureRow? SelectedLogo { get => _logo; private set => Choose(ref _logo, value); }

    /// <summary>The height of the backgrounds and episode stills by the size step of the poster (16:9).</summary>
    public int ExtraHeight => Height.Value switch
    {
        <= 1000 => 720,
        <= 1500 => 1080,
        <= 2250 => 1440,
        _ => 2160,
    };

    /// <summary>The sizes the other pictures are written in (by the poster's size step and quality).</summary>
    public string ExtrasSizeText =>
        Loc.F("Podľa nastavení plagátu: pozadie a obrázky epizód najviac {0} × {1}, plagáty sérií ako folder.jpg ({2}), kvalita JPEG {3}. Logo ostáva PNG (priehľadné).", ExtraHeight * 16 / 9, ExtraHeight, Height.Label, Quality);

    /// <summary>The background as fanart.jpg (Kodi, Jellyfin, Plex).</summary>
    public bool ExtraBackdrop { get => _services.Settings.PosterBackdrop; set => Store(value, _services.Settings.PosterBackdrop, v => _services.Settings.PosterBackdrop = v); }

    /// <summary>The logo of the title as clearlogo.png (transparent).</summary>
    public bool ExtraLogo { get => _services.Settings.PosterLogo; set => Store(value, _services.Settings.PosterLogo, v => _services.Settings.PosterLogo = v); }

    /// <summary>The posters of the seasons as season01-poster.jpg … in the main folder of the series.</summary>
    public bool ExtraSeasons { get => _services.Settings.PosterSeasons; set => Store(value, _services.Settings.PosterSeasons, v => _services.Settings.PosterSeasons = v); }

    /// <summary>The stills of the episodes as "…-thumb.jpg" beside each episode.</summary>
    public bool ExtraEpisodes { get => _services.Settings.PosterEpisodes; set => Store(value, _services.Settings.PosterEpisodes, v => _services.Settings.PosterEpisodes = v); }

    /// <summary>The title is a series (season posters and episode stills).</summary>
    public bool IsSeries => Card?.Kind == MediaKind.Series;

    /// <summary>What TMDB has of the other pictures.</summary>
    public string ExtrasText
    {
        get
        {
            if (_pictures is not { } pictures || Card?.Details is not { } details)
            {
                return "";
            }

            var backdrop = PosterChoice.BestBackdrop(pictures.Backdrops);
            var logo = PosterChoice.BestLogo(pictures.Logos, details.OriginalLanguage);
            return string.Join(" · ", new[]
            {
                backdrop is null ? Loc.T("pozadie: TMDB nemá") : Loc.F("pozadie: {0} × {1}", backdrop.Width, backdrop.Height) + (backdrop.Language is null ? " " + Loc.T("bez textu") : ""),
                logo is null ? Loc.T("logo: TMDB nemá") : Loc.F("logo: {0}", logo.Language is { Length: > 0 } l ? (l == "cs" ? "CZ" : l.ToUpperInvariant()) : Loc.T("bez jazyka")),
                details.Kind == MediaKind.Series ? Loc.F("plagáty sérií: {0}", details.SeasonPosters.Count) : null,
            }.OfType<string>());
        }
    }

    public AsyncRelayCommand ProcessCommand { get; }

    /// <summary>Spracovať ďalšie TMDB obrázky.</summary>
    public AsyncRelayCommand ProcessExtrasCommand { get; }

    /// <summary>What the processing of the other pictures did.</summary>
    public string ExtrasResult { get => _extrasResult; private set => SetProperty(ref _extrasResult, value); }

    public RelayCommand OpenPosterFolderCommand { get; }

    public string Result { get => _result; private set => SetProperty(ref _result, value); }

    public AsyncRelayCommand DraftCommand { get; }

    public string DraftStatus { get => _draftStatus; private set => SetProperty(ref _draftStatus, value); }

    /// <summary>Which service draws and what is sent to it.</summary>
    public string DraftHint => _services.DraftKeys.Service is { } service
        ? Loc.F("Kreslí {0}{1}. Pošle sa len názov, rok, žánre a dej – nie herci ani iné údaje. Návrh je len pre vašu knižnicu – do TMDB sa nahrávať nesmie.", service, service == "OpenAI" ? Loc.T(" (platené, ~0,04 $ za návrh)") : Loc.T(" (zadarmo, denný limit)"))
        : Loc.T("Na návrh cez AI nastavte v Nastaveniach účet Cloudflare (zadarmo) alebo kľúč OpenAI (platené).");

    /// <summary>The posters of the title are offered (awaited by the self-test).</summary>
    public Task OfferReady { get; private set; } = Task.CompletedTask;

    /// <summary>Shows a title found (also the self-test).</summary>
    public Task OpenAsync(MediaKind kind, int id)
    {
        _title.Cancel();
        _title.Dispose();
        _title = new CancellationTokenSource();
        var card = new TitleCardViewModel(_services, kind, id);
        card.OpenRequested += (k, i) => _ = OpenAsync(k, i);
        Card = card;
        Recommended = null;
        Selected = null;
        Groups.Clear();
        Opinion = "";
        Result = "";
        DraftStatus = "";
        NoPosters = false;
        OnPropertyChanged(nameof(NoPosters));
        OnPropertyChanged(nameof(DraftHint));
        OfferReady = OfferAsync(card, _title.Token);
        return OfferReady;
    }

    private async Task SearchAsync()
    {
        if (_services.Finder is not { } finder)
        {
            SearchStatus = Requirement;
            return;
        }

        _busy = true;
        Refresh();
        try
        {
            SearchStatus = Loc.T("Hľadám na TMDB…");
            var found = await finder.FindAsync(Query, Kind.Value);
            Results.Clear();
            foreach (var title in found)
            {
                Results.Add(new FoundTitleRow(title, new PosterThumb(_services, title.PosterPath, PosterSize.Small), row => _ = OpenAsync(row.Title.Kind, row.Title.Id)));
            }

            SearchStatus = found.Count == 0 ? Loc.T("TMDB nič také nenašlo – skúste iný názov, rok alebo TMDB či IMDb ID.") : Loc.F("Nájdené: {0}. Kliknite na titul.", found.Count);
            if (found.Count == 1)
            {
                await OpenAsync(found[0].Kind, found[0].Id);
            }
        }
        catch (TitleServiceException ex)
        {
            SearchStatus = ex.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    /// <summary>Opens a result (the row's command).</summary>
    public Task OpenResultAsync(FoundTitleRow row) => OpenAsync(row.Title.Kind, row.Title.Id);

    // the posters of the title, their small copies measured, the recommendation and the groups
    private async Task OfferAsync(TitleCardViewModel card, CancellationToken cancellationToken)
    {
        await card.Loading;
        if (cancellationToken.IsCancellationRequested || card.Details is not { } details || _services.TitleDetails is not { } tmdb)
        {
            Refresh();
            return;
        }

        try
        {
            OfferStatus = Loc.T("Čítam plagáty z TMDB…");
            var pictures = await tmdb.PicturesAsync(details.Kind, details.Id, details.OriginalLanguage, cancellationToken);
            _pictures = pictures;
            OnPropertyChanged(nameof(ExtrasText));
            OfferExtras();
            var posters = pictures.Posters;
            var looks = new Dictionary<string, PictureLook>(StringComparer.Ordinal);
            if (_services.Posters is { } cache)
            {
                var measured = posters
                    .Where(p => PosterChoice.Tier(p.Language, details.OriginalLanguage) <= 3 || p.Language == "sk")
                    .OrderBy(p => PosterChoice.Tier(p.Language, details.OriginalLanguage)).ThenByDescending(p => p.VoteAverage)
                    .Take(MeasuredAtMost).ToList();
                OfferStatus = Loc.F("Posudzujem farby plagátov ({0})…", measured.Count);
                var tasks = measured.Select(async p =>
                {
                    if (await cache.GetAsync(p.Path, PosterSize.Small, cancellationToken) is { } file && await Task.Run(() => PictureTools.Look(file), cancellationToken) is { } look)
                    {
                        lock (looks)
                        {
                            looks[p.Path] = look;
                        }
                    }
                });
                await Task.WhenAll(tasks);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var offer = PosterChoice.Offer(posters, looks, details.Genres, details.OriginalLanguage, LibraryNames.IsLatinScript(details.OriginalTitle));
            Groups.Clear();
            foreach (var group in offer.Groups)
            {
                Groups.Add(new PosterGroupRow(group.Name, group.Posters.Select(c => Row(c, c.Picture.Path == offer.Recommended?.Picture.Path)).ToList()));
            }

            Recommended = offer.Recommended is { } best ? Groups.SelectMany(g => g.Posters).FirstOrDefault(r => r.Candidate?.Picture.Path == best.Picture.Path) ?? Row(best, true) : null;
            Opinion = offer.Opinion;
            Selected = Recommended;
            NoPosters = posters.Count == 0;
            OnPropertyChanged(nameof(NoPosters));
            OfferStatus = posters.Count == 0 ? Loc.T("TMDB k titulu nemá žiadny plagát – môžete dať navrhnúť plagát cez AI.")
                : Loc.F("Plagáty na TMDB: {0} (v ponuke CZ, SK, EN a pôvodný jazyk v latinke).", posters.Count);
        }
        catch (OperationCanceledException)
        {
            // another title
        }
        catch (TitleServiceException ex)
        {
            OfferStatus = ex.Message;
        }
        finally
        {
            Refresh();
        }
    }

    private PosterOptionRow Row(PosterCandidate candidate, bool recommended)
    {
        var p = candidate.Picture;
        var language = p.Language is { Length: > 0 } l ? l.ToUpperInvariant() switch { "CS" => "CZ", var other => other } : Loc.T("bez textu");
        var votes = p.VoteCount > 0 ? $" · {p.VoteAverage.ToString("0.0", CultureInfo.GetCultureInfo("sk-SK"))}" : "";
        return new PosterOptionRow(candidate, new PosterThumb(_services, p.Path, PosterSize.Small), null, $"{language} · {p.Width} × {p.Height}{votes}",
            Loc.F("{0}, {1} × {2} px, hodnotenie {3} ({4} hlasov)", language, p.Width, p.Height, p.VoteAverage.ToString("0.0", CultureInfo.GetCultureInfo("sk-SK")), p.VoteCount) +
            (candidate.Acceptable ? "" : Loc.T(" – menší alebo iných rozmerov, ako TMDB odporúča")) + (recommended ? "\n" + Loc.T("Odporúčaný") : ""),
            row => Selected = row)
        {
            IsRecommended = recommended,
        };
    }

    // the chosen poster of TMDB larger in the card
    private async Task ShowChosenAsync(TitleCardViewModel card, PosterOptionRow? row)
    {
        if (row?.Candidate is not { } candidate || _services.Posters is not { } cache)
        {
            return;
        }

        if (await cache.GetAsync(candidate.Picture.Path, PosterSize.Large) is { } file && ReferenceEquals(Selected, row) &&
            PictureTools.Show(await File.ReadAllBytesAsync(file), 342) is { } image)
        {
            card.ChosenPoster = image;
        }
    }

    private async Task ProcessAsync()
    {
        if (Selected is not { } chosen || Card?.Details is not { } details || _services.Library is not { } library)
        {
            Result = Requirement;
            return;
        }

        if (details.Title is not { } title)
        {
            Result = Loc.T("Titul nemá názov, podľa ktorého by sa dal pomenovať priečinok.");
            return;
        }

        _busy = true;
        Refresh();
        try
        {
            Result = Loc.T("Sťahujem plagát v plnej veľkosti…");
            byte[] picture;
            if (chosen.Draft is { } draft)
            {
                picture = draft;
            }
            else if (_services.Posters is { } cache && await cache.GetAsync(chosen.Candidate!.Picture.Path, PosterSize.Original) is { } file)
            {
                picture = await File.ReadAllBytesAsync(file);
            }
            else
            {
                Result = Loc.T("Plagát sa nedá stiahnuť z TMDB – skúste to znova.");
                return;
            }

            var jpeg = PictureTools.ToJpeg(picture, Height.Value, Quality);
            var size = $"{jpeg.Length / 1024} kB";
            var places = new PosterPlaces(library, _services.InputDirectory);
            if (!Merge)
            {
                var folder = places.PosterFolder(title);
                Result = Done(PosterPlaces.Save(folder, jpeg), folder, size);
                return;
            }

            if (places.LibraryFolder(title, details.Id) is { } processed)
            {
                Result = Done(PosterPlaces.Save(processed, jpeg), processed, size);
                return;
            }

            Result = Loc.T("Hľadám film alebo seriál na vstupe…");
            if (await Task.Run(() => places.InInput(details.Kind, details.Names, details.Year)) is { } input)
            {
                _services.WaitingPosters.Add(title, details.Id, jpeg);
                Result = Loc.F("{0} je na vstupe ({1}), ešte nie je spracovaný – plagát čaká. Keď ho UNIVERSUM spracuje, dá {2} do jeho priečinka v {3}.",
                         details.Kind == MediaKind.Movie ? Loc.T("Film") : Loc.T("Seriál"), Path.GetFileName(input), PosterPlaces.FileName,
                         details.Kind == MediaKind.Movie ? LibraryNames.MoviesFolder : LibraryNames.ShowsFolder);
                return;
            }

            Result = Loc.F("{0} sa nenašiel ani vo výstupe ({1}, {2}), ani na vstupe. Nič sa nezapísalo – vypnite Zlúčiť poster s filmom/seriálom a plagát sa uloží do POSTER.",
                     details.Kind == MediaKind.Movie ? Loc.T("Film") : Loc.T("Seriál"), LibraryNames.MoviesFolder, LibraryNames.ShowsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException)
        {
            Result = Loc.T("Plagát sa nedá spracovať: ") + ex.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    // Spracovať ďalšie TMDB obrázky: into POSTER, or (Zlúčiť ďalšie obrázky) to the title processed – or waiting for it
    private async Task ProcessExtrasAsync()
    {
        if (Card?.Details is not { } details || _services.Library is not { } library)
        {
            ExtrasResult = Requirement;
            return;
        }

        if (details.Title is not { } title)
        {
            ExtrasResult = Loc.T("Titul nemá názov, podľa ktorého by sa dal pomenovať priečinok.");
            return;
        }

        var stills = details.Kind == MediaKind.Series && ExtraEpisodes;
        if (!ExtraBackdrop && !ExtraLogo && !(details.Kind == MediaKind.Series && ExtraSeasons) && !stills)
        {
            ExtrasResult = Loc.T("Zaškrtnite, ktoré obrázky sa majú spracovať.");
            return;
        }

        _busy = true;
        Refresh();
        try
        {
            var places = new PosterPlaces(library, _services.InputDirectory);
            var kind = details.Kind == MediaKind.Movie ? Loc.T("Film") : Loc.T("Seriál");
            if (!ExtrasMerge)
            {
                var folder = places.PosterFolder(title);
                ExtrasResult = Written(folder, await MainFolderExtrasAsync(details, text => ExtrasResult = text), stills ? await EpisodeStillsAsync(folder, details, processed: false) : 0);
                return;
            }

            if (places.LibraryFolder(title, details.Id) is { } processed)
            {
                ExtrasResult = Written(processed, await MainFolderExtrasAsync(details, text => ExtrasResult = text), stills ? await EpisodeStillsAsync(processed, details, processed: true) : 0);
                return;
            }

            ExtrasResult = Loc.T("Hľadám film alebo seriál na vstupe…");
            if (await Task.Run(() => places.InInput(details.Kind, details.Names, details.Year)) is { } input)
            {
                var pictures = await MainFolderExtrasAsync(details, text => ExtrasResult = text);
                if (pictures.Count == 0 && !stills)
                {
                    ExtrasResult = Loc.T("TMDB nemá nič z vybraných obrázkov.");
                    return;
                }

                _services.WaitingPosters.Add(title, details.Id, null, pictures);
                ExtrasResult = Loc.F("{0} je na vstupe ({1}), ešte nie je spracovaný – ", kind, Path.GetFileName(input)) +
                               (pictures.Count > 0 ? Loc.F("obrázky ({0}) čakajú; keď ho UNIVERSUM spracuje, dá ich do jeho priečinka v {1}.", string.Join(", ", pictures.Keys), details.Kind == MediaKind.Movie ? LibraryNames.MoviesFolder : LibraryNames.ShowsFolder) : "") +
                               (stills ? Loc.T(" Obrázky epizód potrebujú spracované epizódy – spustite ich znova, keď bude seriál v TVSHOWS.") : "");
                return;
            }

            ExtrasResult = Loc.F("{0} sa nenašiel ani vo výstupe ({1}, {2}), ani na vstupe. Nič sa nezapísalo – vypnite Zlúčiť ďalšie obrázky z TMDB s filmom/seriálom a obrázky sa uložia do POSTER.",
                           kind, LibraryNames.MoviesFolder, LibraryNames.ShowsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException or TitleServiceException)
        {
            ExtrasResult = Loc.T("Obrázky sa nedajú spracovať: ") + ex.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    // the pictures written into the folder (an existing one renamed); what was done in words
    private static string Written(string folder, Dictionary<string, byte[]> pictures, int episodes)
    {
        var written = new List<string>();
        var problems = new List<string>();
        foreach (var (name, bytes) in pictures)
        {
            var saved = PosterPlaces.Save(folder, bytes, name);
            if (saved.Done)
            {
                written.Add(saved.KeptAs is { } kept ? Loc.F("{0} (pôvodný premenovaný na {1})", name, kept) : name);
            }
            else if (saved.Problem is { } problem)
            {
                problems.Add(problem);
            }
        }

        if (written.Count == 0 && episodes == 0 && problems.Count == 0)
        {
            return Loc.T("TMDB nemá nič z vybraných obrázkov – nič sa nezapísalo.");
        }

        return (written.Count > 0 ? Loc.F("Uložené do {0}: ", folder) + string.Join(", ", written) + "." : "") +
               (episodes > 0 ? Loc.F(" Obrázky epizód: {0}.", episodes) : "") +
               (problems.Count > 0 ? " " + string.Join(" ", problems) : "");
    }

    // the backgrounds, logos and season posters offered (once per title, only while the block is on)
    private void OfferExtras()
    {
        if (!Extras || _pictures is not { } pictures || Card?.Details is not { } details || Backdrops.Count + Logos.Count + Seasons.Count > 0)
        {
            return;
        }

        foreach (var (picture, i) in PosterChoice.Backdrops(pictures.Backdrops).Take(ExtrasOffered).Select((p, i) => (p, i)))
        {
            Backdrops.Add(new ExtraPictureRow(picture, new PosterThumb(_services, picture.Path, PosterSize.Large), i == 0, r => SelectedBackdrop = r));
        }

        foreach (var (picture, i) in PosterChoice.Logos(pictures.Logos, details.OriginalLanguage).Take(ExtrasOffered).Select((p, i) => (p, i)))
        {
            Logos.Add(new ExtraPictureRow(picture, new PosterThumb(_services, picture.Path, PosterSize.Large), i == 0, r => SelectedLogo = r));
        }

        if (details.Kind == MediaKind.Series)
        {
            foreach (var season in details.SeasonPosters.Where(p => PosterCache.IsValidPath(p.PosterPath)))
            {
                Seasons.Add(new SeasonPictureRow(season, new PosterThumb(_services, season.PosterPath, PosterSize.Small)));
            }
        }

        SelectedBackdrop = Backdrops.FirstOrDefault();
        SelectedLogo = Logos.FirstOrDefault();
        OnPropertyChanged(nameof(HasBackdrops));
        OnPropertyChanged(nameof(HasLogos));
        OnPropertyChanged(nameof(HasSeasons));
    }

    private void Choose(ref ExtraPictureRow? field, ExtraPictureRow? value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (ReferenceEquals(field, value))
        {
            return;
        }

        if (field is not null)
        {
            field.IsSelected = false;
        }

        field = value;
        if (value is not null)
        {
            value.IsSelected = true;
        }

        OnPropertyChanged(name);
    }

    // fanart.jpg, clearlogo.png and the season posters chosen, as they go into the main folder of the title (the size step
    // and the quality of the poster)
    private async Task<Dictionary<string, byte[]>> MainFolderExtrasAsync(TmdbDetails details, Action<string> progress)
    {
        var extras = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (_pictures is not { } pictures || _services.Posters is not { } cache)
        {
            return extras;
        }

        async Task<byte[]?> Original(string path) =>
            await cache.GetAsync(path, PosterSize.Original) is { } file ? await File.ReadAllBytesAsync(file) : null;

        if (ExtraBackdrop && (SelectedBackdrop?.Picture ?? PosterChoice.BestBackdrop(pictures.Backdrops)) is { } backdrop)
        {
            progress(Loc.T("Sťahujem pozadie…"));
            if (await Original(backdrop.Path) is { } background)
            {
                extras["fanart.jpg"] = PictureTools.ToJpeg(background, ExtraHeight, Quality);
            }
        }

        if (ExtraLogo && (SelectedLogo?.Picture ?? PosterChoice.BestLogo(pictures.Logos, details.OriginalLanguage)) is { } logo)
        {
            progress(Loc.T("Sťahujem logo…"));
            if (await Original(logo.Path) is { Length: > 8 } png && png[0] == 0x89 && png[1] == 0x50)
            {
                extras["clearlogo.png"] = png;
            }
        }

        if (details.Kind == MediaKind.Series && ExtraSeasons)
        {
            var chosen = Seasons.Count > 0 ? Seasons.Where(s => s.IsChecked).Select(s => s.Season) : details.SeasonPosters;
            foreach (var season in chosen)
            {
                progress(Loc.F("Sťahujem plagát {0}. série…", season.Number));
                if (await Original(season.PosterPath) is { } poster)
                {
                    extras[$"season{season.Number:00}-poster.jpg"] = PictureTools.ToJpeg(poster, Height.Value, Quality);
                }
            }
        }

        return extras;
    }

    // the stills of the episodes: beside the episodes processed ("<episode>-thumb.jpg" in their season folders); in POSTER
    // in season folders named as in TVSHOWS ("S01E02-thumb.jpg"); how many were written
    private async Task<int> EpisodeStillsAsync(string folder, TmdbDetails details, bool processed)
    {
        if (_services.TitleDetails is not { } tmdb || _services.Posters is not { } cache)
        {
            return 0;
        }

        var count = 0;
        var seasons = details.Seasons is { } total ? Enumerable.Range(1, total) : details.SeasonPosters.Select(s => s.Number);
        foreach (var season in seasons)
        {
            var seasonFolder = Path.Combine(folder, LibraryNames.SeasonFolder(season));
            var episodes = processed
                ? (Directory.Exists(seasonFolder) ? Directory.EnumerateFiles(seasonFolder, "*.mkv").Select(f => (File: f, Number: EpisodeNumber.TryParse(Path.GetFileName(f)))).Where(e => e.Number?.Season == season).ToList() : [])
                : null;
            if (processed && episodes!.Count == 0)
            {
                continue;
            }

            var stills = await tmdb.StillsAsync(details.Id, season);
            foreach (var still in stills)
            {
                var name = processed
                    ? episodes!.FirstOrDefault(e => e.Number is { } n && (n.Episode == still.Episode || (n.Episode <= still.Episode && n.LastEpisode >= still.Episode))).File is { } file
                        ? Path.GetFileNameWithoutExtension(file) + "-thumb.jpg" : null
                    : $"S{season:00}E{still.Episode:00}-thumb.jpg";
                if (name is null)
                {
                    continue;
                }

                ExtrasResult = Loc.F("Sťahujem obrázky epizód ({0})…", ++count);
                if (await cache.GetAsync(still.Path, PosterSize.Original) is { } source)
                {
                    PosterPlaces.Save(seasonFolder, PictureTools.ToJpeg(await File.ReadAllBytesAsync(source), ExtraHeight, Quality), name);
                }
            }
        }

        return count;
    }

    private void Store(bool value, bool current, Action<bool> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (value != current)
        {
            set(value);
            _services.Settings.Save();
            OnPropertyChanged(name);
        }
    }

    private static string Done(PosterPlaces.Saved saved, string folder, string size) =>
        saved.Done
            ? Loc.F("Uložené: {0} ({1}).", Path.Combine(folder, PosterPlaces.FileName), size) + (saved.KeptAs is { } kept ? Loc.F(" Pôvodný {0} tam už bol – premenovaný na {1}.", PosterPlaces.FileName, kept) : "")
            : saved.Problem ?? Loc.T("Nepodarilo sa.");

    private async Task DrawAsync()
    {
        if (Card?.Details is not { } details)
        {
            return;
        }

        _drawing = true;
        Refresh();
        var card = Card;
        try
        {
            DraftStatus = Loc.T("AI kreslí návrh plagátu (môže to trvať do minúty)…");
            var draft = await _services.Drafts.CreateAsync(details, _services.DraftKeys, _title.Token);
            if (!ReferenceEquals(card, Card))
            {
                // another title opened meanwhile: this draft is not its poster
                return;
            }

            if (draft.Picture is not { } picture)
            {
                DraftStatus = draft.Problem ?? Loc.T("Návrh sa nepodaril.");
                return;
            }

            var row = new PosterOptionRow(null, null, picture, Loc.F("Návrh AI ({0})", draft.Service), Loc.F("Návrh plagátu nakreslený cez {0} – len pre vašu knižnicu.", draft.Service), r => Selected = r);
            var others = Groups.FirstOrDefault(g => g.Name == Loc.T("Návrhy AI"))?.Posters ?? [];
            var index = Groups.ToList().FindIndex(g => g.Name == Loc.T("Návrhy AI"));
            var group = new PosterGroupRow(Loc.T("Návrhy AI"), [row, .. others]);
            if (index >= 0)
            {
                Groups[index] = group;
            }
            else
            {
                Groups.Insert(0, group);
            }

            Selected = row;
            DraftStatus = Loc.F("Návrh je hotový ({0}). Vybraný – spracujete ho ako iný plagát; ďalší návrh vyzerá inak.", draft.Service);
        }
        catch (OperationCanceledException)
        {
            DraftStatus = "";
        }
        finally
        {
            _drawing = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        SearchCommand?.NotifyCanExecuteChanged();
        ProcessCommand?.NotifyCanExecuteChanged();
        DraftCommand?.NotifyCanExecuteChanged();
        ProcessExtrasCommand?.NotifyCanExecuteChanged();
    }
}
