using MusicHoarder.Api.CleanEdits;

namespace MusicHoarder.Api.Tests.CleanEdits;

/// <summary>
/// Songs made of numbers: a bed of changing chords with a noise burst per "word", exactly one chord long so a
/// chord change never lands inside a word. An edition is the
/// same bed with each word kept, softened (a label's clean edit) or removed (a mute).
/// </summary>
internal static class SyntheticSong
{
    public const int Rate = BandSpectrogram.SampleRate;
    public const double WordSeconds = 0.25;
    public const double ExplicitWordLevel = 0.12;
    public const double CleanWordLevel = 0.06;

    /// <summary>Chords of three tones that change every quarter second, so every stretch of the song looks different.</summary>
    public static float[] Bed(double seconds, int seed = 7)
    {
        var random = new Random(seed);
        var pcm = new float[(int)(seconds * Rate)];
        var segment = Rate / 4;
        for (var start = 0; start < pcm.Length; start += segment)
        {
            var tones = Enumerable.Range(0, 3).Select(_ => 250 + random.NextDouble() * 3750).ToArray();
            for (var i = start; i < Math.Min(start + segment, pcm.Length); i++)
                pcm[i] = (float)tones.Sum(f => 0.05 * Math.Sin(2 * Math.PI * f * i / Rate));
        }
        return pcm;
    }

    /// <summary>The bed with a burst of noise at each word start, at <paramref name="level"/>; zero leaves the words out.</summary>
    public static float[] WithWords(float[] bed, IEnumerable<double> wordStarts, double level)
    {
        var pcm = (float[])bed.Clone();
        var index = 0;
        foreach (var start in wordStarts)
        {
            var noise = new Random(1000 + index++);
            var first = (int)(start * Rate);
            for (var i = first; i < Math.Min(first + (int)(WordSeconds * Rate), pcm.Length); i++)
                pcm[i] += (float)(level * (noise.NextDouble() * 2 - 1));
        }
        return pcm;
    }

    public static float[] Excerpt(float[] pcm, double from, double seconds) =>
        pcm[(int)(from * Rate)..(int)((from + seconds) * Rate)];

    public static LocatedReference Reference(BandSpectrogram test, Edition edition, string id, float[] pcm, double from, double seconds = 30)
    {
        var preview = BandSpectrogram.FromPcm(Excerpt(pcm, from, seconds));
        var alignment = EditionAligner.Locate(test, preview)
            ?? throw new InvalidOperationException("preview longer than the song");
        return new LocatedReference(edition, id, preview, alignment.OffsetFrames, alignment.Correlation);
    }
}
