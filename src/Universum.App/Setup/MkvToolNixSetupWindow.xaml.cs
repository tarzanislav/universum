using System.Windows;

namespace Universum.App.Setup;

public partial class MkvToolNixSetupWindow : Window
{
    public MkvToolNixSetupWindow(MkvToolNixSetupViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Loaded += async (_, _) => await model.RefreshAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
