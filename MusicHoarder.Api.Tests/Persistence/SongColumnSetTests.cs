using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

/// <summary>
/// One list of columns is the projection, the in-memory copy and what a stamp covers. A result
/// remembered under such a stamp is only safe if the three really are the same list — checked
/// here over every column the model maps, so a column added later is covered without anyone
/// remembering this file.
/// </summary>
public class SongColumnSetTests
{
    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public void Copy_CarriesTheNamedColumns_AndLeavesEveryOtherAsABlankSongHasIt()
    {
        using var db = NewContext();
        var song = SongColumnFill.Blank().FillExcept(db, seed: 3);
        song.Id = 42;
        var columns = new SongColumnSet(
            nameof(SongMetadata.Id), nameof(SongMetadata.Album), nameof(SongMetadata.Year),
            nameof(SongMetadata.IsCompilation), nameof(SongMetadata.AcquisitionIntent));

        var copy = columns.Copy(song);

        var blank = SongColumnFill.Blank();
        Assert.NotSame(song, copy);
        Assert.All(SongColumnFill.Mapped(db), column =>
            Assert.Equal(column.GetValue(columns.Names.Contains(column.Name) ? song : blank), column.GetValue(copy)));
        Assert.Equal([42], columns.Copy([song]).Select(s => s.Id));
    }

    // Column by column: a change to it changes the stamp exactly when the set names it — which is
    // also exactly when a copy carries it. Whatever a pass can read off a copy, the stamp has seen.
    [Fact]
    public void AStampChanges_WithExactlyTheColumnsACopyCarries()
    {
        using var db = NewContext();
        var filled = SongColumnFill.Blank().FillExcept(db, seed: 5);
        filled.Id = 42;
        var mapped = SongColumnFill.Mapped(db);

        Assert.All(mapped, column =>
        {
            var changed = SongColumnFill.Blank();
            column.SetValue(changed, column.GetValue(filled));
            var naming = new SongColumnSet(column.Name);
            var other = new SongColumnSet(mapped.First(c => c.Name != column.Name).Name);

            Assert.NotEqual(Stamp(naming, SongColumnFill.Blank()), Stamp(naming, changed));
            Assert.Equal(Stamp(other, SongColumnFill.Blank()), Stamp(other, changed));
            Assert.Equal(column.GetValue(filled), column.GetValue(naming.Copy(changed)));
            Assert.Equal(column.GetValue(SongColumnFill.Blank()), column.GetValue(other.Copy(changed)));
        });
    }

    [Fact]
    public void AStampOfAList_CoversHowManySongsThereAre_AndTheirOrder()
    {
        var columns = new SongColumnSet(nameof(SongMetadata.Artist), nameof(SongMetadata.Album));
        var (a, b) = (SongColumnFill.Blank(), SongColumnFill.Blank());
        (a.Artist, a.Album) = ("Kanye West", "Graduation");
        (b.Artist, b.Album) = ("JAY-Z", "The Blueprint 3");

        Assert.Equal(Stamp(columns, a, b), Stamp(columns, a, b));
        Assert.NotEqual(Stamp(columns, a, b), Stamp(columns, b, a));
        Assert.NotEqual(Stamp(columns, a), Stamp(columns, a, a));
        Assert.NotEqual(Stamp(columns), Stamp(columns, SongColumnFill.Blank()));
    }

    [Fact]
    public void Union_And_With_NameEveryColumnOnce()
    {
        var names = new SongColumnSet(nameof(SongMetadata.Artist), nameof(SongMetadata.Album));
        var ids = new SongColumnSet(nameof(SongMetadata.Id), nameof(SongMetadata.Album));

        Assert.Equal(["Album", "Artist", "Id"], SongColumnSet.Union(names, ids).Names.Order());
        Assert.Equal(["Album", "Artist", "Year"], names.With(nameof(SongMetadata.Year), nameof(SongMetadata.Album)).Names.Order());
        Assert.Equal(["Album", "Artist"], names.Names.Order());
    }

    private static byte[] Stamp(SongColumnSet columns, params SongMetadata[] songs)
    {
        using var stamp = new ContentStamp();
        columns.AddTo(stamp, songs);
        return stamp.Finish();
    }
}
