using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Jobs;

namespace Universum.App.Jobs;

/// <summary>A run in the list of logs: when, what kind, how it ended; ticked to be deleted.</summary>
public sealed class LogRow(ProcessingLog log) : ObservableObject
{
    private bool _isChecked;

    public ProcessingLog Log { get; } = log;

    public string When => Log.FinishedUtc.ToLocalTime().ToString("d. M. yyyy  HH:mm", CultureInfo.GetCultureInfo("sk-SK"));

    public string Kind => Log.Kind;

    public string Summary => Log.Summary;

    public bool HasProblems => Log.HasProblems;

    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
}

/// <summary>One file of a run in words: the headline, what was done, the problems with what to do, the tools' lines.</summary>
public sealed class LogFileRow(LoggedFile file, RelayCommand<string> open)
{
    public LoggedFile File { get; } = file;

    public string Name => Path.GetFileName(File.Source);

    public string Headline => File.Headline;

    public string Source => "Originál: " + File.Source;

    public string? Result => File.Result is { } result ? "Výsledok: " + result : null;

    public bool HasResult => File.Result is not null;

    public bool IsProblem => File.Outcome is LoggedOutcome.Failed or LoggedOutcome.Attention or LoggedOutcome.Skipped;

    public bool IsAttention => File.Outcome == LoggedOutcome.Attention;

    public bool IsDone => File.Outcome is LoggedOutcome.Done or LoggedOutcome.AlreadyInLibrary or LoggedOutcome.Unchanged;

    public IReadOnlyList<string> Done => File.Done;

    public bool HasDone => File.Done.Count > 0;

    public IReadOnlyList<LoggedProblem> Problems => File.Problems;

    public bool HasProblems => File.Problems.Count > 0;

    public string Technical => string.Join(Environment.NewLine, File.Technical);

    public bool HasTechnical => File.Technical.Count > 0;

    /// <summary>Shows the result (or the original) in Explorer.</summary>
    public RelayCommand<string> OpenCommand { get; } = open;
}

/// <summary>
/// Záznamy spracovania (owner, 31. 10. 2026): every run of Hromadné and Single spracovanie, MEGA, the studios and the
/// editors – what was done with each file and, when something went wrong, what it means and what to do, in words. The
/// logs stay until the user deletes them (the ticked ones or all); deleting them never touches a film.
/// </summary>
public sealed class ProcessingLogsPageViewModel : ObservableObject
{
    private readonly ProcessingLogStore _store;
    private LogRow? _selected;
    private bool _onlyProblems;
    private string _status = "";

    public ProcessingLogsPageViewModel(ProcessingLogStore store)
    {
        _store = store;
        _store.Changed += () => JobRow.OnUi(Refresh);
        DeleteCommand = new RelayCommand(DeleteChosen, () => Logs.Any(l => l.IsChecked) || _selected is not null);
        DeleteAllCommand = new RelayCommand(DeleteAll, () => Logs.Count > 0);
        OpenFolderCommand = new RelayCommand(() => Open(_store.Folder, select: false));
        _openFile = new RelayCommand<string>(path =>
        {
            if (path is not null)
            {
                Open(path, select: true);
            }
        });
        Refresh();
    }

    private readonly RelayCommand<string> _openFile;

    public ObservableCollection<LogRow> Logs { get; } = [];

    public LogRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                ShowFiles();
                DeleteCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The files of the selected run (only those with a problem when <see cref="OnlyProblems"/>).</summary>
    public ObservableCollection<LogFileRow> Files { get; } = [];

    public bool OnlyProblems
    {
        get => _onlyProblems;
        set
        {
            if (SetProperty(ref _onlyProblems, value))
            {
                ShowFiles();
            }
        }
    }

    public string Title => _selected is null ? "Vyberte záznam vľavo." : $"{_selected.Kind} – {_selected.When}";

    public string Outcome => _selected?.Log.Outcome ?? "";

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Deletes the ticked logs (or the selected one when none is ticked).</summary>
    public RelayCommand DeleteCommand { get; }

    public RelayCommand DeleteAllCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Asks before deleting (the self-test answers itself).</summary>
    public Func<string, bool> Confirm { get; set; } = question =>
        System.Windows.MessageBox.Show(question, "UNIVERSUM", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    /// <summary>Reads the logs again (the selected run stays selected).</summary>
    public void Refresh()
    {
        var selected = _selected?.Log.Id;
        foreach (var row in Logs)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Logs.Clear();
        foreach (var log in _store.All())
        {
            var row = new LogRow(log);
            row.PropertyChanged += OnRowChanged;
            Logs.Add(row);
        }

        Selected = Logs.FirstOrDefault(l => l.Log.Id == selected) ?? Logs.FirstOrDefault();
        ShowFiles();
        Status = Logs.Count == 0
            ? "Zatiaľ žiadne záznamy. Každé spracovanie (Hromadné, Single, MEGA, štúdiá, Úprava hlavičky, Kapitoly, Zlučovanie) sa sem zapíše samo."
            : $"Záznamov: {Logs.Count}, s problémom: {Logs.Count(l => l.HasProblems)}. Sú len v tomto počítači a ostanú, kým ich nezmažete.";
        DeleteAllCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => DeleteCommand.NotifyCanExecuteChanged();

    private void ShowFiles()
    {
        Files.Clear();
        foreach (var file in _selected?.Log.Files ?? [])
        {
            var row = new LogFileRow(file, _openFile);
            if (!_onlyProblems || row.IsProblem || row.HasProblems)
            {
                Files.Add(row);
            }
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Outcome));
    }

    private void DeleteChosen()
    {
        var chosen = Logs.Where(l => l.IsChecked).ToList();
        if (chosen.Count == 0 && _selected is not null)
        {
            chosen = [_selected];
        }

        if (chosen.Count == 0 || !Confirm($"Zmazať {(chosen.Count == 1 ? "záznam" : $"{chosen.Count} záznamy")}?\n\nZmaže sa len záznam – filmy ani súbory v knižnici sa nezmenia."))
        {
            return;
        }

        try
        {
            _store.Delete(chosen.Select(l => l.Log.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Záznam sa nepodarilo zmazať: " + ex.Message;
        }
    }

    private void DeleteAll()
    {
        if (!Confirm($"Zmazať všetky záznamy spracovania ({Logs.Count})?\n\nZmažú sa len záznamy – filmy ani súbory v knižnici sa nezmenia."))
        {
            return;
        }

        try
        {
            _store.DeleteAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Záznamy sa nepodarilo zmazať: " + ex.Message;
        }
    }

    // Explorer at the file (selected) or the folder
    private void Open(string path, bool select)
    {
        var target = select && File.Exists(path) ? $"/select,\"{path}\"" : Directory.Exists(path) ? $"\"{path}\"" : Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder) ? $"\"{folder}\"" : null;
        if (target is null)
        {
            Status = $"{path} už neexistuje.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "Prieskumník sa nepodarilo otvoriť: " + ex.Message;
        }
    }
}
