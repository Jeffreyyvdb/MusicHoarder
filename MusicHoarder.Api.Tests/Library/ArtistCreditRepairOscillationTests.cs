using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Artwork;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The credit repair writes AlbumArtist — the field the split-album heal elects and the builder files
/// folders by — so it runs here against both, in the order a build run uses (repair, split heal,
/// build), until nothing changes. A repair the album-level election undoes, or one that undoes the
/// election, would show up as writes that never reach zero (see <see cref="AlbumArtistOscillationTests"/>
/// for the ping-pong that class of bug produced on prod).
/// </summary>
public class ArtistCreditRepairOscillationTests
{
    [Fact]
    public async Task CollabAlbumArtist_LeavesItsOwnFolderOnce_ThenNothingChurns()
    {
        // The incident: two collab tracks of a Hef album were enriched as "Hef met Jayh" (with Hef's
        // id) and built into a "Hef met Jayh" folder of their own; two more had already been through
        // an artist merge that cut their display credit to "Hef". A stale canonical row for the collab
        // spelling and the merge's aliases are still around.
        var fileSystem = SourceFiles(8);
        await using var db = CreateDbContext();
        SeedHefAlbum(db);
        db.CanonicalAlbums.AddRange(
            Canonical("hef", "Hef"),
            Canonical("hef met jayh", "Hef met Jayh"));
        db.ArtistAliases.AddRange(
            new ArtistAlias { OwnerUserId = WellKnownUsers.OwnerId, AliasKey = "hef met jayh", CanonicalName = "Hef" },
            new ArtistAlias { OwnerUserId = WellKnownUsers.OwnerId, AliasKey = "hef", CanonicalName = "Hef" });
        await db.SaveChangesAsync();
        foreach (var song in await db.Songs.IgnoreQueryFilters().ToListAsync())
        {
            fileSystem.AddFile(song.DestinationPath!, new MockFileData("audio-bytes"));
        }

        var tagWriter = new RecordingTagWriter();
        var builder = CreateBuilder(db, fileSystem, tagWriter);

        var repairs = new List<ArtistCreditRepairResult>();
        var heals = new List<AlbumSplitHealResult>();
        var writesPerRound = new List<int>();
        for (var round = 1; round <= 4; round++)
        {
            repairs.Add(await Repairer(db).HealAsync());
            heals.Add(await Healer(db).HealAsync());
            var before = tagWriter.Paths.Count;
            await builder.ProcessNextBatchAsync(Guid.NewGuid());
            writesPerRound.Add(tagWriter.Paths.Count - before);
        }

        // Round one: the two collab album artists become the lead, the two merged credits get their
        // guest back. The moved rows join the lead's album in the same pass, so the split heal
        // converges their divergent year right away instead of re-queuing them a second time.
        Assert.Equal(new ArtistCreditRepairResult(4, 4, 4), repairs[0]);
        Assert.Equal(new AlbumSplitHealResult(1, 2, 0), heals[0]);
        Assert.Equal(4, writesPerRound[0]);

        // Then a fixed point: no repair, no correction, no re-tag.
        Assert.All(repairs.Skip(1), r => Assert.Equal(new ArtistCreditRepairResult(0, 0, 0), r));
        Assert.All(heals.Skip(1), h => Assert.Equal(new AlbumSplitHealResult(0, 0, 0), h));
        Assert.All(writesPerRound.Skip(1), writes => Assert.Equal(0, writes));

        // One album, one folder, and the collab's folder is gone rather than left as a second copy.
        Assert.Equal("/dest/Hef/2020 - Album X", await DistinctFoldersAsync(db));
        Assert.DoesNotContain(fileSystem.AllFiles, f => f.Contains("Hef met Jayh", StringComparison.Ordinal));

        var songs = await db.Songs.IgnoreQueryFilters().OrderBy(s => s.TrackNumber).ToListAsync();
        Assert.All(songs, s => Assert.Equal("Hef", s.AlbumArtist));
        // The two writers agree: every file the builder wrote carries the album artist the DB holds.
        Assert.All(tagWriter.IdentityBySource.Values, identity => Assert.Equal("Hef", identity.AlbumArtist));
        Assert.Equal(
            ["Hef", "Hef", "Hef", "Hef", "Hef met Jayh", "Hef met Jayh", "Hef met Jayh", "Hef met Jayh"],
            songs.Select(s => s.Artist!).ToArray());
    }

