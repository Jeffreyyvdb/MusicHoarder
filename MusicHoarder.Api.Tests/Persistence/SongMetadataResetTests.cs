using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

public class SongMetadataResetTests
{
    [Fact]
    public void ResetPostFingerprint_PreservesScanAndFingerprintFields()
    {
        var song = BuildFullyPopulatedSong();

        song.ResetPostFingerprint();

        Assert.Equal("/src/track.mp3", song.SourcePath);
        Assert.Equal("track.mp3", song.FileName);
        Assert.Equal(".mp3", song.Extension);
        Assert.Equal(1234L, song.FileSizeBytes);
        Assert.Equal("abc123fingerprint", song.Fingerprint);
        Assert.Equal(180, song.DurationSeconds);
        Assert.Equal(180_000, song.DurationMs);
        Assert.Equal(320, song.Bitrate);
        Assert.Equal(new DateTime(2026, 1, 1), song.IndexedAtUtc);
    }

    [Fact]
    public void ResetPostFingerprint_ClearsAllDownstreamState()
    {
        var song = BuildFullyPopulatedSong();

        song.ResetPostFingerprint();

        Assert.Equal(EnrichmentStatus.Pending, song.EnrichmentStatus);
        Assert.Null(song.MatchedBy);
        Assert.Null(song.MatchConfidence);
        Assert.Null(song.MatchWarnings);
        Assert.Null(song.EnrichedAtUtc);
        Assert.Null(song.EnrichmentLastAttemptedAtUtc);
        Assert.Null(song.EnrichmentError);
        Assert.Null(song.AcoustIdTrackId);
        Assert.Null(song.MusicBrainzReleaseId);
        Assert.Empty(song.ProviderAttempts);

        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        Assert.Null(song.LibraryBuiltAtUtc);
        Assert.Null(song.LibraryBuildLastAttemptedAtUtc);
        Assert.Null(song.LibraryBuildError);
        Assert.Null(song.DestinationPath);
        Assert.Null(song.PreviousDestinationPath);

        Assert.Equal(LyricsStatus.NotFetched, song.LyricsStatus);
        Assert.Null(song.PlainLyrics);
        Assert.Null(song.SyncedLyrics);
        Assert.Null(song.IsInstrumental);
        Assert.Null(song.LrclibId);

        Assert.False(song.IsDuplicate);
        Assert.Null(song.DuplicateOfId);
        Assert.False(song.IsUnreleased);
    }

    [Fact]
    public void ResetPostFingerprint_RestoresOriginalMetadataSnapshot()
    {
        var song = BuildFullyPopulatedSong();

        song.ResetPostFingerprint();

        Assert.Equal("Original Artist", song.Artist);
        Assert.Equal("Original Album", song.Album);
        Assert.Equal("Original Title", song.Title);
        Assert.Equal(2020, song.Year);
        Assert.Equal(5, song.TrackNumber);
        Assert.Equal("ISRC-ORIG", song.Isrc);
    }

    [Fact]
    public void ResetPostFingerprint_DoesNotTouchSoftDelete()
    {
        var song = BuildFullyPopulatedSong();
        song.SoftDelete();
        var deletedAt = song.DeletedAtUtc;

        song.ResetPostFingerprint();

        Assert.Equal(deletedAt, song.DeletedAtUtc);
        Assert.True(song.IsDeleted);
    }

