using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Agenda;
using Universum.Core.Library;
using Universum.Core.News;

namespace Universum.App.Pages;

/// <summary>
/// Premiéry and Novinky – Seriály: a film, a series or an episode added to Poznámky or Plánované úlohy (owner, 2. 11. 2026) –
/// the record is kept at once (or the one kept for the title is found) and opened in the editor of its page.
/// </summary>
public static class NewsToAgenda
{
    /// <summary>Opens a record on its page (set by the main window: Poznámky for a note, Plánované úlohy for a task).</summary>
    public static Action<AgendaItem, bool>? Open { get; set; }

    /// <summary>The two commands of a row: Do poznámok and Do úloh.</summary>
    public static (RelayCommand Note, RelayCommand Task) Commands(AppServices services, Func<TitleDraft> draft, Action<string> said) =>
        (new RelayCommand(() => Take(services, draft(), true, said)), new RelayCommand(() => Take(services, draft(), false, said)));

    private static void Take(AppServices services, TitleDraft draft, bool asNote, Action<string> said)
    {
        var (item, existing) = TitleRecords.Take(services.Agenda, draft, asNote, DateOnly.FromDateTime(DateTime.Now));
        said(existing
            ? Loc.F(asNote ? "{0} už má poznámku – otvorená na úpravu." : "{0} už má otvorenú úlohu – otvorená na úpravu.", draft.Title)
            : Loc.F(asNote ? "Pridané do Poznámok: {0}" : "Pridané do Plánovaných úloh: {0}", draft.Title));
        Open?.Invoke(item, asNote);
    }

    /// <summary>A film of Premiéry.</summary>
    public static TitleDraft Film(FilmPremiere film) => new(
        film.Caption,
        Lines(
            Loc.F("Premiéra: {0}", Day(film.Premiere)) + (film.PremiereNote.Length > 0 ? " · " + film.PremiereNote : ""),
            film.LocalTitle.Length > 0 && film.LocalTitle != film.OriginalTitle ? Loc.F("Názov: {0}", film.LocalTitle) : "",
            film.Production.Length > 0 ? Loc.F("Produkcia: {0}", film.Production) : "",
            film.Genre.Length > 0 ? Loc.F("Žáner: {0}", film.Genre) : ""),
        film.Premiere,
        new AgendaLink(LinkKind.Title, MarkStore.Film(film.TmdbId), film.Caption),
        Loc.T("film"),
        TitleLinks.Tmdb(MediaKind.Movie, film.TmdbId));

    /// <summary>A series (Premiéry: its premiere; Novinky: its nearest episode).</summary>
    public static TitleDraft Series(SeriesEpisode episode, string what, string rhythm) => new(
        episode.Caption,
        Lines(
            Loc.F("{0}: {1}", what, Day(episode.AirDate)) + (episode.Code.Length > 0 ? " · " + episode.Code : ""),
            episode.Title != episode.OriginalTitle && episode.Title.Length > 0 ? Loc.F("Názov: {0}", episode.Title) : "",
            Production(episode),
            rhythm.Length > 0 ? Loc.F("Vysielanie: {0}", rhythm) : ""),
        episode.AirDate,
        new AgendaLink(LinkKind.Title, MarkStore.Series(episode), episode.Caption),
        Loc.T("seriál"));

    /// <summary>An episode of Novinky – Seriály.</summary>
    public static TitleDraft Episode(SeriesEpisode episode)
    {
        var name = (episode.Caption + " " + episode.Code).Trim();
        return new TitleDraft(
            name,
            Lines(Loc.F("Vysielanie: {0}", Day(episode.AirDate)), Production(episode)),
            episode.AirDate,
            new AgendaLink(LinkKind.Episode, MarkStore.Series(episode) + ":" + episode.Code, name),
            Loc.T("epizóda"));
    }

    private static string Production(SeriesEpisode episode)
    {
        var parts = new[] { episode.Channel, episode.Country.Length > 0 ? CountryNames.Name(episode.Country) : "" }.Where(p => p.Length > 0).ToList();
        return parts.Count > 0 ? Loc.F("Produkcia: {0}", string.Join(" · ", parts)) : "";
    }

    private static string Day(DateOnly day) => $"{day.Day}. {day.Month}. {day.Year}";

    private static string Lines(params string[] lines) => string.Join("\n", lines.Where(l => l.Length > 0));
}
