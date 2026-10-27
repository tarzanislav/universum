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

    public BatchGroup Group { get; } = group;

    public string Label => Group.Label;

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

    public SlotRow(BatchGroup group, SlotKey key, MediaTrack track, Action changed)
    {
        Group = group;
        Key = key;
        Track = track;
        _changed = changed;
        Refresh();
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

    /// <summary>Language chosen for the whole group (only for undetermined tracks).</summary>
    public Language? ChosenLanguage
    {
        get => Group.Decisions.Languages.TryGetValue(Key, out var tag) ? Languages.Find(tag.Primary) : null;
        set
        {
            if (value is null)
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

    public string Evidence => Texts.Evidence(_evidence);

    public bool HasSuggestion => _evidence is { Confidence: Confidence.Medium };

    public string Suggested => _evidence is { Confidence: Confidence.Medium } suggestion ? Texts.Language(suggestion.Language) : "";

    public bool IsSubtitles => Key.Type == TrackType.Subtitles;

    public bool ForcedSuggested => Group.ForcedSuggestions.Contains(Key);

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

    public string ForcedHint => ForcedSuggested ? "návrh: forced (málo titulkov)" : Track.Flags.Forced ? "forced v súbore" : "";

    /// <summary>Accepts the suggestion (language and/or forced) as the group's decision.</summary>
    public bool ConfirmSuggestion()
    {
        var changed = false;
        if (_evidence is { Confidence: Confidence.Medium } suggestion)
        {
            Group.Decisions.Languages[Key] = suggestion.Language;
            changed = true;
        }

        if (ForcedSuggested && !Group.Decisions.Forced.ContainsKey(Key))
        {
            Group.Decisions.Forced[Key] = true;
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
    }
}

/// <summary>One file of the selected group with its simulated result.</summary>
public sealed class FileRow(BatchItem item, Action<FileRow> processAnywayChanged, BatchPageViewModel page) : ObservableObject
{
    private CommunityToolkit.Mvvm.Input.RelayCommand? _confirm;

    public BatchItem Item { get; private set; } = item;

    public string FileName => System.IO.Path.GetFileName(Item.File.Path);

    /// <summary>Film title ("Creep (2004)") or episode ("S01E04"), editable; empty while undecided.</summary>
    public string Naming
    {
        get
        {
            if (page.Library is not { } library)
            {
                return "";
            }

            if (library.Kind == MediaKind.Series)
            {
                return library.EpisodeFor(Item.File.Path) is ({ } number, false) ? number.Tag : "";
            }

            return library.TitleFor(Item.File.Path)?.FolderName ?? "";
        }
        set => page.SetNaming(Item.File.Path, value);
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

            if (library.Kind == MediaKind.Series)
            {
                return library.EpisodeFor(Item.File.Path) is ({ } number, true) ? "návrh: " + number.Tag : "";
            }

            return library.SuggestedTitleFor(Item.File.Path) is { } title
                ? "návrh: " + title.FolderName + (page.UnverifiedNote(Item.File.Path) is { } why ? " ⚠ " + why : "")
                : "";
        }
    }

    /// <summary>The suggested title waits for this file's own confirmation (HISTRA did not confirm it).</summary>
    public bool IsUnverified =>
        page.Library is { Kind: MediaKind.Movie } library && library.TitleFor(Item.File.Path) is null && page.UnverifiedNote(Item.File.Path) is not null;

    /// <summary>Confirms the suggested title HISTRA did not confirm.</summary>
    public CommunityToolkit.Mvvm.Input.RelayCommand ConfirmCommand => _confirm ??= new(() => page.ConfirmTitle(Item.File.Path));

    /// <summary>Planned place in the library, relative to the output folder.</summary>
    public string Target => Item.PlannedPath is { } path && page.Library is { } library ? System.IO.Path.GetRelativePath(library.Layout.Root, path) : "";

    public string Status => Texts.Status(Item.Status);

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
        OnPropertyChanged(string.Empty);
    }
}