    [Fact]
    public void RequeueForRetag_ResetsBuildStatus_ButKeepsDestinationAndEnrichment()
    {
        var song = BuildFullyPopulatedSong(); // Done, with a DestinationPath and applied enrichment
        var addedAt = song.LibraryBuiltAtUtc;
        Assert.NotNull(addedAt);

        song.RequeueForRetag();

        // Re-queued for the builder, with PreviousDestinationPath pointed at the (kept) destination —
        // the signal that forces the build past its same-size skip-copy fast path...
        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        // ...but the "added to library" time is PRESERVED: a re-tag is the same track rewritten in
        // place, not a new addition, so it must not resurface the album in "recently added" views.
        Assert.Equal(addedAt, song.LibraryBuiltAtUtc);
        Assert.Null(song.LibraryBuildLastAttemptedAtUtc);
        Assert.Null(song.LibraryBuildError);

        // ...the destination file stays referenced (re-tag in place; previous == current means no
        // folder move) and the enrichment match is untouched.
        Assert.Equal("/dest/Artist/Album/01 - Track.mp3", song.DestinationPath);
        Assert.Equal("/dest/Artist/Album/01 - Track.mp3", song.PreviousDestinationPath);
        Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
        Assert.Equal("New Album", song.Album);
        Assert.Equal("mbrel-456", song.MusicBrainzReleaseId);
        Assert.NotEmpty(song.ProviderAttempts);
    }

    [Fact]
    public void MarkBuildDone_KeepsFirstAddedTime_AcrossReTagRebuild()
    {
        var song = BuildFullyPopulatedSong();
        var firstAdded = song.LibraryBuiltAtUtc;
        Assert.NotNull(firstAdded);

        // A heal/re-tag cycle: requeue in place, then the builder completes again.
        song.RequeueForRetag();
        song.MarkBuildDone("/dest/Artist/Album/01 - Track.mp3");

        // Still the FIRST build time — MarkBuildDone must not overwrite an existing "added" stamp.
        Assert.Equal(firstAdded, song.LibraryBuiltAtUtc);
    }

    [Fact]
    public void ResetLibraryBuild_ClearsAddedTime_SoAGenuineRebuildCountsAsNewlyAdded()
    {
        var song = BuildFullyPopulatedSong();
        Assert.NotNull(song.LibraryBuiltAtUtc);

        // A real re-add (source upgrade / relocate / re-enrich) clears the stamp; the next build sets
        // a fresh one, so genuinely re-added tracks DO reappear in "recently added".
        song.ResetLibraryBuild();
        Assert.Null(song.LibraryBuiltAtUtc);

        song.MarkBuildDone("/dest/Artist/Album/01 - Track.mp3");
        Assert.NotNull(song.LibraryBuiltAtUtc);
    }

    private static SongMetadata BuildFullyPopulatedSong()
    {
        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/src/track.mp3",
            FileName = "track.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1234L,
            LastModifiedUtc = new DateTime(2026, 1, 1),
            IndexedAtUtc = new DateTime(2026, 1, 1),
            Fingerprint = "abc123fingerprint",
            DurationSeconds = 180,
            DurationMs = 180_000,
            Bitrate = 320,
            Artist = "Original Artist",
            Album = "Original Album",
            Title = "Original Title",
            Year = 2020,
            TrackNumber = 5,
            Isrc = "ISRC-ORIG",
        };

        song.CaptureOriginalMetadata();

        song.ApplyEnrichmentMatch(new EnrichmentMatchData(
            Artist: "New Artist",
            AlbumArtist: "New AlbumArtist",
            Title: "New Title",
            Year: 2024,
            TrackNumber: 7,
            MusicBrainzId: "mb-123",
            MusicBrainzReleaseId: "mbrel-456",
            SpotifyId: "sp-789",
            AcoustIdTrackId: "acid-abc",
            Isrc: "ISRC-NEW",
            MatchedBy: "AcoustID",
            AdjustedScore: 0.95,
            WarningsJson: "[]",
            RecommendedStatus: EnrichmentStatus.Matched,
            Album: "New Album"));

        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
        });

        song.ApplyLyricsResult("[00:00] lyrics", "plain lyrics", instrumental: false, lrclibId: 42);

        song.MarkBuildDone("/dest/Artist/Album/01 - Track.mp3");

        song.MarkAsDuplicate(duplicateOfId: 9999);
        song.IsUnreleased = true;

        return song;
    }
}
