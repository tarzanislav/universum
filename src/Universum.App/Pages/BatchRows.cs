using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.Batch;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>A structure group in the list of groups.</summary>
public sealed class GroupRow(BatchGroup group) : ObservableObject
{
    private string _state = "";
    private string _title = "";

    public BatchGroup Group { get; } = group;

    public string Label => Group.Label;

    /// <summary>What the group holds, by the titles of its files without year and ID: "Zlatý kapor", "Zlatý kapor + 2 ďalšie".</summary>
    public string Title
    {
        get => _title.Length > 0 ? _title : "Skupina " + Label;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(Tooltip));
            }
        }
    }

    /// <summary>"Skupina A: Zlatý kapor + 2 ďalšie" – the letter the report and universum-cli use.</summary>
    public string Tooltip => $"Skupina {Label}: {Title}";

    public int Count => Group.Files.Count;

    public string Signature => Group.Signature;

    public string State { get => _state; set => SetProperty(ref _state, value); }

    /// <summary>A file was added while the folder is read.</summary>
    public void Refresh() => OnPropertyChanged(nameof(Count));
}

/// <summary>One audio or subtitle position of a group: language and forced decisions for all its files.</summary>
public sealed class SlotRow : ObservableObject
{
    private readonly Action _changed;
    private LanguageEvidence? _evidence;

    private readonly Action<SlotRow, int>? _move;
    private readonly RuleProfile _profile;
    private bool _removedByLanguage;
    private string? _rulesName;

