using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Universum.App.Shell;

/// <summary>
/// A short notice in the corner of the screen that takes no focus (owner, 8. 11. 2026: a film sent from the browser to
/// the Zásobník TMDB is confirmed without taking the browser away). Several in a row share one notice.
/// </summary>
public static class TrayToast
{
    private static Window? _window;
    private static TextBlock? _text;
    private static DispatcherTimer? _timer;
    private static int _count;

    /// <summary>The texts shown (the self-test reads them).</summary>
    public static List<string> Shown { get; } = [];

    public static void Show(string message)
    {
        Shown.Add(message);
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        dispatcher.Invoke(() =>
        {
            if (_window is null)
            {
                _text = new TextBlock { Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
                _window = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Topmost = true,
                    Focusable = false,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Content = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x01, 0x6D, 0x8C)),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(16, 10, 16, 10),
                        Child = _text,
                    },
                };
                _window.MouseLeftButtonUp += (_, _) => Hide();
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _timer.Tick += (_, _) => Hide();
            }

            _count++;
            _text!.Text = _count > 1 ? message + "\n" + Localization.Loc.F("(spolu {0} za chvíľu)", _count) : message;
            _window.Show();
            _window.UpdateLayout();
            var area = SystemParameters.WorkArea;
            _window.Left = area.Right - _window.ActualWidth - 16;
            _window.Top = area.Bottom - _window.ActualHeight - 16;
            _timer!.Stop();
            _timer.Start();
        });
    }

    private static void Hide()
    {
        _timer?.Stop();
        _window?.Hide();
        _count = 0;
    }
}
