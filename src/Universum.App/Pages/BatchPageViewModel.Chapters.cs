using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;

namespace Universum.App.Pages;

// BatchPageViewModel: chapters added every so long (owner, 21. 11. 2026 – to the files without chapters, or to the files selected)
public sealed partial class BatchPageViewModel
{
    private string _chapterMinutes = "10";

    /// <summary>How long a chapter is, in minutes (1–240).</summary>
    public string ChapterMinutes
    {
        get => _chapterMinutes;
        set
        {
            if (SetProperty(ref _chapterMinutes, value))
            {
                AddChaptersCommand.NotifyCanExecuteChanged();
                AddChaptersSelectedCommand.NotifyCanExecuteChanged();
            }
        }
    }

    // the minutes typed, or null when they are no number of 1–240
    private TimeSpan? ChapterInterval =>
        int.TryParse(_chapterMinutes.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var minutes) && minutes is >= 1 and <= 240
            ? TimeSpan.FromMinutes(minutes) : null;

    /// <summary>Doplniť súborom bez kapitol: every file of the batch without chapters gets them.</summary>
    public RelayCommand AddChaptersCommand => _addChapters ??= new(
        () => AddChapters(_analysis?.Groups.SelectMany(g => g.Files.Where(f => !f.HasChapters).Select(f => (g, f.Path))) ?? []),
        () => !Busy && !IsRunning && _analysis is not null && ChapterInterval is not null);

    private RelayCommand? _addChapters;

    /// <summary>Vybraným súborom: the files selected in the list get them (their own chapters are replaced).</summary>
    public RelayCommand AddChaptersSelectedCommand => _addChaptersSelected ??= new(
        () => AddChapters(_selectedFiles.Select(f => (f.Item.Group, f.Item.File.Path))),
        () => !Busy && !IsRunning && _selectedFiles.Count > 0 && ChapterInterval is not null);

    private RelayCommand? _addChaptersSelected;

    /// <summary>Bez pridaných kapitol: the chapters added are taken back.</summary>
    public RelayCommand ClearChaptersCommand => _clearChapters ??= new(() =>
    {
        foreach (var group in _analysis?.Groups ?? [])
        {
            group.Decisions.ChaptersEvery.Clear();
        }

        Replan();
        RunState = Loc.T("Pridané kapitoly zrušené – súbory ostanú so svojimi kapitolami.");
        OnPropertyChanged(nameof(ChaptersNote));
    }, () => !Busy && !IsRunning);

    private RelayCommand? _clearChapters;

    /// <summary>"Kapitoly sa pridajú 12 súborom." – how many files get chapters.</summary>
    public string ChaptersNote => (_analysis?.Groups.Sum(g => g.Decisions.ChaptersEvery.Count) ?? 0) is var count and > 0
        ? Loc.F("Kapitoly sa pridajú súborom: {0}.", count) : "";

    private void AddChapters(IEnumerable<(Core.Batch.BatchGroup Group, string Path)> files)
    {
        if (ChapterInterval is not { } every)
        {
            return;
        }

        var count = 0;
        foreach (var (group, path) in files)
        {
            group.Decisions.ChaptersEvery[path] = every;
            count++;
        }

        Replan();
        RunState = count == 0
            ? Loc.T("Žiadny súbor bez kapitol – nič sa nepridá.")
            : Loc.F("Kapitoly každých {0} min sa pridajú súborom: {1} (stĺpec Kapitoly).", (int)every.TotalMinutes, count);
        OnPropertyChanged(nameof(ChaptersNote));
    }
}
