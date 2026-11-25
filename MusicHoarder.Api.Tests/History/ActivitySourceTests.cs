using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.History;
using MusicHoarder.Api.History.Sources;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.History;

/// <summary>
/// Covers the derived half of the History feed: each source turning rows the pipeline already wrote
/// into feed entries. These also pin that every query actually translates — the sources run against a
/// real provider in production and the in-memory one here.
/// </summary>
public class ActivitySourceTests
{
    private static readonly Guid Owner = Api.Auth.WellKnownUsers.OwnerId;
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ActivityWindow Window = new(Now.AddDays(-7), Now.AddDays(1), 4000);

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static SongMetadata AddSong(
        MusicHoarderDbContext db, int n, string title, string album = "Album", string artist = "Artist")
    {
        var song = new SongMetadata
        {
            OwnerUserId = Owner,
            SourcePath = $"/src/{n}.mp3",
            FileName = $"{n}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1000,
            LastModifiedUtc = Now,
            IndexedAtUtc = Now,
            Title = title,
            Artist = artist,
            Album = album,
            AlbumArtist = artist,
        };
        db.Songs.Add(song);
        db.SaveChanges();
        return song;
    }

    // --- Acquisition -------------------------------------------------------------------------

    [Fact]
    public async Task Acquisition_TellsScannedDownloadedAndAlbumFilledApart()
    {
        using var db = NewContext();
        var scanned = AddSong(db, 1, "Was already here");
        var downloaded = AddSong(db, 2, "Came from Spotify");
        var filled = AddSong(db, 3, "Nobody asked for this");
        foreach (var s in new[] { scanned, downloaded, filled }) s.AcquiredAtUtc = Now.AddHours(-1);

        var source = new WishlistSource
        {
            OwnerUserId = Owner, SourceType = WishlistSourceType.LikedSongs,
            Name = "Liked Songs", CreatedAtUtc = Now.AddDays(-2),
        };
        db.WishlistSources.Add(source);
        db.SaveChanges();

        db.WishlistItems.AddRange(
            NewWishlistItem(downloaded.Id, WishlistItemOrigin.UserRequested, source.Id),
            NewWishlistItem(filled.Id, WishlistItemOrigin.AlbumCompletion, null));
        db.SaveChanges();

        var entries = await new AcquisitionActivitySource(db).CollectAsync(Window, default);

        var kinds = entries.Select(e => e.Kind).ToList();
        Assert.Contains("scanned-in", kinds);
        Assert.Contains("downloaded", kinds);
        Assert.Contains("album-filled", kinds);
        Assert.Contains("Spotify", Assert.Single(entries, e => e.Kind == "downloaded").Detail);
        Assert.All(entries, e => Assert.Equal(ActivityCategory.Acquired, e.Category));
    }

    [Fact]
    public async Task Acquisition_ReportsAnAppliedUpgradeWithItsNewQuality()
    {
        using var db = NewContext();
        var song = AddSong(db, 1, "Runaway");
        song.Extension = ".flac";
        song.Bitrate = 1411;
        db.UpgradeRequests.Add(new UpgradeRequest
        {
            SongId = song.Id, OwnerUserId = Owner, Trigger = UpgradeTrigger.Auto,
            Status = UpgradeRequestStatus.Completed,
            CreatedAtUtc = Now.AddHours(-3), UpdatedAtUtc = Now.AddHours(-1), CompletedAtUtc = Now.AddHours(-1),
        });
        db.SaveChanges();

        var entry = Assert.Single(
            await new AcquisitionActivitySource(db).CollectAsync(Window, default),
            e => e.Kind == "upgrade-applied");
        Assert.Contains("Runaway", entry.Headline);
        Assert.Contains("FLAC", entry.Detail);
        Assert.Equal(song.Id, entry.SongId);
    }

