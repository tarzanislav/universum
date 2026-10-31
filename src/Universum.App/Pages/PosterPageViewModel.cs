using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

/// <summary>A group of posters offered ("CZ", "EN" …).</summary>
public sealed record PosterGroupRow(string Name, IReadOnlyList<PosterOptionRow> Posters);

/// <summary>
/// TMDB poster (owner, 31. 10. 2026): a film or series found by its name (original, Czech, Slovak or English, in any
/// script, with a typo too), with its year or by its TMDB or IMDb ID; its card (as in Listy z HISTRA); the poster UNIVERSUM
/// recommends with its opinion and the others to choose from (the original language in Latin script, English, Czech,
/// Slovak); the poster chosen is written as JPEG (quality and size of the user's choice) as folder.jpg into the main folder
/// of the title in POSTER – or, with Zlúčiť poster s filmom, to the film or series processed (now, or once it is
/// processed). Without any poster a draft can be drawn by AI.
/// </summary>
public sealed class PosterPageViewModel : ObservableObject
{
    /// <summary>Posters measured for their colours at most (their small copies).</summary>
    public const int MeasuredAtMost = 40;

    private readonly AppServices _services;
    private string _query = "";
    private string _searchStatus = "";
    private string _offerStatus = "";
    private string _result = "";
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

    public PosterPageViewModel(AppServices services)
    {
        _services = services;
        _kind = Kinds[0];
        _height = Heights.FirstOrDefault(h => h.Value == services.Settings.PosterHeight) ?? Heights[1];
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => Query.Trim().Length > 0 && !_busy);
        ProcessCommand = new AsyncRelayCommand(ProcessAsync, () => !_busy && Selected is not null && _card?.Details is not null);
        DraftCommand = new AsyncRelayCommand(DrawAsync, () => !_drawing && _card?.Details is not null);
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

    public IReadOnlyList<VideoChoice<MediaKind?>> Kinds { get; } = [new(null, "Filmy aj seriály"), new(MediaKind.Movie, "Filmy"), new(MediaKind.Series, "Seriály")];

    /// <summary>The height of folder.jpg (2:3): 1000 × 1500 by default – sharp on a TV, not too large.</summary>
    public IReadOnlyList<VideoChoice<int>> Heights { get; } =
        [new(1000, "667 × 1000 (menší)"), new(1500, "1000 × 1500 (odporúčané)"), new(2250, "1500 × 2250"), new(3000, "2000 × 3000 (najväčší podľa TMDB)")];

    /// <summary>Why the page cannot work yet; empty when it can.</summary>
    public string Requirement =>
        _services.PosterTmdb is null ? "Na hľadanie plagátov treba kľúč TMDB (Nastavenia → TMDB)."
        : _services.Posters is null ? "Plagáty sú vypnuté v Nastaveniach – zapnite ich."
        : _services.Library is null ? "Nastavte výstupný priečinok (Nastavenia) – plagáty sa ukladajú do jeho priečinka POSTER."
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

