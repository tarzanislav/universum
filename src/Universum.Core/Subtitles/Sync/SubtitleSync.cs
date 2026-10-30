namespace Universum.Core.Subtitles.Sync;

/// <summary>
/// Activity of one part of the film, a hundred values a second: +1 where someone speaks (or, for reference subtitles, a
/// subtitle is shown), −1 elsewhere.
/// </summary>
/// <param name="Start">Where the part starts in the film.</param>
public sealed record ActivityWindow(TimeSpan Start, sbyte[] Values)
{
    /// <summary>Values a second.</summary>
    public const int Rate = 100;

    public TimeSpan Length => TimeSpan.FromSeconds((double)Values.Length / Rate);

    /// <summary>The activity of subtitles shown in the part (reference subtitles that fit the film).</summary>
    public static ActivityWindow FromCues(IEnumerable<(TimeSpan Start, TimeSpan End)> cues, TimeSpan start, TimeSpan length)
    {
        var values = new sbyte[(int)(length.TotalSeconds * Rate)];
        Array.Fill(values, (sbyte)-1);
        foreach (var (from, to) in cues)
        {
            var first = Math.Max(0, (int)((from - start).TotalSeconds * Rate));
            var last = Math.Min(values.Length, (int)((to - start).TotalSeconds * Rate));
            for (var i = first; i < last; i++)
            {
                values[i] = 1;
            }
        }

        return new ActivityWindow(start, values);
    }
}

/// <summary>Where one part of the film says the subtitles are: they come <see cref="Lag"/> late (negative = early).</summary>
/// <param name="Coverage">How much of the subtitles' time has speech at the best lag (0–1).</param>
/// <param name="Margin">How much better the best lag is than any other one more than a second away.</param>
public sealed record SyncWindow(TimeSpan Center, TimeSpan Lag, double Coverage, double Margin)
{
    public bool IsClear => Margin >= SubtitleSync.MinimumMargin && Coverage >= SubtitleSync.MinimumCoverage;
}

/// <summary>The result of automatic sync: the change of the times, how sure it is, and why.</summary>
/// <param name="Timing">The change that makes the subtitles fit (SubtitleTiming.None = they fit).</param>
/// <param name="Reliable">Enough parts of the film agree.</param>
/// <param name="Detail">In Slovak: the method, the frame rate found, the parts that agree.</param>
public sealed record SyncResult(SubtitleTiming Timing, bool Reliable, string Detail, IReadOnlyList<SyncWindow> Windows)
{
    /// <summary>The change is exactly the change of the frame rate <c>From</c> → <c>To</c> (and a shift).</summary>
    public (FrameRate From, FrameRate To)? Rates { get; init; }

    /// <summary>Some parts of the film gave a clear answer, but not enough agree – a proposal to check, never applied by itself.</summary>
    public bool IsProposal => !Reliable && !Timing.IsNone && Windows.Any(w => w.IsClear);

    /// <summary>The lag the subtitles have now at a time of the film (what <see cref="Timing"/> removes).</summary>
    public TimeSpan LagAt(TimeSpan time) => time - Timing.Apply(time);
}

/// <summary>
/// Automatic sync of subtitles (owner, 30. 10. 2026): the subtitles are compared with what happens in the film – where
/// people speak (the audio track) or where other subtitles that fit the film are shown – in several parts of the film.
/// Each part says how late the subtitles are; the frame rates of films (25 ↔ 23,976 …) are tried first, then a robust
/// line through the lags of the parts gives the shift and what is left of the speed. A part counts only when its best
/// lag is clearly better than any other; the result is reliable when most parts agree.
/// </summary>
public static class SubtitleSync
{
    /// <summary>How far the subtitles are looked for around each part (late or early).</summary>
    public static readonly TimeSpan SearchRange = TimeSpan.FromSeconds(90);

    /// <summary>A part counts when its best lag is this much better than any other lag more than a second away.</summary>
    public const double MinimumMargin = 0.04;

    /// <summary>…and when at least this share of the subtitles' time has speech at it.</summary>
    public const double MinimumCoverage = 0.45;

    // parts that agree: their lags differ from the line by at most this
    private const double Agreement = 0.35;

    // what is left of the speed after the frame rates (0,1 %): more is another frame rate, never drift
    private const double MaximumDrift = 0.001;

    // a change of speed that moves no subtitle of the film by more than this is no change (seconds)
    private const double Unnoticed = 0.12;

    // two lags this far apart in the film give a slope (seconds)
    private const double SlopeBase = 60;

