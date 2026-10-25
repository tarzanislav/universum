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
        Tools =
        [
            new("Zlučovanie", "", new PlaceholderPageViewModel("Zlučovanie", "Zdrojové súbory, stopy a ich vlastnosti, výstup a prílohy – ako Multiplexer v MKVToolNix GUI.", "etapa E4")),
            new("Informácie", "", Info),
            new("Úprava hlavičky", "", new PlaceholderPageViewModel("Úprava hlavičky", "Zmena názvov, jazykov a príznakov stôp priamo v súbore, bez nového zápisu.", "etapa E1")),
            new("Kapitoly", "", new PlaceholderPageViewModel("Kapitoly", "Editor kapitol.", "etapa E4")),
            new("Hromadné spracovanie", "", new PlaceholderPageViewModel("Hromadné spracovanie", "Analýza priečinka, skupiny súborov podľa štruktúry a tolerantné šablóny.", "etapa E2")),
            new("Fronta úloh", "", new PlaceholderPageViewModel("Fronta úloh", "Úlohy čakajúce na spracovanie.", "etapa E4")),
            new("Výstup úloh", "", new PlaceholderPageViewModel("Výstup úloh", "Priebeh, upozornenia a chyby spracovania.", "etapa E4")),
        ];
        _selectedTool = Tools[1];
        SetupCommand = new AsyncRelayCommand(SetupAsync);
        OpenMkvToolNixGuiCommand = new RelayCommand(OpenMkvToolNixGui, () => _services.CurrentMkvToolNix?.HasTool(MkvToolNixTool.Gui) == true);
        _ = RefreshStatusAsync();
    }

    public IReadOnlyList<ToolItem> Tools { get; }

    public InfoPageViewModel Info { get; }

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
        var file = Info.File?.Path;
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
