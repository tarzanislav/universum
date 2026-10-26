using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.Library;
using Universum.Core.Library.Histra;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>Settings: the output folder (library), the rules profile (languages, keywords, size classes) and maintenance.</summary>
public sealed class SettingsPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _preferredLanguages = "";
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
        services.LibraryChanged += () => OnPropertyChanged(nameof(OutputDescription));
        Load(services.Profile);
    }

    public RelayCommand ChooseOutputDirectoryCommand { get; }

    /// <summary>Asks the main window to open the update window.</summary>
    public event Action? ShowUpdatesRequested;

    public RelayCommand ShowUpdatesCommand => _showUpdates ??= new RelayCommand(() => ShowUpdatesRequested?.Invoke());

    private RelayCommand? _showUpdates;

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
        : _services.TmdbKey is null ? "Kľúč nie je zadaný – názvy sa zadávajú ručne." : "Kľúč je uložený (chránený pre vaše konto Windows).";

    public string TmdbAttribution => TmdbClient.Attribution;

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
        : _services.HistraToken is null ? "Token nie je zadaný." : "Token je uložený (chránený pre vaše konto Windows).";

    public string OutputDescription => _services.Library is { } library
        ? $"{library.Root}\nFilmy: {library.Movies}\nSeriály: {library.Shows}"
        : "Nie je nastavený. Bez neho UNIVERSUM nič nespracuje.";

    /// <summary>Upper limits of SMALL, BIG and BluRay for films in GB, e.g. "2; 14; 30".</summary>
    public string MovieSizes { get => _movieSizes; set => SetProperty(ref _movieSizes, value); }

    public string SeriesSizes { get => _seriesSizes; set => SetProperty(ref _seriesSizes, value); }

    public RelayCommand SaveCommand { get; }

    public RelayCommand ResetCommand { get; }

    public RelayCommand ClearCacheCommand { get; }

    public string PreferredLanguages { get => _preferredLanguages; set => SetProperty(ref _preferredLanguages, value); }

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
        return profile.PreferredLanguages.SequenceEqual(d.PreferredLanguages) && profile.ForcedKeywords.SequenceEqual(d.ForcedKeywords) &&
               profile.HearingImpairedKeywords.SequenceEqual(d.HearingImpairedKeywords) && profile.CommentaryKeywords.SequenceEqual(d.CommentaryKeywords) &&
               profile.RemoveTags == d.RemoveTags && profile.RepairIndex == d.RepairIndex && profile.VideoNameLength == d.VideoNameLength &&
               profile.Library.Movies == d.Library.Movies && profile.Library.Series == d.Library.Series;
    }

    private static List<string> List(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<string> ListOfNumbers(string text) =>
        text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