    /// <summary>The ratios of frame rates tried (subtitles made for one video shown with another), 1 first.</summary>
    public static IReadOnlyList<double> Scales { get; } = Pairs().Select(p => p.Ratio).Aggregate(new List<double> { 1.0 }, (list, r) =>
    {
        if (list.All(known => Math.Abs(known - r) > 0.0005))
        {
            list.Add(r);
        }

        return list;
    });

    /// <summary>
    /// The subtitles of a track by its index: a point with a duration is one subtitle; picture subtitles (PGS) without
    /// durations come in pairs – shown, cleared – and a pair longer than eight seconds is a pause, so the pairing finds
    /// its way back after a missing point; text without durations lasts until the next one, at most two seconds.
    /// </summary>
    public static IReadOnlyList<(TimeSpan Start, TimeSpan End)> Intervals(IReadOnlyList<(TimeSpan Start, TimeSpan? Duration)> points, bool picture)
    {
        var sorted = points.OrderBy(p => p.Start).ToList();
        var intervals = new List<(TimeSpan, TimeSpan)>(sorted.Count);
        var longest = TimeSpan.FromSeconds(8);
        for (var i = 0; i < sorted.Count; i++)
        {
            var (start, duration) = sorted[i];
            if (duration is { } known && known > TimeSpan.Zero)
            {
                intervals.Add((start, start + known));
            }
            else if (picture)
            {
                if (i + 1 < sorted.Count && sorted[i + 1].Duration is null && sorted[i + 1].Start - start is var shown && shown > TimeSpan.Zero && shown <= longest)
                {
                    intervals.Add((start, sorted[i + 1].Start));
                    i++;
                }
            }
            else
            {
                var next = i + 1 < sorted.Count ? sorted[i + 1].Start : TimeSpan.MaxValue;
                var end = start + TimeSpan.FromSeconds(2);
                intervals.Add((start, next > start && next < end ? next : end));
            }
        }

        return intervals;
    }

    /// <summary>
    /// The change that makes the subtitles fit the activity of the film; null when there is nothing to compare (no
    /// subtitles, no parts).
    /// </summary>
    /// <param name="cues">The subtitles to fit (their times now).</param>
    /// <param name="windows">Activity of parts of the film (speech of the audio, or reference subtitles).</param>
    /// <param name="method">"podľa zvuku" / "podľa titulkov" – for the detail.</param>
    /// <param name="tryScales">Try the frame rates (false: only a shift and a small drift).</param>
    /// <param name="filmFps">The frame rate of the film, when known: which change of frame rate to name.</param>
    public static SyncResult? Align(IReadOnlyList<(TimeSpan Start, TimeSpan End)> cues, IReadOnlyList<ActivityWindow> windows, string method,
        bool tryScales = true, double? filmFps = null)
    {
        if (cues.Count == 0 || windows.Count == 0)
        {
            return null;
        }

        (double Scale, List<SyncWindow> Parts, List<SyncWindow> Good, int Clear, double Score)? best = null;
        foreach (var scale in tryScales ? Scales : [1.0])
        {
            var parts = windows.Select(w => Measure(cues, w, scale)).OfType<SyncWindow>().ToList();
            var clear = parts.Where(p => p.IsClear).ToList();
            if (clear.Count == 0)
            {
                continue;
            }

            var good = Agreeing(clear);
            var score = good.Sum(p => p.Coverage + p.Margin);
            // more parts that agree; then the sharper answer (the right frame rate gives the clearest parts)
            if (best is null || good.Count > best.Value.Good.Count || (good.Count == best.Value.Good.Count && score > best.Value.Score))
            {
                best = (scale, parts, good, clear.Count, score);
            }
        }

        if (best is not { } found)
        {
            return new SyncResult(SubtitleTiming.None, false, $"{method}: v skúmaných častiach filmu sa nedalo nič zistiť (bez reči alebo bez titulkov).", []);
        }

        // the lag at the film's time x: d(x) = a + b·x, the subtitle for x is now at u = k·t = x + d(x)
        var (_, b) = Line(found.Good);
        var measured = found.Scale / (1 + b);
        var length = Math.Max(cues.Max(c => c.End.TotalSeconds), 60);
        var (factor, rates) = Snap(measured, length, filmFps);

        // the shift for that factor from the parts themselves: each part's subtitle t = (x + d)/k belongs at x
        var delay = found.Good.Average(p =>
        {
            var x = p.Center.TotalSeconds;
            var t = (x + p.Lag.TotalSeconds) / found.Scale;
            return x - (factor * t);
        });
        var milliseconds = (long)Math.Round(delay * 1000);
        var timing = rates is { } pair ? SubtitleTiming.Fps(pair.From, pair.To).Then(SubtitleTiming.Delay(milliseconds))
            : factor == 1 ? Math.Abs(milliseconds) < 40 ? SubtitleTiming.None : SubtitleTiming.Delay(milliseconds)
            : SubtitleTiming.FromFactor(factor, milliseconds);

        var reliable = found.Good.Count >= Math.Max(2, (windows.Count + 1) / 2) || (windows.Count <= 2 && found.Good.Count == windows.Count);
        var rate = rates is { } fps ? $"FPS titulkov {fps.From.Label} → film {fps.To.Label}, " : "";
        var detail = $"{method}: {rate}" + (timing.IsNone ? "titulky sedia" : timing.Describe()) +
                     $" – zhoduje sa {found.Good.Count} z {windows.Count} častí filmu" + (found.Clear > found.Good.Count ? $" ({found.Clear - found.Good.Count} iné)" : "") +
                     (reliable ? "." : " – NEISTÉ (titulky môžu byť posunuté o viac ako 90 s alebo patria k inej verzii filmu), overte prehraním.");
        return new SyncResult(timing, reliable, detail, found.Parts) { Rates = rates };
    }

