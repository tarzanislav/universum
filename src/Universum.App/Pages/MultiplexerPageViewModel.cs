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

    public MultiplexerPageViewModel(AppServices services)
    {
        _services = services;
        _current = new MuxSettingsViewModel(services) { IsCurrent = true };
        Settings.Add(_current);
        AddFilesCommand = new AsyncRelayCommand(AddFilesAsync);
        NewSettingsCommand = new RelayCommand(() => Current = NewSettings());
        CloseSettingsCommand = new RelayCommand<MuxSettingsViewModel>(s => Close(s!), s => s is not null);
        SelectSettingsCommand = new RelayCommand<MuxSettingsViewModel>(s => Current = s!, s => s is not null);
        ShowInfoCommand = new RelayCommand(() => InfoRequested?.Invoke(SelectedFile()!), () => SelectedFile() is not null);
        services.Jobs.Changed += () => JobRow.OnUi(QueueChanged);
        services.Jobs.JobChanged += _ => JobRow.OnUi(QueueChanged);
        Watch(_current);
    }

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

        var choice = Decide(list);
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
            _current.AddParts(owner, list);
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

        Distribute(files, choice);
        Message = string.Join("\n", problems);
    }

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

    private void Distribute(List<MediaFile> files, MuxAddChoice choice)
    {
        if (choice.Action == MuxAddAction.Append && choice.Target is { } target)
        {
            _current.Append(target, files);
            return;
        }

        MuxSettingsViewModel? shared = null;
        MuxSettingsViewModel? lastVideo = null;
        var first = true;
        foreach (var file in files)
        {
            MuxSettingsViewModel destination;
            if (choice.NewForVideo && file.TracksOf(TrackType.Video).Any())
            {
                destination = first && _current.Inputs.Count == 0 ? _current : NewSettings();
                lastVideo = destination;
            }
            else if (lastVideo is not null)
            {
                // subtitles or audio after a film go with that film
                destination = lastVideo;
            }
            else
            {
                destination = choice.Action switch
                {
                    MuxAddAction.NewSettingsForAll => shared ??= _current.Inputs.Count == 0 ? _current : NewSettings(),
                    MuxAddAction.NewSettingsForEach => first && _current.Inputs.Count == 0 ? _current : NewSettings(),
                    _ => _current,
                };
            }

            destination.AddFiles([file]);
            first = false;
        }

        // the settings the files went to last are shown
        if (Settings.LastOrDefault(s => s.Inputs.Count > 0 && files.Any(f => s.Inputs.Any(i => i.File == f))) is { } shown)
        {
            Current = shown;
        }
    }

    /// <summary>The selected source file, or the file of the selected track.</summary>
    private string? SelectedFile() => _current.SelectedSource?.Path ?? _current.SelectedTrack?.Input.File.Path;

    private void QueueChanged()
    {
        OnPropertyChanged(nameof(QueueStatus));
        OnPropertyChanged(nameof(QueueProgress));
    }

    private MuxSettingsViewModel NewSettings()
    {
        var settings = new MuxSettingsViewModel(_services);
        Settings.Add(settings);
        Watch(settings);
        return settings;
    }

    // the Information button follows the selected source of the current settings
    private void Watch(MuxSettingsViewModel settings) =>
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MuxSettingsViewModel.SelectedSource) or nameof(MuxSettingsViewModel.SelectedTrack))
            {
                ShowInfoCommand.NotifyCanExecuteChanged();
            }
        };

    private async Task AddFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Video, zvuk a titulky|*.mkv;*.mka;*.mks;*.mp4;*.m4v;*.m4a;*.mov;*.avi;*.ts;*.m2ts;*.mpg;*.vob;*.webm;*.ogg;*.opus;*.flac;*.ac3;*.eac3;*.dts;*.aac;*.mp3;*.wav;*.srt;*.ass;*.ssa;*.vtt;*.sup;*.idx|Všetky súbory|*.*",
        };
        if (dialog.ShowDialog() == true)
        {
            await AddFilesAsync(dialog.FileNames);
        }
    }
}
