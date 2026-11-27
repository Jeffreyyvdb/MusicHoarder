namespace MusicHoarder.Api.CleanEdits;

internal static class Stats
{
    /// <summary>The middle value; the mean of the two middle values for an even count.</summary>
    public static double Median(ReadOnlySpan<double> values) => Percentile(values, 50);

    /// <summary>The <paramref name="percent"/>th percentile with linear interpolation between ranks.</summary>
    public static double Percentile(ReadOnlySpan<double> values, double percent)
    {
        if (values.IsEmpty)
            return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var rank = percent / 100 * (sorted.Length - 1);
        var below = (int)Math.Floor(rank);
        var above = Math.Min(below + 1, sorted.Length - 1);
        return sorted[below] + (sorted[above] - sorted[below]) * (rank - below);
    }

    /// <summary>A centred moving average of <paramref name="width"/> (odd) values; the edges see zeros beyond the signal.</summary>
    public static double[] MovingAverage(double[] values, int width)
    {
        var half = width / 2;
        var smoothed = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            double sum = 0;
            for (var j = Math.Max(0, i - half); j <= Math.Min(values.Length - 1, i + half); j++)
                sum += values[j];
            smoothed[i] = sum / width;
        }
        return smoothed;
    }
}