    [Fact]
    public async Task Acquisition_ReportsAlbumCompletionGoingLookingForMissingTracks()
    {
        using var db = NewContext();
        var canonical = new CanonicalAlbum
        {
            ArtistKey = "kanyewest", AlbumKey = "graduation",
            DisplayTitle = "Graduation", DisplayArtist = "Kanye West", ResolvedTrackCount = 14,
        };
        db.CanonicalAlbums.Add(canonical);
        db.SaveChanges();
        db.AlbumCompletionStates.Add(new AlbumCompletionState
        {
            OwnerUserId = Owner, CanonicalAlbumId = canonical.Id, Status = AlbumCompletionStatus.Filled,
            OwnedTrackCount = 10, CanonicalTrackCount = 14, EnqueuedTrackCount = 4,
            LastSweptAtUtc = Now.AddHours(-2), CreatedAtUtc = Now.AddHours(-2), UpdatedAtUtc = Now.AddHours(-2),
        });
        db.SaveChanges();

        var entry = Assert.Single(
            await new AcquisitionActivitySource(db).CollectAsync(Window, default),
            e => e.Kind == "album-completion");
        Assert.Contains("4 missing tracks", entry.Headline);
        Assert.Contains("10 of 14", entry.Detail);
    }

    private static WishlistItem NewWishlistItem(int songId, WishlistItemOrigin origin, int? sourceId) => new()
    {
        OwnerUserId = Owner, Title = "T", Artist = "A", DurationMs = 1000,
        Origin = origin, WishlistSourceId = sourceId, DownloadedSongId = songId,
        Status = WishlistItemStatus.Downloaded,
        CreatedAtUtc = Now.AddHours(-2), UpdatedAtUtc = Now.AddHours(-1),
    };

    // --- Enrichment --------------------------------------------------------------------------

    [Fact]
    public async Task Enrichment_DoesNotReportYourOwnApprovalAsAnAutomaticMatch()
    {
        using var db = NewContext();
        var auto = AddSong(db, 1, "Matched by itself");
        auto.EnrichmentStatus = EnrichmentStatus.Matched;
        auto.EnrichedAtUtc = Now.AddHours(-2);
        var byHand = AddSong(db, 2, "You decided this one");
        byHand.EnrichmentStatus = EnrichmentStatus.Matched;
        byHand.EnrichedAtUtc = Now.AddHours(-1);
        byHand.ManuallyApprovedAtUtc = Now.AddHours(-1);
        db.SaveChanges();

        var entries = await new EnrichmentActivitySource(db).CollectAsync(Window, default);

        var matched = Assert.Single(entries, e => e.Kind == "matched");
        Assert.Equal(1, matched.TrackCount);
        Assert.Equal(auto.Id, matched.SongId);
        var approved = Assert.Single(entries, e => e.Kind == "review-approved");
        Assert.Equal(byHand.Id, approved.SongId);
    }

    [Fact]
    public async Task Enrichment_FlagsTracksWaitingInReview()
    {
        using var db = NewContext();
        var song = AddSong(db, 1, "Unsure");
        song.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        song.EnrichedAtUtc = Now.AddHours(-1);
        db.SaveChanges();

        var entry = Assert.Single(
            await new EnrichmentActivitySource(db).CollectAsync(Window, default),
            e => e.Kind == "needs-review");
        Assert.Equal(ActivityTint.Warn, entry.Tint);
    }

    // --- Lyrics ------------------------------------------------------------------------------

    [Fact]
    public async Task Lyrics_SeparatesSyncedFromPlainAndReportsTheSource()
    {
        using var db = NewContext();
        var song = AddSong(db, 1, "Blood on the Leaves");
        song.LyricsStatus = LyricsStatus.Fetched;
        song.SyncedLyrics = "[00:01.00] words";
        song.LyricsLastAttemptedAtUtc = Now.AddHours(-1);
        db.SaveChanges();

        var entry = Assert.Single(
            await new LyricsActivitySource(db).CollectAsync(Window, default),
            e => e.Kind == "lyrics-added");
        Assert.Contains("synced lyrics", entry.Headline);
        Assert.Contains("LRCLIB", entry.Detail);
    }

