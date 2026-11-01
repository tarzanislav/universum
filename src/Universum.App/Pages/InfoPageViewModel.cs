using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Localization;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;

namespace Universum.App.Pages;

/// <summary>A file another page works with, offered on the Information page.</summary>
/// <param name="Group">Where it comes from: "Zlučovanie – Film.mkv", "Hromadné spracovanie – Seriál".</param>
public sealed record KnownFile(string Group, string Name, string Path);

/// <summary>
/// Information tool: structure of one file (tracks, chapters, tags, attachments). The files added in the multiplexer
/// and analysed in the batch are listed beside it, so each can be looked at with one click.
/// </summary>
public sealed class InfoPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private KnownFile? _selectedKnown;
    private bool _selecting;
    private int _load;
    private string _fileName = "";
    private string _summary = Loc.T("Otvorte súbor MKV alebo ho sem pretiahnite myšou.");
    private string _error = "";

    public InfoPageViewModel(AppServices services)
    {
        _services = services;
        OpenCommand = new AsyncRelayCommand(OpenAsync);
    }

    public AsyncRelayCommand OpenCommand { get; }

    /// <summary>Gives the files the other pages work with now (set by the main window).</summary>
    public Func<IEnumerable<KnownFile>> KnownSource { get; set; } = () => [];

    /// <summary>Files from the multiplexer and the batch.</summary>
    public ObservableCollection<KnownFile> KnownFiles { get; } = [];

    public bool HasKnownFiles => KnownFiles.Count > 0;

    /// <summary>Choosing a known file shows it.</summary>
    public KnownFile? SelectedKnown
    {
        get => _selectedKnown;
        set
        {
            if (SetProperty(ref _selectedKnown, value) && value is not null && !_selecting)
            {
                _ = LoadAsync(value.Path);
            }
        }
    }

    /// <summary>Reads the known files again (when the page is opened).</summary>
    public void RefreshKnown()
    {
        var path = _selectedKnown?.Path ?? File?.Path;
        KnownFiles.Clear();
        foreach (var known in KnownSource().DistinctBy(k => k.Path, StringComparer.OrdinalIgnoreCase))
        {
            KnownFiles.Add(known);
        }

        Select(path);
        OnPropertyChanged(nameof(HasKnownFiles));
    }

    public ObservableCollection<TrackRow> Tracks { get; } = [];

    public MediaFile? File { get; private set; }

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    public async Task LoadAsync(string path)
    {
        // a later choice wins: an earlier file that is read more slowly does not show over it
        var load = ++_load;
        Error = "";
        Summary = Loc.T("Načítavam…");
        Tracks.Clear();
        File = null;
        FileName = Path.GetFileName(path);
        Select(path);
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (load != _load)
        {
            return;
        }

        if (installation is null)
        {
            Error = Loc.T("MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.");
            return;
        }

        try
        {
            var file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
            if (load != _load)
            {
                return;
            }

            File = file;
            Tracks.Clear();
            foreach (var track in file.Tracks)
            {
                Tracks.Add(new TrackRow(track));
            }

            Summary = Loc.F("{0}, trvanie {1:hh\\:mm\\:ss}, stopy: {2}, kapitoly: {3}, tagy: {4}, prílohy: {5}", file.ContainerType, file.Duration,
                          file.Tracks.Count, file.ChapterEntries, file.GlobalTagEntries + file.Tracks.Sum(t => t.TagEntries), file.AttachmentCount) +
                      (file.Title is null ? "" : "\n" + Loc.F("Titul: {0}", file.Title));
            if (file.Errors.Count > 0 || file.Warnings.Count > 0)
            {
                Error = string.Join("\n", file.Errors.Concat(file.Warnings));
            }
        }
        catch (Exception ex) when (ex is MkvToolNixException or IOException or Core.Processes.ProcessStartException or TimeoutException)
        {
            if (load == _load)
            {
                Error = ex.Message;
            }
        }
    }

    // marks the known file shown without loading it again
    private void Select(string? path)
    {
        _selecting = true;
        try
        {
            SelectedKnown = path is null ? null : KnownFiles.FirstOrDefault(k => Core.IO.Paths.Same(k.Path, path));
        }
        finally
        {
            _selecting = false;
        }
    }

    private async Task OpenAsync()
    {
        var patterns = string.Join(";", Universum.Core.Batch.BatchAnalyzer.VideoExtensions.Concat([".mka", ".mks"]).Select(e => "*" + e));
        var dialog = new OpenFileDialog { Filter = Loc.T("Video a Matroska") + $" (MKV, MP4, AVI, TS, M2TS, MOV, MPG, WebM…)|{patterns}|" + Loc.T("Všetky súbory") + "|*.*" };
        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(dialog.FileName);
        }
    }
}
