using CommunityToolkit.Mvvm.Input;
using Universum.Core.Media;
using Universum.Core.Muxing;

namespace Universum.App.Pages;

/// <summary>
/// The order of the tracks in Zlučovanie (owner, 1. 11. 2026): it follows a language as soon as it is set (CZ at the top,
/// SK under it, the others after them, commentary last), and each track can be moved by hand – by its arrows or by its
/// grip, dragged to another track (as in Hromadné spracovanie).
/// </summary>
public sealed partial class MuxSettingsViewModel
{
    private RelayCommand<object?>? _moveRowUpCommand;
    private RelayCommand<object?>? _moveRowDownCommand;

    /// <summary>The arrow of a track: one place up (the rows of appended files are not moved).</summary>
    public RelayCommand<object?> MoveRowUpCommand => _moveRowUpCommand ??= new RelayCommand<object?>(row => MoveRow(row as MuxTrackRow, -1), row => CanMoveRow(row as MuxTrackRow, -1));

    /// <summary>The arrow of a track: one place down.</summary>
    public RelayCommand<object?> MoveRowDownCommand => _moveRowDownCommand ??= new RelayCommand<object?>(row => MoveRow(row as MuxTrackRow, +1), row => CanMoveRow(row as MuxTrackRow, +1));

    /// <summary>A track dragged by its grip onto another one takes its place (the others move by one).</summary>
    public void MoveTrackTo(MuxTrackRow dragged, MuxTrackRow target)
    {
        var from = Tracks.IndexOf(dragged);
        var to = Tracks.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
        {
            return;
        }

        _arrangeLater.Cancel();
        _sortLater.Clear();
        Tracks.Move(from, to);
        SelectedTrack = dragged;
        Changed();
    }

    private void MoveRow(MuxTrackRow? row, int step)
    {
        if (row is null || !CanMoveRow(row, step))
        {
            return;
        }

        _arrangeLater.Cancel();
        _sortLater.Clear();
        var index = Tracks.IndexOf(row);
        Tracks.Move(index, index + step);
        SelectedTrack = row;
        Changed();
    }

    private bool CanMoveRow(MuxTrackRow? row, int step) =>
        row is not null && Tracks.IndexOf(row) is var index and >= 0 && index + step >= 0 && index + step < Tracks.Count;

    // a language (or Forced) set: the taken tracks of its type in the order of the rules – two seconds after the last
    // change, so the track does not jump away from under the mouse (owner, 1. 11. 2026); the other tracks keep their places
    private void FollowLanguage(MuxTrackRow row)
    {
        if (_loading || !row.Include || row.Kind is not (TrackType.Audio or TrackType.Subtitles))
        {
            return;
        }

        _sortLater.Add(row.Kind);
        _arrangeLater.Schedule(() =>
        {
            var moved = false;
            foreach (var type in _sortLater.Order().ToList())
            {
                moved |= SortTracks(type);
            }

            _sortLater.Clear();
            if (moved)
            {
                Changed();
            }
        });
    }

    private readonly ArrangeLater _arrangeLater = new();
    private readonly HashSet<TrackType> _sortLater = [];

    /// <summary>The taken tracks of the type put in the order of the rules, in the places the tracks of the type have.</summary>
    internal bool SortTracks(TrackType type)
    {
        var taken = Tracks.Where(t => t.Include).ToList();
        var order = MuxRules.OrderOfType(Inputs.Select(i => i.ToSource()).ToList(), taken.Select(t => t.ToTrack(Inputs.IndexOf(t.Input))).ToList(), type, _services.Profile);
        var sorted = new Queue<MuxTrackRow>(order.Select(i => taken[i]));
        var wanted = Tracks.Select(t => t.Include && t.Kind == type ? sorted.Dequeue() : t).ToList();
        if (wanted.SequenceEqual(Tracks))
        {
            return false;
        }

        var selected = SelectedTrack;
        for (var i = 0; i < wanted.Count; i++)
        {
            if (!ReferenceEquals(Tracks[i], wanted[i]))
            {
                Tracks.Move(Tracks.IndexOf(wanted[i]), i);
            }
        }

        if (selected is not null)
        {
            SelectedTrack = selected;
        }

        return true;
    }

    private void RefreshMoves()
    {
        _moveRowUpCommand?.NotifyCanExecuteChanged();
        _moveRowDownCommand?.NotifyCanExecuteChanged();
    }
}
