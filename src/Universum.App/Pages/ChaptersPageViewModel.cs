using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.Chapters;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;

namespace Universum.App.Pages;

/// <summary>A chapter in the editor: start (as text, so any typing is allowed), name and language.</summary>
public sealed class ChapterRow : ObservableObject
{
    private readonly Action _changed;
    private string _start;
    private string _name;
    private LanguageChoice _language;
    private int _number;

    public ChapterRow(Chapter chapter, Action changed)
    {
        _changed = changed;
        _start = ChapterTime.Format(chapter.Start);
        _name = chapter.Name;
        _language = LanguageChoice.Common.FirstOrDefault(c => c.Same(chapter.Language)) ?? LanguageChoice.Of(chapter.Language);
    }

    public int Number { get => _number; set => SetProperty(ref _number, value); }

    public string Start { get => _start; set { if (SetProperty(ref _start, value)) { OnPropertyChanged(nameof(IsValid)); _changed(); } } }

    public string Name { get => _name; set { if (SetProperty(ref _name, value)) { _changed(); } } }

    public LanguageChoice Language { get => _language; set { if (SetProperty(ref _language, value)) { _changed(); } } }

    public IReadOnlyList<LanguageChoice> LanguageChoices => LanguageChoice.Common;

    public bool IsValid => ChapterTime.Parse(Start) is not null;

    public TimeSpan? StartTime => ChapterTime.Parse(Start);

    public Chapter? ToChapter() => StartTime is { } start ? new Chapter(start, Name, Language.Tag) : null;
}

