using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

public class SongMetadataLightQueryTests
{
    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task WithoutHeavyText_KeepsWhatDetectorsCompare_AndLeavesTheTextUnread()
    {
        await using var db = NewContext();
        db.Songs.Add(new SongMetadata
        {
            SourcePath = "/a.flac", FileName = "a.flac", Extension = ".flac", FileSizeBytes = 42,
            LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow,
            Artist = "Artist", AlbumArtist = "Album Artist", Album = "Album", Title = "Title", Year = 2001,
            MusicBrainzReleaseId = "release", DestinationPath = "/dest/a.flac",
            EnrichmentStatus = EnrichmentStatus.Matched,
            Fingerprint = "AQAD-fingerprint", SyncedLyrics = "[00:01.00] line", PlainLyrics = "line",
            TranscribedPlainLyrics = "heard", LastWrittenTagsJson = "{}",
        });
        await db.SaveChangesAsync();

        var song = await db.Songs.WithoutHeavyText(db).SingleAsync();

        Assert.Equal("/a.flac", song.SourcePath);
        Assert.Equal("Album Artist", song.AlbumArtist);
        Assert.Equal("Album", song.Album);
        Assert.Equal(2001, song.Year);
        Assert.Equal("release", song.MusicBrainzReleaseId);
        Assert.Equal("/dest/a.flac", song.DestinationPath);
        Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
        Assert.Null(song.Fingerprint);
        Assert.Null(song.SyncedLyrics);
        Assert.Null(song.PlainLyrics);
        Assert.Null(song.TranscribedPlainLyrics);
        Assert.Null(song.LastWrittenTagsJson);
        Assert.Equal(EntityState.Detached, db.Entry(song).State);
    }

    // A new column must be carried without anyone remembering this file: everything the model maps
    // and code can set is read, except the named heavy ones.
    [Fact]
    public void Projection_CoversEveryMappedSettableProperty_ExceptTheHeavyOnes()
    {
        using var db = NewContext();
        var bound = ((System.Linq.Expressions.MemberInitExpression)SongMetadataLightQuery.Build(db).Body)
            .Bindings.Select(b => b.Member.Name).ToHashSet();
        var mapped = db.Model.FindEntityType(typeof(SongMetadata))!.GetProperties()
            .Where(p => p.PropertyInfo is { SetMethod.IsPublic: true })
            .Select(p => p.Name).ToHashSet();

        Assert.Equal(mapped.Except(SongMetadataLightQuery.HeavyColumns).Order(), bound.Order());
        Assert.All(SongMetadataLightQuery.HeavyColumns, name => Assert.Contains(name, mapped));
        Assert.True(bound.Count > 100);
    }

    // The include list is the opposite promise: a column that is not named is not read, however
    // many the entity has grown since. Checked over every mapped column rather than a sample.
    [Fact]
    public async Task WithOnly_ReadsTheNamedColumns_AndLeavesEveryOtherAsABlankSongHasIt()
    {
        await using var db = NewContext();
        var stored = new SongMetadata
        {
            SourcePath = "/a.flac", FileName = "a.flac", Extension = ".flac", FileSizeBytes = 42,
            LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow,
        }.FillExcept(db, seed: 1);
        db.Songs.Add(stored);
        await db.SaveChangesAsync();
        var columns = new SongColumnSet(
            nameof(SongMetadata.Id), nameof(SongMetadata.OwnerUserId), nameof(SongMetadata.Album),
            nameof(SongMetadata.Year), nameof(SongMetadata.IsCompilation), nameof(SongMetadata.AcquisitionIntent));

        var read = await db.Songs.WithOnly(db, columns).SingleAsync();

        var blank = SongColumnFill.Blank();
        Assert.All(SongColumnFill.Mapped(db), column =>
        {
            var expected = columns.Names.Contains(column.Name) ? stored : blank;
            Assert.Equal(column.GetValue(expected), column.GetValue(read));
            // Otherwise "left blank" proves nothing about this column.
            Assert.NotEqual(column.GetValue(blank), column.GetValue(stored));
        });
        Assert.Equal(EntityState.Detached, db.Entry(read).State);
    }

    [Fact]
    public void SongColumnSet_RefusesANameThatIsNotAColumn()
    {
        using var db = NewContext();

        Assert.Throws<ArgumentException>(() => new SongColumnSet("Albun"));
        // Computed from other columns: there is nothing to select.
        Assert.Throws<ArgumentException>(() => new SongColumnSet(nameof(SongMetadata.TrackLabel)));
        // Settable, but a navigation — selecting it would load the row it points at.
        var navigation = new SongColumnSet(nameof(SongMetadata.DuplicateOf));
        Assert.Throws<InvalidOperationException>(() => db.Songs.WithOnly(db, navigation));
    }
}
