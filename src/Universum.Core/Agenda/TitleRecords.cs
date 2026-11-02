namespace Universum.Core.Agenda;

/// <summary>
/// A film, a series or an episode of Premiéry and Novinky – Seriály as a record of the agenda (owner, 2. 11. 2026: "konkrétny
/// film, seriál alebo epizódu si pridať do Poznámok alebo do Plánovaných úloh a tam ju upraviť").
/// </summary>
/// <param name="Title">The record's title ("Vymyslený film (2026)", "Vymyslený seriál S02E05").</param>
/// <param name="Text">What is known of it, a line each (premiere, where, production).</param>
/// <param name="Day">The day of the premiere or the airing; null when unknown.</param>
/// <param name="Link">The title or episode the record belongs to (one record of a kind for each).</param>
/// <param name="Tag">"film", "seriál" or "epizóda".</param>
/// <param name="Web">The title's page (TMDB), shown as a link of the record.</param>
public sealed record TitleDraft(string Title, string Text, DateOnly? Day, AgendaLink Link, string Tag, Uri? Web = null);

public static class TitleRecords
{
    /// <summary>
    /// The note (or task) of the title: the one already kept for it (a task only while it is open), else a new one saved at
    /// once – a note with the text and links, a task due on the day of the premiere (today when it has passed).
    /// </summary>
    public static (AgendaItem Item, bool Existing) Take(AgendaStore store, TitleDraft draft, bool asNote, DateOnly today)
    {
        var existing = store.Items.FirstOrDefault(i =>
            i.Links.Any(l => l.Kind == draft.Link.Kind && string.Equals(l.Target, draft.Link.Target, StringComparison.Ordinal)) &&
            (asNote ? i.Note is not null : i.Task is { IsOpen: true }));
        if (existing is not null)
        {
            return (existing, true);
        }

        var links = new List<AgendaLink> { draft.Link };
        if (draft.Web is { } web)
        {
            links.Add(new AgendaLink(LinkKind.Web, web.AbsoluteUri, "TMDB"));
        }

        var item = new AgendaItem
        {
            Title = draft.Title,
            Text = draft.Text,
            Tags = draft.Tag.Length > 0 ? [draft.Tag] : [],
            Links = links,
            Note = asNote ? new AgendaNote() : null,
            Task = asNote ? null : new AgendaTask { Due = draft.Day is { } day && day >= today ? day : today },
        };
        store.Save(item);
        return (item, false);
    }
}