    [Fact]
    public async Task CanonicalAlbumCreditsTheCollab_RepairDefers_AndBothReachAFixedPoint()
    {
        // Here the canonical pipeline says the album IS the collab's, so the split heal pulls the whole
        // album onto "Hef met Jayh". The repair must never write the lead into that — every such write
        // would be overlaid right back, re-tagging and relocating the tracks on each pass.
        var fileSystem = SourceFiles(5);
        await using var db = CreateDbContext();
        for (var i = 1; i <= 5; i++)
        {
            var collab = i > 3;
            db.Songs.Add(Track(i,
                artist: collab ? "Hef met Jayh" : "Hef",
                albumArtist: collab ? "Hef met Jayh" : "Hef",
                artists: collab ? "Hef; Jayh" : "Hef",
                artistMbids: collab ? "mbid-hef; mbid-jayh" : "mbid-hef",
                year: 2020,
                buildStatus: LibraryBuildStatus.Pending));
        }
        db.CanonicalAlbums.Add(Canonical("hef", "Hef met Jayh"));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var builder = CreateBuilder(db, fileSystem, tagWriter);

        var repairs = new List<ArtistCreditRepairResult>();
        var heals = new List<AlbumSplitHealResult>();
        for (var round = 1; round <= 4; round++)
        {
            repairs.Add(await Repairer(db).HealAsync());
            heals.Add(await Healer(db).HealAsync());
            await builder.ProcessNextBatchAsync(Guid.NewGuid());
        }

        Assert.All(repairs, r => Assert.Equal(new ArtistCreditRepairResult(0, 0, 0), r));
        Assert.All(heals.Skip(1), h => Assert.Equal(new AlbumSplitHealResult(0, 0, 0), h));

        var songs = await db.Songs.IgnoreQueryFilters().ToListAsync();
        Assert.All(songs, s => Assert.Equal("Hef met Jayh", s.AlbumArtist));
        Assert.Equal("/dest/Hef met Jayh/2020 - Album X", await DistinctFoldersAsync(db));
    }

    [Fact]
    public async Task LegacyAliasFoldingTheLeadIntoTheCollab_RepairAndSplitHealAgree()
    {
        // An alias from before merges refused collab canonicals: "Hef" merged INTO "Hef met Jayh".
        // Both writers resolve it through ArtistAliasMap, which ignores it, so the repair's lead is
        // also what the split heal elects — one move, then a fixed point, never a flip.
        var fileSystem = SourceFiles(5);
        await using var db = CreateDbContext();
        for (var i = 1; i <= 5; i++)
        {
            var collab = i > 3;
            db.Songs.Add(Track(i,
                artist: collab ? "Hef met Jayh" : "Hef",
                albumArtist: collab ? "Hef met Jayh" : "Hef",
                artists: collab ? "Hef; Jayh" : "Hef",
                artistMbids: collab ? "mbid-hef; mbid-jayh" : "mbid-hef",
                year: 2020));
        }
        db.ArtistAliases.Add(new ArtistAlias
        {
            OwnerUserId = WellKnownUsers.OwnerId, AliasKey = "hef", CanonicalName = "Hef met Jayh",
        });
        await db.SaveChangesAsync();
        foreach (var song in await db.Songs.IgnoreQueryFilters().ToListAsync())
        {
            fileSystem.AddFile(song.DestinationPath!, new MockFileData("audio-bytes"));
        }

        var tagWriter = new RecordingTagWriter();
        var builder = CreateBuilder(db, fileSystem, tagWriter);

        var repairs = new List<ArtistCreditRepairResult>();
        var heals = new List<AlbumSplitHealResult>();
        for (var round = 1; round <= 4; round++)
        {
            repairs.Add(await Repairer(db).HealAsync());
            heals.Add(await Healer(db).HealAsync());
            await builder.ProcessNextBatchAsync(Guid.NewGuid());
        }

        Assert.Equal(2, repairs[0].SongsRepaired);
        Assert.All(repairs.Skip(1), r => Assert.Equal(new ArtistCreditRepairResult(0, 0, 0), r));
        Assert.All(heals.Skip(1), h => Assert.Equal(new AlbumSplitHealResult(0, 0, 0), h));

        var songs = await db.Songs.IgnoreQueryFilters().ToListAsync();
        Assert.All(songs, s => Assert.Equal("Hef", s.AlbumArtist));
        Assert.Equal("/dest/Hef/2020 - Album X", await DistinctFoldersAsync(db));
        Assert.DoesNotContain(fileSystem.AllFiles, f => f.Contains("Hef met Jayh", StringComparison.Ordinal));
    }

