using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Library;
using Universum.App.Localization;

namespace Universum.App.Pages;

/// <summary>One remembered series in Nastavenia: its title, the names it is recognised by, when it was last processed.</summary>
public sealed record SeriesMemoryRow(RememberedSeries Series)
{
    public string Title => Series.Title.FolderName + (Series.Title is { TmdbId: { } id, IncludeId: false } ? $" · TMDB {id}" : "");

    public string Names => Loc.T("podľa: ") + string.Join(", ", Series.Names);

    public string Used => Loc.F("naposledy {0} · {1}×", Series.LastUsedUtc.ToLocalTime().ToString("d. M. yyyy", CultureInfo.GetCultureInfo("sk-SK")), Series.Uses);
}

/// <summary>
/// Napovedkyňa seriálov in Nastavenia (owner, 30. 10. 2026): the series UNIVERSUM remembered; a wrong one is forgotten
/// here (it is then searched as before).
/// </summary>
public sealed class SeriesMemoryViewModel : ObservableObject
{
    private readonly SeriesMemory _memory;

    public SeriesMemoryViewModel(AppServices services)
    {
        _memory = services.SeriesMemory;
        ForgetCommand = new RelayCommand<SeriesMemoryRow>(row =>
        {
            if (row is not null)
            {
                _memory.Forget(row.Series.Title);
            }
        });
        _memory.Changed += () =>
        {
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                dispatcher.InvokeAsync(Refresh);
            }
            else
            {
                Refresh();
            }
        };
        Refresh();
    }

    public ObservableCollection<SeriesMemoryRow> Rows { get; } = [];

    public RelayCommand<SeriesMemoryRow> ForgetCommand { get; }

    public string Status => Rows.Count == 0
        ? Loc.T("Zatiaľ žiadny seriál – zapamätá sa, keď jeho epizódy spracujete (Hromadné, MEGA alebo Single spracovanie).")
        : Loc.F("Zapamätané seriály: {0}.", Rows.Count);

    private void Refresh()
    {
        Rows.Clear();
        foreach (var series in _memory.Series)
        {
            Rows.Add(new SeriesMemoryRow(series));
        }

        OnPropertyChanged(nameof(Status));
    }
}
