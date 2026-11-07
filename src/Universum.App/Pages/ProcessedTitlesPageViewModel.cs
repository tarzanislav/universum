using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Localization;
using Universum.Core.Library;

namespace Universum.App.Pages;

/// <summary>A title of the register as the list shows it.</summary>
public sealed class ProcessedTitleRow(RegisteredTitle title)
{
    public RegisteredTitle Title { get; } = title;

    public string Display => Title.Display;

    public string Kind => Title.Kind == MediaKind.Series ? Loc.T("seriál") : Loc.T("film");

    /// <summary>The other names the title was known by (its originals' file and folder names).</summary>
    public string Names => string.Join(", ", Title.Names);

    /// <summary>The Czech title from TMDB (empty until asked, or when it has none).</summary>
    public string CzechName => Title.CzechName ?? "";

    /// <summary>The English title from TMDB.</summary>
    public string EnglishName => Title.EnglishName ?? "";

    public string Episodes => Title.Kind == MediaKind.Series ? Loc.F("{0} ({1})", Title.Episodes.Count, Title.Seasons) : "";

    public int Files => Title.Files;

    public string Last => Title.Last.LocalDateTime.ToString("d. M. yyyy", CultureInfo.GetCultureInfo("sk-SK"));
}

/// <summary>
/// Spracované tituly (owner, 7. 11. 2026, Štatistika): the films and series written into the libraries – filled in the
/// background with every result, read from the output folders on request, exported and imported as the user's bonus.
/// The register is the user's own data in the settings folder; nothing of it goes anywhere else.
/// </summary>
public sealed class ProcessedTitlesPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _filter = "";
    private int _kind;
    private string _status = "";
    private bool _busy;

    public ProcessedTitlesPageViewModel(AppServices services)
    {
        _services = services;
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !_busy);
        ExportCommand = new RelayCommand(Export, () => !_busy);
        ImportCommand = new RelayCommand(Import, () => !_busy);
        services.TitleRegister.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        Refresh();
        _ = services.FillTitleNamesAsync();
    }

    public ObservableCollection<ProcessedTitleRow> Titles { get; } = [];

    /// <summary>Hľadať: a part of the name, the year or the TMDB ID.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                Refresh();
            }
        }
    }

    /// <summary>0 all, 1 series, 2 films.</summary>
    public int KindFilter
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                Refresh();
            }
        }
    }

    public IReadOnlyList<string> KindChoices { get; } = [Loc.T("všetko"), Loc.T("seriály"), Loc.T("filmy")];

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Načítať z knižnice: the films and series already in the output folders (MOVIES, TVSHOWS).</summary>
    public AsyncRelayCommand ScanCommand { get; }

    /// <summary>Export: JSON (whole, to be imported again) or CSV (Excel).</summary>
    public RelayCommand ExportCommand { get; }

    /// <summary>Import: an exported register (JSON) joined with this one.</summary>
    public RelayCommand ImportCommand { get; }

    private void Refresh()
    {
        var all = _services.TitleRegister.Titles;
        var words = _filter.Trim();
        var shown = all.Where(t => _kind == 0 || (_kind == 1) == (t.Kind == MediaKind.Series))
            .Where(t => words.Length == 0 || t.Display.Contains(words, StringComparison.CurrentCultureIgnoreCase) ||
                        t.Names.Any(n => n.Contains(words, StringComparison.CurrentCultureIgnoreCase)) ||
                        (t.CzechName ?? "").Contains(words, StringComparison.CurrentCultureIgnoreCase) || (t.EnglishName ?? "").Contains(words, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        Titles.Clear();
        foreach (var title in shown)
        {
            Titles.Add(new ProcessedTitleRow(title));
        }

        if (!_busy)
        {
            Status = Loc.F("Seriály: {0} ({1} epizód) · filmy: {2} · zobrazené: {3}. Zoznam sa dopĺňa sám pri každom spracovaní do knižnice; je len vo vašom počítači.",
                all.Count(t => t.Kind == MediaKind.Series), all.Where(t => t.Kind == MediaKind.Series).Sum(t => t.Episodes.Count),
                all.Count(t => t.Kind == MediaKind.Movie), shown.Count);
        }
    }

    private async Task ScanAsync()
    {
        SetBusy(true);
        Status = Loc.T("Čítam výstupné priečinky (MOVIES, TVSHOWS)…");
        try
        {
            var folders = _services.Libraries?.ResultFolders ?? [];
            // a title whose folder carries no ID: its TMDB ID from napovedkyňa (the titles processed and remembered)
            var remembered = _services.SeriesMemory.Series.Select(s => s.Title).Where(t => t.TmdbId is not null).ToList();
            var added = await Task.Run(() => _services.TitleRegister.Scan(folders, title =>
                remembered.FirstOrDefault(r => r.Kind == title.Kind && r.Year == title.Year && string.Equals(r.FolderName, title.FolderName, StringComparison.OrdinalIgnoreCase))));
            SetBusy(false);
            Refresh();
            Status = Loc.F("Z knižnice pribudlo {0} súborov. ", added) + Status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetBusy(false);
            Status = Loc.T("Knižnicu sa nepodarilo prečítať: ") + ex.Message;
        }
    }

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Filter = Loc.T("Spracované tituly – na import (*.json)|*.json|Tabuľka CSV pre Excel (*.csv)|*.csv"),
            FileName = Loc.T("UNIVERSUM – spracované tituly") + " " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _services.TitleRegister.Export(dialog.FileName);
            Status = Loc.F("Uložené: {0}", dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = Loc.T("Export sa nepodaril: ") + ex.Message;
        }
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Spracované tituly (*.json)|*.json") };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var added = _services.TitleRegister.Import(dialog.FileName);
            Refresh();
            Status = Loc.F("Načítané: {0} nových titulov (ostatné sa spojili s tými, čo už sú). ", added) + Status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Status = Loc.T("Import sa nepodaril – nie je to súbor Spracovaných titulov: ") + ex.Message;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ScanCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
    }
}
