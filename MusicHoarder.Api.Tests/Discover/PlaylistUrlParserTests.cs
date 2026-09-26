using MusicHoarder.Api.Discover;

namespace MusicHoarder.Api.Tests.Discover;

public class PlaylistUrlParserTests
{
    [Theory]
    // Spotify web URLs (with and without query strings) + URI + bare base62 id.
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DX0XUsuxWHRQd", "spotify", "37i9dQZF1DX0XUsuxWHRQd")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DX0XUsuxWHRQd?si=abc123", "spotify", "37i9dQZF1DX0XUsuxWHRQd")]
    [InlineData("https://open.spotify.com/intl-de/playlist/37i9dQZF1DX0XUsuxWHRQd", "spotify", "37i9dQZF1DX0XUsuxWHRQd")]
    [InlineData("spotify:playlist:37i9dQZF1DX0XUsuxWHRQd", "spotify", "37i9dQZF1DX0XUsuxWHRQd")]
    [InlineData("37i9dQZF1DX0XUsuxWHRQd", "spotify", "37i9dQZF1DX0XUsuxWHRQd")]
    // Deezer web URLs (lang segment, query string) + bare numeric id.
    [InlineData("https://www.deezer.com/playlist/908622995", "deezer", "908622995")]
    [InlineData("https://www.deezer.com/en/playlist/908622995", "deezer", "908622995")]
    [InlineData("https://www.deezer.com/us/playlist/908622995?utm_source=deezer", "deezer", "908622995")]
    [InlineData("deezer.com/playlist/908622995", "deezer", "908622995")]
    [InlineData("908622995", "deezer", "908622995")]
    // YouTube: the list= parameter of any youtube.com / youtu.be link, wherever it sits in the query.
    [InlineData("https://www.youtube.com/playlist?list=PLexampleCoolMusic", "youtube", "PLexampleCoolMusic")]
    [InlineData("https://youtube.com/playlist?list=PLexampleMusicVideosPlaylist00001&si=xyz", "youtube", "PLexampleMusicVideosPlaylist00001")]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_kDx4v_ab-CD", "youtube", "OLAK5uy_kDx4v_ab-CD")]
    [InlineData("https://m.youtube.com/playlist?list=PLexampleCoolMusic", "youtube", "PLexampleCoolMusic")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLexampleCoolMusic&index=3", "youtube", "PLexampleCoolMusic")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?list=PLexampleCoolMusic", "youtube", "PLexampleCoolMusic")]
    [InlineData("youtube.com/playlist?list=PLexampleCoolMusic", "youtube", "PLexampleCoolMusic")]
    public void TryParse_RecognizesProviderAndId(string input, string expectedProvider, string expectedId)
    {
        var ok = PlaylistUrlParser.TryParse(input, out var provider, out var id);

        Assert.True(ok);
        Assert.Equal(expectedProvider, provider);
        Assert.Equal(expectedId, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/not-a-playlist")]
    [InlineData("just some words")]
    // A single YouTube video is a track, not a playlist; and list= on another host is not YouTube's.
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://example.com/playlist?list=PLexampleCoolMusic")]
    public void TryParse_RejectsUnrecognizedInput(string? input)
    {
        var ok = PlaylistUrlParser.TryParse(input, out var provider, out var id);

        Assert.False(ok);
        Assert.Equal("", provider);
        Assert.Equal("", id);
    }
}