    /// <summary>
    /// Both together (owner: audio and other subtitles): when they agree the result is sure; otherwise the more reliable
    /// one, with a warning.
    /// </summary>
    public static SyncResult? Combine(SyncResult? byAudio, SyncResult? bySubtitles, TimeSpan duration)
    {
        if (byAudio is null || !byAudio.Reliable)
        {
            return bySubtitles is { Reliable: true } || byAudio is null ? bySubtitles : byAudio;
        }

        if (bySubtitles is null || !bySubtitles.Reliable)
        {
            return byAudio;
        }

        // the two changes compared at the start, the middle and the end of the film
        var checks = new[] { TimeSpan.FromMinutes(1), duration / 2, duration };
        var apart = checks.Max(t => Math.Abs((byAudio.Timing.Apply(t) - bySubtitles.Timing.Apply(t)).TotalMilliseconds));
        return apart <= 300
            ? byAudio with { Detail = byAudio.Detail + " Titulky v inom jazyku to potvrdzujú – isté." }
            : byAudio with { Reliable = false, Detail = byAudio.Detail + $" POZOR: podľa iných titulkov to vychádza inak (rozdiel {apart / 1000:0.0} s) – overte prehraním." };
    }

    // every change of frame rate the page offers (subtitles of `From` fps shown with a film of `To` fps): times × From/To
    private static IEnumerable<(FrameRate From, FrameRate To, double Ratio)> Pairs() =>
        FrameRate.All.SelectMany(from => FrameRate.All.Where(to => to != from).Select(to => (from, to, Ratio: from.Value / to.Value)))
            .Where(p => p.Ratio is > 0.9 and < 1.1);

    /// <summary>
    /// The measured factor, or exactly 1 or a change of frame rate when that moves nothing of a film of
    /// <paramref name="length"/> seconds noticeably differently (also for sound: Úprava audiostopy).
    /// </summary>
    internal static (double Factor, (FrameRate From, FrameRate To)? Rates) Snap(double measured, double length, double? filmFps)
    {
        if (Math.Abs(measured - 1) * length <= Unnoticed)
        {
            return (1, null);
        }

        // the same ratio may come from two pairs (24 → 23,976 and 30 → 29,97): the one that ends at the film's rate
        var near = Pairs().Where(p => Math.Abs(measured - p.Ratio) * length <= Unnoticed)
            .OrderBy(p => Math.Round(Math.Abs(measured - p.Ratio), 9))
            .ThenBy(p => filmFps is { } fps ? Math.Abs(p.To.Value - fps) : 0)
            .Select(p => ((FrameRate From, FrameRate To)?)(p.From, p.To)).FirstOrDefault();
        return near is { } rates ? (rates.From.Value / rates.To.Value, rates) : (measured, null);
    }

    // the clear parts that lie on one line (Theil–Sen: the median of the slopes between parts, robust to wrong parts)
    private static List<SyncWindow> Agreeing(List<SyncWindow> clear)
    {
        var (a, b) = RobustLine(clear);
        var good = clear.Where(p => Math.Abs(p.Lag.TotalSeconds - (a + (b * p.Center.TotalSeconds))) <= Agreement).ToList();
        if (good.Count < 2)
        {
            return good.Count == 1 ? good : clear.Count > 0 ? [clear.MaxBy(p => p.Coverage + p.Margin)!] : [];
        }

        // the line through the agreeing parts, and the parts close to it
        var (offset, slope) = Line(good);
        return clear.Where(p => Math.Abs(p.Lag.TotalSeconds - (offset + (slope * p.Center.TotalSeconds))) <= Agreement).ToList() is { Count: >= 2 } close ? close : good;
    }

