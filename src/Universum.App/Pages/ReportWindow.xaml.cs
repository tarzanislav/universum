using System.Windows;

namespace Universum.App.Pages;

/// <summary>Tlač a export: the choices on the left, the preview of the pages on the right.</summary>
public partial class ReportWindow : Window
{
    public ReportWindow(ReportViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }
}
