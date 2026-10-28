using System.Windows;

namespace Universum.App.Pages;

/// <summary>Import of HISTRA lists: the user's lists with their names and preview posters, to tick.</summary>
public partial class HistraImportWindow : Window
{
    public HistraImportWindow(HistraImportViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    private void OnImport(object sender, RoutedEventArgs e) => DialogResult = true;
}
