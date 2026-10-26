using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>
/// Where a result goes in the library (specification, section 13): film or series, title, episode, TMDB, and the
/// name the rules give it. Used by the header editor, the chapter editor and the multiplexer – every result is a new
/// file in the library named by the rules; the original is never changed.
/// </summary>
public sealed class LibraryTargetPicker : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<TargetState?> _target;
    private MediaKind _kind = MediaKind.Movie;
    private string _titleText = "";
    private string _episodeText = "";
    private string _titleNote = "";
    private string _description = "";
    private string _episodeNote = "";
    private TmdbTitle? _tmdbTitle;
    private Placement? _placement;

    /// <param name="target">The result as it will be (its tracks give the name: languages, quality, codec).</param>
    public LibraryTargetPicker(AppServices services, Func<TargetState?> target)
    {
        _services = services;
        _target = target;
        Tmdb = new TmdbPicker(services, () => _kind, () => TitleText, ChooseTmdbTitle);
        ChooseOutputDirectoryCommand = new RelayCommand(ChooseOutputDirectory);
        services.TmdbChanged += Tmdb.Refresh;
        services.LibraryChanged += () =>
        {
            OnPropertyChanged(nameof(OutputDescription));
            Update();
        };
    }

    public TmdbPicker Tmdb { get; }

    public RelayCommand ChooseOutputDirectoryCommand { get; }

    /// <summary>Raised when the place changes (the page refreshes its commands).</summary>
    public event Action? Changed;

    public string OutputDescription => _services.Library?.Root ?? "nie je nastavený – zvoľte ho tlačidlom Priečinok…";

    public bool IsMovie { get => _kind == MediaKind.Movie; set => SetKind(value, MediaKind.Movie); }

    public bool IsSeries { get => _kind == MediaKind.Series; set => SetKind(value, MediaKind.Series); }

    /// <summary>Title as in the library: "Creep (2004)" or "Nine Puzzles (2025) [tmdbid-227191]".</summary>
    public string TitleText
    {
        get => _titleText;
        set
        {
            if (SetProperty(ref _titleText, value))
            {
                _tmdbTitle = null;
                TitleNote = "";
                Update();
            }
        }
    }

    /// <summary>Episode of a series file: "S01E04".</summary>
    public string EpisodeText
    {
        get => _episodeText;
        set
        {
            if (SetProperty(ref _episodeText, value))
            {
                _episodeNote = "";
                Update();
            }
        }
    }

    public string TitleNote { get => _titleNote; private set => SetProperty(ref _titleNote, value); }

    /// <summary>Where the result goes, relative to the library, or what is still missing.</summary>
    public string Description { get => _description; private set => SetProperty(ref _description, value); }

    /// <summary>The place, when title (and episode) are known and the library is set.</summary>
    public Placement? Placement => _placement;

    /// <summary>Kind, title and episode guessed from a file's and its folders' names; the user can correct them.</summary>
    public void Guess(string path)
    {
        var guess = TitleGuess.ForFile(path);
        _kind = guess.Kind;
        _titleText = guess.TitleText;
        _episodeText = guess.Episode?.Number.Tag ?? "";
        _episodeNote = EpisodeNote(guess.Episode);
        _tmdbTitle = null;
        TitleNote = "";
        Tmdb.Refresh();
        OnPropertyChanged(nameof(IsMovie));
        OnPropertyChanged(nameof(IsSeries));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(EpisodeText));
        Update();
    }

    /// <summary>Computes the place from the title, the episode and the result as it is now.</summary>
    public void Update()
    {
        _placement = null;
        var layout = _services.Library;
        var target = _target();
        var title = TitleGuess.Typed(_kind, TitleText);
        var episode = EpisodeNumber.TryParse(EpisodeText);
        if (target is null)
        {
            Description = "";
        }
        else if (layout is null)
        {
            Description = "Najprv nastavte výstupný priečinok (knižnicu).";
        }
        else if (title is null)
        {
            Description = "Zadajte názov titulu, napr. Creep (2004).";
        }
        else if (title.Problem is { } problem)
        {
            Description = $"Názov titulu: {problem} – napr. Creep (2004).";
        }
        else if (_kind == MediaKind.Series && episode is null)
        {
            Description = "Zadajte epizódu, napr. S01E04.";
        }
        else
        {
            _placement = layout.Place(target, title, episode, _services.Profile);
            var size = File.Exists(target.Source.Path) ? new FileInfo(target.Source.Path).Length : 0;
            Description = Path.GetRelativePath(layout.Root, _placement.PathFor(size)) +
                          (_placement.EstimatedPathLength(size) > LibraryNames.MaxPathLength ? "  – cesta je príliš dlhá pre Windows" : "") +
                          (episode is not null && _tmdbTitle is { Kind: MediaKind.Series } series && !series.HasEpisode(episode) ? "  – TMDB takú epizódu nepozná" : "") +
                          (_kind == MediaKind.Series && _episodeNote.Length > 0 ? "  – " + _episodeNote : "");
            if (_placement.EstimatedPathLength(size) > LibraryNames.MaxPathLength)
            {
                _placement = null;
            }
        }

        OnPropertyChanged(nameof(Placement));
        Changed?.Invoke();
    }

    /// <summary>Why a guessed episode number should be checked (Slovak), or "".</summary>
    public static string EpisodeNote(EpisodeMatch? episode) => episode switch
    {
        { FolderSeason: { } folder } found => $"sezóna v mene ({found.Number.Season}) a v priečinku ({folder}) sa líšia – skontrolujte epizódu",
        { SeasonAssumed: true } => "sezóna nie je v mene ani v priečinku – predpokladá sa 1",
        _ => "",
    };

    private void SetKind(bool selected, MediaKind kind)
    {
        if (!selected || _kind == kind)
        {
            return;
        }

        _kind = kind;
        OnPropertyChanged(nameof(IsMovie));
        OnPropertyChanged(nameof(IsSeries));
        Update();
    }

    private void ChooseTmdbTitle(TmdbTitle title)
    {
        _tmdbTitle = title;
        if (title.Kind != _kind)
        {
            _kind = title.Kind;
            OnPropertyChanged(nameof(IsMovie));
            OnPropertyChanged(nameof(IsSeries));
        }

        var (text, note) = TmdbPicker.TitleText(title);
        TitleNote = note;
        _titleText = text;
        OnPropertyChanged(nameof(TitleText));
        Update();
    }

    private void ChooseOutputDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Výstupný priečinok (knižnica s MOVIES a TVSHOWS)" };
        if (dialog.ShowDialog() == true)
        {
            _services.SetOutputDirectory(dialog.FolderName);
        }
    }
}
