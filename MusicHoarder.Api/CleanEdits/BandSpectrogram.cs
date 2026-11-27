namespace MusicHoarder.Api.CleanEdits;

/// <summary>
/// A song as a log band spectrogram: mono 22.05 kHz audio cut into 1024-sample Hann frames every 220
/// samples, each frame reduced to 24 log-spaced bands between 200 and 5000 Hz, in dB. Two editions of
/// one song differ in the vocal bands where a word is edited, which is all the check looks at.
/// </summary>
internal sealed class BandSpectrogram
{
    public const int SampleRate = 22050;
    public const int FrameSize = 1024;
    public const int Hop = 220;
    public const int Bands = 24;
    public const double FrameSeconds = (double)Hop / SampleRate;

    private const double LowHz = 200;
    private const double HighHz = 5000;

    private static readonly int[] BandOfBin = BuildBandOfBin();
    private static readonly double[] Window = BuildWindow();

    private readonly double[] _db;

    private BandSpectrogram(double[] db, int frames)
    {
        _db = db;
        Frames = frames;
    }

    public int Frames { get; }

    public double this[int frame, int band] => _db[frame * Bands + band];

    public static BandSpectrogram FromPcm(ReadOnlySpan<float> pcm)
    {
        var frames = Math.Max(0, (pcm.Length - FrameSize) / Hop);
        var db = new double[frames * Bands];
        var re = new double[FrameSize];
        var im = new double[FrameSize];
        var power = new double[Bands];
        for (var f = 0; f < frames; f++)
        {
            var start = f * Hop;
            for (var i = 0; i < FrameSize; i++)
                re[i] = pcm[start + i] * Window[i];
            Array.Clear(im);
            Fft.Transform(re, im);

            Array.Clear(power);
            for (var k = 0; k <= FrameSize / 2; k++)
            {
                var band = BandOfBin[k];
                if (band >= 0)
                    power[band] += re[k] * re[k] + im[k] * im[k];
            }
            for (var b = 0; b < Bands; b++)
                db[f * Bands + b] = 10 * Math.Log10(power[b] + 1e-9);
        }
        return new BandSpectrogram(db, frames);
    }

    /// <summary>Frames <paramref name="from"/> up to but not including <paramref name="to"/>.</summary>
    public BandSpectrogram Slice(int from, int to) =>
        new(_db[(from * Bands)..(to * Bands)], to - from);

    /// <summary>This spectrogram lifted, band by band, by the median level gap to <paramref name="target"/>, so a volume or master difference is not mistaken for an edit.</summary>
    public BandSpectrogram MatchLevelTo(BandSpectrogram target)
    {
        var shifted = new double[_db.Length];
        var gap = new double[Frames];
        for (var b = 0; b < Bands; b++)
        {
            for (var f = 0; f < Frames; f++)
                gap[f] = target[f, b] - this[f, b];
            var shift = Stats.Median(gap);
            for (var f = 0; f < Frames; f++)
                shifted[f * Bands + b] = this[f, b] + shift;
        }
        return new BandSpectrogram(shifted, Frames);
    }

    /// <summary>Each band centred and scaled to unit variance over the frames.</summary>
    public BandSpectrogram ZScored()
    {
        var scored = new double[_db.Length];
        for (var b = 0; b < Bands; b++)
        {
            double sum = 0;
            for (var f = 0; f < Frames; f++)
                sum += this[f, b];
            var mean = sum / Frames;
            double squares = 0;
            for (var f = 0; f < Frames; f++)
            {
                var d = this[f, b] - mean;
                squares += d * d;
            }
            var std = Math.Sqrt(squares / Frames);
            for (var f = 0; f < Frames; f++)
                scored[f * Bands + b] = (this[f, b] - mean) / (std + 1e-9);
        }
        return new BandSpectrogram(scored, Frames);
    }

    /// <summary>Per frame, the mean over all bands of the absolute dB gap between two spectrograms of equal length.</summary>
    public static double[] MeanAbsoluteGap(BandSpectrogram a, BandSpectrogram b)
    {
        var gap = new double[a.Frames];
        for (var f = 0; f < a.Frames; f++)
        {
            double sum = 0;
            for (var band = 0; band < Bands; band++)
                sum += Math.Abs(a[f, band] - b[f, band]);
            gap[f] = sum / Bands;
        }
        return gap;
    }

    /// <summary>Per frame, the mean dB of bands <paramref name="firstBand"/> through <paramref name="lastBand"/>.</summary>
    public double[] BandMean(int firstBand, int lastBand)
    {
        var mean = new double[Frames];
        for (var f = 0; f < Frames; f++)
        {
            double sum = 0;
            for (var b = firstBand; b <= lastBand; b++)
                sum += this[f, b];
            mean[f] = sum / (lastBand - firstBand + 1);
        }
        return mean;
    }

    private static int[] BuildBandOfBin()
    {
        var edges = new double[Bands + 1];
        for (var i = 0; i <= Bands; i++)
            edges[i] = LowHz * Math.Pow(HighHz / LowHz, (double)i / Bands);
        edges[Bands] = HighHz;

        var map = new int[FrameSize / 2 + 1];
        for (var k = 0; k < map.Length; k++)
        {
            var hz = (double)k * SampleRate / FrameSize;
            map[k] = -1;
            for (var b = 0; b < Bands; b++)
            {
                if (hz >= edges[b] && hz < edges[b + 1])
                {
                    map[k] = b;
                    break;
                }
            }
        }
        return map;
    }

    private static double[] BuildWindow()
    {
        var window = new double[FrameSize];
        for (var n = 0; n < FrameSize; n++)
            window[n] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (FrameSize - 1));
        return window;
    }
}
