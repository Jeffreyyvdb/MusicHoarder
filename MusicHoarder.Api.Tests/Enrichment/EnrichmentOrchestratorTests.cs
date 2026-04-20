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
    public async Task ProcessSong_HighConfidenceMatch_SetsMatchedWithEnrichedFields()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-123", "acoust-123", "Lucid Dreams", "Juice WRLD", "Juice WRLD", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var outcome = await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(EnrichmentOutcome.Matched, outcome);
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
    public async Task ProcessSong_ArtistMismatchPenalty_SetsNeedsReview()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-456", "acoust-456", "Lucid Dreams", "Stevie Wonder", "Stevie Wonder", 0.90f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var outcome = await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(EnrichmentOutcome.NeedsReview, outcome);
        Assert.NotNull(updated.MatchWarnings);
        Assert.Contains("artist_mismatch", updated.MatchWarnings);
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

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-789", "acoust-789", "New Title", "New Artist", "New Artist", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        await orchestrator.ProcessSongAsync(song.Id);
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
    public async Task ProcessSong_ProviderChain_StopsAtFirstMatched()
    {
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

        Assert.Single(called);
        Assert.Equal("AcoustID", called[0]);

        var updated = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("AcoustID", updated.MatchedBy);
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

        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                "mb-lyrics", null, null, null, null, "AcoustID", 0.95, [], EnrichmentStatus.Matched))));
        var lrcLib = new StubLrcLibService(_ =>
            Task.FromResult<LyricsResult?>(new LyricsResult("[00:00.00]Hello", "Hello", false)));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider], lrcLibService: lrcLib);
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

        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                "mb-lyrics", null, null, null, null, "AcoustID", 0.95, [], EnrichmentStatus.Matched))));
        var lrcLib = new StubLrcLibService(_ =>
            Task.FromResult<LyricsResult?>(new LyricsResult("ignored", "ignored", true)));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider], lrcLibService: lrcLib);
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

        var provider = new StubEnrichmentProvider("AcoustID", 100,
            canHandle: _ => true,
            enrich: _ => Task.FromResult<ProviderOutcome>(new ProviderMatched(new EnrichmentProviderResult(
                "Artist", "Artist", "Title", null, null,
                "mb-lyrics", null, null, null, null, "AcoustID", 0.95, [], EnrichmentStatus.Matched))));
        var lrcLib = new StubLrcLibService(_ => throw new HttpRequestException("lrc failed"));

        var orchestrator = CreateOrchestratorWithProviders(db, [provider], lrcLibService: lrcLib);
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

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-blank", "acoust-blank", "New Title", "", "", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        await orchestrator.ProcessSongAsync(song.Id);
        var updated = await db.Songs.SingleAsync();

        Assert.Equal("Existing Artist", updated.Artist);
        Assert.Equal("New Title", updated.Title);
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

        Assert.True(wasCalled);
        Assert.Equal(EnrichmentOutcome.Matched, outcome);
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
    public async Task ComputeSummaryStatus_OneMatched_ReturnsMatched()
    {
        var enabledProviders = new HashSet<EnrichmentProvider>
            { EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI };
        var song = CreateSongWithAttempts(
            (EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched));

        var status = song.ComputeSummaryStatus(enabledProviders);

        Assert.Equal(EnrichmentStatus.Matched, status);
    }

    // --- Helpers ---

    private static SongMetadata CreateSongWithAttempts(
        params (EnrichmentProvider Provider, ProviderAttemptStatus Status)[] attempts)
    {
        var song = new SongMetadata
        {
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
}
