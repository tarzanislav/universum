using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Universum.App.Pages;

/// <summary>
/// The tabs of the multiplexer (one per multiplex settings) from left to right; when they do not fit, arrows scroll
/// them (also the mouse wheel), and the current tab is always scrolled into view.
/// </summary>
public partial class MuxTabStrip : UserControl
{
    private const double Step = 160;
    private MultiplexerPageViewModel? _model;

    public MuxTabStrip()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelChanged;
            }

            _model = DataContext as MultiplexerPageViewModel;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelChanged;
            }
        };
        SizeChanged += (_, _) => UpdateArrows();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MultiplexerPageViewModel.Current))
        {
            // after the new tab is laid out
            Dispatcher.BeginInvoke(() =>
            {
                if (_model?.Current is { } current && Tabs.ItemContainerGenerator.ContainerFromItem(current) is FrameworkElement tab)
                {
                    tab.BringIntoView();
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateArrows();

    private void UpdateArrows()
    {
        var overflow = Scroller.ExtentWidth > Scroller.ViewportWidth + 1;
        LeftButton.Visibility = RightButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        LeftButton.IsEnabled = Scroller.HorizontalOffset > 0.5;
        RightButton.IsEnabled = Scroller.HorizontalOffset < Scroller.ScrollableWidth - 0.5;
    }

    private void OnLeft(object sender, RoutedEventArgs e) => Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - Step);

    private void OnRight(object sender, RoutedEventArgs e) => Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + Step);

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }
}
