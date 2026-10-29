using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.News;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>
/// A row of Premiéry or Novinky with the user's mark of its title: chosen (Vybrané) and a note, kept at once – a film
/// by its TMDB ID, a series for all its rows.
/// </summary>
public abstract class MarkedRow(MarkStore marks, string markKey, Watchlist? watch = null) : ObservableObject
{
    /// <summary>The title the mark belongs to.</summary>
    public string MarkKey { get; } = markKey;

    public bool IsChosen
    {
        get => marks.Get(MarkKey).Chosen;
        set => marks.Set(MarkKey, marks.Get(MarkKey) with { Chosen = value });
    }

    public string Note
    {
        get => marks.Get(MarkKey).Note;
        set => marks.Set(MarkKey, marks.Get(MarkKey) with { Note = value ?? "" });
    }

    /// <summary>The note has links.</summary>
    public bool HasLinks => Universum.Core.Text.WebLinks.HasLinks(Note);

    /// <summary>The text of the note without its links – what the field shows and edits (owner, 29. 10. 2026).</summary>
    public string NoteText
    {
        get => NoteParts.Parse(Note).Text;
        set => Note = NoteParts.Parse(Note).WithText(value).Compose();
    }

    /// <summary>The links of the note as words: the address hides in the word, a click opens the page.</summary>
    public IReadOnlyList<NoteLinkItem> Links => [.. NoteParts.Parse(Note).Links.Select(l => new NoteLinkItem(l, this))];

    /// <summary>Adds a link to the note under a short word (the site's name without one); kept at once.</summary>
    public void AddLink(string? word, Uri address)
    {
        var name = string.IsNullOrWhiteSpace(word) ? Universum.Core.Text.WebLinks.SuggestName(address) : word.Trim();
        Note = NoteParts.Parse(Note).WithLink(new NamedLink(name, address)).Compose();
    }

    public void RemoveLink(Uri address) => Note = NoteParts.Parse(Note).WithoutLink(address).Compose();

    /// <summary>Where the title can be watched legally ("▶ Netflix (SK)"), when it is chosen and offered.</summary>
    public string Streaming => watch?.Text(MarkKey) ?? "";

    /// <summary>The mark (or the availability) changed (here or in another row of the same title).</summary>
    public void MarkChanged()
    {
        OnPropertyChanged(nameof(IsChosen));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(NoteText));
        OnPropertyChanged(nameof(Links));
        OnPropertyChanged(nameof(Streaming));
    }
}

/// <summary>A link of a note shown as its word: opens the page, can be removed.</summary>
public sealed class NoteLinkItem(NamedLink link, MarkedRow row)
{
    public string Name => link.Name;

    public string ToolTip => link.Address.AbsoluteUri + " – kliknutím otvoríte stránku";

    public CommunityToolkit.Mvvm.Input.RelayCommand OpenCommand => new(() => NoteLinks.Open(link.Address));

    public CommunityToolkit.Mvvm.Input.RelayCommand RemoveCommand => new(() => row.RemoveLink(link.Address));
}

/// <summary>What the tables of Premiéry and Novinky show: the last week, what is to come, or the history.</summary>
public enum NewsView
{
    Recent,
    Upcoming,
    History,
}

/// <summary>Texts shared by the tables of Premiéry, Novinky and the Dashboard.</summary>
public static class NewsTexts
{
    /// <summary>"DNES", "zajtra", "o 3 dni", "o týždeň", "o 12 dní", "včera", "pred týždňom", "pred 3 dňami".</summary>
    public static string When(DateOnly day)
    {
        var days = day.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
        return days switch
        {
            0 => "DNES",
            1 => "zajtra",
            -1 => "včera",
            7 => "o týždeň",
            -7 => "pred týždňom",
            > 1 => $"o {Texts.Count(days, "deň", "dni", "dní")}",
            _ => $"pred {-days} dňami",
        };
    }
}
