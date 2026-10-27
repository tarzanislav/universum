using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Jobs;
using Universum.Core.Jobs;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;

namespace Universum.App.Pages;

/// <summary>What to do with files added to the multiplexer (MKVToolNix GUI: "Adding multiple files").</summary>
public enum MuxAddAction
{
    /// <summary>Add as new source files to the current multiplex settings.</summary>
    AddToCurrent,

    /// <summary>Create new multiplex settings and add all files to them.</summary>
    NewSettingsForAll,

    /// <summary>Create new multiplex settings for each file.</summary>
    NewSettingsForEach,

    /// <summary>Append to an existing source file (mkvmerge "+").</summary>
    Append,

    /// <summary>Add as additional parts to an existing source file (mkvmerge "( a b )").</summary>
    AdditionalParts,
}

/// <summary>The files to add and the sources of the current settings they could be appended to.</summary>
public sealed record MuxAddRequest(IReadOnlyList<string> Files, IReadOnlyList<MuxInputRow> Sources);

/// <param name="Target">The source to append to or to add parts to.</param>
/// <param name="NewForVideo">Files with video always get new multiplex settings.</param>
/// <param name="Remember">Use this action from now on without asking (only the first three actions).</param>
public sealed record MuxAddChoice(MuxAddAction Action, MuxInputRow? Target = null, bool NewForVideo = false, bool Remember = false);

