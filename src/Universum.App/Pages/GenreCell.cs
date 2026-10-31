using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.Library.Tmdb;
using Universum.Core.News;

namespace Universum.App.Pages;

/// <summary>
/// The genre of a series in the news (owner, 31. 10. 2026: a column Žáner): as its source gives it (in Slovak), else from
/// TMDB – the same search that finds the series' poster, asked only when the row is first shown, so no extra request.
/// </summary>
public sealed class GenreCell(AppServices services, SeriesEpisode episode) : ObservableObject
{
    private string _text = TmdbGenres.Translate(episode.Genres);
    private bool _asked;

    public string Text
    {
        get
        {
            if (_text.Length == 0 && !_asked)
            {
                _asked = true;
                _ = LoadAsync();
            }

            return _text;
        }
    }

    /// <summary>The genre known so far, without asking TMDB (sorting reads every row).</summary>
    public string Sorted => _text;

    public override string ToString() => _text;

    private async Task LoadAsync()
    {
        if (await services.SeriesLookup.FindAsync(episode) is { Genres.Count: > 0 } found)
        {
            _text = TmdbGenres.Join(found.Genres);
            OnPropertyChanged(nameof(Text));
        }
    }
}
