using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Agenda;
using Universum.Core.News;
using Universum.Core.Reports;
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

    /// <summary>The text (looked for, printed); the card shows its <see cref="Rows"/>.</summary>
    public string Text { get; init; } = "";

    /// <summary>The note in rows: its text, and every link on a row of its own (owner, 7. 11. 2026).</summary>
    public IReadOnlyList<NoteRowView> Rows { get; init; } = [];

    /// <summary>"… a ďalšie (3) – otvorte poznámku" when the card does not show all rows.</summary>
    public string More { get; init; } = "";

    public bool HasMore => More.Length > 0;

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

    /// <summary>The width of the card (owner, 8. 11. 2026: a note can be made wider – usual, wide, widest).</summary>
    public double CardWidth { get; init; } = NotesPageViewModel.CardWidths[0];

    /// <summary>Makes the card wider (the widest becomes usual again); null for the notes of titles and in the bin.</summary>
    public RelayCommand? WidenCommand { get; init; }

    public bool CanWiden => WidenCommand is not null;

    public string WidenToolTip { get; init; } = "";

    /// <summary>The card is the Zásobník TMDB (it can be emptied with one click).</summary>
    public bool IsTray { get; init; }

    public RelayCommand? ClearTrayCommand { get; init; }
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

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(Links));
                OnPropertyChanged(nameof(HasLinks));
            }
        }
    }

    /// <summary>Shows a link of the note (Poznámky gives it, with the names of TMDB); null: the links are not shown.</summary>
    public Func<NoteRow, NoteLinkView>? LinkView { get; init; }

    /// <summary>The links of the note, each on a row, to open them (owner, 7. 11. 2026).</summary>
    public IReadOnlyList<NoteLinkView> Links => LinkView is null ? [] : [.. NoteRows.Of(Text).Where(r => r.IsLink).Select(LinkView)];

    public bool HasLinks => Links.Count > 0;

    /// <summary>The links shown again (Meno namiesto linku switched).</summary>
    public void RefreshLinks() => OnPropertyChanged(nameof(Links));

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
    private string? _trayBefore;
    private bool _editingTray;

    /// <summary>The widths of a card: usual, wide, widest.</summary>
    public static readonly double[] CardWidths = [250, 520, 790];

    // how many rows a card shows at each width
    private static readonly int[] CardRowsAt = [8, 16, 30];

    // the widths of the note opened, one click after another
    private static readonly double[] EditorWidths = [340, 560, 820];

    public NotesPageViewModel(AppServices services)
    {
        _services = services;
        _tag = AllTags;
        _sort = Sorts[0];
        AddCommand = new RelayCommand(Add);
        NewCommand = new RelayCommand(() => Edit(null));
        EmptyBinCommand = new RelayCommand(EmptyBin, () => _services.Agenda.Bin.Count > 0);
        WidenEditorCommand = new RelayCommand(WidenEditor);
        ClearTrayCommand = new RelayCommand(ClearTray);
        UndoTrayCommand = new RelayCommand(UndoTray, () => _trayBefore is not null);
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

    /// <summary>
    /// Meno namiesto linku (owner, 7. 11. 2026): a link of TMDB shown by the name of its film or series – the original
    /// title in Latin script, otherwise the English one, as the library names it; the address stays in the tooltip.
    /// </summary>
    public bool LinkNames
    {
        get => _services.Settings.NoteLinkNames;
        set
        {
            if (value != _services.Settings.NoteLinkNames)
            {
                _services.Settings.NoteLinkNames = value;
                _services.Settings.Save();
                OnPropertyChanged();
                Status = value && _services.Tmdb is null
                    ? L("Názvy filmov a seriálov sa načítajú z TMDB – zadajte kľúč TMDB v Nastaveniach.", "Názvy filmů a seriálů se načtou z TMDB – zadejte klíč TMDB v Nastavení.",
                        "The names of films and series come from TMDB – enter a TMDB key in Settings.")
                    : "";
                Build();
                (Editor as AgendaEditor)?.RefreshLinks();
                (Editor as TitleNoteEditor)?.RefreshLinks();
            }
        }
    }

    // the names of the titles of TMDB found, those asked for, and the links shown that wait for a name
    private readonly Dictionary<Core.Library.TmdbReference, string> _titleNames = [];
    private readonly HashSet<Core.Library.TmdbReference> _asking = [];
    private readonly List<NoteLinkView> _waiting = [];

    /// <summary>A link of a note on its row – by the name of its title of TMDB when Meno namiesto linku is on.</summary>
    public NoteLinkView LinkView(NoteRow row)
    {
        var tmdb = LinkNames ? row.Tmdb : null;
        var name = tmdb is null ? null : _titleNames.GetValueOrDefault(tmdb);
        var view = new NoteLinkView(row, name);
        if (tmdb is not null && name is null && row.Name is null)
        {
            _waiting.Add(view);
            _ = NameAsync(tmdb);
        }

        return view;
    }

    /// <summary>The names of the titles of TMDB asked for are known (awaited by the self-test).</summary>
    public Task Named => _naming;

    private Task _naming = Task.CompletedTask;

    // the title of TMDB read once (TMDB's answers are kept 30 days); its links shown by the name
    private async Task NameAsync(Core.Library.TmdbReference tmdb)
    {
        if (_services.Tmdb is not { } client || tmdb.Kind is not { } kind || !_asking.Add(tmdb))
        {
            return;
        }

        var asked = AskAsync();
        _naming = Task.WhenAll(_naming, asked);
        await asked;

        async Task AskAsync()
        {
            try
            {
                if (await client.GetAsync(kind, tmdb.Id) is { } title)
                {
                    var name = NoteRows.TitleName(title);
                    _titleNames[tmdb] = name;
                    foreach (var view in _waiting.Where(v => v.Row.Tmdb == tmdb).ToList())
                    {
                        view.Named(name);
                        _waiting.Remove(view);
                    }
                }
            }
            catch (Exception ex) when (ex is Core.Library.TitleServiceException or HttpRequestException or TaskCanceledException or IOException)
            {
                // without TMDB the address stays; asked again when the notes are shown again
                _asking.Remove(tmdb);
            }
        }
    }

    // how many rows a card shows (the note opened shows all its links)
    private const int CardRows = 8;

    private (IReadOnlyList<NoteRowView> Rows, string More) RowsOf(string text, int cardRows = CardRows)
    {
        var rows = NoteRows.Of(text);
        var shown = rows.Take(cardRows).Select(r => r.IsLink ? new NoteRowView(null, LinkView(r)) : new NoteRowView(r.Text, null)).ToList();
        var left = rows.Count - cardRows;
        return (shown, left > 0
            ? L($"… a ďalšie ({left}) – otvorte poznámku", $"… a další ({left}) – otevřete poznámku", $"… and {left} more – open the note")
            : "");
    }

    public static string AllTags => L("všetky", "všechny", "all");

    public static IReadOnlyList<VideoChoice<int>> Sorts { get; } = [new(0, Loc.T("podľa zmeny")), new(1, Loc.T("podľa vytvorenia")), new(2, Loc.T("podľa názvu"))];

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

    /// <summary>The width of the note opened (owner, 8. 11. 2026: larger with a click; kept for the next time).</summary>
    public double EditorWidth => Math.Clamp(_services.Settings.NotesEditorWidth, EditorWidths[0], 1400);

    public RelayCommand WidenEditorCommand { get; }

    /// <summary>The note opened is the Zásobník TMDB (its button Vyprázdniť is shown).</summary>
    public bool EditingTray { get => _editingTray; private set => SetProperty(ref _editingTray, value); }

    /// <summary>Empties the Zásobník TMDB – no title of TMDB stays in it (the own lines do); Vrátiť takes it back.</summary>
    public RelayCommand ClearTrayCommand { get; }

    public RelayCommand UndoTrayCommand { get; }

    public bool CanUndoTray => _trayBefore is not null;

    private void WidenEditor()
    {
        var next = EditorWidths.FirstOrDefault(w => w > EditorWidth + 1, EditorWidths[0]);
        _services.Settings.NotesEditorWidth = next;
        _services.Settings.Save();
        OnPropertyChanged(nameof(EditorWidth));
    }

    private void ClearTray()
    {
        if (_services.TmdbTray.Clear() is not { } before)
        {
            Status = L("Zásobník TMDB je prázdny.", "Zásobník TMDB je prázdný.", "The TMDB tray is empty.");
            return;
        }

        var count = Core.Library.Tmdb.TmdbTray.Read(before).Count;
        SetTrayBefore(before);
        Status = L($"Zásobník TMDB vyprázdnený ({count}).", $"Zásobník TMDB vyprázdněn ({count}).", $"The TMDB tray emptied ({count}).");
        ReopenTray();
    }

    // one title out of the tray (an episode alone; its series stays)
    private void RemoveFromTray(NoteRow row)
    {
        if (row.Tmdb is not { Kind: { } kind } tmdb || _services.Agenda.Items.FirstOrDefault(i => i.Uid == Core.Library.Tmdb.TmdbTray.NoteUid) is not { } tray)
        {
            return;
        }

        var episode = kind == Core.Library.MediaKind.Series ? Core.Library.Tmdb.TmdbTray.EpisodeOf(row.Link) : null;
        var entry = new Core.Library.Tmdb.TmdbTrayEntry(kind, tmdb.Id, row.Name, episode?.Season, episode?.Episode);
        var before = tray.Text;
        _services.TmdbTray.Remove(entry);
        SetTrayBefore(before);
        Status = L($"Zo zásobníka odstránený: {entry.Display}.", $"Ze zásobníku odstraněn: {entry.Display}.", $"Removed from the tray: {entry.Display}.");
        ReopenTray();
    }

    private void UndoTray()
    {
        if (_trayBefore is { } before)
        {
            _services.TmdbTray.Restore(before);
            SetTrayBefore(null);
            Status = L("Zásobník TMDB vrátený.", "Zásobník TMDB vrácen.", "The TMDB tray taken back.");
            ReopenTray();
        }
    }

    private void SetTrayBefore(string? before)
    {
        _trayBefore = before;
        OnPropertyChanged(nameof(CanUndoTray));
        UndoTrayCommand.NotifyCanExecuteChanged();
    }

    // the tray opened shows its new text
    private void ReopenTray()
    {
        if (EditingTray && _services.Agenda.Items.FirstOrDefault(i => i.Uid == Core.Library.Tmdb.TmdbTray.NoteUid) is { } tray)
        {
            Edit(tray);
        }
    }

    private static int WidthOf(AgendaItem item) =>
        Math.Clamp(item.Note?.Width ?? (item.Uid == Core.Library.Tmdb.TmdbTray.NoteUid ? 2 : 0), 0, CardWidths.Length - 1);

    private void Widen(AgendaItem item)
    {
        if (item.Note is { } note)
        {
            note.Width = (WidthOf(item) + 1) % CardWidths.Length;
            _services.Agenda.Save(item);
        }
    }

    /// <summary>Tlač a export (stage P3): the notes as they are shown (source, tag, search).</summary>
    public RelayCommand PrintCommand => _print ??= new RelayCommand(() => ShowReport(Report()));

    private RelayCommand? _print;

    public Action<ReportViewModel> ShowReport { get; set; } = model =>
        new ReportWindow(model) { Owner = System.Windows.Application.Current.MainWindow }.Show();

    public ReportViewModel Report()
    {
        var title = L("Poznámky", "Poznámky", "Notes");
        var cards = Cards.ToList();
        var records = cards.Select(c => c.Record).OfType<AgendaItem>().ToList();
        IReadOnlyList<ReportExport> exports =
        [
            new("Markdown…", "Markdown|*.md", "UNIVERSUM – poznámky.md", path =>
            {
                File.WriteAllText(path, AgendaExport.Markdown(title, [.. cards.Select(c => c.Record ?? new AgendaItem { Title = c.Title, Text = c.Text, Note = new AgendaNote() })]));
                return L("Uložené: ", "Uloženo: ", "Saved: ") + path;
            }),
            new("CSV (Excel)…", "CSV|*.csv", "UNIVERSUM – poznámky.csv", path =>
            {
                File.WriteAllText(path, AgendaExport.Csv(records, Loc.Language));
                return L("Uložené: ", "Uloženo: ", "Saved: ") + path;
            }),
        ];
        return new ReportViewModel(_services, title, [new("list", L("Ako na obrazovke", "Jako na obrazovce", "As on the screen"))], (_, options, fonts) =>
        {
            var layout = new ReportLayout(fonts, options, title, landscape: false);
            AgendaReports.Notes(layout, title, cards.Select(c => new ReportNote(c.Title, c.Text,
                string.Join(" · ", new[] { c.Pinned ? L("pripnutá", "připnutá", "pinned") : "", c.Where }.Where(t => t.Length > 0)), c.Tags, CalendarPageViewModel.ColorOf(c.Accent), c.Pinned)));
            return layout.Finish();
        }, exports);
    }

    /// <summary>The page was opened: the names of the titles with a note are read from what Premiéry and Novinky keep.</summary>
    public void Shown()
    {
        if (Ready.IsCompleted)
        {
            Ready = CaptionsAsync();
        }
    }

    public void Edit(AgendaItem? item)
    {
        EditingTray = item?.Uid == Core.Library.Tmdb.TmdbTray.NoteUid;
        Editor = new AgendaEditor(_services.Agenda, item, DateOnly.FromDateTime(DateTime.Now), AgendaKind.Note, () =>
        {
            Editor = null;
            EditingTray = false;
        })
        {
            Voice = _services.Voice,
            LinkView = LinkView,
        };
    }

    private void EditTitle(string key, string caption)
    {
        var mark = _services.Marks.Get(key);
        EditingTray = false;
        Editor = new TitleNoteEditor(caption, mark.Note, text => _services.Marks.Set(key, _services.Marks.Get(key) with { Note = text }), () => Editor = null)
        {
            LinkView = LinkView,
        };
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
        _waiting.Clear();
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
                        var (rows, more) = RowsOf(note);
                        cards.Add((new NoteCard
                        {
                            Title = caption,
                            Text = note,
                            Rows = rows,
                            More = more,
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
        var width = WidthOf(item);
        var (rows, more) = RowsOf(item.Text, CardRowsAt[width]);
        var isTray = item.Uid == Core.Library.Tmdb.TmdbTray.NoteUid && !inBin;
        if (isTray)
        {
            // each title of the tray removed alone with its ×; Vrátiť takes it back
            foreach (var link in rows.Select(r => r.Link).OfType<NoteLinkView>().Where(l => l.Row.Tmdb is { Kind: not null }))
            {
                link.RemoveCommand = new RelayCommand(() => RemoveFromTray(link.Row));
            }
        }
        return new NoteCard
        {
            CardWidth = CardWidths[width],
            WidenCommand = inBin ? null : new RelayCommand(() => Widen(item)),
            WidenToolTip = width == CardWidths.Length - 1
                ? L("Zúžiť kartu", "Zúžit kartu", "Make the card narrow")
                : L("Rozšíriť kartu (ukáže aj viac riadkov)", "Rozšířit kartu (ukáže i více řádků)", "Make the card wider (it shows more rows too)"),
            IsTray = isTray,
            ClearTrayCommand = isTray ? ClearTrayCommand : null,
            Title = item.Title.Length > 0 ? item.Title : item.Text.Split('\n')[0],
            Text = item.Text,
            Rows = rows,
            More = more,
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
