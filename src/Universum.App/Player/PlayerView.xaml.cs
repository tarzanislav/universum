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

    // Space plays or pauses also after a click on a button of the player (a button would take Space itself); not in a text box
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.OriginalSource is not TextBox && DataContext is PlayerViewModel { PlayPauseCommand: var command } && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
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
