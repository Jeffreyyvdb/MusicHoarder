using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Behaviour pins for <c>GET /api/library/duplicates</c>: the cluster derivation over active links,
/// keeper election order, per-member evidence aggregation, and the exact wire shape both clients read.
/// Everything is asserted on the serialized payload so the tests hold across an internal restructuring.
/// </summary>
public class ListDuplicatesEndpointTests
{
    private static readonly Guid OwnerA = WellKnownUsers.OwnerId;
    private static readonly Guid OwnerB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task NoLinks_ReturnsEmptyReport()
    {
        using var db = NewContext();
        db.Songs.AddRange(Song(1, ".flac"), Song(2, ".mp3", bitrate: 320));
        db.SaveChanges();

        var json = Json(await SongsEndpoints.ListDuplicates(db));

        Assert.Equal(0, json.GetProperty("TotalDuplicates").GetInt32());
        Assert.Equal(0, json.GetProperty("Groups").GetInt32());
        Assert.Empty(json.GetProperty("DuplicateGroups").EnumerateArray());
    }

    [Fact]
    public async Task ChainedLinks_FormOneGroup_KeeperFirst_GroupIdIsLowestSongId()
    {
        using var db = NewContext();
        db.Songs.AddRange(
            Song(10, ".mp3", bitrate: 128, size: 4_000_000),
            Song(20, ".flac", size: 30_000_000),
            Song(30, ".mp3", bitrate: 320, size: 10_000_000));
        db.SongDuplicateLinks.AddRange(
            Link(10, 20, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed, similarity: 1.0),
            Link(20, 30, DuplicateMatchReason.FingerprintSimilarity, DuplicateConfidence.Confirmed, similarity: 0.97));
        db.SaveChanges();

        var json = Json(await SongsEndpoints.ListDuplicates(db));

        Assert.Equal(1, json.GetProperty("Groups").GetInt32());
        var group = Assert.Single(json.GetProperty("DuplicateGroups").EnumerateArray());
        Assert.Equal(10, group.GetProperty("GroupId").GetInt32());
        Assert.Equal("confirmed", group.GetProperty("Confidence").GetString());
        Assert.Equal(20, group.GetProperty("Keeper").GetProperty("Id").GetInt32());

        // Keeper election: quality first (FLAC > MP3 320 > MP3 128), keeper is the first member.
        var members = group.GetProperty("Members").EnumerateArray().ToList();
        Assert.Equal([20, 30, 10], members.Select(m => m.GetProperty("Id").GetInt32()));
        Assert.Equal([true, false, false], members.Select(m => m.GetProperty("IsKeeper").GetBoolean()));
    }

    [Fact]
    public async Task TotalDuplicates_CountsMembersFlaggedByDetection_NotClusterSize()
    {
        using var db = NewContext();
        var keeper = Song(1, ".flac");
        var lossy = Song(2, ".mp3", bitrate: 320);
        var unflagged = Song(3, ".mp3", bitrate: 128);
        lossy.MarkAsDuplicate(keeper.Id);
        db.Songs.AddRange(keeper, lossy, unflagged);
        db.SongDuplicateLinks.AddRange(
            Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(1, 3, DuplicateMatchReason.Metadata, DuplicateConfidence.Suspected));
        db.SaveChanges();

        var json = Json(await SongsEndpoints.ListDuplicates(db));

        Assert.Equal(1, json.GetProperty("TotalDuplicates").GetInt32());
        var members = Assert.Single(json.GetProperty("DuplicateGroups").EnumerateArray())
            .GetProperty("Members").EnumerateArray().ToList();
        Assert.Equal(3, members.Count);
        var flagged = members.Single(m => m.GetProperty("Id").GetInt32() == 2);
        Assert.True(flagged.GetProperty("IsDuplicate").GetBoolean());
        Assert.Equal(1, flagged.GetProperty("DuplicateOfId").GetInt32());
    }

    [Fact]
    public async Task SuspectedOnlyCluster_IsSuspected_OnGroupAndEveryMember()
    {
        using var db = NewContext();
        db.Songs.AddRange(Song(1, ".flac"), Song(2, ".flac"));
        db.SongDuplicateLinks.Add(Link(1, 2, DuplicateMatchReason.Metadata, DuplicateConfidence.Suspected));
        db.SaveChanges();

        var group = Assert.Single(Json(await SongsEndpoints.ListDuplicates(db))
            .GetProperty("DuplicateGroups").EnumerateArray());

        Assert.Equal("suspected", group.GetProperty("Confidence").GetString());
        Assert.All(group.GetProperty("Members").EnumerateArray(),
            m => Assert.Equal("suspected", m.GetProperty("Confidence").GetString()));
    }

