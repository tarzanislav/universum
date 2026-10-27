using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Universum.App.Pages;

/// <summary>"Pridávanie súborov" (MKVToolNix GUI: Adding multiple files): what to do with files added to the multiplexer.</summary>
public partial class MuxAddFilesWindow : Window
{
    public MuxAddFilesWindow(MuxAddFilesModel model)
    {
        InitializeComponent();
        DataContext = model;
        Model = model;
    }

    public MuxAddFilesModel Model { get; }

    /// <summary>Shows the dialog over the main window; null when cancelled.</summary>
    public static MuxAddChoice? Ask(MuxAddRequest request)
    {
        var window = new MuxAddFilesWindow(new MuxAddFilesModel(request)) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.Model.Choice() : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}

/// <summary>The choices of the add-files dialog.</summary>
public sealed class MuxAddFilesModel : ObservableObject
{
    private MuxAddAction _action;
    private MuxInputRow? _target;
    private bool _newForVideo;
    private bool _remember;

    public MuxAddFilesModel(MuxAddRequest request)
    {
        Sources = request.Sources;
        Heading = request.Files.Count == 1
            ? $"Pridávanie súboru {System.IO.Path.GetFileName(request.Files[0])}"
            : $"Pridávanie {request.Files.Count} súborov";
        // as MKVToolNix: into the current settings when it has sources, otherwise new settings for each file
        _action = request.Sources.Count > 0 ? MuxAddAction.AddToCurrent : MuxAddAction.NewSettingsForEach;
        _target = request.Sources.LastOrDefault();
    }

    public string Heading { get; }

    public IReadOnlyList<MuxInputRow> Sources { get; }

    public bool HasSources => Sources.Count > 0;

    public bool AddToCurrent { get => _action == MuxAddAction.AddToCurrent; set => Set(value, MuxAddAction.AddToCurrent); }

    public bool NewForAll { get => _action == MuxAddAction.NewSettingsForAll; set => Set(value, MuxAddAction.NewSettingsForAll); }

    public bool NewForEach { get => _action == MuxAddAction.NewSettingsForEach; set => Set(value, MuxAddAction.NewSettingsForEach); }

    public bool Append { get => _action == MuxAddAction.Append; set => Set(value, MuxAddAction.Append); }

    public bool Parts { get => _action == MuxAddAction.AdditionalParts; set => Set(value, MuxAddAction.AdditionalParts); }

    public bool NeedsTarget => _action is MuxAddAction.Append or MuxAddAction.AdditionalParts;

    public MuxInputRow? Target { get => _target; set { if (SetProperty(ref _target, value)) { OnPropertyChanged(nameof(CanConfirm)); } } }

    public bool NewForVideo { get => _newForVideo; set => SetProperty(ref _newForVideo, value); }

    /// <summary>Appending and parts depend on the files at hand: they are never remembered.</summary>
    public bool CanRemember => !NeedsTarget;

    public bool Remember { get => _remember && CanRemember; set => SetProperty(ref _remember, value); }

    public bool CanConfirm => !NeedsTarget || _target is not null;

    public MuxAddChoice Choice() => new(_action, NeedsTarget ? _target : null, _newForVideo, Remember);

    private void Set(bool selected, MuxAddAction action)
    {
        if (!selected || _action == action)
        {
            return;
        }

        _action = action;
        OnPropertyChanged(string.Empty);
    }
}
