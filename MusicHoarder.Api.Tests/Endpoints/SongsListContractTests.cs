using System.Collections;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// What one row of <c>GET /songs</c> costs, pinned.
///
/// <para>
/// Every client downloads that response whole, ten thousand rows and more, so a field there costs its
/// name and value once per song on every cold start. The rule (CLAUDE.md, "The library is thousands of
/// rows") is that it carries only what a list or a player reads; a single track's pane fetches the
/// rest from <c>/songs/{id}/enrichment-detail</c> or <c>/api/tracks/{id}/lyrics</c>. A perf audit on
/// 2026-11-15 found thirteen fields each read by one detail pane only, which had crept in one by one.
/// This test is what stops the fourteenth: adding a key fails here until it is added to the list
/// below with a line saying which list reads it. Rows shared with you are pinned separately, by
/// <c>SharedProjectionSurfaceTests</c>.
/// </para>
/// </summary>
public class SongsListContractTests
{
    /// <summary>
    /// Every key of an own row. Order is irrelevant. Before adding one, name the list or player that
    /// reads it; a detail pane is not a reason (extend the detail endpoint instead).
    /// </summary>
    private static readonly string[] OwnRowKeys =
    [
        // Identity and the file: every row, the player, the cover/stream URLs.
        "Id", "SourcePath", "FileName", "Extension", "FileSizeBytes", "DestinationPath",
        // "Recently added" ordering, and the include-deleted triage view.
        "AcquiredAtUtc", "DeletedAtUtc",
        // What a row shows, groups and sorts by.
        "Artist", "Artists", "AlbumArtist", "Album", "Title", "Year", "TrackNumber",
        "DurationSeconds", "DurationMs", "Bitrate", "Genre", "ReleaseDate", "Label",
        // The cover and video affordances on a row. CoverVersion only appears when HasCoverArt
        // (nulls are not written), and is what lets a cover URL be cached immutably.
        "HasCoverArt", "CoverVersion", "HasMusicVideo",
        // Catalog ids: duplicate detection, Spotify matching on the Add pages, album grouping.
        "Isrc", "MusicBrainzId", "MusicBrainzReleaseId", "SpotifyId", "AcoustIdTrackId", "LrclibId",
        // Pipeline state: Tag review's queue, the Pipeline page's lists, the library's filters.
        "HasFingerprint", "EnrichmentStatus", "MatchedBy", "MatchConfidence", "MatchWarnings",
        "ReleaseClassification", "OriginalMetadataCaptured", "IsDuplicate", "DuplicateOfId",
        "LibraryBuildStatus", "IsBuilt",
        // Lyrics flags: the player's Lyrics button and badge, before any text is fetched.
        "LyricsStatus", "HasSyncedLyrics", "HasPlainLyrics", "IsInstrumental", "HasTranscribedLyrics",
        "PreferredLyricsSource", "LyricsProvenance", "LyricsSyncStatus", "LyricsSyncOffsetMs",
        // Likes and plays: Liked Songs, Recently played, the heart on every row.
        "LikedAtUtc", "PlayCount", "LastPlayedAtUtc",
        // Provenance and Spotify save dates: the library's "Spotify Liked" and "My music" filters.
        "OriginKind", "OriginSource", "OriginDetail", "SpotifyAddedAtUtc", "SpotifyLikedAtUtc",
        "AcquisitionIntent", "IsAlbumFill",
    ];

    /// <summary>
    /// A realistic matched and built row, written the way the endpoint writes it, may not grow past
    /// this. It is 1,536 bytes today, with nulls left out; the 64 bytes of headroom are for a renamed
    /// value, not for a new field.
    /// </summary>
    private const int OwnRowByteBudget = 1_600;

    [Fact]
    public async Task An_own_row_carries_exactly_the_pinned_keys()
    {
        await using var db = NewContext();
        db.Songs.Add(RealisticSong());
        await db.SaveChangesAsync();

        var row = SingleRow(await ListSongsCaller.Invoke(db));
        var actual = row.GetType().GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal);

