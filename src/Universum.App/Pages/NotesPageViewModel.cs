using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Agenda;
using Universum.Core.News;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>Which notes Poznámky shows.</summary>
public enum NotesSource
{
    All,
    Mine,
    Days,
    Titles,
    Bin,
}

/// <summary>A note as a card: a record of the agenda, or the note of a title of Premiéry and Novinky.</summary>
public sealed class NoteCard
{
    public required string Title { get; init; }

    /// <summary>The text (the card shows its beginning).</summary>
    public string Text { get; init; } = "";

    public string Tags { get; init; } = "";

    /// <summary>What it belongs to: "k dňu 3. 11. 2026", "k titulu", "v koši od …".</summary>
    public string Where { get; init; } = "";

    public string When { get; init; } = "";

    public Brush Background { get; init; } = Brushes.Transparent;

    public Brush Accent { get; init; } = Brushes.Gray;

    public bool Pinned { get; init; }

    public bool HasRecording { get; init; }

    public bool InBin { get; init; }

    public bool CanPin => !InBin && Record is not null;

    /// <summary>The record; null for the note of a title.</summary>
    public AgendaItem? Record { get; init; }

    /// <summary>The key of the title (MarkStore); null for a record.</summary>
    public string? TitleKey { get; init; }

    public required RelayCommand OpenCommand { get; init; }

    public RelayCommand? PinCommand { get; init; }

    public required RelayCommand DeleteCommand { get; init; }

    public RelayCommand? RestoreCommand { get; init; }
}

/// <summary>The editor of the note of a title (Premiéry, Novinky): its text, kept with the title's mark.</summary>
public sealed class TitleNoteEditor : ObservableObject
{
    private string _text;

    public TitleNoteEditor(string caption, string text, Action<string> save, Action close)
    {
        Caption = caption;
        _text = text;
        SaveCommand = new RelayCommand(() =>
        {
            save(Text);
            close();
        });
        CancelCommand = new RelayCommand(close);
    }

    public string Caption { get; }

    public string Text { get => _text; set => SetProperty(ref _text, value); }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }
}

