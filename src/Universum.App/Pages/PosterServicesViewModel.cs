using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Library;
using Universum.Core.Posters;

namespace Universum.App.Pages;

/// <summary>
/// Nastavenia → TMDB poster (owner, 31. 10. 2026): the services UNIVERSUM may use for posters – Cloudflare Workers AI
/// (free, the user's own account) and OpenAI (paid, the user's own key) to draw a poster when TMDB has none, OMDb (free
/// key) for the ratings of IMDb, Rotten Tomatoes and Metacritic. Keys are kept protected for this Windows account and
/// never shown again.
/// </summary>
public sealed class PosterServicesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _account = "";
    private string _token = "";
    private string _openAi = "";
    private string _omdb = "";
    private string _status = "";

    public PosterServicesViewModel(AppServices services)
    {
        _services = services;
        _account = services.Settings.CloudflareAccount ?? "";
        SaveCloudflareCommand = new RelayCommand(SaveCloudflare, () => DraftKeys.IsCloudflareAccount(Account) && DraftKeys.IsCloudflareToken(Token));
        RemoveCloudflareCommand = new RelayCommand(() =>
        {
            _services.SetCloudflare(null, null);
            Account = "";
            Done(Loc.T("Účet Cloudflare odstránený."));
        }, () => _services.Settings.CloudflareTokenProtected is not null);
        SaveOpenAiCommand = new RelayCommand(() =>
        {
            _services.SetOpenAiKey(OpenAi);
            OpenAi = "";
            Done(Loc.T("Kľúč OpenAI uložený – návrhy kreslí OpenAI (platené)."));
        }, () => DraftKeys.IsOpenAiKey(OpenAi));
        RemoveOpenAiCommand = new RelayCommand(() =>
        {
            _services.SetOpenAiKey(null);
            Done(Loc.T("Kľúč OpenAI odstránený."));
        }, () => _services.Settings.OpenAiKeyProtected is not null);
        SaveOmdbCommand = new RelayCommand(() =>
        {
            _services.SetOmdbKey(Omdb);
            Omdb = "";
            Done(Loc.T("Kľúč OMDb uložený – karta titulu ukáže aj IMDb, Rotten Tomatoes a Metacritic."));
        }, () => OmdbClient.IsValidKey(Omdb));
        RemoveOmdbCommand = new RelayCommand(() =>
        {
            _services.SetOmdbKey(null);
            Done(Loc.T("Kľúč OMDb odstránený."));
        }, () => _services.Settings.OmdbKeyProtected is not null);
    }

    /// <summary>The Cloudflare account ID (not secret; shown).</summary>
    public string Account { get => _account; set { if (SetProperty(ref _account, value)) { Refresh(); } } }

    /// <summary>A new Cloudflare API token (the stored one is never shown).</summary>
    public string Token { get => _token; set { if (SetProperty(ref _token, value)) { Refresh(); } } }

    public string OpenAi { get => _openAi; set { if (SetProperty(ref _openAi, value)) { Refresh(); } } }

    public string Omdb { get => _omdb; set { if (SetProperty(ref _omdb, value)) { Refresh(); } } }

    public RelayCommand SaveCloudflareCommand { get; }

    public RelayCommand RemoveCloudflareCommand { get; }

    public RelayCommand SaveOpenAiCommand { get; }

    public RelayCommand RemoveOpenAiCommand { get; }

    public RelayCommand SaveOmdbCommand { get; }

    public RelayCommand RemoveOmdbCommand { get; }

    /// <summary>What is set, and who draws.</summary>
    public string Status => _status.Length > 0 ? _status + " " + Summary : Summary;

    private string Summary =>
        string.Join(" ", new[]
        {
            _services.DraftKeys.Service switch
            {
                "OpenAI" => Loc.T("Návrhy plagátov kreslí OpenAI (platené)."),
                "Cloudflare" => Loc.T("Návrhy plagátov kreslí Cloudflare (zadarmo)."),
                _ => Loc.T("Návrhy plagátov cez AI nie sú nastavené."),
            },
            _services.OmdbKey is null ? Loc.T("OMDb nie je nastavené – hodnotenie len z TMDB (a HISTRA).") : Loc.T("OMDb je nastavené."),
        });

    private void SaveCloudflare()
    {
        _services.SetCloudflare(Account, Token);
        Token = "";
        Done(_services.Settings.OpenAiKeyProtected is null ? Loc.T("Účet Cloudflare uložený – návrhy kreslí Cloudflare (zadarmo).") : Loc.T("Účet Cloudflare uložený (kreslí OpenAI, kým je nastavený jeho kľúč)."));
    }

    private void Done(string message)
    {
        _status = message;
        OnPropertyChanged(nameof(Status));
        Refresh();
    }

    private void Refresh()
    {
        SaveCloudflareCommand?.NotifyCanExecuteChanged();
        RemoveCloudflareCommand?.NotifyCanExecuteChanged();
        SaveOpenAiCommand?.NotifyCanExecuteChanged();
        RemoveOpenAiCommand?.NotifyCanExecuteChanged();
        SaveOmdbCommand?.NotifyCanExecuteChanged();
        RemoveOmdbCommand?.NotifyCanExecuteChanged();
    }
}
