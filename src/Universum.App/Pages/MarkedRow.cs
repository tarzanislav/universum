using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.News;

namespace Universum.App.Pages;

/// <summary>
/// A row of Premiéry or Novinky with the user's mark of its title: chosen (Vybrané) and a note, kept at once – a film
/// by its TMDB ID, a series for all its rows.
/// </summary>
public abstract class MarkedRow(MarkStore marks, string markKey) : ObservableObject
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

    /// <summary>The mark changed (here or in another row of the same title).</summary>
    public void MarkChanged()
    {
        OnPropertyChanged(nameof(IsChosen));
        OnPropertyChanged(nameof(Note));
    }
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
