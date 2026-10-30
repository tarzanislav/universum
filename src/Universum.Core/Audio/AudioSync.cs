using System.Globalization;
using Universum.Core.Subtitles;
using Universum.Core.Subtitles.Sync;

namespace Universum.Core.Audio;

/// <summary>One part of the film and where the same sound is in the other file.</summary>
/// <param name="Film">The middle of the part in the film.</param>
/// <param name="Other">Where it is in the other file.</param>
/// <param name="Score">How alike the two are there (1 = the same).</param>
/// <param name="Margin">How much better than anything farther away.</param>
public sealed record AudioSyncPart(TimeSpan Film, TimeSpan Other, double Score, double Margin)
{
    public bool IsClear => Score >= AudioSync.MinimumScore && Margin >= AudioSync.MinimumMargin;
}

/// <summary>
/// How another sound fits the film: <see cref="Timing"/> moves (and stretches) its times as mkvmerge and FFmpeg will;
/// <see cref="Rates"/> when the stretch is a change of frame rate (25 → 23,976: a PAL release).
/// </summary>
public sealed record AudioSyncResult(SubtitleTiming Timing, (FrameRate From, FrameRate To)? Rates, bool Reliable, string Detail, IReadOnlyList<AudioSyncPart> Parts);

/// <summary>
/// Fits a sound from elsewhere (a dub, another release) to the film (owner, 30. 10. 2026: automatické napasovanie,
/// UNIVERSUM's own way): the envelopes of both are compared – first a few minute-long parts of the film against the whole
/// other sound at every change of frame rate (every lag at once, by the Fourier transform), then many short parts
/// precisely around the place found. A robust line through the parts gives the shift and the speed; the result is
/// reliable when most parts lie on it.
/// </summary>
public static class AudioSync
{
    /// <summary>A part counts when the sounds are this much alike…</summary>
    public const double MinimumScore = 0.15;

    /// <summary>…and this much more than at any other place half a second or more away.</summary>
    public const double MinimumMargin = 0.04;

    // the search over the whole other sound: 10 values a second, minute-long parts
    private const int CoarseFactor = 10;
    private const double CoarseSeconds = 60;
    private const int CoarseParts = 6;

    // the precise search: 20-second parts, ±2 s around the place found
    private const double FineSeconds = 20;
    private const int FineParts = 16;
    private const double FineRange = 2;
    private const double FineApart = 0.5;

    // parts lie on the line when they differ from it by at most 40 ms (lips and voice)
    private const double Agreement = 0.04;

    // two parts this far apart give a slope (seconds)
    private const double SlopeBase = 60;

    /// <summary>Where <paramref name="other"/> belongs in <paramref name="film"/>; null when either is too short or silent.</summary>
    public static AudioSyncResult? Align(AudioEnvelope film, AudioEnvelope other, double? filmFps, CancellationToken cancellationToken = default)
    {
        var coarse = Coarse(film, other, filmFps, cancellationToken);
        if (coarse is null)
        {
            return null;
        }

        var (scale, offset, coarseParts) = coarse.Value;
        var parts = Fine(film, other, scale, offset, cancellationToken);
        var clear = parts.Where(p => p.IsClear).ToList();
        var length = film.Length.TotalSeconds;
        if (clear.Count < 2)
        {
            // only the search over the whole sound: a proposal
            var rates = SubtitleSync.Snap(scale, length, filmFps).Rates;
            return Result(scale, rates, offset, false, $"zvuk sa našiel len hrubo ({coarseParts.Count(p => p.IsClear)} z {coarseParts.Count} častí)", coarseParts);
        }

        var measured = Slope(clear) ?? scale;
        var (factor, snapped) = SubtitleSync.Snap(measured, length, filmFps);
        var (shift, agreeing) = Fit(clear, factor);
        var (ownShift, ownAgreeing) = Fit(clear, measured);
        if (ownAgreeing.Count > agreeing.Count)
        {
            // the measured speed fits better than any frame rate
            (factor, snapped, shift, agreeing) = (measured, null, ownShift, ownAgreeing);
        }

        var reliable = agreeing.Count >= 4 && agreeing.Count * 3 >= clear.Count * 2;
        return Result(factor, snapped, shift, reliable, $"zvuk sedí v {agreeing.Count} z {parts.Count} častí filmu", parts);
    }

    private static AudioSyncResult Result(double factor, (FrameRate From, FrameRate To)? rates, double shift, bool reliable, string found, IReadOnlyList<AudioSyncPart> parts)
    {
        var delay = (long)Math.Round(shift * 1000);
        var timing = rates is { } pair ? SubtitleTiming.Fps(pair.From, pair.To) with { DelayMilliseconds = delay }
            : Math.Abs(factor - 1) < 1e-9 ? SubtitleTiming.Delay(delay)
            : new SubtitleTiming(delay, (long)Math.Round(factor * 1_000_000), 1_000_000);
        var sk = CultureInfo.GetCultureInfo("sk-SK");
        var speed = rates is { } r ? $", FPS {r.From} → {r.To}" : timing.Numerator != timing.Denominator ? string.Create(sk, $", rýchlosť × {timing.Factor:0.######}") : "";
        return new AudioSyncResult(timing, rates, reliable, string.Create(sk, $"{found}{speed}, posun {delay:+#,0;−#,0;0} ms"), parts);
    }