        Assert.Equal(OwnRowKeys.Order(StringComparer.Ordinal), actual);
    }

    [Theory]
    // Each read by one detail pane only; that pane fetches them per track.
    [InlineData("Copyright")]
    [InlineData("Composer")]
    [InlineData("CatalogNumber")]
    [InlineData("Upc")]
    [InlineData("OriginalArtist")]
    [InlineData("OriginalAlbum")]
    [InlineData("OriginalTitle")]
    [InlineData("EnrichmentError")]
    [InlineData("IndexedAtUtc")]
    [InlineData("LibraryBuiltAtUtc")]
    [InlineData("TranscriptionModel")]
    [InlineData("TranscribedAtUtc")]
    [InlineData("LyricsSyncIssue")]
    // Kilobytes per song.
    [InlineData("Fingerprint")]
    [InlineData("SyncedLyrics")]
    [InlineData("PlainLyrics")]
    public void Detail_only_fields_stay_out_of_the_list(string field)
    {
        Assert.DoesNotContain(field, OwnRowKeys);
    }

    [Fact]
    public async Task A_realistic_row_stays_inside_its_byte_budget()
    {
        await using var db = NewContext();
        db.Songs.Add(RealisticSong());
        await db.SaveChangesAsync();

        using var body = JsonDocument.Parse(await Written(await ListSongsCaller.Invoke(db)));
        var row = body.RootElement.GetProperty("songs")[0].GetRawText();

        Assert.True(
            Encoding.UTF8.GetByteCount(row) <= OwnRowByteBudget,
            $"A /songs row is {Encoding.UTF8.GetByteCount(row)} bytes, over its {OwnRowByteBudget}-byte budget:\n{row}");
    }

    [Fact]
    public async Task Null_fields_are_not_written()
    {
        // A freshly scanned file: no tags beyond its name, no match, never played.
        await using var db = NewContext();
        db.Songs.Add(NewSong("/bare.mp3"));
        await db.SaveChangesAsync();

        var json = await Written(await ListSongsCaller.Invoke(db));
        using var body = JsonDocument.Parse(json);
        var row = body.RootElement.GetProperty("songs")[0];

        Assert.DoesNotContain(":null", json);
        Assert.False(row.TryGetProperty("isrc", out _));
        Assert.False(row.TryGetProperty("likedAtUtc", out _));
        // Non-null values are still written, false and zero included.
        Assert.Equal(0, row.GetProperty("playCount").GetInt32());
        Assert.False(row.GetProperty("isBuilt").GetBoolean());
        // The review queue's original tags are absent, not null, on the library list.
        Assert.False(body.RootElement.TryGetProperty("originalTags", out _));
    }

    [Fact]
    public async Task The_result_says_it_is_a_200_so_the_ETag_filter_tags_it()
    {
        await using var db = NewContext();
        var result = await ListSongsCaller.Invoke(db);

        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public void A_grantor_without_a_display_name_still_writes_the_key()
    {
        var json = JsonSerializer.Serialize(
            new MusicHoarder.Api.Contracts.GrantorDto(Guid.NewGuid(), null, 3), SongsEndpoints.SongsListJson);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("displayName").ValueKind);
    }

    [Fact]
    public async Task A_row_from_before_the_acquisition_column_falls_back_to_its_oldest_stamp()
    {
        await using var db = NewContext();
        var song = NewSong("/old.mp3");
        song.AcquiredAtUtc = null;
        song.IndexedAtUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        song.LibraryBuiltAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fresh = NewSong("/fresh.mp3");
        fresh.AcquiredAtUtc = null;
        fresh.IndexedAtUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Songs.AddRange(song, fresh);
        await db.SaveChangesAsync();

        var rows = Rows(await ListSongsCaller.Invoke(db))
            .ToDictionary(r => (string)r.GetType().GetProperty("SourcePath")!.GetValue(r)!);

        Assert.Equal(song.LibraryBuiltAtUtc, Acquired(rows["/old.mp3"]));
        Assert.Equal(fresh.IndexedAtUtc, Acquired(rows["/fresh.mp3"]));

        static DateTime? Acquired(object row) =>
            (DateTime?)row.GetType().GetProperty("AcquiredAtUtc")!.GetValue(row);
    }

    [Fact]
    public async Task Tag_reviews_queue_gets_the_original_tags_beside_the_rows()
    {
        await using var db = NewContext();
        var healed = NewSong("/healed.mp3");
        healed.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        healed.Title = "Healed title";
        healed.OriginalMetadataCaptured = true;
        healed.OriginalTitle = "track 01";
        healed.OriginalArtist = "unknown artist";
        var untouched = NewSong("/untouched.mp3");
        untouched.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        var matched = NewSong("/matched.mp3");
        matched.EnrichmentStatus = EnrichmentStatus.Matched;
        matched.OriginalMetadataCaptured = true;
        matched.OriginalTitle = "not in the queue";
        db.Songs.AddRange(healed, untouched, matched);
        await db.SaveChangesAsync();

        using var queue = JsonDocument.Parse(
            await Written(await ListSongsCaller.Invoke(db, enrichmentStatus: "needsreview")));
        var tags = queue.RootElement.GetProperty("originalTags");
        var only = Assert.Single(tags.EnumerateObject());
        Assert.Equal(healed.Id.ToString(), only.Name);
        Assert.Equal("track 01", only.Value.GetProperty("title").GetString());
        Assert.Equal("unknown artist", only.Value.GetProperty("artist").GetString());
        Assert.False(only.Value.TryGetProperty("album", out _));

        using var library = JsonDocument.Parse(await Written(await ListSongsCaller.Invoke(db)));
        Assert.False(library.RootElement.TryGetProperty("originalTags", out _));
    }

    [Fact]
    public async Task The_track_detail_carries_what_left_the_list()
    {
        await using var db = NewContext();
        var song = RealisticSong();
        song.EnrichmentError = "no provider matched";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await SongsEndpoints.GetEnrichmentDetail(song.Id, db);
        using var detail = JsonDocument.Parse(await Written(result));
        var root = detail.RootElement;

        Assert.Equal("℗ 2019 Example Records", root.GetProperty("copyright").GetString());
        Assert.Equal("M. Davis", root.GetProperty("composer").GetString());
        Assert.Equal("EX-001", root.GetProperty("catalogNumber").GetString());
        Assert.Equal("0123456789012", root.GetProperty("upc").GetString());
        Assert.Equal("no provider matched", root.GetProperty("enrichmentError").GetString());
        Assert.Equal(song.IndexedAtUtc, root.GetProperty("indexedAtUtc").GetDateTime());
        Assert.Equal(song.LibraryBuiltAtUtc, root.GetProperty("libraryBuiltAtUtc").GetDateTime());
        Assert.Equal("01 kind of blue", root.GetProperty("source").GetProperty("title").GetString());
    }

    /// <summary>The body exactly as the result writes it to the wire.</summary>
    private static async Task<string> Written(IResult result)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        using var stream = new MemoryStream();
        http.Response.Body = stream;
        await result.ExecuteAsync(http);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static List<object> Rows(IResult result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result)!;
        return ((IEnumerable)value.GetType().GetProperty("Songs")!.GetValue(value)!).Cast<object>().ToList();
    }

    private static object SingleRow(IResult result) => Assert.Single(Rows(result));

    private static SongMetadata RealisticSong()
    {
        var song = NewSong("/music/source/Miles Davis/Kind of Blue/01 So What.flac");
        song.Artist = "Miles Davis";
        song.Artists = "Miles Davis";
        song.AlbumArtist = "Miles Davis";
        song.Album = "Kind of Blue (Legacy Edition)";
        song.Title = "So What";
        song.Year = 1959;
        song.TrackNumber = 1;
        song.DurationSeconds = 562;
        song.DurationMs = 562_093;
        song.Bitrate = 1_011;
        song.HasCoverArt = true;
        song.Fingerprint = "AQADtMkUhEL";
        song.Isrc = "USSM15900113";
        song.MusicBrainzId = "4b7f2c56-1f4c-4d8b-9a3e-2f6d5e1c0a9b";
        song.MusicBrainzReleaseId = "8c1d3e5f-7a9b-4c2d-8e6f-1a3b5c7d9e0f";
        song.SpotifyId = "4vLYewWIvqHfKtJDk8c8tq";
        song.AcoustIdTrackId = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d";
        song.LrclibId = "123456";
        song.Genre = "Jazz; Modal Jazz";
        song.ReleaseDate = "1959-08-17";
        song.Label = "Columbia";
        song.CatalogNumber = "EX-001";
        song.Upc = "0123456789012";
        song.Composer = "M. Davis";
        song.Copyright = "℗ 2019 Example Records";
        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.MatchedBy = "AcoustID";
        song.MatchConfidence = 0.97;
        song.MatchWarnings = "[\"duration differs by 2s\"]";
        song.OriginalMetadataCaptured = true;
        song.OriginalTitle = "01 kind of blue";
        song.OriginalArtist = "miles davis";
        song.OriginalAlbum = "kind of blue";
        song.LibraryBuildStatus = LibraryBuildStatus.Done;
        song.LibraryBuiltAtUtc = new DateTime(2025, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        song.DestinationPath = "/music/library/Miles Davis/Kind of Blue (Legacy Edition)/01 - So What.flac";
        song.LyricsStatus = LyricsStatus.Instrumental;
        song.IsInstrumental = true;
        song.LikedAtUtc = new DateTime(2025, 4, 1, 9, 30, 0, DateTimeKind.Utc);
        song.PlayCount = 42;
        song.LastPlayedAtUtc = new DateTime(2026, 11, 14, 21, 5, 0, DateTimeKind.Utc);
        return song;
    }

    private static SongMetadata NewSong(string sourcePath) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 31_457_280,
        LastModifiedUtc = new DateTime(2025, 3, 1, 8, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2025, 3, 1, 9, 0, 0, DateTimeKind.Utc),
        AcquiredAtUtc = new DateTime(2025, 3, 1, 8, 0, 0, DateTimeKind.Utc),
    };

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