/// <summary>
/// Chapter editor (MKVToolNix GUI: Chapter editor) for one edition of flat chapters: start, name and language; adding,
/// removing, sorting, shifting, generating every N minutes and importing XML or OGM files. Saving replaces the file's
/// chapters in place (seconds), verifies them and can be undone.
/// </summary>
public sealed class ChaptersPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private MediaFile? _file;
    private ChapterRow? _selected;
    private string _fileName = "";
    private string _summary = "Otvorte súbor MKV alebo ho sem pretiahnite myšou.";
    private string _problems = "";
    private string _result = "";
    private string _interval = "10";
    private string _shift = "0";
    private bool _complex;
    private bool _busy;
    private bool _dirty;
    private string? _undoRecord;

    public ChaptersPageViewModel(AppServices services)
    {
        _services = services;
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !_busy);
        AddCommand = new RelayCommand(Add, CanEdit);
        RemoveCommand = new RelayCommand(Remove, () => CanEdit() && _selected is not null);
        RemoveAllCommand = new RelayCommand(RemoveAll, () => CanEdit() && Rows.Count > 0);
        SortCommand = new RelayCommand(Sort, () => CanEdit() && Rows.Count > 1);
        GenerateCommand = new RelayCommand(Generate, () => CanEdit() && _file?.Duration is not null);
        ShiftCommand = new RelayCommand(ShiftAll, () => CanEdit() && Rows.Count > 0);
        ImportCommand = new RelayCommand(Import, CanEdit);
        ResetCommand = new AsyncRelayCommand(() => ReadAsync(_file!.Path), () => !_busy && _file is not null);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanEdit() && _dirty && Problems.Length == 0);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => !_busy && _undoRecord is not null);
    }

    public AsyncRelayCommand OpenCommand { get; }

    public RelayCommand AddCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand RemoveAllCommand { get; }

    public RelayCommand SortCommand { get; }

    public RelayCommand GenerateCommand { get; }

    public RelayCommand ShiftCommand { get; }

    public RelayCommand ImportCommand { get; }

    public AsyncRelayCommand ResetCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand UndoCommand { get; }

    public ObservableCollection<ChapterRow> Rows { get; } = [];

    public ChapterRow? SelectedRow { get => _selected; set { if (SetProperty(ref _selected, value)) { RefreshCommands(); } } }

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Problems { get => _problems; private set => SetProperty(ref _problems, value); }

    public string Result { get => _result; private set => SetProperty(ref _result, value); }

    /// <summary>Minutes between generated chapters.</summary>
    public string Interval { get => _interval; set => SetProperty(ref _interval, value); }

    /// <summary>Seconds to shift all chapters by (negative = earlier).</summary>
    public string Shift { get => _shift; set => SetProperty(ref _shift, value); }

    public string? FilePath => _file?.Path;

    public async Task LoadAsync(string path)
    {
        _undoRecord = null;
        Result = "";
        await ReadAsync(path);
    }

    /// <summary>Chapters every <see cref="Interval"/> minutes over the whole file (replacing the list).</summary>
    public void Generate()
    {
        if (_file?.Duration is not { } duration ||
            !double.TryParse(Interval.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
        {
            Result = "Zadajte interval v minútach (napríklad 10).";
            return;
        }

        Fill(ChapterChecks.Every(TimeSpan.FromMinutes(minutes), duration, Language()), dirty: true);
        Result = $"Vygenerované: {Count(Rows.Count)} každých {minutes} min.";
    }

    private static string Count(int chapters) => chapters switch
    {
        1 => "1 kapitola",
        >= 2 and <= 4 => $"{chapters} kapitoly",
        _ => $"{chapters} kapitol",
    };

    private LanguageTag Language() => LanguageTag.FromCode(_services.Profile.PreferredLanguages.FirstOrDefault() ?? "cs");

    private async Task ReadAsync(string path)
    {
        _file = null;
        _complex = false;
        Rows.Clear();
        FileName = Path.GetFileName(path);
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Summary = "MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.";
            RefreshCommands();
            return;
        }

        _busy = true;
        try
        {
            _file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
            var set = await Editor(installation).ReadAsync(path);
            _complex = set.Complex;
            Summary = $"Trvanie {(_file.Duration is { } d ? ChapterTime.Format(d) : "neznáme")}, kapitoly: {set.Chapters.Count}" +
                      (set.Complex ? ". Súbor má viac vydaní alebo vnorené kapitoly – upravte ich v MKVToolNix GUI (UNIVERSUM ich nemení)." : ".");
            Fill(set.Chapters, dirty: false);
        }
        catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException or TimeoutException or System.Xml.XmlException)
        {
            Summary = ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    private ChapterEditor Editor(MkvToolNixInstallation installation) => new(installation, _services.Runner, AppSettings.JournalDirectory);

    private void Fill(IEnumerable<Chapter> chapters, bool dirty)
    {
        Rows.Clear();
        foreach (var chapter in chapters)
        {
            Rows.Add(new ChapterRow(chapter, Changed));
        }

        _dirty = dirty;
        Recalculate();
    }

    private void Changed()
    {
        _dirty = true;
        Recalculate();
    }

    private void Recalculate()
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Number = i + 1;
        }

        var problems = Rows.Where(r => !r.IsValid).Select(r => $"Kapitola {r.Number}: čas „{r.Start}“ nie je v tvare HH:MM:SS.mmm.").ToList();
        problems.AddRange(ChapterChecks.Problems(Rows.Select(r => r.ToChapter()).OfType<Chapter>().ToList(), _file?.Duration));
        Problems = string.Join("\n", problems);
        RefreshCommands();
    }

    private bool CanEdit() => !_busy && _file is not null && !_complex;

    private void Add()
    {
        var after = SelectedRow?.StartTime ?? Rows.Select(r => r.StartTime).Max() ?? TimeSpan.Zero;
        var start = Rows.Count == 0 ? TimeSpan.Zero : after + TimeSpan.FromMinutes(1);
        if (_file?.Duration is { } duration && start >= duration)
        {
            start = after;
        }

        var row = new ChapterRow(new Chapter(start, $"Kapitola {Rows.Count + 1:00}", Language()), Changed);
        Rows.Insert(SelectedRow is null ? Rows.Count : Rows.IndexOf(SelectedRow) + 1, row);
        SelectedRow = row;
        Changed();
    }

    private void Remove()
    {
        if (SelectedRow is { } row)
        {
            Rows.Remove(row);
            Changed();
        }
    }

    private void RemoveAll()
    {
        Rows.Clear();
        Changed();
        Result = "Po uložení súbor nebude mať žiadne kapitoly.";
    }

    private void Sort()
    {
        var sorted = Rows.OrderBy(r => r.StartTime ?? TimeSpan.MaxValue).ToList();
        Rows.Clear();
        foreach (var row in sorted)
        {
            Rows.Add(row);
        }

        Changed();
    }

    private void ShiftAll()
    {
        if (!double.TryParse(Shift.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds == 0)
        {
            Result = "Zadajte posun v sekundách (napríklad 2,5 alebo -1).";
            return;
        }

        foreach (var row in Rows.Where(r => r.StartTime is not null))
        {
            var moved = row.StartTime!.Value + TimeSpan.FromSeconds(seconds);
            row.Start = ChapterTime.Format(moved < TimeSpan.Zero ? TimeSpan.Zero : moved);
        }

        Result = $"Všetky kapitoly sú posunuté o {seconds} s.";
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "Kapitoly (*.xml;*.txt)|*.xml;*.txt|Všetky súbory|*.*" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(dialog.FileName);
            var chapters = text.TrimStart('﻿').TrimStart().StartsWith('<')
                ? ChapterFormats.ParseXml(text).Chapters
                : ChapterFormats.ParseOgm(text, Language());
            Fill(chapters, dirty: true);
            Result = $"Načítané zo súboru: {Count(chapters.Count)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
        {
            Result = "Súbor s kapitolami sa nepodarilo prečítať: " + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (_file is null || _services.CurrentMkvToolNix is not { } installation)
        {
            return;
        }

        _busy = true;
        RefreshCommands();
        try
        {
            var chapters = Rows.Select(r => r.ToChapter()).OfType<Chapter>().ToList();
            var result = await Editor(installation).SaveAsync(_file.Path, chapters);
            _services.Inspector(installation).Invalidate(_file.Path);
            if (result.Saved)
            {
                _undoRecord = result.UndoRecordPath;
                await ReadAsync(_file.Path);
                Result = chapters.Count == 0 ? "Kapitoly sú odstránené. Dá sa to vrátiť späť." : $"Uložené: {Count(chapters.Count)}. Dá sa to vrátiť späť.";
            }
            else
            {
                Result = "Nepodarilo sa – " + (result.UndoRecordPath is null ? "kapitoly v súbore ostali pôvodné" : "POZOR: pôvodné kapitoly sa nepodarilo vrátiť, záznam: " + result.UndoRecordPath) +
                         ".\n" + string.Join("\n", result.Messages);
            }
        }
        catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException)
        {
            Result = "Nepodarilo sa: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    private async Task UndoAsync()
    {
        if (_undoRecord is null || _services.CurrentMkvToolNix is not { } installation || _file is null)
        {
            return;
        }

        var path = _file.Path;
        var problems = await Editor(installation).UndoAsync(_undoRecord);
        _services.Inspector(installation).Invalidate(path);
        if (problems.Count == 0)
        {
            _undoRecord = null;
            await ReadAsync(path);
            Result = "Pôvodné kapitoly sú vrátené.";
        }
        else
        {
            Result = "Vrátenie sa nepodarilo:\n" + string.Join("\n", problems);
        }
    }

    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Súbory Matroska (*.mkv;*.mka;*.mks)|*.mkv;*.mka;*.mks|Všetky súbory|*.*" };
        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(dialog.FileName);
        }
    }

    private void RefreshCommands()
    {
        OpenCommand?.NotifyCanExecuteChanged();
        AddCommand?.NotifyCanExecuteChanged();
        RemoveCommand?.NotifyCanExecuteChanged();
        RemoveAllCommand?.NotifyCanExecuteChanged();
        SortCommand?.NotifyCanExecuteChanged();
        GenerateCommand?.NotifyCanExecuteChanged();
        ShiftCommand?.NotifyCanExecuteChanged();
        ImportCommand?.NotifyCanExecuteChanged();
        ResetCommand?.NotifyCanExecuteChanged();
        SaveCommand?.NotifyCanExecuteChanged();
        UndoCommand?.NotifyCanExecuteChanged();
    }
}
