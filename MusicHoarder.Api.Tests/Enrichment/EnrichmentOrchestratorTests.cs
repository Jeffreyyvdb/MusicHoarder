using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class EnrichmentOrchestratorTests
{
    [Fact]
    public async Task ProcessNextBatch_HighConfidenceMatch_SetsMatchedWithEnrichedFields()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-123", "acoust-123", "Lucid Dreams", "Juice WRLD", "Juice WRLD", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(1, result.Enriched);
        Assert.Equal(0, result.NeedsReview);
        Assert.Equal(0, result.Failed);
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("AcoustID", updated.MatchedBy);
        Assert.NotNull(updated.MatchConfidence);
        Assert.Equal("mb-123", updated.MusicBrainzId);
        Assert.Equal("Juice WRLD", updated.Artist);
        Assert.Equal("Lucid Dreams", updated.Title);
        Assert.NotNull(updated.EnrichedAtUtc);
        Assert.Null(updated.EnrichmentError);
    }

    [Fact]
    public async Task ProcessNextBatch_NoMatch_SetsNeedsReview()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Unknown", title: "Unknown Track");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(null));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(0, result.Enriched);
        Assert.Equal(1, result.NeedsReview);
        Assert.Equal(EnrichmentStatus.NeedsReview, updated.EnrichmentStatus);
        Assert.Contains("No provider returned a match", updated.EnrichmentError);
    }

    [Fact]
    public async Task ProcessNextBatch_ArtistMismatchPenalty_SetsNeedsReview()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-456", "acoust-456", "Lucid Dreams", "Stevie Wonder", "Stevie Wonder", 0.90f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(0, result.Enriched);
        Assert.Equal(1, result.NeedsReview);
        Assert.Equal(EnrichmentStatus.NeedsReview, updated.EnrichmentStatus);
        Assert.NotNull(updated.MatchWarnings);
        Assert.Contains("artist_mismatch", updated.MatchWarnings);
    }

    [Fact]
    public async Task ProcessNextBatch_SongWithTagsOnly_IsSelectedByRelaxedPredicate()
    {
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
            SourcePath = "/source/tags-only.mp3",
            FileName = "tags-only.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Tag Artist",
            Title = "Tag Title",
            Fingerprint = null,
            DurationSeconds = null,
            EnrichmentStatus = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();

        var stubProvider = new StubEnrichmentProvider("TestProvider", 100, canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(null));
        var orchestrator = CreateOrchestratorWithProviders(db, [stubProvider]);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(1, result.TotalTracks);
        Assert.Equal(1, result.NeedsReview);
    }

    [Fact]
    public async Task ProcessNextBatch_SongWithIsrcOnly_IsSelectedByRelaxedPredicate()
    {
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
            SourcePath = "/source/isrc-only.mp3",
            FileName = "isrc-only.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Isrc = "USRC12345",
            Fingerprint = null,
            DurationSeconds = null,
            EnrichmentStatus = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();

        var stubProvider = new StubEnrichmentProvider("TestProvider", 100, canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(null));
        var orchestrator = CreateOrchestratorWithProviders(db, [stubProvider]);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(1, result.TotalTracks);
    }

    [Fact]
    public async Task ProcessNextBatch_SongWithNoMetadata_NotSelected()
    {
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
            SourcePath = "/source/empty.mp3",
            FileName = "empty.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Fingerprint = null,
            DurationSeconds = null,
            Artist = null,
            Title = null,
            Isrc = null,
            EnrichmentStatus = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();

        var stubProvider = new StubEnrichmentProvider("TestProvider", 100,
            canHandle: _ => true,
            enrich: _ => throw new InvalidOperationException("should not be called"));
        var orchestrator = CreateOrchestratorWithProviders(db, [stubProvider]);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(0, result.TotalTracks);
    }

    [Fact]
    public async Task ProcessNextBatch_SongWithWhitespaceArtistAndTitle_NotSelected()
    {
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
            SourcePath = "/source/whitespace-tags.mp3",
            FileName = "whitespace-tags.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "   ",
            Title = "\t",
            Fingerprint = null,
            DurationSeconds = null,
            Isrc = null,
            EnrichmentStatus = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();

        var stubProvider = new StubEnrichmentProvider("TestProvider", 100,
            canHandle: _ => true,
            enrich: _ => throw new InvalidOperationException("should not be called"));
        var orchestrator = CreateOrchestratorWithProviders(db, [stubProvider]);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(0, result.TotalTracks);
    }

    [Fact]
    public async Task ProcessNextBatch_SongWithWhitespaceFingerprint_NotSelected()
    {
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
            SourcePath = "/source/whitespace-fp.mp3",
            FileName = "whitespace-fp.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = null,
            Title = null,
            Fingerprint = "  ",
            DurationSeconds = 240,
            Isrc = null,
            EnrichmentStatus = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();

        var stubProvider = new StubEnrichmentProvider("TestProvider", 100,
            canHandle: _ => true,
            enrich: _ => throw new InvalidOperationException("should not be called"));
        var orchestrator = CreateOrchestratorWithProviders(db, [stubProvider]);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(0, result.TotalTracks);
    }

    [Fact]
    public async Task ProcessNextBatch_SongWithWhitespaceIsrc_NotSelected()
    {
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
            SourcePath = "/source/whitespace-isrc.mp3",
            FileName = "whitespace-isrc.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = null,
            Title = null,
            Fingerprint = null,
            DurationSeconds = null,
            Isrc = "   ",
            EnrichmentStatus = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();

        var stubProvider = new StubEnrichmentProvider("TestProvider", 100,
            canHandle: _ => true,
            enrich: _ => throw new InvalidOperationException("should not be called"));
        var orchestrator = CreateOrchestratorWithProviders(db, [stubProvider]);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(0, result.TotalTracks);
    }

    [Fact]
    public async Task ProcessNextBatch_AcoustIdThrows_SetsFailed()
    {
        await using var db = CreateDb();
        AddPendingSong(db);
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ =>
            throw new HttpRequestException("connection refused"));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(0, result.Enriched);
        Assert.Equal(0, result.NeedsReview);
        Assert.Equal(1, result.Failed);
        Assert.Equal(EnrichmentStatus.Failed, updated.EnrichmentStatus);
        Assert.Contains("connection refused", updated.EnrichmentError);
    }

    [Fact]
    public async Task ProcessNextBatch_DeletedSong_SkipsWithoutChangingStatus()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db);
        song.DeletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => throw new InvalidOperationException("should not be called"));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(0, result.TotalTracks);
    }

    [Fact]
    public async Task ProcessNextBatch_CapturesOriginalMetadata_BeforeOverwriting()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Original Artist", title: "Original Title");
        song.Album = "Original Album";
        song.Isrc = "USRC12345";
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-789", "acoust-789", "New Title", "New Artist", "New Artist", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.True(updated.OriginalMetadataCaptured);
        Assert.Equal("Original Artist", updated.OriginalArtist);
        Assert.Equal("Original Title", updated.OriginalTitle);
        Assert.Equal("Original Album", updated.OriginalAlbum);
        Assert.Equal("USRC12345", updated.OriginalIsrc);
        Assert.NotNull(updated.OriginalMetadataCapturedAtUtc);

        Assert.Equal("New Artist", updated.Artist);
        Assert.Equal("New Title", updated.Title);
    }

    [Fact]
    public async Task ProcessNextBatch_OriginalMetadata_NotOverwrittenOnSecondEnrichment()
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

        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal("Very Original", updated.OriginalArtist);
        Assert.Equal("Very Original Title", updated.OriginalTitle);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), updated.OriginalMetadataCapturedAtUtc);
    }

    [Fact]
    public async Task ProcessNextBatch_MixedBatch_ReturnsCorrectCounts()
    {
        await using var db = CreateDb();

        var matched = AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams", fingerprint: "fp-match");
        var review = AddPendingSong(db, artist: "Unknown", title: "Unknown", fingerprint: "fp-review");
        var failed = AddPendingSong(db, artist: "Fail", title: "Fail", fingerprint: "fp-fail");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(fingerprint => fingerprint switch
        {
            "fp-match" => Task.FromResult<AcoustIdMatch?>(
                new AcoustIdMatch("mb-1", "acoust-1", "Lucid Dreams", "Juice WRLD", "Juice WRLD", 0.95f, 240_000)),
            "fp-review" => Task.FromResult<AcoustIdMatch?>(null),
            "fp-fail" => throw new HttpRequestException("boom"),
            _ => Task.FromResult<AcoustIdMatch?>(null)
        });
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(3, result.TotalTracks);
        Assert.Equal(1, result.Enriched);
        Assert.Equal(1, result.NeedsReview);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task ProcessNextBatch_NoPendingSongs_ReturnsZeroCounts()
    {
        await using var db = CreateDb();
        var orchestrator = CreateOrchestrator(db, new StubAcoustIdService(_ =>
            throw new InvalidOperationException("should not be called")));

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(0, result.TotalTracks);
        Assert.Equal(0, result.Enriched);
        Assert.Equal(0, result.NeedsReview);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task ProcessNextBatch_MatchPreservesExistingArtist_WhenMatchArtistIsBlank()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Existing Artist", title: "Existing Title");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-blank", "acoust-blank", "New Title", "", "", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal("Existing Artist", updated.Artist);
        Assert.Equal("New Title", updated.Title);
    }

    [Fact]
    public async Task ProcessNextBatch_StampsEnrichmentLastAttemptedAtUtc()
    {
        await using var db = CreateDb();
        AddPendingSong(db);
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-ts", "acoust-ts", "Title", "Artist", "Artist", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.NotNull(updated.EnrichmentLastAttemptedAtUtc);
        Assert.True(updated.EnrichmentLastAttemptedAtUtc >= before);
    }

    [Fact]
    public async Task ProcessNextBatch_ProviderChain_StopsAtFirstMatched()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var called = new List<string>();
        var provider1 = new StubEnrichmentProvider("Provider1", 100,
            canHandle: _ => true,
            enrich: _ =>
            {
                called.Add("Provider1");
                return Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                    "Matched Artist", "Matched Artist", "Matched Title", null, null,
                    "mb-1", null, null, null, null, "Provider1", 0.95, [], EnrichmentStatus.Matched));
            });
        var provider2 = new StubEnrichmentProvider("Provider2", 200,
            canHandle: _ => true,
            enrich: _ =>
            {
                called.Add("Provider2");
                return Task.FromResult<EnrichmentProviderResult?>(null);
            });

        var orchestrator = CreateOrchestratorWithProviders(db, [provider1, provider2]);
        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Single(called);
        Assert.Equal("Provider1", called[0]);

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("Provider1", updated.MatchedBy);
    }

    [Fact]
    public async Task ProcessNextBatch_ProviderChain_FallsBackToSecondProvider()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider1 = new StubEnrichmentProvider("Provider1", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(null));
        var provider2 = new StubEnrichmentProvider("Provider2", 200,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                "P2 Artist", "P2 Artist", "P2 Title", null, null,
                null, null, "spotify-1", null, null, "Provider2", 0.90, [], EnrichmentStatus.Matched)));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider1, provider2]);
        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("Provider2", updated.MatchedBy);
        Assert.Equal("spotify-1", updated.SpotifyId);
    }

    [Fact]
    public async Task ProcessNextBatch_ProviderChain_PicksBestNeedsReview()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider1 = new StubEnrichmentProvider("Provider1", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                "P1 Artist", null, "P1 Title", null, null,
                "mb-1", null, null, null, null, "Provider1", 0.40, ["low_score"], EnrichmentStatus.NeedsReview)));
        var provider2 = new StubEnrichmentProvider("Provider2", 200,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                "P2 Artist", null, "P2 Title", null, null,
                null, null, "sp-1", null, null, "Provider2", 0.60, ["uncertain"], EnrichmentStatus.NeedsReview)));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider1, provider2]);
        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.NeedsReview, updated.EnrichmentStatus);
        Assert.Equal("Provider2", updated.MatchedBy);
        Assert.Equal(0.60, updated.MatchConfidence);
    }

    [Fact]
    public async Task ProcessNextBatch_ProviderChain_SkipsProviderThatCannotHandle()
    {
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
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

        var acoustIdCalled = false;
        var provider1 = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: s => !string.IsNullOrWhiteSpace(s.Fingerprint),
            enrich: _ =>
            {
                acoustIdCalled = true;
                return Task.FromResult<EnrichmentProviderResult?>(null);
            });
        var provider2 = new StubEnrichmentProvider("FallbackProvider", 200,
            canHandle: s => !string.IsNullOrWhiteSpace(s.Artist) && !string.IsNullOrWhiteSpace(s.Title),
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                null, null, null, null, null, "FallbackProvider", 0.90, [], EnrichmentStatus.Matched)));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider1, provider2]);
        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.False(acoustIdCalled);
        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("FallbackProvider", updated.MatchedBy);
    }

    [Fact]
    public async Task ProcessNextBatch_ProviderChain_ContinuesAfterProviderException()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider1 = new StubEnrichmentProvider("FailingProvider", 100,
            canHandle: _ => true,
            enrich: _ => throw new HttpRequestException("timeout"));
        var provider2 = new StubEnrichmentProvider("WorkingProvider", 200,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                "mb-1", null, null, null, null, "WorkingProvider", 0.92, [], EnrichmentStatus.Matched)));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider1, provider2]);
        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(1, result.Enriched);
        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("WorkingProvider", updated.MatchedBy);
    }

    [Fact]
    public async Task ProcessNextBatch_ProviderChain_AllProvidersFailMarksAsFailed()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider1 = new StubEnrichmentProvider("Provider1", 100,
            canHandle: _ => true,
            enrich: _ => throw new HttpRequestException("error1"));
        var provider2 = new StubEnrichmentProvider("Provider2", 200,
            canHandle: _ => true,
            enrich: _ => throw new HttpRequestException("error2"));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider1, provider2]);
        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(1, result.Failed);
        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Failed, updated.EnrichmentStatus);
        Assert.Contains("Provider1: error1", updated.EnrichmentError);
        Assert.Contains("Provider2: error2", updated.EnrichmentError);
    }

    [Fact]
    public async Task ProcessNextBatch_ProviderResult_PersistsSpotifyId()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("SpotifyAPI", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", 2024, 5,
                null, null, "spotify-abc", null, "USRC99999", "SpotifyAPI", 0.95,
                [], EnrichmentStatus.Matched)));

        var opts = CreateOptions();
        opts.Value.EnableSpotifyApiProvider = true;
        var orchestrator = CreateOrchestratorWithProviders(db, [provider], opts);
        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        var updated = await db.Songs.SingleAsync();
        Assert.Equal("spotify-abc", updated.SpotifyId);
        Assert.Equal("USRC99999", updated.Isrc);
        Assert.Equal(2024, updated.Year);
        Assert.Equal(5, updated.TrackNumber);
        Assert.Equal("SpotifyAPI", updated.MatchedBy);
    }

    [Fact]
    public async Task ProcessNextBatch_DisabledProvider_IsSkipped()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var acoustIdCalled = false;
        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ =>
            {
                acoustIdCalled = true;
                return Task.FromResult<EnrichmentProviderResult?>(null);
            });

        var opts = CreateOptions();
        opts.Value.EnableAcoustIdProvider = false;
        var orchestrator = CreateOrchestratorWithProviders(db, [provider], opts);
        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.False(acoustIdCalled);
    }

    [Fact]
    public async Task ProcessNextBatch_MatchedSong_LyricsFound_SetsFetchedLyricsState()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("Provider1", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                "mb-lyrics", null, null, null, null, "Provider1", 0.95, [], EnrichmentStatus.Matched)));
        var lrcLib = new StubLrcLibService(_ =>
            Task.FromResult<LyricsResult?>(new LyricsResult("[00:00.00]Hello", "Hello", false)));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider], lrcLibService: lrcLib);
        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(1, result.Enriched);
        Assert.Equal(1, lrcLib.CallCount);

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal(LyricsStatus.Fetched, updated.LyricsStatus);
        Assert.Equal("[00:00.00]Hello", updated.SyncedLyrics);
        Assert.Equal("Hello", updated.PlainLyrics);
        Assert.False(updated.IsInstrumental);
    }

    [Fact]
    public async Task ProcessNextBatch_MatchedSong_InstrumentalLyrics_SetsInstrumentalState()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("Provider1", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                "mb-lyrics", null, null, null, null, "Provider1", 0.95, [], EnrichmentStatus.Matched)));
        var lrcLib = new StubLrcLibService(_ =>
            Task.FromResult<LyricsResult?>(new LyricsResult("ignored", "ignored", true)));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider], lrcLibService: lrcLib);
        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(LyricsStatus.Instrumental, updated.LyricsStatus);
        Assert.True(updated.IsInstrumental);
        Assert.Null(updated.SyncedLyrics);
        Assert.Null(updated.PlainLyrics);
        Assert.Equal(1, lrcLib.CallCount);
    }

    [Fact]
    public async Task ProcessNextBatch_MatchedSong_LyricsFetchThrows_MarksLyricsFailedButKeepsMatch()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Artist", title: "Title");
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("Provider1", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                "mb-lyrics", null, null, null, null, "Provider1", 0.95, [], EnrichmentStatus.Matched)));
        var lrcLib = new StubLrcLibService(_ => throw new HttpRequestException("lrc failed"));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider], lrcLibService: lrcLib);
        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(1, result.Enriched);
        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal(LyricsStatus.Failed, updated.LyricsStatus);
        Assert.Equal(1, lrcLib.CallCount);
    }

    [Fact]
    public async Task ProcessNextBatch_MatchedSongWithoutArtistOrTitle_SkipsLyricsFetch()
    {
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
            SourcePath = "/source/no-tags.mp3",
            FileName = "no-tags.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = null,
            Title = null,
            Fingerprint = "fp-has-data",
            DurationSeconds = 180,
            EnrichmentStatus = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("Provider1", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                "", "", "", null, null,
                "mb-lyrics", null, null, null, null, "Provider1", 0.95, [], EnrichmentStatus.Matched)));
        var lrcLib = new StubLrcLibService(_ =>
            Task.FromResult<LyricsResult?>(new LyricsResult("[00:00.00]Hello", "Hello", false)));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider], lrcLibService: lrcLib);
        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal(LyricsStatus.NotFetched, updated.LyricsStatus);
        Assert.Equal(0, lrcLib.CallCount);
    }

    [Fact]
    public async Task ProcessNextBatch_MatchedSong_AlreadyHasLyrics_DoesNotRefetchLyrics()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Artist", title: "Title");
        song.LyricsStatus = LyricsStatus.Fetched;
        song.PlainLyrics = "Existing plain";
        await db.SaveChangesAsync();

        var provider = new StubEnrichmentProvider("Provider1", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<EnrichmentProviderResult?>(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                "mb-no-refetch", null, null, null, null, "Provider1", 0.95, [], EnrichmentStatus.Matched)));
        var lrcLib = new StubLrcLibService(_ =>
            Task.FromResult<LyricsResult?>(new LyricsResult("[00:01.00]New synced", "New plain", false)));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider], lrcLibService: lrcLib);
        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal(LyricsStatus.Fetched, updated.LyricsStatus);
        Assert.Equal("Existing plain", updated.PlainLyrics);
        Assert.Equal(0, lrcLib.CallCount);
    }

    // --- Helpers ---

    private static SongMetadata AddPendingSong(
        MusicHoarderDbContext db,
        string artist = "Artist",
        string title = "Title",
        string fingerprint = "fp-abc123")
    {
        var song = new SongMetadata
        {
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
        var progressTracker = new EnrichmentProgressTracker();
        var matchValidator = new AcoustIdMatchValidator();
        var scopeFactory = new OrchestratorScopeFactory(db);

        var acoustIdProvider = new AcoustIdEnrichmentProvider(
            acoustIdService, matchValidator,
            NullLogger<AcoustIdEnrichmentProvider>.Instance);

        IEnrichmentProvider[] providerList = [acoustIdProvider];

        return new EnrichmentOrchestrator(
            scopeFactory,
            progressTracker,
            providerList,
            lrcLibService ?? new NoOpLrcLibService(),
            opts,
            NullLogger<EnrichmentOrchestrator>.Instance);
    }

    private static EnrichmentOrchestrator CreateOrchestratorWithProviders(
        MusicHoarderDbContext db,
        IEnrichmentProvider[] providerList,
        IOptions<MusicEnricherOptions>? opts = null,
        ILrcLibService? lrcLibService = null)
    {
        opts ??= CreateOptions();
        var progressTracker = new EnrichmentProgressTracker();
        var scopeFactory = new OrchestratorScopeFactory(db);

        return new EnrichmentOrchestrator(
            scopeFactory,
            progressTracker,
            providerList,
            lrcLibService ?? new NoOpLrcLibService(),
            opts,
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
        Func<SongMetadata, Task<EnrichmentProviderResult?>> enrich) : IEnrichmentProvider
    {
        public string Name => name;
        public int Priority => priority;
        public bool CanHandle(SongMetadata song) => canHandle(song);
        public Task<EnrichmentProviderResult?> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
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
}
