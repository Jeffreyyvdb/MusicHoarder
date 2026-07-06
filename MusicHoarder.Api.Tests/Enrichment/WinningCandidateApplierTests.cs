using System.Text.Json;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class WinningCandidateApplierTests
{
    [Fact]
    public void TryApply_AppliesCandidateFromMatchedDataJson()
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

        var applied = WinningCandidateApplier.TryApply(song);

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
    public void TryApply_CarriesEveryCandidateFieldOntoTheSong()
    {
        // Characterization test: pins the FULL provider-candidate -> song field mapping (the
        // hand-written ~24-field projection), not just the handful of fields the other tests touch.
        // A field added to the twin records but forgotten in the projection would break this test.
        var song = NewSong(artist: "Original Artist", title: "Original Title");
        song.MatchedBy = "MusicBrainzWeb";
        song.MatchConfidence = 0.91;
        song.EnrichmentStatus = EnrichmentStatus.NeedsReview;

        var candidate = new EnrichmentProviderResult(
            Artist: "New Artist",
            AlbumArtist: "New Album Artist",
            Title: "New Title",
            Year: 1999,
            TrackNumber: 7,
            MusicBrainzId: "mb-track",
            MusicBrainzReleaseId: "mb-release",
            SpotifyId: "sp-track",
            AcoustIdTrackId: "acoust-track",
            Isrc: "USRC12345678",
            MatchedBy: "MusicBrainzWeb",
            MatchConfidence: 0.91,
            MatchWarnings: ["duration_mismatch", "low_confidence"],
            RecommendedStatus: EnrichmentStatus.NeedsReview,
            Album: "New Album",
            Artists: "New Artist;Guest",
            ArtistMusicBrainzIds: "mb-a1;mb-a2",
            AlbumArtistMusicBrainzId: "mb-albumartist",
            MusicBrainzReleaseGroupId: "mb-rg",
            DiscNumber: 2,
            TotalDiscs: 3,
            TotalTracks: 12,
            IsCompilation: true,
            ReleaseTypePrimary: "album",
            ReleaseTypes: "album; compilation");

        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.MusicBrainzWeb,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
            MatchedDataJson = JsonSerializer.Serialize(candidate),
        });

        var applied = WinningCandidateApplier.TryApply(song);

        Assert.True(applied);
        Assert.Equal("New Artist", song.Artist);
        Assert.Equal("New Album Artist", song.AlbumArtist);
        Assert.Equal("New Title", song.Title);
        Assert.Equal("New Album", song.Album);
        Assert.Equal(1999, song.Year);
        Assert.Equal(7, song.TrackNumber);
        Assert.Equal("New Artist;Guest", song.Artists);
        Assert.Equal("mb-a1;mb-a2", song.ArtistMusicBrainzIds);
        Assert.Equal(2, song.DiscNumber);
        Assert.Equal(3, song.TotalDiscs);
        Assert.Equal(12, song.TotalTracks);
        Assert.True(song.IsCompilation);
        Assert.Equal("album", song.ReleaseTypePrimary);
        Assert.Equal("album; compilation", song.ReleaseTypes);
        Assert.Equal("mb-track", song.MusicBrainzId);
        Assert.Equal("mb-release", song.MusicBrainzReleaseId);
        Assert.Equal("mb-rg", song.MusicBrainzReleaseGroupId);
        Assert.Equal("mb-albumartist", song.AlbumArtistMusicBrainzId);
        Assert.Equal("sp-track", song.SpotifyId);
        Assert.Equal("acoust-track", song.AcoustIdTrackId);
        Assert.Equal("USRC12345678", song.Isrc);
        Assert.Equal("MusicBrainzWeb", song.MatchedBy);
        Assert.Equal(0.91, song.MatchConfidence);
        // Winning-candidate application always promotes the row to Matched regardless of the
        // candidate's own RecommendedStatus.
        Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
        Assert.NotNull(song.MatchWarnings);
        Assert.Contains("duration_mismatch", song.MatchWarnings);
        Assert.Contains("low_confidence", song.MatchWarnings);
    }

    [Fact]
    public void TryApply_NoMatchedBy_ReturnsFalse()
    {
        var song = NewSong();
        song.MatchedBy = null;

        Assert.False(WinningCandidateApplier.TryApply(song));
    }

    [Fact]
    public void TryApply_NoMatchingAttempt_ReturnsFalse()
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

        Assert.False(WinningCandidateApplier.TryApply(song));
    }

    [Fact]
    public void TryApply_AttemptHasNoJson_ReturnsFalse()
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

        Assert.False(WinningCandidateApplier.TryApply(song));
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
