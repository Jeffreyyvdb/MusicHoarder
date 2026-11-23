using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Artists;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Listening;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Artists;

public class ArtistSuggestionsTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<string> NoFollows = new(StringComparer.Ordinal);

    [Fact]
    public void Suggests_artists_played_across_several_songs_most_recent_listening_first()
    {
        var plays = new[]
        {
            Play(1, "Old Favourite", 80), Play(2, "Old Favourite", 80), Play(3, "Old Favourite", 80),
            Play(4, "Old Favourite", 80), Play(5, "Old Favourite", 80),
            Play(10, "New Find", 1), Play(11, "New Find", 2), Play(12, "New Find", 3),
        };

        var ranked = ArtistSuggestions.Rank(plays, NoFollows, Now);

        Assert.Equal(["New Find", "Old Favourite"], ranked.Select(r => r.Artist));
        Assert.Equal(3, ranked[0].Plays);
        Assert.Equal(3, ranked[0].Songs);
        Assert.Equal(Now.AddDays(-1), ranked[0].LastPlayedAtUtc);
    }

    [Fact]
    public void One_song_on_repeat_and_plays_before_the_window_do_not_make_a_suggestion()
    {
        var plays = new[]
        {
            Play(1, "Repeat", 1), Play(1, "Repeat", 2), Play(1, "Repeat", 3), Play(1, "Repeat", 4),
            Play(2, "Gone", 100), Play(3, "Gone", 100), Play(4, "Gone", 100), Play(5, "Gone", 5),
            Play(6, "Twice", 1), Play(7, "Twice", 2),
        };

        Assert.Empty(ArtistSuggestions.Rank(plays, NoFollows, Now));
    }

    [Fact]
    public void A_play_counts_for_its_lead_artist_whole_names_kept_and_placeholders_skipped()
    {
        var plays = new[]
        {
            Play(1, "Tyler, The Creator", 1), Play(2, "Tyler, The Creator", 2), Play(3, "Tyler, The Creator", 3),
            Play(4, "Simon & Garfunkel feat. Someone", 1), Play(5, "Simon & Garfunkel", 2), Play(6, "Simon & Garfunkel", 3),
            Play(7, "Guest", 1, albumArtist: "Kanye West"), Play(8, "Ye; Guest", 2, albumArtist: "Kanye West"),
            Play(9, "Kanye West", 3),
            Play(10, "Unknown Artist", 1), Play(11, "Unknown Artist", 2), Play(12, "Unknown Artist", 3),
            Play(13, "Compiled One", 1, albumArtist: "Various Artists"), Play(14, "Compiled One", 2, albumArtist: "Various Artists"),
            Play(15, "Compiled One", 3, albumArtist: "Various Artists"),
        };

        var ranked = ArtistSuggestions.Rank(plays, NoFollows, Now);

        Assert.Equal(
            ["Compiled One", "Kanye West", "Simon & Garfunkel", "Tyler, The Creator"],
            ranked.Select(r => r.Artist).Order(StringComparer.Ordinal));
        Assert.All(ranked, r => Assert.Equal(3, r.Plays));
    }

    [Fact]
    public void Followed_artists_are_left_out_by_name_key_and_the_list_is_capped()
    {
        var plays = Enumerable.Range(0, ArtistSuggestions.Limit + 3)
            .SelectMany(a => new[] { Play(a * 10, $"Artist {a}", 1), Play(a * 10 + 1, $"Artist {a}", 2), Play(a * 10 + 2, $"Artist {a}", 3) })
            .Concat([Play(900, "Beyoncé", 1), Play(901, "Beyonce", 1), Play(902, "BEYONCÉ", 1)])
            .ToList();
        var followed = new HashSet<string>(StringComparer.Ordinal) { ArtistSuggestions.KeyOf("Beyonce") };

        var ranked = ArtistSuggestions.Rank(plays, followed, Now);

        Assert.Equal(ArtistSuggestions.Limit, ranked.Count);
        Assert.DoesNotContain(ranked, r => r.Key == "beyonce");
    }

    [Fact]
    public async Task Endpoint_reads_the_callers_own_plays_and_follows_and_names_only_a_trusted_catalog_artist()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var at = DateTime.UtcNow.AddDays(-1);
        await using (var seed = new MusicHoarderDbContext(options))
        {
            for (var i = 0; i < 3; i++)
            {
                seed.ListeningEvents.Add(Event(TestUsers.OwnerId, i, "Linked One", at));
                seed.ListeningEvents.Add(Event(TestUsers.OwnerId, 10 + i, "Unproven", at));
                seed.ListeningEvents.Add(Event(TestUsers.OwnerId, 20 + i, "Followed", at));
                seed.ListeningEvents.Add(Event(TestUsers.FriendId, 30 + i, "Someone Else's", at));
                seed.ListeningEvents.Add(Event(TestUsers.OwnerId, 40 + i, "ASAP Rocky", at));
                seed.ListeningEvents.Add(Event(TestUsers.OwnerId, 50 + i, "Followed By A Friend", at));
            }
            seed.ArtistCatalogLinks.AddRange(
                new ArtistCatalogLink { ArtistKey = "linked one", Status = ArtistCatalogLinkStatus.Linked, DeezerArtistId = "42" },
                new ArtistCatalogLink { ArtistKey = "unproven", Status = ArtistCatalogLinkStatus.Unconfirmed, DeezerArtistId = "7" },
                new ArtistCatalogLink { ArtistKey = "asap rocky", Status = ArtistCatalogLinkStatus.Chosen, DeezerArtistId = "1519461" });
            seed.FollowedArtists.AddRange(
                new FollowedArtist { OwnerUserId = TestUsers.OwnerId, DeezerArtistId = "9", Name = "Followed", ArtistKey = "followed" },
                new FollowedArtist { OwnerUserId = TestUsers.OwnerId, DeezerArtistId = "1519461", Name = "A$AP Rocky", ArtistKey = "aap rocky" },
                new FollowedArtist
                {
                    OwnerUserId = TestUsers.FriendId, DeezerArtistId = "11", Name = "Followed By A Friend", ArtistKey = "followed by a friend",
                });
            await seed.SaveChangesAsync();
        }

        var admin = new CurrentUser(TestUsers.OwnerId, "a@test.local", UserRole.Admin, "Admin");
        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(admin));
        var result = await ArtistDiscographyEndpoints.ListSuggestions(db, CancellationToken.None);

        var suggestions = Assert.IsType<Ok<ArtistSuggestionsResponse>>(result).Value!.Suggestions;
        Assert.Equal(["Followed By A Friend", "Linked One", "Unproven"], suggestions.Select(s => s.Artist).Order());
        Assert.Equal("42", suggestions.Single(s => s.Artist == "Linked One").DeezerArtistId);
        Assert.Null(suggestions.Single(s => s.Artist == "Unproven").DeezerArtistId);
    }

    private static ListeningEventRow Play(int songId, string artist, int daysAgo, string? albumArtist = null) =>
        new(songId, Now.AddDays(-daysAgo), $"Song {songId}", artist, albumArtist, null, null, 200);

    private static ListeningEvent Event(Guid user, int songId, string artist, DateTime at) => new()
    {
        UserId = user, SongId = songId, Title = $"Song {songId}", PlayedAtUtc = at, Artist = artist, DurationSeconds = 100,
    };
}
