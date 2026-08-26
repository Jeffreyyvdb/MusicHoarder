using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The rules behind "keep playing after the queue runs dry". These live once, on the server, so
/// both clients get the same station — the pinning that <c>AlbumProjectionTests</c> exists for,
/// applied to the same lesson.
/// </summary>
public class RadioRankerTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Continues_playing_when_the_seed_album_holds_one_track()
    {
        // The reported bug: a single-track album ends and nothing follows.
        var seed = Row(1, artist: "Kanye West", album: "Only Track");
        var ids = Rank(seed, [seed, Row(2, artist: "Kanye West", album: "Graduation")]);

        Assert.Equal([2], ids);
    }

    [Fact]
    public void Prefers_the_seed_artist_over_an_unrelated_one()
    {
        var seed = Row(1, artist: "Kanye West");
        var ids = Rank(seed, [seed, Row(2, artist: "Adele"), Row(3, artist: "Kanye West")]);

        Assert.Equal(3, ids[0]);
    }

    [Fact]
    public void Matches_on_the_lead_artist_the_library_displays()
    {
        // AlbumArtist wins over the track credit, and folds to its first name — the web's artistOf.
        var seed = Row(1, artist: "Jay-Z feat. Rihanna", albumArtist: "Jay-Z");
        var ids = Rank(seed, [seed, Row(2, artist: "Nas"), Row(3, artist: "Jay-Z & Kanye West")]);

        Assert.Equal(3, ids[0]);
    }

    [Fact]
    public void Links_two_tracks_through_a_shared_feature_when_no_lead_matches()
    {
        var seed = Row(1, artist: "Drake", artists: "Drake;Rihanna");
        var ids = Rank(seed, [seed, Row(2, artist: "Coldplay"), Row(3, artist: "Eminem", artists: "Eminem;Rihanna")]);

        Assert.Equal(3, ids[0]);
    }

    [Fact]
    public void Treats_a_shared_artist_mbid_as_the_same_artist_across_spellings()
    {
        var mbid = "164f0d73-1234-4e2c-8743-d77bf2191051";
        var seed = Row(1, artist: "Ms. Lauryn Hill", artistMbids: mbid);
        var ids = Rank(seed, [seed, Row(2, artist: "Someone Else"), Row(3, artist: "Lauryn Hill", artistMbids: mbid)]);

        Assert.Equal(3, ids[0]);
    }

    [Fact]
    public void Falls_back_to_genre_and_era_when_no_artist_relates()
    {
        var seed = Row(1, artist: "A", genre: "Hip-Hop/Rap", year: 2004);
        var ids = Rank(seed, [
            seed,
            Row(2, artist: "B", genre: "Classical", year: 1890),
            Row(3, artist: "C", genre: "Rap", year: 2005),
        ]);

        Assert.Equal(3, ids[0]);
    }

    [Fact]
    public void Never_returns_the_seed_or_anything_already_queued()
    {
        var seed = Row(1, artist: "A");
        var ids = RadioRanker.Rank(
            seed,
            [seed, Row(2, artist: "A"), Row(3, artist: "A")],
            new HashSet<int> { 2 },
            limit: 10,
            Now);

        Assert.Equal([3], ids);
    }

    [Fact]
    public void Pushes_down_a_track_played_minutes_ago()
    {
        var seed = Row(1, artist: "A");
        var ids = Rank(seed, [
            seed,
            Row(2, artist: "A", lastPlayedAtUtc: Now.AddMinutes(-20)),
            Row(3, artist: "A"),
        ]);

        Assert.Equal(3, ids[0]);
    }

    [Fact]
    public void Pushes_down_a_skit_shorter_than_a_minute()
    {
        var seed = Row(1, artist: "A");
        var ids = Rank(seed, [seed, Row(2, artist: "A", durationSeconds: 18), Row(3, artist: "A", durationSeconds: 200)]);

        Assert.Equal(3, ids[0]);
    }

    [Fact]
    public void Prefers_a_liked_track_between_two_equal_neighbours()
    {
        // Same artist, same everything: taste is what separates them.
        var seed = Row(1, artist: "A");
        var ids = Rank(seed, [seed, Row(2, artist: "A"), Row(3, artist: "A", likedAtUtc: Now.AddDays(-30))]);

        Assert.Equal(3, ids[0]);
    }

    [Fact]
    public void Widens_past_one_artist_instead_of_playing_a_whole_discography()
    {
        var seed = Row(1, artist: "A");
        var candidates = new List<RadioTrackRow> { seed };
        for (var id = 10; id < 20; id++) candidates.Add(Row(id, artist: "A"));
        // Weaker matches — a different artist, same genre — that a pure score sort would never reach.
        for (var id = 20; id < 25; id++) candidates.Add(Row(id, artist: "B", genre: "Rap"));

        var ids = Rank(seed, candidates, limit: 6);

        Assert.Equal(6, ids.Count);
        Assert.Equal(3, ids.Count(id => id is >= 10 and < 20));
    }

    [Fact]
    public void Never_stacks_more_than_two_tracks_by_one_artist_back_to_back()
    {
        var seed = Row(1, artist: "A");
        var candidates = new List<RadioTrackRow> { seed };
        for (var id = 10; id < 16; id++) candidates.Add(Row(id, artist: "A"));
        for (var id = 20; id < 26; id++) candidates.Add(Row(id, artist: "B", genre: "Rap"));

        var picked = Rank(seed, candidates, limit: 8)
            .Select(id => id < 20 ? "A" : "B")
            .ToList();

        var run = 1;
        for (var i = 1; i < picked.Count; i++)
        {
            run = picked[i] == picked[i - 1] ? run + 1 : 1;
            Assert.True(run <= 2, $"three in a row by {picked[i]}: {string.Join(",", picked)}");
        }
    }

    [Fact]
    public void Repeats_an_artist_rather_than_returning_a_short_list()
    {
        // A library of one artist still has to keep playing — silence is the bug being fixed.
        var seed = Row(1, artist: "A");
        var candidates = new List<RadioTrackRow> { seed };
        for (var id = 10; id < 20; id++) candidates.Add(Row(id, artist: "A"));

        Assert.Equal(8, Rank(seed, candidates, limit: 8).Count);
    }

    [Fact]
    public void Gives_the_same_station_for_the_same_seed_every_time()
    {
        // The tie-break spread is a stable hash, not a random number: a restart must not reshuffle
        // the station, and the phone and the browser must agree about it.
        var seed = Row(1, artist: "A");
        var candidates = new List<RadioTrackRow> { seed };
        for (var id = 10; id < 40; id++) candidates.Add(Row(id, artist: "A", genre: "Rap"));

        Assert.Equal(Rank(seed, candidates, limit: 10), Rank(seed, candidates, limit: 10));
    }

    [Fact]
    public void Gives_two_different_seeds_two_different_stations()
    {
        var candidates = new List<RadioTrackRow>();
        for (var id = 10; id < 60; id++) candidates.Add(Row(id, artist: "A", genre: "Rap"));

        var first = Rank(Row(1, artist: "A", genre: "Rap"), candidates, limit: 10);
        var second = Rank(Row(2, artist: "A", genre: "Rap"), candidates, limit: 10);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Returns_nothing_when_the_library_holds_only_the_seed()
    {
        var seed = Row(1, artist: "A");
        Assert.Empty(Rank(seed, [seed]));
    }

    private static IReadOnlyList<int> Rank(
        RadioTrackRow seed, IEnumerable<RadioTrackRow> candidates, int limit = 10) =>
        RadioRanker.Rank(seed, candidates, new HashSet<int>(), limit, Now);

    private static RadioTrackRow Row(
        int id,
        string? artist = null,
        string? albumArtist = null,
        string? artists = null,
        string? artistMbids = null,
        string? album = null,
        string? genre = null,
        string? label = null,
        int? year = null,
        int? durationSeconds = 200,
        int playCount = 0,
        DateTime? likedAtUtc = null,
        DateTime? lastPlayedAtUtc = null,
        bool isBuilt = true) =>
        new(id, artist, albumArtist, artists, artistMbids, album, genre, label, year,
            durationSeconds, playCount, likedAtUtc, lastPlayedAtUtc, isBuilt);
}
