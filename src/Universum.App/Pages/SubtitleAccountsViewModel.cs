using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Subtitles.Download;

namespace Universum.App.Pages;

/// <summary>
/// Nastavenia – Titulky: the user's accounts for OpenSubtitles.com (API key, name, password) and svsub.net (token, AI
/// provider), kept encrypted for this Windows account (owner, 30. 10. 2026). Nothing is sent until the user searches,
/// downloads or translates on the Titulky page.
/// </summary>
public sealed class SubtitleAccountsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _openSubtitlesKey = "";
    private string _openSubtitlesUser;
    private string _openSubtitlesPassword = "";
    private string _svSubToken = "";
    private string _svSubProvider;
    private string _svSubModel;
    private string _status = "";

    public SubtitleAccountsViewModel(AppServices services)
    {
        _services = services;
        _openSubtitlesUser = services.OpenSubtitlesAccount.User ?? "";
        _svSubProvider = services.SvSubProvider;
        _svSubModel = services.SvSubModel;
        SaveOpenSubtitlesCommand = new AsyncRelayCommand(SaveOpenSubtitlesAsync, () => OpenSubtitlesKey.Trim().Length > 0 || OpenSubtitlesPassword.Length > 0 || OpenSubtitlesUser.Trim() != (services.OpenSubtitlesAccount.User ?? ""));
        RemoveOpenSubtitlesCommand = new RelayCommand(() =>
        {
            _services.SetOpenSubtitlesAccount(null, null, null);
            OpenSubtitlesUser = "";
            Status = "Účet OpenSubtitles je odstránený.";
            Refresh();
        }, () => _services.OpenSubtitlesAccount.Key is not null);
        SaveSvSubCommand = new RelayCommand(SaveSvSub, () => SvSubToken.Trim().Length > 0 || SvSubProvider.Trim() != _services.SvSubProvider || SvSubModel.Trim() != _services.SvSubModel);
        RemoveSvSubCommand = new RelayCommand(() =>
        {
            _services.SetSvSub(null, SvSubProvider, SvSubModel);
            Status = "Token svsub.net je odstránený.";
            Refresh();
        }, () => _services.SvSubToken is not null);
    }

    public string OpenSubtitlesKey { get => _openSubtitlesKey; set { if (SetProperty(ref _openSubtitlesKey, value ?? "")) { Refresh(); } } }

    public string OpenSubtitlesUser { get => _openSubtitlesUser; set { if (SetProperty(ref _openSubtitlesUser, value ?? "")) { Refresh(); } } }

    public string OpenSubtitlesPassword { get => _openSubtitlesPassword; set { if (SetProperty(ref _openSubtitlesPassword, value ?? "")) { Refresh(); } } }

    public string SvSubToken { get => _svSubToken; set { if (SetProperty(ref _svSubToken, value ?? "")) { Refresh(); } } }

    /// <summary>The AI provider svsub.net translates with: openai, anthropic, gemini, deepseek.</summary>
    public string SvSubProvider { get => _svSubProvider; set { if (SetProperty(ref _svSubProvider, value ?? "")) { Refresh(); } } }

    public IReadOnlyList<string> Providers { get; } = ["openai", "anthropic", "gemini", "deepseek"];

    /// <summary>The AI model ("gpt-6-astra"); empty = the service's default.</summary>
    public string SvSubModel { get => _svSubModel; set { if (SetProperty(ref _svSubModel, value ?? "")) { Refresh(); } } }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>What is stored now (never the secrets themselves).</summary>
    public string Stored =>
        "OpenSubtitles: " + (_services.OpenSubtitlesAccount is { Key: not null } account
            ? "kľúč uložený" + (account.User is { } user ? $", účet {user}" + (account.Password is null ? " (bez hesla – sťahovať sa nedá)" : "") : " (bez účtu – len hľadanie)")
            : "nenastavené") +
        " · svsub.net: " + (_services.SvSubToken is null ? "nenastavené"
            : $"token uložený, poskytovateľ {_services.SvSubProvider}, model {(_services.SvSubModel.Length > 0 ? _services.SvSubModel : "predvolený služby")}");

    public AsyncRelayCommand SaveOpenSubtitlesCommand { get; }

    public RelayCommand RemoveOpenSubtitlesCommand { get; }

    public RelayCommand SaveSvSubCommand { get; }

    public RelayCommand RemoveSvSubCommand { get; }

    // the key is checked by a search (needs no login); the login only when a password is given
    private async Task SaveOpenSubtitlesAsync()
    {
        var stored = _services.OpenSubtitlesAccount;
        var key = OpenSubtitlesKey.Trim().Length > 0 ? OpenSubtitlesKey.Trim() : stored.Key;
        var password = OpenSubtitlesPassword.Length > 0 ? OpenSubtitlesPassword : stored.Password;
        if (key is null)
        {
            Status = "Zadajte API kľúč (OpenSubtitles.com → profil → API Consumers).";
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var client = new OpenSubtitlesClient(http, key, AppServices.SubtitleUserAgent);
            await client.SearchAsync(new OpenSubtitlesQuery("en", null, "test", null));
            if (OpenSubtitlesUser.Trim().Length > 0 && password is not null)
            {
                await client.LoginAsync(OpenSubtitlesUser.Trim(), password);
            }

            _services.SetOpenSubtitlesAccount(key, OpenSubtitlesUser, password);
            OpenSubtitlesKey = "";
            OpenSubtitlesPassword = "";
            Status = "OpenSubtitles: overené a uložené (šifrovane pre váš účet Windows).";
        }
        catch (SubtitleServiceException ex)
        {
            Status = ex.Message + " – nič sa neuložilo.";
        }

        Refresh();
    }

    private void SaveSvSub()
    {
        var token = SvSubToken.Trim().Length > 0 ? SvSubToken.Trim() : _services.SvSubToken;
        if (token is not null && !SvSubClient.IsValidToken(token))
        {
            Status = "Toto nevyzerá ako token svsub.net (začína „svs_“).";
            return;
        }

        _services.SetSvSub(token, SvSubProvider, SvSubModel);
        SvSubToken = "";
        Status = "svsub.net: uložené (token šifrovane). Overí sa pri prvom preklade.";
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Stored));
        SaveOpenSubtitlesCommand?.NotifyCanExecuteChanged();
        RemoveOpenSubtitlesCommand?.NotifyCanExecuteChanged();
        SaveSvSubCommand?.NotifyCanExecuteChanged();
        RemoveSvSubCommand?.NotifyCanExecuteChanged();
    }
}