    private static (double Offset, double Slope) RobustLine(IReadOnlyList<SyncWindow> parts)
    {
        var slopes = new List<double>();
        for (var i = 0; i < parts.Count; i++)
        {
            for (var j = i + 1; j < parts.Count; j++)
            {
                var dx = parts[j].Center.TotalSeconds - parts[i].Center.TotalSeconds;
                if (Math.Abs(dx) >= SlopeBase)
                {
                    slopes.Add((parts[j].Lag.TotalSeconds - parts[i].Lag.TotalSeconds) / dx);
                }
            }
        }

        var slope = slopes.Count == 0 ? 0 : Median(slopes);
        if (Math.Abs(slope) > MaximumDrift)
        {
            slope = 0;
        }

        return (Median(parts.Select(p => p.Lag.TotalSeconds - (slope * p.Center.TotalSeconds))), slope);
    }

    // one part of the film: the lag of the subtitles (scaled) with the most speech under them
    private static SyncWindow? Measure(IReadOnlyList<(TimeSpan Start, TimeSpan End)> cues, ActivityWindow window, double scale)
    {
        var values = window.Values;
        var length = values.Length;
        var prefix = new int[length + 1];
        for (var i = 0; i < length; i++)
        {
            prefix[i + 1] = prefix[i] + values[i];
        }

        var range = (int)(SearchRange.TotalSeconds * ActivityWindow.Rate);
        var start = window.Start.TotalSeconds * ActivityWindow.Rate;

        // the subtitles that can fall into the part at any lag of the range, in samples of the film (scaled)
        var spans = new List<(int From, int To)>();
        foreach (var (from, to) in cues)
        {
            var a = (int)Math.Round((from.TotalSeconds * scale * ActivityWindow.Rate) - start);
            var b = (int)Math.Round((to.TotalSeconds * scale * ActivityWindow.Rate) - start);
            if (b > -range && a < length + range && b > a)
            {
                spans.Add((a, b));
            }
        }

        if (spans.Count < 3)
        {
            return null;
        }

        // score(d): speech minus silence under the subtitles moved d samples earlier (they come d late)
        var scores = new double[(2 * range) + 1];
        var covered = new int[scores.Length];
        for (var d = -range; d <= range; d++)
        {
            long sum = 0;
            var inside = 0;
            foreach (var (a, b) in spans)
            {
                var from = Math.Clamp(a - d, 0, length);
                var to = Math.Clamp(b - d, 0, length);
                sum += prefix[to] - prefix[from];
                inside += to - from;
            }

            covered[d + range] = inside;
            // only lags that keep most of the part's subtitles inside it compare fairly
            scores[d + range] = inside == 0 ? double.MinValue : (double)sum / inside;
        }

        var typical = Median(covered.Where(c => c > 0).Select(c => (double)c));
        var bestIndex = -1;
        for (var i = 0; i < scores.Length; i++)
        {
            if (covered[i] >= typical * 0.6 && (bestIndex < 0 || scores[i] > scores[bestIndex]))
            {
                bestIndex = i;
            }
        }

        if (bestIndex < 0 || scores[bestIndex] == double.MinValue)
        {
            return null;
        }

        var second = double.MinValue;
        for (var i = 0; i < scores.Length; i++)
        {
            if (Math.Abs(i - bestIndex) > ActivityWindow.Rate && covered[i] >= typical * 0.6)
            {
                second = Math.Max(second, scores[i]);
            }
        }

        // scores are in −1 … +1; coverage = the share of the subtitles' time with speech
        var coverage = (scores[bestIndex] + 1) / 2;
        var margin = second == double.MinValue ? 1 : (scores[bestIndex] - second) / 2;
        var lag = TimeSpan.FromSeconds((double)(bestIndex - range) / ActivityWindow.Rate);
        return new SyncWindow(window.Start + (window.Length / 2), lag, coverage, margin);
    }

    // least squares through the lags of the parts (close together or one: no slope; the drift stays small)
    private static (double Offset, double Slope) Line(IReadOnlyList<SyncWindow> parts)
    {
        if (parts.Count == 0)
        {
            return (0, 0);
        }

        var points = parts.Select(p => (X: p.Center.TotalSeconds, Y: p.Lag.TotalSeconds)).ToList();
        if (points.Count == 1 || points.Max(p => p.X) - points.Min(p => p.X) < SlopeBase)
        {
            return (points.Average(p => p.Y), 0);
        }

        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);
        var slope = Math.Clamp(points.Sum(p => (p.X - meanX) * (p.Y - meanY)) / points.Sum(p => (p.X - meanX) * (p.X - meanX)), -MaximumDrift, MaximumDrift);
        return (meanY - (slope * meanX), slope);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
