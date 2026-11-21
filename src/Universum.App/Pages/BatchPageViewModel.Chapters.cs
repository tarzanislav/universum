using System.Globalization;
using System.IO;
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

    /// <summary>
    /// What the batch knows of chapters (owner, 21. 11. 2026): „s kapitolami 30 (spolu 412) · bez kapitol 12 · pridajú sa 12“,
    /// and of the file selected in the list – its chapters and how far apart they are.
    /// </summary>
    public string ChaptersNote
    {
        get
        {
            if (_analysis is null)
            {
                return "";
            }

            var files = _analysis.Groups.SelectMany(g => g.Files).ToList();
            var with = files.Where(f => f.HasChapters).ToList();
            var added = _analysis.Groups.Sum(g => g.Decisions.ChaptersEvery.Count);
            var note = Loc.F("s kapitolami {0} (spolu {1}) · bez kapitol {2}", with.Count, with.Sum(f => f.ChapterEntries), files.Count - with.Count) +
                       (added > 0 ? " · " + Loc.F("pridajú sa {0}", added) : "");
            if (SelectedFile?.Item is { } item)
            {
                var file = item.File;
                var chapters = item.Target.NewChapters?.Count ?? file.ChapterEntries;
                note += " · " + Path.GetFileName(file.Path) + ": " + (chapters == 0 ? Loc.T("bez kapitol")
                    : file.Duration is { } length && length > TimeSpan.Zero
                        ? Loc.F("{0} kapitol, priemerne každých {1:0} min", chapters, length.TotalMinutes / chapters)
                        : Loc.F("{0} kapitol", chapters));
            }

            return note;
        }
    }

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
