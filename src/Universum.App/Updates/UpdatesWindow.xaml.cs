using System.Windows;

namespace Universum.App.Updates;

public partial class UpdatesWindow : Window
{
    public UpdatesWindow(UpdatesViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.CloseRequested += Close;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
