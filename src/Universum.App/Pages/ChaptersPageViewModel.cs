using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Localization;
using Universum.Core.Chapters;
using Universum.Core.Execution;
using Universum.Core.Planning;
using Universum.Core.Rules;
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
    private string _summary = Loc.T("Otvorte súbor MKV alebo ho sem pretiahnite myšou.");
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
        Library = new LibraryTargetPicker(services, Target);
        Library.Changed += RefreshCommands;
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !_busy);
        AddCommand = new RelayCommand(Add, CanEdit);
        RemoveCommand = new RelayCommand(Remove, () => CanEdit() && _selected is not null);
        RemoveAllCommand = new RelayCommand(RemoveAll, () => CanEdit() && Rows.Count > 0);
        SortCommand = new RelayCommand(Sort, () => CanEdit() && Rows.Count > 1);
        GenerateCommand = new RelayCommand(Generate, () => CanEdit() && _file?.Duration is not null);
        ShiftCommand = new RelayCommand(ShiftAll, () => CanEdit() && Rows.Count > 0);
        ImportCommand = new RelayCommand(Import, CanEdit);
        ResetCommand = new AsyncRelayCommand(() => ReadAsync(_file!.Path), () => !_busy && _file is not null);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanEdit() && _dirty && Problems.Length == 0 && Library.Placement is not null);
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

    /// <summary>Where the result goes in the library (title, episode, name by the rules); the original is never changed.</summary>
    public LibraryTargetPicker Library { get; }

    /// <summary>The file with the chapters of the list (tracks unchanged); null without a file.</summary>
    public TargetState? Target() =>
        _file is null ? null : ManualTarget.Create(_file, ManualTarget.AsIs(_file), title: "") with { NewChapters = Rows.Select(r => r.ToChapter()).OfType<Chapter>().ToList() };

    public async Task LoadAsync(string path)
    {
        _undoRecord = null;
        Result = "";
        await ReadAsync(path);
        Library.Guess(path);
    }

    /// <summary>Chapters every <see cref="Interval"/> minutes over the whole file (replacing the list).</summary>
    public void Generate()
    {
        if (_file?.Duration is not { } duration ||
            !double.TryParse(Interval.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
        {
            Result = Loc.T("Zadajte interval v minútach (napríklad 10).");
            return;
        }

        Fill(ChapterChecks.Every(TimeSpan.FromMinutes(minutes), duration, Language()), dirty: true);
        Result = Loc.F("Vygenerované: {0} každých {1} min.", Count(Rows.Count), minutes);
    }

    private static string Count(int chapters) => chapters switch
    {
        1 => Loc.T("1 kapitola"),
        >= 2 and <= 4 => Loc.F("{0} kapitoly", chapters),
        _ => Loc.F("{0} kapitol", chapters),
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
            Summary = Loc.T("MKVToolNix nie je k dispozícii. Nastavte ho tlačidlom MKVToolNix dole.");
            RefreshCommands();
            return;
        }

        _busy = true;
        try
        {
            _file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(path);
            var set = await Editor(installation).ReadAsync(path);
            _complex = set.Complex;
            Summary = Loc.F("Trvanie {0}, kapitoly: {1}", _file.Duration is { } d ? ChapterTime.Format(d) : Loc.T("neznáme"), set.Chapters.Count) +
                      (set.Complex ? Loc.T(". Súbor má viac vydaní alebo vnorené kapitoly – upravte ich v MKVToolNix GUI (UNIVERSUM ich nemení).") : ".");
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

        var problems = Rows.Where(r => !r.IsValid).Select(r => Loc.F("Kapitola {0}: čas „{1}“ nie je v tvare HH:MM:SS.mmm.", r.Number, r.Start)).ToList();
        problems.AddRange(ChapterChecks.Problems(Rows.Select(r => r.ToChapter()).OfType<Chapter>().ToList(), _file?.Duration));
        Problems = string.Join("\n", problems);
        Library?.Update();
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
        Result = Loc.T("Po uložení súbor nebude mať žiadne kapitoly.");
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
            Result = Loc.T("Zadajte posun v sekundách (napríklad 2,5 alebo -1).");
            return;
        }

        foreach (var row in Rows.Where(r => r.StartTime is not null))
        {
            var moved = row.StartTime!.Value + TimeSpan.FromSeconds(seconds);
            row.Start = ChapterTime.Format(moved < TimeSpan.Zero ? TimeSpan.Zero : moved);
        }

        Result = Loc.F("Všetky kapitoly sú posunuté o {0} s.", seconds);
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Kapitoly") + " (*.xml;*.txt)|*.xml;*.txt|" + Loc.T("Všetky súbory") + "|*.*" };
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
            Result = Loc.F("Načítané zo súboru: {0}.", Count(chapters.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
        {
            Result = Loc.T("Súbor s kapitolami sa nepodarilo prečítať: ") + ex.Message;
        }
    }

    /// <summary>Writes a copy with the new chapters into the library (named by the rules); the original stays as it is.</summary>
    private async Task SaveAsync()
    {
        if (Target() is not { } target || _services.CurrentMkvToolNix is not { } installation || Library.Placement is not { } placement)
        {
            return;
        }

        _busy = true;
        RefreshCommands();
        try
        {
            var plan = EditPlan.For(target);
            var started = DateTime.UtcNow;
            var result = await _services.Processor(installation).ProcessAsync(plan, placement, new OutputOptions(OriginalHandling.Keep));
            _services.LogProcessing("Kapitoly", plan, result, started);
            _services.Inspector(installation).Invalidate(target.Source.Path);
            var count = Count(target.NewChapters!.Count);
            switch (result.Status)
            {
                case ProcessingStatus.Done:
                    _undoRecord = result.UndoRecordPath;
                    await ReadAsync(result.OutputPath!);
                    Result = Loc.F("Uložené do knižnice ({0}): {1}.", count, LibraryPath(result.OutputPath)) +
                             (_undoRecord is null ? Loc.T(" Originál ostal nezmenený.") : Loc.T(" Zmenu súboru v knižnici je možné vrátiť späť."));
                    break;
                case ProcessingStatus.AlreadyInLibrary:
                    Result = Loc.F("Rovnaký výsledok už v knižnici je: {0}. Nič sa nezapísalo.", LibraryPath(result.OutputPath));
                    break;
                default:
                    Result = (FileProcessor.NeedsAttention(result)
                        ? Loc.T("POZOR: nepodarilo sa a súbor v knižnici sa nepodarilo úplne vrátiť – záznam: ") + result.UndoRecordPath
                        : Loc.T("Nepodarilo sa – nič sa nezmenilo.")) + "\n" + string.Join("\n", result.Messages);
                    break;
            }
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    private string LibraryPath(string? path) =>
        path is null ? "" : _services.Library is { } layout && layout.Contains(path) ? Path.GetRelativePath(layout.Root, path) : path;

    /// <summary>Puts back the chapters of a library file that was edited in place.</summary>
    private async Task UndoAsync()
    {
        if (_undoRecord is null || _services.CurrentMkvToolNix is not { } installation || _file is null)
        {
            return;
        }

        var path = _file.Path;
        var problems = await _services.Processor(installation).UndoAsync(_undoRecord);
        _services.Inspector(installation).Invalidate(path);
        if (problems.Count == 0)
        {
            _undoRecord = null;
            await ReadAsync(path);
            Result = Loc.T("Pôvodné kapitoly sú vrátené.");
        }
        else
        {
            Result = Loc.T("Vrátenie sa nepodarilo:") + "\n" + string.Join("\n", problems);
        }
    }

    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Súbory Matroska") + " (*.mkv;*.mka;*.mks)|*.mkv;*.mka;*.mks|" + Loc.T("Všetky súbory") + "|*.*" };
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
