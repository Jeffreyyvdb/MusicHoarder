using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class ReleaseSelectorTests
{
    [Fact]
    public void OriginalAlbumCorroboratedByTwo_BeatsLoneCompilation()
    {
        // Song 1364 "Ready or Not": Deezer attributes the recording to a "Greatest Hits" compilation,
        // but Spotify + Apple both say the original "The Score". Field corroboration alone fixes it.
        var cluster = new[]
        {
            Cand("Deezer", album: "Greatest Hits", year: 2000, track: 3),
            Cand("SpotifyAPI", album: "The Score (Expanded Edition)", year: 1996, track: 3),
            Cand("AppleMusic", album: "The Score", year: 1996, track: 3),
        };

        var r = ReleaseSelector.Select(cluster, embeddedAlbum: "The Score (Expanded Edition)", embeddedYear: 1996, preferOriginalRelease: true);

        // The curated edition string is kept (the file's tag matched the winning release).
        Assert.Equal("The Score (Expanded Edition)", r.Album);
        Assert.Equal(1996, r.Year);
        Assert.Equal(3, r.TrackNumber);
        Assert.Contains("Album", r.CorroboratedFields);
        Assert.Contains("Year", r.CorroboratedFields);
        Assert.Contains("TrackNumber", r.CorroboratedFields);
    }

    [Fact]
    public void CompilationCorroboratedButYearSplit_YearNotCorroborated()
    {
        // Song 1374 "No Woman, No Cry" WITHOUT album-aware search (Part B): two providers point at the
        // compilation, but they disagree on its year (2000 vs 2003). The album is corroborated, but the
        // contradictory year is NOT — so the merger will keep the embedded 1996 instead of a bogus year.
        var cluster = new[]
        {
            Cand("Deezer", album: "Greatest Hits", year: 2000, track: 2),
            Cand("SpotifyAPI", album: "Greatest Hits", year: 2003, track: 6),
            Cand("AppleMusic", album: "The Score (Expanded Edition)", year: 1996, track: 12),
        };

        var r = ReleaseSelector.Select(cluster, embeddedAlbum: "The Score (Expanded Edition)", embeddedYear: 1996, preferOriginalRelease: true);

        Assert.Equal("Greatest Hits", r.Album);
        Assert.Contains("Album", r.CorroboratedFields);
        Assert.DoesNotContain("Year", r.CorroboratedFields);
        Assert.DoesNotContain("TrackNumber", r.CorroboratedFields);
    }

    [Fact]
    public void AlbumAwareSearchFlipsDeezer_OriginalNowCorroborated()
    {
        // Song 1374 WITH album-aware search (Part B): Deezer now returns the original album too, so two
        // providers corroborate "The Score" / 1996 and it wins outright.
        var cluster = new[]
        {
            Cand("Deezer", album: "The Score", year: 1996, track: 12),
            Cand("SpotifyAPI", album: "Greatest Hits", year: 2003, track: 6),
            Cand("AppleMusic", album: "The Score (Expanded Edition)", year: 1996, track: 12),
        };

        var r = ReleaseSelector.Select(cluster, embeddedAlbum: "The Score (Expanded Edition)", embeddedYear: 1996, preferOriginalRelease: true);

        Assert.Equal("The Score (Expanded Edition)", r.Album);
        Assert.Equal(1996, r.Year);
        Assert.Contains("Album", r.CorroboratedFields);
        Assert.Contains("Year", r.CorroboratedFields);
    }

    [Fact]
    public void GenuineCompilationAgreedByAll_IsKept_NotForcedToPhantomOriginal()
    {
        // A track that really only lives on the compilation: every provider agrees. Don't invent an
        // "original" — the corroborated compilation stands.
        var cluster = new[]
        {
            Cand("Deezer", album: "Now That's Music 50", year: 2015, track: 4),
            Cand("SpotifyAPI", album: "Now That's Music 50", year: 2015, track: 4),
            Cand("AppleMusic", album: "Now That's Music 50", year: 2015, track: 4),
        };

        var r = ReleaseSelector.Select(cluster, embeddedAlbum: null, embeddedYear: null, preferOriginalRelease: true);

        Assert.Equal("Now That's Music 50", r.Album);
        Assert.Equal(2015, r.Year);
        Assert.Contains("Album", r.CorroboratedFields);
        Assert.Contains("Year", r.CorroboratedFields);
    }

    [Fact]
    public void TieBetweenReleases_PrefersOriginalOverCompilation()
    {
        // One provider each, so the vote is tied — the original-release preference breaks it toward the
        // non-compilation, earlier pressing. Neither is corroborated (1 vote), so it's a tiebreak only.
        var cluster = new[]
        {
            Cand("Deezer", album: "The Score", year: 1996, track: 3, isCompilation: null),
            Cand("SpotifyAPI", album: "Greatest Hits", year: 2003, track: 6, isCompilation: true),
        };

        var withPreference = ReleaseSelector.Select(cluster, embeddedAlbum: null, embeddedYear: null, preferOriginalRelease: true);
        Assert.Equal("The Score", withPreference.Album);
        Assert.DoesNotContain("Album", withPreference.CorroboratedFields); // only one provider backed it
    }

    [Fact]
    public void EmptyCluster_ReturnsEmptySelection()
    {
        var cluster = new[] { Cand("Deezer", album: null, year: null, track: null) };

        var r = ReleaseSelector.Select(cluster, embeddedAlbum: "X", embeddedYear: 2000, preferOriginalRelease: true);

        Assert.Null(r.Album);
        Assert.Null(r.Year);
        Assert.Empty(r.CorroboratedFields);
    }

    private static EnrichmentProviderResult Cand(
        string matchedBy, string? album, int? year, int? track, bool? isCompilation = null)
        => new(
            Artist: "Fugees", AlbumArtist: "Fugees", Title: "Ready or Not",
            Year: year, TrackNumber: track,
            MusicBrainzId: null, MusicBrainzReleaseId: null, SpotifyId: null, AcoustIdTrackId: null, Isrc: null,
            MatchedBy: matchedBy, MatchConfidence: 0.99, MatchWarnings: [], RecommendedStatus: EnrichmentStatus.Matched,
            Album: album, IsCompilation: isCompilation);
}