    public SlotRow(BatchGroup group, SlotKey key, MediaTrack track, Action changed, Action<SlotRow, int>? move = null, RuleProfile? profile = null)
    {
        _profile = profile ?? RuleProfile.Default;
        Group = group;
        Key = key;
        Track = track;
        _changed = changed;
        _move = move;
        MoveUpCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _move?.Invoke(this, -1), () => !_removedByLanguage);
        MoveDownCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _move?.Invoke(this, 1), () => !_removedByLanguage);
        Refresh();
    }

    /// <summary>Moves the track one place up among the tracks of its type (owner, 29. 10. 2026).</summary>
    public CommunityToolkit.Mvvm.Input.RelayCommand MoveUpCommand { get; }

    public CommunityToolkit.Mvvm.Input.RelayCommand MoveDownCommand { get; }

    /// <summary>
    /// The subtitles are left out: their language is not among the subtitle languages (Nastavenia) nor the audio's.
    /// </summary>
    public bool IsRemovedByLanguage => _removedByLanguage;

    /// <summary>The user keeps the subtitles although their language is not in the list.</summary>
    public bool Kept
    {
        get => Group.Decisions.Kept.Contains(Key);
        set
        {
            if (value)
            {
                Group.Decisions.Kept.Add(Key);
            }
            else
            {
                Group.Decisions.Kept.Remove(Key);
            }

            OnPropertyChanged();
            _changed();
        }
    }

    /// <summary>Subtitles out of the language list: the user may keep them.</summary>
    public bool CanKeep => IsSubtitles && (_removedByLanguage || Kept);

    /// <summary>What happens with the track in the result when it is not just kept.</summary>
    public string ResultNote => _removedByLanguage ? "odstráni sa – jazyk nie je v zozname titulkov (Nastavenia)"
        : Kept ? "ponechané – jazyk mimo zoznamu titulkov"
        : "";

    /// <summary>An audio or subtitle track: its name can be written for the whole group (owner, 30. 10. 2026).</summary>
    public bool CanName => Key.Type is TrackType.Audio or TrackType.Subtitles;

    /// <summary>
    /// Track name: the name the result gets – the rules' one, or the one written here for the whole group (it wins over
    /// the rules'); empty = no name.
    /// </summary>
    public string TrackName
    {
        get => Group.Decisions.Names.TryGetValue(Key, out var name) ? name : _rulesName ?? "";
        set
        {
            value = value.Trim();
            if (!CanName || value == TrackName)
            {
                return;
            }

            if (value == (_rulesName ?? ""))
            {
                Group.Decisions.Names.Remove(Key);
            }
            else
            {
                Group.Decisions.Names[Key] = value;
            }

            OnPropertyChanged();
            _changed();
        }
    }

    /// <summary>Called after a recalculation with the name the track gets in the group's first file.</summary>
    public void SetResultName(string? name)
    {
        if (!Group.Decisions.Names.ContainsKey(Key))
        {
            _rulesName = name;
            OnPropertyChanged(nameof(TrackName));
        }
    }

    /// <summary>Called after a recalculation with what the rules do with the track in the group's first file.</summary>
    public void SetRemovedByLanguage(bool removed)
    {
        _removedByLanguage = removed;
        OnPropertyChanged(nameof(IsRemovedByLanguage));
        OnPropertyChanged(nameof(CanKeep));
        OnPropertyChanged(nameof(ResultNote));
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    public BatchGroup Group { get; }

    public SlotKey Key { get; }

    public MediaTrack Track { get; }

    public string Slot => Texts.Slot(Key);

    public string Codec => Track.Codec;

    public string Name => Track.Name ?? "";

    public bool NeedsLanguage => Track.Language.IsUndetermined;

    public string FileLanguage => Texts.Language(Track.Language);

    public IReadOnlyList<Language> LanguageChoices => Languages.All;

    /// <summary>
    /// Language chosen for the whole group; without a choice the file's language, or the language named in the track
    /// name ("CZ titulky"), which is used as it is – another choice overrides it (owner, 29. 10. 2026: also a wrong
    /// language written in the file, "EN" that is Czech).
    /// </summary>
    public Language? ChosenLanguage
    {
        get => Group.Decisions.Languages.TryGetValue(Key, out var tag) ? Languages.Find(tag.Primary)
            : !NeedsLanguage ? Languages.Find(Track.Language.Primary)
            : _evidence is { Source: EvidenceSource.TrackName } named ? Languages.Find(named.Language.Primary)
            : null;
        set
        {
            // the file's own language again: no decision is needed
            if (value is null || (!NeedsLanguage && string.Equals(LanguageTag.FromCode(value.Ietf).Primary, Track.Language.Primary, StringComparison.OrdinalIgnoreCase)))
            {
                Group.Decisions.Languages.Remove(Key);
            }
            else
            {
                Group.Decisions.Languages[Key] = LanguageTag.FromCode(value.Ietf);
            }

            OnPropertyChanged();
            _changed();
        }
    }

    /// <summary>
    /// What decides the language for the group; without a group decision the suggestions heard in the files themselves
    /// are summed up (owner, 29. 10. 2026: "návrhy pri súboroch: angličtina 20×, čeština 2×") – each file has its own.
    /// </summary>
    public string Evidence => _evidence is null && FileSuggestions is { Length: > 0 } files
        ? $"návrhy pri súboroch: {files} – potvrďte ich tlačidlom Potvrdiť návrhy (alebo vyberte jazyk pre celú skupinu)"
        : !NeedsLanguage && _evidence is { Source: EvidenceSource.GroupDecision }
            ? $"vaša voľba (v súbore: {Texts.Language(Track.Language)})"
            : Texts.Evidence(_evidence);

    // the files' own suggestions for this track, by language: "angličtina 20×, čeština 2×"
    private string FileSuggestions => string.Join(", ", Group.FileSuggestions.Values
        .Select(s => s.TryGetValue(Key, out var suggestion) ? suggestion : null).OfType<LanguageEvidence>()
        .GroupBy(s => s.Language.Primary).OrderByDescending(g => g.Count())
        .Select(g => $"{Texts.Language(g.First().Language)} {g.Count()}×"));

    public bool HasSuggestion => _evidence is { Confidence: Confidence.Medium };

    /// <summary>A doubtful suggestion that "Potvrdiť návrhy" leaves for the user's own choice at the track.</summary>
    public bool NeedsOwnConfirmation => _evidence is { Confidence: Confidence.Medium, NeedsOwnConfirmation: true };

    /// <summary>The language is named in the track name ("CZ Dabing") and used without a choice.</summary>
    public bool IsNamedInTrack => _evidence is { Source: EvidenceSource.TrackName } && !Group.Decisions.Languages.ContainsKey(Key);

    public string Suggested => _evidence is { Confidence: Confidence.Medium } suggestion ? Texts.Language(suggestion.Language) : "";

    public bool IsSubtitles => Key.Type == TrackType.Subtitles;


    /// <summary>Forced decision for the group: true / false, or null = by the file (flag and name).</summary>
    public bool? Forced
    {
        get => Group.Decisions.Forced.TryGetValue(Key, out var forced) ? forced : null;
        set
        {
            if (value is null)
            {
                Group.Decisions.Forced.Remove(Key);
            }
            else
            {
                Group.Decisions.Forced[Key] = value.Value;
            }

            OnPropertyChanged();
            _changed();
        }
    }

    /// <summary>
    /// Why the subtitles are forced without a choice of the user (owner, 30. 10. 2026: also by their small amount –
    /// UNIVERSUM's suggestion, changed with the check box).
    /// </summary>
    public string ForcedHint => !IsSubtitles || Group.Decisions.Forced.ContainsKey(Key) ? ""
        : Track.Flags.Forced ? "forced v súbore"
        : Core.Text.Words.ContainsAny(Track.Name, _profile.ForcedKeywords) ? "forced podľa názvu"
        : ForcedSubtitles.ByAmount(Group.Representative, Track) is { } amount ? $"NÁVRH UNIVERSUM: forced – {amount}"
        : "";

    /// <summary>Accepts the language suggestion as the group's decision.</summary>
    public bool ConfirmSuggestion()
    {
        var changed = false;
        if (_evidence is { Confidence: Confidence.Medium, NeedsOwnConfirmation: false } suggestion)
        {
            Group.Decisions.Languages[Key] = suggestion.Language;
            changed = true;
        }

        return changed;
    }

    public void Refresh()
    {
        _evidence = BatchPlanner.Evidence(Track, Key, Group);
        OnPropertyChanged(nameof(ChosenLanguage));
        OnPropertyChanged(nameof(Evidence));
        OnPropertyChanged(nameof(HasSuggestion));
        OnPropertyChanged(nameof(Suggested));
        OnPropertyChanged(nameof(Forced));
        OnPropertyChanged(nameof(ForcedHint));
        OnPropertyChanged(nameof(Kept));
        OnPropertyChanged(nameof(CanKeep));
        OnPropertyChanged(nameof(ResultNote));
    }
}

