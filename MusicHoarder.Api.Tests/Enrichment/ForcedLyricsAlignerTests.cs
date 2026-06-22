using MusicHoarder.Api.Enrichment;

namespace MusicHoarder.Api.Tests.Enrichment;

public class ForcedLyricsAlignerTests
{
    private static TimedWord W(string word, double start) => new(word, start, start + 0.3);

    [Fact]
    public void Align_RepeatedHook_MapsEachRepetitionToItsOwnOccurrence()
    {
        // "The Sweet Escape" failure mode: the same line repeats, and an LLM index-mapping collapses them
        // onto one timestamp. The monotonic forced aligner must map each repetition to its own occurrence.
        var reference = new[] { "Woo hoo, yee hoo", "Woo hoo, yee hoo", "If I could escape", "Woo hoo, yee hoo" };
        var words = new List<TimedWord>
        {
            W("Woo", 1.0), W("hoo", 1.4), W("yee", 1.8), W("hoo", 2.2),
            W("Woo", 4.0), W("hoo", 4.4), W("yee", 4.8), W("hoo", 5.2),
            W("If", 8.0), W("I", 8.3), W("could", 8.6), W("escape", 9.0),
            W("Woo", 14.0), W("hoo", 14.4), W("yee", 14.8), W("hoo", 15.2),
        };

        var result = ForcedLyricsAligner.Align(reference, words);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Count);
        // Each line anchored to its own occurrence — not collapsed.
        Assert.Equal(1.0, result[0].Start, precision: 1);
        Assert.Equal(4.0, result[1].Start, precision: 1);
        Assert.Equal(8.0, result[2].Start, precision: 1);
        Assert.Equal(14.0, result[3].Start, precision: 1);
        // And strictly increasing.
        for (var i = 1; i < result.Count; i++)
            Assert.True(result[i].Start > result[i - 1].Start);
    }

    [Fact]
    public void Align_KeepsReferenceTextAndPunctuationVerbatim()
    {
        var reference = new[] { "Ooh momma, this could be you" };
        var words = new List<TimedWord> { W("ooh", 3.0), W("momma", 3.4), W("this", 3.8), W("could", 4.0), W("be", 4.2), W("you", 4.4) };

        var result = ForcedLyricsAligner.Align(reference, words);

        Assert.NotNull(result);
        Assert.Equal("Ooh momma, this could be you", result![0].Text);
        Assert.Equal(3.0, result[0].Start, precision: 1);
    }

    [Fact]
    public void Align_ReferenceUnrelatedToAudio_ReturnsNullSoCallerFallsBack()
    {
        var reference = new[] { "completely different lyrics that are not in this song at all" };
        var words = new List<TimedWord> { W("alpha", 0.0), W("beta", 1.0), W("gamma", 2.0), W("delta", 3.0) };

        Assert.Null(ForcedLyricsAligner.Align(reference, words));
    }

    [Fact]
    public void Align_EmptyInputs_ReturnsNull()
    {
        Assert.Null(ForcedLyricsAligner.Align(Array.Empty<string>(), new List<TimedWord> { W("hi", 0) }));
        Assert.Null(ForcedLyricsAligner.Align(new[] { "hello" }, new List<TimedWord>()));
    }
}
