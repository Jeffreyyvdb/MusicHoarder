using System.Text;
using MusicHoarder.Api.Discover;

namespace MusicHoarder.Api.Tests.Discover;

public class ArchiveFormatTests
{
    private const string Old = "https://example.test/old";
    private const string New = "https://example.test/new";

    private static MemoryStream Json(string json) => new(Encoding.UTF8.GetBytes(json));

    private static string Entry(string name, long followers, string? description = null, string snapshot = "s") =>
        $$"""{ "description": {{(description is null ? "null" : $"\"{description}\"")}}, "num_followers": {{followers}}, "original_name": "{{name}}", "owner": { "name": "Spotify", "url": "u" }, "snapshot_id": "{{snapshot}}", "unique_name": "{{name}}", "url": "x" }""";

    private static IReadOnlyList<ArchivedPlaylist> Parse(string baseUrl, params (string Id, string Entry)[] entries) =>
        ArchiveFormat.ParseMetadata(Json("{" + string.Join(",", entries.Select(e => $"\"{e.Id}\": {e.Entry}")) + "}"), baseUrl);

    private const string IdA = "37i9dQZF1DXcBWIGoYBM5M";
    private const string IdB = "37i9dQZF1DX0XUsuxWHRQd";
    private const string IdC = "37i9dQZF1DX4JAvHpjipBk";

    [Fact]
    public void ParseMetadata_ReadsTheArchiveShape()
    {
        var playlists = Parse(Old, (IdA, Entry("Today’s Top Hits", 33_574_029, "The hottest 50.", "snap")));

        var p = Assert.Single(playlists);
        Assert.Equal(new ArchivedPlaylist(IdA, "Today’s Top Hits", "The hottest 50.", 33_574_029, "Spotify", "snap", Old), p);
    }

    [Fact]
    public void ParseMetadata_SkipsKeysThatAreNotPlaylistIds()
    {
        var playlists = Parse(Old, ("../x", Entry("Bad", 1)), (IdA, Entry("Good", 1)));

        Assert.Equal([IdA], playlists.Select(p => p.Id));
    }

    [Fact]
    public void Merge_ALaterSourceOverridesAnEarlierOne_AndAnIdOnlyTheOlderListsStays()
    {
        var old = Parse(Old, (IdA, Entry("Old name", 10)), (IdB, Entry("Only in old", 5)));
        var current = Parse(New, (IdA, Entry("New name", 20)), (IdC, Entry("Only in new", 1)));

        var catalog = ArchiveFormat.Merge([old, current]);

        Assert.Equal(3, catalog.Count);
        Assert.Equal(("New name", New), (catalog[IdA].Name, catalog[IdA].SourceBaseUrl));
        Assert.Equal(("Only in old", Old), (catalog[IdB].Name, catalog[IdB].SourceBaseUrl));
        Assert.Equal(New, catalog[IdC].SourceBaseUrl);
    }

    [Fact]
    public void Search_RanksByFollowersThenName_WithOrWithoutAQuery()
    {
        var catalog = ArchiveFormat.Merge([Parse(Old,
            (IdA, Entry("Rap Small", 5)), (IdB, Entry("Rap Big", 500)), (IdC, Entry("Aaa Rap", 5)))]);

        Assert.Equal(["Rap Big", "Aaa Rap", "Rap Small"], ArchiveFormat.Search(catalog.Values, null, 10).Select(p => p.Name));
        Assert.Equal(["Rap Big", "Aaa Rap", "Rap Small"], ArchiveFormat.Search(catalog.Values, "rap", 10).Select(p => p.Name));
        Assert.Equal(["Rap Big"], ArchiveFormat.Search(catalog.Values, "", 1).Select(p => p.Name));
    }

    [Fact]
    public void Search_EveryTermMustMatchTheNameOrTheDescription_FoldingCaseDiacriticsAndApostrophes()
    {
        var catalog = ArchiveFormat.Merge([Parse(Old,
            (IdA, Entry("Today’s Top Hits", 3, "The hottest 50.")),
            (IdB, Entry("Café del Mar", 2, "Chill out")),
            (IdC, Entry("RapCaviar", 1, "New music from Offset")))]);

        Assert.Equal(["Today’s Top Hits"], ArchiveFormat.Search(catalog.Values, "todays TOP hottest", 10).Select(p => p.Name));
        Assert.Equal(["Café del Mar"], ArchiveFormat.Search(catalog.Values, "cafe mar", 10).Select(p => p.Name));
        Assert.Equal(["RapCaviar"], ArchiveFormat.Search(catalog.Values, "offset rapcaviar", 10).Select(p => p.Name));
        Assert.Empty(ArchiveFormat.Search(catalog.Values, "cafe offset", 10));
    }

    [Fact]
    public void ParseTracks_TakesTheIdFromTheUrl_SkipsTracksWithoutOne_AndReadsAddedAtAsUtc()
    {
        var tracks = ArchiveFormat.ParseTracks(Json("""
            { "tracks": [
              { "added_at": "2026-11-06 05:02:41", "album": { "name": "Album", "url": "a" },
                "artists": [ { "name": "KAROL G", "url": "a" }, { "name": "Judeline", "url": "b" } ],
                "duration_ms": 225834, "name": "BbY WOW", "url": "https://open.spotify.com/track/3h5T5JypYU7huFiVYhv1dr" },
              { "added_at": "2026-11-06 05:02:41", "artists": [], "duration_ms": 1, "name": "Local file", "url": null },
              { "added_at": "2026-11-06 05:02:41", "artists": [], "duration_ms": 1, "name": "No url key" },
              { "added_at": "2026-11-06 05:02:41", "artists": [], "duration_ms": 1, "name": "Episode", "url": "https://open.spotify.com/episode/3h5T5JypYU7huFiVYhv1dr" },
              { "added_at": "garbage", "artists": [ { "name": "Solo" } ], "name": "Undated", "url": "https://open.spotify.com/track/2sFepQNyl1WjpROruP8bJ0" }
            ] }
            """));

        Assert.Equal(2, tracks.Count);
        var first = tracks[0];
        Assert.Equal(("3h5T5JypYU7huFiVYhv1dr", "BbY WOW", "Album", 225834), (first.SpotifyTrackId, first.Title, first.Album, first.DurationMs));
        Assert.Equal(["KAROL G", "Judeline"], first.Artists);
        Assert.Equal(new DateTime(2026, 11, 6, 5, 2, 41, DateTimeKind.Utc), first.AddedAtUtc);
        Assert.Equal(DateTimeKind.Utc, first.AddedAtUtc!.Value.Kind);
        Assert.Null(tracks[1].AddedAtUtc);
        Assert.Equal(0, tracks[1].DurationMs);
    }

    [Theory]
    [InlineData("37i9dQZF1DXcBWIGoYBM5M", true)]
    [InlineData("../x", false)]
    [InlineData("37i9dQZF1DXcBWIGoYBM5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("37i9dQZF1DXcBWIGoYBM5M\n", false)]
    public void IsSpotifyId_AcceptsOnlyTwentyTwoAlphanumerics(string? id, bool expected) =>
        Assert.Equal(expected, ArchiveFormat.IsSpotifyId(id));
}