/// <summary>
/// One audio track without a language in one file: the language chosen for this file, and what the file's own speech
/// (or the group) suggests – the films of one group may be spoken in different languages.
/// </summary>
public sealed class FileSlotRow : ObservableObject
{
    private readonly Action _changed;
    private LanguageEvidence? _evidence;

    public FileSlotRow(BatchGroup group, string path, SlotKey key, MediaTrack track, Action changed)
    {
        Group = group;
        Path = path;
        Key = key;
        Track = track;
        _changed = changed;
        Choices = new PrependedList<FileLanguageItem>(new FileLanguageItem(null, ""), FileLanguageItem.All);
        Refresh();
    }

    public BatchGroup Group { get; }

    public string Path { get; }

    public SlotKey Key { get; }

    public MediaTrack Track { get; }

    public string Slot => Texts.Slot(Key);

    public IReadOnlyList<Language> LanguageChoices => Languages.All;

    /// <summary>
    /// The choices of the file's list (owner, 29. 10. 2026: an empty list looked as if no language were set): first
    /// what applies without a choice for the file ("podľa skupiny (čeština)"), then the languages.
    /// </summary>
    public IReadOnlyList<FileLanguageItem> Choices { get; }

    /// <summary>The item shown in the file's list; the first one while no language is chosen for the file.</summary>
    public FileLanguageItem SelectedChoice
    {
        get => ChosenLanguage is { } language ? Choices.Skip(1).FirstOrDefault(c => c.Language == language) ?? Choices[0] : Choices[0];
        set
        {
            // WPF may push null while the list is rebuilt – never read that as "back to the group"
            if (value is null || value == SelectedChoice)
            {
                return;
            }

            ChosenLanguage = value.Language;
            OnPropertyChanged();
        }
    }

    /// <summary>The language chosen for this file's track (empty: the group's, the name's or none).</summary>
    public Language? ChosenLanguage
    {
        get => Group.Decisions.FileLanguage(Path, Key) is { } tag ? Languages.Find(tag.Primary) : null;
        set
        {
            Group.Decisions.SetFileLanguage(Path, Key, value is null ? null : LanguageTag.FromCode(value.Ietf));
            OnPropertyChanged();
            _changed();
        }
    }

    /// <summary>What decides the language when none is chosen for the file: "skupina: čeština", "návrh: … (overte)".</summary>
    public string Hint => _evidence switch
    {
        null => "neurčený – vyberte",
        { Source: EvidenceSource.FileDecision } => "",
        { Confidence: Confidence.Medium } suggestion => "návrh: " + Texts.Language(suggestion.Language) +
                                                         (suggestion.Detail is { } detail ? $" ({detail})" : "") +
                                                         (Group.FileSuggestion(Path, Key) is null ? " – zo skupiny" : ""),
        { } known => Texts.Evidence(known) + ": " + Texts.Language(known.Language),
    };

    /// <summary>This file's own suggestion (not a doubtful one) waits for "Potvrdiť návrhy".</summary>
    public bool HasOwnSuggestion => Group.FileSuggestion(Path, Key) is { NeedsOwnConfirmation: false } && _evidence is { Confidence: Confidence.Medium };

