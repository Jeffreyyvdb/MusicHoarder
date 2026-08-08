using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Download;

public class AlbumCompletionSweepTests
{
    private const string Artist = "Daft Punk";
    private const string Album = "Discovery";

    // ── The happy path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_OwningOneTrack_QueuesTheRest()
    {
        await using var db = NewContext();
        var canonical = AddCanonicalAlbum(db, Artist, Album, "One", "Two", "Three");
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        await db.SaveChangesAsync();

        var queued = await CreateSweep(db).SweepAsync(CancellationToken.None);

        Assert.Equal(2, queued);
        var items = await db.WishlistItems.IgnoreQueryFilters().OrderBy(w => w.Title).ToListAsync();
        Assert.Equal(["Three", "Two"], items.Select(i => i.Title));
        Assert.All(items, i =>
        {
            Assert.Equal(WishlistItemOrigin.AlbumCompletion, i.Origin);
            Assert.Equal(canonical.Id, i.CanonicalAlbumId);
            Assert.Equal(WishlistItemStatus.Pending, i.Status);
            Assert.Equal(Artist, i.Artist);
            Assert.Equal(Album, i.Album);
            // "When the owner saved it on Spotify" — and they never did. Left null on purpose; the
            // Origin discriminator is what orders the download queue.
            Assert.Null(i.SpotifyAddedAtUtc);
            Assert.Null(i.WishlistSourceId);
        });

        var marker = await db.AlbumCompletionStates.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AlbumCompletionStatus.Filled, marker.Status);
        Assert.Equal(2, marker.EnqueuedTrackCount);
        Assert.NotNull(marker.NextSweepAfterUtc);
    }

    [Fact]
    public async Task Sweep_OwningEveryTrack_QueuesNothingAndMarksNothingMissing()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, Artist, Album, "One", "Two");
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        db.Songs.Add(Song("/b.mp3", Artist, Album, title: "Two", track: 2));
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));

        var marker = await db.AlbumCompletionStates.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AlbumCompletionStatus.NothingMissing, marker.Status);
    }

    [Fact]
    public async Task Sweep_UnmatchedFileInTheFolder_StillCountsAsOwned()
    {
        // The trigger needs one Matched song, but every live file in the group feeds the owned-track
        // matcher — an unmatched file is still on disk and must not be re-downloaded.
        await using var db = NewContext();
        AddCanonicalAlbum(db, Artist, Album, "One", "Two");
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        var pending = Song("/b.mp3", Artist, Album, title: "Two", track: 2);
        pending.EnrichmentStatus = EnrichmentStatus.Pending;
        db.Songs.Add(pending);
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));
    }

    // ── Gates ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_FeatureDisabled_DoesNothing()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, Artist, Album, "One", "Two");
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db, runtimeEnabled: false).SweepAsync(CancellationToken.None));
        Assert.Empty(await db.WishlistItems.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Sweep_WishlistDownloadsDisabled_DoesNothing()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, Artist, Album, "One", "Two");
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        await db.SaveChangesAsync();

        var sweep = CreateSweep(db, configure: o => o.EnableWishlistDownloads = false);
        Assert.Equal(0, await sweep.SweepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_CanonicalAlbumNotFetched_IsSkipped()
    {
        await using var db = NewContext();
        var canonical = AddCanonicalAlbum(db, Artist, Album, "One", "Two");
        canonical.Status = CanonicalAlbumStatus.Pending;
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));
        Assert.Empty(await db.AlbumCompletionStates.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Sweep_DemoTenant_IsExcluded()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, Artist, Album, "One", "Two");
        var demoSong = Song("/a.mp3", Artist, Album, title: "One", track: 1);
        demoSong.OwnerUserId = TestUsers.DemoId;
        db.Songs.Add(demoSong);
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_MinOwnedTracksNotMet_IsSkipped()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, Artist, Album, "One", "Two", "Three");
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        await db.SaveChangesAsync();

        var sweep = CreateSweep(db, configure: o => o.AlbumCompletionMinCanonicalTracks = 99);
        Assert.Equal(0, await sweep.SweepAsync(CancellationToken.None));

        var marker = await db.AlbumCompletionStates.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AlbumCompletionStatus.Skipped, marker.Status);
        Assert.Equal(AlbumCompletionEligibility.ReasonTooFewCanonicalTracks, marker.SkipReason);
    }

    // ── Compilation / Various Artists ──────────────────────────────────────────

    [Fact]
    public async Task Sweep_VariousArtistsAlbumArtist_IsSkipped()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, "Various Artists", "Now 42", "One", "Two", "Three");
        var song = Song("/a.mp3", "Various Artists", "Now 42", title: "One", track: 1);
        song.IsCompilation = true;
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));
        await AssertSkipped(db, AlbumCompletionEligibility.ReasonVariousArtists);
    }

    [Fact]
    public async Task Sweep_CompilationWithNoAlbumArtist_IsSkipped()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, "Some Artist", "Soundtrack", "One", "Two", "Three");
        var song = Song("/a.mp3", null, "Soundtrack", title: "One", track: 1, artist: "Some Artist");
        song.IsCompilation = true;
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));
        await AssertSkipped(db, AlbumCompletionEligibility.ReasonVariousArtists);
    }

    [Fact]
    public async Task Sweep_CanonicalDisplayArtistIsVaSentinel_IsSkipped()
    {
        // Local tags say a real artist; the reconciled providers say VA. Trust the providers.
        await using var db = NewContext();
        AddCanonicalAlbumAs(db, Artist, "Mixtape", "VA", "One", "Two", "Three");
        db.Songs.Add(Song("/a.mp3", Artist, "Mixtape", title: "One", track: 1));
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));
        await AssertSkipped(db, AlbumCompletionEligibility.ReasonVariousArtists);
    }

    [Fact]
    public async Task Sweep_CompilationReleaseType_IsSkipped()
    {
        // A single-artist greatest-hits with a clean album artist: only the release-group type gives it away.
        await using var db = NewContext();
        AddCanonicalAlbum(db, Artist, "Greatest Hits", "One", "Two", "Three");
        var song = Song("/a.mp3", Artist, "Greatest Hits", title: "One", track: 1);
        song.ReleaseTypes = "album;compilation";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));
        await AssertSkipped(db, AlbumCompletionEligibility.ReasonCompilationReleaseType);
    }

    [Fact]
    public async Task Sweep_ShatteredCompilation_IsSkippedOnArtistMismatch()
    {
        // The nastiest case: a compilation ingested with no album artist groups by *track* artist, so it
        // looks like a plausible single-artist album with one owned track. Without the artist-mismatch
        // guard the whole compilation would be queued once per contributor, each track searched under
        // the wrong name.
        await using var db = NewContext();
        AddCanonicalAlbumAs(db, "Contributor One", "Big Sampler", "Various Artists", "One", "Two", "Three");
        db.Songs.Add(Song("/a.mp3", null, "Big Sampler", title: "One", track: 1, artist: "Contributor One"));
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));
        // The VA sentinel on the canonical row catches this one first, which is the same outcome by a
        // stronger signal; the mismatch guard is what covers a non-sentinel wrong artist.
        var marker = await db.AlbumCompletionStates.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AlbumCompletionStatus.Skipped, marker.Status);
    }

    [Fact]
    public async Task Sweep_DifferentArtistSameAlbumTitle_IsSkippedOnArtistMismatch()
    {
        // The keys match (that is how the join found each other), but the reconciled album turns out to
        // describe a different act entirely — a title collision, not this owner's record.
        await using var db = NewContext();
        AddCanonicalAlbumAs(db, "Wrong Band", "Crossroads", "Completely Different Ensemble",
            "One", "Two", "Three");
        db.Songs.Add(Song("/a.mp3", "Wrong Band", "Crossroads", title: "One", track: 1));
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));
        await AssertSkipped(db, AlbumCompletionEligibility.ReasonArtistMismatch);
    }

    // ── Throttles ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_RespectsAlbumsPerSweep()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, Artist, "First", "One", "Two");
        AddCanonicalAlbum(db, Artist, "Second", "One", "Two");
        db.Songs.Add(Song("/a.mp3", Artist, "First", title: "One", track: 1));
        db.Songs.Add(Song("/b.mp3", Artist, "Second", title: "One", track: 1));
        await db.SaveChangesAsync();

        var sweep = CreateSweep(db, configure: o => o.AlbumCompletionAlbumsPerSweep = 1);
        Assert.Equal(1, await sweep.SweepAsync(CancellationToken.None));
        Assert.Single(await db.AlbumCompletionStates.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Sweep_RespectsMaxTracksPerAlbum()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, Artist, Album, "One", "Two", "Three", "Four", "Five");
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        await db.SaveChangesAsync();

        var sweep = CreateSweep(db, configure: o => o.AlbumCompletionMaxTracksPerAlbum = 2);
        Assert.Equal(2, await sweep.SweepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_AtPendingCeiling_ShortCircuits()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, Artist, Album, "One", "Two", "Three");
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            Origin = WishlistItemOrigin.AlbumCompletion,
            Status = WishlistItemStatus.Pending,
            Title = "Unrelated",
            Artist = "Someone",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var sweep = CreateSweep(db, configure: o => o.AlbumCompletionMaxPendingItems = 1);
        Assert.Equal(0, await sweep.SweepAsync(CancellationToken.None));
        Assert.Empty(await db.AlbumCompletionStates.IgnoreQueryFilters().ToListAsync());
    }

    // ── Idempotence, tombstones and revisits ───────────────────────────────────

    [Fact]
    public async Task Sweep_RunTwice_DoesNotRequeue()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, Artist, Album, "One", "Two", "Three");
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        await db.SaveChangesAsync();

        Assert.Equal(2, await CreateSweep(db).SweepAsync(CancellationToken.None));
        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));
        Assert.Equal(2, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
    }

    [Theory]
    [InlineData(WishlistItemStatus.Failed)]
    [InlineData(WishlistItemStatus.NotFound)]
    [InlineData(WishlistItemStatus.Downloaded)]
    [InlineData(WishlistItemStatus.SkippedOwned)]
    public async Task Sweep_TerminalItemForTrack_IsNotRequeued(WishlistItemStatus status)
    {
        // Failed/NotFound are terminal — nothing resets them — so they double as permanent tombstones.
        await using var db = NewContext();
        var canonical = AddCanonicalAlbum(db, Artist, Album, "One", "Two");
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        await db.SaveChangesAsync();

        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            Origin = WishlistItemOrigin.AlbumCompletion,
            CanonicalAlbumId = canonical.Id,
            Status = status,
            Title = "Two",
            Artist = Artist,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateSweep(db).SweepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_SkippedAlbum_GetsNoRevisitTimer()
    {
        await using var db = NewContext();
        AddCanonicalAlbum(db, "Various Artists", "Now 42", "One", "Two", "Three");
        var song = Song("/a.mp3", "Various Artists", "Now 42", title: "One", track: 1);
        song.IsCompilation = true;
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        await CreateSweep(db).SweepAsync(CancellationToken.None);

        var marker = await db.AlbumCompletionStates.IgnoreQueryFilters().SingleAsync();
        Assert.Null(marker.NextSweepAfterUtc);
    }

    [Fact]
    public async Task Sweep_CanonicalRefetchedSinceLastSweep_LooksAgain()
    {
        await using var db = NewContext();
        var canonical = AddCanonicalAlbum(db, Artist, Album, "One", "Two");
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        await db.SaveChangesAsync();

        Assert.Equal(1, await CreateSweep(db).SweepAsync(CancellationToken.None));

        // A later edition turns up on re-fetch. The revisit timer hasn't come due, but a fresh fetch
        // must override it.
        canonical.Tracks.Add(new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 3, Title = "Three" });
        canonical.FetchedAtUtc = DateTime.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();

        Assert.Equal(1, await CreateSweep(db).SweepAsync(CancellationToken.None));
        Assert.Equal(2, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
    }

    // ── Track-level filters ────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_ContestedTrack_IsSkippedByDefault()
    {
        await using var db = NewContext();
        var canonical = AddCanonicalAlbum(db, Artist, Album, "One", "Two");
        canonical.Tracks.Add(new CanonicalAlbumTrack
        {
            DiscNumber = 1, TrackNumber = 3, Title = "Bonus Phantom", IsContested = true,
        });
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        await db.SaveChangesAsync();

        Assert.Equal(1, await CreateSweep(db).SweepAsync(CancellationToken.None));
        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("Two", item.Title);
    }

    [Fact]
    public async Task Sweep_BlankTitledTrack_IsSkipped()
    {
        await using var db = NewContext();
        var canonical = AddCanonicalAlbum(db, Artist, Album, "One", "Two");
        canonical.Tracks.Add(new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 3, Title = "  " });
        db.Songs.Add(Song("/a.mp3", Artist, Album, title: "One", track: 1));
        await db.SaveChangesAsync();

        Assert.Equal(1, await CreateSweep(db).SweepAsync(CancellationToken.None));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task AssertSkipped(MusicHoarderDbContext db, string reason)
    {
        var marker = await db.AlbumCompletionStates.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AlbumCompletionStatus.Skipped, marker.Status);
        Assert.Equal(reason, marker.SkipReason);
        Assert.Empty(await db.WishlistItems.IgnoreQueryFilters().ToListAsync());
    }

    private static CanonicalAlbum AddCanonicalAlbum(
        MusicHoarderDbContext db, string artist, string album, params string[] trackTitles)
        => AddCanonicalAlbumAs(db, artist, album, displayArtist: null, trackTitles);

    /// <summary>Same, but with a reconciled display artist that differs from the local tags.</summary>
    private static CanonicalAlbum AddCanonicalAlbumAs(
        MusicHoarderDbContext db, string artist, string album, string? displayArtist, params string[] trackTitles)
    {
        var row = new CanonicalAlbum
        {
            ArtistKey = Api.Matching.TitleNormalizer.NormalizeForSearch(artist),
            AlbumKey = Api.Matching.TitleNormalizer.NormalizeForSearch(album),
            DisplayArtist = displayArtist ?? artist,
            DisplayTitle = album,
            Status = CanonicalAlbumStatus.Fetched,
            FetchedAtUtc = DateTime.UtcNow,
            ResolvedTrackCount = trackTitles.Length,
        };
        for (var i = 0; i < trackTitles.Length; i++)
            row.Tracks.Add(new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = i + 1, Title = trackTitles[i] });

        db.CanonicalAlbums.Add(row);
        return row;
    }

    private static SongMetadata Song(
        string sourcePath, string? albumArtist, string album, string title, int track, string? artist = null) => new()
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            SourcePath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            Extension = Path.GetExtension(sourcePath),
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            EnrichmentStatus = EnrichmentStatus.Matched,
            AlbumArtist = albumArtist,
            Artist = artist ?? albumArtist,
            Album = album,
            Title = title,
            DiscNumber = 1,
            TrackNumber = track,
        };

    private static AlbumCompletionSweep CreateSweep(
        MusicHoarderDbContext db,
        bool runtimeEnabled = true,
        Action<MusicEnricherOptions>? configure = null)
    {
        var opts = new MusicEnricherOptions
        {
            SourceDirectory = "/src",
            DestinationDirectory = "/dest",
            DownloadDirectory = "/downloads",
            EnableWishlistDownloads = true,
            EnableAlbumCompletion = true,
        };
        configure?.Invoke(opts);

        return new AlbumCompletionSweep(
            db,
            new TestOwnerLookupService(),
            new StubRuntimeSettings(runtimeEnabled && opts.EnableAlbumCompletion),
            Microsoft.Extensions.Options.Options.Create(opts),
            NullLogger<AlbumCompletionSweep>.Instance);
    }

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class StubRuntimeSettings(bool albumCompletionEnabled) : IRuntimeSettingsService
    {
        private readonly EffectiveSettings _effective = new(
            EnableAcoustIdProvider: true,
            EnableMusicBrainzWebProvider: true,
            EnableSpotifyApiProvider: true,
            EnableTrackerProvider: true,
            EnableDeezerProvider: true,
            EnableAppleMusicProvider: true,
            QualityGradingEnabled: true,
            AutoDownloadWishlist: true,
            AlbumCompletionEnabled: albumCompletionEnabled,
            UpdatedAtUtc: null);

        public Task<EffectiveSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(_effective);

        public Task<EffectiveSettings> UpdateAsync(RuntimeSettingsUpdate update, CancellationToken ct = default) =>
            Task.FromResult(_effective);
    }
}