    // the search over the whole other sound at every change of frame rate: the scale and shift most parts agree on
    private static (double Scale, double Offset, List<AudioSyncPart> Parts)? Coarse(AudioEnvelope film, AudioEnvelope other, double? filmFps, CancellationToken cancellationToken)
    {
        const double rate = (double)AudioEnvelope.Rate / CoarseFactor;
        var a = film.Coarse(CoarseFactor);
        var b = other.Coarse(CoarseFactor);
        var window = Math.Min((int)(CoarseSeconds * rate), a.Length / 2);
        if (window < 10 * rate || b.Length < window)
        {
            return null;
        }

        // the parts of the film (zero mean, unit length): their spectra serve every scale
        var largest = SubtitleSync.Scales.Max();
        var size = Fourier.Size((int)(b.Length * largest) + 2 + window);
        var starts = Spread(a.Length, window, CoarseParts, 0.05);
        var spectra = new List<(int Start, (double[] Re, double[] Im) Spectrum)>();
        foreach (var start in starts)
        {
            if (Normalized(a.AsSpan(start, window)) is { } part)
            {
                spectra.Add((start, Fourier.Spectrum(part, size)));
            }
        }

        if (spectra.Count == 0)
        {
            return null;
        }

        (double Scale, int Count, double Sum, double Offset, List<AudioSyncPart> Parts)? best = null;
        foreach (var scale in SubtitleSync.Scales)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stretched = Stretch(b, scale);
            if (stretched.Length < window)
            {
                continue;
            }

            var whole = Fourier.Spectrum(stretched, size);
            var (sums, squares) = Prefix(stretched);
            var parts = new List<(AudioSyncPart Part, double Offset)>();
            foreach (var (start, spectrum) in spectra)
            {
                var lags = stretched.Length - window + 1;
                var correlation = Fourier.Correlation(spectrum, whole, lags);
                var (lag, score, margin) = Peak(correlation, sums, squares, window, (int)rate);
                var middle = (start + (window / 2.0)) / rate;
                parts.Add((new AudioSyncPart(TimeSpan.FromSeconds(middle), TimeSpan.FromSeconds((lag + (window / 2.0)) / rate / scale), score, margin),
                    (start - lag) / rate));
            }

            // the shift the most parts share (within half a second), their likeness summed
            foreach (var (part, offset) in parts.Where(p => p.Part.Score > 0))
            {
                var near = parts.Where(p => Math.Abs(p.Offset - offset) <= 0.5).ToList();
                var (count, sum) = (near.Count, near.Sum(p => p.Part.Score));
                if (best is null || count > best.Value.Count || (count == best.Value.Count && sum > best.Value.Sum))
                {
                    best = (scale, count, sum, near.Select(p => p.Offset).Order().ElementAt(near.Count / 2), parts.Select(p => p.Part).ToList());
                }
            }
        }

