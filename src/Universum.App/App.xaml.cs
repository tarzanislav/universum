using System.Windows;
using System.Windows.Threading;
using Universum.App.SelfTesting;
using Universum.App.Setup;
using Universum.App.Shell;

namespace Universum.App;

public partial class App : Application
{
    /// <summary>Command-line switch used by the installer: make sure MKVToolNix is installed.</summary>
    public const string PrepareMkvToolNixSwitch = "--priprav-mkvtoolnix";

    /// <summary>Command-line switch used by CI: run the self-test and exit with 0 (passed) or 1.</summary>
    public const string SelfTestSwitch = "--samotest";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        var services = new AppServices();

        if (e.Args.Contains(SelfTestSwitch, StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(await SelfTest.RunAsync(services));
            return;
        }

        if (e.Args.Contains(PrepareMkvToolNixSwitch, StringComparer.OrdinalIgnoreCase))
        {
            // exit code 0 = a usable MKVToolNix is present afterwards; the installer reads it
            var installation = await services.FindMkvToolNixAsync();
            if (installation is null || !installation.IsSupportedVersion)
            {
                new MkvToolNixSetupWindow(new MkvToolNixSetupViewModel(services)).ShowDialog();
                installation = services.CurrentMkvToolNix;
            }

            Shutdown(installation is { IsSupportedVersion: true } ? 0 : 1);
            return;
        }

        var window = new MainWindow(new MainViewModel(services));
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Nastala neočakávaná chyba. Program môže pokračovať, ale posledná akcia sa nemusela dokončiť.\n\n{e.Exception.Message}",
            "UNIVERSUM", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
