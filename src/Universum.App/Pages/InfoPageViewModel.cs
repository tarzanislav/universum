using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;

namespace Universum.App.Pages;

/// <summary>Information tool: structure of one file (tracks, chapters, tags, attachments).</summary>
public sealed class InfoPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _fileName = "";
    private string _summary = "Otvorte súbor MKV alebo ho sem pretiahnite myšou.";
    private string _error = "";

    public InfoPageViewModel(AppServices services)
    {
        _services = services;
        OpenCommand = new AsyncRelayCommand(OpenAsync);
    }

    public AsyncRelayCommand OpenCommand { get; }

    public ObservableCollection<TrackRow> Tracks { get; } = [];

    public MediaFile? File { get; private set; }

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    public async Task LoadAsync(string path)
    {
        Error = "";
        Tracks.Clear();
        File = null;
        FileName = Path.GetFileName(path);
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Error = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            return;
        }

        try
        {
            var file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
            File = file;
            foreach (var track in file.Tracks)
            {
                Tracks.Add(new TrackRow(track));
            }

            Summary = $"{file.ContainerType}, trvanie {file.Duration:hh\\:mm\\:ss}, " +
                      $"stopy: {file.Tracks.Count}, kapitoly: {file.ChapterEntries}, " +
                      $"tagy: {file.GlobalTagEntries + file.Tracks.Sum(t => t.TagEntries)}, prílohy: {file.AttachmentCount}" +
                      (file.Title is null ? "" : $"\nTitul: {file.Title}");
            if (file.Errors.Count > 0 || file.Warnings.Count > 0)
            {
                Error = string.Join("\n", file.Errors.Concat(file.Warnings));
            }
        }
        catch (Exception ex) when (ex is MkvToolNixException or IOException or Core.Processes.ProcessStartException or TimeoutException)
        {
            Error = ex.Message;
        }
    }

    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Súbory Matroska (*.mkv;*.mka;*.mks;*.webm)|*.mkv;*.mka;*.mks;*.webm|Všetky súbory|*.*" };
        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(dialog.FileName);
        }
    }
}
