using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Scanner;

/// <summary>The duplicates read model in isolation: plain link rows and a song dictionary in, a report out.</summary>
public class DuplicateGroupProjectionTests
{
    [Fact]
    public void Build_WithNoLinks_IsEmpty()
    {
        var report = DuplicateGroupProjection.Build([], ById(Song(1, ".flac")));

        Assert.Equal(0, report.TotalDuplicates);
        Assert.Equal(0, report.Groups);
        Assert.Empty(report.DuplicateGroups);
    }

    [Fact]
    public void Build_ClustersTransitivelyAcrossLinks_AndRanksTheKeeperFirst()
    {
        var songs = ById(
            Song(10, ".mp3", bitrate: 128),
            Song(20, ".flac"),
            Song(30, ".mp3", bitrate: 320));
        var links = new[]
        {
            Link(10, 20, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(20, 30, DuplicateMatchReason.FingerprintSimilarity, DuplicateConfidence.Confirmed),
        };

        var group = Assert.Single(DuplicateGroupProjection.Build(links, songs).DuplicateGroups);

        Assert.Equal(10, group.GroupId);
        Assert.Equal("confirmed", group.Confidence);
        Assert.Equal(20, group.Keeper.Id);
        Assert.Same(group.Members[0], group.Keeper);
        Assert.Equal([20, 30, 10], group.Members.Select(m => m.Id));
        Assert.Equal([true, false, false], group.Members.Select(m => m.IsKeeper));
    }

    [Fact]
    public void Build_DropsLinksWhoseEndIsNotAmongTheSongs_AndNeverEmitsAOneMemberGroup()
    {
        var songs = ById(Song(1, ".flac"), Song(2, ".flac"), Song(5, ".flac"));
        var links = new[]
        {
            Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(2, 3, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed), // 3 not loaded (soft-deleted)
            Link(5, 6, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed), // 6 not loaded
        };

        var report = DuplicateGroupProjection.Build(links, songs);

        var group = Assert.Single(report.DuplicateGroups);
        Assert.Equal([1, 2], group.Members.Select(m => m.Id).Order());
        Assert.Equal(1, report.Groups);
    }

    [Fact]
    public void Build_OrdersGroupsByGroupId()
    {
        var songs = ById(Song(1, ".flac"), Song(4, ".flac"), Song(7, ".flac"), Song(9, ".flac"));
        var links = new[]
        {
            Link(7, 9, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(1, 4, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
        };

        var report = DuplicateGroupProjection.Build(links, songs);

        Assert.Equal([1, 7], report.DuplicateGroups.Select(g => g.GroupId));
    }

    [Fact]
    public void Build_GroupConfidence_IsConfirmedWhenAnyLinkIs_MemberConfidenceFollowsItsOwnLinks()
    {
        var songs = ById(Song(1, ".flac"), Song(2, ".mp3", bitrate: 320), Song(3, ".mp3", bitrate: 128));
        var links = new[]
        {
            Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(2, 3, DuplicateMatchReason.Metadata, DuplicateConfidence.Suspected),
        };

        var group = Assert.Single(DuplicateGroupProjection.Build(links, songs).DuplicateGroups);

        Assert.Equal("confirmed", group.Confidence);
        var byId = group.Members.ToDictionary(m => m.Id, m => m.Confidence);
        Assert.Equal("confirmed", byId[1]);
        Assert.Equal("confirmed", byId[2]);
        Assert.Equal("suspected", byId[3]);
    }

    [Fact]
    public void Build_SuspectedOnlyCluster_IsSuspectedThroughout()
    {
        var songs = ById(Song(1, ".flac"), Song(2, ".flac"));
        var links = new[] { Link(1, 2, DuplicateMatchReason.Metadata, DuplicateConfidence.Suspected) };

        var group = Assert.Single(DuplicateGroupProjection.Build(links, songs).DuplicateGroups);

        Assert.Equal("suspected", group.Confidence);
        Assert.All(group.Members, m => Assert.Equal("suspected", m.Confidence));
    }

    [Fact]
    public void Build_MemberEvidence_UnionsReasons_AndTakesTheMaxSimilarity_NullWhenUnmeasured()
    {
        var songs = ById(Song(1, ".flac"), Song(2, ".mp3", bitrate: 320), Song(3, ".mp3", bitrate: 128), Song(4, ".mp3", bitrate: 64));
        var links = new[]
        {
            Link(1, 2, DuplicateMatchReason.ExactFingerprint | DuplicateMatchReason.Metadata, DuplicateConfidence.Confirmed, similarity: 1.0),
            Link(2, 3, DuplicateMatchReason.Isrc, DuplicateConfidence.Suspected, similarity: 0.8),
            Link(3, 4, DuplicateMatchReason.AcoustIdTrack, DuplicateConfidence.Suspected),
        };

        var byId = Assert.Single(DuplicateGroupProjection.Build(links, songs).DuplicateGroups)
            .Members.ToDictionary(m => m.Id);

        Assert.Equal(["exact-fingerprint", "isrc", "metadata"], byId[2].Reasons);
        Assert.Equal(1.0, byId[2].Similarity);
        Assert.Equal(["acoustid", "isrc"], byId[3].Reasons);
        Assert.Equal(0.8, byId[3].Similarity);
        Assert.Equal(["acoustid"], byId[4].Reasons);
        Assert.Null(byId[4].Similarity);
    }

    [Fact]
    public void Build_TotalDuplicates_CountsFlaggedMembersOnly()
    {
        var keeper = Song(1, ".flac");
        var flagged = Song(2, ".mp3", bitrate: 320);
        flagged.MarkAsDuplicate(keeper.Id);
        var songs = ById(keeper, flagged, Song(3, ".mp3", bitrate: 128));
        var links = new[]
        {
            Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(1, 3, DuplicateMatchReason.Metadata, DuplicateConfidence.Suspected),
        };

        var report = DuplicateGroupProjection.Build(links, songs);

        Assert.Equal(1, report.TotalDuplicates);
        var member = Assert.Single(report.DuplicateGroups).Members.Single(m => m.Id == 2);
        Assert.True(member.IsDuplicate);
        Assert.Equal(1, member.DuplicateOfId);
    }

    [Fact]
    public void Build_PinnedMember_IsKeeperAndPinned_BuiltIsDoneWithADestination()
    {
        var pinned = Song(1, ".mp3", bitrate: 128);
        pinned.DuplicateKeeperPinnedAtUtc = DateTime.UtcNow;
        var built = Song(2, ".flac");
        built.LibraryBuildStatus = LibraryBuildStatus.Done;
        built.DestinationPath = "/dest/2.flac";
        var doneWithoutPath = Song(3, ".flac");
        doneWithoutPath.LibraryBuildStatus = LibraryBuildStatus.Done;
        var links = new[]
        {
            Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(2, 3, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
        };

        var byId = Assert.Single(DuplicateGroupProjection.Build(links, ById(pinned, built, doneWithoutPath)).DuplicateGroups)
            .Members.ToDictionary(m => m.Id);

        Assert.True(byId[1].IsKeeper);
        Assert.True(byId[1].IsPinned);
        Assert.False(byId[1].IsBuilt);
        Assert.False(byId[2].IsKeeper);
        Assert.True(byId[2].IsBuilt);
        Assert.False(byId[3].IsBuilt);
    }

    [Fact]
    public void Build_CopiesTheSongColumns_AndScoresQualityLikeDetection()
    {
        var song = Song(1, ".mp3", bitrate: 320, size: 9_000_000);
        song.Artist = "Artist";
        song.AlbumArtist = "Album Artist";
        song.Album = "Album";
        song.Title = "Title";
        song.Year = 2001;
        song.TrackNumber = 7;
        song.DurationSeconds = 241;
        song.Fingerprint = "FP";
        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.DestinationPath = "/dest/x.mp3";
        var links = new[] { Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed) };

        var keeper = Assert.Single(DuplicateGroupProjection.Build(links, ById(song, Song(2, ".mp3", bitrate: 128))).DuplicateGroups).Keeper;

        Assert.Equal(1, keeper.Id);
        Assert.Equal("/src/1.mp3", keeper.SourcePath);
        Assert.Equal("1.mp3", keeper.FileName);
        Assert.Equal(".mp3", keeper.Extension);
        Assert.Equal(9_000_000, keeper.FileSizeBytes);
        Assert.Equal("Artist", keeper.Artist);
        Assert.Equal("Album Artist", keeper.AlbumArtist);
        Assert.Equal("Album", keeper.Album);
        Assert.Equal("Title", keeper.Title);
        Assert.Equal(2001, keeper.Year);
        Assert.Equal(7, keeper.TrackNumber);
        Assert.Equal(241, keeper.DurationSeconds);
        Assert.Equal(320, keeper.Bitrate);
        Assert.Equal("FP", keeper.Fingerprint);
        Assert.Equal(EnrichmentStatus.Matched, keeper.EnrichmentStatus);
        Assert.Equal("/dest/x.mp3", keeper.DestinationPath);
        Assert.Equal(IDuplicateDetectionService.QualityScore(song), keeper.QualityScore);
    }

    [Theory]
    [InlineData(DuplicateMatchReason.None, new string[0])]
    [InlineData(DuplicateMatchReason.ExactFingerprint, new[] { "exact-fingerprint" })]
    [InlineData(DuplicateMatchReason.FingerprintSimilarity, new[] { "fingerprint-similarity" })]
    [InlineData(DuplicateMatchReason.AcoustIdTrack, new[] { "acoustid" })]
    [InlineData(DuplicateMatchReason.Isrc, new[] { "isrc" })]
    [InlineData(DuplicateMatchReason.Metadata, new[] { "metadata" })]
    [InlineData(DuplicateMatchReason.Metadata | DuplicateMatchReason.ExactFingerprint | DuplicateMatchReason.Isrc,
        new[] { "exact-fingerprint", "isrc", "metadata" })]
    public void DescribeReasons_NamesEachFlag_InFlagOrder(DuplicateMatchReason reasons, string[] expected)
    {
        Assert.Equal(expected, DuplicateGroupProjection.DescribeReasons(reasons));
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static Dictionary<int, SongMetadata> ById(params SongMetadata[] songs) => songs.ToDictionary(s => s.Id);

    private static SongMetadata Song(int id, string extension, int? bitrate = null, long size = 20_000_000) => new()
    {
        Id = id,
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = $"/src/{id}{extension}",
        FileName = $"{id}{extension}",
        Extension = extension,
        FileSizeBytes = size,
        Bitrate = bitrate,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
    };

    private static SongDuplicateLink Link(
        int low, int high,
        DuplicateMatchReason reasons,
        DuplicateConfidence confidence,
        double? similarity = null) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SongIdLow = low,
        SongIdHigh = high,
        Reasons = reasons,
        Confidence = confidence,
        Similarity = similarity,
        Status = DuplicateLinkStatus.Active,
        DetectedAtUtc = DateTime.UtcNow,
    };
}
