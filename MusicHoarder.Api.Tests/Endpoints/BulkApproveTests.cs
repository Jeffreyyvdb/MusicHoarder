using System.Text.Json;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class BulkApproveTests
{
    [Fact]
    public void TryApplyWinningCandidate_AppliesCandidateFromMatchedDataJson()
    {
        // Tier 1 changed the orchestrator to no longer write a NeedsReview candidate's
        // Artist/Title/Album onto the song row — those values now live only on the
        // SongProviderAttempt's MatchedDataJson. BulkApprove must read them from there.
        var song = NewSong(artist: "EsDeeKid", title: "4 Raws");
        song.MatchedBy = "SpotifyAPI";
        song.MatchConfidence = 0.78;
        song.EnrichmentStatus = EnrichmentStatus.NeedsReview;

        var candidate = new EnrichmentProviderResult(
            Artist: "EsDeeKid",
            AlbumArtist: "EsDeeKid",
            Title: "4 Raws",
            Year: 2017,
            TrackNumber: 1,
            MusicBrainzId: null,
            MusicBrainzReleaseId: null,
            SpotifyId: "spotify-real-track",
            AcoustIdTrackId: null,
            Isrc: "GBCEL0700066",
            MatchedBy: "SpotifyAPI",
            MatchConfidence: 0.78,
            MatchWarnings: ["duration_mismatch"],
            RecommendedStatus: EnrichmentStatus.NeedsReview,
            Album: "Rebel");

        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.SpotifyAPI,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
            MatchedDataJson = JsonSerializer.Serialize(candidate),
        });

        var applied = SongsEndpoints.TryApplyWinningCandidate(song);

        Assert.True(applied);
        Assert.Equal("EsDeeKid", song.Artist);
        Assert.Equal("4 Raws", song.Title);
        Assert.Equal("Rebel", song.Album);
        Assert.Equal("spotify-real-track", song.SpotifyId);
        Assert.Equal("GBCEL0700066", song.Isrc);
        Assert.Equal(2017, song.Year);
        Assert.True(song.OriginalMetadataCaptured);
        Assert.NotNull(song.MatchWarnings);
        Assert.Contains("duration_mismatch", song.MatchWarnings);
    }

    [Fact]
    public void TryApplyWinningCandidate_NoMatchedBy_ReturnsFalse()
    {
        var song = NewSong();
        song.MatchedBy = null;

        Assert.False(SongsEndpoints.TryApplyWinningCandidate(song));
    }

    [Fact]
    public void TryApplyWinningCandidate_NoMatchingAttempt_ReturnsFalse()
    {
        var song = NewSong();
        song.MatchedBy = "SpotifyAPI";
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
            MatchedDataJson = "{}",
        });

        Assert.False(SongsEndpoints.TryApplyWinningCandidate(song));
    }

    [Fact]
    public void TryApplyWinningCandidate_AttemptHasNoJson_ReturnsFalse()
    {
        var song = NewSong();
        song.MatchedBy = "SpotifyAPI";
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.SpotifyAPI,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
            MatchedDataJson = null,
        });

        Assert.False(SongsEndpoints.TryApplyWinningCandidate(song));
    }

    private static SongMetadata NewSong(string artist = "Original Artist", string title = "Original Title")
    {
        return new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/x.mp3",
            FileName = "x.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = artist,
            Title = title,
            EnrichmentStatus = EnrichmentStatus.NeedsReview,
        };
    }
}
