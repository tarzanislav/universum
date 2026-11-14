using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Localization;
using Universum.App.Templates;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Speech;
using Universum.Core.Templates;
using Universum.Core.Text;
using Universum.Platform.Windows;

namespace Universum.App.Pages;

// BatchPageViewModel: the studios opened from a file, the edits and additions kept per file
public sealed partial class BatchPageViewModel
{
    // the selected file as the analysis has it (with the subtitle files found with it) and its group
    private (MediaFile File, BatchGroup Group)? Analysed() =>
        _selectedFile?.Item is { } item && item.Group.Files.FirstOrDefault(f => Paths.Same(f.Path, item.File.Path)) is { } analysed ? (analysed, item.Group) : null;

    private void EditInStudio(bool sound)
    {
        if (Analysed() is not var (analysed, group))
        {
            return;
        }

        // the names the group gives the file's own tracks: the studio shows them until a name of its own is written
        var names = SlotKey.Of(analysed).Where(x => x.Track.ExternalFile is null && group.Decisions.Names.ContainsKey(x.Key))
            .ToDictionary(x => x.Track.Id, x => group.Decisions.Names[x.Key]);
        (sound ? SoundStudioRequested : StudioRequested)?.Invoke(analysed, group.Decisions.Studio.GetValueOrDefault(analysed.Path), names);
    }

    /// <summary>The studios' edits of a file of the batch now (each studio saves its part over them).</summary>
    public StudioEdits? StudioOf(string path) =>
        _analysis?.Groups.FirstOrDefault(g => g.Files.Any(f => Paths.Same(f.Path, path)))?.Decisions.Studio.GetValueOrDefault(path);

    /// <summary>
    /// Uložiť do úlohy: the studio's edits of a file of the batch (none: forgotten); the plan is computed again. They
    /// stay with the file's path, also when the folder is analysed again. Null when taken, else why not.
    /// </summary>
    public string? SaveStudio(string path, StudioEdits edits)
    {
        if (IsRunning || _ranSinceAnalysis || _needsAnalysis)
        {
            return IsRunning
                ? Loc.T("Hromadné spracovanie práve beží – úpravu uložte do úlohy po jeho skončení a novej analýze.")
                : Loc.T("Hromadné spracovanie už prebehlo (alebo čaká na novú analýzu) – analyzujte priečinok znova a úpravu uložte potom.");
        }

        if (_analysis?.Groups.FirstOrDefault(g => g.Files.Any(f => Paths.Same(f.Path, path))) is not { } group)
        {
            return Loc.T("Súbor už nie je v Hromadnom spracovaní – analyzujte priečinok znova.");
        }

        var key = group.Files.First(f => Paths.Same(f.Path, path)).Path;
        if (edits.IsEmpty)
        {
            group.Decisions.Studio.Remove(key);
            _studioEdits.Remove(key);
        }
        else
        {
            group.Decisions.Studio[key] = edits;
            _studioEdits[key] = edits;
        }

        Replan();
        RunState = Loc.F("{0}: úpravy zo štúdií v úlohe – {1}.", Path.GetFileName(path), edits.Describe());
        return null;
    }

    // the studio's edits by the files' paths: given again to the groups of every new analysis of the folder
    private readonly Dictionary<string, StudioEdits> _studioEdits = new(Paths.Comparer);

    // the files of the analyses before (of the same folder) and the place of each one's group in the list
    private readonly HashSet<string> _knownFiles = new(Paths.Comparer);
    private readonly Dictionary<string, int> _groupRanks = new(Paths.Comparer);
    private string? _knownFolder;

