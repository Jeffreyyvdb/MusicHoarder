using System.Text.Json;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class QualityDossierFactoryTests
{
    private sealed class StubResolver(string path) : IDestinationPathResolver
    {
        public string ResolvePath(SongMetadata song) => path;
    }

    private sealed class ThrowingResolver : IDestinationPathResolver
    {
        public string ResolvePath(SongMetadata song) => throw new InvalidOperationException("missing metadata");
    }

    private static SongMetadata BaseSong() => new()
    {
        Id = 7,
        OwnerUserId = Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = "/root/music/Juice WRLD/Loose downloads discord/Juice - Benjamin.mp3",
        FileName = "Juice - Benjamin.mp3",
        Extension = ".mp3",
        FileSizeBytes = 5_800_000,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public void Build_UsesOriginalAsEmbedded_WhenCaptured()
    {
        var song = BaseSong();
        song.OriginalMetadataCaptured = true;
        song.OriginalTitle = "Benjamin";
        song.OriginalArtist = "Juice WRLD";
        song.Title = "Blood On My Jeans";   // what enrichment changed it to
        song.Artist = "Juice WRLD";
        song.EnrichmentStatus = EnrichmentStatus.NeedsReview;

        var factory = new QualityDossierFactory(new StubResolver("/dest/x.mp3"));
        var dossier = factory.Build(song, []);

        Assert.Equal("Benjamin", dossier.EmbeddedTags.Title);
        Assert.Equal("Blood On My Jeans", dossier.CurrentMetadata.Title);
        Assert.Equal("NeedsReview", dossier.Enrichment.Status);
    }

    [Fact]
    public void Build_PrefersCommittedDestinationPath_OverResolver()
    {
        var song = BaseSong();
        song.DestinationPath = "/committed/path.mp3";

        var factory = new QualityDossierFactory(new StubResolver("/resolved/other.mp3"));
        var dossier = factory.Build(song, []);

        Assert.Equal("/committed/path.mp3", dossier.DestinationPathPreview);
    }

    [Fact]
    public void Build_NullDestination_WhenResolverThrows()
    {
        var song = BaseSong();

        var factory = new QualityDossierFactory(new ThrowingResolver());
        var dossier = factory.Build(song, []);

        Assert.Null(dossier.DestinationPathPreview);
    }

    [Fact]
    public void Build_ProjectsProviderAttemptsAndCandidates()
    {
        var song = BaseSong();
        var candidate = new EnrichmentProviderResult(
            Artist: "Juice WRLD", AlbumArtist: "Juice WRLD", Title: "Benjamin",
            Year: 2020, TrackNumber: 1,
            MusicBrainzId: null, MusicBrainzReleaseId: null, SpotifyId: "sp1", AcoustIdTrackId: null,
            Isrc: null, MatchedBy: "SpotifyAPI", MatchConfidence: 0.64,
            MatchWarnings: ["duration_mismatch"], RecommendedStatus: EnrichmentStatus.NeedsReview,
            Album: "Singles");
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.SpotifyAPI,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
            MatchedDataJson = JsonSerializer.Serialize(candidate),
        });
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.Deezer,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
            MatchedDataJson = null,
        });

        var factory = new QualityDossierFactory(new StubResolver("/dest/x.mp3"));
        var dossier = factory.Build(song, []);

        Assert.Equal(2, dossier.ProviderAttempts.Count);
        var spotify = dossier.ProviderAttempts.First(a => a.Provider == "SpotifyAPI");
        Assert.NotNull(spotify.Candidate);
        Assert.Equal("Benjamin", spotify.Candidate!.Title);
        Assert.Equal(0.64, spotify.Candidate.MatchConfidence);
        Assert.Contains("duration_mismatch", spotify.Candidate.Warnings);

        var deezer = dossier.ProviderAttempts.First(a => a.Provider == "Deezer");
        Assert.Null(deezer.Candidate);
    }

    [Fact]
    public void Build_ProjectsChangeLog_AppliedAndProposed()
    {
        var song = BaseSong();
        var now = DateTime.UtcNow;
        var changes = new List<SongMetadataChange>
        {
            new() { SongId = song.Id, FieldName = "Title", OldValue = "Benjamin", NewValue = "Blood On My Jeans",
                    Source = "consensus", Confidence = 0.6, CreatedAtUtc = now, AppliedAtUtc = now },
            new() { SongId = song.Id, FieldName = "Album", OldValue = null, NewValue = "Legends Never Die",
                    Source = "SpotifyAPI", Confidence = 0.5, CreatedAtUtc = now }, // proposed (not applied)
        };

        var factory = new QualityDossierFactory(new StubResolver("/dest/x.mp3"));
        var dossier = factory.Build(song, changes);

        Assert.Equal(2, dossier.ChangeLog.Count);
        var applied = dossier.ChangeLog.First(c => c.Field == "Title");
        Assert.True(applied.Applied);
        Assert.False(applied.Proposed);
        var proposed = dossier.ChangeLog.First(c => c.Field == "Album");
        Assert.False(proposed.Applied);
        Assert.True(proposed.Proposed);
    }
}
