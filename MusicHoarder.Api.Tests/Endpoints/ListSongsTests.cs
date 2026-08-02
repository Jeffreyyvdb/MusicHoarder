using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class ListSongsTests
{
    [Fact]
    public async Task ListSongs_IncludesFingerprintInPayload()
    {
        await using var db = NewContext();
        var song = NewSong("/a.mp3", "a.mp3");
        song.Fingerprint = "AQADtMkUhEL-fingerprint-sample";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db);

        var first = SingleSong(result);
        Assert.Equal("AQADtMkUhEL-fingerprint-sample", GetProperty<string?>(first, "Fingerprint"));
    }

    [Fact]
    public async Task ListSongs_NullFingerprint_IsExposedAsNull()
    {
        await using var db = NewContext();
        db.Songs.Add(NewSong("/b.mp3", "b.mp3"));
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db);

        var first = SingleSong(result);
        Assert.Null(GetProperty<string?>(first, "Fingerprint"));
    }

    [Fact]
    public async Task ListSongs_ClassifiesATrackerUnreleasedMatch()
    {
        await using var db = NewContext();
        var song = NewSong("/leak.mp3", "leak.mp3");
        song.MatchedBy = "YeTracker";
        song.MatchWarnings = """["category:unreleased"]""";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db);

        var first = SingleSong(result);
        Assert.Equal("Unreleased", GetProperty<string>(first, "ReleaseClassification"));
    }

    [Fact]
    public async Task ListSongs_ClassifiesACommercialMatchAsReleased()
    {
        await using var db = NewContext();
        var song = NewSong("/single.mp3", "single.mp3");
        song.MatchedBy = "SpotifyAPI";
        song.SpotifyId = "6f2Y5W6t1E";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db);

        var first = SingleSong(result);
        Assert.Equal("Released", GetProperty<string>(first, "ReleaseClassification"));
    }

    [Fact]
    public async Task ListSongs_ReviewWithNoCandidateAtAll_ClassifiesAsLikelyUnreleased()
    {
        await using var db = NewContext();
        var song = NewSong("/nowhere.mp3", "nowhere.mp3");
        song.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        song.EnrichedAtUtc = DateTime.UtcNow;
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db);

        var first = SingleSong(result);
        Assert.Equal("LikelyUnreleased", GetProperty<string>(first, "ReleaseClassification"));
    }

    [Fact]
    public async Task ListSongs_UnmatchedSong_ClassifiesAsUnknown()
    {
        await using var db = NewContext();
        db.Songs.Add(NewSong("/c.mp3", "c.mp3"));
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db);

        var first = SingleSong(result);
        Assert.Equal("Unknown", GetProperty<string>(first, "ReleaseClassification"));
    }

    private static object SingleSong(IResult result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result)!;
        var songs = (IEnumerable)value.GetType().GetProperty("Songs")!.GetValue(value)!;
        return songs.Cast<object>().Single();
    }

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property '{name}' not found on {obj.GetType()}");
        return (T)prop.GetValue(obj)!;
    }

    private static SongMetadata NewSong(string sourcePath, string fileName) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = fileName,
        Extension = Path.GetExtension(fileName),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
    };

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