    /// <summary>
    /// The groups in the order of the analysis before (owner, 31. 10. 2026): a group with files known before keeps its
    /// place; groups of only new files (folders and files put into the input folder since) follow at the end. A group
    /// with new files is shown on a pastel background. The first analysis of a folder keeps the analysis' own order.
    /// </summary>
    private List<GroupRow> Arranged(string folder, IReadOnlyList<BatchGroup> groups)
    {
        if (_knownFolder is null || !Paths.Same(_knownFolder, folder))
        {
            _knownFiles.Clear();
            _groupRanks.Clear();
            _knownFolder = folder;
        }

        var before = _knownFiles.Count > 0;
        var rows = groups.Select((group, index) =>
        {
            var ranks = group.Files.Where(f => _groupRanks.ContainsKey(f.Path)).Select(f => _groupRanks[f.Path]).ToList();
            return (Row: new GroupRow(group) { Title = GroupTitle(group), IsNew = before && group.Files.Any(f => !_knownFiles.Contains(f.Path)) },
                Rank: ranks.Count > 0 ? ranks.Min() : int.MaxValue, Index: index);
        }).OrderBy(x => x.Rank).ThenBy(x => x.Index).Select(x => x.Row).ToList();

        for (var i = 0; i < rows.Count; i++)
        {
            foreach (var file in rows[i].Group.Files)
            {
                _knownFiles.Add(file.Path);
                _groupRanks[file.Path] = i;
            }
        }

        return rows;
    }

    // Doplnok k názvu by the files' paths: given again to the groups of every new analysis of the folder
    private readonly Dictionary<string, string> _additions = new(Paths.Comparer);

    // Revízia by the files' paths, kept as Doplnok k názvu
    private readonly HashSet<string> _revisions = new(Paths.Comparer);

    /// <summary>
    /// Revízia of one file (owner, 31. 10. 2026): written again although its result is in the library; the new version is
    /// verified, then the earlier one goes to the Recycle Bin.
    /// </summary>
    public void SetRevision(string path, bool revision)
    {
        if (_analysis?.Groups.FirstOrDefault(g => g.Files.Any(f => Paths.Same(f.Path, path))) is not { } group || IsRunning)
        {
            return;
        }

        var key = group.Files.First(f => Paths.Same(f.Path, path)).Path;
        if (revision ? !group.Decisions.Revisions.Add(key) : !group.Decisions.Revisions.Remove(key))
        {
            return;
        }

        if (revision)
        {
            _revisions.Add(key);
        }
        else
        {
            _revisions.Remove(key);
        }

        Replan();
        RunState = revision
            ? Loc.F("{0}: revízia – spracuje sa znova; nová verzia sa zapíše a overí, až potom ide doterajší výsledok do Koša.", Path.GetFileName(path))
            : Loc.F("{0}: bez revízie.", Path.GetFileName(path));
    }

    private void ApplyStudioEdits()
    {
        foreach (var group in _analysis?.Groups ?? [])
        {
            foreach (var file in group.Files)
            {
                if (_studioEdits.TryGetValue(file.Path, out var edits))
                {
                    group.Decisions.Studio[file.Path] = edits;
                }

                if (_additions.TryGetValue(file.Path, out var addition))
                {
                    group.Decisions.Additions[file.Path] = addition;
                }

                if (_revisions.Contains(file.Path))
                {
                    group.Decisions.Revisions.Add(file.Path);
                }
            }
        }
    }

    /// <summary>
    /// Doplnok k názvu of one file (owner, 31. 10. 2026: written or chosen – "Directors Cut", "Remaster"); it goes into the
    /// name before the size class. A text that cannot be used is said and not taken.
    /// </summary>
    public void SetAddition(string path, string? text)
    {
        if (_analysis?.Groups.FirstOrDefault(g => g.Files.Any(f => Paths.Same(f.Path, path))) is not { } group || IsRunning)
        {
            return;
        }

        var key = group.Files.First(f => Paths.Same(f.Path, path)).Path;
        var (addition, problem) = LibraryNames.Addition(text);
        if (problem is not null)
        {
            RunState = $"{Path.GetFileName(path)}: {problem}";
            return;
        }

        if (addition == group.Decisions.Additions.GetValueOrDefault(key))
        {
            return;
        }

        if (addition is null)
        {
            group.Decisions.Additions.Remove(key);
            _additions.Remove(key);
        }
        else
        {
            group.Decisions.Additions[key] = addition;
            _additions[key] = addition;
        }

        Replan();
        RunState = addition is null ? Loc.F("{0}: bez doplnku k názvu.", Path.GetFileName(path)) : Loc.F("{0}: doplnok k názvu „{1}“ – nová verzia titulu v knižnici.", Path.GetFileName(path), addition);
    }
}