    /// <summary>Accepts this file's own suggestion as the language chosen for the file.</summary>
    public bool ConfirmSuggestion()
    {
        if (!HasOwnSuggestion || _evidence is null)
        {
            return false;
        }

        Group.Decisions.SetFileLanguage(Path, Key, _evidence.Language);
        return true;
    }

    public void Refresh()
    {
        _evidence = BatchPlanner.Evidence(Track, Key, Group, Path);
        Choices[0].Display = InheritedLabel();
        OnPropertyChanged(nameof(ChosenLanguage));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(HasOwnSuggestion));
    }

    // what the file gets without its own choice; with a choice for the file the evidence is that choice, so the first
    // item says only where it would go back to
    private string InheritedLabel() => _evidence switch
    {
        null => "nevybraté – vyberte",
        { Source: EvidenceSource.FileDecision } => "podľa skupiny",
        { Confidence: Confidence.Medium } suggestion => $"návrh ({Name(suggestion.Language)}) – overte",
        { Source: EvidenceSource.GroupDecision } known => $"podľa skupiny ({Name(known.Language)})",
        { } known => $"automaticky ({Name(known.Language)})",
    };

    private static string Name(LanguageTag tag) => Languages.Find(tag.Primary)?.SlovakName ?? Texts.Language(tag);
}

/// <summary>One item of a file's language list: a language, or (null) what applies without a choice for the file.</summary>
public sealed class FileLanguageItem(Language? language, string display) : ObservableObject
{
    private string _display = display;

    private static (IReadOnlyList<Language> Source, IReadOnlyList<FileLanguageItem> Items)? _all;

    /// <summary>The languages, shared by all lists (built again when MKVToolNix adds its languages).</summary>
    public static IReadOnlyList<FileLanguageItem> All
    {
        get
        {
            var languages = Universum.Core.Media.Languages.All;
            if (_all is not { } built || !ReferenceEquals(built.Source, languages))
            {
                built = (languages, [.. languages.Select(l => new FileLanguageItem(l, l.SlovakName))]);
                _all = built;
            }

            return built.Items;
        }
    }

    public Language? Language { get; } = language;

    public string Display
    {
        get => _display;
        set => SetProperty(ref _display, value);
    }
}

/// <summary>One file of the selected group with its simulated result.</summary>
public sealed class FileRow : ObservableObject
{
    private readonly Action<FileRow> processAnywayChanged;
    private readonly BatchPageViewModel page;
    private CommunityToolkit.Mvvm.Input.RelayCommand? _confirm;

    public FileRow(BatchItem item, Action<FileRow> processAnywayChanged, BatchPageViewModel page)
    {
        Item = item;
        this.processAnywayChanged = processAnywayChanged;
        this.page = page;
        AudioSlots = SlotKey.Of(item.File)
            .Where(x => x.Key.Type == TrackType.Audio && x.Track.Language.IsUndetermined)
            .Select(x => new FileSlotRow(item.Group, item.File.Path, x.Key, x.Track, page.Replan))
            .ToList();
    }

    public BatchItem Item { get; private set; }

    /// <summary>The file's audio tracks without a language, each with its own choice (empty for most files).</summary>
    public IReadOnlyList<FileSlotRow> AudioSlots { get; }

    public string FileName => System.IO.Path.GetFileName(Item.File.Path);

    /// <summary>Film title ("Creep (2004)") or episode ("S01E04") by the file's own kind, editable; empty while undecided.</summary>
    public string Naming
    {
        get
        {
            if (page.Library is not { } library)
            {
                return "";
            }

            if (library.KindFor(Item.File.Path) == MediaKind.Series)
            {
                return library.EpisodeFor(Item.File.Path) is ({ } number, false) ? number.Tag : "";
            }

            return library.TitleFor(Item.File.Path)?.FolderName ?? "";
        }
        set => page.SetNaming(Item.File.Path, value);
    }