/// <summary>
/// Poznámky (owner, 31. 10. 2026 – the group POZNÁMKY, stage P2): the notes of the agenda as coloured cards – pinned
/// first – with the notes of titles from Premiéry and Novinky among them (the same notes, not copies), the notes of days,
/// a quick note in one line, the voice (Windows' voice typing or a recording written down in the computer), tags,
/// search without diacritics, and the bin, from which a note can be taken back.
/// </summary>
public sealed class NotesPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<string, string> _captions = new(StringComparer.Ordinal);
    private NotesSource _source = NotesSource.All;
    private string _quick = "";
    private string _filter = "";
    private string _tag;
    private VideoChoice<int> _sort;
    private object? _editor;
    private string _status = "";
    private bool _building;

    public NotesPageViewModel(AppServices services)
    {
        _services = services;
        _tag = AllTags;
        _sort = Sorts[0];
        AddCommand = new RelayCommand(Add);
        NewCommand = new RelayCommand(() => Edit(null));
        EmptyBinCommand = new RelayCommand(EmptyBin, () => _services.Agenda.Bin.Count > 0);
        services.Agenda.Changed += () =>
        {
            if (!_building)
            {
                Build();
            }
        };
        services.Marks.Changed += _ => Build();
        Build();
    }

    public static string AllTags => L("všetky", "všechny", "all");

    public static IReadOnlyList<VideoChoice<int>> Sorts { get; } = [new(0, "podľa zmeny"), new(1, "podľa vytvorenia"), new(2, "podľa názvu")];

    /// <summary>The captions of titles are known (awaited by the self-test).</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    /// <summary>A note in one line (the first sentence its title).</summary>
    public string QuickText { get => _quick; set => SetProperty(ref _quick, value); }

    public RelayCommand AddCommand { get; }

    public RelayCommand NewCommand { get; }

    public RelayCommand EmptyBinCommand { get; }

    public bool ShowAll { get => _source == NotesSource.All; set => SetSource(value, NotesSource.All); }

    public bool ShowMine { get => _source == NotesSource.Mine; set => SetSource(value, NotesSource.Mine); }

    public bool ShowDays { get => _source == NotesSource.Days; set => SetSource(value, NotesSource.Days); }

    public bool ShowTitles { get => _source == NotesSource.Titles; set => SetSource(value, NotesSource.Titles); }

    public bool ShowBin { get => _source == NotesSource.Bin; set => SetSource(value, NotesSource.Bin); }

    public string BinText => L("Kôš", "Koš", "Bin") + $" ({_services.Agenda.Bin.Count})";

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                Build();
            }
        }
    }

    public IReadOnlyList<string> TagChoices { get; private set; } = [AllTags];

    public string Tag
    {
        get => _tag;
        set
        {
            if (SetProperty(ref _tag, value ?? AllTags))
            {
                Build();
            }
        }
    }

    public VideoChoice<int> Sort
    {
        get => _sort;
        set
        {
            if (SetProperty(ref _sort, value))
            {
                Build();
            }
        }
    }

    public ObservableCollection<NoteCard> Cards { get; } = [];

    public bool IsEmpty => Cards.Count == 0;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>The editor on the right: of a record (AgendaEditor) or of a title's note (TitleNoteEditor); null: none.</summary>
    public object? Editor
    {
        get => _editor;
        private set
        {
            if (SetProperty(ref _editor, value))
            {
                OnPropertyChanged(nameof(HasEditor));
            }
        }
    }

    public bool HasEditor => Editor is not null;

    /// <summary>The page was opened: the names of the titles with a note are read from what Premiéry and Novinky keep.</summary>
    public void Shown()
    {
        if (Ready.IsCompleted)
        {
            Ready = CaptionsAsync();
        }
    }

    public void Edit(AgendaItem? item) => Editor = new AgendaEditor(_services.Agenda, item, DateOnly.FromDateTime(DateTime.Now), AgendaKind.Note, () => Editor = null)
    {
        Voice = _services.Voice,
    };

    private void EditTitle(string key, string caption)
    {
        var mark = _services.Marks.Get(key);
        Editor = new TitleNoteEditor(caption, mark.Note, text => _services.Marks.Set(key, _services.Marks.Get(key) with { Note = text }), () => Editor = null);
    }

    private void SetSource(bool chosen, NotesSource source)
    {
        if (!chosen || _source == source)
        {
            return;
        }

        _source = source;
        foreach (var name in new[] { nameof(ShowAll), nameof(ShowMine), nameof(ShowDays), nameof(ShowTitles), nameof(ShowBin) })
        {
            OnPropertyChanged(name);
        }

        Build();
    }

    private void Add()
    {
        var text = QuickText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // the first sentence (at most 80 letters) is the title, the rest the text; #words are tags
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tags = words.Where(w => w.Length > 1 && w[0] == '#').Select(w => w[1..].TrimEnd(',', ';', '.')).ToList();
        var plain = string.Join(' ', words.Where(w => !(w.Length > 1 && w[0] == '#')));
        var end = plain.IndexOfAny(['.', '!', '?']);
        var title = end is > 0 and < 80 ? plain[..(end + 1)] : plain.Length <= 80 ? plain : plain[..80].TrimEnd() + "…";
        var rest = end is > 0 and < 80 ? plain[(end + 1)..].Trim() : plain.Length <= 80 ? "" : plain;
        _services.Agenda.Save(new AgendaItem { Title = title, Text = rest, Tags = tags, Note = new AgendaNote() });
        QuickText = "";
        Status = L("Poznámka pridaná: ", "Poznámka přidána: ", "Note added: ") + title;
    }

    private void Pin(AgendaItem item)
    {
        if (item.Note is { } note)
        {
            note.Pinned = !note.Pinned;
            _services.Agenda.Save(item);
        }
    }

    private void EmptyBin()
    {
        // the recordings of the notes that go for good go too
        var recordings = _services.Agenda.Bin.Select(i => i.Note?.Recording).OfType<string>().ToList();
        var removed = _services.Agenda.EmptyBin();
        foreach (var recording in recordings)
        {
            try
            {
                File.Delete(Path.Combine(VoiceRecorder.Folder, recording));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a recording that cannot be deleted now stays in the folder
            }
        }

        Status = L($"Kôš vysypaný ({removed}).", $"Koš vysypán ({removed}).", $"The bin emptied ({removed}).");
    }

    /// <summary>The cards again.</summary>
    public void Build()
    {
        _building = true;
        try
        {
            var cards = new List<(NoteCard Card, DateTimeOffset Modified, DateTimeOffset Created)>();
            var notes = _services.Agenda.Items.Where(i => i.Note is not null).ToList();
            if (_source == NotesSource.Bin)
            {
                foreach (var item in _services.Agenda.Bin)
                {
                    cards.Add((Card(item, true), item.Modified, item.Created));
                }
            }
            else
            {
                if (_source is NotesSource.All or NotesSource.Mine or NotesSource.Days)
                {
                    foreach (var item in notes.Where(i => _source switch
                             {
                                 NotesSource.Mine => !i.Links.Any(l => l.Kind == LinkKind.Day),
                                 NotesSource.Days => i.Links.Any(l => l.Kind == LinkKind.Day),
                                 _ => true,
                             }))
                    {
                        cards.Add((Card(item, false), item.Modified, item.Created));
                    }
                }

                if (_source is NotesSource.All or NotesSource.Titles)
                {
                    foreach (var (key, note) in _services.Marks.Notes)
                    {
                        var caption = _captions.GetValueOrDefault(key) ?? Caption(key);
                        cards.Add((new NoteCard
                        {
                            Title = caption,
                            Text = note,
                            Where = L("poznámka k titulu (Premiéry, Novinky)", "poznámka k titulu (Premiéry, Novinky)", "note of a title (Premieres, New episodes)"),
                            Background = Tint(CalendarColors.Of(CalendarLayer.FilmsWorld)),
                            Accent = CalendarColors.Of(CalendarLayer.FilmsWorld),
                            TitleKey = key,
                            OpenCommand = new RelayCommand(() => EditTitle(key, caption)),
                            DeleteCommand = new RelayCommand(() => _services.Marks.Set(key, _services.Marks.Get(key) with { Note = "" })),
                        }, DateTimeOffset.MinValue, DateTimeOffset.MinValue));
                    }
                }
            }

            TagChoices = [AllTags, .. notes.SelectMany(n => n.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
            OnPropertyChanged(nameof(TagChoices));
            var wanted = Words.Normalize(Filter);
            var shown = cards.Where(c => (Tag == AllTags || c.Card.Record?.Tags.Contains(Tag, StringComparer.CurrentCultureIgnoreCase) == true) &&
                                         (wanted.Length == 0 || Words.Normalize(c.Card.Title + " " + c.Card.Text + " " + c.Card.Tags).Contains(wanted, StringComparison.Ordinal)));
            var ordered = shown.OrderByDescending(c => c.Card.Pinned);
            ordered = Sort.Value switch
            {
                1 => ordered.ThenByDescending(c => c.Created),
                2 => ordered.ThenBy(c => c.Card.Title, StringComparer.CurrentCultureIgnoreCase),
                _ => ordered.ThenByDescending(c => c.Modified),
            };
            Cards.Clear();
            foreach (var (card, _, _) in ordered)
            {
                Cards.Add(card);
            }

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(BinText));
            EmptyBinCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _building = false;
        }
    }

    private NoteCard Card(AgendaItem item, bool inBin)
    {
        var accent = CalendarColors.Of(CalendarLayer.Notes, item.Color);
        var day = item.Links.FirstOrDefault(l => l.Kind == LinkKind.Day);
        var kind = item.Time is not null ? L("udalosť", "událost", "event") : item.Task is not null ? L("úloha", "úkol", "task") : null;
        return new NoteCard
        {
            Title = item.Title.Length > 0 ? item.Title : item.Text.Split('\n')[0],
            Text = item.Text,
            Tags = string.Join(" ", item.Tags.Select(t => "#" + t)),
            Where = inBin ? L("v koši od ", "v koši od ", "in the bin since ") + item.Deleted?.ToString("d. M. yyyy HH:mm", CultureInfo.InvariantCulture) + (kind is null ? "" : " · " + kind)
                : day is not null ? L("k dňu ", "ke dni ", "of the day ") + day.Caption : "",
            When = item.Modified.ToString("d. M. yyyy HH:mm", CultureInfo.InvariantCulture),
            Background = Tint(accent),
            Accent = accent,
            Pinned = item.Note?.Pinned == true,
            HasRecording = item.Note?.Recording is { Length: > 0 },
            InBin = inBin,
            Record = item,
            OpenCommand = new RelayCommand(() =>
            {
                if (!inBin)
                {
                    Edit(item);
                }
            }),
            PinCommand = new RelayCommand(() => Pin(item)),
            DeleteCommand = new RelayCommand(() =>
            {
                _services.Agenda.Delete(item.Id);
                Status = L("Poznámka je v koši – dá sa vrátiť.", "Poznámka je v koši – lze ji vrátit.", "The note is in the bin – it can be taken back.");
            }),
            RestoreCommand = new RelayCommand(() => _services.Agenda.Restore(item.Id)),
        };
    }

    // the names of titles with a note, from the kept premieres and episodes
    private async Task CaptionsAsync()
    {
        try
        {
            var news = _services.SeriesNews;
            var archive = await Task.Run(() => _services.PremiereStore.Load());
            var kept = await news.ReadAsync(_services.NewsSources, DateOnly.FromDateTime(DateTime.Now), offline: true);
            foreach (var film in archive.Films)
            {
                _captions[MarkStore.Film(film.TmdbId)] = film.Caption;
            }

            foreach (var episode in archive.Series.Select(s => s.First).Concat(kept.Episodes))
            {
                _captions[MarkStore.Series(episode)] = episode.Caption;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // the keys are shown as they are
        }

        Build();
    }

    // "film:603" → "film 603", "serial:…" → the series' key
    private static string Caption(string key) => key.StartsWith("film:", StringComparison.Ordinal) ? "film " + key[5..]
        : key.StartsWith("serial:", StringComparison.Ordinal) ? key[7..] : key;

    private static SolidColorBrush Tint(SolidColorBrush accent)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x26, accent.Color.R, accent.Color.G, accent.Color.B));
        brush.Freeze();
        return brush;
    }

    private static string L(string sk, string cs, string en) => Loc.Language switch { "cs" => cs, "en" => en, _ => sk };
}
