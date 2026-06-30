using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class EnrichmentProviderResultExtensionsTests
{
    [Fact]
    public void ToMatchData_MapsEveryFieldAcrossTheTwinRecords()
    {
        var candidate = new EnrichmentProviderResult(
            Artist: "Artist",
            AlbumArtist: "Album Artist",
            Title: "Title",
            Year: 2010,
            TrackNumber: 4,
            MusicBrainzId: "mb-track",
            MusicBrainzReleaseId: "mb-release",
            SpotifyId: "sp-track",
            AcoustIdTrackId: "acoust-track",
            Isrc: "USRC10000001",
            MatchedBy: "SpotifyAPI",
            MatchConfidence: 0.83,
            MatchWarnings: ["duration_mismatch"],
            RecommendedStatus: EnrichmentStatus.NeedsReview,
            Album: "Album",
            Artists: "Artist;Guest",
            ArtistMusicBrainzIds: "mb-a1;mb-a2",
            AlbumArtistMusicBrainzId: "mb-albumartist",
            MusicBrainzReleaseGroupId: "mb-rg",
            DiscNumber: 1,
            TotalDiscs: 2,
            TotalTracks: 11,
            IsCompilation: true,
            ReleaseTypePrimary: "album",
            ReleaseTypes: "album; compilation");

        var match = candidate.ToMatchData(EnrichmentStatus.Matched);

        Assert.Equal(candidate.Artist, match.Artist);
        Assert.Equal(candidate.AlbumArtist, match.AlbumArtist);
        Assert.Equal(candidate.Title, match.Title);
        Assert.Equal(candidate.Year, match.Year);
        Assert.Equal(candidate.TrackNumber, match.TrackNumber);
        Assert.Equal(candidate.MusicBrainzId, match.MusicBrainzId);
        Assert.Equal(candidate.MusicBrainzReleaseId, match.MusicBrainzReleaseId);
        Assert.Equal(candidate.SpotifyId, match.SpotifyId);
        Assert.Equal(candidate.AcoustIdTrackId, match.AcoustIdTrackId);
        Assert.Equal(candidate.Isrc, match.Isrc);
        Assert.Equal(candidate.MatchedBy, match.MatchedBy);
        Assert.Equal(candidate.Album, match.Album);
        Assert.Equal(candidate.Artists, match.Artists);
        Assert.Equal(candidate.ArtistMusicBrainzIds, match.ArtistMusicBrainzIds);
        Assert.Equal(candidate.AlbumArtistMusicBrainzId, match.AlbumArtistMusicBrainzId);
        Assert.Equal(candidate.MusicBrainzReleaseGroupId, match.MusicBrainzReleaseGroupId);
        Assert.Equal(candidate.DiscNumber, match.DiscNumber);
        Assert.Equal(candidate.TotalDiscs, match.TotalDiscs);
        Assert.Equal(candidate.TotalTracks, match.TotalTracks);
        Assert.Equal(candidate.IsCompilation, match.IsCompilation);
        Assert.Equal(candidate.ReleaseTypePrimary, match.ReleaseTypePrimary);
        Assert.Equal(candidate.ReleaseTypes, match.ReleaseTypes);

        // The three deliberate transforms:
        Assert.Equal(candidate.MatchConfidence, match.AdjustedScore);
        Assert.Equal(EnrichmentStatus.Matched, match.RecommendedStatus);
        Assert.Equal("[\"duration_mismatch\"]", match.WarningsJson);
    }

    [Fact]
    public void ToMatchData_NoWarnings_LeavesWarningsJsonNull()
    {
        var candidate = NewMinimalCandidate(warnings: []);

        var match = candidate.ToMatchData(EnrichmentStatus.Matched);

        Assert.Null(match.WarningsJson);
    }

    [Fact]
    public void ToMatchData_SubstitutesTheRequestedRecommendedStatus()
    {
        var candidate = NewMinimalCandidate(warnings: ["x"]) with { RecommendedStatus = EnrichmentStatus.Failed };

        var match = candidate.ToMatchData(EnrichmentStatus.NeedsReview);

        Assert.Equal(EnrichmentStatus.NeedsReview, match.RecommendedStatus);
    }

    private static EnrichmentProviderResult NewMinimalCandidate(List<string> warnings) =>
        new(
            Artist: "A",
            AlbumArtist: "A",
            Title: "T",
            Year: null,
            TrackNumber: null,
            MusicBrainzId: null,
            MusicBrainzReleaseId: null,
            SpotifyId: null,
            AcoustIdTrackId: null,
            Isrc: null,
            MatchedBy: "SpotifyAPI",
            MatchConfidence: 0.5,
            MatchWarnings: warnings,
            RecommendedStatus: EnrichmentStatus.NeedsReview);
}
