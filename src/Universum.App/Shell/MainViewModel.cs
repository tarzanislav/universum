using Universum.App.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Pages;
using Universum.App.Setup;
using Universum.App.Updates;
using Universum.Core.Updates;
using Universum.Core.MkvToolNix;
using Universum.Core.Shell;

namespace Universum.App.Shell;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private ToolItem _selectedTool;
    private string _mkvToolNixStatus = "Hľadám MKVToolNix…";
    private string _updateBadge = "";

    public MainViewModel(AppServices services)
    {
        _services = services;
        Info = new InfoPageViewModel(services);
        Rules = new RulesPageViewModel(services);
        Batch = new BatchPageViewModel(services);
        Settings = new SettingsPageViewModel(services);
        HeaderEditor = new HeaderEditorPageViewModel(services);
        Mux = new MultiplexerPageViewModel(services);
        Chapters = new ChaptersPageViewModel(services);
        Subtitles = new SubtitlesPageViewModel(services);
        Audio = new AudioPageViewModel(services);
        Video = new VideoPageViewModel(services);
        Queue = new Jobs.JobQueuePageViewModel(services);
        Output = new Jobs.JobOutputPageViewModel(Queue);
        Logs = new Jobs.ProcessingLogsPageViewModel(_services.ProcessingLogs);
        Dashboard = new DashboardPageViewModel(services, this);
        Posters = new PosterPageViewModel(services);
        HistraLists = new HistraListsPageViewModel(services)
        {
            OpenInPoster = async (kind, id) =>
            {
                Open(Posters);
                await Posters.OpenAsync(kind, id);
            },
            SendToSingle = SendTitleToProcessingAsync,
        };
        Premieres = new PremieresPageViewModel(services);
        SeriesNews = new SeriesNewsPageViewModel(services)
        {
            SendToSingle = SendEpisodeToSingleAsync,
            SendToBatch = SendEpisodesToBatchAsync,
        };
        Interests = new InterestsPageViewModel(services);
        Calendar = new CalendarPageViewModel(services) { OpenNews = OpenInNews };
        AgendaTasks = new PlaceholderPageViewModel(Loc.T("Plánované úlohy"),
            "Moje úlohy, úlohy navrhnuté programom (napr. vybraný titul je vo vstupnom priečinku) a neskôr úlohy, ktoré UNIVERSUM vykoná sám. Úlohy s termínom už teraz vidno v Kalendári.", "etapa P2");
        AgendaNotes = new PlaceholderPageViewModel(Loc.T("Poznámky"),
            "Poznámky (aj hlasom), poznámky k titulom z Premiér a Noviniek, k dňom, úlohám a spracovaniu. Poznámky k dňu sa už teraz dajú písať v Kalendári.", "etapa P2");
        NotesGuide = new PlaceholderPageViewModel(Loc.T("Návod"), "Stručný návod ku skupine POZNÁMKY – s obrázkami, zvýrazneniami a postupmi krok za krokom.", "etapa N");
        Mega = new MegaPageViewModel(services);

        // the input folder watched: new films and episodes are told, sent to Hromadné spracovanie on a click
        ArrivalsToBatchCommand = new AsyncRelayCommand(ArrivalsToBatchAsync);
        DismissArrivalsCommand = new RelayCommand(services.Arrivals.Clear);
        services.Arrivals.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasArrivals));
            OnPropertyChanged(nameof(ArrivalsText));
        };
        services.RestartWatch();

        // owner, 31. 10. 2026: the reminders of the calendar while UNIVERSUM runs – a bar on top and the sound of Windows
        DismissReminderCommand = new RelayCommand(() => ReminderText = "");
        OpenReminderCommand = new RelayCommand(() =>
        {
            if (_reminderDay is { } day)
            {
                Open(Calendar);
                Calendar.Select(day);
            }

            ReminderText = "";
        });
        _reminderCheck = DateTime.Now.AddMinutes(-1);
        _reminderTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _reminderTimer.Tick += (_, _) => CheckReminders();
        _reminderTimer.Start();

        // the left rail: Dashboard on its own, then framed groups, each with its own tint (owner's order, 27. and 28. 10.)
        var news = new ToolGroup(Loc.T("NOVINKY A OBSAH"), Color.FromRgb(0x9B, 0x6B, 0xD6));
        var multiplexing = new ToolGroup(Loc.T("MULTIPLEXING"), Color.FromRgb(0x4A, 0x7B, 0xD9));
        var studios = new ToolGroup(Loc.T("EDITÁCIA VIDEOSTÔP (video, audio, titulky)"), Color.FromRgb(0x2B, 0xA6, 0xA6));
        var templates = new ToolGroup(Loc.T("ŠABLÓNY (TEMPLATE)"), Color.FromRgb(0x3E, 0xA8, 0x5F));
        var statistics = new ToolGroup(Loc.T("ŠTATISTIKA"), Color.FromRgb(0xD9, 0x8B, 0x2B));
        var notes = new ToolGroup(Loc.T("POZNÁMKY"), CalendarColors.Group);
        Tools =
        [
            new(Loc.T("Dashboard"), "\uE80F", Dashboard),
            new(Loc.T("Premiéry"), "\uE8B2", Premieres, news),
            new(Loc.T("Novinky – Seriály"), "\uE7F4", SeriesNews, news),
            new(Loc.T("Listy z HISTRA"), "\uE8F1", HistraLists, news),
            new(Loc.T("TMDB poster"), "\uE8B9", Posters, news),
            new(Loc.T("Zaujímavosti"), "\uE734", Interests, news),
            // owner, 31. 10. 2026: the group POZNÁMKY – the calendar, the tasks, the notes and a guide
            new(Loc.T("Kalendár"), "\uE787", Calendar, notes),
            new(Loc.T("Plánované úlohy"), "\uE9D5", AgendaTasks, notes),
            new(Loc.T("Poznámky"), "\uE70B", AgendaNotes, notes),
            new(Loc.T("Návod"), "\uE897", NotesGuide, notes),
            new(Loc.T("Zlučovanie"), "\uE71D", Mux, multiplexing),
            new(Loc.T("Úprava hlavičky"), "\uE70F", HeaderEditor, multiplexing),
            new(Loc.T("Kapitoly"), "\uE8FD", Chapters, multiplexing),
            new(Loc.T("Informácie"), "\uE946", Info, multiplexing),
            // owner, 30. 10. 2026: three studios with one player – the subtitles first (the top priority), sound and picture later
            new(Loc.T("Úprava videostopy"), "\uE714", Video, studios),
            new(Loc.T("Úprava audiostopy"), "\uE8D6", Audio, studios),
            new(Loc.T("Úprava titulkostopy"), "\uE7F0", Subtitles, studios),
            new(Loc.T("Single spracovanie"), "\uE90F", Rules, templates),
            new(Loc.T("Hromadné spracovanie"), "\uE8B7", Batch, templates),
            new(Loc.T("MEGA spracovanie"), "\uE945", Mega, templates),
            new(Loc.T("Fronta úloh"), "\uE823", Queue, statistics),
            new(Loc.T("Výstup úloh"), "\uE7C3", Output, statistics),
            new(Loc.T("Záznamy spracovania"), "\uE81C", Logs, statistics),
            new(Loc.T("Nastavenia"), "\uE713", Settings, statistics),
        ];
        var view = new ListCollectionView(Tools.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ToolItem.Group)));
        ToolsView = view;
        _selectedTool = Tools[0];
        SetupCommand = new AsyncRelayCommand(SetupAsync);
        OpenMkvToolNixGuiCommand = new RelayCommand(OpenMkvToolNixGui, () => _services.CurrentMkvToolNix?.HasTool(MkvToolNixTool.Gui) == true);
        ShowUpdatesCommand = new AsyncRelayCommand(ShowUpdatesAsync);
        Settings.ShowUpdatesRequested += () => _ = ShowUpdatesAsync();
        Queue.OutputRequested += row =>
        {
            Output.Show(row, follow: false);
            SelectedTool = Tools.First(t => t.Page == Output);
        };
        _services.Updates.Checked += UpdateBadgeText;
        Info.KnownSource = () => Mux.KnownFiles.Concat(Batch.KnownFiles);
        Mux.InfoRequested += path => _ = ShowInfoAsync(path);
        Batch.InfoRequested += path => _ = ShowInfoAsync(path);
        Batch.MegaRequested += () => SelectedTool = Tools.First(t => t.Page == Mega);
        Batch.StudioRequested += (file, edits, names) => _ = EditInStudioAsync(file, edits, names);
        Batch.SoundStudioRequested += (file, edits, names) => _ = EditSoundInStudioAsync(file, edits, names);
        Mega.EditRequested += batch => _ = EditMegaBatchAsync(batch);
    }

    // owner, 30. 10. 2026: Upraviť v štúdiu – a file of Hromadné spracovanie in Úprava titulkostopy, back with Uložiť do úlohy
    private async Task EditInStudioAsync(Core.Media.MediaFile file, Core.Batch.StudioEdits? edits, IReadOnlyDictionary<int, string> names)
    {
        var batch = Tools.First(t => t.Page == Batch);
        SelectedTool = Tools.First(t => t.Page == Subtitles);
        await Subtitles.EditForBatchAsync(file, edits, saved =>
        {
            // the subtitles' part over the task as it is now (the sounds' part may have been saved meanwhile)
            var refused = Batch.SaveStudio(file.Path, saved.WithSoundsOf(Batch.StudioOf(file.Path)));
            if (refused is null)
            {
                SelectedTool = batch;
            }

            return refused;
        }, () => SelectedTool = batch, names);
    }

    // Upraviť zvuk v štúdiu – a file of Hromadné spracovanie in Úprava audiostopy, back with Uložiť do úlohy
    private async Task EditSoundInStudioAsync(Core.Media.MediaFile file, Core.Batch.StudioEdits? edits, IReadOnlyDictionary<int, string> names)
    {
        var batch = Tools.First(t => t.Page == Batch);
        SelectedTool = Tools.First(t => t.Page == Audio);
        await Audio.EditForBatchAsync(file, edits, saved =>
        {
            // the sounds' part over the task as it is now (the subtitles' part may have been saved meanwhile)
            var refused = Batch.SaveStudio(file.Path, saved.WithSubtitlesOf(Batch.StudioOf(file.Path)));
            if (refused is null)
            {
                SelectedTool = batch;
            }

            return refused;
        }, () => SelectedTool = batch, names);
    }

    /// <summary>
    /// What closing the program would lose (MKVToolNix GUI asks the same): settings of the multiplexer not yet in the
    /// queue, a running or waiting job, a running batch; null when nothing.
    /// </summary>
    public string? CloseWarning()
    {
        var lost = Mux.Unprocessed.Select(s => $"• Nastavenia zlučovania „{s.DisplayName}“ boli zmenené a nie sú vo fronte.").ToList();
        if (_services.Jobs.Running is { } running)
        {
            lost.Add($"• Práve beží úloha {running.Id} ({running.Description}) – preruší sa.");
        }

        if (_services.Jobs.WaitingCount is > 0 and var waiting)
        {
            lost.Add($"• Vo fronte čaká úloh: {waiting} – nespracujú sa.");
        }

        if (Batch.IsRunning)
        {
            lost.Add("• Beží hromadné spracovanie – zastaví sa.");
        }

        return lost.Count == 0
            ? null
            : string.Join("\n", lost) + "\n\nNaozaj chcete UNIVERSUM zavrieť? Všetky zmeny sa stratia. Originálne súbory zostanú nedotknuté.";
    }

    /// <summary>
    /// Closing anyway: the queue stops, the running job and the batch are cancelled, and closing waits (at most
    /// <paramref name="limit"/>) until they ended – mkvmerge is stopped and its unfinished output removed.
    /// </summary>
    public async Task StopWorkAsync(TimeSpan limit)
    {
        _services.Jobs.Stop();
        if (_services.Jobs.Running is { } running)
        {
            _services.Jobs.Cancel(running);
        }

        if (Batch.IsRunning && Batch.StopCommand.CanExecute(null))
        {
            Batch.StopCommand.Execute(null);
        }

        var until = DateTime.UtcNow + limit;
        while ((_services.Jobs.Running is not null || Batch.IsRunning) && DateTime.UtcNow < until)
        {
            await Task.Delay(100);
        }
    }

    /// <summary>Shows a page of the left menu.</summary>
    public void Open(object page) => SelectedTool = Tools.First(t => t.Page == page);

    /// <summary>Opens the Information page with a file from another page.</summary>
    public async Task ShowInfoAsync(string path)
    {
        SelectedTool = Tools.First(t => t.Page == Info);
        await Info.LoadAsync(path);
    }

    /// <summary>
    /// Start-up: MKVToolNix status, the request of the command line (context menu of the Explorer), then (when due) the
    /// update check.
    /// </summary>
    public async Task StartAsync(ShellRequest? request = null)
    {
        // the own request is collected at once: files handed over by other starts meanwhile join it
        var handling = request is { Paths.Count: > 0 } ? HandleAsync(request) : Task.CompletedTask;
        await RefreshStatusAsync();
        Dashboard.Shown();
        await handling;
        _ = DailyUpdateAsync();
        _services.EnsureSortingFolders();
        _ = _services.InputWatch.ScanAsync();

        try
        {
            if (await _services.Updates.CheckIfDueAsync())
            {
                UpdateBadgeText();
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // no internet: the next start tries again
        }
    }

    public IReadOnlyList<ToolItem> Tools { get; }

    /// <summary>The tools as the left rail shows them: grouped (Novinky a obsah, Multiplexing, Šablóny, Štatistika).</summary>
    public ICollectionView ToolsView { get; }

    /// <summary>Listy z HISTRA – the user's HISTRA lists, read only.</summary>
    public HistraListsPageViewModel HistraLists { get; }

    /// <summary>New films and episodes came into the input folder.</summary>
    public bool HasArrivals => _services.Arrivals.Count > 0;

    private readonly System.Windows.Threading.DispatcherTimer _reminderTimer;
    private DateTime _reminderCheck;
    private DateOnly? _reminderDay;
    private string _reminderText = "";

    /// <summary>The reminder shown on top ("🔔 20:00 Kino – …"); empty: none.</summary>
    public string ReminderText
    {
        get => _reminderText;
        private set
        {
            if (SetProperty(ref _reminderText, value))
            {
                OnPropertyChanged(nameof(HasReminder));
            }
        }
    }

    public bool HasReminder => ReminderText.Length > 0;

    public RelayCommand DismissReminderCommand { get; }

    public RelayCommand OpenReminderCommand { get; }

    /// <summary>Shows the reminders due since the last check (the self-test calls it with its own moment).</summary>
    public void CheckReminders(DateTime? now = null)
    {
        var upTo = now ?? DateTime.Now;
        var due = _services.Agenda.Reminders(_reminderCheck, upTo);
        _reminderCheck = upTo;
        if (due.Count == 0)
        {
            return;
        }

        _reminderDay = DateOnly.FromDateTime(due[0].Start);
        ReminderText = string.Join("   ·   ", due.Select(d => (d.Item.Time?.StartTime is null ? d.Start.ToString("d. M.", System.Globalization.CultureInfo.InvariantCulture) : d.Start.ToString("d. M. HH:mm", System.Globalization.CultureInfo.InvariantCulture)) + "  " + d.Item.Title));
        if (_services.Settings.FinishSound)
        {
            _services.PlayFinishSound(false);
        }
    }

    /// <summary>"Na vstupe pribudli nové súbory (3): Vymyslený.Film.mkv, …".</summary>
    public string ArrivalsText =>
        $"Na vstupe pribudli nové súbory ({_services.Arrivals.Count}): " +
        string.Join(", ", _services.Arrivals.Take(3).Select(System.IO.Path.GetFileName)) + (_services.Arrivals.Count > 3 ? " …" : "");

    public AsyncRelayCommand ArrivalsToBatchCommand { get; }

    public RelayCommand DismissArrivalsCommand { get; }

    // the new files into Hromadné spracovanie (analysed there; nothing runs by itself)
    private async Task ArrivalsToBatchAsync()
    {
        var files = _services.Arrivals.Where(System.IO.File.Exists).ToList();
        if (files.Count == 0)
        {
            _services.Arrivals.Clear();
            return;
        }

        Open(Batch);
        if (await Batch.AnalyzeChosenAsync(files))
        {
            _services.Arrivals.Clear();
        }
        else
        {
            System.Windows.MessageBox.Show("Hromadné spracovanie práve pracuje – nové súbory pošlite po jeho skončení.", "UNIVERSUM");
        }
    }

    /// <summary>TMDB poster – a poster of a film or series found on TMDB, written as folder.jpg (owner, 31. 10. 2026).</summary>
    public PosterPageViewModel Posters { get; }

    /// <summary>Kalendár (the group POZNÁMKY).</summary>
    public CalendarPageViewModel Calendar { get; }

    public PlaceholderPageViewModel AgendaTasks { get; }

    public PlaceholderPageViewModel AgendaNotes { get; }

    public PlaceholderPageViewModel NotesGuide { get; }

    /// <summary>Premiéry – films and series, SK/CZ and the world.</summary>
    public PremieresPageViewModel Premieres { get; }

    /// <summary>Novinky – Seriály – new episodes of running series, sent to processing from here.</summary>
    public SeriesNewsPageViewModel SeriesNews { get; }

    /// <summary>Zaujímavosti – news and curiosities of the film world.</summary>
    public InterestsPageViewModel Interests { get; }

    /// <summary>MEGA spracovanie – batches prepared in Hromadné spracovanie, written later (owner, 29. 10. 2026).</summary>
    public MegaPageViewModel Mega { get; }

    public DashboardPageViewModel Dashboard { get; }

    public InfoPageViewModel Info { get; }

    public RulesPageViewModel Rules { get; }

    public BatchPageViewModel Batch { get; }

    public SettingsPageViewModel Settings { get; }

    public HeaderEditorPageViewModel HeaderEditor { get; }

    public MultiplexerPageViewModel Mux { get; }

    public ChaptersPageViewModel Chapters { get; }

    /// <summary>
    /// Úprava titulkostopy (formerly Titulky): the player with the subtitles, FPS, speed, delay, conversion to SRT, external
    /// subtitles (owner, 30. 10. 2026).
    /// </summary>
    public SubtitlesPageViewModel Subtitles { get; }

    /// <summary>
    /// Úprava audiostopy (owner, 30. 10. 2026): the sounds with the film – shift, PAL 25 ↔ 23,976, a dub fitted
    /// automatically, codec, stereo, loudness, language and name.
    /// </summary>
    public AudioPageViewModel Audio { get; }

    /// <summary>Úprava videostopy (owner, 30. 10. 2026): cut, proportions, turned, another height or codec – with the player.</summary>
    public VideoPageViewModel Video { get; }

    public Jobs.JobQueuePageViewModel Queue { get; }

    public Jobs.JobOutputPageViewModel Output { get; }

    /// <summary>Záznamy spracovania (owner, 31. 10. 2026).</summary>
    public Jobs.ProcessingLogsPageViewModel Logs { get; }

    public ToolItem SelectedTool
    {
        get => _selectedTool;
        set
        {
            if (SetProperty(ref _selectedTool, value) && value?.Page == Info)
            {
                // the files of the multiplexer and the batch, as they are now
                Info.RefreshKnown();
            }

            if (value?.Page == Dashboard)
            {
                Dashboard.Shown();
            }

            if (value?.Page == Premieres)
            {
                Premieres.Shown();
            }

            if (value?.Page == SeriesNews)
            {
                SeriesNews.Shown();
            }

            if (value?.Page == Interests)
            {
                Interests.Shown();
            }

            if (value?.Page == Calendar)
            {
                Calendar.Shown();
            }
        }
    }

    /// <summary>The calendar opens a premiere in Premiéry or an episode in Novinky: its tab, the title looked for.</summary>
    private void OpenInNews(CalendarLayer layer, string title)
    {
        var find = title.StartsWith("★ ", StringComparison.Ordinal) ? title[2..] : title;
        if (layer is CalendarLayer.EpisodesDomestic or CalendarLayer.EpisodesWorld)
        {
            Open(SeriesNews);
            SeriesNews.SelectedTab = SeriesNews.Tabs.First(t => t.Region == (layer == CalendarLayer.EpisodesDomestic ? Core.News.NewsRegion.Domestic : Core.News.NewsRegion.World));
            SeriesNews.Filter = find;
            return;
        }

        Open(Premieres);
        Premieres.SelectedTab = Premieres.Tab(layer switch
        {
            CalendarLayer.FilmsDomestic => PremiereTab.FilmDomestic,
            CalendarLayer.FilmsWorld => PremiereTab.FilmWorld,
            CalendarLayer.SeriesDomestic => PremiereTab.SeriesDomestic,
            _ => PremiereTab.SeriesWorld,
        });
        Premieres.ShowAll = true;
        Premieres.Filter = find;
    }

    /// <summary>
    /// The first start of a day brings Premiéry, Novinky and Zaujímavosti up to date in the background (owner, 29. 10.
    /// 2026: only once a day, else by Obnoviť); the self-test switches it off.
    /// </summary>
    public async Task DailyUpdateAsync()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (!PremieresPageViewModel.AutoLoad || _services.Settings.LastDailyUpdate == today)
        {
            return;
        }

        _services.Settings.LastDailyUpdate = today;
        _services.Settings.Save();
        await Premieres.LoadAsync(refresh: false);
        await SeriesNews.LoadAsync(refresh: false);
        if (_services.FeedSources.Count > 0)
        {
            Interests.Shown();
            await Interests.RefreshAsync();
        }

        await _services.Watchlist.CheckAsync();
        await _services.InputWatch.ScanAsync();
        await Dashboard.Week.RefreshAsync();
    }

    /// <summary>A chosen title found in the input folder goes to processing: one file to Single, several episodes to Hromadné.</summary>
    public async Task ProcessFoundAsync(Pages.FoundInFolder found)
    {
        var sent = found switch
        {
            { IsSeries: true, Files.Count: > 1 } => await SendEpisodesToBatchAsync([(found.Title, found.Files)]),
            { IsSeries: true } => await SendEpisodeToSingleAsync(found.Files[0], found.Title, found.Episode),
            _ => await SendFilmToSingleAsync(found.Files[0], found.Title),
        };
        if (!sent)
        {
            System.Windows.MessageBox.Show("Spracovanie práve pracuje – pošlite titul znova po jeho skončení.", "UNIVERSUM");
        }
    }

    /// <summary>A MEGA batch opened in Hromadné spracovanie to be corrected or completed.</summary>
    private async Task EditMegaBatchAsync(Universum.Core.Mega.MegaBatch batch)
    {
        SelectedTool = Tools.First(t => t.Page == Batch);
        if (!await Batch.OpenMegaBatchAsync(batch))
        {
            System.Windows.MessageBox.Show("Hromadné spracovanie práve pracuje (alebo dávka nemá čakajúce súbory) – skúste to po jeho skončení.", "UNIVERSUM");
        }
    }

    /// <summary>
    /// A title from Listy z HISTRA to processing (owner, 31. 10. 2026): looked for in the input folder (a file or a folder
    /// named as it); a film or one episode into Single spracovanie, more episodes of a series into Hromadné spracovanie
    /// (a film folder: its largest video). What happened in words when nothing was sent; null when it was.
    /// </summary>
    private async Task<string?> SendTitleToProcessingAsync(Universum.Core.Library.MediaKind kind, IReadOnlyList<string> names, int? year, string caption)
    {
        if (_services.InputDirectory is not { } input || !System.IO.Directory.Exists(input))
        {
            return "Nastavte vstupný priečinok (Nastavenia) – titul sa hľadá v ňom.";
        }

        var found = await Task.Run(() => new Universum.Core.Posters.PosterPlaces(_services.Library ?? new Universum.Core.Library.LibraryLayout(input), input).InInput(kind, names, year));
        if (found is null)
        {
            return $"{caption} sa vo vstupnom priečinku nenašiel – v Single spracovaní otvorte súbor sami.";
        }

        List<string> files = System.IO.File.Exists(found) ? [found]
            : await Task.Run(() => System.IO.Directory.EnumerateFiles(found, "*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(f => Universum.Core.Posters.PosterPlaces.IsVideo(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList());
        if (files.Count == 0)
        {
            return $"V priečinku {System.IO.Path.GetFileName(found)} nie je žiadne video.";
        }

        bool sent;
        if (kind == Universum.Core.Library.MediaKind.Movie)
        {
            sent = await SendFilmToSingleAsync(files.MaxBy(f => new System.IO.FileInfo(f).Length)!, caption);
        }
        else if (files.Count > 1)
        {
            sent = await SendEpisodesToBatchAsync([(caption, files)]);
        }
        else
        {
            var number = Universum.Core.Library.EpisodeNumber.TryParse(System.IO.Path.GetFileName(files[0]));
            sent = await SendEpisodeToSingleAsync(files[0], caption, number is { } n ? $"S{n.Season:00}E{n.Episode:00}" : "");
        }

        return sent ? null : "Spracovanie práve pracuje – pošlite titul znova po jeho skončení.";
    }

    /// <summary>A chosen film from the input folder into Single spracovanie with its title; false while it is busy.</summary>
    private async Task<bool> SendFilmToSingleAsync(string file, string title)
    {
        if (Rules.Busy)
        {
            return false;
        }

        Open(Rules);
        await Rules.LoadAsync(file);
        Rules.IsSeries = false;
        Rules.TitleText = title;
        return true;
    }

    public string MkvToolNixStatus { get => _mkvToolNixStatus; private set => SetProperty(ref _mkvToolNixStatus, value); }

    public AsyncRelayCommand SetupCommand { get; }

    public RelayCommand OpenMkvToolNixGuiCommand { get; }

    public AsyncRelayCommand ShowUpdatesCommand { get; }

    /// <summary>Text of the update button in the status bar: "Aktualizácie…" or what is available.</summary>
    public string UpdateBadge => _updateBadge.Length > 0 ? _updateBadge : "Aktualizácie…";

    public bool HasUpdate => _updateBadge.Length > 0;

    /// <summary>Why updating is not possible now (a batch or a file is being processed); null when it is.</summary>
    public string? UpdateBlocker() =>
        Batch.IsRunning ? "Beží hromadné spracovanie – aktualizovať sa dá po jeho skončení."
        : _services.Jobs.Running is not null ? "Beží fronta úloh – aktualizovať sa dá po jej dokončení (alebo ju zastavte)."
        : Rules.Busy ? "Spracúva sa súbor – aktualizovať sa dá po jeho dokončení."
        : _services.Jobs.WaitingCount > 0 ? "Vo fronte čakajú úlohy – aktualizovať sa dá, keď sa spracujú (alebo ich z fronty odstráňte)."
        : Mux.Unprocessed.Count > 0 ? "V Zlučovaní sú zmenené nastavenia, ktoré nie sú vo fronte – pridajte ich do fronty alebo zavrite, potom aktualizujte."
        : null;

    /// <summary>Opens the update window (also used by the self-test).</summary>
    public UpdatesWindow CreateUpdatesWindow() =>
        new(new UpdatesViewModel(_services, _services.Updates, UpdateBlocker)) { Owner = Application.Current.MainWindow };

    public string Version => $"UNIVERSUM {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";

    public async Task RefreshStatusAsync()
    {
        var installation = await _services.FindMkvToolNixAsync();
        MkvToolNixStatus = installation switch
        {
            null => "MKVToolNix nenájdený",
            { IsSupportedVersion: false } => $"MKVToolNix {installation.Version} – zastaraný",
            _ => $"MKVToolNix {installation.Version}",
        };
        OpenMkvToolNixGuiCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// A request from the Explorer's context menu. The Explorer starts UNIVERSUM once per selected file; the files of one
    /// action are collected until none came for a moment and are then handled together, in a stable order (several
    /// files to the multiplexer are asked about once, the film before its subtitles).
    /// </summary>
    public async Task HandleAsync(ShellRequest request)
    {
        if (request.Paths.Count == 0)
        {
            return;
        }

        if (!_pending.TryGetValue(request.Action, out var paths))
        {
            _pending[request.Action] = paths = [];
        }

        paths.AddRange(request.Paths);
        var ticket = ++_tickets;
        _lastTicket[request.Action] = ticket;
        await Task.Delay(CollectFor);

        // a later file of the same action waits on: that one handles them all
        if (_lastTicket[request.Action] != ticket)
        {
            return;
        }

        _pending.Remove(request.Action);
        await HandleNowAsync(request.Action, ShellRequest.Ordered(paths));
    }

    /// <summary>How long the files of one action are collected after the last one came.</summary>
    public TimeSpan CollectFor { get; set; } = TimeSpan.FromMilliseconds(700);

    /// <summary>Tells the user something (a message box; the self-test reads it).</summary>
    public Action<string> Notify { get; set; } = message =>
        MessageBox.Show(Application.Current.MainWindow!, message, "UNIVERSUM", MessageBoxButton.OK, MessageBoxImage.Information);

    private async Task HandleNowAsync(ShellAction action, IReadOnlyList<string> paths)
    {
        var existing = paths.Where(p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p)).ToList();
        if (existing.Count == 0)
        {
            Notify("Súbor alebo priečinok z Prieskumníka už neexistuje:\n" + string.Join("\n", paths));
            return;
        }

        var first = existing[0];
        switch (action)
        {
            case ShellAction.Multiplexer:
                Open(Mux);
                await Mux.AddFilesAsync(existing.Where(System.IO.File.Exists));
                return;
            case ShellAction.Batch:
            case ShellAction.Open when System.IO.Directory.Exists(first):
                if (BatchBusy())
                {
                    return;
                }

                Open(Batch);
                if (System.IO.Directory.Exists(first))
                {
                    Batch.Folder = first;
                    await Batch.AnalyzeAsync();
                }
                else
                {
                    // a file: its folder is analysed and the template made from it
                    await Batch.TemplateFromFileAsync(first);
                }

                break;
            case ShellAction.Info:
                await ShowInfoAsync(first);
                break;
            default:
                // Single spracovanie (and a file opened with UNIVERSUM): one file at a time
                if (Rules.Busy)
                {
                    Open(Rules);
                    Notify("V Single spracovaní sa práve spracúva súbor – po jeho dokončení ho otvorte znova.\n\nNeotvorený: " +
                           System.IO.Path.GetFileName(first));
                    return;
                }

                Open(Rules);
                await Rules.LoadAsync(first);
                break;
        }

        // the tools that take one file (or one folder) say which ones were left out (in batch processing the files of
        // the analysed folder are all in it)
        static string FolderOf(string path) => System.IO.Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path) ?? path;
        var batch = action == ShellAction.Batch || System.IO.Directory.Exists(first);
        var left = existing.Skip(1)
            .Where(p => !batch || !Universum.Core.IO.Paths.Same(FolderOf(p), FolderOf(first)))
            .Select(p => System.IO.Path.GetFileName(p.TrimEnd('\\', '/')))
            .ToList();
        if (left.Count > 0)
        {
            Notify($"Otvorené: {System.IO.Path.GetFileName(first.TrimEnd('\\', '/'))}\n\n" +
                   (action == ShellAction.Info ? "Informácie zobrazujú jeden súbor. " : batch
                       ? "Hromadné spracovanie spracuje jeden priečinok naraz (súbory v ňom sa spracujú všetky). "
                       : "Single spracovanie spracúva jeden súbor. Viac súborov naraz spracuje Hromadné spracovanie. ") +
                   $"Neotvorené ({left.Count}):\n" + string.Join("\n", left.Take(10)) + (left.Count > 10 ? "\n…" : ""));
        }
    }

    private readonly Dictionary<ShellAction, List<string>> _pending = [];
    private readonly Dictionary<ShellAction, long> _lastTicket = [];
    private long _tickets;

    // the batch being analysed or processed is never replaced
    private bool BatchBusy()
    {
        if (!Batch.Busy)
        {
            return false;
        }

        Open(Batch);
        Notify("Hromadné spracovanie práve pracuje – nový priečinok otvorte po jeho skončení (alebo ho zastavte).");
        return true;
    }

    /// <summary>An episode from Novinky into Single spracovanie, with its series and episode; false while Single is busy.</summary>
    private async Task<bool> SendEpisodeToSingleAsync(string file, string seriesTitle, string episode)
    {
        if (Rules.Busy)
        {
            return false;
        }

        Open(Rules);
        await Rules.LoadAsync(file);
        Rules.IsSeries = true;
        Rules.TitleText = seriesTitle;
        if (Rules.EpisodeText.Length == 0)
        {
            Rules.EpisodeText = episode;
        }

        return true;
    }

    /// <summary>Episodes of one or more series from Novinky into Hromadné spracovanie (one batch); false while it is busy.</summary>
    private async Task<bool> SendEpisodesToBatchAsync(IReadOnlyList<(string Title, IReadOnlyList<string> Files)> series)
    {
        if (Batch.Busy || Batch.IsRunning)
        {
            return false;
        }

        Open(Batch);
        await Batch.AnalyzeSeriesAsync(series);
        return true;
    }

    /// <summary>A folder opens in batch processing, a file in Single spracovanie.</summary>
    public Task OpenPathAsync(string path) => HandleNowAsync(ShellAction.Open, [path]);

    private async Task ShowUpdatesAsync()
    {
        var window = CreateUpdatesWindow();
        if (window.DataContext is UpdatesViewModel model && _services.Updates.LastReport is null)
        {
            window.Loaded += async (_, _) => await model.CheckAsync();
        }

        window.ShowDialog();
        await RefreshStatusAsync();
        UpdateBadgeText();
    }

    private void UpdateBadgeText()
    {
        var settings = _services.Settings;
        var report = _services.Updates.LastReport;
        _updateBadge = report is null || !UpdatePolicy.ShouldOffer(report, settings.SkippedMkvToolNixVersion, settings.SkippedUniversumVersion, settings.RemindUpdatesAfterUtc, DateTime.UtcNow)
            ? ""
            : string.Join(" · ", new[]
            {
                report.MkvToolNix is { IsAvailable: true } m ? $"MKVToolNix {MkvToolNixDownloads.Format(m.Latest)}" + (m.HasSecurityFixes ? " (bezpečnostné opravy)" : "") : null,
                report.Universum is { IsAvailable: true } u ? $"UNIVERSUM {u.Release.Version.ToString(3)}" : null,
            }.OfType<string>()) is { Length: > 0 } text ? "Nová verzia: " + text : "";
        OnPropertyChanged(nameof(UpdateBadge));
        OnPropertyChanged(nameof(HasUpdate));
    }

    private async Task SetupAsync()
    {
        new MkvToolNixSetupWindow(new MkvToolNixSetupViewModel(_services)) { Owner = Application.Current.MainWindow }.ShowDialog();
        await RefreshStatusAsync();
    }

    private void OpenMkvToolNixGui()
    {
        var gui = _services.CurrentMkvToolNix?.ToolPath(MkvToolNixTool.Gui);
        var file = SelectedTool.Page == Rules ? Rules.Plan?.Target.Source.Path
            : SelectedTool.Page == HeaderEditor ? HeaderEditor.FilePath
            : Info.File?.Path;
        if (gui is not null)
        {
            var start = new ProcessStartInfo(gui) { UseShellExecute = false };
            if (file is not null)
            {
                start.ArgumentList.Add(file);
            }

            Process.Start(start)?.Dispose();
        }
    }
}
