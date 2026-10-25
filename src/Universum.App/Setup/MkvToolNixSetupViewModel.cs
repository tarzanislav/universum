using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.MkvToolNix;

namespace Universum.App.Setup;

/// <summary>Finding, choosing or installing MKVToolNix.</summary>
public sealed class MkvToolNixSetupViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _status = "Hľadám MKVToolNix…";
    private string _details = "";
    private double _progress;
    private bool _busy;
    private Version? _latest;

    public MkvToolNixSetupViewModel(AppServices services)
    {
        _services = services;
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !Busy);
        ChooseFolderCommand = new AsyncRelayCommand(ChooseFolderAsync, () => !Busy);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !Busy);
    }

    public AsyncRelayCommand InstallCommand { get; }

    public AsyncRelayCommand ChooseFolderCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public string Details { get => _details; private set => SetProperty(ref _details, value); }

    /// <summary>Download progress 0–1.</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                InstallCommand.NotifyCanExecuteChanged();
                ChooseFolderCommand.NotifyCanExecuteChanged();
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsReady => _services.CurrentMkvToolNix is { IsSupportedVersion: true };

    public string InstallButtonText => _latest is null ? "Stiahnuť a nainštalovať" : $"Stiahnuť a nainštalovať {MkvToolNixDownloads.Format(_latest)}";

    public async Task RefreshAsync()
    {
        Busy = true;
        try
        {
            var installation = await _services.FindMkvToolNixAsync();
            Status = installation switch
            {
                null => "MKVToolNix nie je nainštalovaný. UNIVERSUM ho potrebuje na všetku prácu so súbormi MKV.",
                { IsSupportedVersion: false } => $"Nainštalovaný MKVToolNix {installation.Version} je starší ako podporovaná verzia {MkvToolNixVersion.Minimum}.",
                _ => $"MKVToolNix {installation.Version} je pripravený.",
            };
            Details = installation?.Directory ?? "";
            OnPropertyChanged(nameof(IsReady));
            try
            {
                _latest = await MkvToolNixInstaller.GetLatestVersionAsync();
                OnPropertyChanged(nameof(InstallButtonText));
                if (installation is not null && _latest > installation.Version)
                {
                    Details += $"\nNajnovšia verzia na oficiálnej stránke: {MkvToolNixDownloads.Format(_latest)}";
                }
            }
            catch (SetupException ex)
            {
                Details += "\n" + ex.Message;
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task InstallAsync()
    {
        Busy = true;
        try
        {
            Status = "Zisťujem najnovšiu verziu MKVToolNix…";
            var version = _latest ?? await MkvToolNixInstaller.GetLatestVersionAsync();
            Status = $"Sťahujem a overujem MKVToolNix {MkvToolNixDownloads.Format(version)}…";
            var installer = await MkvToolNixInstaller.DownloadAsync(version, new Progress<double>(p => Progress = p));
            Status = "Inštalátor je overený (SHA-256 a podpis LINET Services GmbH). Spúšťam ho…";
            if (!await MkvToolNixInstaller.RunAsync(installer, silent: false))
            {
                Status = "Inštalácia bola zrušená v okne Windows.";
                return;
            }

            TryDelete(installer);
        }
        catch (SetupException ex)
        {
            Status = ex.Message;
            return;
        }
        finally
        {
            Busy = false;
        }

        await RefreshAsync();
    }

    private async Task ChooseFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Priečinok s MKVToolNix (obsahuje mkvmerge.exe)" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Busy = true;
        try
        {
            var installation = await _services.UseMkvToolNixDirectoryAsync(dialog.FolderName);
            Status = installation is null
                ? "Vo vybranom priečinku nie sú funkčné nástroje MKVToolNix (mkvmerge.exe, mkvpropedit.exe)."
                : $"MKVToolNix {installation.Version} je pripravený.";
            Details = installation?.Directory ?? dialog.FolderName;
            OnPropertyChanged(nameof(IsReady));
        }
        finally
        {
            Busy = false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the temporary installer stays in %TEMP%
        }
    }
}