    private static void SeedHefAlbum(MusicHoarderDbContext db)
    {
        var mergedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 8; i++)
        {
            var song = i switch
            {
                <= 4 => Track(i, artist: "Hef", albumArtist: "Hef", artists: "Hef", artistMbids: "mbid-hef", year: 2020),
                // Enriched as the collab, from a single on another release: their own year.
                <= 6 => Track(i, artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
                    artistMbids: "mbid-hef; mbid-jayh", year: 2021),
                // Through the merge "Hef met Jayh" → "Hef": both credits cut to the lead.
                _ => Track(i, artist: "Hef", albumArtist: "Hef", artists: "Hef; Jayh",
                    artistMbids: "mbid-hef; mbid-jayh", year: 2020),
            };
            db.Songs.Add(song);
            if (i > 6)
            {
                foreach (var field in new[] { nameof(SongMetadata.Artist), nameof(SongMetadata.AlbumArtist) })
                {
                    db.SongMetadataChanges.Add(new SongMetadataChange
                    {
                        Song = song, FieldName = field, OldValue = "Hef met Jayh", NewValue = "Hef",
                        Source = "artist-merge", Confidence = 1.0, CreatedAtUtc = mergedAt, AppliedAtUtc = mergedAt,
                    });
                }
            }
        }
    }

    private static SongMetadata Track(
        int i, string artist, string albumArtist, string artists, string artistMbids, int year,
        LibraryBuildStatus buildStatus = LibraryBuildStatus.Done) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = $"/source/u{i}.flac",
        FileName = $"u{i}.flac",
        Extension = ".flac",
        FileSizeBytes = 11,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        OriginalMetadataCaptured = true,
        Artist = artist,
        AlbumArtist = albumArtist,
        Artists = artists,
        ArtistMusicBrainzIds = artistMbids,
        AlbumArtistMusicBrainzId = "mbid-hef",
        MusicBrainzReleaseId = "rel-1",
        Album = "Album X",
        Title = $"Track {i}",
        TrackNumber = i,
        Year = year,
        LibraryBuildStatus = buildStatus,
        DestinationPath = buildStatus == LibraryBuildStatus.Done
            ? $"/dest/{albumArtist}/{year} - Album X/{i:00} - Track {i}.flac"
            : null,
    };

    private static CanonicalAlbum Canonical(string artistKey, string displayArtist) => new()
    {
        ArtistKey = artistKey,
        AlbumKey = "album x",
        DisplayArtist = displayArtist,
        DisplayTitle = "Album X",
        Year = 2020,
        Status = CanonicalAlbumStatus.Fetched,
    };

    private static async Task<string> DistinctFoldersAsync(MusicHoarderDbContext db)
    {
        var songs = await db.Songs.IgnoreQueryFilters().ToListAsync();
        return string.Join(" | ", songs
            .Select(s => Path.GetDirectoryName(s.DestinationPath) ?? "<none>")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
    }

    private static MockFileSystem SourceFiles(int count)
    {
        var files = new Dictionary<string, MockFileData>();
        for (var i = 1; i <= count; i++)
        {
            files[$"/source/u{i}.flac"] = new MockFileData("audio-bytes");
        }

        return new MockFileSystem(files);
    }

    private static MusicEnricherOptions BuildOptions() => new()
    {
        SourceDirectory = "/source",
        DestinationDirectory = "/dest",
        LibraryBuilderBatchSize = 100,
        LibraryBuilderWorkerConcurrency = 1,
        EnableAlbumIdentityReconciliation = true,
        EnableCanonicalDrivenBuild = true,
        LyricsBeforeBuildWaitMinutes = 0,
    };

    private static IArtistCreditRepairHealer Repairer(MusicHoarderDbContext db) => new ArtistCreditRepairHealer(
        db,
        Microsoft.Extensions.Options.Options.Create(BuildOptions()),
        NullLogger<ArtistCreditRepairHealer>.Instance);

    private static IAlbumSplitHealer Healer(MusicHoarderDbContext db)
    {
        var options = Microsoft.Extensions.Options.Options.Create(BuildOptions());
        return new AlbumSplitHealer(
            db,
            new AlbumIdentityReconciler(),
            new DestinationPathResolver(options),
            options,
            NullLogger<AlbumSplitHealer>.Instance);
    }

    private static LibraryBuilderService CreateBuilder(
        MusicHoarderDbContext db, IFileSystem fileSystem, ILibraryTagWriter tagWriter)
    {
        var options = Microsoft.Extensions.Options.Options.Create(BuildOptions());
        var coverWriter = new AlbumCoverWriter(
            fileSystem,
            new CoverArtResolver(fileSystem, new NoPictureReader()),
            new StubExternalCoverArtFetcher(),
            options,
            NullLogger<AlbumCoverWriter>.Instance);

        return new LibraryBuilderService(
            new SingleScopeFactory(db, tagWriter),
            new DestinationPathResolver(options),
            fileSystem,
            new LibraryDestinationCleaner(fileSystem),
            tagWriter,
            coverWriter,
            new AlbumIdentityReconciler(),
            options,
            TestPipelineMetrics.Create(),
            new NoOpTrackSyncEnqueuer(),
            NullLogger<LibraryBuilderService>.Instance);
    }

    private static MusicHoarderDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class RecordingTagWriter : ILibraryTagWriter
    {
        public List<string> Paths { get; } = [];

        public Dictionary<string, AlbumIdentity> IdentityBySource { get; } = new(StringComparer.Ordinal);

        public Task WriteTagsAsync(string path, SongMetadata song, AlbumIdentity albumIdentity, CancellationToken ct = default)
        {
            Paths.Add(path);
            IdentityBySource[song.SourcePath] = albumIdentity;
            return Task.CompletedTask;
        }
    }

    private sealed class NoPictureReader : IEmbeddedPictureReader
    {
        public EmbeddedPicture? ReadFront(string filePath) => null;
    }

    private sealed class NoOpTrackSyncEnqueuer : MusicHoarder.Api.Sync.ITrackSyncEnqueuer
    {
        public void TryEnqueue(int songId, Guid ownerUserId) { }
    }

    private sealed class SingleScopeFactory(MusicHoarderDbContext db, ILibraryTagWriter tagWriter)
        : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SingleScope(new SingleScopeProvider(db, tagWriter));
    }

    private sealed class SingleScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;

        public void Dispose()
        {
        }
    }

    private sealed class SingleScopeProvider(MusicHoarderDbContext db, ILibraryTagWriter tagWriter) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(MusicHoarderDbContext)) return db;
            if (serviceType == typeof(ILibraryTagWriter)) return tagWriter;
            return null;
        }
    }
}
