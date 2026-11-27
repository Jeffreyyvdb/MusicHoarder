namespace MusicHoarder.Api.CleanEdits;

/// <summary>Where a short reference preview sits inside a song, and how well it fits there.</summary>
internal readonly record struct Alignment(int OffsetFrames, double Correlation);

internal static class EditionAligner
{
    /// <summary>A preview is only believed to be in the song at this correlation or better.</summary>
    public const double MinimumCorrelation = 0.6;

    private const int EdgeFrames = 5;

    /// <summary>
    /// Slides the reference across the song and returns the best normalized cross-correlation of the
    /// z-scored band spectrograms, summed over bands. The ends of the reference are left out because
    /// a preview fades in and out. Null when the reference is longer than the song.
    /// </summary>
    public static Alignment? Locate(BandSpectrogram test, BandSpectrogram reference)
    {
        var t = test.ZScored();
        var r = reference.ZScored();
        var window = r.Frames - 2 * EdgeFrames;
        var positions = t.Frames - window + 1;
        if (window < 1 || positions < 1)
            return null;

        var size = Fft.NextPowerOfTwo(t.Frames + window);
        var correlation = new double[positions];
        var tRe = new double[size];
        var tIm = new double[size];
        var rRe = new double[size];
        var rIm = new double[size];

        for (var b = 0; b < BandSpectrogram.Bands; b++)
        {
            Array.Clear(tRe);
            Array.Clear(tIm);
            Array.Clear(rRe);
            Array.Clear(rIm);
            for (var f = 0; f < t.Frames; f++)
                tRe[f] = t[f, b];

            double mean = 0;
            for (var k = 0; k < window; k++)
                mean += r[k + EdgeFrames, b];
            mean /= window;
            for (var k = 0; k < window; k++)
                rRe[k] = r[k + EdgeFrames, b] - mean;

            Fft.Transform(tRe, tIm);
            Fft.Transform(rRe, rIm);
            for (var i = 0; i < size; i++)
            {
                var re = tRe[i] * rRe[i] + tIm[i] * rIm[i];
                var im = tIm[i] * rRe[i] - tRe[i] * rIm[i];
                tRe[i] = re;
                tIm[i] = im;
            }
            Fft.Transform(tRe, tIm, inverse: true);
            for (var j = 0; j < positions; j++)
                correlation[j] += tRe[j];
        }

        var squares = new double[t.Frames + 1];
        var sums = new double[t.Frames + 1];
        for (var f = 0; f < t.Frames; f++)
        {
            double sq = 0, sum = 0;
            for (var b = 0; b < BandSpectrogram.Bands; b++)
            {
                sq += t[f, b] * t[f, b];
                sum += t[f, b];
            }
            squares[f + 1] = squares[f] + sq;
            sums[f + 1] = sums[f] + sum;
        }

        var cells = (double)window * BandSpectrogram.Bands;
        var best = 0;
        var bestScore = double.NegativeInfinity;
        for (var j = 0; j < positions; j++)
        {
            var energy = squares[j + window] - squares[j];
            var total = sums[j + window] - sums[j];
            var variance = energy - total * total / cells;
            var score = correlation[j] / Math.Sqrt(Math.Max(variance, 1e-9) * cells);
            if (score > bestScore)
            {
                bestScore = score;
                best = j;
            }
        }
        return new Alignment(best - EdgeFrames, bestScore);
    }
}
