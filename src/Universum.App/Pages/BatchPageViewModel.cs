using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Templates;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Templates;
using Universum.Core.Text;
using Universum.Platform.Windows;

namespace Universum.App.Pages;

/// <summary>
/// Batch processing of a folder (specification, section 7): analysis, structure groups, decisions per group,
/// tolerant template, simulation, run with pause and stop, report.
/// </summary>
public sealed class BatchPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private BatchAnalysis? _analysis;
    private IReadOnlyList<BatchItem> _items = [];
    private IReadOnlyList<ProcessingResult> _results = [];
    private GroupRow? _selectedGroup;
    private BatchTemplate _template = BatchTemplate.RulesOnly;
    private string _summary = "Vyberte priečinok so súbormi MKV a dajte Analyzovať.";
    private string _simulation = "";
    private string _runState = "";
    private double _progress;
    private bool _busy;
    private bool _running;
    private BatchControl? _control;
    private CancellationTokenSource? _cancellation;

    public BatchPageViewModel(AppServices services)
    {
        _services = services;
        ChooseFolderCommand = new RelayCommand(ChooseFolder, () => !Busy);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !Busy && Directory.Exists(Folder));
        ConfirmSuggestionsCommand = new RelayCommand(ConfirmSuggestions, () => !Busy && SlotRows.Any(s => s.HasSuggestion || s.ForcedSuggested));
        DetectSubtitlesCommand = new AsyncRelayCommand(DetectSubtitlesAsync, () => !Busy && SlotRows.Any(s => s.IsSubtitles && s.NeedsLanguage && s.Track.IsTextSubtitles));
        EditTemplateCommand = new RelayCommand(EditTemplate, () => !Busy);
        TemplateFromGroupCommand = new RelayCommand(TemplateFromGroup, () => !Busy && SelectedGroup is not null);
        LoadTemplateCommand = new RelayCommand(LoadTemplate, () => !Busy);
        SaveTemplateCommand = new RelayCommand(SaveTemplate, () => !Busy && Template.Slots.Count > 0);
        RulesOnlyCommand = new RelayCommand(() => Template = BatchTemplate.RulesOnly, () => !Busy);
        RunCommand = new AsyncRelayCommand(RunAsync, () => !Busy && _items.Any(i => i.NeedsWork) && _services.Settings.OriginalHandling is not null);
        PauseCommand = new RelayCommand(TogglePause, () => _running);
        StopCommand = new RelayCommand(Stop, () => _running);
        CleanupCommand = new RelayCommand(Cleanup, () => !Busy && _analysis?.LeftoverTemporaryFiles.Count > 0);
        SaveReportCommand = new RelayCommand(SaveReport, () => !Busy && _items.Count > 0);
        ChooseTargetDirectoryCommand = new RelayCommand(ChooseTargetDirectory, () => !Busy);
        ClearTargetDirectoryCommand = new RelayCommand(() => SetTargetDirectory(null), () => !Busy);
    }

    public RelayCommand ChooseTargetDirectoryCommand { get; }

    public RelayCommand ClearTargetDirectoryCommand { get; }

    public RelayCommand ChooseFolderCommand { get; }

    public AsyncRelayCommand AnalyzeCommand { get; }

    public RelayCommand ConfirmSuggestionsCommand { get; }

    public AsyncRelayCommand DetectSubtitlesCommand { get; }

    public RelayCommand EditTemplateCommand { get; }

    public RelayCommand TemplateFromGroupCommand { get; }

    public RelayCommand LoadTemplateCommand { get; }

    public RelayCommand SaveTemplateCommand { get; }

    public RelayCommand RulesOnlyCommand { get; }

    public AsyncRelayCommand RunCommand { get; }

    public RelayCommand PauseCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand CleanupCommand { get; }

    public RelayCommand SaveReportCommand { get; }

    public ObservableCollection<GroupRow> Groups { get; } = [];

    public ObservableCollection<SlotRow> SlotRows { get; } = [];

    public ObservableCollection<FileRow> FileRows { get; } = [];

    public IReadOnlyList<BatchItem> Items => _items;

    public IReadOnlyList<ProcessingResult> Results => _results;

    public BatchAnalysis? Analysis => _analysis;

    public string? Folder
    {
        get => _services.Settings.BatchFolder;
        set
        {
            _services.Settings.BatchFolder = value;
            _services.Settings.Save();
            OnPropertyChanged();
            RefreshCommands();
        }
    }

    public bool Recursive
    {
        get => _services.Settings.BatchRecursive;
        set
        {
            _services.Settings.BatchRecursive = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    public GroupRow? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                ShowGroup();
            }
        }
    }

    public BatchTemplate Template
    {
        get => _template;
        set
        {
            if (SetProperty(ref _template, value))
            {
                OnPropertyChanged(nameof(TemplateDescription));
                OnPropertyChanged(nameof(RemoveUnlisted));
                Replan();
            }
        }
    }

    public string TemplateDescription => Template.Slots.Count == 0
        ? "bez šablóny – len pravidlá UNIVERSUM"
        : $"{Template.Name}: " + string.Join(", ", Template.Slots.Select(s =>
            $"{Texts.Type(s.Type)} {s.Language}{(s.Role == TrackRole.Main ? "" : " " + Texts.Role(s.Role))}{(s.Required ? " (povinná)" : "")}"));

    public bool RemoveUnlisted
    {
        get => Template.Unlisted == UnlistedTracks.Remove;
        set => Template = Template with { Unlisted = value ? UnlistedTracks.Remove : UnlistedTracks.Keep };
    }

    public bool KeepOriginal
    {
        get => _services.Settings.OriginalHandling == OriginalHandling.Keep;
        set => SetHandling(value, OriginalHandling.Keep);
    }

    public bool DeleteOriginal
    {
        get => _services.Settings.OriginalHandling == OriginalHandling.Delete;
        set => SetHandling(value, OriginalHandling.Delete);
    }

    public string? TargetDirectory => _services.Settings.TargetDirectory;

    public string TargetDescription => TargetDirectory is null ? "vedľa originálov" : TargetDirectory + " (so štruktúrou podpriečinkov)";

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Simulation { get => _simulation; private set => SetProperty(ref _simulation, value); }

    public string RunState { get => _runState; private set => SetProperty(ref _runState, value); }

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

    public string PauseText => _control?.IsPaused == true ? "Pokračovať" : "Pozastaviť";

    public async Task AnalyzeAsync()
    {
        if (Folder is null)
        {
            return;
        }

        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Summary = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            return;
        }

        Busy = true;
        _results = [];
        try
        {
            var inspector = _services.Inspector(installation);
            _analysis = await BatchAnalyzer.AnalyzeAsync(Folder, Recursive, inspector,
                new Progress<(int Done, int Total)>(p => Summary = $"Analyzujem {p.Done} / {p.Total}…"),
                excludeDirectory: TargetDirectory);
            TrySave(inspector);
            Groups.Clear();
            foreach (var group in _analysis.Groups)
            {
                Groups.Add(new GroupRow(group));
            }

            Summary = $"{_analysis.FileCount} súborov v {_analysis.Groups.Count} skupinách" +
                      (_analysis.Failed.Count > 0 ? $", {_analysis.Failed.Count} sa nedalo prečítať ({string.Join(", ", _analysis.Failed.Select(f => Path.GetFileName(f.Path)))})" : "") +
                      (_analysis.LeftoverTemporaryFiles.Count > 0 ? $", {_analysis.LeftoverTemporaryFiles.Count} zvyšok po prerušenom spracovaní" : "") + ".";
            Replan();
            SelectedGroup = Groups.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Recomputes the simulation after any decision (fast: no file is read).</summary>
    public void Replan()
    {
        if (_analysis is null)
        {
            return;
        }

        BatchPlanner.Suggest(_analysis, _services.Profile);
        _items = BatchPlanner.Plan(_analysis, Template, _services.Profile);
        foreach (var row in Groups)
        {
            var items = _items.Where(i => i.Group == row.Group).ToList();
            var review = items.Count(i => i.Status == BatchStatus.NeedsReview);
            row.State = review == 0 ? "pripravené" : $"na kontrolu: {review}";
        }

        foreach (var slot in SlotRows)
        {
            slot.Refresh();
        }

        var byItem = _items.ToDictionary(i => i.File.Path);
        foreach (var file in FileRows)
        {
            file.Update(byItem[file.Item.File.Path]);
        }

        var remux = _items.Count(i => i.Status == BatchStatus.Remux);
        var header = _items.Count(i => i.Status == BatchStatus.HeaderEdit);
        Simulation = $"Spracuje sa {remux + header} súborov ({remux} nový súbor, {header} len hlavička) · " +
                     $"bez zmeny {_items.Count(i => i.Status == BatchStatus.Unchanged)} · na kontrolu {_items.Count(i => i.Status == BatchStatus.NeedsReview)}";
        RefreshCommands();
    }

    private void ShowGroup()
    {
        SlotRows.Clear();
        FileRows.Clear();
        if (SelectedGroup?.Group is not { } group)
        {
            return;
        }

        foreach (var (key, track) in SlotKey.Of(group.Representative))
        {
            SlotRows.Add(new SlotRow(group, key, track, Replan));
        }

        foreach (var item in _items.Where(i => i.Group == group))
        {
            FileRows.Add(new FileRow(item, _ => Replan()));
        }

        RefreshCommands();
    }

    private void ConfirmSuggestions()
    {
        foreach (var slot in SlotRows)
        {
            slot.ConfirmSuggestion();
        }

        Replan();
    }

    /// <summary>Recognises the language of undetermined text subtitles of the selected group (reads the representative file).</summary>
    private async Task DetectSubtitlesAsync()
    {
        if (SelectedGroup?.Group is not { } group || _services.CurrentMkvToolNix is not { } installation)
        {
            return;
        }

        Busy = true;
        try
        {
            foreach (var slot in SlotRows.Where(s => s.IsSubtitles && s.NeedsLanguage && s.Track.IsTextSubtitles).ToList())
            {
                RunState = $"Čítam titulky {slot.Slot}…";
                var text = await SubtitleText.ExtractAsync(installation, _services.Runner, group.Representative, slot.Track);
                if (text is not null && TextLanguageDetector.Detect(text) is { } code)
                {
                    group.Suggestions[slot.Key] = new LanguageEvidence(LanguageTag.FromCode(code), Confidence.Medium, EvidenceSource.SubtitleText);
                }
            }

            RunState = "";
            Replan();
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RunAsync()
    {
        if (_services.CurrentMkvToolNix is not { } installation || _services.Settings.OriginalHandling is not { } handling || _analysis is null)
        {
            return;
        }

        Busy = true;
        _running = true;
        _control = new BatchControl();
        _cancellation = new CancellationTokenSource();
        RefreshCommands();
        try
        {
            var processor = new FileProcessor(installation, _services.Runner, new WindowsRecycleBin(), AppSettings.JournalDirectory);
            var options = new OutputOptions(handling, TargetDirectory);
            var root = _analysis.Folder;
            _results = await BatchRunner.RunAsync(
                _items, processor, item => BatchRunner.OutputFor(options, root, item.File.Path),
                new Progress<BatchProgress>(p =>
                {
                    Progress = p.Total == 0 ? 0 : (p.Finished + (p.Result is null ? p.FileFraction : 0)) / p.Total;
                    RunState = $"{p.Finished} / {p.Total} · {Path.GetFileName(p.Item.File.Path)}";
                }),
                _control, _cancellation.Token);
            var cache = _services.Inspector(installation);
            foreach (var result in _results.Where(r => r.Status == ProcessingStatus.Done))
            {
                cache.Invalidate(result.SourcePath);
                if (result.OutputPath is not null)
                {
                    cache.Invalidate(result.OutputPath);
                }
            }

            TrySave(cache);
            var done = _results.Count(r => r.Status == ProcessingStatus.Done);
            var failed = _results.Count(r => r.Status == ProcessingStatus.Failed);
            RunState = $"Hotovo: {done} súborov" + (failed > 0 ? $", {failed} s chybou (originály nezmenené)" : "") +
                       (_control.StopAfterCurrent ? " – zastavené na požiadanie." : ".");
        }
        finally
        {
            _running = false;
            Busy = false;
            OnPropertyChanged(nameof(PauseText));
            RefreshCommands();
        }
    }

    private void TogglePause()
    {
        if (_control is null)
        {
            return;
        }

        if (_control.IsPaused)
        {
            _control.Resume();
        }
        else
        {
            _control.Pause();
        }

        OnPropertyChanged(nameof(PauseText));
    }

    private void Stop()
    {
        if (_control is not null)
        {
            _control.StopAfterCurrent = true;
            _control.Resume();
            RunState = "Zastavím po dokončení rozpracovaných súborov…";
        }
    }

    private void Cleanup()
    {
        if (_analysis is null)
        {
            return;
        }

        var bin = new WindowsRecycleBin();
        foreach (var file in _analysis.LeftoverTemporaryFiles.Where(File.Exists))
        {
            try
            {
                if (bin.CanRecycle(file))
                {
                    bin.MoveToRecycleBin(file);
                }
            }
            catch (IOException ex)
            {
                RunState = ex.Message;
            }
        }

        _ = AnalyzeAsync();
    }

    private void SaveReport()
    {
        var dialog = new SaveFileDialog { Filter = "Stránka HTML (*.html)|*.html|Tabuľka CSV (*.csv)|*.csv", FileName = "UNIVERSUM – správa.html" };
        if (dialog.ShowDialog() == true)
        {
            var content = dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? "﻿" + BatchReport.Csv(_items, _results)
                : BatchReport.Html("UNIVERSUM – hromadné spracovanie " + Folder, _items, _results);
            File.WriteAllText(dialog.FileName, content);
        }
    }

    private void ChooseTargetDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Priečinok pre upravené súbory" };
        if (dialog.ShowDialog() == true)
        {
            SetTargetDirectory(dialog.FolderName);
        }
    }

    /// <summary>Sets the folder for results (null = next to the originals); also used by the self-test.</summary>
    public void SetTargetDirectory(string? folder)
    {
        _services.Settings.TargetDirectory = folder;
        _services.Settings.Save();
        OnPropertyChanged(nameof(TargetDirectory));
        OnPropertyChanged(nameof(TargetDescription));
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Priečinok so súbormi MKV" };
        if (dialog.ShowDialog() == true)
        {
            Folder = dialog.FolderName;
        }
    }

    private void EditTemplate()
    {
        var model = new TemplateEditorViewModel(Template.Slots.Count == 0 ? Template with { Name = "Moja šablóna" } : Template);
        if (new TemplateEditorWindow(model) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog() == true)
        {
            Template = model.ToTemplate();
        }
    }

    private void TemplateFromGroup()
    {
        var item = _items.FirstOrDefault(i => i.Group == SelectedGroup?.Group);
        if (item is not null)
        {
            Template = BatchTemplate.FromTarget($"Podľa skupiny {item.Group.Label}", item.Target);
        }
    }

    private void LoadTemplate()
    {
        Directory.CreateDirectory(AppSettings.TemplateDirectory);
        var dialog = new OpenFileDialog { Filter = "Šablóny UNIVERSUM (*.json)|*.json", InitialDirectory = AppSettings.TemplateDirectory };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                Template = BatchTemplate.FromJson(File.ReadAllText(dialog.FileName));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                RunState = "Šablónu sa nepodarilo načítať: " + ex.Message;
            }
        }
    }

    private void SaveTemplate()
    {
        Directory.CreateDirectory(AppSettings.TemplateDirectory);
        var dialog = new SaveFileDialog
        {
            Filter = "Šablóny UNIVERSUM (*.json)|*.json",
            InitialDirectory = AppSettings.TemplateDirectory,
            FileName = string.Concat(Template.Name.Split(Path.GetInvalidFileNameChars())) + ".json",
        };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, Template.ToJson());
        }
    }

    private void SetHandling(bool selected, OriginalHandling handling)
    {
        if (!selected)
        {
            return;
        }

        _services.Settings.OriginalHandling = handling;
        _services.Settings.Save();
        OnPropertyChanged(nameof(KeepOriginal));
        OnPropertyChanged(nameof(DeleteOriginal));
        RefreshCommands();
    }

    private static void TrySave(CachingInspector inspector)
    {
        try
        {
            inspector.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the cache is only an optimisation
        }
    }

    private void RefreshCommands()
    {
        foreach (var command in new IRelayCommand[]
                 {
                     ChooseFolderCommand, AnalyzeCommand, ConfirmSuggestionsCommand, DetectSubtitlesCommand, EditTemplateCommand,
                     TemplateFromGroupCommand, LoadTemplateCommand, SaveTemplateCommand, RulesOnlyCommand, RunCommand, PauseCommand,
                     StopCommand, CleanupCommand, SaveReportCommand, ChooseTargetDirectoryCommand, ClearTargetDirectoryCommand,
                 })
        {
            command.NotifyCanExecuteChanged();
        }
    }
}
