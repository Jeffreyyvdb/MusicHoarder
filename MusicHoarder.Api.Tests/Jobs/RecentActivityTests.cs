using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Jobs;

public class RecentActivityTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private static RecentActivityFacts Facts(
        int id,
        string? title = "Song",
        string? artist = "Artist",
        string? album = null,
        string fileName = "f67417a4.flac",
        DateTime? acquired = null,
        DateTime? indexed = null,
        DateTime? enriched = null,
        DateTime? built = null,
        EnrichmentStatus enrichment = EnrichmentStatus.Pending,
        LibraryBuildStatus build = LibraryBuildStatus.Pending,
        bool inLibrary = false,
        bool wasEverBuilt = false) =>
        new(id, title, artist, null, album, fileName,
            acquired, indexed ?? Now.AddDays(-30), enriched, null, built, null,
            enrichment, build, inLibrary, wasEverBuilt || inLibrary);

    [Fact]
    public void A_retag_of_an_old_song_makes_no_new_event()
    {
        var retagged = Facts(1, acquired: Now.AddDays(-21), built: Now, enrichment: EnrichmentStatus.Matched,
            build: LibraryBuildStatus.Done, inLibrary: true, enriched: Now);
        var fresh = Facts(2, acquired: Now.AddHours(-1), enrichment: EnrichmentStatus.Matched, inLibrary: true);

        var events = RecentActivity.Derive([retagged, fresh], 10);

        Assert.Equal(["added-2", "added-1"], events.Select(e => e.Id));
        Assert.Equal(Now.AddDays(-21), events[1].At);
    }

    [Fact]
    public void A_fresh_download_in_the_library_is_added()
    {
        var e = Assert.Single(RecentActivity.Derive(
            [Facts(7, acquired: Now.AddMinutes(-5), inLibrary: true, enrichment: EnrichmentStatus.Matched)], 10));

        Assert.Equal(RecentActivityKind.Added, e.Kind);
        Assert.Equal(Now.AddMinutes(-5), e.At);
    }

    [Fact]
    public void A_row_predating_AcquiredAt_falls_back_to_the_older_of_built_and_indexed()
    {
        var e = Assert.Single(RecentActivity.Derive(
            [Facts(1, indexed: Now.AddDays(-9), built: Now, inLibrary: true)], 10));

        Assert.Equal(Now.AddDays(-9), e.At);
    }

    [Fact]
    public void A_matched_song_never_built_is_matched()
    {
        var e = Assert.Single(RecentActivity.Derive(
            [Facts(3, enrichment: EnrichmentStatus.Matched, enriched: Now.AddMinutes(-2))], 10));

        Assert.Equal(RecentActivityKind.Matched, e.Kind);
        Assert.Equal("matched-3", e.Id);
    }

    [Fact]
    public void A_rematched_library_song_makes_no_event()
    {
        var rematched = Facts(4, enrichment: EnrichmentStatus.Matched, enriched: Now, wasEverBuilt: true);

        Assert.Empty(RecentActivity.Derive([rematched], 10));
    }

    [Fact]
    public void Pending_and_unmatched_songs_make_no_event()
    {
        Assert.Empty(RecentActivity.Derive([Facts(1), Facts(2, enrichment: EnrichmentStatus.Matched)], 10));
    }

    [Fact]
    public void Twelve_tracks_of_one_album_within_minutes_are_one_album_row()
    {
        var tracks = Enumerable.Range(1, 12)
            .Select(i => Facts(i, album: "Donda", acquired: Now.AddMinutes(-20 + i), inLibrary: true));

        var e = Assert.Single(RecentActivity.Derive(tracks, 10));

        var album = Assert.IsType<AlbumSubject>(e.Subject);
        Assert.Equal(12, album.Count);
        Assert.Equal("Donda", album.Album);
        Assert.Equal("added-album-1", e.Id);
        Assert.Equal(Now.AddMinutes(-8), e.At);
    }

    [Fact]
    public void Album_names_group_regardless_of_case_and_padding()
    {
        var e = Assert.Single(RecentActivity.Derive(
            [Facts(1, album: "Donda", acquired: Now.AddMinutes(-3), inLibrary: true),
             Facts(2, album: " DONDA ", acquired: Now.AddMinutes(-2), inLibrary: true)], 10));

        Assert.Equal(2, Assert.IsType<AlbumSubject>(e.Subject).Count);
    }

    [Fact]
    public void Two_bursts_of_the_same_album_two_hours_apart_stay_two_rows()
    {
        var first = Enumerable.Range(1, 3).Select(i => Facts(i, album: "A", acquired: Now.AddHours(-3).AddMinutes(i), inLibrary: true));
        var second = Enumerable.Range(10, 3).Select(i => Facts(i, album: "A", acquired: Now.AddMinutes(-3 + i - 10), inLibrary: true));

        var events = RecentActivity.Derive(first.Concat(second), 10);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(3, Assert.IsType<AlbumSubject>(e.Subject).Count));
    }

    [Fact]
    public void Songs_without_an_album_never_group()
    {
        var events = RecentActivity.Derive(
            [Facts(1, album: " ", acquired: Now.AddMinutes(-3), inLibrary: true),
             Facts(2, album: null, acquired: Now.AddMinutes(-2), inLibrary: true)], 10);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.IsType<SongSubject>(e.Subject));
    }

    [Fact]
    public void A_song_without_a_title_shows_its_file_name()
    {
        var e = Assert.Single(RecentActivity.Derive(
            [Facts(1, title: " ", artist: null, fileName: "f67417a4.flac", acquired: Now, inLibrary: true)], 10));

        Assert.Equal("f67417a4.flac", Assert.IsType<SongSubject>(e.Subject).Title);
    }

    [Fact]
    public void Needs_review_and_failed_songs_yield_their_kinds()
    {
        var events = RecentActivity.Derive(
            [Facts(1, enrichment: EnrichmentStatus.NeedsReview, enriched: Now.AddMinutes(-1)),
             Facts(2, enrichment: EnrichmentStatus.Failed, enriched: Now.AddMinutes(-2)),
             Facts(3, build: LibraryBuildStatus.Failed, enriched: Now.AddMinutes(-3))], 10);

        Assert.Equal(
            [RecentActivityKind.Review, RecentActivityKind.Failed, RecentActivityKind.Failed],
            events.Select(e => e.Kind));
    }

    [Fact]
    public void Take_limits_after_grouping()
    {
        var album = Enumerable.Range(1, 5).Select(i => Facts(i, album: "A", acquired: Now.AddMinutes(-i), inLibrary: true));
        var single = Facts(99, acquired: Now.AddHours(-2), inLibrary: true);

        var events = RecentActivity.Derive(album.Append(single), 2);

        Assert.Equal(2, events.Count);
        Assert.IsType<AlbumSubject>(events[0].Subject);
    }

    [Fact]
    public void An_album_subject_needs_two_songs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlbumSubject("A", null, 1));
    }

    [Fact]
    public void The_wire_shape_has_a_lowercase_kind_and_a_typed_subject()
    {
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var song = new RecentActivityEvent("matched-7", RecentActivityKind.Matched, new SongSubject(7, "T", "A"), Now.AddMinutes(-3));
        var album = new RecentActivityEvent("added-album-1", RecentActivityKind.Added, new AlbumSubject("Al", null, 12), Now);

        var songJson = System.Text.Json.JsonSerializer.Serialize(RecentActivity.ToWire(song, Now), web);
        var albumJson = System.Text.Json.JsonSerializer.Serialize(RecentActivity.ToWire(album, Now), web);

        Assert.Contains("\"kind\":\"matched\"", songJson);
        Assert.Contains("\"time\":\"3 min ago\"", songJson);
        Assert.Contains("\"subject\":{\"type\":\"song\",\"songId\":7,\"title\":\"T\",\"artist\":\"A\"}", songJson);
        Assert.Contains("\"subject\":{\"type\":\"album\",\"album\":\"Al\",\"artist\":null,\"count\":12}", albumJson);
    }

    [Fact]
    public async Task The_query_projects_the_facts_and_derives_the_feed()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new MusicHoarderDbContext(options);

        SongMetadata Song(string path) => new()
        {
            OwnerUserId = TestUsers.OwnerId, SourcePath = path, FileName = Path.GetFileName(path),
            Extension = ".flac", FileSizeBytes = 1, LastModifiedUtc = Now, IndexedAtUtc = Now.AddDays(-30),
        };

        var retagged = Song("/a/old.flac");
        retagged.DestinationPath = "/lib/old.flac";
        retagged.AcquiredAtUtc = Now.AddDays(-20);
        retagged.LibraryBuiltAtUtc = Now;
        retagged.EnrichmentStatus = EnrichmentStatus.Matched;
        retagged.EnrichedAtUtc = Now;

        var rematched = Song("/a/rematched.flac");
        rematched.PreviousDestinationPath = "/lib/rematched.flac";
        rematched.EnrichmentStatus = EnrichmentStatus.Matched;
        rematched.EnrichedAtUtc = Now;

        var incoming = Song("/a/incoming.flac");
        incoming.Title = "Incoming";
        incoming.EnrichmentStatus = EnrichmentStatus.Matched;
        incoming.EnrichedAtUtc = Now.AddMinutes(-1);

        var review = Song("/a/review.flac");
        review.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        review.EnrichedAtUtc = Now.AddMinutes(-2);

        db.Songs.AddRange(retagged, rematched, incoming, review);
        await db.SaveChangesAsync();

        var events = await PipelineSnapshot.ComputeRecentActivityAsync(db.Songs.IgnoreQueryFilters(), 10);

        Assert.Equal(
            [RecentActivityKind.Matched, RecentActivityKind.Review, RecentActivityKind.Added],
            events.Select(e => e.Kind));
        Assert.Equal("Incoming", Assert.IsType<SongSubject>(events[0].Subject).Title);
        Assert.Equal(Now.AddDays(-20), events[2].At);
    }
}