    [Fact]
    public async Task Lyrics_SaysWhichSourceTheWordsCameFrom()
    {
        using var db = NewContext();
        void Fetched(int n, string album, LyricsOrigin origin)
        {
            var song = AddSong(db, n, $"Track {n}", album);
            song.ApplyLyricsResult(null, "words", false, origin: origin, geniusSongId: origin == LyricsOrigin.Genius ? n : null);
            song.LyricsLastAttemptedAtUtc = Now.AddHours(-1);
        }
        Fetched(1, "Only LRCLIB", LyricsOrigin.Lrclib);
        Fetched(2, "Only Genius", LyricsOrigin.Genius);
        Fetched(3, "Both", LyricsOrigin.Lrclib);
        Fetched(4, "Both", LyricsOrigin.Genius);
        var missing = AddSong(db, 5, "Track 5", "Nothing");
        missing.MarkLyricsNotFound();
        missing.LyricsLastAttemptedAtUtc = Now.AddHours(-1);
        db.SaveChanges();

        var entries = await new LyricsActivitySource(db).CollectAsync(Window, default);

        string DetailFor(string album) =>
            Assert.Single(entries, e => e.Kind == "lyrics-added" && e.Headline.Contains(album)).Detail!;
        Assert.Equal("From LRCLIB", DetailFor("Only LRCLIB"));
        Assert.Equal("From Genius", DetailFor("Only Genius"));
        Assert.Equal("From LRCLIB and Genius", DetailFor("Both"));
        Assert.Equal(
            "No lyrics found for 1 track of 'Nothing'",
            Assert.Single(entries, e => e.Kind == "lyrics-missing").Headline);
    }

    [Fact]
    public async Task Lyrics_ReportsARepairedTimingWithItsShift()
    {
        using var db = NewContext();
        var song = AddSong(db, 1, "Ghost Town");
        song.LyricsSyncStatus = LyricsSyncStatus.Corrected;
        song.LyricsSyncOffsetMs = 2400;
        song.LyricsSyncCheckedAtUtc = Now.AddHours(-1);
        db.SaveChanges();

        var entry = Assert.Single(
            await new LyricsActivitySource(db).CollectAsync(Window, default),
            e => e.Kind == "lyrics-timing-fixed");
        Assert.Contains("2.4s", entry.Detail);
        Assert.Contains("AI Enhanced", entry.Detail);
    }

    [Fact]
    public async Task Lyrics_LabelsAiGeneratedApartFromAiEnhanced()
    {
        using var db = NewContext();
        var generated = AddSong(db, 1, "Nobody wrote these down");
        generated.TranscriptionStatus = TranscriptionStatus.Completed;
        generated.TranscribedSyncedLyrics = "[00:01.00]words the AI heard";
        generated.TranscribedAtUtc = Now.AddHours(-2);
        generated.TranscriptionModel = "whisper-1";
        var enhanced = AddSong(db, 2, "Official words, re-timed");
        enhanced.TranscriptionStatus = TranscriptionStatus.Completed;
        enhanced.TranscribedAtUtc = Now.AddHours(-1);
        enhanced.TranscriptionAlignedToReference = true;
        enhanced.TranscribedSyncedLyrics = "[00:01.00]official words";
        db.SaveChanges();

        var entries = await new LyricsActivitySource(db).CollectAsync(Window, default);

        Assert.Contains("AI Generated", Assert.Single(entries, e => e.Kind == "lyrics-transcribed").Detail);
        Assert.Contains("AI Enhanced", Assert.Single(entries, e => e.Kind == "lyrics-realigned").Detail);
    }

