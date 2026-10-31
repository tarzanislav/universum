using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.Library;
using Universum.Core.Library.Histra;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.Rules;
using Universum.Core.Weather;

namespace Universum.App.Pages;

/// <summary>Settings: the output folder (library), the rules profile (languages, keywords, size classes) and maintenance.</summary>
public sealed class SettingsPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _preferredLanguages = "";
    private string _subtitleLanguages = "";
    private string _forcedKeywords = "";
    private string _hearingImpairedKeywords = "";
    private string _commentaryKeywords = "";
    private bool _removeTags;
    private bool _repairIndex;
    private int _videoNameLength;
    private string _movieSizes = "";
    private string _seriesSizes = "";
    private string _message = "";
    private string _tmdbKeyInput = "";
    private string _tmdbStatus = "";
    private string _histraTokenInput = "";
    private string _histraStatus = "";
    private string _weatherTownInput = "";
    private string _weatherStatus = "";

    public SettingsPageViewModel(AppServices services)
    {
        _services = services;
        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(() => Load(RuleProfile.Default));
        ClearCacheCommand = new RelayCommand(ClearCache);
        ChooseOutputDirectoryCommand = new RelayCommand(ChooseOutputDirectory);
        SaveTmdbKeyCommand = new AsyncRelayCommand(SaveTmdbKeyAsync, () => TmdbKeyInput.Trim().Length > 0);
        RemoveTmdbKeyCommand = new RelayCommand(RemoveTmdbKey, () => _services.Settings.TmdbKeyProtected is not null);
        SaveHistraTokenCommand = new RelayCommand(SaveHistraToken, () => HistraTokenInput.Trim().Length > 0);
        RemoveHistraTokenCommand = new RelayCommand(RemoveHistraToken, () => _services.Settings.HistraTokenProtected is not null);
        OpenHistraTokenPageCommand = new RelayCommand(() => OpenInBrowser(HistraApi.TokenPage));
        services.TmdbChanged += () => OnPropertyChanged(nameof(SyncStatus));
        services.HistraChanged += () => OnPropertyChanged(nameof(SyncStatus));
        services.LibraryChanged += () => OnPropertyChanged(nameof(OutputDescription));
        FindWeatherTownCommand = new AsyncRelayCommand(FindWeatherTownAsync, () => WeatherTownInput.Trim().Length > 0);
        UseWeatherChoiceCommand = new RelayCommand(UseWeatherChoice, () => SelectedWeatherChoice is not null);
        RemoveWeatherTownCommand = new RelayCommand(RemoveWeatherTown, () => _services.Settings.WeatherPlace is not null);
        _weatherTownInput = services.Settings.WeatherPlace?.Name ?? "";
        NewsSources = new NewsSourcesViewModel(services);
        PosterServices = new PosterServicesViewModel(services);
        InterestSources = new InterestSourcesViewModel(services);
        SubtitleAccounts = new SubtitleAccountsViewModel(services);
        SeriesMemory = new SeriesMemoryViewModel(services);
        Load(services.Profile);
    }

    public RelayCommand ChooseOutputDirectoryCommand { get; }

    /// <summary>The input folder (the same on every page).</summary>
    public InputFolderViewModel InputFolder => _services.InputFolder;

    /// <summary>Zdroje noviniek (Premiéry, Novinky).</summary>
    public NewsSourcesViewModel NewsSources { get; }

    /// <summary>The input folder is watched: new films and episodes are told (owner, 31. 10. 2026).</summary>
    public bool WatchInput
    {
        get => _services.Settings.WatchInput;
        set
        {
            _services.Settings.WatchInput = value;
            _services.Settings.Save();
            _services.RestartWatch();
            OnPropertyChanged();
        }
    }

    /// <summary>The languages of the window to choose from.</summary>
    public IReadOnlyList<LanguageOption> UiLanguages { get; } = [.. Localization.Loc.Languages.Select(l => new LanguageOption(l.Code, l.Name))];

    /// <summary>The language of the window (owner, 31. 10. 2026: Slovak, Czech, English); applied after a restart.</summary>
    public string UiLanguage
    {
        get => _services.Settings.UiLanguage;
        set
        {
            if (value is not ("sk" or "cs" or "en") || value == _services.Settings.UiLanguage)
            {
                return;
            }

            _services.Settings.UiLanguage = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>A sound when all the jobs are done (owner, 29. 10. 2026; also on Fronta úloh).</summary>
    public bool FinishSound
    {
        get => _services.Settings.FinishSound;
        set
        {
            _services.Settings.FinishSound = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>TMDB poster: drawing by AI (Cloudflare, OpenAI) and the ratings of OMDb.</summary>
    public PosterServicesViewModel PosterServices { get; }

    /// <summary>Nastavenia → Zaujímavosti.</summary>
    public InterestSourcesViewModel InterestSources { get; }

    /// <summary>OpenSubtitles.com and svsub.net accounts (Titulky).</summary>
    public SubtitleAccountsViewModel SubtitleAccounts { get; }

    /// <summary>Napovedkyňa seriálov: the series remembered with their titles.</summary>
    public SeriesMemoryViewModel SeriesMemory { get; }

    /// <summary>Asks the main window to open the update window.</summary>
    public event Action? ShowUpdatesRequested;

    public RelayCommand ShowUpdatesCommand => _showUpdates ??= new RelayCommand(() => ShowUpdatesRequested?.Invoke());

    private RelayCommand? _showUpdates;

    /// <summary>Multiplexer: ask what to do when several files are added (unticked = the remembered action is used).</summary>
    public bool MuxAskWhenAdding
    {
        get => _services.Settings.MuxAddAction is null;
        set
        {
            if (value)
            {
                _services.Settings.MuxAddAction = null;
                _services.Settings.MuxNewSettingsForVideo = false;
                _services.Settings.Save();
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(MuxAddNote));
        }
    }

    /// <summary>The remembered action, if any.</summary>
    public string MuxAddNote => _services.Settings.MuxAddAction switch
    {
        null => "Pri pridaní viacerých súborov sa Zlučovanie opýta, čo s nimi.",
        nameof(MuxAddAction.AddToCurrent) => "Zapamätané: pridať do aktuálnych nastavení zlučovania.",
        nameof(MuxAddAction.NewSettingsForAll) => "Zapamätané: nové nastavenia zlučovania pre všetky súbory.",
        _ => "Zapamätané: nové nastavenia zlučovania pre každý súbor.",
    } + (_services.Settings.MuxAddAction is not null && _services.Settings.MuxNewSettingsForVideo ? " Súbory s videom vždy do nových nastavení." : "");

    /// <summary>Check for updates at start and then once a day.</summary>
    public bool AutoCheckUpdates
    {
        get => _services.Settings.AutoCheckUpdates;
        set
        {
            _services.Settings.AutoCheckUpdates = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    public AsyncRelayCommand SaveTmdbKeyCommand { get; }

    public RelayCommand RemoveTmdbKeyCommand { get; }

    /// <summary>A new TMDB key typed by the user (the stored key is never shown).</summary>
    public string TmdbKeyInput
    {
        get => _tmdbKeyInput;
        set
        {
            if (SetProperty(ref _tmdbKeyInput, value))
            {
                SaveTmdbKeyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string TmdbStatus => _tmdbStatus.Length > 0
        ? _tmdbStatus
        : _services.TmdbKey is null ? "Kľúč nie je zadaný – názvy sa zadávajú ručne."
        : TmdbEnabled ? "Kľúč je uložený (chránený pre vaše konto Windows)."
        : "Kľúč je uložený, pripájanie je vypnuté – na TMDB sa nič neposiela.";

    /// <summary>Connect to TMDB; switching it off keeps the key.</summary>
    public bool TmdbEnabled
    {
        get => _services.Settings.TmdbEnabled;
        set
        {
            if (value != _services.Settings.TmdbEnabled)
            {
                _services.SetTmdbEnabled(value);
                _tmdbStatus = "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(TmdbStatus));
            }
        }
    }

    /// <summary>Connect to HISTRA; switching it off keeps the token.</summary>
    public bool HistraEnabled
    {
        get => _services.Settings.HistraEnabled;
        set
        {
            if (value != _services.Settings.HistraEnabled)
            {
                _services.SetHistraEnabled(value);
                _histraStatus = "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(HistraStatus));
            }
        }
    }

    /// <summary>Posters are shown (Listy z HISTRA, Premiéry, Novinky).</summary>
    public bool PostersEnabled
    {
        get => _services.Settings.PostersEnabled;
        set
        {
            if (value != _services.Settings.PostersEnabled)
            {
                _services.SetPosters(value, PosterCache.ParseServer(_services.Settings.PosterServer));
                _posterStatus = value ? "Plagáty sa zobrazujú." : "Plagáty sa nezobrazujú a nič sa kvôli nim nesťahuje.";
                OnPropertyChanged();
                OnPropertyChanged(nameof(PosterStatus));
            }
        }
    }

    /// <summary>The address of the poster server as typed.</summary>
    public string PosterServerInput
    {
        get => _posterServerInput ??= _services.Settings.PosterServer ?? PosterCache.DefaultServer.AbsoluteUri;
        set => SetProperty(ref _posterServerInput, value);
    }

    private string? _posterServerInput;
    private string _posterStatus = "";

    public string PosterStatus => _posterStatus.Length > 0 ? _posterStatus
        : _services.Settings.PosterServer is null ? "Plagáty sú zo servera TMDB (HISTRA uvádza plagáty v tvare TMDB)." : $"Plagáty sú zo servera {_services.Settings.PosterServer}.";

    public RelayCommand SavePosterServerCommand => _savePosterServer ??= new RelayCommand(() =>
    {
        if (PosterCache.ParseServer(PosterServerInput) is not { } server)
        {
            _posterStatus = "Adresa sa neuložila – musí to byť https://… bez mena a hesla, napr. " + PosterCache.DefaultServer.AbsoluteUri;
        }
        else
        {
            _services.SetPosters(PostersEnabled, server);
            PosterServerInput = server.AbsoluteUri;
            _posterStatus = $"Uložené – plagáty budú zo servera {server.Host} (stiahnuté plagáty ostávajú).";
        }

        OnPropertyChanged(nameof(PosterStatus));
    });

    public RelayCommand DefaultPosterServerCommand => _defaultPosterServer ??= new RelayCommand(() =>
    {
        _services.SetPosters(PostersEnabled, null);
        PosterServerInput = PosterCache.DefaultServer.AbsoluteUri;
        _posterStatus = "";
        OnPropertyChanged(nameof(PosterStatus));
    });

    private RelayCommand? _savePosterServer;
    private RelayCommand? _defaultPosterServer;

    /// <summary>What happens with TMDB and HISTRA together.</summary>
    public string SyncStatus =>
        _services.SyncActive ? "TMDB aj HISTRA sú zapnuté – názvy sa hľadajú na TMDB a overujú v katalógu HISTRA (čo TMDB nenájde, hľadá sa v HISTRA); titul, ktorý HISTRA nepotvrdí, čaká na vaše potvrdenie."
        : _services.Tmdb is not null ? "Zapnuté je len TMDB."
        : _services.HistraActive ? "Zapnutá je len HISTRA – názvy sa hľadajú v jej katalógu (podľa názvu a roku; ID a adresy TMDB a epizódy overí len TMDB)."
        : "TMDB ani HISTRA sa nepripájajú – názvy sa zadávajú ručne.";

    public string TmdbAttribution => TmdbClient.Attribution;

    /// <summary>Hľadať: the towns of this name; one is stored at once, several are offered to choose from.</summary>
    public AsyncRelayCommand FindWeatherTownCommand { get; }

    /// <summary>Stores the town chosen from the offered ones.</summary>
    public RelayCommand UseWeatherChoiceCommand { get; }

    public RelayCommand RemoveWeatherTownCommand { get; }

    /// <summary>Towns of the typed name (several places are called Valaská): district, region, country and height tell them apart.</summary>
    public System.Collections.ObjectModel.ObservableCollection<WeatherPlace> WeatherChoices { get; } = [];

    public bool HasWeatherChoices => WeatherChoices.Count > 0;

    public WeatherPlace? SelectedWeatherChoice
    {
        get => _selectedWeatherChoice;
        set
        {
            if (SetProperty(ref _selectedWeatherChoice, value))
            {
                UseWeatherChoiceCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private WeatherPlace? _selectedWeatherChoice;

    /// <summary>Town of the Dashboard weather, as typed.</summary>
    public string WeatherTownInput
    {
        get => _weatherTownInput;
        set
        {
            if (SetProperty(ref _weatherTownInput, value))
            {
                FindWeatherTownCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string WeatherStatus => _weatherStatus.Length > 0
        ? _weatherStatus
        : _services.Settings.WeatherPlace is { } place
            ? string.Create(System.Globalization.CultureInfo.CurrentCulture,
                $"Počasie pre: {place.FullDisplay} (súradnice {place.Latitude:0.00}, {place.Longitude:0.00}).")
            : "Mesto nie je zadané – počasie sa nezisťuje a na Open-Meteo sa nič neposiela.";

    public string WeatherAttribution => OpenMeteo.Attribution;

    /// <summary>Looks the town up on Open-Meteo (only the typed name is sent): one match is stored, several are offered.</summary>
    private async Task FindWeatherTownAsync()
    {
        var town = WeatherTownInput.Trim();
        WeatherChoices.Clear();
        SelectedWeatherChoice = null;
        try
        {
            var found = await _services.Weather.SearchAsync(town);
            if (found.Count == 0)
            {
                _weatherStatus = $"Mesto „{town}“ Open-Meteo nenašlo – skúste iný zápis (napr. bez skratiek).";
            }
            else if (found.Count == 1)
            {
                Use(found[0]);
            }
            else
            {
                foreach (var place in found)
                {
                    WeatherChoices.Add(place);
                }

                SelectedWeatherChoice = WeatherChoices[0];
                _weatherStatus = $"Miest s týmto menom je viac ({found.Count}) – vyberte to správne a kliknite Použiť.";
            }
        }
        catch (WeatherException ex)
        {
            _weatherStatus = "Mesto sa neuložilo: " + ex.Message;
        }

        OnPropertyChanged(nameof(HasWeatherChoices));
        OnPropertyChanged(nameof(WeatherStatus));
        RemoveWeatherTownCommand.NotifyCanExecuteChanged();
    }

    private void UseWeatherChoice()
    {
        if (SelectedWeatherChoice is { } place)
        {
            Use(place);
            WeatherChoices.Clear();
            SelectedWeatherChoice = null;
            OnPropertyChanged(nameof(HasWeatherChoices));
            OnPropertyChanged(nameof(WeatherStatus));
            RemoveWeatherTownCommand.NotifyCanExecuteChanged();
        }
    }

    private void Use(WeatherPlace place)
    {
        _services.SetWeatherPlace(place);
        WeatherTownInput = place.Name;
        _weatherStatus = "";
    }

    private void RemoveWeatherTown()
    {
        _services.SetWeatherPlace(null);
        WeatherTownInput = "";
        WeatherChoices.Clear();
        SelectedWeatherChoice = null;
        OnPropertyChanged(nameof(HasWeatherChoices));
        _weatherStatus = "";
        OnPropertyChanged(nameof(WeatherStatus));
        RemoveWeatherTownCommand.NotifyCanExecuteChanged();
    }


    public RelayCommand SaveHistraTokenCommand { get; }

    public RelayCommand RemoveHistraTokenCommand { get; }

    public RelayCommand OpenHistraTokenPageCommand { get; }

    /// <summary>A new HISTRA token typed by the user (the stored token is never shown).</summary>
    public string HistraTokenInput
    {
        get => _histraTokenInput;
        set
        {
            if (SetProperty(ref _histraTokenInput, value))
            {
                SaveHistraTokenCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string HistraStatus => _histraStatus.Length > 0
        ? _histraStatus
        : _services.HistraToken is null ? "Token nie je zadaný."
        : HistraEnabled ? "Token je uložený (chránený pre vaše konto Windows)."
        : "Token je uložený, pripájanie je vypnuté – na HISTRA sa nič neposiela.";

    public string OutputDescription => _services.Library is { } library
        ? $"{library.Root}\nFilmy: {library.Movies}\nSeriály: {library.Shows}"
        : "Nie je nastavený. Bez neho pracuje len Zlučovanie (výstup vedľa zdroja); ostatné nástroje zapisujú do knižnice.";

    /// <summary>Upper limits of SMALL, BIG and BluRay for films in GB, e.g. "2; 14; 30".</summary>
    public string MovieSizes { get => _movieSizes; set => SetProperty(ref _movieSizes, value); }

    public string SeriesSizes { get => _seriesSizes; set => SetProperty(ref _seriesSizes, value); }

    public RelayCommand SaveCommand { get; }

    public RelayCommand ResetCommand { get; }

    public RelayCommand ClearCacheCommand { get; }

    public string PreferredLanguages { get => _preferredLanguages; set => SetProperty(ref _preferredLanguages, value); }

    /// <summary>Subtitle languages kept, in their order (owner, 29. 10. 2026: cs, sk, en, pl, hu, ru).</summary>
    public string SubtitleLanguages { get => _subtitleLanguages; set => SetProperty(ref _subtitleLanguages, value); }

    public string ForcedKeywords { get => _forcedKeywords; set => SetProperty(ref _forcedKeywords, value); }

    public string HearingImpairedKeywords { get => _hearingImpairedKeywords; set => SetProperty(ref _hearingImpairedKeywords, value); }

    public string CommentaryKeywords { get => _commentaryKeywords; set => SetProperty(ref _commentaryKeywords, value); }

    public bool RemoveTags { get => _removeTags; set => SetProperty(ref _removeTags, value); }

    public bool RepairIndex { get => _repairIndex; set => SetProperty(ref _repairIndex, value); }

    public int VideoNameLength { get => _videoNameLength; set => SetProperty(ref _videoNameLength, value); }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public string Folders => $"Nastavenia a šablóny: {AppSettings.Directory}\nZáznamy pre Vrátiť späť: {AppSettings.JournalDirectory}";

    private void Load(RuleProfile profile)
    {
        PreferredLanguages = string.Join(", ", profile.PreferredLanguages);
        SubtitleLanguages = string.Join(", ", profile.SubtitleLanguages);
        ForcedKeywords = string.Join(", ", profile.ForcedKeywords);
        HearingImpairedKeywords = string.Join(", ", profile.HearingImpairedKeywords);
        CommentaryKeywords = string.Join(", ", profile.CommentaryKeywords);
        RemoveTags = profile.RemoveTags;
        RepairIndex = profile.RepairIndex;
        VideoNameLength = profile.VideoNameLength;
        MovieSizes = Sizes(profile.Library.Movies);
        SeriesSizes = Sizes(profile.Library.Series);
        Message = "";
    }

    private static string Sizes(SizeClasses sizes) =>
        string.Join("; ", new[] { sizes.SmallMaxGb, sizes.BigMaxGb, sizes.BluRayMaxGb }.Select(v => v.ToString(System.Globalization.CultureInfo.CurrentCulture)));

    private static SizeClasses? ParseSizes(string text)
    {
        var values = ListOfNumbers(text).Select(v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var d) ? d : -1).ToList();
        return values.Count == 3 && values[0] > 0 && values[0] < values[1] && values[1] < values[2]
            ? new SizeClasses { SmallMaxGb = values[0], BigMaxGb = values[1], BluRayMaxGb = values[2] }
            : null;
    }

    /// <summary>Checks the key with one request (a well-known film) before storing it.</summary>
    private async Task SaveTmdbKeyAsync()
    {
        var key = TmdbKeyInput.Trim();
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var title = await new TmdbClient(http, key).GetAsync(MediaKind.Movie, 8555);
            _services.SetTmdbKey(key);
            TmdbKeyInput = "";
            _tmdbStatus = title is null ? "Kľúč je uložený." : $"Kľúč funguje a je uložený (skúška: {title.Display}).";
        }
        catch (TmdbException ex)
        {
            _tmdbStatus = "Kľúč sa neuložil: " + ex.Message;
        }

        OnPropertyChanged(nameof(TmdbStatus));
        RemoveTmdbKeyCommand.NotifyCanExecuteChanged();
    }

    private void RemoveTmdbKey()
    {
        _services.SetTmdbKey(null);
        _tmdbStatus = "";
        OnPropertyChanged(nameof(TmdbStatus));
        RemoveTmdbKeyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Stores the token after checking that it can go into a Bearer header. It is not tried on HISTRA yet: the requests
    /// UNIVERSUM will make are added once the HISTRA API is described.
    /// </summary>
    private void SaveHistraToken()
    {
        var token = HistraTokenInput.Trim();
        if (!HistraApi.IsValidToken(token))
        {
            _histraStatus = "Token sa neuložil: nevyzerá ako token HISTRA (len písmená, číslice a - . _ ~ + / =, bez medzier, aspoň 8 znakov).";
        }
        else
        {
            _services.SetHistraToken(token);
            HistraTokenInput = "";
            _histraStatus = "Token je uložený (chránený pre vaše konto Windows).";
        }

        OnPropertyChanged(nameof(HistraStatus));
        RemoveHistraTokenCommand.NotifyCanExecuteChanged();
    }

    private void RemoveHistraToken()
    {
        _services.SetHistraToken(null);
        _histraStatus = "";
        OnPropertyChanged(nameof(HistraStatus));
        RemoveHistraTokenCommand.NotifyCanExecuteChanged();
    }

    private static void OpenInBrowser(Uri page)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // no browser registered: the address is in the tooltip
        }
    }

    private void ChooseOutputDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Výstupný priečinok (knižnica s MOVIES a TVSHOWS)" };
        if (dialog.ShowDialog() == true)
        {
            _services.SetOutputDirectory(dialog.FolderName);
        }
    }

    private void Save()
    {
        var languages = List(PreferredLanguages).Select(code => Languages.Find(code)?.Ietf ?? code.ToLowerInvariant()).Distinct().ToList();
        if (languages.Count == 0)
        {
            Message = "Zadajte aspoň jeden preferovaný jazyk (napr. cs, sk).";
            return;
        }

        if (VideoNameLength is < 4 or > 32)
        {
            Message = "Dĺžka názvu videa musí byť 4 až 32 znakov.";
            return;
        }

        if (ParseSizes(MovieSizes) is not { } movieSizes || ParseSizes(SeriesSizes) is not { } seriesSizes)
        {
            Message = "Hranice veľkosti zadajte ako tri rastúce čísla v GB oddelené bodkočiarkou, napr. 2; 14; 30.";
            return;
        }

        var profile = RuleProfile.Default with
        {
            Library = RuleProfile.Default.Library with { Movies = movieSizes, Series = seriesSizes },
            Name = "Vlastný profil",
            PreferredLanguages = languages,
            SubtitleLanguages = List(SubtitleLanguages).Select(code => Languages.Find(code)?.Ietf ?? code.ToLowerInvariant()).Distinct().ToList(),
            ForcedKeywords = List(ForcedKeywords),
            HearingImpairedKeywords = List(HearingImpairedKeywords),
            CommentaryKeywords = List(CommentaryKeywords),
            RemoveTags = RemoveTags,
            RepairIndex = RepairIndex,
            VideoNameLength = VideoNameLength,
        };
        _services.Settings.Profile = IsDefault(profile) ? null : profile;
        _services.Settings.Save();
        Load(_services.Profile);
        Message = "Uložené. Platí pre Pravidlá aj Hromadné spracovanie (pri ďalšom otvorení súboru alebo analýze).";
    }

    private void ClearCache()
    {
        try
        {
            File.Delete(AppSettings.IdentificationCacheFile);
            Message = "Vyrovnávacia pamäť analýzy je vymazaná.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = ex.Message;
        }
    }

    private static bool IsDefault(RuleProfile profile)
    {
        var d = RuleProfile.Default;
        return profile.PreferredLanguages.SequenceEqual(d.PreferredLanguages) && profile.SubtitleLanguages.SequenceEqual(d.SubtitleLanguages) &&
               profile.ForcedKeywords.SequenceEqual(d.ForcedKeywords) &&
               profile.HearingImpairedKeywords.SequenceEqual(d.HearingImpairedKeywords) && profile.CommentaryKeywords.SequenceEqual(d.CommentaryKeywords) &&
               profile.RemoveTags == d.RemoveTags && profile.RepairIndex == d.RepairIndex && profile.VideoNameLength == d.VideoNameLength &&
               profile.Library.Movies == d.Library.Movies && profile.Library.Series == d.Library.Series;
    }

    private static List<string> List(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<string> ListOfNumbers(string text) =>
        text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>A language of the window in the list: its code and its name in that language.</summary>
public sealed record LanguageOption(string Code, string Name);
