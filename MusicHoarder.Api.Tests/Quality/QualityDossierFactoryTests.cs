using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
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

    private static IOptionsMonitor<QualityGradingOptions> Opts(QualityGradingOptions? value = null) =>
        new TestOptionsMonitor(value ?? new QualityGradingOptions());

    private sealed class TestOptionsMonitor(QualityGradingOptions value) : IOptionsMonitor<QualityGradingOptions>
    {
        public QualityGradingOptions CurrentValue { get; } = value;
        public QualityGradingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<QualityGradingOptions, string?> listener) => null;
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

        var factory = new QualityDossierFactory(new StubResolver("/dest/x.mp3"), Opts());
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

        var factory = new QualityDossierFactory(new StubResolver("/resolved/other.mp3"), Opts());
        var dossier = factory.Build(song, []);

        Assert.Equal("/committed/path.mp3", dossier.DestinationPathPreview);
    }

    [Fact]
    public void Build_NullDestination_WhenResolverThrows()
    {
        var song = BaseSong();

        var factory = new QualityDossierFactory(new ThrowingResolver(), Opts());
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

        var factory = new QualityDossierFactory(new StubResolver("/dest/x.mp3"), Opts());
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

        var factory = new QualityDossierFactory(new StubResolver("/dest/x.mp3"), Opts());
        var dossier = factory.Build(song, changes);

        Assert.Equal(2, dossier.ChangeLog.Count);
        var applied = dossier.ChangeLog.First(c => c.Field == "Title");
        Assert.True(applied.Applied);
        Assert.False(applied.Proposed);
        var proposed = dossier.ChangeLog.First(c => c.Field == "Album");
        Assert.False(proposed.Applied);
        Assert.True(proposed.Proposed);
    }

    [Fact]
    public void Build_CapsChangeLog_KeepingNewest_AndReportsTruncation()
    {
        var song = BaseSong();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var changes = Enumerable.Range(0, 250)
            .Select(i => new SongMetadataChange
            {
                SongId = song.Id,
                FieldName = "AlbumArtist",
                OldValue = $"old-{i}",
                NewValue = $"new-{i}",
                Source = "AlbumSplitHealer",
                Confidence = 0.9,
                CreatedAtUtc = start.AddMinutes(i),
                AppliedAtUtc = start.AddMinutes(i),
            })
            .ToList();

        var factory = new QualityDossierFactory(
            new StubResolver("/dest/x.mp3"),
            Opts(new QualityGradingOptions { MaxChangeLogEntries = 10 }));
        var dossier = factory.Build(song, changes);

        Assert.Equal(10, dossier.ChangeLog.Count);
        Assert.Equal("new-240", dossier.ChangeLog[0].NewValue);   // oldest kept
        Assert.Equal("new-249", dossier.ChangeLog[^1].NewValue);  // newest overall
        Assert.NotNull(dossier.Truncation);
        Assert.Equal(250, dossier.Truncation!.ChangeLogTotal);
        Assert.Equal(10, dossier.Truncation.ChangeLogIncluded);
    }

    [Fact]
    public void Build_ShedsChangeLog_UntilDossierFitsCharBudget()
    {
        var song = BaseSong();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var changes = Enumerable.Range(0, 500)
            .Select(i => new SongMetadataChange
            {
                SongId = song.Id,
                FieldName = "AlbumArtist",
                OldValue = new string('a', 400),
                NewValue = new string('b', 400),
                Source = "AlbumSplitHealer",
                Confidence = 0.9,
                CreatedAtUtc = start.AddMinutes(i),
            })
            .ToList();

        var opts = new QualityGradingOptions { MaxChangeLogEntries = 500, MaxDossierChars = 6000, MaxChangeValueChars = 64 };
        var factory = new QualityDossierFactory(new StubResolver("/dest/x.mp3"), Opts(opts));
        var dossier = factory.Build(song, changes);

        Assert.True(QualityGradingPrompt.SerializeDossier(dossier).Length <= opts.MaxDossierChars);
        Assert.True(dossier.ChangeLog.Count < 500);
        Assert.NotNull(dossier.Truncation);
        Assert.Equal(500, dossier.Truncation!.ChangeLogTotal);
        // Values are elided too, so a single pathological row cannot dominate the payload.
        Assert.All(dossier.ChangeLog, c => Assert.True(c.NewValue!.Length <= opts.MaxChangeValueChars + 1));
    }

    [Fact]
    public void Build_LeavesTruncationNull_WhenChangeLogFits()
    {
        var song = BaseSong();
        var changes = new List<SongMetadataChange>
        {
            new() { SongId = song.Id, FieldName = "Title", OldValue = "a", NewValue = "b",
                    Source = "consensus", Confidence = 0.6, CreatedAtUtc = DateTime.UtcNow },
        };

        var factory = new QualityDossierFactory(new StubResolver("/dest/x.mp3"), Opts());
        var dossier = factory.Build(song, changes);

        Assert.Null(dossier.Truncation);
        Assert.Single(dossier.ChangeLog);
    }
}
