using MusicHoarder.Api.Enrichment;

namespace MusicHoarder.Api.Tests.Enrichment;

public class LrcBuilderTests
{
    private static TimedWord W(string word, double start, double end) => new(word, start, end);

    // --- SplitReferenceLines ---

    [Fact]
    public void SplitReferenceLines_StripsLrcTimestampsAndBlankLines()
    {
        var text = "[00:12.34] First line\r\n\r\n  Second line  \n[01:02.5]Third line";

        var result = LrcBuilder.SplitReferenceLines(text);

        Assert.Equal(new[] { "First line", "Second line", "Third line" }, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void SplitReferenceLines_ReturnsNull_WhenNothingUsable(string? text)
    {
        Assert.Null(LrcBuilder.SplitReferenceLines(text));
    }

    // --- BuildLinesFromWords ---

    [Fact]
    public void BuildLinesFromWords_BreaksOnPause_AndStampsFirstWordStart()
    {
        var words = new List<TimedWord>
        {
            W("hello", 1.0, 1.4),
            W("world", 1.4, 1.8),
            // 2.0s silent gap -> new line
            W("second", 3.8, 4.1),
            W("line", 4.1, 4.4),
        };

        var lines = LrcBuilder.BuildLinesFromWords(words, pauseThresholdSeconds: 1.0, maxWordsPerLine: 20);

        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Equal((1.0, "hello world"), lines[0]);
        Assert.Equal((3.8, "second line"), lines[1]);
    }

    [Fact]
    public void BuildLinesFromWords_BreaksOnMaxWords()
    {
        var words = Enumerable.Range(0, 5)
            .Select(i => W($"w{i}", i * 0.5, i * 0.5 + 0.4))
            .ToList();

        var lines = LrcBuilder.BuildLinesFromWords(words, pauseThresholdSeconds: 0, maxWordsPerLine: 2);

        Assert.NotNull(lines);
        Assert.Equal(3, lines!.Count);
        Assert.Equal("w0 w1", lines[0].Text);
        Assert.Equal("w2 w3", lines[1].Text);
        Assert.Equal("w4", lines[2].Text);
    }

    [Fact]
    public void BuildLinesFromWords_BlankWordCarriesEndTime_SoNoSpuriousBreak()
    {
        // A blank word emits no token but its END time still advances prevEnd. Here the blank ends at 2.0
        // and 'b' starts at 2.1, so the gap is only 0.1s and the words stay on one line. Were the blank
        // dropped instead of consumed, the gap would be measured from 'a' (1.7s) and split spuriously —
        // this is exactly why the caller feeds blank-preserving words to the splitter.
        var words = new List<TimedWord>
        {
            W("a", 0.0, 0.4),
            W("   ", 0.4, 2.0),   // blank: carries prevEnd forward to 2.0
            W("b", 2.1, 2.4),     // gap since blank end (2.0) is 0.1 -> no break
        };

        var lines = LrcBuilder.BuildLinesFromWords(words, pauseThresholdSeconds: 1.0, maxWordsPerLine: 20);

        Assert.NotNull(lines);
        Assert.Single(lines!);
        Assert.Equal("a b", lines![0].Text);
    }

    [Fact]
    public void BuildLinesFromWords_ReturnsNull_WhenNoWords()
    {
        Assert.Null(LrcBuilder.BuildLinesFromWords(null, 1.0, 10));
        Assert.Null(LrcBuilder.BuildLinesFromWords(new List<TimedWord>(), 1.0, 10));
    }

    // --- BuildLinesFromSegments ---

    [Fact]
    public void BuildLinesFromSegments_KeepsNonEmptyTrimmedSegments()
    {
        var segments = new List<TranscriptSegment>
        {
            new(0.0, "  first  "),
            new(3.0, "   "),
            new(5.0, "second"),
        };

        var lines = LrcBuilder.BuildLinesFromSegments(segments);

        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Equal((0.0, "first"), lines[0]);
        Assert.Equal((5.0, "second"), lines[1]);
    }

    // --- IsDegenerate ---

    [Fact]
    public void IsDegenerate_TrueForNullOrEmpty()
    {
        Assert.True(LrcBuilder.IsDegenerate(null));
        Assert.True(LrcBuilder.IsDegenerate(new List<(double, string)>()));
    }

    [Fact]
    public void IsDegenerate_FalseForFewerThanFourLines_EvenIfCollapsed()
    {
        var lines = new List<(double Start, string Text)> { (1.0, "a"), (1.0, "b"), (1.0, "c") };
        Assert.False(LrcBuilder.IsDegenerate(lines));
    }

    [Fact]
    public void IsDegenerate_TrueWhenOverFortyPercentShareOneTimestamp()
    {
        // 3 of 5 lines (60%) collapse onto 2.0 -> degenerate.
        var lines = new List<(double Start, string Text)>
        {
            (1.0, "a"), (2.0, "b"), (2.0, "c"), (2.0, "d"), (5.0, "e"),
        };
        Assert.True(LrcBuilder.IsDegenerate(lines));
    }

    [Fact]
    public void IsDegenerate_FalseWhenTimestampsAreSpread()
    {
        var lines = new List<(double Start, string Text)>
        {
            (1.0, "a"), (2.0, "b"), (3.0, "c"), (4.0, "d"), (5.0, "e"),
        };
        Assert.False(LrcBuilder.IsDegenerate(lines));
    }

    // --- SpreadRepeatedConsecutiveLines ---

    [Fact]
    public void SpreadRepeatedConsecutiveLines_SpreadsCollapsedRunEvenlyToNextDistinctLine()
    {
        var lines = new List<(double Start, string Text)>
        {
            (10.0, "hook"),
            (10.0, "hook"),
            (10.0, "hook"),
            (16.0, "verse"),
        };

        LrcBuilder.SpreadRepeatedConsecutiveLines(lines);

        // Run of 3 spreads across 10.0 -> 16.0 in steps of 2.0.
        Assert.Equal(10.0, lines[0].Start, precision: 3);
        Assert.Equal(12.0, lines[1].Start, precision: 3);
        Assert.Equal(14.0, lines[2].Start, precision: 3);
        Assert.Equal(16.0, lines[3].Start, precision: 3);
    }

    [Fact]
    public void SpreadRepeatedConsecutiveLines_LeavesDistinctTimestampsUntouched()
    {
        var lines = new List<(double Start, string Text)>
        {
            (10.0, "hook"),
            (12.0, "hook"),
            (14.0, "verse"),
        };
        var snapshot = lines.ToList();

        LrcBuilder.SpreadRepeatedConsecutiveLines(lines);

        Assert.Equal(snapshot, lines);
    }

    // --- Format ---

    [Fact]
    public void Format_RendersLrcTimestamps()
    {
        var lines = new List<(double Start, string Text)>
        {
            (0.0, "start"),
            (75.34, "later"),
        };

        var lrc = LrcBuilder.Format(lines);

        Assert.Equal("[00:00.00]start\n[01:15.34]later", lrc);
    }

    [Fact]
    public void Format_RollsOverCentisecondAndSecondCarry()
    {
        // 59.999s rounds centis to 100 -> carries to seconds 60 -> carries to the next minute.
        var lrc = LrcBuilder.Format(new List<(double Start, string Text)> { (59.999, "edge") });

        Assert.Equal("[01:00.00]edge", lrc);
    }

    [Fact]
    public void Format_ClampsNegativeStartToZero()
    {
        var lrc = LrcBuilder.Format(new List<(double Start, string Text)> { (-3.0, "clamped") });

        Assert.Equal("[00:00.00]clamped", lrc);
    }
}
