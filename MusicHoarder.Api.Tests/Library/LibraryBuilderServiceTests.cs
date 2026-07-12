using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Artwork;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Reflection;
using System.Text.Json;

namespace MusicHoarder.Api.Tests.Library;

public class LibraryBuilderServiceTests
{
    [Fact]
    public async Task ProcessNextBatchAsync_SkipsCopy_WhenDestinationExistsWithMatchingSize()
    {
        var sourcePath = "/source/track.mp3";
        var destinationPath = "/dest/Artist/2026 - Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("12345"),
            [destinationPath] = new("12345")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal(0, result.Failed);
        Assert.Equal(LibraryBuildStatus.Done, song.LibraryBuildStatus);
        Assert.Null(song.LibraryBuildError);
        Assert.Empty(tagWriter.Paths);
    }

    [Fact]
    public async Task ProcessNextBatchAsync_UsesTempFileAndRenamesOnSuccess()
    {
        var sourcePath = "/source/track.mp3";
        var destinationPath = "/dest/Artist/2026 - Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal(LibraryBuildStatus.Done, song.LibraryBuildStatus);
        Assert.True(fileSystem.File.Exists(destinationPath));
        var usedTempPath = tagWriter.Paths.Single();
        Assert.Contains(".tmp.", usedTempPath, StringComparison.Ordinal);
        Assert.False(fileSystem.File.Exists(usedTempPath));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_CreatesDestinationDirectory()
    {
        var sourcePath = "/source/track.mp3";
        var destinationDirectory = "/dest/Artist/2026 - Album";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.True(fileSystem.Directory.Exists(destinationDirectory));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_ContinuesWhenSingleFileFails()
    {
        var goodSource = "/source/good.mp3";
        var missingSource = "/source/missing.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [goodSource] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(goodSource, 5, "Good Track"));
        db.Songs.Add(CreateMatchedSong(missingSource, 5, "Missing Track"));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var songs = await db.Songs.OrderBy(s => s.Title).ToListAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal(1, result.Failed);
        Assert.Equal(LibraryBuildStatus.Done, songs[0].LibraryBuildStatus);
        Assert.Equal(LibraryBuildStatus.Failed, songs[1].LibraryBuildStatus);
        Assert.False(string.IsNullOrWhiteSpace(songs[1].LibraryBuildError));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_ReconcilesLegacyArtistPath_AndPrunesEmptyDirectories()
    {
        var sourcePath = "/source/track.mp3";
        var legacyPath = "/dest/Artist A; Artist B/2026 - Album/01 - Track.mp3";
        var newPath = "/dest/Artist A/2026 - Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde"),
            [legacyPath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(
            sourcePath,
            5,
            title: "Track",
            artist: "Artist A; Artist B",
            albumArtist: "Artist A",
            libraryBuildStatus: LibraryBuildStatus.Done));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();

        Assert.Equal(1, result.Done);
        Assert.True(fileSystem.File.Exists(newPath));
        Assert.False(fileSystem.File.Exists(legacyPath));
        Assert.Equal(newPath, song.DestinationPath);
        Assert.Null(song.PreviousDestinationPath);
        Assert.False(fileSystem.Directory.Exists("/dest/Artist A; Artist B/2026 - Album"));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_OverwritesDestination_WhenExistingContentDiffers()
    {
        var sourcePath = "/source/track.mp3";
        var destinationPath = "/dest/Artist/2026 - Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("12345"),
            [destinationPath] = new("different")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();
        var writtenSize = fileSystem.FileInfo.New(destinationPath).Length;

        Assert.Equal(1, result.Done);
        Assert.Equal(5, writtenSize);
        Assert.Equal(destinationPath, song.DestinationPath);
    }

    [Fact]
    public async Task ProcessNextBatchAsync_PreservesUnknownFiles_WhenCleaningLegacyDirectories()
    {
        var sourcePath = "/source/track.mp3";
        var legacyPath = "/dest/Artist A; Artist B/2026 - Album/01 - Track.mp3";
        var unknownPath = "/dest/Artist A; Artist B/2026 - Album/notes.txt";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde"),
            [legacyPath] = new("abcde"),
            [unknownPath] = new("keep me")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(
            sourcePath,
            5,
            title: "Track",
            artist: "Artist A; Artist B",
            albumArtist: "Artist A",
            libraryBuildStatus: LibraryBuildStatus.Done));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.True(fileSystem.File.Exists(unknownPath));
        Assert.True(fileSystem.Directory.Exists("/dest/Artist A; Artist B/2026 - Album"));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_MarksFailed_WhenTempCleanupDeleteThrows()
    {
        var sourcePath = "/source/track.mp3";
        var innerFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });
        var fileSystem = new DeleteThrowingFileSystem(
            innerFileSystem,
            path => path.Contains(".tmp.", StringComparison.Ordinal));

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingThrowingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();

        Assert.Equal(0, result.Done);
        Assert.Equal(1, result.Failed);
        Assert.Equal(LibraryBuildStatus.Failed, song.LibraryBuildStatus);
        Assert.False(string.IsNullOrWhiteSpace(song.LibraryBuildError));
        Assert.Contains("tag writer exploded", song.LibraryBuildError, StringComparison.OrdinalIgnoreCase);
        var tempPath = Assert.Single(tagWriter.Paths);
        Assert.True(fileSystem.File.Exists(tempPath));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_ProcessesSingleCandidate_WhenDestinationCollidesWithinBatch()
    {
        var sourcePath1 = "/source/track1.mp3";
        var sourcePath2 = "/source/track2.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath1] = new("abcde"),
            [sourcePath2] = new("fghij")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath1, 5, title: "Unknown Title", artist: "Artist", albumArtist: "Artist"));
        db.Songs.Add(CreateMatchedSong(sourcePath2, 5, title: "Unknown Title", artist: "Artist", albumArtist: "Artist"));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();

        Assert.Equal(1, result.TotalTracks);
        Assert.Equal(1, result.Done);
        Assert.Equal(0, result.Failed);
        Assert.Equal(LibraryBuildStatus.Done, songs[0].LibraryBuildStatus);
        Assert.Equal(LibraryBuildStatus.Pending, songs[1].LibraryBuildStatus);
        Assert.Single(tagWriter.Paths);
    }

