using System.IO;
using System.Net.Http;
using System.Text.Json;
using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Library.Histra;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Processes;
using Universum.Core.Speech;
using Universum.Core.Subtitles;
using Universum.Core.Weather;
using Universum.Platform.Windows;
using Universum.App.Localization;

namespace Universum.App;

// AppServices: the input and output folders on the disks, the watch of the input, the libraries
public sealed partial class AppServices
{
    /// <summary>
    /// The input folder (owner, 28. 10. 2026): where new films and episodes come; Novinky look for chosen episodes there
    /// (with its subfolders) and the file dialogs of Single and batch processing start in it. Files there are only read.
    /// </summary>
    public string? InputDirectory => Settings.InputDirectory;

    /// <summary>
    /// Every input folder (owner, 8. 11. 2026 – an input folder on every disk): the first one, then those on other disks.
    /// New films are watched, episodes and titles looked for in all of them.
    /// </summary>
    public IReadOnlyList<string> InputDirectories =>
        [.. new[] { Settings.InputDirectory }.Concat(Settings.OtherInputDirectories).OfType<string>().Where(f => f.Trim().Length > 0).Distinct(Universum.Core.IO.Paths.Comparer)];

    /// <summary>Every output folder: the default one, then those on other disks.</summary>
    public IReadOnlyList<string> OutputDirectories =>
        [.. new[] { Settings.OutputDirectory }.Concat(Settings.OtherOutputDirectories).OfType<string>().Where(f => f.Trim().Length > 0)];

    // an input folder is never in the results, nor an output folder, nor a second one on its disk (except the one it replaces)
    private string CheckedInput(string folder, string? replacing)
    {
        folder = Path.GetFullPath(folder);
        if (Libraries?.ResultFolders.Any(r => Universum.Core.IO.Paths.IsUnder(folder, r)) == true)
        {
            throw new ArgumentException(Loc.T("Vstupný priečinok nemôže byť v MOVIES ani v TVSHOWS knižnice – tam sú výsledky."));
        }

        if (Universum.Core.Library.OutputFolders.InputProblem(folder, OutputDirectories) is { } problem)
        {
            throw new ArgumentException(problem);
        }

        if (InputDirectories.FirstOrDefault(i => (replacing is null || !Universum.Core.IO.Paths.Same(i, replacing)) && Universum.Core.IO.Paths.SameVolume(i, folder)) is { } taken)
        {
            throw new ArgumentException(Loc.F("Na tomto disku už vstupný priečinok je: {0}. Zmeňte ho v jeho riadku.", taken));
        }

        return folder;
    }

    /// <summary>
    /// Sets the (first) input folder (null clears it – the next one takes its place); <see cref="ArgumentException"/> when it
    /// lies in the results, is an output folder, or its disk has another input folder.
    /// </summary>
    public void SetInputDirectory(string? folder)
    {
        if (folder is not null)
        {
            folder = CheckedInput(folder, Settings.InputDirectory);
            Settings.InputDirectory = folder;
        }
        else
        {
            Settings.InputDirectory = Settings.OtherInputDirectories.FirstOrDefault();
            if (Settings.InputDirectory is { } promoted)
            {
                Settings.OtherInputDirectories.Remove(promoted);
            }
        }

        InputsChanged();
    }

    /// <summary>An input folder of another disk added, or one replaced (<paramref name="replacing"/>); ArgumentException as above.</summary>
    public void SetInputFolder(string folder, string? replacing)
    {
        if (replacing is null && Settings.InputDirectory is null)
        {
            SetInputDirectory(folder);
            return;
        }

        if (replacing is not null && Settings.InputDirectory is { } first && Universum.Core.IO.Paths.Same(first, replacing))
        {
            SetInputDirectory(folder);
            return;
        }

        var chosen = CheckedInput(folder, replacing);
        if (replacing is not null)
        {
            Settings.OtherInputDirectories.RemoveAll(f => Universum.Core.IO.Paths.Same(f, replacing));
        }

        Settings.OtherInputDirectories.Add(chosen);
        InputsChanged();
    }

