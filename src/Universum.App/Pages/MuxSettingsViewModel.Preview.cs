using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Library;
using Universum.Core.Muxing;
using Range = Universum.Core.Muxing.PreviewRange;

namespace Universum.App.Pages;

/// <summary>
/// Zlučovanie: a preview and MEGA spracovanie (owner, 1. 11. 2026). Several merged results can be made (a tab each) and
/// looked at in a player before the final one goes to MEGA spracovanie; the previews are written into UNIVERSUM's own
/// folder of previews – never into the output folders – and deleted with their tab or after a week.
/// </summary>
public sealed partial class MuxSettingsViewModel
{
    private CancellationTokenSource? _previewing;
    private string? _previewPath;
    private string? _previewRecipe;
    private string _previewText = "";
    private double _previewProgress;
    private int _previews;

    public static IReadOnlyList<VideoChoice<Range>> PreviewRanges { get; } =
    [
        new(Range.Samples, "úseky: začiatok, stred, koniec (3 × 2 min)"),
        new(Range.Whole, "celý film"),
    ];

    /// <summary>How much the preview writes (kept in the settings).</summary>
    public VideoChoice<Range> PreviewRange
    {
        get => PreviewRanges.FirstOrDefault(r => r.Value.ToString() == _services.Settings.MuxPreviewRange) ?? PreviewRanges[0];
        set
        {
            if (value is not null && value.Value.ToString() != _services.Settings.MuxPreviewRange)
            {
                _services.Settings.MuxPreviewRange = value.Value.ToString();
                _services.Settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool IsPreviewing => _previewing is not null;

    public double PreviewProgress { get => _previewProgress; private set => SetProperty(ref _previewProgress, value); }

    /// <summary>What the preview is, or what happened.</summary>
    public string PreviewText { get => _previewText; private set => SetProperty(ref _previewText, value); }

    /// <summary>The preview written last (in the folder of previews).</summary>
    public string? PreviewPath => _previewPath is { } path && File.Exists(path) ? path : null;

    public bool HasPreview => PreviewPath is not null;

    /// <summary>The settings changed since the preview was made: it does not show the result any more.</summary>
    public bool PreviewIsOld => HasPreview && _previewRecipe != Recipe(Spec());

    private AsyncRelayCommand? _previewCommand;
    private RelayCommand? _cancelPreviewCommand;
    private RelayCommand? _playPreviewCommand;
    private RelayCommand? _openPreviewCommand;
    private RelayCommand? _sendToMegaCommand;
    private MegaSender? _mega;

    /// <summary>Vytvoriť náhľad: the settings written into the folder of previews (parts or the whole film).</summary>
    public AsyncRelayCommand PreviewCommand => _previewCommand ??= new AsyncRelayCommand(PreviewAsync, () => CanWrite() && !IsPreviewing);

    public RelayCommand CancelPreviewCommand => _cancelPreviewCommand ??= new RelayCommand(() => _previewing?.Cancel(), () => IsPreviewing);

    /// <summary>Prehrať náhľad: in UNIVERSUM's player with the subtitles of the result.</summary>
    public RelayCommand PlayPreviewCommand => _playPreviewCommand ??= new RelayCommand(() => (PlayRequested ?? Play)(PreviewPath!), () => HasPreview && !IsPreviewing);

    /// <summary>V inom prehrávači: the preview opened by the player Windows has for MKV.</summary>
    public RelayCommand OpenPreviewCommand => _openPreviewCommand ??= new RelayCommand(OpenPreview, () => HasPreview && !IsPreviewing);

    /// <summary>Where Odoslať do MEGA sends the result: a new batch or a waiting one.</summary>
    public MegaSender Mega => _mega ??= new MegaSender(_services);

    /// <summary>Odoslať do MEGA: the final result's recipe, written later by MEGA spracovanie.</summary>
    public RelayCommand SendToMegaCommand => _sendToMegaCommand ??= new RelayCommand(SendToMega, () => CanWrite() && _services.Library is not null);

    /// <summary>Opens a preview in UNIVERSUM's player (set by the page; the self-test answers itself).</summary>
    public Action<string>? PlayRequested { get; set; }

    /// <summary>Writes a preview of the settings as they are now.</summary>
    public async Task PreviewAsync()
    {
        var spec = Spec();
        if (ProblemList(spec).Count > 0 || _services.CurrentMkvToolNix is not { } installation)
        {
            return;
        }

        var folder = _services.PreviewFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PreviewText = "Priečinok náhľadov sa nedá vytvoriť: " + ex.Message;
            return;
        }

        var title = Library.Title?.Name ?? Path.GetFileNameWithoutExtension(DisplayName);
        var output = Path.Combine(folder, MuxPreview.FileName(title, ++_previews, DateTime.Now));
        var preview = MuxPreview.Of(spec, output, PreviewRange.Value);
        _previewing = new CancellationTokenSource();
        RefreshPreview();
        PreviewProgress = 0;
        PreviewText = preview.PreviewParts is null ? "Zapisujem náhľad celého filmu…" : "Zapisujem náhľad – tri úseky po dve minúty…";
        try
        {
            var result = await new Multiplexer(installation, _services.Runner).MuxAsync(preview, new Progress<double>(p => PreviewProgress = p), _previewing.Token);
            if (result.Status == MuxStatus.Done && result.OutputPath is { } written)
            {
                // the previous preview of this tab is not needed any more
                Forget(_previewPath);
                _previewPath = written;
                _previewRecipe = Recipe(spec);
                PreviewText = (preview.PreviewParts is null ? "Náhľad celého filmu" : "Náhľad úsekov (začiatok, stred, koniec)") +
                              $" je hotový ({new FileInfo(written).Length / (1024 * 1024.0):0.#} MB) – prehrajte ho; keď je výsledok v poriadku, odošlite ho do MEGA.";
            }
            else
            {
                PreviewText = result.Status == MuxStatus.Cancelled ? "Náhľad sa zastavil – nič sa nezapísalo." : "Náhľad sa nepodarilo zapísať: " + string.Join(" | ", result.Messages);
            }
        }
        catch (OperationCanceledException)
        {
            PreviewText = "Náhľad sa zastavil – nič sa nezapísalo.";
        }
        finally
        {
            _previewing.Dispose();
            _previewing = null;
            RefreshPreview();
        }
    }

    /// <summary>Sends the final result to MEGA spracovanie (the recipe of the whole result, not the preview).</summary>
    public void SendToMega()
    {
        var spec = Spec();
        if (ProblemList(spec).Count > 0 || _services.Library is not { } library)
        {
            return;
        }

        var title = Library.Title ?? new TitleInfo(MediaKind.Movie, Path.GetFileNameWithoutExtension(DisplayName), null);
        var planned = spec.Placement is { } placement ? Path.Combine(placement.Directory, placement.Prefix) + " …" + placement.Extension : spec.OutputPath;
        var file = Core.Mega.MegaFile.FromMux(spec, title, Library.Episode, planned);
        Result = Mega.Send([file], "Zlučovanie: " + title.Name + (Library.Episode is { } episode ? " " + episode.Tag : ""), library.Root, _services.Profile) +
                 (PreviewIsOld ? " (Pozor: nastavenia sa od náhľadu zmenili.)" : "");
        IsModified = false;
        OnPropertyChanged(nameof(IsUnprocessed));
    }

    /// <summary>The tab is closed: its preview goes too.</summary>
    public void ForgetPreview()
    {
        _previewing?.Cancel();
        Forget(_previewPath);
        _previewPath = null;
        RefreshPreview();
    }

    private void Play(string path) =>
        new PreviewWindow(new PreviewViewModel(_services, path)) { Owner = System.Windows.Application.Current.MainWindow }.Show();

    private void OpenPreview()
    {
        if (PreviewPath is { } path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                PreviewText = "Windows nemá prehrávač pre MKV: " + ex.Message;
            }
        }
    }

    // UNIVERSUM's own preview, deleted (never anything outside the folder of previews)
    private void Forget(string? path)
    {
        if (path is null || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(_services.PreviewFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still open in a player: deleted with the old previews later
        }
    }

    // what the result is made of (the preview shows these settings as long as this is the same)
    private static string Recipe(MuxSpec spec)
    {
        try
        {
            return JsonSerializer.Serialize(spec with { PreviewParts = null });
        }
        catch (NotSupportedException)
        {
            return Guid.NewGuid().ToString();
        }
    }

    private void RefreshPreview()
    {
        OnPropertyChanged(nameof(IsPreviewing));
        OnPropertyChanged(nameof(PreviewPath));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(PreviewIsOld));
        PreviewCommand.NotifyCanExecuteChanged();
        CancelPreviewCommand.NotifyCanExecuteChanged();
        PlayPreviewCommand.NotifyCanExecuteChanged();
        OpenPreviewCommand.NotifyCanExecuteChanged();
        SendToMegaCommand.NotifyCanExecuteChanged();
    }
}
