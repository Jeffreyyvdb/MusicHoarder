using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class ListSongsTests
{
    // The list says whether a song has a fingerprint and never carries the value: a Chromaprint
    // fingerprint is kilobytes per song, and it was half of the response.
    [Fact]
    public async Task ListSongs_SaysASongHasAFingerprint_WithoutCarryingIt()
    {
        await using var db = NewContext();
        var song = NewSong("/a.mp3", "a.mp3");
        song.Fingerprint = "AQADtMkUhEL-fingerprint-sample";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db);

        var first = SingleSong(result);
        Assert.True(GetProperty<bool>(first, "HasFingerprint"));
        Assert.Null(first.GetType().GetProperty("Fingerprint"));
    }

    [Fact]
    public async Task ListSongs_NullFingerprint_IsNoFingerprint()
    {
        await using var db = NewContext();
        db.Songs.Add(NewSong("/b.mp3", "b.mp3"));
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db);

        var first = SingleSong(result);
        Assert.False(GetProperty<bool>(first, "HasFingerprint"));
    }

    [Fact]
    public async Task ListSongs_ReportsLyricsAsFlags_NotText()
    {
        await using var db = NewContext();
        var song = NewSong("/c.mp3", "c.mp3");
        song.SyncedLyrics = "[00:01.00] line";
        song.PlainLyrics = "   ";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db);

        var first = SingleSong(result);
        Assert.True(GetProperty<bool>(first, "HasSyncedLyrics"));
        Assert.True(GetProperty<bool>(first, "HasPlainLyrics"));
        Assert.Null(first.GetType().GetProperty("SyncedLyrics"));
        Assert.Null(first.GetType().GetProperty("PlainLyrics"));
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

    /// <summary>
    /// Both clients used to work these out for themselves — one from a stringly-typed enum, the
    /// other from a status-plus-path pair — which is two definitions of a server fact. The endpoint
    /// answers now; the clients only fall back for a server older than the fields.
    /// </summary>
    [Fact]
    public async Task ListSongs_DecidesBuiltAndAlbumFillForTheClients()
    {
        await using var db = NewContext();
        var song = NewSong("/filled.flac", "filled.flac");
        song.AcquisitionIntent = SongAcquisitionIntent.AlbumFill;
        song.MarkBuildDone("/dest/Artist/Album/01 - filled.flac");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var first = SingleSong(await ListSongsCaller.Invoke(db));

        Assert.True(GetProperty<bool>(first, "IsAlbumFill"));
        Assert.True(GetProperty<bool>(first, "IsBuilt"));
        // The enum name stays on the wire: shipped Android builds read it.
        Assert.Equal("AlbumFill", GetProperty<string>(first, "AcquisitionIntent"));
    }

    [Fact]
    public async Task ListSongs_AnUnbuiltTrackYouAskedFor_ReportsBothFlagsFalse()
    {
        await using var db = NewContext();
        db.Songs.Add(NewSong("/mine.mp3", "mine.mp3"));
        await db.SaveChangesAsync();

        var first = SingleSong(await ListSongsCaller.Invoke(db));

        Assert.False(GetProperty<bool>(first, "IsAlbumFill"));
        Assert.False(GetProperty<bool>(first, "IsBuilt"));
    }

    [Fact]
    public async Task ListSongs_ATrackStillCopying_IsNotBuiltYet()
    {
        // Done AND a destination path — a row part-way through the build has one without the other.
        await using var db = NewContext();
        var song = NewSong("/copying.mp3", "copying.mp3");
        song.LibraryBuildStatus = LibraryBuildStatus.Copied;
        song.DestinationPath = "/dest/Artist/Album/01 - copying.mp3";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var first = SingleSong(await ListSongsCaller.Invoke(db));

        Assert.False(GetProperty<bool>(first, "IsBuilt"));
    }

    [Fact]
    public async Task ListSongs_OrdersByInvariantCultureWithDeterministicIdTieBreak()
    {
        await using var db = NewContext();
        var zedd = NewSong("/z.mp3", "z.mp3"); zedd.Artist = "Zedd"; zedd.Title = "Clarity";
        var alice = NewSong("/a.mp3", "a.mp3"); alice.Artist = "alice"; alice.Title = "Song";
        var olafur = NewSong("/o.mp3", "o.mp3"); olafur.Artist = "Ólafur"; olafur.Title = "Near Light";
        db.Songs.AddRange(zedd, alice, olafur);
        await db.SaveChangesAsync();

        var value = (await ListSongsCaller.Invoke(db)).GetType().GetProperty("Value")!.GetValue(await ListSongsCaller.Invoke(db))!;
        var songs = ((IEnumerable)value.GetType().GetProperty("Songs")!.GetValue(value)!).Cast<object>().ToList();
        var artists = songs.Select(s => GetProperty<string>(s, "Artist")).ToList();

        Assert.Equal(["alice", "Ólafur", "Zedd"], artists);
    }

    [Fact]
    public async Task PresenceFlags_AreForcedModifiedWhenTextChangesEvenIfBoolWasAlreadyTrue()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(dbName).Options;

        int id;
        await using (var seed = new MusicHoarderDbContext(options))
        {
            var s = NewSong("/fp.mp3", "fp.mp3");
            s.Fingerprint = "fp-1";
            s.PlainLyrics = "hello";
            s.SyncedLyrics = "[00:01.00] hello";
            seed.Songs.Add(s);
            await seed.SaveChangesAsync();
            id = s.Id;
        }

        // Context A loads with HasFingerprint = true; Context B clears it to false;
        // Context A then writes a new non-empty value. SyncSongPresenceFlags must force
        // HasFingerprint = true into Context A's save so it cannot stay false.
        await using var ctxA = new MusicHoarderDbContext(options);
        await using var ctxB = new MusicHoarderDbContext(options);

        var rowA = await ctxA.Songs.SingleAsync(s => s.Id == id);
        var rowB = await ctxB.Songs.SingleAsync(s => s.Id == id);

        rowB.Fingerprint = null;
        rowB.PlainLyrics = "   ";
        await ctxB.SaveChangesAsync();

        rowA.Fingerprint = "fp-2";
        rowA.PlainLyrics = "updated lyrics";
        await ctxA.SaveChangesAsync();

        await using var verify = new MusicHoarderDbContext(options);
        var saved = await verify.Songs.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.Equal("fp-2", saved.Fingerprint);
        Assert.True(saved.HasFingerprint);
        Assert.True(saved.HasPlainLyrics);
        Assert.True(saved.HasNonBlankPlainLyrics);
    }

    [Fact]
    public async Task TranscribedLyrics_ConcurrentSaveWritesBothColumnsWithFlag()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(dbName).Options;

        int id;
        await using (var seed = new MusicHoarderDbContext(options))
        {
            var s = NewSong("/tx.mp3", "tx.mp3");
            s.TranscribedSyncedLyrics = "[00:01.00] synced";
            seed.Songs.Add(s);
            await seed.SaveChangesAsync();
            id = s.Id;
        }

        // Context A replaces synced with plain-only; Context B loaded synced-only and clears both.
        // Because SyncSongPresenceFlags marks both transcription columns and HasTranscribedLyrics
        // modified together, the winning write leaves text and boolean 100% consistent.
        await using var ctxA = new MusicHoarderDbContext(options);
        await using var ctxB = new MusicHoarderDbContext(options);

        var rowA = await ctxA.Songs.SingleAsync(s => s.Id == id);
        var rowB = await ctxB.Songs.SingleAsync(s => s.Id == id);

        rowA.TranscribedSyncedLyrics = null;
        rowA.TranscribedPlainLyrics = "plain only";
        await ctxA.SaveChangesAsync();

        rowB.TranscribedSyncedLyrics = null;
        rowB.TranscribedPlainLyrics = null;
        await ctxB.SaveChangesAsync();

        await using var verify = new MusicHoarderDbContext(options);
        var saved = await verify.Songs.AsNoTracking().SingleAsync(s => s.Id == id);
        var hasText = !string.IsNullOrEmpty(saved.TranscribedSyncedLyrics) || !string.IsNullOrEmpty(saved.TranscribedPlainLyrics);
        Assert.Equal(hasText, saved.HasTranscribedLyrics);
        Assert.Null(saved.TranscribedPlainLyrics);
        Assert.False(saved.HasTranscribedLyrics);
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
