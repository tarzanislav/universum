using System.IO;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Library;

namespace Universum.App.Pages;

/// <summary>
/// A disk in the table Priečinky na diskoch of Nastavenia (owner, 8. 11. 2026: "v nastaveniach chaos – prerob"): its input
/// folder and its output folder side by side, whether its output is the default one, and what can be done with them.
/// </summary>
public sealed class DiskFolderView(DiskFolderRow row, DiskFolders owner)
{
    public DiskFolderRow Row { get; } = row;

    /// <summary>"E:".</summary>
    public string Disk => Row.Root.TrimEnd('\\', '/');

    public string? Input => Row.Input;

    public string? Output => Row.Output;

    public string InputText => Row.Input ?? Loc.T("— bez vstupu");

    public string OutputText => Row.Output ?? Loc.T("— bez výstupu");

    public bool HasInput => Row.Input is not null;

    public bool HasOutput => Row.Output is not null;

    public bool IsMain => Row.IsMain;

    /// <summary>"Disk E:".</summary>
    public string DiskCaption => Loc.F("Disk {0}", Disk);

    /// <summary>"★ hlavný" for the disk of the default output.</summary>
    public string MainCaption => Row.IsMain ? "  ★ " + Loc.T("hlavný") : "";

    /// <summary>
    /// Where the films of this disk go, in one sentence (owner, 8. 11. 2026: "nikto nedokáže určiť, ktorý vstupný patrí
    /// kam"): "Filmy z D:\UNIVERSUM\ZDROJ sa zapíšu do D:\UNIVERSUM\VYSTUP."
    /// </summary>
    public string Flow => (Row.Input, Row.Output) switch
    {
        ({ } input, { } output) => Loc.F("Filmy z {0} sa zapíšu do {1}.", input, output),
        ({ } input, null) => Loc.F("Disk nemá vlastný výstup – filmy z {0} sa skopírujú do hlavného výstupu {1} na inom disku.", input, owner.MainOutput ?? "?"),
        (null, { }) when Row.IsMain => "",
        (null, { } output) => Loc.F("Disk nemá vstup – do {0} sa zapíšu filmy, ktoré ležia na tomto disku.", output),
        _ => "",
    } + (Row.IsMain && Row.Output is not null ? (Row.Input is null ? "" : " ") + Loc.T("Sem idú aj filmy z diskov bez vlastného výstupu.") : "");

    public bool CanMakeMain => !Row.IsMain && Row.Output is not null;

    /// <summary>A disk with films but no output: they would be copied to another disk.</summary>
    public bool NeedsOutput => Row.Input is not null && Row.Output is null;

