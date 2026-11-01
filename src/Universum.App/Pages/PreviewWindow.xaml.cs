using System.Windows;

namespace Universum.App.Pages;

/// <summary>A preview of Zlučovanie in UNIVERSUM's player; the player is closed with the window.</summary>
public partial class PreviewWindow : Window
{
    public PreviewWindow(PreviewViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Loaded += async (_, _) => await model.OpenAsync();
        Closed += async (_, _) => await model.CloseAsync();
    }
}
