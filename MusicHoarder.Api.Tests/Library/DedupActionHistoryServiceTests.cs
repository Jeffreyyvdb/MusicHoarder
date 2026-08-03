using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class DedupActionHistoryServiceTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    [Fact]
    public async Task List_GroupsChangesIntoActionBatches_NewestFirst()
    {
        await using var db = NewContext();
        db.Songs.AddRange(Song(1, "/a/1.mp3"), Song(2, "/a/2.mp3"));
        var earlier = new DateTime(2026, 8, 2, 10, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 8, 2, 11, 0, 0, DateTimeKind.Utc);
        db.SongMetadataChanges.AddRange(
            Change(1, "Artist", "JAYZ", "JAY-Z", "artist-merge", earlier),
            Change(2, "Artist", "Jaÿ-z", "JAY-Z", "artist-merge", earlier),
            Change(1, "Album", "Blueprint 3", "The Blueprint 3", "album-merge", later));
        await db.SaveChangesAsync();

        var actions = await Service(db).ListAsync();

        Assert.Equal(2, actions.Count);
        Assert.Equal("album-merge", actions[0].Source);
        Assert.Equal(1, actions[0].SongCount);
        Assert.Equal("artist-merge", actions[1].Source);
        Assert.Equal(2, actions[1].SongCount);
        Assert.Equal(2, actions[1].ChangeCount);
        Assert.Contains(actions[1].Highlights, h => h.Contains("JAY-Z"));
        Assert.True(actions[1].Revertible);
    }

    [Fact]
    public async Task List_MarksHealBatchesNonRevertible()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(1, "/a/1.mp3"));
        db.SongMetadataChanges.Add(
            Change(1, "AlbumArtist", "Kanye west", "Kanye West", "album-identity-heal", DateTime.UtcNow));
        await db.SaveChangesAsync();

        var action = Assert.Single(await Service(db).ListAsync());
        Assert.False(action.Revertible);
        Assert.False(action.Reverted);
    }

    [Fact]
    public async Task Revert_ArtistMerge_RestoresFields_RemovesAliases_RequeuesBuilt()
    {
        await using var db = NewContext();
        var stamp = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        var built = Song(1, "/a/1.mp3");
        built.Artist = "JAY-Z";
        built.AlbumArtist = "JAY-Z";
        built.MarkBuildDone("/dest/JAY-Z/x.mp3");
        var pending = Song(2, "/a/2.mp3");
        pending.Artist = "JAY-Z";
        db.Songs.AddRange(built, pending);
        db.SongMetadataChanges.AddRange(
            Change(1, "Artist", "JAYZ", "JAY-Z", "artist-merge", stamp),
            Change(1, "AlbumArtist", "JAYZ", "JAY-Z", "artist-merge", stamp),
            Change(2, "Artist", "Jaÿ-z", "JAY-Z", "artist-merge", stamp));
        db.ArtistAliases.Add(new ArtistAlias
        {
            OwnerUserId = Owner,
            AliasKey = "jayz",
            CanonicalName = "JAY-Z",
            CreatedAtUtc = stamp,
        });
        await db.SaveChangesAsync();

        var result = await Service(db).RevertAsync(Owner, "artist-merge", stamp.Ticks);

        Assert.Equal(2, result.SongsReverted);
        Assert.Equal(3, result.ChangesReverted);
        Assert.Equal(1, result.SongsRequeued);
        Assert.Equal(1, result.AliasesRemoved);

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal("JAYZ", songs[0].Artist);
        Assert.Equal("JAYZ", songs[0].AlbumArtist);
        Assert.Equal("Jaÿ-z", songs[1].Artist);
        Assert.NotEqual(LibraryBuildStatus.Done, songs[0].LibraryBuildStatus);
        Assert.NotNull(songs[0].PreviousDestinationPath);
        Assert.Empty(await db.ArtistAliases.ToListAsync());
        Assert.All(await db.SongMetadataChanges.ToListAsync(), c => Assert.NotNull(c.RevertedAtUtc));
    }

    [Fact]
    public async Task Revert_SecondCall_RevertsNothing()
    {
        await using var db = NewContext();
        var stamp = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        var song = Song(1, "/a/1.mp3");
        song.Album = "The Blueprint 3";
        db.Songs.Add(song);
        db.SongMetadataChanges.Add(Change(1, "Album", "Blueprint 3", "The Blueprint 3", "album-merge", stamp));
        await db.SaveChangesAsync();

        var first = await Service(db).RevertAsync(Owner, "album-merge", stamp.Ticks);
        Assert.Equal(1, first.ChangesReverted);
        Assert.Equal("Blueprint 3", (await db.Songs.SingleAsync()).Album);

        var second = await Service(db).RevertAsync(Owner, "album-merge", stamp.Ticks);
        Assert.Equal(0, second.ChangesReverted);
    }

    [Fact]
    public async Task Revert_HealBatch_IsRefused()
    {
        await using var db = NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(db).RevertAsync(Owner, "album-identity-heal", DateTime.UtcNow.Ticks));
    }

    [Fact]
    public async Task Revert_MultipleChangesOnOneField_RestoresOldestValue()
    {
        // Same field written twice within one batch (defensive — merges write once, but restore
        // order must still land on the value that predates the batch).
        await using var db = NewContext();
        var stamp = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        var song = Song(1, "/a/1.mp3");
        song.Artists = "JAY-Z";
        db.Songs.Add(song);
        db.SongMetadataChanges.AddRange(
            Change(1, "Artists", "JAYZ; Kanye West", "JAY-Z; Kanye West", "artist-merge", stamp),
            Change(1, "Artists", "JAY-Z; Kanye West", "JAY-Z", "artist-merge", stamp));
        await db.SaveChangesAsync();

        await Service(db).RevertAsync(Owner, "artist-merge", stamp.Ticks);

        Assert.Equal("JAYZ; Kanye West", (await db.Songs.SingleAsync()).Artists);
    }

    private static DedupActionHistoryService Service(MusicHoarderDbContext db) => new(
        db,
        NullLogger<DedupActionHistoryService>.Instance);

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static SongMetadataChange Change(
        int songId, string field, string? oldValue, string? newValue, string source, DateTime stamp) => new()
    {
        SongId = songId,
        FieldName = field,
        OldValue = oldValue,
        NewValue = newValue,
        Source = source,
        Confidence = 1.0,
        CreatedAtUtc = stamp,
        AppliedAtUtc = stamp,
    };

    private static SongMetadata Song(int id, string sourcePath) => new()
    {
        OwnerUserId = Owner,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "Test",
        Title = Path.GetFileNameWithoutExtension(sourcePath),
    };
}
