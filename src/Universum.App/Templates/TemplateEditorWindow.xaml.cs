using System.Windows;

namespace Universum.App.Templates;

public partial class TemplateEditorWindow : Window
{
    public TemplateEditorWindow(TemplateEditorViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
