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
    public void Followed_artists_featured_credits_and_various_artists_are_handled_by_name_key()
    {
        var plays = new[]
        {
            Play(1, "Kanye West feat. Ty Dolla $ign", 1), Play(2, "Kanye West", 2), Play(3, "Kanye West", 3),
            Play(4, "Ty Dolla $ign", 4), Play(5, "TY DOLLA $IGN", 5),
            Play(6, "Various Artists", 1), Play(7, "Various Artists", 2), Play(8, "Various Artists", 3),
        };
        var followed = new HashSet<string>(StringComparer.Ordinal) { ArtistSuggestions.KeyOf("Kanye West") };

        var ranked = ArtistSuggestions.Rank(plays, followed, Now);

        var only = Assert.Single(ranked);
        Assert.Equal("Ty Dolla $ign", only.Artist);
        Assert.Equal(3, only.Plays);
    }

    [Fact]
    public async Task Endpoint_reads_the_callers_own_plays_and_names_only_a_trusted_catalog_artist()
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
            }
            seed.ArtistCatalogLinks.AddRange(
                new ArtistCatalogLink { ArtistKey = "linked one", Status = ArtistCatalogLinkStatus.Linked, DeezerArtistId = "42" },
                new ArtistCatalogLink { ArtistKey = "unproven", Status = ArtistCatalogLinkStatus.Unconfirmed, DeezerArtistId = "7" });
            seed.FollowedArtists.Add(new FollowedArtist
            {
                OwnerUserId = TestUsers.OwnerId, DeezerArtistId = "9", Name = "Followed", ArtistKey = "followed",
            });
            await seed.SaveChangesAsync();
        }

        var admin = new CurrentUser(TestUsers.OwnerId, "a@test.local", UserRole.Admin, "Admin");
        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(admin));
        var result = await ArtistDiscographyEndpoints.ListSuggestions(db, CancellationToken.None);

        var suggestions = Assert.IsType<Ok<ArtistSuggestionsResponse>>(result).Value!.Suggestions;
        Assert.Equal(["Linked One", "Unproven"], suggestions.Select(s => s.Artist).Order());
        Assert.Equal("42", suggestions.Single(s => s.Artist == "Linked One").DeezerArtistId);
        Assert.Null(suggestions.Single(s => s.Artist == "Unproven").DeezerArtistId);
    }

    private static ListeningEventRow Play(int songId, string artist, int daysAgo) =>
        new(songId, Now.AddDays(-daysAgo), $"Song {songId}", artist, null, null, null, 200);

    private static ListeningEvent Event(Guid user, int songId, string artist, DateTime at) => new()
    {
        UserId = user, SongId = songId, Title = $"Song {songId}", PlayedAtUtc = at, Artist = artist, DurationSeconds = 100,
    };
}
