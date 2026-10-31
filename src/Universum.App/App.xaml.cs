using System.Windows;
using System.Windows.Threading;
using Universum.App.SelfTesting;
using Universum.App.Setup;
using Universum.App.Shell;
using Universum.Core.Shell;

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
        Universum.Platform.Windows.WindowsVolumes.Register();
        var services = new AppServices();
        Localization.Loc.Init(services.Settings.UiLanguage);

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

        // one UNIVERSUM per user: a second start (each file selected in the Explorer starts one) hands its request over;
        // the first one listens at once (requests wait until its window is ready)
        _instance = SingleInstance.TryAcquire();
        if (_instance is null && SingleInstance.Forward(e.Args, TimeSpan.FromSeconds(10)))
        {
            Shutdown(0);
            return;
        }

        var ready = new TaskCompletionSource<(MainWindow Window, MainViewModel Model)>();
        _instance?.Listen(args => Dispatcher.InvokeAsync(async () =>
        {
            var (shown, handler) = await ready.Task;
            try
            {
                shown.BringToFront();
                await handler.HandleAsync(ShellRequest.Parse(args));
            }
            catch (Exception ex)
            {
                // nobody waits for this task: the error is shown here, as the first start's would be
                MessageBox.Show(shown, "Súbor z Prieskumníka sa nepodarilo otvoriť.\n\n" + ex.Message, "UNIVERSUM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }));

        var model = new MainViewModel(services);
        var window = new MainWindow(model);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        ready.SetResult((window, model));

        // the context menu of the Explorer: Zlučovanie, Hromadné spracovanie, Single spracovanie, Informácie
        await model.StartAsync(ShellRequest.Parse(e.Args));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    private SingleInstance? _instance;

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Nastala neočakávaná chyba. Program môže pokračovať, ale posledná akcia sa nemusela dokončiť.\n\n{e.Exception.Message}",
            "UNIVERSUM", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
