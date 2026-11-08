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
using Universum.App.Localization;

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
        ChoosePreviewFolderCommand = new RelayCommand(ChoosePreviewFolder);
        OpenPreviewFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(_services.PreviewFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_services.PreviewFolder) { UseShellExecute = true })?.Dispose();
        });
        ClearPreviewsCommand = new RelayCommand(() =>
        {
            var cleared = _services.ClearOldPreviews(TimeSpan.Zero);
            PreviewFolderNote = Loc.F("Vymazané náhľady: {0}. ", cleared) + PreviewNote();
        });
        SaveTmdbKeyCommand = new AsyncRelayCommand(SaveTmdbKeyAsync, () => TmdbKeyInput.Trim().Length > 0);
        RemoveTmdbKeyCommand = new RelayCommand(RemoveTmdbKey, () => _services.Settings.TmdbKeyProtected is not null);
        SaveHistraTokenCommand = new RelayCommand(SaveHistraToken, () => HistraTokenInput.Trim().Length > 0);
        RemoveHistraTokenCommand = new RelayCommand(RemoveHistraToken, () => _services.Settings.HistraTokenProtected is not null);
        OpenHistraTokenPageCommand = new RelayCommand(() => OpenInBrowser(HistraApi.TokenPage));
        services.TmdbChanged += () => OnPropertyChanged(nameof(SyncStatus));
        services.KeepMp4Changed += () => OnPropertyChanged(nameof(KeepMp4));
        InstallTrayScriptCommand = new RelayCommand(() =>
        {
            if (!_services.TmdbTray.Receiving)
            {
                TrayMessage = Loc.T("Najprv zapnite Prijímať zo skriptu v prehliadači – skript sa inštaluje z UNIVERSUM.");
                return;
            }

            OpenInBrowser(new Uri(_services.TmdbTray.ScriptAddress));
            TrayMessage = Loc.T("V prehliadači sa otvoril skript – potvrďte inštaláciu v Tampermonkey alebo Violentmonkey. Ak sa ukáže len text, rozšírenie nie je nainštalované.");
        });
        CopyTrayScriptCommand = new RelayCommand(() =>
        {
            System.Windows.Clipboard.SetText(_services.TmdbTray.Script);
            TrayMessage = Loc.T("Skript je v schránke – v rozšírení zvoľte Nový skript (Vytvoriť nový skript), nahraďte text a uložte.");
        });
        CopyBookmarkletCommand = new RelayCommand(() =>
        {
            System.Windows.Clipboard.SetText(Universum.Core.Library.Tmdb.TmdbUserScript.Bookmarklet);
            TrayMessage = Loc.T("Záložka je v schránke – pravým klikom na lištu záložiek → Pridať stránku, názov Do UNIVERSUM, adresu vložte.");
        });
        NewTrayKeyCommand = new RelayCommand(() =>
        {
            _services.TmdbTray.NewKey();
            OnPropertyChanged(nameof(TrayScript));
            OnPropertyChanged(nameof(TmdbTrayStatus));
            TrayMessage = Loc.T("Nový kľúč je nastavený – skript nainštalujte znova (Nainštalovať skript).");
        });
        OpenMp4RecordsCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(AppSettings.Mp4RecordDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppSettings.Mp4RecordDirectory) { UseShellExecute = true })?.Dispose();
        });
        services.HistraChanged += () => OnPropertyChanged(nameof(SyncStatus));
        services.LibraryChanged += () =>
        {
            OnPropertyChanged(nameof(OutputDescription));
            OnPropertyChanged(nameof(OutputWarning));
            OnPropertyChanged(nameof(HasOutputWarning));
        };
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

    /// <summary>Priečinky na diskoch – every disk with its input and output folder (owner, 8. 11. 2026).</summary>
    public DiskFolders Disks => _disks ??= new DiskFolders(_services, this);

    private DiskFolders? _disks;

    /// <summary>Zdroje noviniek (Premiéry, Novinky).</summary>
    public NewsSourcesViewModel NewsSources { get; }

    /// <summary>The input folder is watched: new films and episodes are told (owner, 31. 10. 2026).</summary>
    // ---------------------------------------------------------------- previews of Zlučovanie (owner, 1. 11. 2026)

    /// <summary>Where Zlučovanie writes its previews (never an output folder).</summary>
    public string PreviewFolder => _services.PreviewFolder;

    private string? _previewNote;

    public string PreviewFolderNote { get => _previewNote ??= PreviewNote(); private set => SetProperty(ref _previewNote, value); }

    public RelayCommand ChoosePreviewFolderCommand { get; }

    public RelayCommand OpenPreviewFolderCommand { get; }

    public RelayCommand ClearPreviewsCommand { get; }

    // the size of the previews there and the free space of the disk
    private string PreviewNote()
    {
        var text = Loc.T("Zlučovanie sem zapisuje náhľady výsledkov (nikdy nie do výstupných priečinkov); náhľad sa vymaže so svojou kartou alebo po týždni.");
        try
        {
            var folder = _services.PreviewFolder;
            var size = Directory.Exists(folder) ? Universum.Core.Muxing.MuxPreview.Old(folder, TimeSpan.Zero, DateTime.Now.AddSeconds(1)).Sum(f => new FileInfo(f).Length) : 0;
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            var free = root is { Length: > 0 } ? new DriveInfo(root).AvailableFreeSpace : -1;
            return text + Loc.F(" Náhľady teraz: {0:0.#} MB", size / (1024 * 1024.0)) + (free >= 0 ? Loc.F(" · voľné miesto na disku: {0:0.#} GB.", free / (1024 * 1024 * 1024.0)) : ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return text;
        }
    }

    private void ChoosePreviewFolder()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Priečinok náhľadov Zlučovania (nie výstupný ani vstupný priečinok)") };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var chosen = Path.GetFullPath(dialog.FolderName);
        static bool Inside(string folder, string? root) =>
            root is { Length: > 0 } && (SameFolder(folder, root) || folder.StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        static bool SameFolder(string a, string b) => string.Equals(a.TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        if (_services.OutputDirectories.Concat(_services.InputDirectories).Any(root => Inside(chosen, root)))
        {
            PreviewFolderNote = Loc.T("Náhľady nesmú byť vo výstupnom priečinku (knižnici) ani vo vstupnom priečinku – zvoľte iný. ") + PreviewNote();
            return;
        }

        _services.Settings.MuxPreviewFolder = chosen;
        _services.Settings.Save();
        OnPropertyChanged(nameof(PreviewFolder));
        PreviewFolderNote = PreviewNote();
    }

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
        null => Loc.T("Pri pridaní viacerých súborov sa Zlučovanie opýta, čo s nimi."),
        nameof(MuxAddAction.AddToCurrent) => Loc.T("Zapamätané: pridať do aktuálnych nastavení zlučovania."),
        nameof(MuxAddAction.NewSettingsForAll) => Loc.T("Zapamätané: nové nastavenia zlučovania pre všetky súbory."),
        _ => Loc.T("Zapamätané: nové nastavenia zlučovania pre každý súbor."),
    } + (_services.Settings.MuxAddAction is not null && _services.Settings.MuxNewSettingsForVideo ? Loc.T(" Súbory s videom vždy do nových nastavení.") : "");

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
        : _services.TmdbKey is null ? Loc.T("Kľúč nie je zadaný – názvy sa zadávajú ručne.")
        : TmdbEnabled ? Loc.T("Kľúč je uložený (chránený pre vaše konto Windows).")
        : Loc.T("Kľúč je uložený, pripájanie je vypnuté – na TMDB sa nič neposiela.");

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
                _posterStatus = value ? Loc.T("Plagáty sa zobrazujú.") : Loc.T("Plagáty sa nezobrazujú a nič sa kvôli nim nesťahuje.");
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
        : _services.Settings.PosterServer is null ? Loc.T("Plagáty sú zo servera TMDB (HISTRA uvádza plagáty v tvare TMDB).") : Loc.F("Plagáty sú zo servera {0}.", _services.Settings.PosterServer);

    public RelayCommand SavePosterServerCommand => _savePosterServer ??= new RelayCommand(() =>
    {
        if (PosterCache.ParseServer(PosterServerInput) is not { } server)
        {
            _posterStatus = Loc.T("Adresa sa neuložila – musí to byť https://… bez mena a hesla, napr. ") + PosterCache.DefaultServer.AbsoluteUri;
        }
        else
        {
            _services.SetPosters(PostersEnabled, server);
            PosterServerInput = server.AbsoluteUri;
            _posterStatus = Loc.F("Uložené – plagáty budú zo servera {0} (stiahnuté plagáty ostávajú).", server.Host);
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
        _services.SyncActive ? Loc.T("TMDB aj HISTRA sú zapnuté – názvy sa hľadajú na TMDB a overujú v katalógu HISTRA (čo TMDB nenájde, hľadá sa v HISTRA); titul, ktorý HISTRA nepotvrdí, čaká na vaše potvrdenie.")
        : _services.Tmdb is not null ? Loc.T("Zapnuté je len TMDB.")
        : _services.HistraActive ? Loc.T("Zapnutá je len HISTRA – názvy sa hľadajú v jej katalógu (podľa názvu a roku; ID a adresy TMDB a epizódy overí len TMDB).")
        : Loc.T("TMDB ani HISTRA sa nepripájajú – názvy sa zadávajú ručne.");

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
            ? Loc.F("Počasie pre: {0} (súradnice {1:0.00}, {2:0.00}).", place.FullDisplay, place.Latitude, place.Longitude)
            : Loc.T("Mesto nie je zadané – počasie sa nezisťuje a na Open-Meteo sa nič neposiela.");

    public string WeatherAttribution => Loc.T(OpenMeteo.Attribution);

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
                _weatherStatus = Loc.F("Mesto „{0}“ Open-Meteo nenašlo – skúste iný zápis (napr. bez skratiek).", town);
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
                _weatherStatus = Loc.F("Miest s týmto menom je viac ({0}) – vyberte to správne a kliknite Použiť.", found.Count);
            }
        }
        catch (WeatherException ex)
        {
            _weatherStatus = Loc.T("Mesto sa neuložilo: ") + ex.Message;
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
        : _services.HistraToken is null ? Loc.T("Token nie je zadaný.")
        : HistraEnabled ? Loc.T("Token je uložený (chránený pre vaše konto Windows).")
        : Loc.T("Token je uložený, pripájanie je vypnuté – na HISTRA sa nič neposiela.");

    public string OutputDescription => _services.Library is { } library
        ? Loc.F("{0}\nFilmy: {1}\nSeriály: {2}", library.Root, library.Movies, library.Shows)
        : Loc.T("Nie je nastavený. Bez neho pracuje len Zlučovanie (výstup vedľa zdroja); ostatné nástroje zapisujú do knižnice.");

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

    /// <summary>
    /// Ponechať MP4 (owner, 7. 11. 2026): saved at once – the same switch as in Hromadné and Single spracovanie (not a part
    /// of the profile saved by Uložiť).
    /// </summary>
    public bool KeepMp4 { get => _services.KeepMp4; set => _services.KeepMp4 = value; }

    /// <summary>Zásobník TMDB: UNIVERSUM's script in the browser may send films and series (saved at once, the receiver follows).</summary>
    public bool TmdbTrayReceive
    {
        get => _services.Settings.TmdbTrayReceive;
        set
        {
            if (value != _services.Settings.TmdbTrayReceive)
            {
                _services.Settings.TmdbTrayReceive = value;
                _services.Settings.Save();
                _services.TmdbTray.StartReceiving();
                OnPropertyChanged();
                OnPropertyChanged(nameof(TmdbTrayStatus));
            }
        }
    }

    public string TmdbTrayStatus => _services.TmdbTray.Status;

    /// <summary>The script as it is installed (to read it before installing).</summary>
    public string TrayScript => _services.TmdbTray.Script;

    public string TrayMessage { get => _trayMessage; private set => SetProperty(ref _trayMessage, value); }

    private string _trayMessage = "";

    public RelayCommand InstallTrayScriptCommand { get; }

    public RelayCommand CopyTrayScriptCommand { get; }

    public RelayCommand CopyBookmarkletCommand { get; }

    public RelayCommand NewTrayKeyCommand { get; }

    /// <summary>Opens the folder of the záznamy MP4 (what went wrong with an MP4, without names – for the developer).</summary>
    public RelayCommand OpenMp4RecordsCommand { get; }

    public int VideoNameLength { get => _videoNameLength; set => SetProperty(ref _videoNameLength, value); }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public string Folders => Loc.F("Nastavenia a šablóny: {0}\nZáznamy pre Vrátiť späť: {1}", AppSettings.Directory, AppSettings.JournalDirectory);

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
            _tmdbStatus = title is null ? Loc.T("Kľúč je uložený.") : Loc.F("Kľúč funguje a je uložený (skúška: {0}).", title.Display);
        }
        catch (TmdbException ex)
        {
            _tmdbStatus = Loc.T("Kľúč sa neuložil: ") + ex.Message;
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
            _histraStatus = Loc.T("Token sa neuložil: nevyzerá ako token HISTRA (len písmená, číslice a - . _ ~ + / =, bez medzier, aspoň 8 znakov).");
        }
        else
        {
            _services.SetHistraToken(token);
            HistraTokenInput = "";
            _histraStatus = Loc.T("Token je uložený (chránený pre vaše konto Windows).");
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

    // ---------------------------------------------------------------- output folders on other disks (owner, 2. 11. 2026)

    /// <summary>The output folders on other disks, each with what it holds and its disk.</summary>
    public IReadOnlyList<OtherOutputRow> OtherOutputs => [.. _services.Settings.OtherOutputDirectories.Select(f => new OtherOutputRow(f, this))];

    /// <summary>A file goes into the output folder on its own disk (off: every file into the default one).</summary>
    public bool OutputBySourceDisk
    {
        get => _services.Settings.OutputBySourceDisk;
        set
        {
            if (_services.Settings.OutputBySourceDisk != value)
            {
                _services.Settings.OutputBySourceDisk = value;
                _services.Settings.Save();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The choices of Na koľkých diskoch naraz.</summary>
    public IReadOnlyList<int> ParallelDiskChoices { get; } = [1, 2, 3, 4];

    /// <summary>On how many disks files are written at the same time (one file per disk).</summary>
    public int ParallelDisks
    {
        get => Math.Clamp(_services.Settings.ParallelDisks, 1, 4);
        set
        {
            if (_services.Settings.ParallelDisks != value)
            {
                _services.Settings.ParallelDisks = Math.Clamp(value, 1, 4);
                _services.Settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public RelayCommand AddOtherOutputCommand => _addOtherOutput ??= new RelayCommand(AddOtherOutput, () => _services.Library is not null);

    private RelayCommand? _addOtherOutput;

    private string _outputMessage = "";

    /// <summary>What happened to the output folders (shown in their card).</summary>
    public string OutputMessage { get => _outputMessage; private set => SetProperty(ref _outputMessage, value); }

    private void AddOtherOutput()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Výstupný priečinok na inom disku (vlastné MOVIES a TVSHOWS)") };
        if (dialog.ShowDialog() == true)
        {
            AddOtherOutput(dialog.FolderName);
        }
    }

    /// <summary>
    /// Asks the owner (Áno / Nie / Zrušiť – true / false / null); the self-test answers by itself. Owner, 8. 11. 2026: the
    /// default output was replaced without a question.
    /// </summary>
    public Func<string, bool, bool?> Ask { get; set; } = (question, three) =>
        System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow!, question, "UNIVERSUM",
            three ? System.Windows.MessageBoxButton.YesNoCancel : System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) switch
        {
            System.Windows.MessageBoxResult.Yes => true,
            System.Windows.MessageBoxResult.No => false,
            _ => null,
        };

    // the checks every output folder passes: not the input folder, not a folder of films (asked)
    private bool Acceptable(string chosen)
    {
        if (Universum.Core.Library.OutputFolders.Problem(chosen, _services.InputDirectories) is { } problem)
        {
            OutputMessage = problem;
            return false;
        }

        if (Universum.Core.Library.OutputFolders.LooksLikeSource(chosen) &&
            Ask(Loc.F("V priečinku {0} sú filmy alebo seriály mimo MOVIES a TVSHOWS – vyzerá ako zdrojový priečinok, nie ako výstup.\n\nNaozaj ho použiť ako výstupný priečinok?", chosen), false) != true)
        {
            OutputMessage = Loc.T("Výstupný priečinok sa nezmenil.");
            return false;
        }

        return true;
    }

    /// <summary>An output folder on another disk added (also the self-test).</summary>
    public void AddOtherOutput(string folder)
    {
        var chosen = Path.GetFullPath(folder);
        if (_services.Library is { } main && Universum.Core.IO.Paths.SameVolume(chosen, main.Root))
        {
            OutputMessage = Loc.T("Tento priečinok je na tom istom disku ako predvolený výstupný priečinok – na jeden disk stačí jeden. Zvoľte priečinok na inom disku.");
            return;
        }

        if (_services.Settings.OtherOutputDirectories.FirstOrDefault(f => Universum.Core.IO.Paths.SameVolume(f, chosen)) is { } taken)
        {
            OutputMessage = Loc.F("Na tomto disku už výstupný priečinok je: {0}", taken);
            return;
        }

        if (!Acceptable(chosen))
        {
            return;
        }

        _services.AddOutputDirectory(chosen);
        OutputMessage = Loc.F("Pridaný výstupný priečinok: {0} – súbory z tohto disku pôjdu sem.", chosen);
        OnPropertyChanged(nameof(OtherOutputs));
        OnPropertyChanged(nameof(OutputWarning));
        OnPropertyChanged(nameof(HasOutputWarning));
    }

    /// <summary>
    /// The default output folder chosen (also the self-test; owner, 8. 11. 2026): a folder on another disk than the default
    /// one is offered as a further output (the default one stays); replacing the default one is asked first. Nothing in the
    /// old folder changes.
    /// </summary>
    public void ChooseOutput(string folder)
    {
        var chosen = Path.GetFullPath(folder);
        if (_services.Library is { } current && Universum.Core.IO.Paths.Same(current.Root, chosen))
        {
            return;
        }

        if (!Acceptable(chosen))
        {
            return;
        }

        if (_services.Library is { } main)
        {
            if (!Universum.Core.IO.Paths.SameVolume(main.Root, chosen))
            {
                var answer = Ask(Loc.F("{0} je na inom disku ako predvolený výstupný priečinok {1}.\n\nÁno – pridať ho ako výstup na ďalšom disku (odporúčané; {1} ostane predvolený).\nNie – nahradiť ním predvolený výstupný priečinok.\nZrušiť – nič nemeniť.", chosen, main.Root), true);
                if (answer is null)
                {
                    OutputMessage = Loc.T("Výstupný priečinok sa nezmenil.");
                    return;
                }

                if (answer == true)
                {
                    AddOtherOutput(chosen);
                    return;
                }
            }
            else if (Ask(Loc.F("Nahradiť predvolený výstupný priečinok {0} priečinkom {1}?\n\nV {0} sa nič nezmení ani nezmaže – UNIVERSUM ho len prestane používať.", main.Root, chosen), false) != true)
            {
                OutputMessage = Loc.T("Výstupný priečinok sa nezmenil.");
                return;
            }
        }

        var before = _services.Library?.Root;

        // a further output on the disk of the new default one would share its disk: it is no longer used
        var dropped = _services.Settings.OtherOutputDirectories.Where(f => Universum.Core.IO.Paths.SameVolume(f, chosen)).ToList();
        foreach (var other in dropped)
        {
            _services.RemoveOutputDirectory(other);
        }

        _services.SetOutputDirectory(chosen);
        OutputMessage = (before is null ? Loc.F("Výstupný priečinok: {0}", chosen) : Loc.F("Predvolený výstupný priečinok je teraz {0} (predtým {1} – v ňom sa nič nezmenilo).", chosen, before)) +
                  (dropped.Count > 0 ? " " + Loc.F("Výstup na tom istom disku sa už nepoužíva: {0}.", string.Join(", ", dropped)) : "");
        OnPropertyChanged(nameof(OtherOutputs));
        OnPropertyChanged(nameof(OutputWarning));
        OnPropertyChanged(nameof(HasOutputWarning));
    }

    /// <summary>Output folders sharing a disk (saved before the checks existed): only one per disk is used.</summary>
    public string OutputWarning => Universum.Core.Library.OutputFolders.SharingADisk(_services.Settings.OutputDirectory, _services.Settings.OtherOutputDirectories) is { Count: > 0 } sharing
        ? Loc.F("Na jednom disku je viac výstupných priečinkov ({0}) – súbory z neho pôjdu len do jedného. Odoberte ten, ktorý je navyše.", string.Join(", ", sharing))
        : "";

    public bool HasOutputWarning => OutputWarning.Length > 0;

    internal void RemoveOtherOutput(string folder)
    {
        _services.RemoveOutputDirectory(folder);
        OutputMessage = Loc.F("Výstupný priečinok {0} sa už nepoužíva (nič v ňom sa nezmenilo).", folder);
        OnPropertyChanged(nameof(OtherOutputs));
        OnPropertyChanged(nameof(OutputWarning));
        OnPropertyChanged(nameof(HasOutputWarning));
    }

    private void ChooseOutputDirectory()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Výstupný priečinok (knižnica s MOVIES a TVSHOWS)") };
        if (dialog.ShowDialog() == true)
        {
            ChooseOutput(dialog.FolderName);
        }
    }

    private void Save()
    {
        var languages = List(PreferredLanguages).Select(code => Languages.Find(code)?.Ietf ?? code.ToLowerInvariant()).Distinct().ToList();
        if (languages.Count == 0)
        {
            Message = Loc.T("Zadajte aspoň jeden preferovaný jazyk (napr. cs, sk).");
            return;
        }

        if (VideoNameLength is < 4 or > 32)
        {
            Message = Loc.T("Dĺžka názvu videa musí byť 4 až 32 znakov.");
            return;
        }

        if (ParseSizes(MovieSizes) is not { } movieSizes || ParseSizes(SeriesSizes) is not { } seriesSizes)
        {
            Message = Loc.T("Hranice veľkosti zadajte ako tri rastúce čísla v GB oddelené bodkočiarkou, napr. 2; 14; 30.");
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
        Message = Loc.T("Uložené. Platí pre Pravidlá aj Hromadné spracovanie (pri ďalšom otvorení súboru alebo analýze).");
    }

    private void ClearCache()
    {
        try
        {
            File.Delete(AppSettings.IdentificationCacheFile);
            Message = Loc.T("Vyrovnávacia pamäť analýzy je vymazaná.");
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

/// <summary>An output folder on another disk, in Nastavenia (owner, 2. 11. 2026).</summary>
public sealed class OtherOutputRow(string folder, SettingsPageViewModel page)
{
    public string Folder { get; } = folder;

    /// <summary>"disk E:\ · voľné 1 234 GB".</summary>
    public string Detail
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(Folder)) ?? "";
                var drive = new DriveInfo(root);
                return drive.IsReady
                    ? Loc.F("disk {0} · voľné {1:N0} GB", root, drive.AvailableFreeSpace / (1024.0 * 1024 * 1024))
                    : Loc.F("disk {0} · nie je pripojený", root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Loc.T("disk nie je dostupný");
            }
        }
    }

    public CommunityToolkit.Mvvm.Input.RelayCommand RemoveCommand => new(() => page.RemoveOtherOutput(Folder));
}

