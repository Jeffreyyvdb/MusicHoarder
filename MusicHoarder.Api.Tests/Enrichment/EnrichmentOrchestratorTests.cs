using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Tests.Enrichment;

public class EnrichmentOrchestratorTests
{
    [Fact]
    public async Task ProcessSong_AcoustIdSolo_CleanUnambiguousMatch_SetsMatched()
    {
        // A clean, unambiguous fingerprint match is acoustically authoritative: once it's the only
        // enabled provider (and has had its turn) it may stand alone rather than rotting in review.
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-123", "acoust-123", "Lucid Dreams", "Juice WRLD", "Juice WRLD", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var outcome = await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.Include(s => s.ProviderAttempts).SingleAsync();

        Assert.Equal(EnrichmentOutcome.Matched, outcome);
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("AcoustID", updated.MatchedBy);
        Assert.Equal("mb-123", updated.MusicBrainzId);
        var attempt = Assert.Single(updated.ProviderAttempts);
        Assert.Equal(ProviderAttemptStatus.Matched, attempt.Status);
        Assert.NotNull(attempt.MatchedDataJson);
    }

    [Fact]
    public async Task ProcessSong_AcoustIdSolo_AmbiguousFingerprint_StaysNeedsReview()
    {
        // multiple_candidates means the fingerprint resolved to several recordings — never stand alone.
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams");
        await db.SaveChangesAsync();

        var acoustId = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Juice WRLD", "Juice WRLD", "Lucid Dreams", null, null,
                "mb-123", null, null, "acoust-123", null, "AcoustID", 0.95,
                ["multiple_candidates"], EnrichmentStatus.Matched))));
        var orchestrator = CreateOrchestratorWithProviders(db, [acoustId]);

        var outcome = await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(EnrichmentOutcome.NeedsReview, outcome);
        Assert.Equal(EnrichmentStatus.NeedsReview, updated.EnrichmentStatus);
        Assert.Null(updated.MusicBrainzId);
    }

    [Fact]
    public async Task ProcessSong_AcoustIdAndSpotifyAgree_SetsMatchedByConsensus()
    {
        // Two independent providers landing on the same identity → Matched, even though
        // neither AcoustID alone nor a borderline Spotify confidence would qualify solo.
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams");
        await db.SaveChangesAsync();

        var acoustId = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Juice WRLD", "Juice WRLD", "Lucid Dreams", null, null,
                "mb-123", null, null, "acoust-123", null, "AcoustID", 0.80, [], EnrichmentStatus.Matched))));
        var spotify = new StubEnrichmentProvider("SpotifyAPI", 200,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Juice WRLD", "Juice WRLD", "Lucid Dreams", 2018, null,
                null, null, "spotify-xyz", null, "USUM71807062", "SpotifyAPI", 0.74, [], EnrichmentStatus.NeedsReview))));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [acoustId, spotify], opts);

        var outcome = await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.Include(s => s.ProviderAttempts).SingleAsync();

        Assert.Equal(EnrichmentOutcome.Matched, outcome);
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal(2, updated.ProviderAttempts.Count);
        // Winner prefers the name-based provider's richer metadata.
        Assert.Equal("spotify-xyz", updated.SpotifyId);
        Assert.NotNull(updated.MatchConfidence);
    }

    [Fact]
    public async Task ProcessSong_NoMatch_SetsNeedsReviewOrFailed()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Unknown", title: "Unknown Track");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(null));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var outcome = await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.Include(s => s.ProviderAttempts).SingleAsync();

        Assert.True(outcome is EnrichmentOutcome.NeedsReview or EnrichmentOutcome.Failed);
        Assert.True(updated.EnrichmentStatus is EnrichmentStatus.NeedsReview or EnrichmentStatus.Failed);
        Assert.Contains(updated.ProviderAttempts, a =>
            a.Provider == EnrichmentProvider.AcoustID && a.Status == ProviderAttemptStatus.NoMatch);
    }

    [Fact]
    public async Task ProcessSong_ArtistMismatchPenalty_SetsNeedsReview_WithoutOverwritingRow()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-456", "acoust-456", "Lucid Dreams", "Stevie Wonder", "Stevie Wonder", 0.90f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var outcome = await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.Include(s => s.ProviderAttempts).SingleAsync();

        Assert.Equal(EnrichmentOutcome.NeedsReview, outcome);
        Assert.NotNull(updated.MatchWarnings);
        Assert.Contains("artist_mismatch", updated.MatchWarnings);

        // Row's user-visible metadata must NOT be overwritten by a NeedsReview-recommended hit.
        // The wrong candidate ("Stevie Wonder") is preserved on the provider attempt for review.
        Assert.Equal("Juice WRLD", updated.Artist);
        Assert.Equal("Lucid Dreams", updated.Title);
        Assert.Null(updated.MusicBrainzId);
        Assert.Null(updated.AcoustIdTrackId);

        var attempt = Assert.Single(updated.ProviderAttempts);
        Assert.Equal(EnrichmentProvider.AcoustID, attempt.Provider);
        Assert.Equal(ProviderAttemptStatus.Matched, attempt.Status);
        Assert.NotNull(attempt.MatchedDataJson);
        var candidate = JsonSerializer.Deserialize<EnrichmentProviderResult>(attempt.MatchedDataJson!);
        Assert.NotNull(candidate);
        Assert.Equal("Stevie Wonder", candidate!.Artist);
        Assert.Equal("mb-456", candidate.MusicBrainzId);
    }

    [Fact]
    public async Task ProcessSong_AcoustIdThrows_RecordsFailedAttempt()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db);
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ =>
            throw new HttpRequestException("connection refused"));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var outcome = await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.Include(s => s.ProviderAttempts).SingleAsync();

        Assert.True(outcome is EnrichmentOutcome.Failed or EnrichmentOutcome.NeedsReview);
        Assert.Contains(updated.ProviderAttempts, a =>
            a.Provider == EnrichmentProvider.AcoustID && a.Status == ProviderAttemptStatus.Failed);
    }

    [Fact]
    public async Task ProcessSong_ManuallyApprovedSong_IsSkipped()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.LockManualApproval();
        await db.SaveChangesAsync();

        var called = false;
        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => { called = true; return Task.FromResult<ProviderOutcome>(new ProviderNoMatch()); });

        var orchestrator = CreateOrchestratorWithProviders(db, [provider]);
        var outcome = await orchestrator.ProcessSongAsync(song.Id);

        Assert.Equal(EnrichmentOutcome.Skipped, outcome);
        Assert.False(called);
    }

    [Fact]
    public async Task ProcessSong_CooldownExpiredNoMatch_RetriesProvider()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        song.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow.AddDays(-31),
            NextRetryAfterUtc = DateTime.UtcNow.AddMinutes(-1), // cooldown elapsed
        });
        await db.SaveChangesAsync();

        var called = false;
        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => { called = true; return Task.FromResult<ProviderOutcome>(new ProviderNoMatch()); });

        var orchestrator = CreateOrchestratorWithProviders(db, [provider]);
        await orchestrator.ProcessSongAsync(song.Id);

        Assert.True(called);
    }

    [Fact]
    public async Task ProcessSong_CooldownNotElapsed_SkipsProvider()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        song.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow.AddDays(-1),
            NextRetryAfterUtc = DateTime.UtcNow.AddDays(29), // still cooling down
        });
        await db.SaveChangesAsync();

        var called = false;
        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => { called = true; return Task.FromResult<ProviderOutcome>(new ProviderNoMatch()); });

        var orchestrator = CreateOrchestratorWithProviders(db, [provider]);
        await orchestrator.ProcessSongAsync(song.Id);

        Assert.False(called);
    }

    [Fact]
    public async Task ProcessSong_NoMatch_StampsCooldown()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderNoMatch()));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider]);
        await orchestrator.ProcessSongAsync(song.Id);

        var attempt = await db.SongProviderAttempts.SingleAsync();
        Assert.Equal(ProviderAttemptStatus.NoMatch, attempt.Status);
        Assert.NotNull(attempt.NextRetryAfterUtc);
        Assert.True(attempt.NextRetryAfterUtc > DateTime.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task ProcessSong_DeletedSong_Skipped()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db);
        song.DeletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => throw new InvalidOperationException("should not be called"));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var outcome = await orchestrator.ProcessSongAsync(song.Id);

        Assert.Equal(EnrichmentOutcome.Skipped, outcome);
    }

    [Fact]
    public async Task ProcessSong_CapturesOriginalMetadata_BeforeOverwriting()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Original Artist", title: "Original Title");
        song.Album = "Original Album";
        song.Isrc = "USRC12345";
        await db.SaveChangesAsync();

        // Two providers agree at high confidence → strong consensus → a good curated value may be
        // upgraded. The row's Artist/Title are overwritten and the originals captured at that moment.
        var acoustId = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Original Artist Trio", "Original Artist Trio", "Original Title (Remastered)", null, null,
                "mb-789", null, null, "ac-789", null, "AcoustID", 0.97, [], EnrichmentStatus.Matched))));
        var spotify = new StubEnrichmentProvider("SpotifyAPI", 200,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Original Artist Trio", "Original Artist Trio", "Original Title (Remastered)", null, null,
                null, null, "spotify-789", null, null, "SpotifyAPI", 0.97, [], EnrichmentStatus.Matched))));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [acoustId, spotify], opts);

        await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.True(updated.OriginalMetadataCaptured);
        Assert.Equal("Original Artist", updated.OriginalArtist);
        Assert.Equal("Original Title", updated.OriginalTitle);
        Assert.Equal("Original Album", updated.OriginalAlbum);
        Assert.Equal("USRC12345", updated.OriginalIsrc);
        Assert.NotNull(updated.OriginalMetadataCapturedAtUtc);

        Assert.Equal("Original Artist Trio", updated.Artist);
        Assert.Equal("Original Title (Remastered)", updated.Title);
    }

    [Fact]
    public async Task ProcessSong_OriginalMetadata_NotOverwrittenOnSecondEnrichment()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Already Changed", title: "Already Changed");
        song.OriginalMetadataCaptured = true;
        song.OriginalArtist = "Very Original";
        song.OriginalTitle = "Very Original Title";
        song.OriginalMetadataCapturedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-999", "acoust-999", "Third Title", "Third Artist", "Third Artist", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.SingleAsync();

        Assert.Equal("Very Original", updated.OriginalArtist);
        Assert.Equal("Very Original Title", updated.OriginalTitle);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), updated.OriginalMetadataCapturedAtUtc);
    }

    [Fact]
    public async Task ProcessSong_ProviderChain_RunsAllProviders_NoEarlyStop()
    {
        // AcoustID no longer short-circuits the chain — every provider runs so candidates can be
        // corroborated. Once they've all had a turn, a clean fingerprint hit that nothing
        // contradicts may stand alone (Matched).
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var called = new List<string>();
        var provider1 = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ =>
            {
                called.Add("AcoustID");
                return Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                    "Matched Artist", "Matched Artist", "Matched Title", null, null,
                    "mb-1", null, null, null, null, "AcoustID", 0.95, [], EnrichmentStatus.Matched)));
            });
        var provider2 = new StubEnrichmentProvider("SpotifyAPI", 200,
            canHandle: _ => true,
            enrich: _ =>
            {
                called.Add("SpotifyAPI");
                return Task.FromResult<ProviderOutcome>(new ProviderNoMatch());
            });

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [provider1, provider2], opts);
        await orchestrator.ProcessSongAsync(song.Id);

        Assert.Equal(2, called.Count);
        Assert.Contains("AcoustID", called);
        Assert.Contains("SpotifyAPI", called);

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
    }

    [Fact]
    public async Task ProcessSong_ProviderChain_FallsBackToSecondProvider()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider1 = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderNoMatch()));
        var provider2 = new StubEnrichmentProvider("SpotifyAPI", 200,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "P2 Artist", "P2 Artist", "P2 Title", null, null,
                null, null, "spotify-1", null, null, "SpotifyAPI", 0.90, [], EnrichmentStatus.Matched))));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [provider1, provider2], opts);
        await orchestrator.ProcessSongAsync(song.Id);

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("SpotifyAPI", updated.MatchedBy);
        Assert.Equal("spotify-1", updated.SpotifyId);
    }

    [Fact]
    public async Task ProcessSong_ProviderChain_SkipsProviderThatCannotHandle()
    {
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/source/no-fp.mp3",
            FileName = "no-fp.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Artist",
            Title = "Title",
            Fingerprint = null,
            DurationSeconds = null,
            EnrichmentStatus = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();
        var songId = (await db.Songs.SingleAsync()).Id;

        var acoustIdCalled = false;
        var provider1 = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: s => !string.IsNullOrWhiteSpace(s.Fingerprint),
            enrich: _ =>
            {
                acoustIdCalled = true;
                return Task.FromResult<ProviderOutcome>(new ProviderNoMatch());
            });
        var provider2 = new StubEnrichmentProvider("SpotifyAPI", 200,
            canHandle: s => !string.IsNullOrWhiteSpace(s.Artist) && !string.IsNullOrWhiteSpace(s.Title),
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                null, null, null, null, null, "SpotifyAPI", 0.90, [], EnrichmentStatus.Matched))));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [provider1, provider2], opts);
        await orchestrator.ProcessSongAsync(songId);

        Assert.False(acoustIdCalled);
        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("SpotifyAPI", updated.MatchedBy);
    }

    [Fact]
    public async Task ProcessSong_ProviderChain_ContinuesAfterProviderException()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider1 = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => throw new HttpRequestException("timeout"));
        var provider2 = new StubEnrichmentProvider("SpotifyAPI", 200,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                "mb-1", null, null, null, null, "SpotifyAPI", 0.92, [], EnrichmentStatus.Matched))));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [provider1, provider2], opts);
        var outcome = await orchestrator.ProcessSongAsync(song.Id);

        Assert.Equal(EnrichmentOutcome.Matched, outcome);
        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("SpotifyAPI", updated.MatchedBy);
    }

    [Fact]
    public async Task ProcessSong_ProviderResult_PersistsSpotifyId()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("SpotifyAPI", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", 2024, 5,
                null, null, "spotify-abc", null, "USRC99999", "SpotifyAPI", 0.95,
                [], EnrichmentStatus.Matched))));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [provider], opts);
        await orchestrator.ProcessSongAsync(song.Id);

        var updated = await db.Songs.SingleAsync();
        Assert.Equal("spotify-abc", updated.SpotifyId);
        Assert.Equal("USRC99999", updated.Isrc);
        Assert.Equal(2024, updated.Year);
        Assert.Equal(5, updated.TrackNumber);
        Assert.Equal("SpotifyAPI", updated.MatchedBy);
    }

    [Fact]
    public async Task ProcessSong_DisabledProvider_IsSkipped()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var acoustIdCalled = false;
        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ =>
            {
                acoustIdCalled = true;
                return Task.FromResult<ProviderOutcome>(new ProviderNoMatch());
            });

        var opts = CreateOptions();
        opts.Value.EnableAcoustIdProvider = false;
        var orchestrator = CreateOrchestratorWithProviders(db, [provider], opts);
        await orchestrator.ProcessSongAsync(song.Id);

        Assert.False(acoustIdCalled);
    }

    [Fact]
    public async Task ProcessSong_MatchedSong_LyricsFound_SetsFetchedLyricsState()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("SpotifyAPI", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                null, null, "spotify-lyrics", null, null, "SpotifyAPI", 0.95, [], EnrichmentStatus.Matched))));
        var lrcLib = new StubLrcLibService(_ =>
            Task.FromResult<LyricsResult?>(new LyricsResult("[00:00.00]Hello", "Hello", false)));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [provider], opts, lrcLibService: lrcLib);
        var outcome = await orchestrator.ProcessSongAsync(song.Id);

        Assert.Equal(EnrichmentOutcome.Matched, outcome);
        Assert.Equal(1, lrcLib.CallCount);

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal(LyricsStatus.Fetched, updated.LyricsStatus);
        Assert.Equal("[00:00.00]Hello", updated.SyncedLyrics);
        Assert.Equal("Hello", updated.PlainLyrics);
        Assert.False(updated.IsInstrumental);
    }

    [Fact]
    public async Task ProcessSong_MatchedSong_InstrumentalLyrics_SetsInstrumentalState()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("SpotifyAPI", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                null, null, "spotify-lyrics", null, null, "SpotifyAPI", 0.95, [], EnrichmentStatus.Matched))));
        var lrcLib = new StubLrcLibService(_ =>
            Task.FromResult<LyricsResult?>(new LyricsResult("ignored", "ignored", true)));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [provider], opts, lrcLibService: lrcLib);
        await orchestrator.ProcessSongAsync(song.Id);

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(LyricsStatus.Instrumental, updated.LyricsStatus);
        Assert.True(updated.IsInstrumental);
        Assert.Null(updated.SyncedLyrics);
        Assert.Null(updated.PlainLyrics);
        Assert.Equal(1, lrcLib.CallCount);
    }

    [Fact]
    public async Task ProcessSong_MatchedSong_LyricsFetchThrows_MarksLyricsFailedButKeepsMatch()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("SpotifyAPI", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                null, null, "spotify-lyrics", null, null, "SpotifyAPI", 0.95, [], EnrichmentStatus.Matched))));
        var lrcLib = new StubLrcLibService(_ => throw new HttpRequestException("lrc failed"));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [provider], opts, lrcLibService: lrcLib);
        var outcome = await orchestrator.ProcessSongAsync(song.Id);

        Assert.Equal(EnrichmentOutcome.Matched, outcome);
        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal(LyricsStatus.Failed, updated.LyricsStatus);
        Assert.Equal(1, lrcLib.CallCount);
    }

    [Fact]
    public async Task ProcessSong_MatchPreservesExistingArtist_WhenMatchArtistIsBlank()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Existing Artist", title: "Existing Title");
        await db.SaveChangesAsync();

        // Winners have blank Artist → merge must preserve the existing Artist while still
        // upgrading the Title under strong consensus.
        var acoustId = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "", "", "Existing Title (Remastered)", null, null,
                "mb-blank", null, null, null, null, "AcoustID", 0.97, [], EnrichmentStatus.Matched))));
        var spotify = new StubEnrichmentProvider("SpotifyAPI", 200,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "", "", "Existing Title (Remastered)", null, null,
                null, null, "spotify-blank", null, null, "SpotifyAPI", 0.97, [], EnrichmentStatus.Matched))));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [acoustId, spotify], opts);

        await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("Existing Artist", updated.Artist);
        Assert.Equal("Existing Title (Remastered)", updated.Title);
    }

    [Fact]
    public async Task ProcessSong_StampsEnrichmentLastAttemptedAtUtc()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db);
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-ts", "acoust-ts", "Title", "Artist", "Artist", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.SingleAsync();

        Assert.NotNull(updated.EnrichmentLastAttemptedAtUtc);
        Assert.True(updated.EnrichmentLastAttemptedAtUtc >= before);
    }

    [Fact]
    public async Task ProcessSong_RateLimited_RecordsRateLimitedAttempt_StatusStaysPending()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(
                new ProviderRateLimited(TimeSpan.FromSeconds(30))));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider]);
        var outcome = await orchestrator.ProcessSongAsync(song.Id);

        var updated = await db.Songs.Include(s => s.ProviderAttempts).SingleAsync();

        Assert.Equal(EnrichmentOutcome.Skipped, outcome);
        Assert.Equal(EnrichmentStatus.Pending, updated.EnrichmentStatus);
        Assert.Contains(updated.ProviderAttempts, a =>
            a.Provider == EnrichmentProvider.AcoustID
            && a.Status == ProviderAttemptStatus.RateLimited
            && a.RetryAfterUtc.HasValue);
    }

    [Fact]
    public async Task ProcessSong_ExistingTerminalAttempt_SkipsProvider()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        var wasCalled = false;
        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ =>
            {
                wasCalled = true;
                return Task.FromResult<ProviderOutcome>(new ProviderNoMatch());
            });

        var orchestrator = CreateOrchestratorWithProviders(db, [provider]);
        await orchestrator.ProcessSongAsync(song.Id);

        Assert.False(wasCalled);
    }

    [Fact]
    public async Task ProcessSong_RateLimitedAttemptPastRetry_RetriesProvider()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.RateLimited,
            AttemptedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            RetryAfterUtc = DateTime.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var wasCalled = false;
        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ =>
            {
                wasCalled = true;
                return Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                    "Artist", "Artist", "Title", null, null,
                    "mb-1", null, null, null, null, "AcoustID", 0.95, [], EnrichmentStatus.Matched)));
            });

        var orchestrator = CreateOrchestratorWithProviders(db, [provider]);
        var outcome = await orchestrator.ProcessSongAsync(song.Id);

        // The rate-limited AcoustID attempt is retried (the point of this test). The retried hit is
        // clean and unambiguous, and AcoustID is the only enabled provider, so it stands alone.
        Assert.True(wasCalled);
        Assert.Equal(EnrichmentOutcome.Matched, outcome);
    }

    [Fact]
    public async Task ProcessSong_ProviderNoMatch_WithBestCandidate_PersistsCandidateJson()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var bestCandidate = new EnrichmentProviderResult(
            Artist: "Spotify Artist",
            AlbumArtist: "Spotify Artist",
            Title: "Spotify Title",
            Year: 2018,
            TrackNumber: 4,
            MusicBrainzId: null,
            MusicBrainzReleaseId: null,
            SpotifyId: "spotify-near-miss",
            AcoustIdTrackId: null,
            Isrc: null,
            MatchedBy: "SpotifyAPI",
            MatchConfidence: 0.42,
            MatchWarnings: ["artist_mismatch"],
            RecommendedStatus: EnrichmentStatus.NeedsReview,
            Album: "Spotify Album");

        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderNoMatch(bestCandidate)));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider]);
        await orchestrator.ProcessSongAsync(song.Id);

        var attempt = await db.SongProviderAttempts.SingleAsync();
        Assert.Equal(ProviderAttemptStatus.NoMatch, attempt.Status);
        Assert.NotNull(attempt.MatchedDataJson);

        var roundTrip = JsonSerializer.Deserialize<EnrichmentProviderResult>(attempt.MatchedDataJson!);
        Assert.NotNull(roundTrip);
        Assert.Equal("Spotify Title", roundTrip!.Title);
        Assert.Equal("Spotify Artist", roundTrip.Artist);
        Assert.Equal("spotify-near-miss", roundTrip.SpotifyId);
        Assert.Equal(0.42, roundTrip.MatchConfidence);
        Assert.Contains("artist_mismatch", roundTrip.MatchWarnings);
    }

    [Fact]
    public async Task ProcessSong_NeedsReviewProviderHit_DoesNotPoisonNextProviderQuery()
    {
        // Regression: this reproduces the "4 Raws / EsDeeKid → Arctic Monkeys / Balaclava" failure.
        // AcoustID matched a wrong recording but correctly recommended NeedsReview. Previously,
        // the orchestrator applied the wrong metadata to the row anyway, so when Spotify ran next
        // it saw "Arctic Monkeys / Balaclava" instead of the original "EsDeeKid / 4 Raws", then
        // happily found that exact track on Spotify and persisted IDs/album for the wrong song.
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "EsDeeKid", title: "4 Raws");
        song.Album = "Rebel";
        await db.SaveChangesAsync();

        string? observedSpotifyArtist = null;
        string? observedSpotifyTitle = null;

        var acoustIdProvider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                Artist: "Arctic Monkeys",
                AlbumArtist: "Arctic Monkeys",
                Title: "Balaclava",
                Year: null,
                TrackNumber: null,
                MusicBrainzId: "mb-wrong",
                MusicBrainzReleaseId: null,
                SpotifyId: null,
                AcoustIdTrackId: "acoust-wrong",
                Isrc: null,
                MatchedBy: "AcoustID",
                MatchConfidence: 0.279,
                MatchWarnings: ["artist_mismatch", "duration_mismatch", "title_mismatch"],
                RecommendedStatus: EnrichmentStatus.NeedsReview))));

        var spotifyProvider = new StubEnrichmentProvider("SpotifyAPI", 200,
            canHandle: _ => true,
            enrich: s =>
            {
                observedSpotifyArtist = s.Artist;
                observedSpotifyTitle = s.Title;
                return Task.FromResult<ProviderOutcome>(new ProviderNoMatch());
            });

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [acoustIdProvider, spotifyProvider], opts);
        var outcome = await orchestrator.ProcessSongAsync(song.Id);

        Assert.Equal(EnrichmentOutcome.NeedsReview, outcome);

        // Spotify must have seen the original artist/title — never the AcoustID-suggested wrong values.
        Assert.Equal("EsDeeKid", observedSpotifyArtist);
        Assert.Equal("4 Raws", observedSpotifyTitle);

        var updated = await db.Songs.Include(s => s.ProviderAttempts).SingleAsync();
        Assert.Equal("EsDeeKid", updated.Artist);
        Assert.Equal("4 Raws", updated.Title);
        Assert.Equal("Rebel", updated.Album);
        Assert.Null(updated.MusicBrainzId);
        Assert.Null(updated.AcoustIdTrackId);

        // Bookkeeping: row tracks the best candidate's confidence so reviewers/bulk-approve can find it.
        Assert.Equal("AcoustID", updated.MatchedBy);
        Assert.Equal(0.279, updated.MatchConfidence);

        // Both attempts persisted; AcoustID's wrong candidate is preserved for the review UI.
        Assert.Equal(2, updated.ProviderAttempts.Count);
        var acoustIdAttempt = updated.ProviderAttempts.Single(a => a.Provider == EnrichmentProvider.AcoustID);
        Assert.Equal(ProviderAttemptStatus.Matched, acoustIdAttempt.Status);
        Assert.NotNull(acoustIdAttempt.MatchedDataJson);
        var candidate = JsonSerializer.Deserialize<EnrichmentProviderResult>(acoustIdAttempt.MatchedDataJson!);
        Assert.Equal("Arctic Monkeys", candidate!.Artist);
        Assert.Equal("Balaclava", candidate.Title);
    }

    [Fact]
    public async Task ProcessSong_NeedsReviewProviderHit_TracksHighestConfidenceCandidate()
    {
        // Two providers both return NeedsReview at different confidences. The row's MatchedBy /
        // MatchConfidence should reflect the higher-confidence candidate so it can be picked up
        // by /songs/bulk-approve at minConfidence ≥ that value.
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var lowConf = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null, "mb-low", null, null, null, null,
                "AcoustID", 0.40, [], EnrichmentStatus.NeedsReview))));
        var highConf = new StubEnrichmentProvider("SpotifyAPI", 200,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", 2024, null, null, null, "spot-high", null, null,
                "SpotifyAPI", 0.78, [], EnrichmentStatus.NeedsReview))));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [lowConf, highConf], opts);
        await orchestrator.ProcessSongAsync(song.Id);

        var updated = await db.Songs.SingleAsync();
        Assert.Equal("SpotifyAPI", updated.MatchedBy);
        Assert.Equal(0.78, updated.MatchConfidence);
    }

    [Fact]
    public async Task ProcessSong_ProviderNoMatch_WithoutBestCandidate_PersistsNullJson()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderNoMatch()));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider]);
        await orchestrator.ProcessSongAsync(song.Id);

        var attempt = await db.SongProviderAttempts.SingleAsync();
        Assert.Equal(ProviderAttemptStatus.NoMatch, attempt.Status);
        Assert.Null(attempt.MatchedDataJson);
    }

    [Fact]
    public async Task ComputeSummaryStatus_AllProvidersNoMatch_ReturnsNeedsReview()
    {
        var enabledProviders = new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID };
        var song = CreateSongWithAttempts(
            (EnrichmentProvider.AcoustID, ProviderAttemptStatus.NoMatch));

        var status = song.ComputeSummaryStatus(enabledProviders);

        Assert.Equal(EnrichmentStatus.NeedsReview, status);
    }

    [Fact]
    public async Task ComputeSummaryStatus_SomeRateLimited_ReturnsPending()
    {
        var enabledProviders = new HashSet<EnrichmentProvider>
            { EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI };
        var song = CreateSongWithAttempts(
            (EnrichmentProvider.AcoustID, ProviderAttemptStatus.NoMatch),
            (EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.RateLimited));

        var status = song.ComputeSummaryStatus(enabledProviders);

        Assert.Equal(EnrichmentStatus.Pending, status);
    }

    [Fact]
    public async Task ComputeSummaryStatus_SingleCleanAcoustIdCandidate_Matched()
    {
        // A lone *clean, unambiguous* AcoustID candidate (validator recommended Matched, no
        // multiple_candidates) is acoustically authoritative and may stand alone.
        var enabledProviders = new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID };
        var song = CreateSongWithCandidate(
            EnrichmentProvider.AcoustID,
            new EnrichmentProviderResult("Artist", "Artist", "Title", null, null,
                "mb-1", null, null, "ac-1", null, "AcoustID", 0.95, [], EnrichmentStatus.Matched));

        var status = song.ComputeSummaryStatus(enabledProviders);

        Assert.Equal(EnrichmentStatus.Matched, status);
    }

    [Fact]
    public async Task ComputeSummaryStatus_AmbiguousAcoustIdCandidate_NeedsReview()
    {
        // multiple_candidates → the fingerprint matched several recordings → cannot stand alone.
        var enabledProviders = new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID };
        var song = CreateSongWithCandidate(
            EnrichmentProvider.AcoustID,
            new EnrichmentProviderResult("Artist", "Artist", "Title", null, null,
                "mb-1", null, null, "ac-1", null, "AcoustID", 0.95, ["multiple_candidates"], EnrichmentStatus.Matched));

        var status = song.ComputeSummaryStatus(enabledProviders);

        Assert.Equal(EnrichmentStatus.NeedsReview, status);
    }

    [Fact]
    public async Task ComputeSummaryStatus_TwoProvidersAgree_ReturnsMatched()
    {
        var enabledProviders = new HashSet<EnrichmentProvider>
            { EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI };
        var song = CreateSongWithCandidate(
            EnrichmentProvider.AcoustID,
            new EnrichmentProviderResult("Daft Punk", "Daft Punk", "One More Time", null, null,
                "mb-1", null, null, "ac-1", null, "AcoustID", 0.80, [], EnrichmentStatus.Matched));
        AddCandidate(song, EnrichmentProvider.SpotifyAPI,
            new EnrichmentProviderResult("Daft Punk", "Daft Punk", "One More Time", 2001, null,
                null, null, "spot-1", null, null, "SpotifyAPI", 0.75, [], EnrichmentStatus.NeedsReview));

        var status = song.ComputeSummaryStatus(enabledProviders);

        Assert.Equal(EnrichmentStatus.Matched, status);
    }

    [Fact]
    public async Task ProcessSong_AcoustIdMbid_IsGossipedToMusicBrainz_WithinSamePass()
    {
        // AcoustID resolves a recording MBID; within the same pass that MBID is handed to the
        // MusicBrainz provider so it can do an exact lookup (genuine corroboration), instead of
        // only learning it on a later pass.
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams");
        await db.SaveChangesAsync();

        string? musicBrainzSawMbid = "<<unset>>";
        var acoustId = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Juice WRLD", "Juice WRLD", "Lucid Dreams", null, null,
                "mb-777", null, null, "acoust-1", null, "AcoustID", 0.70, [], EnrichmentStatus.NeedsReview))));
        var musicBrainz = new StubEnrichmentProvider("MusicBrainzWeb", 200,
            canHandle: s => !string.IsNullOrWhiteSpace(s.MusicBrainzId)
                || (!string.IsNullOrWhiteSpace(s.Artist) && !string.IsNullOrWhiteSpace(s.Title)),
            enrich: s =>
            {
                musicBrainzSawMbid = s.MusicBrainzId;
                return Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                    "Juice WRLD", "Juice WRLD", "Lucid Dreams", 2018, null,
                    "mb-777", null, null, null, null, "MusicBrainzWeb", 0.90, [], EnrichmentStatus.Matched)));
            });

        var opts = CreateOptions();
        opts.Value.EnableMusicBrainzWebProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [acoustId, musicBrainz], opts);
        var outcome = await orchestrator.ProcessSongAsync(song.Id);

        Assert.Equal("mb-777", musicBrainzSawMbid);
        Assert.Equal(EnrichmentOutcome.Matched, outcome);

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("mb-777", updated.MusicBrainzId);
    }

    [Fact]
    public async Task ProcessSong_GossipedIdentifier_NotPersisted_WhenNotConsensusWinner()
    {
        // AcoustID gossips an MBID that helps a later provider's lookup, but if the song ends in
        // NeedsReview (no winner) the gossiped identifier must be reverted, not written to the row.
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var acoustId = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                "mb-gossip", null, null, "ac-1", null, "AcoustID", 0.70, [], EnrichmentStatus.NeedsReview))));
        var musicBrainz = new StubEnrichmentProvider("MusicBrainzWeb", 200,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderNoMatch()));

        var opts = CreateOptions();
        opts.Value.EnableMusicBrainzWebProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [acoustId, musicBrainz], opts);
        var outcome = await orchestrator.ProcessSongAsync(song.Id);

        Assert.Equal(EnrichmentOutcome.NeedsReview, outcome);
        var updated = await db.Songs.SingleAsync();
        Assert.Null(updated.MusicBrainzId);
    }

    [Fact]
    public async Task ProcessSong_NoEnabledProviderCanFullyHandle_SurfacesNeedsReview_NotSilentPending()
    {
        // A fingerprint-less leak that AcoustID can't handle and a name-based provider can't match
        // must surface for review (visible in the "missing" section) rather than dead-ending
        // silently in Pending. We never force a terminal Failed.
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/root/music/Some Artist/Leak/unreleased.mp3",
            FileName = "unreleased.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Some Artist",
            Title = "Unreleased Leak",
            Fingerprint = null,
            DurationSeconds = null,
            EnrichmentStatus = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();
        var songId = (await db.Songs.SingleAsync()).Id;

        var acoustId = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: s => !string.IsNullOrWhiteSpace(s.Fingerprint),
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderNoMatch()));
        var spotify = new StubEnrichmentProvider("SpotifyAPI", 200,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderNoMatch()));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [acoustId, spotify], opts);
        var outcome = await orchestrator.ProcessSongAsync(songId);

        Assert.Equal(EnrichmentOutcome.NeedsReview, outcome);
        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.NeedsReview, updated.EnrichmentStatus);
    }

    // --- Helpers ---

    private static SongMetadata CreateSongWithAttempts(
        params (EnrichmentProvider Provider, ProviderAttemptStatus Status)[] attempts)
    {
        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/x.mp3",
            FileName = "x.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            EnrichmentStatus = EnrichmentStatus.Pending,
        };
        foreach (var (provider, status) in attempts)
        {
            song.ProviderAttempts.Add(new SongProviderAttempt
            {
                Provider = provider,
                Status = status,
                AttemptedAtUtc = DateTime.UtcNow,
            });
        }
        return song;
    }

    private static SongMetadata CreateSongWithCandidate(
        EnrichmentProvider provider, EnrichmentProviderResult candidate)
    {
        var song = CreateSongWithAttempts();
        AddCandidate(song, provider, candidate);
        return song;
    }

    private static void AddCandidate(
        SongMetadata song, EnrichmentProvider provider, EnrichmentProviderResult candidate)
    {
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = provider,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
            MatchedDataJson = JsonSerializer.Serialize(candidate),
        });
    }

    private static SongMetadata AddPendingSong(
        MusicHoarderDbContext db,
        string artist = "Artist",
        string title = "Title",
        string fingerprint = "fp-abc123")
    {
        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = $"/source/{Guid.NewGuid():N}.mp3",
            FileName = $"{title}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5_000_000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = artist,
            Title = title,
            Fingerprint = fingerprint,
            DurationSeconds = 240,
            DurationMs = 240_000,
            EnrichmentStatus = EnrichmentStatus.Pending,
        };
        db.Songs.Add(song);
        return song;
    }

    private static MusicHoarderDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static IOptions<MusicEnricherOptions> CreateOptions()
    {
        return Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            EnrichmentBatchSize = 100,
            EnrichmentWorkerConcurrency = 1,
            EnableAcoustIdProvider = true,
            EnableMusicBrainzWebProvider = false,
            EnableSpotifyApiProvider = false,
            EnableTrackerProvider = false,
        });
    }

    private static EnrichmentOrchestrator CreateOrchestrator(
        MusicHoarderDbContext db,
        IAcoustIdService acoustIdService,
        ILrcLibService? lrcLibService = null)
    {
        var opts = CreateOptions();
        var matchValidator = new AcoustIdMatchValidator();
        var scopeFactory = new OrchestratorScopeFactory(db);

        var acoustIdProvider = new AcoustIdEnrichmentProvider(
            acoustIdService, matchValidator,
            NullLogger<AcoustIdEnrichmentProvider>.Instance);

        IEnrichmentProvider[] providerList = [acoustIdProvider];

        return new EnrichmentOrchestrator(
            scopeFactory,
            providerList,
            lrcLibService ?? new NoOpLrcLibService(),
            opts,
            new TestRuntimeSettingsService(opts.Value),
            NullLogger<EnrichmentOrchestrator>.Instance);
    }

    private static EnrichmentOrchestrator CreateOrchestratorWithProviders(
        MusicHoarderDbContext db,
        IEnrichmentProvider[] providerList,
        IOptions<MusicEnricherOptions>? opts = null,
        ILrcLibService? lrcLibService = null)
    {
        opts ??= CreateOptions();
        var scopeFactory = new OrchestratorScopeFactory(db);

        return new EnrichmentOrchestrator(
            scopeFactory,
            providerList,
            lrcLibService ?? new NoOpLrcLibService(),
            opts,
            new TestRuntimeSettingsService(opts.Value),
            NullLogger<EnrichmentOrchestrator>.Instance);
    }

    internal sealed class StubAcoustIdService(
        Func<string, Task<AcoustIdMatch?>> handler) : IAcoustIdService
    {
        public Task<AcoustIdMatch?> LookupAsync(string fingerprint, int durationSeconds, CancellationToken ct = default)
            => handler(fingerprint);
    }

    private sealed class NoOpLrcLibService : ILrcLibService
    {
        public Task<LyricsResult?> FetchLyricsAsync(SongMetadata song, CancellationToken ct = default)
            => Task.FromResult<LyricsResult?>(null);
    }

    private sealed class StubLrcLibService(
        Func<SongMetadata, Task<LyricsResult?>> handler) : ILrcLibService
    {
        public int CallCount { get; private set; }

        public Task<LyricsResult?> FetchLyricsAsync(SongMetadata song, CancellationToken ct = default)
        {
            CallCount++;
            return handler(song);
        }
    }

    internal sealed class StubEnrichmentProvider(
        string name,
        int priority,
        Func<SongMetadata, bool> canHandle,
        Func<SongMetadata, Task<ProviderOutcome>> enrich) : IEnrichmentProvider
    {
        public string Name => name;
        public int Priority => priority;
        public bool CanHandle(SongMetadata song) => canHandle(song);
        public Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
            => enrich(song);
    }

    private sealed class OrchestratorScopeFactory(
        MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            new SimpleScope(new SimpleServiceProvider(db));
    }

    private sealed class SimpleScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;
        public void Dispose() { }
    }

    private sealed class SimpleServiceProvider(
        MusicHoarderDbContext db) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(MusicHoarderDbContext)) return db;
            return null;
        }
    }

    private sealed class TestRuntimeSettingsService : IRuntimeSettingsService
    {
        private readonly EffectiveSettings _effective;

        public TestRuntimeSettingsService(MusicEnricherOptions opts)
        {
            _effective = new EffectiveSettings(
                opts.EnableAcoustIdProvider,
                opts.EnableMusicBrainzWebProvider,
                opts.EnableSpotifyApiProvider,
                opts.EnableTrackerProvider,
                opts.SpotifyApiMatchedThreshold,
                opts.AcoustIdScoreThreshold,
                opts.EnrichmentWorkerConcurrency,
                opts.LibraryBuilderWorkerConcurrency,
                UpdatedAtUtc: null);
        }

        public Task<EffectiveSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(_effective);

        public Task<EffectiveSettings> UpdateAsync(RuntimeSettingsUpdate update, CancellationToken ct = default) => Task.FromResult(_effective);
    }
}
