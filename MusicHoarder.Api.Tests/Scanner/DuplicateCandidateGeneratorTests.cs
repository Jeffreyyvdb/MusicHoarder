using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Scanner;

/// <summary>
/// The blocking rules in isolation: no DbContext, no audio — songs in, candidate pairs out.
/// </summary>
public class DuplicateCandidateGeneratorTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    [Fact]
    public void Generate_ExactFingerprint_PairsWithoutAnyGuard()
    {
        // Byte-identical audio needs no duration or qualifier agreement — the tags may say anything.
        var candidates = Generate(
            Song(1, fingerprint: "FP", title: "Song (Live)", durationSeconds: 100),
            Song(2, fingerprint: "FP", title: "Song", durationSeconds: 300));

        var reason = Assert.Single(candidates);
        Assert.Equal(new SongIdPair(1, 2), reason.Key);
        Assert.Equal(DuplicateMatchReason.ExactFingerprint, reason.Value);
    }

    [Fact]
    public void Generate_KeysEveryPairLowIdFirst_RegardlessOfInputOrder()
    {
        var candidates = Generate(
            Song(7, fingerprint: "FP"),
            Song(3, fingerprint: "FP"));

        Assert.Equal(new SongIdPair(3, 7), Assert.Single(candidates).Key);
    }

    [Fact]
    public void Generate_BlankFingerprints_NeverBlockTogether()
    {
        var candidates = Generate(
            Song(1, fingerprint: "", title: "A"),
            Song(2, fingerprint: "", title: "B"),
            Song(3, fingerprint: null, title: "C"),
            Song(4, fingerprint: null, title: "D"));

        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData(200, 205, true)]   // within twice the 3s tolerance
    [InlineData(200, 210, false)]  // outside it
    [InlineData(200, null, true)]  // an unknown duration doesn't block an identifier match
    public void Generate_SharedAcoustId_UsesTheLooseDurationTolerance(int a, int? b, bool expectPair)
    {
        var candidates = Generate(
            new MusicEnricherOptions { DuplicateDurationToleranceSeconds = 3 },
            Song(1, acoustId: "acoustid-1", title: "One", durationSeconds: a),
            Song(2, acoustId: "acoustid-1", title: "Two", durationSeconds: b));

        if (expectPair)
            Assert.Equal(DuplicateMatchReason.AcoustIdTrack, Assert.Single(candidates).Value);
        else
            Assert.Empty(candidates);
    }

    [Fact]
    public void Generate_SharedIsrc_MatchesAcrossCaseAndDashes()
    {
        var candidates = Generate(
            Song(1, isrc: "us-abc-12-34567", title: "One"),
            Song(2, isrc: "USABC1234567", title: "Two"));

        Assert.Equal(DuplicateMatchReason.Isrc, Assert.Single(candidates).Value);
    }

    [Fact]
    public void Generate_MetadataBlock_KeysOnPrimaryArtist_AndRequiresBothDurations()
    {
        var paired = Generate(
            Song(1, artist: "Main Artist feat. Guest", title: "Same Song", durationSeconds: 200),
            Song(2, artist: "Main Artist", title: "Same Song", durationSeconds: 202));
        Assert.Equal(DuplicateMatchReason.Metadata, Assert.Single(paired).Value);

        var unknownDuration = Generate(
            Song(1, artist: "Main Artist", title: "Same Song", durationSeconds: 200),
            Song(2, artist: "Main Artist", title: "Same Song", durationSeconds: null));
        Assert.Empty(unknownDuration);

        var driftedDuration = Generate(
            Song(1, artist: "Main Artist", title: "Same Song", durationSeconds: 200),
            Song(2, artist: "Main Artist", title: "Same Song", durationSeconds: 204));
        Assert.Empty(driftedDuration);
    }

    [Fact]
    public void Generate_StrongQualifier_GatesIdentifierAndMetadataBlocks()
    {
        // Same AcoustID and same normalized text, but one is the live version.
        var candidates = Generate(
            Song(1, acoustId: "acoustid-1", artist: "Artist", title: "Song (Live)", durationSeconds: 200),
            Song(2, acoustId: "acoustid-1", artist: "Artist", title: "Song", durationSeconds: 200));

        Assert.Empty(candidates);
    }

    [Fact]
    public void Generate_AccumulatesReasons_WhenSeveralStrategiesFindOnePair()
    {
        var candidates = Generate(
            Song(1, isrc: "USABC1234567", acoustId: "acoustid-1", artist: "Artist", title: "Song", durationSeconds: 200),
            Song(2, isrc: "USABC1234567", acoustId: "acoustid-1", artist: "Artist", title: "Song", durationSeconds: 200));

        Assert.Equal(
            DuplicateMatchReason.AcoustIdTrack | DuplicateMatchReason.Isrc | DuplicateMatchReason.Metadata,
            Assert.Single(candidates).Value);
    }

    [Fact]
    public void Generate_SkipsABlockOverTheConfiguredCap_ButKeepsTheOthers()
    {
        var candidates = Generate(
            new MusicEnricherOptions { DuplicateMaxBlockSize = 2 },
            Song(1, fingerprint: "BIG", title: "One"),
            Song(2, fingerprint: "BIG", title: "Two"),
            Song(3, fingerprint: "BIG", title: "Three"),
            Song(4, fingerprint: "PAIR", title: "Four"),
            Song(5, fingerprint: "PAIR", title: "Five"));

        Assert.Equal(new SongIdPair(4, 5), Assert.Single(candidates).Key);
    }

    [Theory]
    [InlineData("Artist", "Title", false)]
    [InlineData("", "Title", true)]
    [InlineData("Artist", "", true)]
    [InlineData(null, null, true)]
    public void MetadataBlockKey_IsNullWhenArtistOrTitleIsBlank(string? artist, string? title, bool expectNull)
    {
        var key = DuplicateCandidateGenerator.MetadataBlockKey(Song(1, artist: artist, title: title));

        Assert.Equal(expectNull, key is null);
    }

    [Fact]
    public void MetadataBlockKey_SeparatesArtistFromTitle()
    {
        // "ab" + "c" must not collide with "a" + "bc".
        var first = DuplicateCandidateGenerator.MetadataBlockKey(Song(1, artist: "ab", title: "c"));
        var second = DuplicateCandidateGenerator.MetadataBlockKey(Song(2, artist: "a", title: "bc"));

        Assert.NotEqual(first, second);
    }

    private static Dictionary<SongIdPair, DuplicateMatchReason> Generate(params SongMetadata[] songs)
        => Generate(new MusicEnricherOptions(), songs);

    private static Dictionary<SongIdPair, DuplicateMatchReason> Generate(
        MusicEnricherOptions opts, params SongMetadata[] songs)
        => new DuplicateCandidateGenerator(NullLogger<DuplicateCandidateGenerator>.Instance)
            .Generate(Owner, songs, opts);

    private static SongMetadata Song(
        int id,
        string? fingerprint = null,
        string? acoustId = null,
        string? isrc = null,
        string? artist = "Test Artist",
        string? title = "Test Track",
        int? durationSeconds = null)
        => new()
        {
            Id = id,
            OwnerUserId = Owner,
            SourcePath = $"/music/{id}.mp3",
            FileName = $"{id}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1_000_000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Fingerprint = fingerprint,
            AcoustIdTrackId = acoustId,
            Isrc = isrc,
            Artist = artist,
            Title = title,
            DurationSeconds = durationSeconds,
        };
}
