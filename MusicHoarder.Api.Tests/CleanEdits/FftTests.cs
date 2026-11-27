using MusicHoarder.Api.CleanEdits;

namespace MusicHoarder.Api.Tests.CleanEdits;

public class FftTests
{
    [Fact]
    public void A_pure_tone_peaks_in_its_own_bin()
    {
        const int n = 256;
        var re = Enumerable.Range(0, n).Select(i => Math.Cos(2 * Math.PI * 20 * i / n)).ToArray();
        var im = new double[n];

        Fft.Transform(re, im);

        var peak = Enumerable.Range(0, n / 2).MaxBy(k => re[k] * re[k] + im[k] * im[k]);
        Assert.Equal(20, peak);
    }

    [Fact]
    public void The_inverse_gives_back_what_went_in()
    {
        var random = new Random(3);
        var original = Enumerable.Range(0, 512).Select(_ => random.NextDouble() - 0.5).ToArray();
        var re = (double[])original.Clone();
        var im = new double[original.Length];

        Fft.Transform(re, im);
        Fft.Transform(re, im, inverse: true);

        Assert.All(original.Zip(re), pair => Assert.Equal(pair.First, pair.Second, 9));
        Assert.All(im, v => Assert.Equal(0, v, 9));
    }

    [Fact]
    public void A_length_that_is_not_a_power_of_two_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Fft.Transform(new double[12], new double[12]));
    }
}
