namespace Universum.Core.Audio;

/// <summary>
/// The fast Fourier transform (radix 2, in place) and what UNIVERSUM does with it: the cross-correlation of a short part
/// of one sound with the whole of another – every lag at once.
/// </summary>
internal static class Fourier
{
    /// <summary>The smallest power of two of at least <paramref name="length"/>.</summary>
    public static int Size(int length)
    {
        var size = 1;
        while (size < length)
        {
            size <<= 1;
        }

        return size;
    }

    /// <summary>Transforms <paramref name="re"/>, <paramref name="im"/> (length a power of two); <paramref name="inverse"/> divides by the length.</summary>
    public static void Transform(double[] re, double[] im, bool inverse)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = (inverse ? 2 : -2) * Math.PI / length;
            var (stepRe, stepIm) = (Math.Cos(angle), Math.Sin(angle));
            var half = length >> 1;
            for (var start = 0; start < n; start += length)
            {
                var (wRe, wIm) = (1.0, 0.0);
                for (var k = 0; k < half; k++)
                {
                    var (a, b) = (start + k, start + k + half);
                    var tRe = (re[b] * wRe) - (im[b] * wIm);
                    var tIm = (re[b] * wIm) + (im[b] * wRe);
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    (wRe, wIm) = ((wRe * stepRe) - (wIm * stepIm), (wRe * stepIm) + (wIm * stepRe));
                }
            }
        }

        if (inverse)
        {
            for (var i = 0; i < n; i++)
            {
                re[i] /= n;
                im[i] /= n;
            }
        }
    }

    /// <summary>The spectrum of <paramref name="values"/> zero-padded to <paramref name="size"/>.</summary>
    public static (double[] Re, double[] Im) Spectrum(ReadOnlySpan<float> values, int size)
    {
        var re = new double[size];
        var im = new double[size];
        for (var i = 0; i < values.Length && i < size; i++)
        {
            re[i] = values[i];
        }

        Transform(re, im, inverse: false);
        return (re, im);
    }

    /// <summary>
    /// For every lag q: Σ part[i] · whole[q + i] – from the spectra of both (the same size, at least the sum of the two
    /// lengths, so no lag wraps round). The first <paramref name="lags"/> lags are given.
    /// </summary>
    public static double[] Correlation((double[] Re, double[] Im) part, (double[] Re, double[] Im) whole, int lags)
    {
        var size = part.Re.Length;
        var re = new double[size];
        var im = new double[size];
        for (var i = 0; i < size; i++)
        {
            // conj(part) · whole
            re[i] = (part.Re[i] * whole.Re[i]) + (part.Im[i] * whole.Im[i]);
            im[i] = (part.Re[i] * whole.Im[i]) - (part.Im[i] * whole.Re[i]);
        }

        Transform(re, im, inverse: true);
        return re.AsSpan(0, Math.Clamp(lags, 0, size)).ToArray();
    }
}
