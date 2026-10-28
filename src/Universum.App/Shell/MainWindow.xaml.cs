using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Universum.App.Pages;

namespace Universum.App.Shell;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Model = model;
    }

    public MainViewModel Model { get; }

    /// <summary>Another start handed its request over: the window comes to the front.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Closing asks when work would be lost (the self-test closes without asking).</summary>
    public static bool ConfirmOnClose { get; set; } = true;

    private bool _stopping;
    private bool _stopped;

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _stopped || !ConfirmOnClose || Model.CloseWarning() is not { } warning)
        {
            return;
        }

        // the window stays until the answer – and, when work runs, until it has stopped
        e.Cancel = true;
        if (_stopping || MessageBox.Show(this, warning, "UNIVERSUM", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }

        _stopping = true;
        IsEnabled = false;
        Title = "UNIVERSUM – zastavujem prácu…";
        await Model.StopWorkAsync(TimeSpan.FromSeconds(20));
        _stopped = true;

        // not from inside this Closing (WPF refuses Close() while the window is closing): after it has returned
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    // the button's menu opens on a click (not only with the right button), with the page as its data
    private void OnSpeechMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.DataContext = button.DataContext;
            menu.IsOpen = true;
        }
    }

    // several files can be selected in batch processing (Ctrl, Shift) – for listening to their audio
    private void OnBatchFilesSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid)
        {
            Model.Batch.SelectFiles(grid.SelectedItems.OfType<Pages.FileRow>());
        }
    }

    // Novinky: an opened series shows the tree of its episodes (a binding of the row itself – a style's would lose to
    // the grid's details mode)
    private void OnNewsLoadingRow(object sender, System.Windows.Controls.DataGridRowEventArgs e) =>
        e.Row.SetBinding(System.Windows.Controls.DataGridRow.DetailsVisibilityProperty,
            new System.Windows.Data.Binding(nameof(NewsSeriesRow.IsExpanded)) { Converter = new System.Windows.Controls.BooleanToVisibilityConverter() });

    private Point _listDragStart;

    // Listy z HISTRA: a list is dragged by its grip and dropped on another list's place
    private void OnListGripDown(object sender, MouseButtonEventArgs e) => _listDragStart = e.GetPosition(this);

    private void OnListGripMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement { DataContext: HistraListRow row } grip &&
            (e.GetPosition(this) - _listDragStart).Length > SystemParameters.MinimumVerticalDragDistance)
        {
            DragDrop.DoDragDrop(grip, new DataObject(typeof(HistraListRow), row), DragDropEffects.Move);
        }
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(HistraListRow)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(HistraListRow)) is HistraListRow dragged && sender is FrameworkElement { DataContext: HistraListRow target })
        {
            Model.HistraLists.MoveTo(dragged, Model.HistraLists.Lists.IndexOf(target));
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Model.SelectedTool.Page == Model.Mux && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } inputs)
        {
            await Model.Mux.AddFilesAsync(inputs.Where(File.Exists));
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } dropped && Directory.Exists(dropped[0]))
        {
            Model.SelectedTool = Model.Tools.First(t => t.Page == Model.Batch);
            Model.Batch.Folder = dropped[0];
            await Model.Batch.AnalyzeAsync();
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]))
        {
            if (Model.SelectedTool.Page == Model.Rules)
            {
                await Model.Rules.LoadAsync(files[0]);
                return;
            }

            if (Model.SelectedTool.Page == Model.Chapters)
            {
                await Model.Chapters.LoadAsync(files[0]);
                return;
            }

            if (Model.SelectedTool.Page == Model.HeaderEditor)
            {
                await Model.HeaderEditor.LoadAsync(files[0]);
                return;
            }

            Model.SelectedTool = Model.Tools.First(t => t.Page == Model.Info);
            await Model.Info.LoadAsync(files[0]);
        }
    }
}
