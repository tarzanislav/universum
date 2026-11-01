using Universum.App.Localization;

namespace Universum.App.Pages;

/// <summary>
/// Where the user gets the keys of the services UNIVERSUM may use (owner, 31. 10. 2026: click, register, paste the key
/// into UNIVERSUM) – the pages of the services themselves, opened in the browser; free keys are said to be enough.
/// </summary>
public static class ServiceLinks
{
    public static IReadOnlyList<TitleLink> Tmdb { get; } =
    [
        new(Loc.T("1. Registrácia (zadarmo)…"), new Uri("https://www.themoviedb.org/signup")),
        new(Loc.T("2. Získať kľúč API…"), new Uri("https://www.themoviedb.org/settings/api")),
    ];

    public static IReadOnlyList<TitleLink> OpenSubtitles { get; } =
    [
        new(Loc.T("OpenSubtitles: registrácia (zadarmo)…"), new Uri("https://www.opensubtitles.com/en/users/sign_up")),
        new(Loc.T("OpenSubtitles: API kľúč…"), new Uri("https://www.opensubtitles.com/en/consumers")),
    ];

    public static IReadOnlyList<TitleLink> SvSub { get; } =
    [
        new(Loc.T("svsub.net: účet a token…"), new Uri("https://svsub.net/")),
    ];

    public static IReadOnlyList<TitleLink> DeepL { get; } =
    [
        new(Loc.T("1. Registrácia DeepL API Free (zadarmo)…"), new Uri("https://www.deepl.com/pro-api")),
        new(Loc.T("2. Získať kľúč…"), new Uri("https://www.deepl.com/your-account/keys")),
    ];

    public static IReadOnlyList<TitleLink> Cloudflare { get; } =
    [
        new(Loc.T("1. Registrácia (zadarmo)…"), new Uri("https://dash.cloudflare.com/sign-up")),
        new("2. Account ID (Workers AI)…", new Uri("https://dash.cloudflare.com/?to=/:account/ai/workers-ai")),
        new(Loc.T("3. Vytvoriť API token…"), new Uri("https://dash.cloudflare.com/profile/api-tokens")),
    ];

    public static IReadOnlyList<TitleLink> OpenAi { get; } =
    [
        new(Loc.T("Získať kľúč (platené)…"), new Uri("https://platform.openai.com/api-keys")),
    ];

    public static IReadOnlyList<TitleLink> Omdb { get; } =
    [
        new(Loc.T("Získať kľúč (zadarmo)…"), new Uri("https://www.omdbapi.com/apikey.aspx")),
    ];
}