    /// <summary>An input folder no longer used (nothing in it is touched).</summary>
    public void RemoveInputFolder(string folder)
    {
        if (Settings.InputDirectory is { } first && Universum.Core.IO.Paths.Same(first, folder))
        {
            SetInputDirectory(null);
            return;
        }

        if (Settings.OtherInputDirectories.RemoveAll(f => Universum.Core.IO.Paths.Same(f, folder)) > 0)
        {
            InputsChanged();
        }
    }

    private void InputsChanged()
    {
        Settings.Save();
        EnsureSortingFolders();
        InputDirectoryChanged?.Invoke();
        RestartWatch();
    }

    /// <summary>A film or series (a file or a folder named as it) in any input folder; null when none has it.</summary>
    public string? InAnyInput(Universum.Core.Library.MediaKind kind, IReadOnlyList<string> names, int? year)
    {
        foreach (var input in InputDirectories.Where(Directory.Exists))
        {
            if (new Universum.Core.Posters.PosterPlaces(Library ?? new Universum.Core.Library.LibraryLayout(input), input).InInput(kind, names, year) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private readonly List<Universum.Core.IO.FolderWatch> _watches = [];

    /// <summary>
    /// Films and episodes that came into the input folder while UNIVERSUM runs (owner, 31. 10. 2026: tell and offer – they
    /// go to Hromadné spracovanie only on the user's click). Changed on the window's thread.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<string> Arrivals { get; } = [];

    /// <summary>How long a new file must stay the same before it is told (10 s; the self-test shortens it).</summary>
    public TimeSpan WatchSettle { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Watches the input folder (again) as the settings say; a folder that cannot be watched is simply not.</summary>
    public void RestartWatch()
    {
        foreach (var watch in _watches)
        {
            watch.Dispose();
        }

        _watches.Clear();
        if (!Settings.WatchInput)
        {
            return;
        }

        // every input folder (owner, 8. 11. 2026 – one on every disk)
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        foreach (var folder in InputDirectories.Where(Directory.Exists))
        {
            try
            {
                var watch = new Universum.Core.IO.FolderWatch(folder, WatchSettle, path => Libraries?.HoldsResult(path) == true);
                watch.Arrived += files => dispatcher.BeginInvoke(() =>
                {
                    foreach (var file in files.Where(f => !Arrivals.Contains(f, Universum.Core.IO.Paths.Comparer)))
                    {
                        Arrivals.Add(file);
                    }
                });
                _watches.Add(watch);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // a folder that cannot be watched is simply not
            }
        }
    }

    /// <summary>
    /// Owner, 29. 10. 2026: the input folder holds MOVIES and TVSHOWS – the user sorts films and series into them, so
    /// Hromadné spracovanie tells them apart for certain (nothing is moved by the program). Created when missing.
    /// </summary>
    /// <summary>Where Zlučovanie writes its previews (the folder chosen in Nastavenia, else UNIVERSUM's own).</summary>
    public string PreviewFolder => Settings.MuxPreviewFolder is { Length: > 0 } chosen ? chosen : AppSettings.PreviewDirectory;

    /// <summary>The previews older than a week are deleted (only UNIVERSUM's own: "… – náhľad …"); how many.</summary>
    public int ClearOldPreviews(TimeSpan? age = null)
    {
        var cleared = 0;
        foreach (var file in Universum.Core.Muxing.MuxPreview.Old(PreviewFolder, age ?? TimeSpan.FromDays(7), DateTime.Now))
        {
            try
            {
                File.Delete(file);
                cleared++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a preview still open in a player is left for the next time
            }
        }

        return cleared;
    }

    public void EnsureSortingFolders()
    {
        foreach (var folder in InputDirectories.Where(Directory.Exists))
        {
            EnsureSortingFolders(folder);
        }
    }

    private static void EnsureSortingFolders(string folder)
    {
        foreach (var name in new[] { Universum.Core.Library.TitleUnits.MoviesFolder, Universum.Core.Library.TitleUnits.ShowsFolder })
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a read-only folder: the user can create them
            }
        }
    }

    /// <summary>Raised when the input folder changes (all pages show the same one).</summary>
    public event Action? InputDirectoryChanged;

    private Pages.InputFolderViewModel? _inputFolder;

    /// <summary>The input folder as the pages show it (one for all of them).</summary>
    public Pages.InputFolderViewModel InputFolder => _inputFolder ??= new Pages.InputFolderViewModel(this);

    /// <summary>The library in the (default) output folder; null until the user chooses the folder.</summary>
    public LibraryLayout? Library => Settings.OutputDirectory is { Length: > 0 } folder ? new LibraryLayout(folder) : null;

    /// <summary>
    /// Every output folder (owner, 2. 11. 2026 – práca na rôznych diskoch): the default one and those on other disks; a file
    /// goes into the one on its own disk. Null until the default output folder is chosen.
    /// </summary>
    public LibrarySet? Libraries => Library is { } library
        ? new LibrarySet(library, Settings.OtherOutputDirectories.Where(f => f.Trim().Length > 0).Select(f => new LibraryLayout(f)).ToList(), Settings.OutputBySourceDisk)
        : null;

    /// <summary>The library a file is written into: the output folder on its disk, else the default one.</summary>
    public LibraryLayout? LibraryFor(string? sourcePath) => Libraries is { } libraries ? sourcePath is { Length: > 0 } path ? libraries.For(path) : libraries.Default : null;

    /// <summary>Adds an output folder on another disk (MOVIES, TVSHOWS and POSTER are created in it).</summary>
    public void AddOutputDirectory(string folder)
    {
        var layout = new LibraryLayout(Path.GetFullPath(folder));
        if (Settings.OtherOutputDirectories.Any(f => Universum.Core.IO.Paths.Same(f, layout.Root)) ||
            Settings.OutputDirectory is { } main && Universum.Core.IO.Paths.Same(main, layout.Root))
        {
            return;
        }

        layout.EnsureCreated();
        Settings.OtherOutputDirectories.Add(layout.Root);
        Settings.Save();
        LibraryChanged?.Invoke();
    }

    /// <summary>Removes an output folder on another disk from the list (nothing in it is touched).</summary>
    public void RemoveOutputDirectory(string folder)
    {
        if (Settings.OtherOutputDirectories.RemoveAll(f => Universum.Core.IO.Paths.Same(f, folder)) > 0)
        {
            Settings.Save();
            LibraryChanged?.Invoke();
        }
    }

    /// <summary>Raised when the output folder changes, so the pages can update.</summary>
    public event Action? LibraryChanged;

    /// <summary>
    /// Ponechať MP4 (owner, 7. 11. 2026): one switch for Hromadné, Single spracovanie and Nastavenia –
    /// an MP4 whose header only changes stays MP4 (UNIVERSUM edits its header itself).
    /// </summary>
    public bool KeepMp4
    {
        get => Settings.KeepMp4;
        set
        {
            if (Settings.KeepMp4 != value)
            {
                Settings.KeepMp4 = value;
                Settings.Save();
                KeepMp4Changed?.Invoke();
            }
        }
    }

    /// <summary>Raised when Ponechať MP4 is switched (the pages plan again).</summary>
    public event Action? KeepMp4Changed;

    /// <summary>Sets the output folder and creates MOVIES and TVSHOWS in it (they are never deleted by UNIVERSUM).</summary>
    public void SetOutputDirectory(string folder)
    {
        var layout = new LibraryLayout(Path.GetFullPath(folder));
        layout.EnsureCreated();
        Settings.OutputDirectory = layout.Root;
        Settings.Save();
        LibraryChanged?.Invoke();
    }

    private LibraryIndex? _libraryIndex;

    /// <summary>Which original each library file was made from (recognises the same stream processed again).</summary>
    public LibraryIndex LibraryIndex => _libraryIndex ??= new LibraryIndex(AppSettings.LibraryIndexFile);
}
