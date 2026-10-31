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
    // Časy titulkov: only the text of a subtitle that can be changed (subtitles read as text, no repair of the whole text);
    // a double click goes to the subtitle, it does not edit
    private void OnCueBeginningEdit(object sender, System.Windows.Controls.DataGridBeginningEditEventArgs e) =>
        e.Cancel = e.Row.Item is not SubtitlePreviewRow { IsEditable: true } || e.EditingEventArgs is MouseButtonEventArgs { ClickCount: >= 2 };

    private void OnCueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGridRow { Item: SubtitlePreviewRow cue } row &&
            System.Windows.Controls.ItemsControl.ItemsControlFromItemContainer(row)?.DataContext is SubtitlesPageViewModel page)
        {
            page.SeekToCueCommand.Execute(cue);
        }
    }

    // the list is made anew while a text is being edited (a change of time, a track read): the edit ends first – a list
    // in the middle of an edit cannot be replaced
    private void OnCueGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid { DataContext: SubtitlesPageViewModel page } grid && !_cueGrids.Contains(grid))
        {
            _cueGrids.Add(grid);
            page.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(SubtitlesPageViewModel.Preview) && !grid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true))
                {
                    grid.CancelEdit(System.Windows.Controls.DataGridEditingUnit.Row);
                }
            };
        }
    }

    // OCR: the answer is typed at once – the box takes the focus when it appears and with every next shape asked
    private void OnOcrAnswerVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { DataContext: SubtitlesPageViewModel page } box || e.NewValue is not true)
        {
            return;
        }

        if (_ocrBoxes.Add(box))
        {
            page.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(SubtitlesPageViewModel.OcrQuestion) && box.IsVisible)
                {
                    Dispatcher.BeginInvoke(() => box.Focus());
                }
            };
        }

        Dispatcher.BeginInvoke(() => box.Focus());
    }

    // the row's edit ends with the text's, before the list is made anew with the changed text
    private void OnCueCellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == System.Windows.Controls.DataGridEditAction.Commit && sender is System.Windows.Controls.DataGrid grid)
        {
            Dispatcher.BeginInvoke(() => grid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true));
        }
    }

    // the subtitle chosen (also when the list was made anew) is in sight
    private void OnCueSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid { SelectedItem: { } chosen } grid)
        {
            grid.ScrollIntoView(chosen);
        }
    }

    private readonly HashSet<System.Windows.Controls.DataGrid> _cueGrids = [];
    private readonly HashSet<System.Windows.Controls.TextBox> _ocrBoxes = [];

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

    // Hromadné spracovanie, Stopy skupiny: a track is dragged by its grip onto the place of another track of its type
    private void OnSlotGripDown(object sender, MouseButtonEventArgs e) => _listDragStart = e.GetPosition(this);

    private void OnSlotGripMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement { DataContext: SlotRow row } grip && !row.IsRemovedByLanguage &&
            (e.GetPosition(this) - _listDragStart).Length > SystemParameters.MinimumVerticalDragDistance)
        {
            DragDrop.DoDragDrop(grip, new DataObject(typeof(SlotRow), row), DragDropEffects.Move);
        }
    }

    private void OnSlotDragOver(object sender, DragEventArgs e)
    {
        // files dropped from Explorer go on to the window
        if (!e.Data.GetDataPresent(typeof(SlotRow)))
        {
            return;
        }

        e.Effects = e.Data.GetData(typeof(SlotRow)) is SlotRow dragged && SlotUnder(e) is { } target &&
                    target.Key.Type == dragged.Key.Type && !target.IsRemovedByLanguage
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSlotDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SlotRow)) is SlotRow dragged && SlotUnder(e) is { } target)
        {
            Model.Batch.MoveSlotTo(dragged, target);
            e.Handled = true;
        }
    }

    // Single spracovanie: the same for the tracks of the opened file
    private void OnRuleGripDown(object sender, MouseButtonEventArgs e) => _listDragStart = e.GetPosition(this);

    private void OnRuleGripMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement { DataContext: RuleRow { CanMove: true } row } grip &&
            (e.GetPosition(this) - _listDragStart).Length > SystemParameters.MinimumVerticalDragDistance)
        {
            DragDrop.DoDragDrop(grip, new DataObject(typeof(RuleRow), row), DragDropEffects.Move);
        }
    }

    private void OnRuleDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(RuleRow)))
        {
            return;
        }

        e.Effects = e.Data.GetData(typeof(RuleRow)) is RuleRow dragged && RowUnder<RuleRow>(e) is { CanMove: true } target &&
                    target.Target.Type == dragged.Target.Type
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnRuleDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(RuleRow)) is RuleRow dragged && RowUnder<RuleRow>(e) is { } target)
        {
            Model.Rules.MoveTrackTo(dragged.TrackId, target.TrackId);
            e.Handled = true;
        }
    }

    // the item of the grid row under the mouse
    private static T? RowUnder<T>(DragEventArgs e)
        where T : class
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is System.Windows.Controls.DataGridRow { DataContext: T row })
            {
                return row;
            }
        }

        return null;
    }

    private static SlotRow? SlotUnder(DragEventArgs e) => RowUnder<SlotRow>(e);

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

            if (Model.SelectedTool.Page == Model.Subtitles)
            {
                await Model.Subtitles.LoadAsync(files.Where(File.Exists).ToList());
                return;
            }

            if (Model.SelectedTool.Page == Model.Audio)
            {
                await Model.Audio.LoadAsync(files.Where(File.Exists).ToList());
                return;
            }

            if (Model.SelectedTool.Page == Model.Video)
            {
                await Model.Video.LoadAsync(files.Where(File.Exists).ToList());
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