/// <summary>
/// Multiplexer (MKVToolNix GUI: Multiplexer): several multiplex settings side by side as tabs, each one output file.
/// Files are added to the current settings, to new settings, appended or added as parts – asked when it is not
/// obvious. Closing settings or the program with changes not yet in the queue asks first.
/// </summary>
public sealed class MultiplexerPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private MuxSettingsViewModel _current;
    private string _message = "";
    private readonly SemaphoreSlim _adding = new(1, 1);

    public MultiplexerPageViewModel(AppServices services)
    {
        _services = services;
        _current = new MuxSettingsViewModel(services) { IsCurrent = true };
        Prepare(_current);
        Settings.Add(_current);
        AddFilesCommand = new AsyncRelayCommand(AddFilesAsync);
        NewSettingsCommand = new RelayCommand(() => Current = NewSettings());
        CloseSettingsCommand = new RelayCommand<MuxSettingsViewModel>(s => Close(s!), s => s is not null);
        SelectSettingsCommand = new RelayCommand<MuxSettingsViewModel>(s => Current = s!, s => s is not null);
        ShowInfoCommand = new RelayCommand(() => InfoRequested?.Invoke(SelectedFile()!), () => SelectedFile() is not null);
        services.Jobs.Changed += () => JobRow.OnUi(QueueChanged);
        services.Jobs.JobChanged += _ => JobRow.OnUi(QueueChanged);
    }

    /// <summary>Picks files to add (the open dialog with this title; the self-test answers itself); null = cancelled.</summary>
    public Func<string, IReadOnlyList<string>?> PickFiles { get; set; } = PickWithDialog;

    /// <summary>Asks what to do with added files (the dialog; the self-test answers itself).</summary>
    public Func<MuxAddRequest, MuxAddChoice?> AskHowToAdd { get; set; } = MuxAddFilesWindow.Ask;

    /// <summary>Asks before something is lost (true = go on); the self-test answers itself.</summary>
    public Func<string, bool> Confirm { get; set; } = message =>
        MessageBox.Show(Application.Current.MainWindow!, message, "UNIVERSUM", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    /// <summary>The Information page should show this file.</summary>
    public event Action<string>? InfoRequested;

    public AsyncRelayCommand AddFilesCommand { get; }

    public RelayCommand NewSettingsCommand { get; }

    public RelayCommand<MuxSettingsViewModel> CloseSettingsCommand { get; }

    public RelayCommand<MuxSettingsViewModel> SelectSettingsCommand { get; }

    /// <summary>Opens the selected source file on the Information page.</summary>
    public RelayCommand ShowInfoCommand { get; }

    /// <summary>The tabs: one multiplex settings each.</summary>
    public ObservableCollection<MuxSettingsViewModel> Settings { get; } = [];

    public MuxSettingsViewModel Current
    {
        get => _current;
        set
        {
            if (value is null || ReferenceEquals(_current, value))
            {
                return;
            }

            _current.IsCurrent = false;
            _current = value;
            _current.IsCurrent = true;
            OnPropertyChanged();
            ShowInfoCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>What happened with the last added files (unreadable files).</summary>
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>Settings with files that were not put into the queue yet.</summary>
    public IReadOnlyList<MuxSettingsViewModel> Unprocessed => Settings.Where(s => s.IsUnprocessed).ToList();

    /// <summary>The queue as the status line shows it: "Fronta: 1 beží (45 %), 2 čakajú · hotové 3".</summary>
    public string QueueStatus
    {
        get
        {
            var jobs = _services.Jobs.Jobs;
            var running = _services.Jobs.Running;
            var waiting = jobs.Count(j => j.Status == JobStatus.Waiting);
            var done = jobs.Count(j => j.IsFinished);
            return jobs.Count == 0
                ? "Fronta úloh je prázdna."
                : $"Fronta: {(running is null ? "nič nebeží" : $"beží úloha {running.Id} ({running.Progress:P0})")}, čaká {waiting}, dokončené {done}" +
                  (jobs.Any(j => j.Status == JobStatus.Failed) ? $", s chybou {jobs.Count(j => j.Status == JobStatus.Failed)}" : "") + ".";
        }
    }

    /// <summary>Overall progress of the running job (0–1) for the status line.</summary>
    public double QueueProgress => _services.Jobs.Running?.Progress ?? 0;

    /// <summary>A job is running (its progress bar is shown).</summary>
    public bool IsQueueRunning => _services.Jobs.Running is not null;

    /// <summary>Every file the multiplexer reads now, labelled by its settings (for the Information page).</summary>
    public IEnumerable<KnownFile> KnownFiles =>
        Settings.SelectMany(s => s.Files.Select(f => new KnownFile($"Zlučovanie – {s.ShortName}", Path.GetFileName(f), f)));

    /// <summary>
    /// Adds files (button, drag and drop, self-test): with several files, or when the current settings already have
    /// sources, asks what to do – add, new settings for all or for each, append, additional parts.
    /// </summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0)
        {
            return;
        }

        // files added while others are still being read wait for them (and are then asked about on their own)
        await _adding.WaitAsync();
        try
        {
            await AddAsync(list, _current, null);
        }
        finally
        {
            _adding.Release();
        }
    }

    /// <summary>
    /// The source list's menu: picks files and adds them to these settings as asked – as sources, appended to a file or
    /// as its further parts (nothing else is asked).
    /// </summary>
    public async Task AddPickedAsync(MuxSettingsViewModel settings, MuxAddAction action, MuxInputRow? to)
    {
        var title = action switch
        {
            MuxAddAction.Append => $"Pripojiť súbory k {to?.FileName}",
            MuxAddAction.AdditionalParts => $"Pridať ďalšie časti súboru {to?.FileName}",
            _ => "Pridať súbory",
        };
        if (PickFiles(title) is not { Count: > 0 } picked)
        {
            return;
        }

        await _adding.WaitAsync();
        try
        {
            await AddAsync(picked.ToList(), settings, new MuxAddChoice(action, to));
        }
        finally
        {
            _adding.Release();
        }
    }

    private async Task AddAsync(List<string> list, MuxSettingsViewModel target, MuxAddChoice? preset)
    {
        // the settings the files were meant for, even if another tab is chosen while they are read
        var choice = preset ?? Decide(list);
        if (choice is null || (choice.Action is MuxAddAction.Append or MuxAddAction.AdditionalParts && choice.Target is null))
        {
            return;
        }

        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Message = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            return;
        }

        // additional parts are read as one with their file: they are not inspected on their own
        if (choice.Action == MuxAddAction.AdditionalParts && choice.Target is { } owner)
        {
            if (!target.Inputs.Contains(owner))
            {
                Message = TargetGone;
                return;
            }

            target.AddParts(owner, list);
            Message = "";
            return;
        }

        var inspector = new MkvMergeInspector(installation, _services.Runner);
        var problems = new List<string>();
        var files = new List<MediaFile>();
        foreach (var path in list)
        {
            try
            {
                var file = await inspector.InspectAsync(path);
                if (!file.IsRecognized || !file.IsSupported || file.Tracks.Count == 0)
                {
                    problems.Add($"{Path.GetFileName(path)}: mkvmerge tento súbor nevie spracovať.");
                    continue;
                }

                files.Add(file);
            }
            catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException or TimeoutException)
            {
                problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (!Settings.Contains(target))
        {
            problems.Insert(0, "Nastavenia, do ktorých sa mali súbory pridať, boli medzitým zatvorené – súbory sa nepridali.");
        }
        else if (choice.Target is { } appendTo && !target.Inputs.Contains(appendTo))
        {
            problems.Insert(0, TargetGone);
        }
        else
        {
            Distribute(target, files, choice);
        }

        Message = string.Join("\n", problems);
    }

    private const string TargetGone = "Súbor, ku ktorému sa mali súbory pripojiť, bol medzitým odstránený – súbory sa nepridali.";

    /// <summary>Closes settings; with changes not yet in the queue it asks first. One empty settings always stays.</summary>
    public void Close(MuxSettingsViewModel settings)
    {
        if (settings.IsUnprocessed &&
            !Confirm($"Nastavenia zlučovania „{settings.DisplayName}“ boli zmenené. Naozaj ich chcete zavrieť? Všetky zmeny sa stratia."))
        {
            return;
        }

        var index = Settings.IndexOf(settings);
        Settings.Remove(settings);
        settings.Detach();
        if (Settings.Count == 0)
        {
            NewSettings();
        }

        if (ReferenceEquals(_current, settings))
        {
            _current = Settings[Math.Min(index, Settings.Count - 1)];
            _current.IsCurrent = true;
            OnPropertyChanged(nameof(Current));
            ShowInfoCommand.NotifyCanExecuteChanged();
        }
    }

    private MuxAddChoice? Decide(List<string> paths)
    {
        if (paths.Count == 1 && _current.Inputs.Count == 0)
        {
            return new MuxAddChoice(MuxAddAction.AddToCurrent);
        }

        if (_services.Settings.MuxAddAction is { } remembered && Enum.TryParse<MuxAddAction>(remembered, out var action) && action <= MuxAddAction.NewSettingsForEach)
        {
            return new MuxAddChoice(action, NewForVideo: _services.Settings.MuxNewSettingsForVideo);
        }

        var choice = AskHowToAdd(new MuxAddRequest(paths, _current.Inputs.ToList()));
        if (choice is { Remember: true, Action: <= MuxAddAction.NewSettingsForEach })
        {
            _services.Settings.MuxAddAction = choice.Action.ToString();
            _services.Settings.MuxNewSettingsForVideo = choice.NewForVideo;
            _services.Settings.Save();
        }

        return choice;
    }

    private void Distribute(MuxSettingsViewModel target, List<MediaFile> files, MuxAddChoice choice)
    {
        if (choice.Action == MuxAddAction.Append && choice.Target is { } appendTo)
        {
            target.Append(appendTo, files);
            return;
        }

        var placed = new List<(MediaFile File, MuxSettingsViewModel Settings)>();
        var fresh = target.Inputs.Count == 0;
        MuxSettingsViewModel Next()
        {
            // the first new settings may be the empty current one
            if (fresh)
            {
                fresh = false;
                return target;
            }

            return NewSettings();
        }

        if (choice.NewForVideo && files.Any(HasVideo))
        {
            // each file with video gets its own settings; subtitles and audio go to the film whose name they start with
            // ("Film.cz.srt" → "Film.mkv"), otherwise to the first film – the order in which they were picked does not matter
            var films = files.Where(HasVideo).Select(f => (File: f, Settings: Next())).ToList();
            foreach (var (film, settings) in films)
            {
                settings.AddFiles([film]);
                placed.Add((film, settings));
            }

            foreach (var file in files.Where(f => !HasVideo(f)))
            {
                var stem = Path.GetFileNameWithoutExtension(file.Path);
                var owner = films
                    .Where(f => BelongsTo(stem, Path.GetFileNameWithoutExtension(f.File.Path)))
                    .OrderByDescending(f => Path.GetFileNameWithoutExtension(f.File.Path).Length)
                    .Select(f => f.Settings)
                    .FirstOrDefault() ?? films[0].Settings;
                owner.AddFiles([file]);
                placed.Add((file, owner));
            }
        }
        else
        {
            MuxSettingsViewModel? shared = null;
            foreach (var file in files)
            {
                var destination = choice.Action switch
                {
                    MuxAddAction.NewSettingsForAll => shared ??= Next(),
                    MuxAddAction.NewSettingsForEach => Next(),
                    _ => target,
                };
                destination.AddFiles([file]);
                placed.Add((file, destination));
            }
        }

        // the settings the files went to last are shown
        if (placed.Count > 0)
        {
            Current = placed[^1].Settings;
        }
    }

    private static bool HasVideo(MediaFile file) => file.TracksOf(TrackType.Video).Any();

    // "Film 1.cz" belongs to "Film 1", "Film 11" does not
    private static bool BelongsTo(string stem, string film) =>
        stem.StartsWith(film, StringComparison.OrdinalIgnoreCase) && (stem.Length == film.Length || !char.IsLetterOrDigit(stem[film.Length]));

    /// <summary>The selected source file, or the file of the selected track (of an appended file: that file).</summary>
    private string? SelectedFile() =>
        _current.SelectedSource?.Path ?? _current.SelectedTrack?.Input.File.Path ?? _current.SelectedAppendedTrack?.File.Path;

    private void QueueChanged()
    {
        OnPropertyChanged(nameof(QueueStatus));
        OnPropertyChanged(nameof(QueueProgress));
        OnPropertyChanged(nameof(IsQueueRunning));
    }

    private MuxSettingsViewModel NewSettings()
    {
        var settings = new MuxSettingsViewModel(_services);
        Prepare(settings);
        Settings.Add(settings);
        return settings;
    }

    // what new settings need from the page: names already taken, the source list's menu, the Information page
    private void Prepare(MuxSettingsViewModel settings)
    {
        settings.ReservedOutputs = Reserved(settings);
        settings.AddRequested = (action, to) => AddPickedAsync(settings, action, to);
        settings.InfoRequested = path => InfoRequested?.Invoke(path);
        Watch(settings);
    }

    // the outputs of the other tabs and of multiplex jobs not finished yet: a default name never repeats them
    private Func<IEnumerable<string>> Reserved(MuxSettingsViewModel self) => () =>
        Settings.Where(s => !ReferenceEquals(s, self)).Select(s => s.OutputPath)
            .Concat(_services.Jobs.Jobs.OfType<MuxJob>().Where(j => !j.IsFinished).Select(j => j.Spec.OutputPath))
            .Where(p => !string.IsNullOrWhiteSpace(p));

    // the Information button follows the selected source of the current settings
    private void Watch(MuxSettingsViewModel settings) =>
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MuxSettingsViewModel.SelectedSource) or nameof(MuxSettingsViewModel.SelectedTrackItem))
            {
                ShowInfoCommand.NotifyCanExecuteChanged();
            }
        };

    private async Task AddFilesAsync()
    {
        if (PickFiles("Pridať súbory") is { Count: > 0 } picked)
        {
            await AddFilesAsync(picked);
        }
    }

    private static IReadOnlyList<string>? PickWithDialog(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Multiselect = true,
            Filter = "Video, zvuk a titulky|*.mkv;*.mka;*.mks;*.mk3d;*.mp4;*.m4v;*.m4a;*.mov;*.avi;*.ts;*.m2ts;*.mts;*.mpg;*.mpeg;*.vob;*.webm;*.ogg;*.ogm;*.opus;*.flac;*.ac3;*.eac3;*.dts;*.aac;*.mp3;*.wav;*.h264;*.h265;*.hevc;*.mpls;*.flv;*.rm;*.rmvb;*.3gp;*.srt;*.ass;*.ssa;*.vtt;*.sup;*.idx|Všetky súbory|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }
}
