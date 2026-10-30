using Universum.Core.Subtitles;

namespace Universum.Core.Playback;

/// <summary>
/// Subtitles as the player shows them: in order of time, the ones shown at a moment found by a binary search (also when
/// they overlap), the previous and the next one for stepping through them.
/// </summary>
public sealed class SubtitleTimeline
{
    private readonly SubtitleCue[] _cues;
    private readonly TimeSpan _longest;

    public SubtitleTimeline(IEnumerable<SubtitleCue> cues)
    {
        _cues = cues.Where(c => c.End > c.Start).OrderBy(c => c.Start).ThenBy(c => c.End).ToArray();
        _longest = _cues.Length == 0 ? TimeSpan.Zero : _cues.Max(c => c.End - c.Start);
    }

    public static SubtitleTimeline Empty { get; } = new([]);

    public IReadOnlyList<SubtitleCue> Cues => _cues;

    /// <summary>The subtitles shown at <paramref name="time"/> (usually one or none).</summary>
    public IReadOnlyList<SubtitleCue> At(TimeSpan time)
    {
        var shown = new List<SubtitleCue>(2);
        // every cue that starts after time - longest cannot be over yet; earlier ones are
        for (var i = LastStartingAtOrBefore(time); i >= 0 && _cues[i].Start >= time - _longest; i--)
        {
            if (_cues[i].End > time)
            {
                shown.Insert(0, _cues[i]);
            }
        }

        return shown;
    }

    /// <summary>The index of the subtitle that starts last at or before <paramref name="time"/>; −1 when none.</summary>
    public int LastStartingAtOrBefore(TimeSpan time)
    {
        int low = 0, high = _cues.Length - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (_cues[middle].Start <= time)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    /// <summary>The first subtitle that starts after <paramref name="time"/>.</summary>
    public SubtitleCue? Next(TimeSpan time) => LastStartingAtOrBefore(time) + 1 is var next && next < _cues.Length ? _cues[next] : null;

    /// <summary>The last subtitle that starts before <paramref name="time"/> (a little before: stepping back while one is shown).</summary>
    public SubtitleCue? Previous(TimeSpan time)
    {
        var index = LastStartingAtOrBefore(time - TimeSpan.FromMilliseconds(300));
        return index >= 0 ? _cues[index] : null;
    }

    /// <summary>The subtitle whose start is closest to <paramref name="time"/> (the one the viewer means).</summary>
    public SubtitleCue? Nearest(TimeSpan time)
    {
        var before = LastStartingAtOrBefore(time);
        var candidates = new[] { before, before + 1 }.Where(i => i >= 0 && i < _cues.Length).Select(i => _cues[i]);
        return candidates.MinBy(c => (c.Start - time).Duration());
    }
}
