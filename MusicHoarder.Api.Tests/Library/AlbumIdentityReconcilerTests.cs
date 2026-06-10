using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The album-level vote the per-song enrichment pipeline never takes: given every track that builds
/// into one destination folder, elect one canonical <see cref="AlbumIdentity"/> so the on-disk album
/// isn't split by a server's MusicBrainz-release grouping key.
/// </summary>
public class AlbumIdentityReconcilerTests
{
    private readonly AlbumIdentityReconciler reconciler = new();

    [Fact]
    public void Reconcile_MixedReleaseIds_ElectsTheMajorityIdForAll()
    {
        var members = new[]
        {
            Song(releaseId: "rel-keep", track: 1),
            Song(releaseId: "rel-keep", track: 2),
            Song(releaseId: "rel-stray", track: 3),
        };

        var identity = reconciler.Reconcile(members);

        Assert.Equal("rel-keep", identity.MusicBrainzReleaseId);
    }

    [Fact]
    public void Reconcile_ReleaseIdTie_BreaksDeterministically_ByAnchorThenOrdinal()
    {
        // 1 vote each -> tie broken by the disc-1/track-1 anchor: track 1 carries "rel-bbb".
        var members = new[]
        {
            Song(releaseId: "rel-ccc", track: 2),
            Song(releaseId: "rel-bbb", track: 1),
        };

        var first = reconciler.Reconcile(members).MusicBrainzReleaseId;
        var second = reconciler.Reconcile(members.Reverse().ToArray()).MusicBrainzReleaseId;

        Assert.Equal("rel-bbb", first);
        Assert.Equal(first, second); // stable regardless of input order
    }

    [Fact]
    public void Reconcile_NoMemberHasReleaseId_LeavesItNull_ButHarmonizesOtherFields()
    {
        var members = new[]
        {
            Song(releaseId: null, album: "1999", year: 2012),
            Song(releaseId: null, album: "1999", year: 2012),
        };

        var identity = reconciler.Reconcile(members);

        Assert.Null(identity.MusicBrainzReleaseId);
        Assert.Equal("1999", identity.Album);
        Assert.Equal(2012, identity.Year);
    }

    [Fact]
    public void Reconcile_Compilation_IsAdditive_OneTrueFlagWins()
    {
        var members = new[]
        {
            Song(releaseId: "rel", isCompilation: false),
            Song(releaseId: "rel", isCompilation: true),
            Song(releaseId: "rel", isCompilation: false),
        };

        Assert.True(reconciler.Reconcile(members).IsCompilation);
    }

    [Fact]
    public void Reconcile_TotalDiscs_TakesMax()
    {
        var members = new[]
        {
            Song(releaseId: "rel", totalDiscs: 1),
            Song(releaseId: "rel", totalDiscs: 2),
            Song(releaseId: "rel", totalDiscs: null),
        };

        Assert.Equal(2, reconciler.Reconcile(members).TotalDiscs);
    }

    [Fact]
    public void Reconcile_Year_TieBreaksToEarliest()
    {
        // No release id, so all members vote on year; 1-1 tie resolves to the earliest pressing.
        var members = new[]
        {
            Song(releaseId: null, year: 2009),
            Song(releaseId: null, year: 1969),
        };

        Assert.Equal(1969, reconciler.Reconcile(members).Year);
    }

    [Fact]
    public void Reconcile_ReleaseGroupAndAlbumArtistMbid_TravelWithWinner()
    {
        var members = new[]
        {
            Song(releaseId: "rel-keep", releaseGroupId: "rg-keep", albumArtist: "Joey", albumArtistMbid: "aa-keep"),
            Song(releaseId: "rel-keep", releaseGroupId: "rg-keep", albumArtist: "Joey", albumArtistMbid: "aa-keep"),
            Song(releaseId: "rel-stray", releaseGroupId: "rg-stray", albumArtist: "Joey", albumArtistMbid: "aa-stray"),
        };

        var identity = reconciler.Reconcile(members);

        Assert.Equal("rg-keep", identity.MusicBrainzReleaseGroupId);
        Assert.Equal("aa-keep", identity.AlbumArtistMusicBrainzId);
    }

    [Fact]
    public void Reconcile_SingleMember_ReturnsItsOwnIdentityUnchanged()
    {
        var song = Song(releaseId: "rel-solo", album: "Solo", year: 2020);

        var identity = reconciler.Reconcile([song]);

        Assert.Equal(AlbumIdentity.FromSong(song), identity);
    }

    private static SongMetadata Song(
        string? releaseId,
        string album = "Album",
        string albumArtist = "Artist",
        int? year = 2020,
        int track = 1,
        bool isCompilation = false,
        int? totalDiscs = null,
        string? releaseGroupId = null,
        string? albumArtistMbid = null) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = $"/source/{Guid.NewGuid():N}.mp3",
        FileName = "song.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Title = "Title",
        Album = album,
        Artist = albumArtist,
        AlbumArtist = albumArtist,
        Year = year,
        TrackNumber = track,
        IsCompilation = isCompilation,
        TotalDiscs = totalDiscs,
        MusicBrainzReleaseId = releaseId,
        MusicBrainzReleaseGroupId = releaseGroupId,
        AlbumArtistMusicBrainzId = albumArtistMbid,
        EnrichmentStatus = EnrichmentStatus.Matched,
    };
}
