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