    [Fact]
    public async Task Lyrics_ReportsATrackTheAiHeardNoSingingInAsInstrumental()
    {
        using var db = NewContext();
        var song = AddSong(db, 1, "Interlude");
        song.LyricsStatus = LyricsStatus.NotFound;
        song.ApplyHeardNoSinging("whisper-large-v3");
        song.TranscribedAtUtc = Now.AddHours(-1);
        db.SaveChanges();

        var entries = await new LyricsActivitySource(db).CollectAsync(Window, default);

        var entry = Assert.Single(entries, e => e.Kind == "lyrics-instrumental");
        Assert.Contains("no singing", entry.Headline);
        Assert.Contains("Marked instrumental by AI", entry.Detail);
        Assert.DoesNotContain(entries, e => e.Kind is "lyrics-transcribed" or "lyrics-transcription-failed");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lyrics_SaysNothingOfAnAiInstrumentalMarkThatWasLaterLifted(bool lrclibConfirmed)
    {
        using var db = NewContext();
        var song = AddSong(db, 1, "Interlude");
        song.LyricsStatus = LyricsStatus.NotFound;
        song.ApplyHeardNoSinging("whisper-large-v3");
        song.TranscribedAtUtc = Now.AddHours(-1);
        if (lrclibConfirmed)
            song.ApplyLyricsResult(null, null, instrumental: true);
        else
            song.ApplyLyricsResult(null, "official words", instrumental: false);
        db.SaveChanges();

        var entries = await new LyricsActivitySource(db).CollectAsync(Window, default);

        Assert.DoesNotContain(entries, e => e.Kind is "lyrics-transcribed" or "lyrics-realigned"
            || (e.Kind == "lyrics-instrumental" && e.Headline.Contains("no singing")));
    }

    // --- Music video -------------------------------------------------------------------------

    [Fact]
    public async Task MusicVideo_SaysWhichClipItChoseAndHowItLinedItUp()
    {
        using var db = NewContext();
        var searched = AddSong(db, 1, "Went West");
        var ownUpload = AddSong(db, 2, "Straight from the source");
        db.SongMusicVideos.AddRange(
            new SongMusicVideo
            {
                SongId = searched.Id, Status = MusicVideoStatus.Ready, FetchedAtUtc = Now.AddHours(-2),
                SyncSource = MusicVideoSyncSource.AutoAligned, SyncOffsetMs = 3200, YouTubeVideoId = "abc123",
            },
            new SongMusicVideo
            {
                SongId = ownUpload.Id, Status = MusicVideoStatus.Ready, FetchedAtUtc = Now.AddHours(-1),
                SyncSource = MusicVideoSyncSource.SameSource, YouTubeVideoId = "def456",
            });
        db.SaveChanges();

        var entries = await new MusicVideoActivitySource(db).CollectAsync(Window, default);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.SongId == searched.Id && e.Detail!.Contains("searching"));
        Assert.Contains(entries, e => e.SongId == ownUpload.Id && e.Detail!.Contains("own source upload"));
    }

    [Fact]
    public async Task MusicVideo_SaysWhenNobodyChoseTheClip()
    {
        using var db = NewContext();
        var picked = AddSong(db, 1, "Picked for you");
        var chosen = AddSong(db, 2, "Chosen by hand");
        db.SongMusicVideos.AddRange(
            new SongMusicVideo
            {
                SongId = picked.Id, Status = MusicVideoStatus.Ready, FetchedAtUtc = Now.AddHours(-2),
                SyncSource = MusicVideoSyncSource.AutoAligned, SyncOffsetMs = 1000, YouTubeVideoId = "abc123",
                Pick = MusicVideoPick.Automatic,
            },
            new SongMusicVideo
            {
                SongId = chosen.Id, Status = MusicVideoStatus.Ready, FetchedAtUtc = Now.AddHours(-1),
                SyncSource = MusicVideoSyncSource.AutoAligned, SyncOffsetMs = 1000, YouTubeVideoId = "def456",
            });
        db.SaveChanges();

        var entries = await new MusicVideoActivitySource(db).CollectAsync(Window, default);

        Assert.StartsWith(MusicVideoActivitySource.PickedAutomatically, entries.Single(e => e.SongId == picked.Id).Detail);
        Assert.DoesNotContain(MusicVideoActivitySource.PickedAutomatically, entries.Single(e => e.SongId == chosen.Id).Detail);
    }

    [Fact]
    public async Task MusicVideo_IgnoresAFetchStillInFlight()
    {
        using var db = NewContext();
        var song = AddSong(db, 1, "Downloading right now");
        db.SongMusicVideos.Add(new SongMusicVideo
        {
            SongId = song.Id, Status = MusicVideoStatus.Fetching, FetchedAtUtc = Now.AddMinutes(-2),
        });
        db.SaveChanges();

        Assert.Empty(await new MusicVideoActivitySource(db).CollectAsync(Window, default));
    }

    // --- Listening ---------------------------------------------------------------------------

    [Fact]
    public async Task Listening_RollsADaysLikesIntoOneEntry()
    {
        using var db = NewContext();
        for (var i = 1; i <= 3; i++)
        {
            var song = AddSong(db, i, $"Liked {i}");
            song.LikedAtUtc = Now.AddHours(-i);
        }
        db.SaveChanges();

        var entry = Assert.Single(
            await new ListeningActivitySource(db).CollectAsync(Window, default),
            e => e.Kind == "liked");
        Assert.Equal(3, entry.TrackCount);
        Assert.Contains("3 tracks", entry.Headline);
    }

    // --- Sync --------------------------------------------------------------------------------

