using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.News;

namespace Universum.App.Pages;

/// <summary>
/// Nastavenia → Zdroje noviniek: the sources of Premiéry and Novinky. Web pages are added by importing their rules
/// file (the addresses of pages live only in the settings, never in the program); any source can be switched off or
/// removed, TVmaze and TMDB can be brought back.
/// </summary>
public sealed class NewsSourcesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _status = "";

    public NewsSourcesViewModel(AppServices services)
    {
        _services = services;
        ImportCommand = new RelayCommand(Import);
        RestoreCommand = new RelayCommand(Restore, () => NewsSource.Defaults.Any(d => _services.NewsSources.All(s => s.Kind != d.Kind)));
        ClearCacheCommand = new RelayCommand(() =>
        {
            if (!Confirm("Vymazať všetky uložené premiéry a novinky vrátane histórie?\n\nVaše výbery a poznámky ostanú. Zdroje sa pri ďalšom zobrazení načítajú znova."))
            {
                return;
            }

            try
            {
                _services.ClearNews();
                Status = "Uložené premiéry, novinky a história sú vymazané – pri ďalšom zobrazení sa zdroje načítajú znova.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status = ex.Message;
            }
        });
        Reload();
    }

    public ObservableCollection<NewsSourceRow> Rows { get; } = [];

    /// <summary>Asks the user (the self-test answers itself).</summary>
    public Func<string, bool> Confirm { get; set; } = question =>
        System.Windows.MessageBox.Show(question, "UNIVERSUM", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    public RelayCommand ImportCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public RelayCommand ClearCacheCommand { get; }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Adds (or replaces, by name) a web page source from its rules file; false with the reason in <see cref="Status"/>.</summary>
    public bool ImportFile(string path)
    {
        try
        {
            var rules = PageRules.Load(File.ReadAllText(path));
            var source = new NewsSource { Name = rules.Name, Kind = NewsSourceKind.Page, Rules = rules };
            var sources = _services.NewsSources.Where(s => !string.Equals(s.Name, rules.Name, StringComparison.OrdinalIgnoreCase)).Append(source).ToList();
            _services.SetNewsSources(sources);
            Reload();
            var (back, ahead) = SeriesNews.Window(source);
            Status = $"Zdroj „{rules.Name}“ je pridaný ({rules.PageAddress(DateOnly.FromDateTime(DateTime.Now)).Host}, " +
                     $"{Texts.Count(back, "deň", "dni", "dní")} dozadu a {Texts.Count(ahead, "deň", "dni", "dní")} dopredu). Premiéry a Novinky ho načítajú pri ďalšom zobrazení.";
            return true;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Status = "Zdroj sa nepridal: " + ex.Message;
            return false;
        }
    }

    internal void SetEnabled(NewsSourceRow row, bool enabled)
    {
        _services.SetNewsSources(_services.NewsSources.Select(s => s.Name == row.Name ? s with { Enabled = enabled } : s));
        Status = enabled ? $"Zdroj „{row.Name}“ je zapnutý." : $"Zdroj „{row.Name}“ je vypnutý – nič sa z neho nečíta.";
    }

    internal void Remove(NewsSourceRow row)
    {
        _services.SetNewsSources(_services.NewsSources.Where(s => s.Name != row.Name));
        Reload();
        Status = $"Zdroj „{row.Name}“ je odstránený.";
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "Zdroj noviniek (súbor pravidiel)", Filter = "Pravidlá zdroja (*.json)|*.json" };
        if (dialog.ShowDialog() == true)
        {
            ImportFile(dialog.FileName);
        }
    }

    private void Restore()
    {
        _services.SetNewsSources(_services.NewsSources.Concat(NewsSource.Defaults.Where(d => _services.NewsSources.All(s => s.Kind != d.Kind))));
        Reload();
        Status = "TVmaze a TMDB sú opäť medzi zdrojmi.";
    }

    private void Reload()
    {
        Rows.Clear();
        foreach (var source in _services.NewsSources)
        {
            Rows.Add(new NewsSourceRow(source, this));
        }

        RestoreCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A source in the settings.</summary>
public sealed class NewsSourceRow(NewsSource source, NewsSourcesViewModel owner) : ObservableObject
{
    private bool _enabled = source.Enabled;

    public string Name => source.Name;

    public string KindText => source.KindText;

    public string Address => source.Kind == NewsSourceKind.Page && source.Rules is { } rules ? rules.PageAddress(DateOnly.FromDateTime(DateTime.Now)).Host : source.Address;

    public string Detail => source.Kind switch
    {
        NewsSourceKind.Page when source.Rules is { } rules =>
            $"{Texts.Count(SeriesNews.Window(source).Back, "deň", "dni", "dní")} dozadu, {Texts.Count(SeriesNews.Window(source).Ahead, "deň", "dni", "dní")} dopredu · CZ/SK stanice: {rules.Domestic.Count}",
        NewsSourceKind.TvMaze => $"krajiny {string.Join(", ", source.Countries)} a streamovacie služby · popularita aspoň {source.MinWeight}",
        _ => "kľúč TMDB z karty TMDB",
    };

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                owner.SetEnabled(this, value);
            }
        }
    }

    public RelayCommand RemoveCommand => _remove ??= new RelayCommand(() => owner.Remove(this));

    private RelayCommand? _remove;
}
