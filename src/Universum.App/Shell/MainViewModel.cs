using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Pages;
using Universum.App.Setup;
using Universum.Core.MkvToolNix;

namespace Universum.App.Shell;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private ToolItem _selectedTool;
    private string _mkvToolNixStatus = "Hľadám MKVToolNix…";

    public MainViewModel(AppServices services)
    {
        _services = services;
        Info = new InfoPageViewModel(services);
        Rules = new RulesPageViewModel(services);
        Batch = new BatchPageViewModel(services);
        Tools =
        [
            new("Zlučovanie", "\uE71D", new PlaceholderPageViewModel("Zlučovanie", "Zdrojové súbory, stopy a ich vlastnosti, výstup a prílohy – ako Multiplexer v MKVToolNix GUI.", "etapa E4")),
            new("Informácie", "\uE946", Info),
            new("Pravidlá UNIVERSUM", "\uE90F", Rules),
            new("Úprava hlavičky", "\uE70F", new PlaceholderPageViewModel("Úprava hlavičky", "Zmena názvov, jazykov a príznakov stôp priamo v súbore – ako Header editor v MKVToolNix GUI. Pravidlá UNIVERSUM sú už v nástroji Pravidlá UNIVERSUM.", "etapa E4")),
            new("Kapitoly", "\uE8FD", new PlaceholderPageViewModel("Kapitoly", "Editor kapitol.", "etapa E4")),
            new("Hromadné spracovanie", "\uE8B7", Batch),
            new("Fronta úloh", "\uE823", new PlaceholderPageViewModel("Fronta úloh", "Úlohy čakajúce na spracovanie.", "etapa E4")),
            new("Výstup úloh", "\uE7C3", new PlaceholderPageViewModel("Výstup úloh", "Priebeh, upozornenia a chyby spracovania.", "etapa E4")),
        ];
        _selectedTool = Tools[1];
        SetupCommand = new AsyncRelayCommand(SetupAsync);
        OpenMkvToolNixGuiCommand = new RelayCommand(OpenMkvToolNixGui, () => _services.CurrentMkvToolNix?.HasTool(MkvToolNixTool.Gui) == true);
        _ = RefreshStatusAsync();
    }

    public IReadOnlyList<ToolItem> Tools { get; }

    public InfoPageViewModel Info { get; }

    public RulesPageViewModel Rules { get; }

    public BatchPageViewModel Batch { get; }

    public ToolItem SelectedTool { get => _selectedTool; set => SetProperty(ref _selectedTool, value); }

    public string MkvToolNixStatus { get => _mkvToolNixStatus; private set => SetProperty(ref _mkvToolNixStatus, value); }

    public AsyncRelayCommand SetupCommand { get; }

    public RelayCommand OpenMkvToolNixGuiCommand { get; }

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

    private async Task SetupAsync()
    {
        new MkvToolNixSetupWindow(new MkvToolNixSetupViewModel(_services)) { Owner = Application.Current.MainWindow }.ShowDialog();
        await RefreshStatusAsync();
    }

    private void OpenMkvToolNixGui()
    {
        var gui = _services.CurrentMkvToolNix?.ToolPath(MkvToolNixTool.Gui);
        var file = SelectedTool.Page == Rules ? Rules.Plan?.Target.Source.Path : Info.File?.Path;
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