    [Fact]
    public async Task MixedCluster_GroupIsConfirmed_MemberConfidenceFollowsItsOwnLinks()
    {
        using var db = NewContext();
        db.Songs.AddRange(Song(1, ".flac"), Song(2, ".mp3", bitrate: 320), Song(3, ".mp3", bitrate: 128));
        db.SongDuplicateLinks.AddRange(
            Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(2, 3, DuplicateMatchReason.Metadata, DuplicateConfidence.Suspected));
        db.SaveChanges();

        var group = Assert.Single(Json(await SongsEndpoints.ListDuplicates(db))
            .GetProperty("DuplicateGroups").EnumerateArray());

        Assert.Equal("confirmed", group.GetProperty("Confidence").GetString());
        var byId = group.GetProperty("Members").EnumerateArray()
            .ToDictionary(m => m.GetProperty("Id").GetInt32(), m => m.GetProperty("Confidence").GetString());
        Assert.Equal("confirmed", byId[1]);
        Assert.Equal("confirmed", byId[2]);
        Assert.Equal("suspected", byId[3]);
    }

    [Fact]
    public async Task MemberEvidence_UnionsReasonsAcrossItsLinks_InFlagOrder_AndTakesMaxSimilarity()
    {
        using var db = NewContext();
        db.Songs.AddRange(Song(1, ".flac"), Song(2, ".mp3", bitrate: 320), Song(3, ".mp3", bitrate: 128));
        db.SongDuplicateLinks.AddRange(
            Link(1, 2, DuplicateMatchReason.Metadata | DuplicateMatchReason.ExactFingerprint,
                DuplicateConfidence.Confirmed, similarity: 1.0),
            Link(2, 3, DuplicateMatchReason.Isrc | DuplicateMatchReason.AcoustIdTrack,
                DuplicateConfidence.Suspected, similarity: 0.8));
        db.SaveChanges();

        var members = Assert.Single(Json(await SongsEndpoints.ListDuplicates(db))
            .GetProperty("DuplicateGroups").EnumerateArray())
            .GetProperty("Members").EnumerateArray()
            .ToDictionary(m => m.GetProperty("Id").GetInt32());

        Assert.Equal(["exact-fingerprint", "acoustid", "isrc", "metadata"], Strings(members[2], "Reasons"));
        Assert.Equal(1.0, members[2].GetProperty("Similarity").GetDouble());
        Assert.Equal(["exact-fingerprint", "metadata"], Strings(members[1], "Reasons"));
        Assert.Equal(["acoustid", "isrc"], Strings(members[3], "Reasons"));
        Assert.Equal(0.8, members[3].GetProperty("Similarity").GetDouble());
    }

    [Fact]
    public async Task Similarity_IsNull_WhenNoLinkOfTheMemberCarriesOne()
    {
        using var db = NewContext();
        db.Songs.AddRange(Song(1, ".flac"), Song(2, ".flac"));
        db.SongDuplicateLinks.Add(Link(1, 2, DuplicateMatchReason.AcoustIdTrack, DuplicateConfidence.Confirmed));
        db.SaveChanges();

        var group = Assert.Single(Json(await SongsEndpoints.ListDuplicates(db))
            .GetProperty("DuplicateGroups").EnumerateArray());

        Assert.All(group.GetProperty("Members").EnumerateArray(),
            m => Assert.Equal(JsonValueKind.Null, m.GetProperty("Similarity").ValueKind));
    }

    [Fact]
    public async Task DismissedLinks_NeverJoinAGroup()
    {
        using var db = NewContext();
        db.Songs.AddRange(Song(1, ".flac"), Song(2, ".flac"), Song(3, ".flac"), Song(4, ".flac"));
        db.SongDuplicateLinks.AddRange(
            Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(2, 3, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed, status: DuplicateLinkStatus.Dismissed),
            Link(3, 4, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed, status: DuplicateLinkStatus.Dismissed));
        db.SaveChanges();

        var json = Json(await SongsEndpoints.ListDuplicates(db));

        var group = Assert.Single(json.GetProperty("DuplicateGroups").EnumerateArray());
        Assert.Equal([1, 2], group.GetProperty("Members").EnumerateArray()
            .Select(m => m.GetProperty("Id").GetInt32()).Order());
    }

