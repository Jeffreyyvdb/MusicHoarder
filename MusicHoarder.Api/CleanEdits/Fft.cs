using System.Collections.Concurrent;

namespace MusicHoarder.Api.CleanEdits;

/// <summary>An in-place radix-2 complex FFT. There is no FFT package in the solution, and this is the only transform the comparison needs.</summary>
internal static class Fft
{
    private static readonly ConcurrentDictionary<int, (double[] Cos, double[] Sin)> Twiddles = new();

    public static int NextPowerOfTwo(int n)
    {
        var size = 1;
        while (size < n)
            size <<= 1;
        return size;
    }

    /// <summary>Transforms <paramref name="re"/> and <paramref name="im"/> in place; the inverse is scaled by 1/n. Their length must be a power of two.</summary>
    public static void Transform(double[] re, double[] im, bool inverse = false)
    {
        var n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT length must be a power of two and equal for both parts.");
        if (n == 1)
            return;

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        var (cos, sin) = Twiddles.GetOrAdd(n, static size =>
        {
            var c = new double[size / 2];
            var s = new double[size / 2];
            for (var k = 0; k < c.Length; k++)
            {
                c[k] = Math.Cos(2 * Math.PI * k / size);
                s[k] = -Math.Sin(2 * Math.PI * k / size);
            }
            return (c, s);
        });
        var sign = inverse ? -1.0 : 1.0;

        for (var length = 2; length <= n; length <<= 1)
        {
            var half = length >> 1;
            var step = n / length;
            for (var start = 0; start < n; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = cos[k * step];
                    var wi = sign * sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = re[b] * wr - im[b] * wi;
                    var ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
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
}
