using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class CanonicalAlbumConsolidatorTests
{
    private static readonly DateTime EnrichedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ConsolidateAsync_UnifiesSplitAlbum_FixesTrackNumbers_AndRenamesToCanonical()
    {
        await using var db = NewContext();
        SeedCanonical(db);

        // Owned: plain "A Love Letter to You 4" name, mixed years, duplicate track #1 and #12.
        db.Songs.AddRange(
            OwnedSong("/i-love-you.flac", title: "I Love You", trackNumber: 1, year: 2020),
            OwnedSong("/leray.flac", title: "Leray", trackNumber: 1, year: 2019),          // wrong #1
            OwnedSong("/love-me-more.flac", title: "Love Me More", trackNumber: 12, year: 2020),
            OwnedSong("/sickening.flac", title: "Sickening", trackNumber: 12, year: 2020)); // wrong #12
        await db.SaveChangesAsync();

        var result = await Consolidator().ConsolidateAsync(db, "Trippie Redd", "A Love Letter to You 4", CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.True(result.CanonicalFound);
        Assert.Equal(4, result.Matched);
        Assert.Equal(4, result.Requeued);
        Assert.Equal(0, result.Unmatched);

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();

        // All adopt the canonical Deluxe title and canonical year.
        Assert.All(songs, s => Assert.Equal("A Love Letter To You 4 (Deluxe)", s.Album));
        Assert.All(songs, s => Assert.Equal(2019, s.Year));

        // Track numbers are now unique and canonical (no more duplicate #1 / #12).
        Assert.Equal(new[] { 1, 9, 12, 20 }, songs.Select(s => s.TrackNumber!.Value).OrderBy(n => n).ToArray());
        Assert.Equal(20, songs.Single(s => s.Title == "Sickening").TrackNumber);
        Assert.Equal(9, songs.Single(s => s.Title == "Leray").TrackNumber);

        // Re-queued for retag: off Done, force-rebuild signal set.
        Assert.All(songs, s => Assert.Equal(LibraryBuildStatus.Pending, s.LibraryBuildStatus));
        Assert.All(songs, s => Assert.False(string.IsNullOrEmpty(s.PreviousDestinationPath)));

        // Grade-staleness guard: enrichment timestamp untouched, so no auto-regrade is triggered.
        Assert.All(songs, s => Assert.Equal(EnrichedAt, s.EnrichedAtUtc));

        // Reversible history recorded.
        var changes = await db.SongMetadataChanges.ToListAsync();
        Assert.NotEmpty(changes);
        Assert.All(changes, c => Assert.Equal("canonical-album", c.Source));
        Assert.All(changes, c => Assert.NotNull(c.AppliedAtUtc));
    }

    [Fact]
    public async Task ConsolidateAsync_NoCanonicalAlbum_ReturnsNotFound_AndLeavesSongsUntouched()
    {
        await using var db = NewContext();
        db.Songs.Add(OwnedSong("/x.flac", title: "X", trackNumber: 3, year: 2020));
        await db.SaveChangesAsync();

        var result = await Consolidator().ConsolidateAsync(db, "Trippie Redd", "A Love Letter to You 4", CancellationToken.None);

        Assert.False(result.CanonicalFound);
        var song = await db.Songs.SingleAsync();
        Assert.Equal("A Love Letter to You 4", song.Album);
        Assert.Equal(LibraryBuildStatus.Done, song.LibraryBuildStatus);
        Assert.Empty(await db.SongMetadataChanges.ToListAsync());
    }

    [Fact]
    public async Task ConsolidateAsync_PendingCanonicalAlbum_TreatedAsNotFound()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "trippie redd", AlbumKey = "a love letter to you 4",
            DisplayTitle = "A Love Letter To You 4 (Deluxe)", Year = 2019, Status = CanonicalAlbumStatus.Pending,
        });
        db.Songs.Add(OwnedSong("/i-love-you.flac", title: "I Love You", trackNumber: 1, year: 2020));
        await db.SaveChangesAsync();

        var result = await Consolidator().ConsolidateAsync(db, "Trippie Redd", "A Love Letter to You 4", CancellationToken.None);

        Assert.False(result.CanonicalFound);
    }

    [Fact]
    public async Task ConsolidateAsync_UnmatchedSong_FallsBackPerSong()
    {
        await using var db = NewContext();
        SeedCanonical(db);
        db.Songs.AddRange(
            OwnedSong("/i-love-you.flac", title: "I Love You", trackNumber: 1, year: 2020),
            OwnedSong("/bonus.flac", title: "Some Unrelated Bonus Track", trackNumber: 30, year: 2020));
        await db.SaveChangesAsync();

        var result = await Consolidator().ConsolidateAsync(db, "Trippie Redd", "A Love Letter to You 4", CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Unmatched);

        var bonus = await db.Songs.SingleAsync(s => s.Title == "Some Unrelated Bonus Track");
        Assert.Equal("A Love Letter to You 4", bonus.Album);            // untouched
        Assert.Equal(LibraryBuildStatus.Done, bonus.LibraryBuildStatus); // not re-queued
    }

    [Fact]
    public async Task ConsolidateAsync_UnifiesAlbumArtist_FromCanonicalDisplayArtist()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "lauryn hill",
            AlbumKey = "the miseducation of lauryn hill",
            DisplayTitle = "The Miseducation of Lauryn Hill",
            DisplayArtist = "Ms. Lauryn Hill", // canonical, authoritative spelling
            Year = 1998,
            Status = CanonicalAlbumStatus.Fetched,
            Tracks = [new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 1, Title = "Lost Ones" }],
        });
        var song = OwnedSong("/lost-ones.flac", title: "Lost Ones", trackNumber: 1, year: 1998);
        song.Artist = "Lauryn Hill";
        song.AlbumArtist = "Lauryn Hill"; // divergent per-track spelling
        song.Album = "The Miseducation of Lauryn Hill";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await Consolidator().ConsolidateAsync(db, "Lauryn Hill", "The Miseducation of Lauryn Hill", CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(1, result.Matched);
        var updated = await db.Songs.SingleAsync();
        Assert.Equal("Ms. Lauryn Hill", updated.AlbumArtist);
        Assert.Contains(await db.SongMetadataChanges.ToListAsync(),
            c => c.FieldName == nameof(SongMetadata.AlbumArtist) && c.NewValue == "Ms. Lauryn Hill");
    }

    private static void SeedCanonical(MusicHoarderDbContext db) => db.CanonicalAlbums.Add(new CanonicalAlbum
    {
        ArtistKey = "trippie redd",
        AlbumKey = "a love letter to you 4",
        DisplayTitle = "A Love Letter To You 4 (Deluxe)",
        DisplayArtist = "Trippie Redd",
        Year = 2019,
        Status = CanonicalAlbumStatus.Fetched,
        ResolvedTrackCount = 4,
        Tracks =
        [
            new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 1, Title = "I Love You" },
            new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 9, Title = "Leray" },
            new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 12, Title = "Love Me More" },
            new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 20, Title = "Sickening" },
        ],
    });

    private static ICanonicalAlbumConsolidator Consolidator() => new CanonicalAlbumConsolidator(
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = "/source", DestinationDirectory = "/dest" }),
        NullLogger<CanonicalAlbumConsolidator>.Instance);

    private static SongMetadata OwnedSong(string sourcePath, string title, int trackNumber, int year) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        EnrichedAtUtc = EnrichedAt,
        OriginalMetadataCaptured = true,
        Artist = "Trippie Redd",
        AlbumArtist = "Trippie Redd",
        Album = "A Love Letter to You 4",
        Title = title,
        TrackNumber = trackNumber,
        DiscNumber = 1,
        Year = year,
        LibraryBuildStatus = LibraryBuildStatus.Done,
        DestinationPath = $"/dest/Trippie Redd/{year} - A Love Letter to You 4/{trackNumber:00} - {title}.flac",
    };

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