        return best is { } found ? (found.Scale, found.Offset, found.Parts) : null;
    }

    // short parts of the film found precisely around where the coarse search put the other sound
    private static List<AudioSyncPart> Fine(AudioEnvelope film, AudioEnvelope other, double scale, double offset, CancellationToken cancellationToken)
    {
        const int rate = AudioEnvelope.Rate;
        var a = film.Values;
        var window = Math.Min((int)(FineSeconds * rate), a.Length / 2);
        var range = (int)(FineRange * rate);
        var parts = new List<AudioSyncPart>();
        if (window < 5 * rate)
        {
            return parts;
        }

        foreach (var start in Spread(a.Length, window, FineParts, 0.03))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Normalized(a.AsSpan(start, window)) is not { } part)
            {
                continue;
            }

            // the other sound on the film's time as found, ±range around the part
            var from = start - range;
            var near = new float[window + (2 * range)];
            var inside = 0;
            for (var i = 0; i < near.Length; i++)
            {
                var at = (((from + i) / (double)rate) - offset) / scale * rate;
                if (at >= 0 && at < other.Values.Length - 1)
                {
                    var whole = (int)at;
                    near[i] = (float)(other.Values[whole] + ((other.Values[whole + 1] - other.Values[whole]) * (at - whole)));
                    inside++;
                }
            }

            if (inside < near.Length * 0.9)
            {
                continue;
            }

            var correlation = new double[(2 * range) + 1];
            for (var lag = 0; lag < correlation.Length; lag++)
            {
                var sum = 0.0;
                for (var i = 0; i < window; i++)
                {
                    sum += part[i] * near[lag + i];
                }

                correlation[lag] = sum;
            }

            var (sums, squares) = Prefix(near);
            var (best, score, margin) = Peak(correlation, sums, squares, window, (int)(FineApart * rate));
            var refined = best + Vertex(correlation, sums, squares, window, best);
            var middle = (start + (window / 2.0)) / rate;
            var found = (from + refined + (window / 2.0)) / rate;
            parts.Add(new AudioSyncPart(TimeSpan.FromSeconds(middle), TimeSpan.FromSeconds((found - offset) / scale), score, margin));
        }

        return parts;
    }

    // the robust slope of film time over other time (Theil–Sen: the median of the slopes between parts far apart)
    private static double? Slope(IReadOnlyList<AudioSyncPart> parts)
    {
        var slopes = new List<double>();
        for (var i = 0; i < parts.Count; i++)
        {
            for (var j = i + 1; j < parts.Count; j++)
            {
                var du = parts[j].Other.TotalSeconds - parts[i].Other.TotalSeconds;
                if (Math.Abs(du) >= SlopeBase)
                {
                    slopes.Add((parts[j].Film.TotalSeconds - parts[i].Film.TotalSeconds) / du);
                }
            }
        }

        return slopes.Count == 0 ? null : Median(slopes);
    }

    // with the factor: the shift (the median) and the parts on the line
    private static (double Shift, List<AudioSyncPart> Agreeing) Fit(IReadOnlyList<AudioSyncPart> parts, double factor)
    {
        var shift = Median(parts.Select(p => p.Film.TotalSeconds - (factor * p.Other.TotalSeconds)).ToList());
        return (shift, parts.Where(p => Math.Abs(p.Film.TotalSeconds - (factor * p.Other.TotalSeconds) - shift) <= Agreement).ToList());
    }

    // the best lag by the normalized correlation, its score and how much better it is than any lag farther than apart
    private static (int Lag, double Score, double Margin) Peak(double[] correlation, double[] sums, double[] squares, int window, int apart)
    {
        var scores = new double[correlation.Length];
        var best = 0;
        for (var lag = 0; lag < correlation.Length; lag++)
        {
            scores[lag] = Normalize(correlation[lag], sums, squares, window, lag);
            if (scores[lag] > scores[best])
            {
                best = lag;
            }
        }

        var second = double.NegativeInfinity;
        for (var lag = 0; lag < scores.Length; lag++)
        {
            if (Math.Abs(lag - best) > apart && scores[lag] > second)
            {
                second = scores[lag];
            }
        }

        return (best, scores[best], double.IsNegativeInfinity(second) ? scores[best] : scores[best] - second);
    }

    // the top of a parabola through the best lag and its neighbours (a fraction of a value)
    private static double Vertex(double[] correlation, double[] sums, double[] squares, int window, int best)
    {
        if (best <= 0 || best >= correlation.Length - 1)
        {
            return 0;
        }

        var (left, middle, right) = (Normalize(correlation[best - 1], sums, squares, window, best - 1), Normalize(correlation[best], sums, squares, window, best),
            Normalize(correlation[best + 1], sums, squares, window, best + 1));
        var curve = left - (2 * middle) + right;
        return curve < 0 ? Math.Clamp((left - right) / (2 * curve), -0.5, 0.5) : 0;
    }

    // the correlation of a part of unit length and zero mean with the other's values under it, as their cosine
    private static double Normalize(double correlation, double[] sums, double[] squares, int window, int lag)
    {
        var sum = sums[lag + window] - sums[lag];
        var energy = squares[lag + window] - squares[lag] - (sum * sum / window);
        return energy > 1e-9 ? correlation / Math.Sqrt(energy) : 0;
    }

    private static (double[] Sums, double[] Squares) Prefix(float[] values)
    {
        var sums = new double[values.Length + 1];
        var squares = new double[values.Length + 1];
        for (var i = 0; i < values.Length; i++)
        {
            sums[i + 1] = sums[i] + values[i];
            squares[i + 1] = squares[i] + ((double)values[i] * values[i]);
        }

        return (sums, squares);
    }

    // zero mean, unit length; null for silence
    private static float[]? Normalized(ReadOnlySpan<float> values)
    {
        var mean = 0.0;
        foreach (var value in values)
        {
            mean += value;
        }

        mean /= values.Length;
        var energy = 0.0;
        foreach (var value in values)
        {
            energy += (value - mean) * (value - mean);
        }

        if (energy < 1e-6)
        {
            return null;
        }

        var norm = Math.Sqrt(energy);
        var result = new float[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = (float)((values[i] - mean) / norm);
        }

        return result;
    }

    // the other sound on a time stretched by the scale (its value at t / scale)
    private static float[] Stretch(float[] values, double scale)
    {
        var result = new float[(int)((values.Length - 1) * scale) + 1];
        for (var i = 0; i < result.Length; i++)
        {
            var at = i / scale;
            var whole = (int)at;
            result[i] = whole + 1 < values.Length ? (float)(values[whole] + ((values[whole + 1] - values[whole]) * (at - whole))) : values[^1];
        }

        return result;
    }

    // count windows evenly between the edges (a share of the length left out at both ends)
    private static IEnumerable<int> Spread(int length, int window, int count, double edge)
    {
        var first = (int)(length * edge);
        var last = Math.Max(first, length - (int)(length * edge) - window);
        return Enumerable.Range(0, count).Select(i => count == 1 ? first : first + (int)((long)(last - first) * i / (count - 1))).Distinct();
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values.Count % 2 == 1 ? values[values.Count / 2] : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2;
    }
}
