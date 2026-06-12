using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class ArtistCreditHealerTests
{
    private static readonly DateTime EnrichedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HealAsync_BackfillsArtistsAndAlignedIdsFromMusicBrainzAttempt()
    {
        await using var db = NewContext();
        var song = Song("/a.flac", musicBrainzId: "rec-1");
        AddAttempt(song, EnrichmentProvider.MusicBrainzWeb,
            Candidate(mbid: "rec-1", artists: "Alice; Bob", artistMbids: "mbid-a; mbid-b"));
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(1, result.SongsHealed);
        Assert.Equal(1, result.SongsRequeued);

        var healed = await db.Songs.SingleAsync();
        Assert.Equal("Alice; Bob", healed.Artists);
        Assert.Equal("mbid-a; mbid-b", healed.ArtistMusicBrainzIds);

        // Re-queued with the force-rebuild signal (FLAC re-tags are size-identical and would
        // otherwise be skipped), and no grade-staleness: EnrichedAtUtc untouched.
        Assert.Equal(LibraryBuildStatus.Pending, healed.LibraryBuildStatus);
        Assert.Equal(healed.DestinationPath, healed.PreviousDestinationPath);
        Assert.Equal(EnrichedAt, healed.EnrichedAtUtc);

        var changes = await db.SongMetadataChanges.ToListAsync();
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal("artist-credit-heal", c.Source));
    }

    [Fact]
    public async Task HealAsync_AttemptForDifferentRecording_DoesNotDonate()
    {
        // A stale MB attempt that matched a different recording than the song ended up with must
        // never donate its credit.
        await using var db = NewContext();
        var song = Song("/a.flac", musicBrainzId: "rec-current");
        AddAttempt(song, EnrichmentProvider.MusicBrainzWeb,
            Candidate(mbid: "rec-other", artists: "Wrong; Credit"));
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(0, result.SongsHealed);
        Assert.Null((await db.Songs.SingleAsync()).Artists);
    }

    [Fact]
    public async Task HealAsync_FallsBackToSpotifyAttempt()
    {
        await using var db = NewContext();
        var song = Song("/a.flac", spotifyId: "spot-1");
        AddAttempt(song, EnrichmentProvider.SpotifyAPI,
            Candidate(spotifyId: "spot-1", artists: "Alice; Bob"));
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(1, result.SongsHealed);
        var healed = await db.Songs.SingleAsync();
        Assert.Equal("Alice; Bob", healed.Artists);
        Assert.Null(healed.ArtistMusicBrainzIds);
    }

    [Fact]
    public async Task HealAsync_MisalignedIds_BackfillsNamesOnly()
    {
        await using var db = NewContext();
        var song = Song("/a.flac", musicBrainzId: "rec-1");
        AddAttempt(song, EnrichmentProvider.MusicBrainzWeb,
            Candidate(mbid: "rec-1", artists: "Alice; Bob", artistMbids: "mbid-a"));
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        await Healer(db).HealAsync();

        var healed = await db.Songs.SingleAsync();
        Assert.Equal("Alice; Bob", healed.Artists);
        Assert.Null(healed.ArtistMusicBrainzIds);
    }

    [Fact]
    public async Task HealAsync_SongWithArtistsAlready_NotTouched()
    {
        await using var db = NewContext();
        var song = Song("/a.flac", musicBrainzId: "rec-1", artists: "Already; Set");
        AddAttempt(song, EnrichmentProvider.MusicBrainzWeb,
            Candidate(mbid: "rec-1", artists: "Alice; Bob"));
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(0, result.SongsHealed);
        Assert.Equal("Already; Set", (await db.Songs.SingleAsync()).Artists);
    }

    [Fact]
    public async Task HealAsync_DemoTenant_Excluded()
    {
        // Demo rows are seeded terminal with DestinationPath == SourcePath; a re-queue would point
        // the builder's delete at the read-only source mount. They must never be eligible.
        await using var db = NewContext();
        var song = Song("/a.flac", musicBrainzId: "rec-1");
        song.OwnerUserId = WellKnownUsers.DemoId;
        AddAttempt(song, EnrichmentProvider.MusicBrainzWeb,
            Candidate(mbid: "rec-1", artists: "Alice; Bob"));
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(0, result.SongsHealed);
        Assert.Null((await db.Songs.IgnoreQueryFilters().SingleAsync()).Artists);
    }

    [Fact]
    public async Task HealAsync_NotYetBuilt_HealedButNotRequeued()
    {
        await using var db = NewContext();
        var song = Song("/a.flac", musicBrainzId: "rec-1",
            buildStatus: LibraryBuildStatus.Pending, destinationPath: null);
        AddAttempt(song, EnrichmentProvider.MusicBrainzWeb,
            Candidate(mbid: "rec-1", artists: "Alice; Bob"));
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(1, result.SongsHealed);
        Assert.Equal(0, result.SongsRequeued);
        var healed = await db.Songs.SingleAsync();
        Assert.Equal("Alice; Bob", healed.Artists);
        Assert.Null(healed.PreviousDestinationPath);
    }

    [Fact]
    public async Task HealAsync_SecondPass_NoOp()
    {
        await using var db = NewContext();
        var song = Song("/a.flac", musicBrainzId: "rec-1");
        AddAttempt(song, EnrichmentProvider.MusicBrainzWeb,
            Candidate(mbid: "rec-1", artists: "Alice; Bob"));
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        Assert.Equal(1, (await Healer(db).HealAsync()).SongsHealed);
        Assert.Equal(0, (await Healer(db).HealAsync()).SongsHealed);
    }

    [Fact]
    public async Task DetectAsync_ReportsGapWithoutMutating()
    {
        await using var db = NewContext();
        var song = Song("/a.flac", musicBrainzId: "rec-1");
        AddAttempt(song, EnrichmentProvider.MusicBrainzWeb,
            Candidate(mbid: "rec-1", artists: "Alice; Bob", artistMbids: "mbid-a; mbid-b"));
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var gaps = await Healer(db).DetectAsync();

        var gap = Assert.Single(gaps);
        Assert.Equal(EnrichmentProvider.MusicBrainzWeb, gap.DonorProvider);
        Assert.Equal("Alice; Bob", gap.Artists);
        Assert.Equal("mbid-a; mbid-b", gap.ArtistMusicBrainzIds);

        var untouched = await db.Songs.SingleAsync();
        Assert.Null(untouched.Artists);
        Assert.Equal(LibraryBuildStatus.Done, untouched.LibraryBuildStatus);
    }

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static IArtistCreditHealer Healer(MusicHoarderDbContext db)
        => new ArtistCreditHealer(db, NullLogger<ArtistCreditHealer>.Instance);

    private static SongMetadata Song(
        string sourcePath,
        string? musicBrainzId = null,
        string? spotifyId = null,
        string? artists = null,
        LibraryBuildStatus buildStatus = LibraryBuildStatus.Done,
        string? destinationPath = "unset") => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        EnrichedAtUtc = EnrichedAt,
        OriginalMetadataCaptured = true,
        Artist = "Alice & Bob",
        AlbumArtist = "Alice",
        Album = "Duets",
        Title = "Duet",
        TrackNumber = 1,
        Year = 2020,
        MusicBrainzId = musicBrainzId,
        SpotifyId = spotifyId,
        Artists = artists,
        LibraryBuildStatus = buildStatus,
        DestinationPath = destinationPath == "unset" ? "/dest/Alice/2020 - Duets/01 - Duet.flac" : destinationPath,
    };

    private static EnrichmentProviderResult Candidate(
        string? mbid = null, string? spotifyId = null, string? artists = null, string? artistMbids = null)
        => new("Alice & Bob", "Alice", "Duet", 2020, 1, mbid, null, spotifyId, null, null,
            "test", 0.9, [], EnrichmentStatus.Matched,
            Artists: artists, ArtistMusicBrainzIds: artistMbids);

    private static void AddAttempt(SongMetadata song, EnrichmentProvider provider, EnrichmentProviderResult candidate)
        => song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = provider,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
            MatchedDataJson = JsonSerializer.Serialize(candidate),
        });
}
