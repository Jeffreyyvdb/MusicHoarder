using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class QualityBucketTests
{
    [Theory]
    // Flagged: the algorithm asked for review at grade time, whatever the LLM later said.
    [InlineData("NeedsReview", SongQualityVerdict.Wrong, QualityBucket.Flagged)]
    [InlineData("NeedsReview", SongQualityVerdict.Excellent, QualityBucket.Flagged)]
    [InlineData("needsreview", SongQualityVerdict.Good, QualityBucket.Flagged)]
    // Silent failures: auto-accepted, but the LLM disagrees.
    [InlineData("Matched", SongQualityVerdict.Wrong, QualityBucket.Silent)]
    [InlineData("Matched", SongQualityVerdict.Questionable, QualityBucket.Silent)]
    [InlineData("matched", SongQualityVerdict.Wrong, QualityBucket.Silent)]
    // Verified clean: auto-accepted and the LLM agrees it's excellent.
    [InlineData("Matched", SongQualityVerdict.Excellent, QualityBucket.Verified)]
    // Everything else falls through to Other.
    [InlineData("Matched", SongQualityVerdict.Good, QualityBucket.Other)]
    [InlineData("Matched", SongQualityVerdict.Ungradeable, QualityBucket.Other)]
    [InlineData("Pending", SongQualityVerdict.Wrong, QualityBucket.Other)]
    [InlineData("Failed", SongQualityVerdict.Excellent, QualityBucket.Other)]
    [InlineData(null, SongQualityVerdict.Wrong, QualityBucket.Other)]
    public void Classify_BucketsByStatusAndVerdict(
        string? enrichmentStatusAtGrade, SongQualityVerdict verdict, QualityBucket expected)
    {
        Assert.Equal(expected, QualityBuckets.Classify(enrichmentStatusAtGrade, verdict));
    }

    [Theory]
    [InlineData(QualityBucket.Flagged, "flagged")]
    [InlineData(QualityBucket.Silent, "silent")]
    [InlineData(QualityBucket.Verified, "verified")]
    [InlineData(QualityBucket.Other, "other")]
    public void Name_ReturnsLowercaseWireName(QualityBucket bucket, string expected)
    {
        Assert.Equal(expected, bucket.Name());
    }
}