    [Fact]
    public async Task Sync_SeparatesPushesFromFailures()
    {
        using var db = NewContext();
        var pushed = AddSong(db, 1, "Pushed");
        var broke = AddSong(db, 2, "Broke");
        db.TrackSyncStates.AddRange(
            new TrackSyncState
            {
                SongId = pushed.Id, Status = TrackSyncStatus.Synced,
                CreatedAtUtc = Now.AddHours(-3), UpdatedAtUtc = Now.AddHours(-2),
            },
            new TrackSyncState
            {
                SongId = broke.Id, Status = TrackSyncStatus.Failed, LastError = "connection reset",
                CreatedAtUtc = Now.AddHours(-3), UpdatedAtUtc = Now.AddHours(-1),
            });
        db.SaveChanges();

        var entries = await new SyncActivitySource(db).CollectAsync(Window, default);

        Assert.Contains(entries, e => e.Kind == "synced");
        var failed = Assert.Single(entries, e => e.Kind == "sync-failed");
        Assert.Contains("connection reset", failed.Detail);
    }

    // --- Curation ----------------------------------------------------------------------------

    [Fact]
    public async Task Curation_ReportsRemovalsGradesAndDuplicates()
    {
        using var db = NewContext();
        var gone = AddSong(db, 1, "Vanished");
        gone.DeletedAtUtc = Now.AddHours(-3);
        var graded = AddSong(db, 2, "Graded");
        db.SongQualityGrades.Add(new SongQualityGrade
        {
            SongId = graded.Id, OwnerUserId = Owner, Score = 91,
            Verdict = SongQualityVerdict.Good, PromptVersion = 2, GradedAtUtc = Now.AddHours(-2),
        });
        db.SongDuplicateLinks.Add(new SongDuplicateLink
        {
            OwnerUserId = Owner, SongIdLow = gone.Id, SongIdHigh = graded.Id,
            Status = DuplicateLinkStatus.Active, DetectedAtUtc = Now.AddHours(-1),
        });
        db.SaveChanges();

        var entries = await new CurationActivitySource(db, new NoDedupHistory()).CollectAsync(Window, default);

        Assert.Contains(entries, e => e.Kind == "track-removed");
        Assert.Contains(entries, e => e.Kind == "graded");
        Assert.Contains(entries, e => e.Kind == "duplicates-found");
        Assert.All(entries, e => Assert.Equal(ActivityCategory.Curation, e.Category));
    }

    [Fact]
    public async Task Curation_SurfacesDedupActionsFromTheInboxsOwnHistory()
    {
        using var db = NewContext();
        var history = new StubDedupHistory(new DedupActionSummary(
            "artist-merge", Now.AddHours(-1), Now.AddHours(-1).Ticks, 348, 348,
            ["AlbumArtist → 'Kanye West' (348 songs)"], Reverted: false, Revertible: true));

        var entry = Assert.Single(
            await new CurationActivitySource(db, history).CollectAsync(Window, default),
            e => e.Kind == "artists-merged");
        Assert.Contains("348 tracks", entry.Headline);
        Assert.Contains("Kanye West", entry.Detail);
    }

    // --- Pipeline ----------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_SummarisesAScanByWhatItGotThrough()
    {
        using var db = NewContext();
        db.IngestRuns.Add(NewRun(IngestRunStatus.Completed, newTracks: 24, newlyBuilt: 18, newReview: 4, label: "auto-scan",
            totalDiscovered: 9000));
        db.SaveChanges();

        var entry = Assert.Single(await new PipelineActivitySource(db).CollectAsync(Window, default));
        Assert.Equal("scan-completed", entry.Kind);
        Assert.Contains("24 new tracks", entry.Headline);
        Assert.DoesNotContain("9000", entry.Headline);
        Assert.Contains("18 built", entry.Detail);
        Assert.Contains("4 to review", entry.Detail);
        Assert.Contains("auto-scan", entry.Detail);
    }

    [Fact]
    public async Task Pipeline_LeavesOutAQuietCompletedRun()
    {
        using var db = NewContext();
        db.IngestRuns.Add(NewRun(IngestRunStatus.Completed, newTracks: 0, newlyBuilt: 40, newReview: 0, totalDiscovered: 9000));
        db.SaveChanges();

        Assert.Empty(await new PipelineActivitySource(db).CollectAsync(Window, default));
    }