    [Fact]
    public async Task LinksTouchingASoftDeletedSong_AreSkipped_AndAOnePersonGroupIsDropped()
    {
        using var db = NewContext();
        var deleted = Song(3, ".flac");
        deleted.SoftDelete();
        var lone = Song(5, ".flac");
        var loneTwin = Song(6, ".flac");
        loneTwin.SoftDelete();
        db.Songs.AddRange(Song(1, ".flac"), Song(2, ".flac"), deleted, lone, loneTwin);
        db.SongDuplicateLinks.AddRange(
            Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(2, 3, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(5, 6, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed));
        db.SaveChanges();

        var json = Json(await SongsEndpoints.ListDuplicates(db));

        Assert.Equal(1, json.GetProperty("Groups").GetInt32());
        var group = Assert.Single(json.GetProperty("DuplicateGroups").EnumerateArray());
        Assert.Equal([1, 2], group.GetProperty("Members").EnumerateArray()
            .Select(m => m.GetProperty("Id").GetInt32()).Order());
    }

    [Fact]
    public async Task Groups_AreOrderedByGroupId_Ascending()
    {
        using var db = NewContext();
        db.Songs.AddRange(Song(7, ".flac"), Song(8, ".flac"), Song(1, ".flac"), Song(9, ".flac"));
        db.SongDuplicateLinks.AddRange(
            Link(7, 8, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed),
            Link(1, 9, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed));
        db.SaveChanges();

        var json = Json(await SongsEndpoints.ListDuplicates(db));

        Assert.Equal(2, json.GetProperty("Groups").GetInt32());
        Assert.Equal([1, 7], json.GetProperty("DuplicateGroups").EnumerateArray()
            .Select(g => g.GetProperty("GroupId").GetInt32()));
    }

    [Fact]
    public async Task PinnedKeeper_BeatsQuality_AndIsReportedPinned()
    {
        using var db = NewContext();
        var pinned = Song(1, ".mp3", bitrate: 128);
        pinned.DuplicateKeeperPinnedAtUtc = DateTime.UtcNow;
        var better = Song(2, ".flac");
        better.LibraryBuildStatus = LibraryBuildStatus.Done;
        better.DestinationPath = "/dest/better.flac";
        db.Songs.AddRange(pinned, better);
        db.SongDuplicateLinks.Add(Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed));
        db.SaveChanges();

        var group = Assert.Single(Json(await SongsEndpoints.ListDuplicates(db))
            .GetProperty("DuplicateGroups").EnumerateArray());

        var keeper = group.GetProperty("Keeper");
        Assert.Equal(1, keeper.GetProperty("Id").GetInt32());
        Assert.True(keeper.GetProperty("IsPinned").GetBoolean());
        Assert.False(keeper.GetProperty("IsBuilt").GetBoolean());
        var loser = group.GetProperty("Members")[1];
        Assert.Equal(2, loser.GetProperty("Id").GetInt32());
        Assert.False(loser.GetProperty("IsPinned").GetBoolean());
        Assert.True(loser.GetProperty("IsBuilt").GetBoolean());
    }

    [Fact]
    public async Task Member_CarriesTheSongColumns_AndTheServerQualityScore()
    {
        using var db = NewContext();
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
        db.Songs.AddRange(song, Song(2, ".mp3", bitrate: 128));
        db.SongDuplicateLinks.Add(Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed));
        db.SaveChanges();

        var member = Assert.Single(Json(await SongsEndpoints.ListDuplicates(db))
            .GetProperty("DuplicateGroups").EnumerateArray()).GetProperty("Keeper");

