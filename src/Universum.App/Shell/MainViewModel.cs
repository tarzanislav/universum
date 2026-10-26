using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Pages;
using Universum.App.Setup;
using Universum.App.Updates;
using Universum.Core.Updates;
using Universum.Core.MkvToolNix;

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
        Queue = new Jobs.JobQueuePageViewModel(services);
        Output = new Jobs.JobOutputPageViewModel(Queue);
        Tools =
        [
            new("Zlučovanie", "\uE71D", new PlaceholderPageViewModel("Zlučovanie", "Zdrojové súbory, stopy a ich vlastnosti, výstup a prílohy – ako Multiplexer v MKVToolNix GUI.", "etapa E4")),
            new("Informácie", "\uE946", Info),
            new("Pravidlá UNIVERSUM", "\uE90F", Rules),
            new("Úprava hlavičky", "\uE70F", HeaderEditor),
            new("Kapitoly", "\uE8FD", new PlaceholderPageViewModel("Kapitoly", "Editor kapitol.", "etapa E4")),
            new("Hromadné spracovanie", "\uE8B7", Batch),
            new("Fronta úloh", "\uE823", Queue),
            new("Výstup úloh", "\uE7C3", Output),
            new("Nastavenia", "\uE713", Settings),
        ];
        _selectedTool = Tools[1];
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
    }

    /// <summary>
    /// Start-up: MKVToolNix status, a file or folder given on the command line (context menu of the Explorer), then (when
    /// due) the update check.
    /// </summary>
    public async Task StartAsync(string? path = null)
    {
        await RefreshStatusAsync();
        if (path is not null)
        {
            await OpenPathAsync(path);
        }

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

    public InfoPageViewModel Info { get; }

    public RulesPageViewModel Rules { get; }

    public BatchPageViewModel Batch { get; }

    public SettingsPageViewModel Settings { get; }

    public HeaderEditorPageViewModel HeaderEditor { get; }

    public Jobs.JobQueuePageViewModel Queue { get; }

    public Jobs.JobOutputPageViewModel Output { get; }

    public ToolItem SelectedTool { get => _selectedTool; set => SetProperty(ref _selectedTool, value); }

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

    /// <summary>A folder opens in batch processing, a file in the UNIVERSUM rules.</summary>
    public async Task OpenPathAsync(string path)
    {
        if (System.IO.Directory.Exists(path))
        {
            SelectedTool = Tools.First(t => t.Page == Batch);
            Batch.Folder = path;
            await Batch.AnalyzeAsync();
        }
        else if (System.IO.File.Exists(path))
        {
            SelectedTool = Tools.First(t => t.Page == Rules);
            await Rules.LoadAsync(path);
        }
    }

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
