using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.Execution;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;
using Universum.Platform.Windows;

namespace Universum.App.Pages;

/// <summary>Applying the UNIVERSUM rules to one file: preview, languages for undetermined tracks, writing, undo.</summary>
public sealed class RulesPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<int, LanguageTag> _languages = [];
    private readonly Dictionary<int, string> _videoNames = [];
    private MediaFile? _file;
    private EditPlan? _plan;
    private string _fileName = "";
    private string _summary = "Otvorte súbor MKV alebo ho sem pretiahnite myšou.";
    private string _issues = "";
    private string _result = "";
    private double _progress;
    private bool _busy;
    private string? _undoRecord;

    public RulesPageViewModel(AppServices services)
    {
        _services = services;
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !Busy);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !Busy && _plan is { Method: not WriteMethod.None } && !_plan.Target.IsBlocked && _services.Settings.OriginalHandling is not null);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => !Busy && _undoRecord is not null);
        ChooseTargetDirectoryCommand = new RelayCommand(ChooseTargetDirectory);
        ClearTargetDirectoryCommand = new RelayCommand(() => TargetDirectory = null);
    }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand ApplyCommand { get; }

    public AsyncRelayCommand UndoCommand { get; }

    public RelayCommand ChooseTargetDirectoryCommand { get; }

    public RelayCommand ClearTargetDirectoryCommand { get; }

    public ObservableCollection<RuleRow> Rows { get; } = [];

    public EditPlan? Plan => _plan;

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Issues { get => _issues; private set => SetProperty(ref _issues, value); }

    public string Result { get => _result; private set => SetProperty(ref _result, value); }

    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool KeepOriginal
    {
        get => _services.Settings.OriginalHandling == OriginalHandling.Keep;
        set => SetHandling(value ? OriginalHandling.Keep : null);
    }

    public bool DeleteOriginal
    {
        get => _services.Settings.OriginalHandling == OriginalHandling.Delete;
        set => SetHandling(value ? OriginalHandling.Delete : null);
    }

    public string? TargetDirectory
    {
        get => _services.Settings.TargetDirectory;
        set
        {
            _services.Settings.TargetDirectory = value;
            _services.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetDescription));
        }
    }

    public string TargetDescription => TargetDirectory is null ? "vedľa originálu" : TargetDirectory;

    public async Task LoadAsync(string path)
    {
        Result = "";
        _undoRecord = null;
        _languages.Clear();
        _videoNames.Clear();
        _file = null;
        _plan = null;
        Rows.Clear();
        FileName = Path.GetFileName(path);
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Summary = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            RefreshCommands();
            return;
        }

        try
        {
            _file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
            Recalculate();
        }
        catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException or TimeoutException)
        {
            Summary = ex.Message;
            RefreshCommands();
        }
    }

    /// <summary>Chooses a language for an undetermined track (also used by the self-test).</summary>
    public void ChooseLanguage(int trackId, string code)
    {
        _languages[trackId] = LanguageTag.FromCode(code);
        Recalculate();
    }

    private void Recalculate()
    {
        if (_file is null)
        {
            return;
        }

        var target = UniversumRules.Apply(_file, RuleProfile.Default, _languages, _videoNames);
        foreach (var video in target.Tracks.Where(t => t.Type == TrackType.Video && t.Name is not null))
        {
            _videoNames[video.TrackId] = video.Name!;
        }

        _plan = EditPlan.For(target);
        Rows.Clear();
        for (var i = 0; i < target.Tracks.Count; i++)
        {
            var track = target.Tracks[i];
            var chosen = _languages.TryGetValue(track.TrackId, out var tag) ? Languages.Find(tag.Primary) : null;
            Rows.Add(new RuleRow(track, i + 1, chosen, row =>
            {
                // recalculate after the combo box has finished its update (the rows are rebuilt)
                if (row.ChosenLanguage is { } language)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ChooseLanguage(row.TrackId, language.Ietf));
                }
            }));
        }

        Summary = _plan.Method switch
        {
            WriteMethod.None => "Súbor už spĺňa pravidlá UNIVERSUM – nie je čo meniť.",
            WriteMethod.HeaderEdit => $"Zmení sa len hlavička ({_plan.Changes.Count} zmien) – trvá to pár sekúnd.",
            _ => $"Mení sa poradie stôp – vznikne nový súbor ({_plan.Changes.Count} zmien).",
        } + (target.TagsChanged ? " Tagy sa odstránia." : "") + (_file.HasChapters ? " Kapitoly ostanú." : "");
        Issues = string.Join("\n", target.Issues.Select(Describe));
        RefreshCommands();
    }

    private async Task ApplyAsync()
    {
        if (_plan is null || _services.CurrentMkvToolNix is null || _services.Settings.OriginalHandling is not { } handling)
        {
            return;
        }

        Busy = true;
        Result = "Pracujem…";
        Progress = 0;
        try
        {
            var processor = new FileProcessor(_services.CurrentMkvToolNix, _services.Runner, new WindowsRecycleBin(), AppSettings.JournalDirectory);
            var result = await processor.ProcessAsync(_plan, new OutputOptions(handling, TargetDirectory), new Progress<double>(p => Progress = p));
            _undoRecord = result.UndoRecordPath;
            Result = result.Status switch
            {
                ProcessingStatus.Done => $"Hotovo: {result.OutputPath}" + (_undoRecord is null ? "" : "\nZmenu hlavičky je možné vrátiť späť."),
                ProcessingStatus.Unchanged => "Súbor už spĺňa pravidlá.",
                ProcessingStatus.NeedsReview => "Súbor treba najprv skontrolovať (pozri upozornenia).",
                ProcessingStatus.Cancelled => "Zrušené.",
                _ => "Nepodarilo sa – originál ostal nezmenený.\n" + string.Join("\n", result.Messages),
            };
            if (result.Status == ProcessingStatus.Done && result.Messages.Count > 0)
            {
                Result += "\n" + string.Join("\n", result.Messages);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task UndoAsync()
    {
        if (_undoRecord is null || _services.CurrentMkvToolNix is null)
        {
            return;
        }

        Busy = true;
        try
        {
            var processor = new FileProcessor(_services.CurrentMkvToolNix, _services.Runner, new WindowsRecycleBin(), AppSettings.JournalDirectory);
            var problems = await processor.UndoAsync(_undoRecord);
            Result = problems.Count == 0 ? "Pôvodná hlavička je obnovená." : "Vrátenie sa nepodarilo:\n" + string.Join("\n", problems);
            if (problems.Count == 0)
            {
                _undoRecord = null;
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Súbory Matroska (*.mkv)|*.mkv|Všetky súbory|*.*" };
        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(dialog.FileName);
        }
    }

    private void ChooseTargetDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Priečinok pre upravené súbory" };
        if (dialog.ShowDialog() == true)
        {
            TargetDirectory = dialog.FolderName;
        }
    }

    private void SetHandling(OriginalHandling? handling)
    {
        if (handling is null)
        {
            return;
        }

        _services.Settings.OriginalHandling = handling;
        _services.Settings.Save();
        OnPropertyChanged(nameof(KeepOriginal));
        OnPropertyChanged(nameof(DeleteOriginal));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OpenCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    private string Describe(RuleIssue issue) => issue.Kind switch
    {
        RuleIssueKind.UndeterminedLanguage => $"Stopa {Rows.FirstOrDefault(r => r.TrackId == issue.TrackId)?.Position}: neurčený jazyk – vyberte ho v stĺpci Jazyk.",
        RuleIssueKind.MultipleVideoTracks => "Súbor má viac video stôp – treba ho skontrolovať ručne.",
        RuleIssueKind.NoAudio => "Súbor nemá zvukovú stopu – treba ho skontrolovať ručne.",
        RuleIssueKind.NoPreferredSubtitles => "Zvuk nie je v češtine ani slovenčine a chýbajú české aj slovenské titulky – žiadne titulky sa nezobrazia automaticky.",
        _ => issue.Kind.ToString(),
    };
}