        Assert.Equal("/src/1.mp3", member.GetProperty("SourcePath").GetString());
        Assert.Equal("1.mp3", member.GetProperty("FileName").GetString());
        Assert.Equal(".mp3", member.GetProperty("Extension").GetString());
        Assert.Equal(9_000_000, member.GetProperty("FileSizeBytes").GetInt64());
        Assert.Equal("Artist", member.GetProperty("Artist").GetString());
        Assert.Equal("Album Artist", member.GetProperty("AlbumArtist").GetString());
        Assert.Equal("Album", member.GetProperty("Album").GetString());
        Assert.Equal("Title", member.GetProperty("Title").GetString());
        Assert.Equal(2001, member.GetProperty("Year").GetInt32());
        Assert.Equal(7, member.GetProperty("TrackNumber").GetInt32());
        Assert.Equal(241, member.GetProperty("DurationSeconds").GetInt32());
        Assert.Equal(320, member.GetProperty("Bitrate").GetInt32());
        Assert.Equal("FP", member.GetProperty("Fingerprint").GetString());
        Assert.Equal((int)EnrichmentStatus.Matched, member.GetProperty("EnrichmentStatus").GetInt32());
        Assert.Equal("/dest/x.mp3", member.GetProperty("DestinationPath").GetString());
        Assert.Equal(AudioQuality.Score(".mp3", 320), member.GetProperty("QualityScore").GetInt32());
    }

    [Fact]
    public async Task WireShape_IsExactlyWhatTheClientsRead()
    {
        using var db = NewContext();
        db.Songs.AddRange(Song(1, ".flac"), Song(2, ".mp3", bitrate: 320));
        db.SongDuplicateLinks.Add(Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed));
        db.SaveChanges();

        var json = Json(await SongsEndpoints.ListDuplicates(db));

        Assert.Equal(["TotalDuplicates", "Groups", "DuplicateGroups"], Names(json));
        var group = json.GetProperty("DuplicateGroups")[0];
        Assert.Equal(["GroupId", "Confidence", "Keeper", "Members"], Names(group));
        string[] member =
        [
            "Id", "SourcePath", "FileName", "Extension", "FileSizeBytes",
            "Artist", "AlbumArtist", "Album", "Title", "Year", "TrackNumber",
            "DurationSeconds", "Bitrate", "Fingerprint", "IsDuplicate", "DuplicateOfId",
            "EnrichmentStatus", "DestinationPath", "IsBuilt", "IsKeeper", "IsPinned",
            "Confidence", "Reasons", "Similarity", "QualityScore",
        ];
        Assert.Equal(member, Names(group.GetProperty("Keeper")));
        Assert.All(group.GetProperty("Members").EnumerateArray(), m => Assert.Equal(member, Names(m)));
    }

    [Fact]
    public async Task ScopesToTheCallingOwner()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;

        using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.AddRange(Song(1, ".flac"), Song(2, ".flac"));
            seed.SongDuplicateLinks.Add(Link(1, 2, DuplicateMatchReason.ExactFingerprint, DuplicateConfidence.Confirmed));
            seed.SaveChanges();
        }

        using var asOwnerB = new MusicHoarderDbContext(options, new StubCurrentUser(OwnerB));
        var json = Json(await SongsEndpoints.ListDuplicates(asOwnerB));

        Assert.Equal(0, json.GetProperty("Groups").GetInt32());
        Assert.Empty(json.GetProperty("DuplicateGroups").EnumerateArray());
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static JsonElement Json(IResult result)
    {
        var value = ((IValueHttpResult)result).Value!;
        return JsonSerializer.SerializeToElement(value, value.GetType());
    }

    private static List<string> Names(JsonElement el) => el.EnumerateObject().Select(p => p.Name).ToList();

    private static List<string?> Strings(JsonElement el, string property) =>
        el.GetProperty(property).EnumerateArray().Select(x => x.GetString()).ToList();

    private static SongMetadata Song(int id, string extension, int? bitrate = null, long size = 20_000_000, Guid? owner = null) => new()
    {
        Id = id,
        OwnerUserId = owner ?? OwnerA,
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
        double? similarity = null,
        DuplicateLinkStatus status = DuplicateLinkStatus.Active,
        Guid? owner = null) => new()
    {
        OwnerUserId = owner ?? OwnerA,
        SongIdLow = low,
        SongIdHigh = high,
        Reasons = reasons,
        Confidence = confidence,
        Similarity = similarity,
        Status = status,
        DetectedAtUtc = DateTime.UtcNow,
    };

    private sealed class StubCurrentUser(Guid userId) : ICurrentUserAccessor
    {
        public CurrentUser? User { get; } = new(userId, "owner@test", UserRole.Admin, "Owner");
        public Guid UserId => userId;
    }
}