    [Fact]
    public async Task ProcessNextBatchAsync_RetagsInPlace_WhenDoneSongRequeuedForRetag()
    {
        // A Done song with a destination file is normally skipped by the build query. RequeueForRetag
        // flips it back to Pending while KEEPING DestinationPath, so the builder re-copies and re-tags
        // the file in place (same path). This is what the album-page "Re-tag" button drives.
        //
        // The destination here is the SAME size as the source — the realistic FLAC case, where padding
        // keeps a re-tagged file's size identical. Without the PreviousDestinationPath force-signal the
        // skip-copy fast path would mark it Done without re-tagging (the bug seen in production);
        // RequeueForRetag sets that signal so the rewrite actually happens.
        var sourcePath = "/source/track.mp3";
        var destinationPath = "/dest/Artist/2026 - Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde"),     // 5 bytes (source)
            [destinationPath] = new("xxxxx") // same size -> skip-copy would fire without the force-signal
        });

        await using var db = CreateDbContext();
        var song = CreateMatchedSong(sourcePath, 5, libraryBuildStatus: LibraryBuildStatus.Done);
        song.DestinationPath = destinationPath;
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        song.RequeueForRetag();
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var reloaded = await db.Songs.SingleAsync();

        Assert.Equal(1, result.Done);
        Assert.Single(tagWriter.Paths); // it actually re-tagged (not skipped)
        Assert.Equal(LibraryBuildStatus.Done, reloaded.LibraryBuildStatus);
        Assert.Equal(destinationPath, reloaded.DestinationPath); // same path — in place
        Assert.Null(reloaded.PreviousDestinationPath);            // no folder move
    }

    [Fact]
    public async Task ProcessNextBatchAsync_HarmonizesAlbumIdentity_AcrossTracksOfOneFolder()
    {
        // Two tracks of one album (same artist/album/year -> same folder) that enriched to DIFFERENT
        // MusicBrainz releases. Without reconciliation Navidrome would split the album; here both must
        // be tagged with the single elected release id (the majority "rel-keep").
        var sourceA = "/source/a.mp3";
        var sourceB = "/source/b.mp3";
        var sourceC = "/source/c.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourceA] = new("aaaaa"),
            [sourceB] = new("bbbbb"),
            [sourceC] = new("ccccc")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourceA, 5, title: "T1", trackNumber: 1, musicBrainzReleaseId: "rel-keep", totalTracks: 12));
        db.Songs.Add(CreateMatchedSong(sourceB, 5, title: "T2", trackNumber: 2, musicBrainzReleaseId: "rel-keep", totalTracks: 12));
        db.Songs.Add(CreateMatchedSong(sourceC, 5, title: "T3", trackNumber: 3, musicBrainzReleaseId: "rel-stray", totalTracks: 3));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        var ids = tagWriter.IdentityBySource;
        Assert.Equal(3, ids.Count);
        Assert.All(ids.Values, identity => Assert.Equal("rel-keep", identity.MusicBrainzReleaseId));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_ExcludesDemoRows_FromBuildAndIdentityElection()
    {
        // Destination folder keys carry no owner segment, so a demo album with the same artist/album
        // resolves to the same folder as the owner's. The demo rows must neither build nor vote in
        // the folder's identity election (here they hold the majority release id and would win).
        var sourceA = "/source/a.mp3";
        var sourceB = "/source/b.mp3";
        var demoC = "/demo/c.mp3";
        var demoD = "/demo/d.mp3";
        var demoE = "/demo/e.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourceA] = new("aaaaa"),
            [sourceB] = new("bbbbb"),
            [demoC] = new("ccccc"),
            [demoD] = new("ddddd"),
            [demoE] = new("eeeee")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourceA, 5, title: "T1", trackNumber: 1, musicBrainzReleaseId: "rel-owner"));
        db.Songs.Add(CreateMatchedSong(sourceB, 5, title: "T2", trackNumber: 2, musicBrainzReleaseId: "rel-owner"));
        db.Songs.Add(CreateMatchedSong(demoC, 5, title: "T3", trackNumber: 3, musicBrainzReleaseId: "rel-demo",
            owner: MusicHoarder.Api.Auth.WellKnownUsers.DemoId));
        db.Songs.Add(CreateMatchedSong(demoD, 5, title: "T4", trackNumber: 4, musicBrainzReleaseId: "rel-demo",
            owner: MusicHoarder.Api.Auth.WellKnownUsers.DemoId));
        db.Songs.Add(CreateMatchedSong(demoE, 5, title: "T5", trackNumber: 5, musicBrainzReleaseId: "rel-demo",
            owner: MusicHoarder.Api.Auth.WellKnownUsers.DemoId));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        // Only the owner's tracks were built, and their identity ignored the demo majority.
        Assert.Equal(new[] { sourceA, sourceB }, tagWriter.IdentityBySource.Keys.OrderBy(k => k).ToArray());
        Assert.All(tagWriter.IdentityBySource.Values, identity => Assert.Equal("rel-owner", identity.MusicBrainzReleaseId));

        var demoRows = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OwnerUserId == MusicHoarder.Api.Auth.WellKnownUsers.DemoId)
            .ToListAsync();
        Assert.All(demoRows, s => Assert.Equal(LibraryBuildStatus.Pending, s.LibraryBuildStatus));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_KeepsPerSongIdentity_WhenReconciliationDisabled()
    {
        var sourceA = "/source/a.mp3";
        var sourceB = "/source/b.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourceA] = new("aaaaa"),
            [sourceB] = new("bbbbb")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourceA, 5, title: "T1", trackNumber: 1, musicBrainzReleaseId: "rel-aaa"));
        db.Songs.Add(CreateMatchedSong(sourceB, 5, title: "T2", trackNumber: 2, musicBrainzReleaseId: "rel-bbb"));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter, enableAlbumIdentityReconciliation: false);

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal("rel-aaa", tagWriter.IdentityBySource[sourceA].MusicBrainzReleaseId);
        Assert.Equal("rel-bbb", tagWriter.IdentityBySource[sourceB].MusicBrainzReleaseId);
    }

    [Fact]
    public async Task ProcessNextBatchAsync_ElectsFromFullMembership_AcrossBatchBoundaries()
    {
        // batchSize=1 splits the album across two cycles. Each cycle must elect from the FULL pair, so
        // both tracks get the same identity even though they build in separate batches.
        var sourceA = "/source/a.mp3";
        var sourceB = "/source/b.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourceA] = new("aaaaa"),
            [sourceB] = new("bbbbb")
        });

        await using var db = CreateDbContext();
        // Two of "rel-keep" so the majority is unambiguous regardless of which one is in the slice.
        db.Songs.Add(CreateMatchedSong(sourceA, 5, title: "T1", trackNumber: 1, musicBrainzReleaseId: "rel-keep"));
        db.Songs.Add(CreateMatchedSong(sourceB, 5, title: "T2", trackNumber: 2, musicBrainzReleaseId: "rel-keep"));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter, batchSize: 1);

        await service.ProcessNextBatchAsync(Guid.NewGuid());
        await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(2, tagWriter.IdentityBySource.Count);
        Assert.All(tagWriter.IdentityBySource.Values, identity => Assert.Equal("rel-keep", identity.MusicBrainzReleaseId));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_WritesAlbumCover_FromEmbeddedArt()
    {
        var sourcePath = "/source/track.mp3";
        var coverPath = "/dest/Artist/2026 - Album/cover.png";
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var service = CreateService(
            db, fileSystem, new RecordingTagWriter(),
            new StubEmbeddedPictureReader(new EmbeddedPicture(pngBytes, "image/png")));

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.True(fileSystem.File.Exists(coverPath));
        Assert.Equal(pngBytes, await fileSystem.File.ReadAllBytesAsync(coverPath));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_WritesAlbumCover_FromSourceFolderImage()
    {
        var sourcePath = "/source/track.mp3";
        var sourceCover = "/source/cover.jpg";
        var destCover = "/dest/Artist/2026 - Album/cover.jpg";
        var jpgBytes = new byte[] { 0xFF, 0xD8, 0xFF, 9, 8, 7 };
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde"),
            [sourceCover] = new(jpgBytes)
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        // No embedded art — the folder image in the source directory must win (Navidrome's order).
        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.True(fileSystem.File.Exists(destCover));
        Assert.Equal(jpgBytes, await fileSystem.File.ReadAllBytesAsync(destCover));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_DoesNotOverwriteExistingDestinationCover()
    {
        var sourcePath = "/source/track.mp3";
        var existingCover = "/dest/Artist/2026 - Album/cover.jpg";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde"),
            [existingCover] = new("user-provided")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var service = CreateService(
            db, fileSystem, new RecordingTagWriter(),
            new StubEmbeddedPictureReader(new EmbeddedPicture([0x89, 0x50, 0x4E, 0x47], "image/png")));

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        // The existing cover is left untouched and no second cover.* is added.
        Assert.Equal("user-provided", fileSystem.File.ReadAllText(existingCover));
        Assert.False(fileSystem.File.Exists("/dest/Artist/2026 - Album/cover.png"));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_DoesNotWriteCover_ForUnreleasedTracks()
    {
        var sourcePath = "/source/track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5, isUnreleased: true));
        await db.SaveChangesAsync();

        var service = CreateService(
            db, fileSystem, new RecordingTagWriter(),
            new StubEmbeddedPictureReader(new EmbeddedPicture([0x89, 0x50, 0x4E, 0x47], "image/png")));

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        // Unreleased folders mix unrelated singles — no shared cover is dropped in.
        Assert.False(fileSystem.File.Exists("/dest/Artist/Unreleased/cover.png"));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_RemovesOrphanCover_WhenAlbumFolderMoves()
    {
        var sourcePath = "/source/track.mp3";
        var legacyPath = "/dest/Artist A; Artist B/2026 - Album/01 - Track.mp3";
        var legacyCover = "/dest/Artist A; Artist B/2026 - Album/cover.jpg";
        var newPath = "/dest/Artist A/2026 - Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde"),
            [legacyPath] = new("abcde"),
            [legacyCover] = new("stale cover")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(
            sourcePath,
            5,
            title: "Track",
            artist: "Artist A; Artist B",
            albumArtist: "Artist A",
            libraryBuildStatus: LibraryBuildStatus.Done));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        // The track moved albums; the orphaned cover in the old folder is removed so the now-empty
        // folder gets pruned.
        Assert.True(fileSystem.File.Exists(newPath));
        Assert.False(fileSystem.File.Exists(legacyCover));
        Assert.False(fileSystem.Directory.Exists("/dest/Artist A; Artist B/2026 - Album"));
    }

    // --- Destination-write history capture (LibraryWriteEvent) ---

    [Fact]
    public async Task ProcessNextBatchAsync_RecordsWriteEvent_DiffedAgainstOriginal_OnFirstBuild()
    {
        var sourcePath = "/source/track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        var song = CreateMatchedSong(sourcePath, 5);
        song.CaptureOriginalMetadata();   // originals = current (Album = "Album")
        song.Album = "Greatest Hits";     // an enrichment correction the first build will write
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        var events = await db.LibraryWriteEvents.ToListAsync();
        var albumChange = Assert.Single(events, e => e.FieldName == "Album");
        Assert.Equal(LibraryWriteEventKind.TrackTagsWritten, albumChange.Kind);
        Assert.Equal("Album", albumChange.OldValue);
        Assert.Equal("Greatest Hits", albumChange.NewValue);
        Assert.True(albumChange.IsAlbumIdentityField);
        Assert.Equal(song.Id, albumChange.SongId);

        var reloaded = await db.Songs.SingleAsync();
        Assert.False(string.IsNullOrEmpty(reloaded.LastWrittenTagsJson));
        Assert.NotNull(reloaded.LastWrittenAtUtc);
    }

    [Fact]
    public async Task ProcessNextBatchAsync_RecordsNoWriteEvents_OnSameSizeSkip()
    {
        var sourcePath = "/source/track.mp3";
        var destinationPath = "/dest/Artist/2026 - Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("12345"),
            [destinationPath] = new("12345")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Empty(await db.LibraryWriteEvents.ToListAsync());
        var song = await db.Songs.SingleAsync();
        Assert.Null(song.LastWrittenTagsJson); // untouched — nothing was rewritten
    }

    [Fact]
    public async Task ProcessNextBatchAsync_RecordsNoNewWriteEvents_OnNoOpRetag()
    {
        var sourcePath = "/source/track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        var song = CreateMatchedSong(sourcePath, 5);
        song.CaptureOriginalMetadata();
        song.Album = "Greatest Hits";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        await service.ProcessNextBatchAsync(Guid.NewGuid());
        var afterFirst = await db.LibraryWriteEvents.CountAsync();

        // Re-tag with nothing changed: the diff against the just-written snapshot is empty.
        var built = await db.Songs.SingleAsync();
        built.RequeueForRetag();
        await db.SaveChangesAsync();

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(afterFirst, await db.LibraryWriteEvents.CountAsync());
    }

    [Fact]
    public async Task ProcessNextBatchAsync_RecordsAlbumIdentityEvent_WhenRetagConsolidatesRelease()
    {
        // C was last written as the stray release; A and B (the majority) force reconciliation to elect
        // "rel-keep" for the folder. C's forced re-tag must record the release id moving rel-stray→rel-keep.
        var sourceA = "/source/a.mp3";
        var sourceB = "/source/b.mp3";
        var sourceC = "/source/c.mp3";
        var destC = "/dest/Artist/2026 - Album/03 - T3.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourceA] = new("aaaaa"),
            [sourceB] = new("bbbbb"),
            [sourceC] = new("ccccc")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourceA, 5, title: "T1", trackNumber: 1, musicBrainzReleaseId: "rel-keep"));
        db.Songs.Add(CreateMatchedSong(sourceB, 5, title: "T2", trackNumber: 2, musicBrainzReleaseId: "rel-keep"));

        var c = CreateMatchedSong(sourceC, 5, title: "T3", trackNumber: 3, musicBrainzReleaseId: "rel-stray",
            libraryBuildStatus: LibraryBuildStatus.Done);
        c.DestinationPath = destC;
        // Snapshot what C last wrote (its own stray identity), then force a re-tag.
        c.LastWrittenTagsJson = JsonSerializer.Serialize(WrittenTagSet.From(c, AlbumIdentity.FromSong(c)));
        c.RequeueForRetag();
        db.Songs.Add(c);
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        var releaseChange = Assert.Single(
            await db.LibraryWriteEvents.Where(e => e.FieldName == "MusicBrainzReleaseId").ToListAsync());
        Assert.Equal(c.Id, releaseChange.SongId);
        Assert.Equal("rel-stray", releaseChange.OldValue);
        Assert.Equal("rel-keep", releaseChange.NewValue);
        Assert.True(releaseChange.IsAlbumIdentityField);

        // The re-tag must refresh the snapshot too: the next diff baselines against what was just
        // written (rel-keep), not the stale pre-consolidation tags.
        var reloadedC = await db.Songs.SingleAsync(s => s.Id == c.Id);
        Assert.NotNull(reloadedC.LastWrittenTagsJson);
        Assert.Contains("rel-keep", reloadedC.LastWrittenTagsJson);
        Assert.DoesNotContain("rel-stray", reloadedC.LastWrittenTagsJson);
    }

    [Fact]
    public async Task ProcessNextBatchAsync_RecordsCoverEvent_WhenCoverWritten()
    {
        var sourcePath = "/source/track.mp3";
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var service = CreateService(
            db, fileSystem, new RecordingTagWriter(),
            new StubEmbeddedPictureReader(new EmbeddedPicture(pngBytes, "image/png")));

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        var cover = Assert.Single(
            await db.LibraryWriteEvents.Where(e => e.Kind == LibraryWriteEventKind.AlbumCoverWritten).ToListAsync());
        Assert.Equal("Cover", cover.FieldName);
        Assert.Equal("Album", cover.Album);
        Assert.Equal("/dest/Artist/2026 - Album", cover.AlbumFolder);
    }

    [Fact]
    public async Task ProcessNextBatchAsync_FetchesExternalCover_WhenSourceHasNoArt()
    {
        var sourcePath = "/source/track.mp3";
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5, musicBrainzReleaseId: "rel-1"));
        await db.SaveChangesAsync();

        var fetcher = new StubExternalCoverArtFetcher
        {
            Result = new ExternalCoverArtFetchResult(new FetchedCoverArt(pngBytes, "image/png", "coverartarchive"), false),
        };
        var service = CreateService(db, fileSystem, new RecordingTagWriter(), externalFetcher: fetcher);

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        // The fetch was keyed by the reconciled identity's release MBID and album/artist names.
        var query = Assert.Single(fetcher.Calls);
        Assert.Equal("rel-1", query.MusicBrainzReleaseId);
        Assert.Equal("Album", query.Album);

        Assert.Equal(pngBytes, fileSystem.File.ReadAllBytes("/dest/Artist/2026 - Album/cover.png"));
        var cover = Assert.Single(
            await db.LibraryWriteEvents.Where(e => e.Kind == LibraryWriteEventKind.AlbumCoverWritten).ToListAsync());
        Assert.Equal("fetched:coverartarchive", cover.NewValue);
    }

    [Fact]
    public async Task ProcessNextBatchAsync_RecordsNoCoverEvent_WhenCoverAlreadyExists()
    {
        var sourcePath = "/source/track.mp3";
        var existingCover = "/dest/Artist/2026 - Album/cover.jpg";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde"),
            [existingCover] = new("user-provided")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var service = CreateService(
            db, fileSystem, new RecordingTagWriter(),
            new StubEmbeddedPictureReader(new EmbeddedPicture([0x89, 0x50, 0x4E, 0x47], "image/png")));

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Empty(await db.LibraryWriteEvents.Where(e => e.Kind == LibraryWriteEventKind.AlbumCoverWritten).ToListAsync());
    }

    [Fact]
    public async Task ProcessNextBatchAsync_FlagsFreshBuildAsDuplicate_WhenPositionAlreadyBuiltWithBetterQuality()
    {
        // A FLAC of track 1 is already built; a lower-quality OPUS re-encode of the same track would
        // resolve to a different file name (different extension), so without the position guard it
        // lands NEXT TO the FLAC instead of colliding with it.
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/new.opus"] = new("abcde"),
            ["/dest/Artist/2026 - Album/01 - Track.flac"] = new("flac-bytes")
        });

        await using var db = CreateDbContext();
        var occupant = CreateMatchedSong("/source/old.flac", 10, libraryBuildStatus: LibraryBuildStatus.Done);
        occupant.Extension = ".flac";
        occupant.DestinationPath = "/dest/Artist/2026 - Album/01 - Track.flac";
        var candidate = CreateMatchedSong("/source/new.opus", 5);
        candidate.Extension = ".opus";
        db.Songs.AddRange(occupant, candidate);
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());
        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(0, result.TotalTracks);
        var flagged = await db.Songs.SingleAsync(s => s.Id == candidate.Id);
        Assert.True(flagged.IsDuplicate);
        Assert.Equal(occupant.Id, flagged.DuplicateOfId);
        Assert.Equal(LibraryBuildStatus.Pending, flagged.LibraryBuildStatus);
        Assert.False(fileSystem.File.Exists("/dest/Artist/2026 - Album/01 - Track.opus"));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_AllowsSamePosition_WhenTitlesAreClearlyDifferentSongs()
    {
        // Two unrelated songs can share a track number through bad tags — the position guard must not
        // eat one of them; only a fuzzy-similar title makes a position conflict a duplicate.
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/new.opus"] = new("abcde"),
            ["/dest/Artist/2026 - Album/01 - Completely Different Song.flac"] = new("flac-bytes")
        });

        await using var db = CreateDbContext();
        var occupant = CreateMatchedSong("/source/old.flac", 10, title: "Completely Different Song", libraryBuildStatus: LibraryBuildStatus.Done);
        occupant.Extension = ".flac";
        occupant.DestinationPath = "/dest/Artist/2026 - Album/01 - Completely Different Song.flac";
        var candidate = CreateMatchedSong("/source/new.opus", 5);
        candidate.Extension = ".opus";
        db.Songs.AddRange(occupant, candidate);
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());
        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(1, result.Done);
        var built = await db.Songs.SingleAsync(s => s.Id == candidate.Id);
        Assert.False(built.IsDuplicate);
        Assert.Equal(LibraryBuildStatus.Done, built.LibraryBuildStatus);
    }

    [Fact]
    public async Task ProcessNextBatchAsync_StillBuilds_WhenFreshCopyIsHigherQualityThanBuiltOccupant()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/new.flac"] = new("flac-bytes"),
            ["/dest/Artist/2026 - Album/01 - Track.opus"] = new("opus-bytes")
        });

        await using var db = CreateDbContext();
        var occupant = CreateMatchedSong("/source/old.opus", 5, libraryBuildStatus: LibraryBuildStatus.Done);
        occupant.Extension = ".opus";
        occupant.DestinationPath = "/dest/Artist/2026 - Album/01 - Track.opus";
        var candidate = CreateMatchedSong("/source/new.flac", 10);
        candidate.Extension = ".flac";
        db.Songs.AddRange(occupant, candidate);
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());
        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(1, result.Done);
        var built = await db.Songs.SingleAsync(s => s.Id == candidate.Id);
        Assert.False(built.IsDuplicate);
        Assert.Equal(LibraryBuildStatus.Done, built.LibraryBuildStatus);
        Assert.True(fileSystem.File.Exists("/dest/Artist/2026 - Album/01 - Track.flac"));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_BuildsOnlyBestCopy_WhenTwoFreshCandidatesShareAPosition()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/track.flac"] = new("flac-bytes"),
            ["/source/track.opus"] = new("opus-bytes")
        });

        await using var db = CreateDbContext();
        var flac = CreateMatchedSong("/source/track.flac", 10);
        flac.Extension = ".flac";
        var opus = CreateMatchedSong("/source/track.opus", 5);
        opus.Extension = ".opus";
        db.Songs.AddRange(flac, opus);
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        // First sweep: the FLAC wins the position, the OPUS is deferred (still pending, unflagged).
        var first = await service.ProcessNextBatchAsync(Guid.NewGuid());
        Assert.Equal(1, first.TotalTracks);
        Assert.Equal(1, first.Done);
        Assert.Equal(LibraryBuildStatus.Done, (await db.Songs.SingleAsync(s => s.Id == flac.Id)).LibraryBuildStatus);
        var deferred = await db.Songs.SingleAsync(s => s.Id == opus.Id);
        Assert.False(deferred.IsDuplicate);
        Assert.Equal(LibraryBuildStatus.Pending, deferred.LibraryBuildStatus);

        // Second sweep: the FLAC now occupies the position, so the OPUS resolves to a duplicate.
        var second = await service.ProcessNextBatchAsync(Guid.NewGuid());
        Assert.Equal(0, second.TotalTracks);
        var flagged = await db.Songs.SingleAsync(s => s.Id == opus.Id);
        Assert.True(flagged.IsDuplicate);
        Assert.Equal(flac.Id, flagged.DuplicateOfId);
        Assert.False(fileSystem.File.Exists("/dest/Artist/2026 - Album/01 - Track.opus"));
    }

    // --- NeedsReview guard (issue #329 hot-loop) ---

    [Fact]
    public async Task ProcessNextBatchAsync_BuildsNeedsReviewTrack_WhenBuildNeedsReviewEnabled()
    {
        // With EnableBuildNeedsReview on, LibraryBuildQuery selects NeedsReview rows. The build MUST
        // actually build them — before the guard was aligned it rejected them WITHOUT persisting, so the
        // batch query re-selected them forever (built frozen, failed climbing 200/sec on prod, CPU burned).
        var sourcePath = "/source/track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5, enrichmentStatus: EnrichmentStatus.NeedsReview));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter(), enableBuildNeedsReview: true);

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal(0, result.Failed);
        Assert.Equal(LibraryBuildStatus.Done, song.LibraryBuildStatus);
        Assert.True(fileSystem.File.Exists("/dest/Artist/2026 - Album/01 - Track.mp3"));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_LeavesNeedsReviewTrackUntouched_WhenBuildNeedsReviewDisabled()
    {
        // The query doesn't select NeedsReview when the flag is off, so the row is neither built nor
        // failed — it just stays Pending. No spurious failure, no loop.
        var sourcePath = "/source/track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5, enrichmentStatus: EnrichmentStatus.NeedsReview));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter(), enableBuildNeedsReview: false);

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();

        Assert.Equal(0, result.TotalTracks);
        Assert.Equal(0, result.Failed);
        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        Assert.Equal(0, song.LibraryBuildAttempts);
    }

    // --- Deterministic album folder from elected identity (build hot-loop / split fix) ---

    [Fact]
    public async Task ProcessNextBatchAsync_ConsolidatesYearDriftedAlbum_IntoOneFolder_AndStaysDone()
    {
        // Two tracks of ONE album whose per-song enrichment disagrees on the year (2011 vs 2012). Per-song
        // routing would file them under different "year - Album" folders (a split that, with the heal loop
        // re-electing each run, keeps relocating the file — the production flap). The folder must instead
        // derive from the elected identity (year tie -> earliest, 2011), so both land in ONE folder and a
        // second build finds nothing to do — the album settles.
        var sourceA = "/source/a.mp3";
        var sourceB = "/source/b.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourceA] = new("aaaaa"),
            [sourceB] = new("bbbbb")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourceA, 5, title: "T1", trackNumber: 1, year: 2011));
        db.Songs.Add(CreateMatchedSong(sourceB, 5, title: "T2", trackNumber: 2, year: 2012));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        var first = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();

        Assert.Equal(2, first.Done);
        Assert.All(songs, s => Assert.Equal(LibraryBuildStatus.Done, s.LibraryBuildStatus));
        // Both files landed in the SAME elected-year folder, not one per drifted year.
        Assert.All(songs, s => Assert.StartsWith("/dest/Artist/2011 - Album/", s.DestinationPath!, StringComparison.Ordinal));
        Assert.All(tagWriter.IdentityBySource.Values, id => Assert.Equal(2011, id.Year));

        // Nothing left to do: the album is settled, no relocation churn on the next sweep.
        var second = await service.ProcessNextBatchAsync(Guid.NewGuid());
        Assert.Equal(0, second.TotalTracks);
        var reloaded = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.All(reloaded, s => Assert.Null(s.PreviousDestinationPath));
    }

    private static LibraryBuilderService CreateService(
        MusicHoarderDbContext db,
        IFileSystem fileSystem,
        ILibraryTagWriter tagWriter,
        IEmbeddedPictureReader? embeddedReader = null,
        int batchSize = 100,
        bool enableAlbumIdentityReconciliation = true,
        IExternalCoverArtFetcher? externalFetcher = null,
        bool enableBuildNeedsReview = false)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            LibraryBuilderBatchSize = batchSize,
            LibraryBuilderWorkerConcurrency = 1,
            LibraryBuilderIdleDelaySeconds = 20,
            EnableAlbumIdentityReconciliation = enableAlbumIdentityReconciliation,
            EnableBuildNeedsReview = enableBuildNeedsReview
        });

        var pathResolver = new DestinationPathResolver(options);
        var scopeFactory = new SingleScopeFactory(db, tagWriter);

        var cleaner = new LibraryDestinationCleaner(fileSystem);

        // Use the real resolver/writer over the mock filesystem so folder-image detection/writing is
        // exercised end-to-end; the embedded-picture reader (which would touch TagLib) is faked.
        var coverResolver = new CoverArtResolver(fileSystem, embeddedReader ?? new StubEmbeddedPictureReader());
        var coverWriter = new AlbumCoverWriter(
            fileSystem, coverResolver, externalFetcher ?? new StubExternalCoverArtFetcher(), options,
            NullLogger<AlbumCoverWriter>.Instance);

        return new LibraryBuilderService(
            scopeFactory,
            pathResolver,
            fileSystem,
            cleaner,
            tagWriter,
            coverWriter,
            new AlbumIdentityReconciler(),
            options,
            TestPipelineMetrics.Create(),
            new NoOpTrackSyncEnqueuer(),
            NullLogger<LibraryBuilderService>.Instance);
    }

    private static SongMetadata CreateMatchedSong(
        string sourcePath,
        long size,
        string title = "Track",
        string artist = "Artist",
        string albumArtist = "Artist",
        LibraryBuildStatus libraryBuildStatus = LibraryBuildStatus.Pending,
        bool isUnreleased = false,
        int trackNumber = 1,
        string? musicBrainzReleaseId = null,
        int? totalTracks = null,
        int? totalDiscs = null,
        Guid? owner = null,
        string album = "Album",
        int? year = 2026,
        EnrichmentStatus enrichmentStatus = EnrichmentStatus.Matched)
    {
        return new SongMetadata
        {
            OwnerUserId = owner ?? MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            Extension = ".mp3",
            FileSizeBytes = size,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = album,
            Title = title,
            Year = year,
            TrackNumber = trackNumber,
            MusicBrainzReleaseId = musicBrainzReleaseId,
            TotalTracks = totalTracks,
            TotalDiscs = totalDiscs,
            IsUnreleased = isUnreleased,
            EnrichmentStatus = enrichmentStatus,
            LibraryBuildStatus = libraryBuildStatus
        };
    }

    private static MusicHoarderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class RecordingTagWriter : ILibraryTagWriter
    {
        public List<string> Paths { get; } = [];
        // The album identity each track was tagged with, keyed by source path (the temp path varies).
        public Dictionary<string, AlbumIdentity> IdentityBySource { get; } = new(StringComparer.Ordinal);

        public Task WriteTagsAsync(string path, SongMetadata song, AlbumIdentity albumIdentity, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Paths.Add(path);
            IdentityBySource[song.SourcePath] = albumIdentity;
            return Task.CompletedTask;
        }
    }

    // Stands in for the TagLib-backed reader (which would hit the real disk). Returns a fixed
    // embedded picture for every file, or null to model files with no embedded art.
    private sealed class StubEmbeddedPictureReader(EmbeddedPicture? picture = null) : IEmbeddedPictureReader
    {
        public EmbeddedPicture? ReadFront(string filePath) => picture;
    }

    private sealed class RecordingThrowingTagWriter : ILibraryTagWriter
    {
        public List<string> Paths { get; } = [];

        public Task WriteTagsAsync(string path, SongMetadata song, AlbumIdentity albumIdentity, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Paths.Add(path);
            throw new IOException("tag writer exploded");
        }
    }

    private sealed class DeleteThrowingFileSystem : IFileSystem
    {
        private readonly IFileSystem inner;

        public DeleteThrowingFileSystem(IFileSystem inner, Func<string, bool> shouldThrowDelete)
        {
            this.inner = inner;
            var proxy = DispatchProxy.Create<IFile, DeleteThrowingFileProxy>();
            var typedProxy = (DeleteThrowingFileProxy)(object)proxy;
            typedProxy.Inner = inner.File;
            typedProxy.ShouldThrowDelete = shouldThrowDelete;
            File = proxy;
        }

        public IDirectory Directory => inner.Directory;
        public IFile File { get; }
        public IFileInfoFactory FileInfo => inner.FileInfo;
        public IFileVersionInfoFactory FileVersionInfo => inner.FileVersionInfo;
        public IPath Path => inner.Path;
        public IDirectoryInfoFactory DirectoryInfo => inner.DirectoryInfo;
        public IDriveInfoFactory DriveInfo => inner.DriveInfo;
        public IFileStreamFactory FileStream => inner.FileStream;
        public IFileSystemWatcherFactory FileSystemWatcher => inner.FileSystemWatcher;
    }

    private class DeleteThrowingFileProxy : DispatchProxy
    {
        public IFile Inner { get; set; } = default!;
        public Func<string, bool> ShouldThrowDelete { get; set; } = _ => false;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new InvalidOperationException("Proxy method metadata is missing.");
            }

            if (targetMethod.Name == nameof(IFile.Delete)
                && args is [{ } firstArg, ..]
                && firstArg is string path
                && ShouldThrowDelete(path))
            {
                throw new IOException($"Resource busy : '{path}'");
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
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

    private sealed class NoOpTrackSyncEnqueuer : MusicHoarder.Api.Sync.ITrackSyncEnqueuer
    {
        public void TryEnqueue(int songId, Guid ownerUserId) { }
    }
}