using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Universum.App.Pages;

/// <summary>
/// The input folder as every page shows it – Nastavenia, Premiéry, Novinky – Seriály: one value of the settings, so a
/// change anywhere is a change everywhere.
/// </summary>
public sealed class InputFolderViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _message = "";

    public InputFolderViewModel(AppServices services)
    {
        _services = services;
        ChooseCommand = new RelayCommand(Choose);
        OpenCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = false })?.Dispose(),
            () => Folder is { } folder && Directory.Exists(folder));
        ClearCommand = new RelayCommand(() => Set(null), () => Folder is not null);
        services.InputDirectoryChanged += () =>
        {
            OnPropertyChanged(nameof(Folder));
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(IsSet));
            OnPropertyChanged(nameof(SortingHint));
            OpenCommand.NotifyCanExecuteChanged();
            ClearCommand.NotifyCanExecuteChanged();
        };
    }

    public string? Folder => _services.InputDirectory;

    public bool IsSet => Folder is not null;

    public string Display => Folder is { } folder
        ? Directory.Exists(folder) ? folder : folder + " (nie je dostupný)"
        : "nie je nastavený – zvoľte ho tlačidlom Zmeniť…";

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>Owner, 29. 10. 2026: films go into MOVIES, series into TVSHOWS (each series in its own folder).</summary>
    public string SortingHint => IsSet ? "Filmy dajte do priečinka MOVIES, seriály do TVSHOWS – každý seriál do vlastného priečinka." : "";

    public RelayCommand ChooseCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand ClearCommand { get; }

    /// <summary>Asks for a folder; the self-test answers instead.</summary>
    public Func<string?, string?> PickFolder { get; set; } = start =>
    {
        var dialog = new OpenFolderDialog { Title = "Vstupný priečinok (kam prichádzajú nové filmy a epizódy)" };
        if (start is not null && Directory.Exists(start))
        {
            dialog.InitialDirectory = start;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    };

    /// <summary>Sets the folder; false (with the reason in <see cref="Message"/>) when it cannot be used.</summary>
    public bool Set(string? folder)
    {
        try
        {
            _services.SetInputDirectory(folder);
            Message = folder is null ? "Vstupný priečinok je zrušený." : "";
            return true;
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message;
            return false;
        }
    }

    private void Choose()
    {
        if (PickFolder(Folder) is { } folder)
        {
            Set(folder);
        }
    }
}