    public string SelectedText => Selected is null ? "Vyberte plagát kliknutím." : "Vybraný: " + Selected.Label;

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
            }
        }
    }

    /// <summary>Zlúčiť poster s filmom: into the folder of the film or series processed (now, or once processed) instead of POSTER.</summary>
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

    public AsyncRelayCommand ProcessCommand { get; }

    public RelayCommand OpenPosterFolderCommand { get; }

    public string Result { get => _result; private set => SetProperty(ref _result, value); }

    public AsyncRelayCommand DraftCommand { get; }

    public string DraftStatus { get => _draftStatus; private set => SetProperty(ref _draftStatus, value); }

    /// <summary>Which service draws and what is sent to it.</summary>
    public string DraftHint => _services.DraftKeys.Service is { } service
        ? $"Kreslí {service}{(service == "OpenAI" ? " (platené, ~0,04 $ za návrh)" : " (zadarmo, denný limit)")}. Pošle sa len názov, rok, žánre a dej – nie herci ani iné údaje. Návrh je len pre vašu knižnicu – do TMDB sa nahrávať nesmie."
        : "Na návrh cez AI nastavte v Nastaveniach účet Cloudflare (zadarmo) alebo kľúč OpenAI (platené).";

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
            SearchStatus = "Hľadám na TMDB…";
            var found = await finder.FindAsync(Query, Kind.Value);
            Results.Clear();
            foreach (var title in found)
            {
                Results.Add(new FoundTitleRow(title, new PosterThumb(_services, title.PosterPath, PosterSize.Small), row => _ = OpenAsync(row.Title.Kind, row.Title.Id)));
            }

            SearchStatus = found.Count == 0 ? "TMDB nič také nenašlo – skúste iný názov, rok alebo TMDB či IMDb ID." : $"Nájdené: {found.Count}. Kliknite na titul.";
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
            OfferStatus = "Čítam plagáty z TMDB…";
            var posters = await tmdb.PostersAsync(details.Kind, details.Id, details.OriginalLanguage, cancellationToken);
            var looks = new Dictionary<string, PictureLook>(StringComparer.Ordinal);
            if (_services.Posters is { } cache)
            {
                var measured = posters
                    .Where(p => PosterChoice.Tier(p.Language, details.OriginalLanguage) <= 3 || p.Language == "sk")
                    .OrderBy(p => PosterChoice.Tier(p.Language, details.OriginalLanguage)).ThenByDescending(p => p.VoteAverage)
                    .Take(MeasuredAtMost).ToList();
                OfferStatus = $"Posudzujem farby plagátov ({measured.Count})…";
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
            OfferStatus = posters.Count == 0 ? "TMDB k titulu nemá žiadny plagát – môžete dať navrhnúť plagát cez AI."
                : $"Plagáty na TMDB: {posters.Count} (v ponuke CZ, SK, EN a pôvodný jazyk v latinke).";
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
        var language = p.Language is { Length: > 0 } l ? l.ToUpperInvariant() switch { "CS" => "CZ", var other => other } : "bez textu";
        var votes = p.VoteCount > 0 ? $" · {p.VoteAverage.ToString("0.0", CultureInfo.GetCultureInfo("sk-SK"))}" : "";
        return new PosterOptionRow(candidate, new PosterThumb(_services, p.Path, PosterSize.Small), null, $"{language} · {p.Width} × {p.Height}{votes}",
            $"{language}, {p.Width} × {p.Height} px, hodnotenie {p.VoteAverage.ToString("0.0", CultureInfo.GetCultureInfo("sk-SK"))} ({p.VoteCount} hlasov)" +
            (candidate.Acceptable ? "" : " – menší alebo iných rozmerov, ako TMDB odporúča") + (recommended ? "\nOdporúčaný" : ""),
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
            Result = "Titul nemá názov, podľa ktorého by sa dal pomenovať priečinok.";
            return;
        }

        _busy = true;
        Refresh();
        try
        {
            Result = "Sťahujem plagát v plnej veľkosti…";
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
                Result = "Plagát sa nedá stiahnuť z TMDB – skúste to znova.";
                return;
            }

            var jpeg = PictureTools.ToJpeg(picture, Height.Value, Quality);
            var size = $"{jpeg.Length / 1024} kB";
            var places = new PosterPlaces(library, _services.InputDirectory);
            if (!Merge)
            {
                Result = Done(PosterPlaces.Save(places.PosterFolder(title), jpeg), places.PosterFolder(title), size);
                return;
            }

            if (places.LibraryFolder(title, details.Id) is { } processed)
            {
                Result = Done(PosterPlaces.Save(processed, jpeg), processed, size);
                return;
            }

            Result = "Hľadám film alebo seriál na vstupe…";
            if (await Task.Run(() => places.InInput(details.Kind, details.Names, details.Year)) is { } input)
            {
                _services.WaitingPosters.Add(title, details.Id, jpeg);
                Result = $"{(details.Kind == MediaKind.Movie ? "Film" : "Seriál")} je na vstupe ({Path.GetFileName(input)}), ešte nie je spracovaný – plagát čaká. " +
                         $"Keď ho UNIVERSUM spracuje, dá {PosterPlaces.FileName} do jeho priečinka v {(details.Kind == MediaKind.Movie ? LibraryNames.MoviesFolder : LibraryNames.ShowsFolder)}.";
                return;
            }

            Result = $"{(details.Kind == MediaKind.Movie ? "Film" : "Seriál")} sa nenašiel ani vo výstupe ({LibraryNames.MoviesFolder}, {LibraryNames.ShowsFolder}), ani na vstupe. " +
                     "Nič sa nezapísalo – vypnite Zlúčiť poster s filmom a plagát sa uloží do POSTER.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException)
        {
            Result = "Plagát sa nedá spracovať: " + ex.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private static string Done(PosterPlaces.Saved saved, string folder, string size) =>
        saved.Done
            ? $"Uložené: {Path.Combine(folder, PosterPlaces.FileName)} ({size})." + (saved.KeptAs is { } kept ? $" Pôvodný {PosterPlaces.FileName} tam už bol – premenovaný na {kept}." : "")
            : saved.Problem ?? "Nepodarilo sa.";

    private async Task DrawAsync()
    {
        if (Card?.Details is not { } details)
        {
            return;
        }

        _drawing = true;
        Refresh();
        try
        {
            DraftStatus = "AI kreslí návrh plagátu (môže to trvať do minúty)…";
            var draft = await _services.Drafts.CreateAsync(details, _services.DraftKeys, _title.Token);
            if (draft.Picture is not { } picture)
            {
                DraftStatus = draft.Problem ?? "Návrh sa nepodaril.";
                return;
            }

            var row = new PosterOptionRow(null, null, picture, $"Návrh AI ({draft.Service})", $"Návrh plagátu nakreslený cez {draft.Service} – len pre vašu knižnicu.", r => Selected = r);
            var others = Groups.FirstOrDefault(g => g.Name == "Návrhy AI")?.Posters ?? [];
            var index = Groups.ToList().FindIndex(g => g.Name == "Návrhy AI");
            var group = new PosterGroupRow("Návrhy AI", [row, .. others]);
            if (index >= 0)
            {
                Groups[index] = group;
            }
            else
            {
                Groups.Insert(0, group);
            }

            Selected = row;
            DraftStatus = $"Návrh je hotový ({draft.Service}). Vybraný – spracujete ho ako iný plagát; ďalší návrh vyzerá inak.";
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
    }
}