    /// <summary>"voľné 1 234 GB" or that the disk is not there.</summary>
    public string Free
    {
        get
        {
            try
            {
                var drive = new DriveInfo(Row.Root);
                return "  · " + (drive.IsReady ? Loc.F("voľné {0:N0} GB", drive.AvailableFreeSpace / (1024.0 * 1024 * 1024)) : Loc.T("nie je pripojený"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return "";
            }
        }
    }

    /// <summary>What is wrong on this disk (two inputs or outputs saved before, an output that holds films …); empty when nothing.</summary>
    public string Warning => owner.WarningOf(Row);

    public bool HasWarning => Warning.Length > 0;

    /// <summary>The output folder holds films of its own – it looks like a source folder: it can become the input of this disk.</summary>
    public bool OutputLooksLikeSource => Row.Output is { } output && !Row.IsMain && OutputFolders.LooksLikeSource(output);

    public string SuggestedOutput => Row.Input is { } input ? OutputFolders.SuggestedOutput(input) : "";

    public RelayCommand ChangeInputCommand => new(() => owner.ChangeInput(Row));

    public RelayCommand ChangeOutputCommand => new(() => owner.ChangeOutput(Row));

    public RelayCommand CreateOutputCommand => new(() => owner.CreateOutput(Row));

    public RelayCommand MakeMainCommand => new(() => owner.MakeMain(Row));

    public RelayCommand RemoveCommand => new(() => owner.Remove(Row));

    public RelayCommand OutputToInputCommand => new(() => owner.OutputToInput(Row));

    public RelayCommand RemoveExtrasCommand => new(() => owner.RemoveExtras(Row));

    public bool HasExtras => Row.ExtraInputs.Count + Row.ExtraOutputs.Count > 0;
}

/// <summary>
/// Priečinky na diskoch (owner, 8. 11. 2026): every disk with its input folder (where films come) and its output folder (the
/// library written into) in one table; a file goes into the output of its own disk, a file from a disk without one into the
/// default (★) output. An input is never an output, each disk has one of each; nothing in a folder is ever deleted.
/// </summary>
public sealed class DiskFolders : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly AppServices _services;
    private readonly SettingsPageViewModel _settings;
    private string _message = "";

    public DiskFolders(AppServices services, SettingsPageViewModel settings)
    {
        _services = services;
        _settings = settings;
        AddInputCommand = new RelayCommand(() => { if (Pick(Loc.T("Vstupný priečinok (kam prichádzajú nové filmy a epizódy)"), null) is { } folder) { AddInput(folder); } });
        AddOutputCommand = new RelayCommand(() => { if (Pick(Loc.T("Výstupný priečinok (knižnica s MOVIES, TVSHOWS a POSTER)"), null) is { } folder) { AddOutput(folder); } });
        services.LibraryChanged += Refresh;
        services.InputDirectoryChanged += Refresh;
    }

    /// <summary>The disks, the one of the default output first.</summary>
    public IReadOnlyList<DiskFolderView> Rows =>
        [.. OutputFolders.Rows(_services.InputDirectories, _services.Settings.OutputDirectory, _services.Settings.OtherOutputDirectories).Select(r => new DiskFolderView(r, this))];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The default output folder (★).</summary>
    public string? MainOutput => _services.Settings.OutputDirectory;

    /// <summary>What happened (shown under the table; it goes after a few seconds – owner, 8. 11. 2026).</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value) && value.Length > 0 && System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            {
                var shown = value;
                var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(12), System.Windows.Threading.DispatcherPriority.Background, (sender, _) =>
                {
                    ((System.Windows.Threading.DispatcherTimer)sender!).Stop();
                    if (_message == shown)
                    {
                        Message = "";
                    }
                }, dispatcher);
                timer.Start();
            }
        }
    }

    public RelayCommand AddInputCommand { get; }

    public RelayCommand AddOutputCommand { get; }

    /// <summary>Asks for a folder (the title, where to start); the self-test answers instead.</summary>
    public Func<string, string?, string?> Pick { get; set; } = (title, start) =>
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (start is not null && Directory.Exists(start))
        {
            dialog.InitialDirectory = start;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(IsEmpty));
    }

    internal string WarningOf(DiskFolderRow row)
    {
        var parts = new List<string>();
        if (row.ExtraOutputs.Count > 0)
        {
            parts.Add(Loc.F("Na disku je viac výstupných priečinkov – používa sa len {0}; navyše: {1}.", row.Output ?? "", string.Join(", ", row.ExtraOutputs)));
        }

        if (row.ExtraInputs.Count > 0)
        {
            parts.Add(Loc.F("Na disku je viac vstupných priečinkov – ukazuje sa {0}; navyše: {1}.", row.Input ?? "", string.Join(", ", row.ExtraInputs)));
        }

        return string.Join(" ", parts);
    }

    /// <summary>An input folder added (its disk gets a row; a disk has one input).</summary>
    public void AddInput(string folder) => Do(() =>
    {
        _services.SetInputFolder(folder, null);
        Message = Loc.F("Vstupný priečinok pridaný: {0}.", Path.GetFullPath(folder)) + NoOutputHint(folder);
    });

    /// <summary>An output folder added: the default one when there is none, else the output of its disk.</summary>
    public void AddOutput(string folder)
    {
        var chosen = Path.GetFullPath(folder);
        if (_services.Library is null)
        {
            if (Acceptable(chosen))
            {
                _services.SetOutputDirectory(chosen);
                Message = Loc.F("Výstupný priečinok: {0}", chosen);
            }

            return;
        }

        if (_services.OutputDirectories.FirstOrDefault(o => Universum.Core.IO.Paths.SameVolume(o, chosen)) is { } taken)
        {
            Message = Loc.F("Disk už má výstupný priečinok {0} – zmeňte ho v jeho riadku (…).", taken);
            return;
        }

        if (Acceptable(chosen))
        {
            _services.AddOutputDirectory(chosen);
            Message = Loc.F("Pridaný výstupný priečinok: {0} – súbory z tohto disku pôjdu sem.", chosen);
        }
    }

    public void ChangeInput(DiskFolderRow row)
    {
        if (Pick(Loc.T("Vstupný priečinok (kam prichádzajú nové filmy a epizódy)"), row.Input) is { } folder)
        {
            SetInput(row, folder);
        }
    }

    /// <summary>The input of a disk set (also the self-test).</summary>
    public void SetInput(DiskFolderRow row, string folder) => Do(() =>
    {
        _services.SetInputFolder(folder, row.Input);
        Message = Loc.F("Vstupný priečinok: {0}.", Path.GetFullPath(folder)) + (row.Input is { } old && !Universum.Core.IO.Paths.Same(old, folder) ? " " + Loc.F("(predtým {0} – v ňom sa nič nezmenilo)", old) : "") +
                  NoOutputHint(folder);
    });

    public void ChangeOutput(DiskFolderRow row)
    {
        if (Pick(Loc.T("Výstupný priečinok (knižnica s MOVIES, TVSHOWS a POSTER)"), row.Output ?? row.Input) is { } folder)
        {
            SetOutput(row, folder);
        }
    }

    /// <summary>The output of a disk set (also the self-test): the default one stays the default one.</summary>
    public void SetOutput(DiskFolderRow row, string folder)
    {
        var chosen = Path.GetFullPath(folder);
        if (row.Output is { } old && Universum.Core.IO.Paths.Same(old, chosen))
        {
            return;
        }

        if (_services.OutputDirectories.FirstOrDefault(o => (row.Output is null || !Universum.Core.IO.Paths.Same(o, row.Output)) && Universum.Core.IO.Paths.SameVolume(o, chosen)) is { } taken)
        {
            Message = Loc.F("Disk už má výstupný priečinok {0} – zmeňte ho v jeho riadku (…).", taken);
            return;
        }

        if (!Acceptable(chosen))
        {
            return;
        }

        if (row.IsMain || _services.Library is null)
        {
            _services.SetOutputDirectory(chosen);
        }
        else
        {
            if (row.Output is { } replaced)
            {
                _services.RemoveOutputDirectory(replaced);
            }

            _services.AddOutputDirectory(chosen);
        }

        Message = Loc.F("Výstupný priečinok: {0}.", chosen) + (row.Output is { } before ? " " + Loc.F("(predtým {0} – v ňom sa nič nezmenilo)", before) : "");
    }

    /// <summary>The output beside the input of a disk ("…\\VYSTUP"), made with one click.</summary>
    public void CreateOutput(DiskFolderRow row)
    {
        if (row.Input is { } input)
        {
            SetOutput(row, OutputFolders.SuggestedOutput(input));
        }
    }

    /// <summary>The output of this disk becomes the default one (files from disks without an output go there).</summary>
    public void MakeMain(DiskFolderRow row)
    {
        if (row.Output is not { } output || row.IsMain || _services.Library is not { } main)
        {
            return;
        }

        _services.RemoveOutputDirectory(output);
        _services.SetOutputDirectory(output);
        _services.AddOutputDirectory(main.Root);
        Message = Loc.F("Hlavný výstup je teraz {0}; súbory z diskov bez výstupu pôjdu sem.", output);
    }

    /// <summary>The disk's folders no longer used (nothing in them changes); the disk of the default output cannot go.</summary>
    public void Remove(DiskFolderRow row)
    {
        if (row.IsMain)
        {
            Message = Loc.T("Disk s hlavným výstupom sa nedá odobrať – najprv označte iný disk ako hlavný (★).");
            return;
        }

        if (row.Output is { } output)
        {
            _services.RemoveOutputDirectory(output);
        }

        foreach (var input in new[] { row.Input }.Concat(row.ExtraInputs).OfType<string>())
        {
            _services.RemoveInputFolder(input);
        }

        foreach (var extra in row.ExtraOutputs)
        {
            _services.RemoveOutputDirectory(extra);
        }

        Message = Loc.F("Disk {0} sa už nepoužíva – v jeho priečinkoch sa nič nezmenilo.", row.Root.TrimEnd('\\'));
    }

    /// <summary>Owner, 8. 11. 2026: a source folder saved among the outputs becomes the input of its disk.</summary>
    public void OutputToInput(DiskFolderRow row)
    {
        if (row.Output is not { } output || row.IsMain)
        {
            return;
        }

        _services.RemoveOutputDirectory(output);
        Message = Loc.F("{0} sa už nepoužíva ako výstup.", output);
        if (row.Input is null)
        {
            Do(() =>
            {
                _services.SetInputFolder(output, null);
                Message = Loc.F("{0} je teraz vstupný priečinok disku (nie výstup). Výstup na tomto disku vytvoríte tlačidlom Vytvoriť výstup.", output);
            });
        }
    }

    /// <summary>The folders saved beyond one per disk no longer used.</summary>
    public void RemoveExtras(DiskFolderRow row)
    {
        foreach (var extra in row.ExtraOutputs)
        {
            _services.RemoveOutputDirectory(extra);
        }

        foreach (var extra in row.ExtraInputs)
        {
            _services.RemoveInputFolder(extra);
        }

        Message = Loc.T("Priečinky navyše sa už nepoužívajú – nič v nich sa nezmenilo.");
    }

    // the checks of an output (the input folders, a folder of films asked)
    private bool Acceptable(string chosen)
    {
        if (OutputFolders.Problem(chosen, _services.InputDirectories) is { } problem)
        {
            Message = problem;
            return false;
        }

        if (_services.Libraries?.ResultFolders.Any(r => Universum.Core.IO.Paths.IsUnder(chosen, r)) == true)
        {
            Message = Loc.T("Výstupný priečinok nemôže byť v MOVIES ani v TVSHOWS iného výstupu.");
            return false;
        }

        if (OutputFolders.LooksLikeSource(chosen) &&
            _settings.Ask(Loc.F("V priečinku {0} sú filmy alebo seriály mimo MOVIES a TVSHOWS – vyzerá ako zdrojový priečinok, nie ako výstup.\n\nNaozaj ho použiť ako výstupný priečinok?", chosen), false) != true)
        {
            Message = Loc.T("Výstupný priečinok sa nezmenil.");
            return false;
        }

        return true;
    }

    private string NoOutputHint(string input) =>
        _services.OutputDirectories.Any(o => Universum.Core.IO.Paths.SameVolume(o, input)) ? "" : " " + Loc.T("Disk zatiaľ nemá výstup – vytvorte ho tlačidlom Vytvoriť výstup v jeho riadku.");

    private void Do(Action change)
    {
        try
        {
            change();
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = Loc.T("Priečinok sa nedá použiť: ") + ex.Message;
        }
    }
}