    /// <summary>
    /// A TMDB ID or address typed for a film (owner, 29. 10. 2026: exact when the name finds nothing): the number is a
    /// film's ID, a miniseries is given by its address (…/tv/…). Applied when the field is left; the field then clears.
    /// </summary>
    public string TmdbId
    {
        get => "";
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                page.UseTmdbId(Item.File.Path, value);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>A suggestion waiting for confirmation, shown under the editable value.</summary>
    public string NamingSuggestion
    {
        get
        {
            if (page.Library is not { } library)
            {
                return "";
            }

            if (library.KindFor(Item.File.Path) == MediaKind.Series)
            {
                return library.EpisodeFor(Item.File.Path) is ({ } number, true) ? "návrh: " + number.Tag : "";
            }

            return library.SuggestedTitleFor(Item.File.Path) is { } title
                ? "návrh: " + title.FolderName + (page.UnverifiedNote(Item.File.Path) is { } why ? " ⚠ " + why : "")
                : "";
        }
    }

    /// <summary>The titles found for the file at "Spresniť názvy", to choose from while its title is not decided.</summary>
    public IReadOnlyList<Universum.Core.Library.Tmdb.TmdbTitle> Candidates =>
        IsFilm && page.Library is { } library && library.TitleFor(Item.File.Path) is null ? page.CandidatesFor(Item.File.Path) : [];

    /// <summary>The file is a film (its own title and the field TMDB ID); an episode has its number.</summary>
    public bool IsFilm => page.Library is { } library && library.KindFor(Item.File.Path) == MediaKind.Movie;

    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>Choosing one of <see cref="Candidates"/> names the file by it (checked in HISTRA as any title).</summary>
    public Universum.Core.Library.Tmdb.TmdbTitle? ChosenCandidate
    {
        get => null;
        set
        {
            if (value is not null)
            {
                _ = page.ChooseCandidateAsync(Item.File.Path, value);
            }
        }
    }

    /// <summary>The suggested title waits for this file's own confirmation (HISTRA did not confirm it).</summary>
    public bool IsUnverified =>
        IsFilm && page.Library is { } library && library.TitleFor(Item.File.Path) is null && page.UnverifiedNote(Item.File.Path) is not null;

    /// <summary>Confirms the suggested title HISTRA did not confirm.</summary>
    public CommunityToolkit.Mvvm.Input.RelayCommand ConfirmCommand => _confirm ??= new(() => page.ConfirmTitle(Item.File.Path));

    /// <summary>Planned place in the library, relative to the output folder.</summary>
    public string Target => Item.PlannedPath is { } path && page.Library is { } library ? System.IO.Path.GetRelativePath(library.Layout.Root, path) : "";

    /// <summary>"nový súbor", or "nový MKV z MP4" for a file of another container.</summary>
    public string Status => Item.AlreadyInLibraryAt is not null && Item.Reasons.Count == 0 ? "už v knižnici"
        : Item.Status == BatchStatus.Remux && !Item.File.IsMatroska
        ? $"nový MKV z {Item.File.ContainerName}"
        : Texts.Status(Item.Status);

    public string Reasons => string.Join("; ", Item.Reasons.Select(Texts.Reason));

    /// <summary>Status and, for files waiting for review, why.</summary>
    public string Outcome => Item.Reasons.Count == 0 ? Status : Status + ": " + Reasons;

    /// <summary>Required template tracks are missing: the user may still allow processing.</summary>
    public bool CanProcessAnyway =>
        Item.Group.Decisions.ProcessAnyway.Contains(Item.File.Path) || Item.Reasons.Any(r => r.CanBeOverridden);

    public bool ProcessAnyway
    {
        get => Item.Group.Decisions.ProcessAnyway.Contains(Item.File.Path);
        set
        {
            if (value)
            {
                Item.Group.Decisions.ProcessAnyway.Add(Item.File.Path);
            }
            else
            {
                Item.Group.Decisions.ProcessAnyway.Remove(Item.File.Path);
            }

            processAnywayChanged(this);
        }
    }

    public void Update(BatchItem item)
    {
        Item = item;
        foreach (var slot in AudioSlots)
        {
            slot.Refresh();
        }

        OnPropertyChanged(string.Empty);
    }
}

/// <summary>How the last run of Hromadné spracovanie ended.</summary>
public enum BatchFinishKind
{
    Done,
    Errors,
    Stopped,
}

/// <summary>The end of a run, shown boldly (owner, 29. 10. 2026): what happened, what came out, how long it took.</summary>
public sealed record BatchFinish(BatchFinishKind Kind, string Title, string Detail)
{
    public string Background => Kind switch
    {
        BatchFinishKind.Done => "#2F9461",
        BatchFinishKind.Errors => "#C0392B",
        _ => "#D68910",
    };

    public string Icon => Kind == BatchFinishKind.Done ? "\uE73E" : Kind == BatchFinishKind.Errors ? "\uE783" : "\uE7BA";
}

/// <summary>Where Odoslať do MEGA puts the files: a new batch (null) or a waiting one.</summary>
public sealed record MegaTarget(string Label, Universum.Core.Mega.MegaBatch? Batch);
