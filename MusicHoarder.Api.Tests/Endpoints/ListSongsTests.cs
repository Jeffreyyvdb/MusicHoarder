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

        var result = await SongsEndpoints.ListSongs(db);

        var first = SingleSong(result);
        Assert.Equal("AQADtMkUhEL-fingerprint-sample", GetProperty<string?>(first, "Fingerprint"));
    }

    [Fact]
    public async Task ListSongs_NullFingerprint_IsExposedAsNull()
    {
        await using var db = NewContext();
        db.Songs.Add(NewSong("/b.mp3", "b.mp3"));
        await db.SaveChangesAsync();

        var result = await SongsEndpoints.ListSongs(db);

        var first = SingleSong(result);
        Assert.Null(GetProperty<string?>(first, "Fingerprint"));
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
