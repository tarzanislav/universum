using System.IO;
using System.Windows;

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

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]))
        {
            if (Model.SelectedTool.Page == Model.Rules)
            {
                await Model.Rules.LoadAsync(files[0]);
                return;
            }

            Model.SelectedTool = Model.Tools.First(t => t.Page == Model.Info);
            await Model.Info.LoadAsync(files[0]);
        }
    }
}