    [Fact]
    public async Task Pipeline_LeavesOutALegacyRunThatOnlyKeptTotals()
    {
        using var db = NewContext();
        db.IngestRuns.Add(NewRun(IngestRunStatus.Completed, newTracks: null, newlyBuilt: null, newReview: null, totalDiscovered: 9000));
        db.SaveChanges();

        Assert.Empty(await new PipelineActivitySource(db).CollectAsync(Window, default));
    }

    [Fact]
    public async Task Pipeline_KeepsARetryRunThatOnlyAddedReviews()
    {
        using var db = NewContext();
        db.IngestRuns.Add(NewRun(IngestRunStatus.Completed, newTracks: 0, newlyBuilt: 0, newReview: 3));
        db.SaveChanges();

        var entry = Assert.Single(await new PipelineActivitySource(db).CollectAsync(Window, default));
        Assert.Equal("Pipeline run finished", entry.Headline);
        Assert.Contains("3 to review", entry.Detail);
    }

    [Fact]
    public async Task Pipeline_KeepsAFailedRunEvenWhenNothingChanged()
    {
        using var db = NewContext();
        db.IngestRuns.Add(NewRun(IngestRunStatus.Failed, newTracks: 0, newlyBuilt: 0, newReview: 0));
        db.SaveChanges();

        var entry = Assert.Single(await new PipelineActivitySource(db).CollectAsync(Window, default));
        Assert.Equal("scan-failed", entry.Kind);
    }

    private static IngestRun NewRun(
        IngestRunStatus status, int? newTracks, int? newlyBuilt, int? newReview, string? label = null,
        int totalDiscovered = 0) => new()
    {
        Id = Guid.NewGuid(), OwnerUserId = Owner, SourcePath = "/src", DestinationPath = "/dest",
        StartedAtUtc = Now.AddHours(-2), EndedAtUtc = Now.AddHours(-1), Status = status,
        TracksDiscovered = totalDiscovered, TriggerLabel = label,
        NewTracks = newTracks, NewlyBuilt = newlyBuilt, NewReview = newReview, NewFailed = newTracks is null ? null : 0,
    };

    [Fact]
    public async Task Pipeline_ExplainsAnUpdateAndASettingsChangeFromSnapshots()
    {
        using var db = NewContext();
        db.EnrichmentSnapshots.AddRange(
            NewSnapshot(Now.AddHours(-5), "2.3.1", "hash-a", """{"providers":{"appleMusic":false}}"""),
            NewSnapshot(Now.AddHours(-2), "2.4.0", "hash-b", """{"providers":{"appleMusic":true}}"""));
        db.SaveChanges();

        var entries = await new PipelineActivitySource(db).CollectAsync(Window, default);

        var version = Assert.Single(entries, e => e.Kind == "version-changed");
        Assert.Contains("2.4.0", version.Headline);
        Assert.Contains("2.3.1", version.Detail);
        var settings = Assert.Single(entries, e => e.Kind == "settings-changed");
        Assert.Contains("appleMusic", settings.Headline);
        Assert.Contains("True", settings.Headline);
    }

    [Fact]
    public async Task Pipeline_SaysNothingWhenTheConfigDidNotMove()
    {
        using var db = NewContext();
        db.EnrichmentSnapshots.AddRange(
            NewSnapshot(Now.AddHours(-5), "2.4.0", "hash-a", """{"providers":{"appleMusic":true}}"""),
            NewSnapshot(Now.AddHours(-2), "2.4.0", "hash-a", """{"providers":{"appleMusic":true}}"""));
        db.SaveChanges();

        Assert.Empty(await new PipelineActivitySource(db).CollectAsync(Window, default));
    }

    private static EnrichmentSnapshot NewSnapshot(DateTime at, string version, string hash, string configJson) => new()
    {
        OwnerUserId = Owner, CapturedAtUtc = at, Trigger = SnapshotTrigger.PipelineRun,
        Version = version, ConfigHash = hash, ConfigJson = configJson,
    };

    // --- Artwork -----------------------------------------------------------------------------

