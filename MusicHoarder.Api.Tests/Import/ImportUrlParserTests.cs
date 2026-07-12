using MusicHoarder.Api.Import;

namespace MusicHoarder.Api.Tests.Import;

public class ImportUrlParserTests
{
    [Theory]
    // Spotify track: web URL (+query, +intl segment) and URI form.
    [InlineData("https://open.spotify.com/track/6rqhFgbbKwnb9MLmUQDhG6", ImportUrlKind.SpotifyTrack, "6rqhFgbbKwnb9MLmUQDhG6")]
    [InlineData("https://open.spotify.com/track/6rqhFgbbKwnb9MLmUQDhG6?si=abc123", ImportUrlKind.SpotifyTrack, "6rqhFgbbKwnb9MLmUQDhG6")]
    [InlineData("https://open.spotify.com/intl-de/track/6rqhFgbbKwnb9MLmUQDhG6", ImportUrlKind.SpotifyTrack, "6rqhFgbbKwnb9MLmUQDhG6")]
    [InlineData("spotify:track:6rqhFgbbKwnb9MLmUQDhG6", ImportUrlKind.SpotifyTrack, "6rqhFgbbKwnb9MLmUQDhG6")]
    // YouTube: watch, short host, music/mobile hosts, shorts, and v-not-first query param.
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", ImportUrlKind.YouTube, "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", ImportUrlKind.YouTube, "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=42", ImportUrlKind.YouTube, "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", ImportUrlKind.YouTube, "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", ImportUrlKind.YouTube, "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?app=desktop&v=dQw4w9WgXcQ&list=PL123", ImportUrlKind.YouTube, "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", ImportUrlKind.YouTube, "dQw4w9WgXcQ")]
    public void TryParse_RecognizesKindAndId(string input, ImportUrlKind expectedKind, string expectedId)
    {
        var ok = ImportUrlParser.TryParse(input, out var kind, out var id);

        Assert.True(ok);
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedId, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // An album/playlist link is not a single track.
    [InlineData("https://open.spotify.com/album/1DFixLWuPkv3KT3TnV35m3")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DX0XUsuxWHRQd")]
    [InlineData("https://example.com/not-a-track")]
    [InlineData("just some words")]
    public void TryParse_RejectsNonTrackInput(string? input)
    {
        var ok = ImportUrlParser.TryParse(input, out _, out var id);

        Assert.False(ok);
        Assert.Equal("", id);
    }

    [Fact]
    public void CanonicalUrls_AreCleanWatchAndTrackForms()
    {
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", ImportUrlParser.YouTubeWatchUrl("dQw4w9WgXcQ"));
        Assert.Equal("https://open.spotify.com/track/abc", ImportUrlParser.SpotifyTrackUrl("abc"));
    }
}
