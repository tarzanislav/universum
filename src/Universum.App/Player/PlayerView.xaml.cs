using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Universum.App.Player;

/// <summary>The player on a page: it draws with the window (a picture each frame of WPF) and pauses when the page is left.</summary>
public partial class PlayerView : UserControl
{
    public PlayerView()
    {
        InitializeComponent();
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            (DataContext as PlayerViewModel)?.Pause();
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space && DataContext is PlayerViewModel { PlayPauseCommand: var command } && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void OnRendering(object? sender, EventArgs e) => (DataContext as PlayerViewModel)?.Tick();

    private void OnPictureClick(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (DataContext is PlayerViewModel { PlayPauseCommand: var command } && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