    [Fact]
    public async Task Artwork_OnlyReportsCoverFailuresForFoldersThisOwnerWroteTo()
    {
        using var db = NewContext();
        db.AlbumCoverFetchAttempts.AddRange(
            new AlbumCoverFetchAttempt
            {
                AlbumFolder = "/dest/Artist/Mine", Status = AlbumCoverFetchStatus.NotFound,
                AttemptCount = 3, LastAttemptAtUtc = Now.AddHours(-1),
            },
            // Catalog-style table with no owner column — a folder this owner never wrote to must not leak.
            new AlbumCoverFetchAttempt
            {
                AlbumFolder = "/dest/Somebody/Else", Status = AlbumCoverFetchStatus.NotFound,
                AttemptCount = 1, LastAttemptAtUtc = Now.AddHours(-1),
            });
        db.LibraryWriteEvents.Add(new LibraryWriteEvent
        {
            OwnerUserId = Owner, Kind = LibraryWriteEventKind.TrackTagsWritten, WrittenAtUtc = Now.AddHours(-2),
            AlbumFolder = "/dest/Artist/Mine", Album = "Mine", AlbumArtist = "Artist", FieldName = "Title",
        });
        db.SaveChanges();

        var entry = Assert.Single(await new ArtworkActivitySource(db).CollectAsync(Window, default));
        Assert.Equal(1, entry.TrackCount);
        Assert.Contains("Mine", entry.Headline);
    }

    [Fact]
    public async Task Artwork_IgnoresUpgradeCooldownsForFoldersThatAlreadyHaveACover()
    {
        using var db = NewContext();
        db.AlbumCoverFetchAttempts.AddRange(
            new AlbumCoverFetchAttempt
            {
                AlbumFolder = "/dest/Artist/CaaNotOnSpotify", Status = AlbumCoverFetchStatus.UpgradeNotFound,
                AttemptCount = 1, LastAttemptAtUtc = Now.AddHours(-1),
            },
            new AlbumCoverFetchAttempt
            {
                AlbumFolder = "/dest/Artist/CaaRateLimited", Status = AlbumCoverFetchStatus.UpgradeFailed,
                AttemptCount = 1, LastAttemptAtUtc = Now.AddHours(-1),
            });
        db.LibraryWriteEvents.AddRange(
            new LibraryWriteEvent
            {
                OwnerUserId = Owner, Kind = LibraryWriteEventKind.AlbumCoverWritten, WrittenAtUtc = Now.AddHours(-2),
                AlbumFolder = "/dest/Artist/CaaNotOnSpotify", Album = "CaaNotOnSpotify", AlbumArtist = "Artist",
                FieldName = "Cover", NewValue = "fetched:coverartarchive",
            },
            new LibraryWriteEvent
            {
                OwnerUserId = Owner, Kind = LibraryWriteEventKind.AlbumCoverWritten, WrittenAtUtc = Now.AddHours(-2),
                AlbumFolder = "/dest/Artist/CaaRateLimited", Album = "CaaRateLimited", AlbumArtist = "Artist",
                FieldName = "Cover", NewValue = "fetched:coverartarchive",
            });
        db.SaveChanges();

        Assert.Empty(await new ArtworkActivitySource(db).CollectAsync(Window, default));
    }

    // --- Library writes ----------------------------------------------------------------------

    [Fact]
    public async Task LibraryWrites_AnnounceATrackReachingTheDestination()
    {
        using var db = NewContext();
        var song = AddSong(db, 1, "Built");
        song.LibraryBuiltAtUtc = Now.AddHours(-1);
        db.SaveChanges();

        var entry = Assert.Single(
            await new LibraryWriteActivitySource(db).CollectAsync(Window, default),
            e => e.Kind == "built");
        Assert.Contains("Built 1 track", entry.Headline);
        Assert.Equal(ActivityCategory.Written, entry.Category);
    }

    private sealed class NoDedupHistory : IDedupActionHistory
    {
        public Task<IReadOnlyList<DedupActionSummary>> ListAsync(int take = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DedupActionSummary>>([]);

        public Task<DedupActionRevertResult> RevertAsync(
            Guid ownerUserId, string source, long batchTicks, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubDedupHistory(params DedupActionSummary[] actions) : IDedupActionHistory
    {
        public Task<IReadOnlyList<DedupActionSummary>> ListAsync(int take = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DedupActionSummary>>(actions);

        public Task<DedupActionRevertResult> RevertAsync(
            Guid ownerUserId, string source, long batchTicks, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
